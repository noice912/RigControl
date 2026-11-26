using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using RigControl.iPhone.Device;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Device;

// In-process stand-ins for Apple's usbmuxd and the services on an iPhone. They speak the wire formats on a loopback
// TCP socket, so the real clients run unchanged. Nothing here reaches a real device.

/// <summary>A usbmuxd on 127.0.0.1:(random port).</summary>
internal sealed class FakeUsbMux : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;

    public FakeUsbMux()
    {
        _listener.Start();
        _accept = AcceptLoopAsync();
    }

    public List<FakeDevice> Devices { get; } = [];
    public Channel<PlistDict> Events { get; } = Channel.CreateUnbounded<PlistDict>();
    public ConcurrentQueue<(uint Version, uint Type, uint Tag, PlistDict Message)> Received { get; } = new();

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public UsbMuxClient Client(TimeSpan? timeout = null) => new(UsbMuxEndpoint.Tcp(EndPoint), timeout ?? TimeSpan.FromSeconds(5));

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        try
        {
            var (version, type, tag, msg) = await ReadAsync(s);
            Received.Enqueue((version, type, tag, msg));
            switch (msg.String("MessageType"))
            {
                case "ListDevices":
                    await WriteAsync(s, tag, new PlistDict { ["DeviceList"] = Devices.Select(d => (object)d.Entry()).ToList() });
                    break;
                case "ReadPairRecord":
                {
                    var device = Devices.FirstOrDefault(d => d.Udid == msg.String("PairRecordID"));
                    if (device?.PairRecord is { } record)
                        await WriteAsync(s, tag, new PlistDict { ["PairRecordData"] = PlistFile.ToXml(record) });
                    else
                        await WriteAsync(s, tag, Result(2));
                    break;
                }
                case "ReadBUID":
                    await WriteAsync(s, tag, new PlistDict { ["BUID"] = "FAKE-BUID" });
                    break;
                case "Listen":
                    await WriteAsync(s, tag, Result(0));
                    await foreach (var e in Events.Reader.ReadAllAsync(_stop.Token))
                        await WriteAsync(s, 0, e);
                    break;
                case "Connect":
                {
                    var device = Devices.FirstOrDefault(d => d.DeviceId == msg.Long("DeviceID"));
                    var raw = (int)(msg.Long("PortNumber") ?? 0);
                    var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    var handler = device?.Handler(port);
                    if (device is null)
                    {
                        await WriteAsync(s, tag, Result(2));
                        break;
                    }
                    if (handler is null)
                    {
                        await WriteAsync(s, tag, Result(3));
                        break;
                    }
                    await WriteAsync(s, tag, Result(0));
                    await handler(s);
                    break;
                }
                default:
                    await WriteAsync(s, tag, Result(1));
                    break;
            }
        }
        catch (Exception e) when (e is IOException or DeviceException or OperationCanceledException or ObjectDisposedException
                                      or System.Security.Authentication.AuthenticationException)
        {
        }
    }

    public static PlistDict Result(long number) => new() { ["MessageType"] = "Result", ["Number"] = number };

    private static async Task<(uint, uint, uint, PlistDict)> ReadAsync(Stream s)
    {
        var header = new byte[16];
        await s.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var body = new byte[length - 16];
        await s.ReadExactlyAsync(body);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), PlistFile.ParseDict(body));
    }

    public static async Task WriteAsync(Stream s, uint tag, PlistDict message)
    {
        var body = PlistFile.ToXml(message);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + body.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), tag);
        await s.WriteAsync(header);
        await s.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Events.Writer.TryComplete();
        await _accept;
    }
}

/// <summary>One iPhone: lockdown plus AFC and installation_proxy on fixed ports.</summary>
internal sealed class FakeDevice
{
    public const int AfcPort = 50001, ProxyPort = 50002;
    public const string HostId = "HOST-0001";

    public int DeviceId { get; init; } = 7;
    public string Udid { get; init; } = "00008110-000A1B2C3D4E5F60";
    public string ConnectionType { get; init; } = "USB";

    /// <summary>What usbmuxd returns for ReadPairRecord; null = never trusted.</summary>
    public PlistDict? PairRecord { get; set; } = MakePairRecord(HostId);

    public PlistDict Values { get; } = new()
    {
        ["DeviceName"] = "Test iPhone",
        ["ProductType"] = "iPhone14,2",
        ["ProductVersion"] = "18.1",
        ["UniqueDeviceID"] = "00008110-000A1B2C3D4E5F60",
    };

    public Dictionary<string, PlistDict> Domains { get; } = new();

    /// <summary>Lockdown's answer to StartSession instead of a session, for example "PasswordProtected".</summary>
    public string? SessionError { get; set; }

    /// <summary>When set, StartSession answers EnableSessionSSL and the connection is wrapped by this.</summary>
    public Func<Stream, Task<Stream>>? ServerTls { get; set; }

    public FakeAfc Afc { get; } = new();
    public FakeInstallProxy Proxy { get; }
    public ConcurrentQueue<string> LockdownRequests { get; } = new();

    public FakeDevice() => Proxy = new FakeInstallProxy(Afc);

    public PlistDict Entry() => new()
    {
        ["DeviceID"] = (long)DeviceId,
        ["MessageType"] = "Attached",
        ["Properties"] = new PlistDict
        {
            ["ConnectionType"] = ConnectionType,
            ["DeviceID"] = (long)DeviceId,
            ["SerialNumber"] = Udid,
        },
    };

    public static PlistDict MakePairRecord(string hostId, byte[]? hostCert = null, byte[]? hostKey = null, byte[]? deviceCert = null) => new()
    {
        ["HostID"] = hostId,
        ["SystemBUID"] = "FAKE-BUID",
        ["HostCertificate"] = hostCert ?? "not used without TLS"u8.ToArray(),
        ["HostPrivateKey"] = hostKey ?? "not used without TLS"u8.ToArray(),
        ["DeviceCertificate"] = deviceCert ?? "not used without TLS"u8.ToArray(),
    };

    public Func<Stream, Task>? Handler(int port) => port switch
    {
        LockdownClient.Port => ServeLockdownAsync,
        AfcPort => Afc.ServeAsync,
        ProxyPort => Proxy.ServeAsync,
        _ => null,
    };

    private async Task ServeLockdownAsync(Stream stream)
    {
        var session = false;
        while (true)
        {
            PlistDict request;
            try
            {
                request = await PlistMessages.ReadAsync(stream, TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (DeviceException)
            {
                return;
            }
            var name = request.String("Request") ?? "";
            LockdownRequests.Enqueue(name);
            var reply = new PlistDict { ["Request"] = name };
            var startTls = false;
            switch (name)
            {
                case "QueryType":
                    reply["Type"] = "com.apple.mobile.lockdown";
                    break;
                case "GetValue":
                {
                    var domain = request.String("Domain");
                    var key = request.String("Key");
                    PlistDict? source = domain is null ? Values : Domains.GetValueOrDefault(domain);
                    if (!session && domain is not null)
                        source = null;
                    if (source is null)
                        reply["Error"] = "MissingValue";
                    else if (key is null)
                        reply["Value"] = session ? source : Without(source, "DeviceName");
                    else if (source.TryGetValue(key, out var v))
                        reply["Value"] = v;
                    else
                        reply["Error"] = "MissingValue";
                    break;
                }
                case "StartSession":
                    if (SessionError is not null)
                        reply["Error"] = SessionError;
                    else if (request.String("HostID") != PairRecord?.String("HostID") || PairRecord is null)
                        reply["Error"] = "InvalidHostID";
                    else
                    {
                        reply["SessionID"] = "SESSION-1";
                        reply["EnableSessionSSL"] = ServerTls is not null;
                        startTls = ServerTls is not null;
                        session = true;
                    }
                    break;
                case "StartService":
                    if (!session)
                        reply["Error"] = "SessionInactive";
                    else if (request.String("Service") == AfcClient.ServiceName)
                        reply["Port"] = (long)AfcPort;
                    else if (request.String("Service") == InstallationProxyClient.ServiceName)
                        reply["Port"] = (long)ProxyPort;
                    else
                        reply["Error"] = "InvalidService";
                    if (!reply.ContainsKey("Error"))
                        reply["EnableServiceSSL"] = false;
                    break;
                case "StopSession":
                    session = false;
                    break;
                default:
                    reply["Error"] = "InvalidRequest";
                    break;
            }
            await PlistMessages.WriteAsync(stream, reply, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (startTls)
                stream = await ServerTls!(stream);
        }
    }

    // Without a session, the fake leaves out the name so tests can tell the two paths apart.
    private static PlistDict Without(PlistDict d, string key)
    {
        var copy = new PlistDict();
        foreach (var (k, v) in d)
            if (k != key)
                copy[k] = v;
        return copy;
    }
}

/// <summary>An in-memory AFC file system.</summary>
internal sealed class FakeAfc
{
    private ulong _nextHandle = 1;
    private readonly ConcurrentDictionary<ulong, (string Path, MemoryStream Data)> _open = new();

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentBag<string> Directories { get; } = [];
    public ConcurrentQueue<int> WriteSizes { get; } = new();
    public ConcurrentQueue<string> Removed { get; } = new();

    /// <summary>Answer writes with "no space left" once this many bytes have arrived.</summary>
    public long? NoSpaceAfter { get; set; }

    public async Task ServeAsync(Stream s)
    {
        while (true)
        {
            var header = new byte[40];
            try
            {
                await s.ReadExactlyAsync(header);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            var entire = (int)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            var thisLen = (int)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
            var number = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
            var op = (AfcClient.Op)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
            var args = new byte[thisLen - 40];
            await s.ReadExactlyAsync(args);
            var payload = new byte[entire - thisLen];
            await s.ReadExactlyAsync(payload);
            byte[] reply = op switch
            {
                AfcClient.Op.MakeDir => Ok(number, () => Directories.Add(Path(args, 0))),
                AfcClient.Op.FileOpen => Open(number, args),
                AfcClient.Op.FileWrite => Write(number, args, payload),
                AfcClient.Op.FileClose => Close(number, args),
                AfcClient.Op.RemovePath => Files.TryRemove(Path(args, 0), out _)
                    ? Ok(number, () => Removed.Enqueue(Path(args, 0)))
                    : Status(number, AfcClient.ObjectNotFound),
                AfcClient.Op.GetFileInfo => Files.TryGetValue(Path(args, 0), out var f)
                    ? AfcClient.BuildPacket(number, AfcClient.Op.Data, [], Encoding.UTF8.GetBytes($"st_size\0{f.Length}\0st_ifmt\0S_IFREG\0"))
                    : Status(number, AfcClient.ObjectNotFound),
                _ => Status(number, 15),
            };
            await s.WriteAsync(reply);
        }
    }

    private byte[] Open(ulong number, byte[] args)
    {
        var handle = _nextHandle++;
        _open[handle] = (Path(args, 8), new MemoryStream());
        var h = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(h, handle);
        return AfcClient.BuildPacket(number, AfcClient.Op.FileOpenResult, h, []);
    }

    private byte[] Write(ulong number, byte[] args, byte[] payload)
    {
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(args);
        if (!_open.TryGetValue(handle, out var f))
            return Status(number, 7);
        if (NoSpaceAfter is { } limit && f.Data.Length + payload.Length > limit)
            return Status(number, AfcClient.NoSpaceLeft);
        f.Data.Write(payload);
        WriteSizes.Enqueue(payload.Length);
        return Status(number, 0);
    }

    private byte[] Close(ulong number, byte[] args)
    {
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(args);
        if (!_open.TryRemove(handle, out var f))
            return Status(number, 7);
        Files[f.Path] = f.Data.ToArray();
        return Status(number, 0);
    }

    private static byte[] Ok(ulong number, Action action)
    {
        action();
        return Status(number, 0);
    }

    private static byte[] Status(ulong number, ulong code)
    {
        var c = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(c, code);
        return AfcClient.BuildPacket(number, AfcClient.Op.Status, c, []);
    }

    private static string Path(byte[] args, int start)
    {
        var end = Array.IndexOf(args, (byte)0, start);
        return Encoding.UTF8.GetString(args, start, (end < 0 ? args.Length : end) - start);
    }
}

/// <summary>installation_proxy: checks the package was staged, then sends status messages and Complete (or an error).</summary>
internal sealed class FakeInstallProxy(FakeAfc afc)
{
    public List<PlistDict> Statuses { get; } =
    [
        new() { ["Status"] = "CreatingStagingDirectory", ["PercentComplete"] = 5L },
        new() { ["Status"] = "ExtractingPackage", ["PercentComplete"] = 15L },
        new() { ["Status"] = "VerifyingApplication", ["PercentComplete"] = 60L },
        new() { ["Status"] = "GeneratingApplicationMap", ["PercentComplete"] = 90L },
    ];

    /// <summary>Sent after the first status instead of the rest, for example an ApplicationVerificationFailed dict.</summary>
    public PlistDict? Error { get; set; }

    public PlistDict? LastRequest { get; private set; }
    public byte[]? InstalledPackage { get; private set; }
    public Dictionary<string, PlistDict> Installed { get; } = new();

    public async Task ServeAsync(Stream s)
    {
        var request = await PlistMessages.ReadAsync(s, TimeSpan.FromSeconds(10), CancellationToken.None);
        LastRequest = request;
        var timeout = TimeSpan.FromSeconds(10);
        switch (request.String("Command"))
        {
            case "Install":
            {
                if (!afc.Files.TryGetValue(request.String("PackagePath") ?? "", out var package))
                {
                    await PlistMessages.WriteAsync(s, new PlistDict { ["Error"] = "PackageInspectionFailed" }, timeout, default);
                    return;
                }
                InstalledPackage = package;
                if (Error is not null)
                {
                    await PlistMessages.WriteAsync(s, Statuses[0], timeout, default);
                    await PlistMessages.WriteAsync(s, Error, timeout, default);
                    return;
                }
                foreach (var status in Statuses)
                    await PlistMessages.WriteAsync(s, status, timeout, default);
                await PlistMessages.WriteAsync(s, new PlistDict { ["Status"] = "Complete" }, timeout, default);
                break;
            }
            case "Lookup":
            {
                var ids = request.Dict("ClientOptions")?.Array("BundleIDs")?.OfType<string>() ?? [];
                var result = new PlistDict();
                foreach (var id in ids)
                    if (Installed.TryGetValue(id, out var app))
                        result[id] = app;
                await PlistMessages.WriteAsync(s, new PlistDict { ["LookupResult"] = result, ["Status"] = "Complete" }, timeout, default);
                break;
            }
        }
    }
}

/// <summary>Self-signed certificates standing in for the pair record's host and device certificates.</summary>
internal sealed class TestCert : IDisposable
{
    private TestCert(byte[] certPem, byte[] keyPem, X509Certificate2 withKey)
    {
        CertPem = certPem;
        KeyPem = keyPem;
        WithKey = withKey;
    }

    public byte[] CertPem { get; }
    public byte[] KeyPem { get; }
    public X509Certificate2 WithKey { get; }

    public static TestCert Create(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Apple's pair records hold "RSA PRIVATE KEY" PEM, so the tests use the same form.
        var keyPem = Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem());
        var withKey = new X509Certificate2(cert.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.UserKeySet);
        return new TestCert(Encoding.ASCII.GetBytes(cert.ExportCertificatePem()), keyPem, withKey);
    }

    /// <summary>The iPhone's side of the handshake: shows <paramref name="device"/>, requires the host certificate.</summary>
    public static Func<Stream, Task<Stream>> Server(TestCert device, TestCert host, Action<bool>? sawHost = null) => async s =>
    {
        var ssl = new SslStream(s, leaveInnerStreamOpen: false, (_, cert, _, _) =>
        {
            var ok = cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(host.WithKey.RawData);
            sawHost?.Invoke(ok);
            return ok;
        });
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = device.WithKey,
            ClientCertificateRequired = true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
        return ssl;
    };

    public void Dispose() => WithKey.Dispose();
}
