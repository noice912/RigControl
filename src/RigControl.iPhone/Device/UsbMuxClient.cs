using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Device;

/// <summary>A device as usbmuxd lists it. <see cref="DeviceId"/> is only valid while it stays plugged in.</summary>
public sealed record UsbMuxDevice(int DeviceId, string Udid, bool Usb);

public enum UsbMuxEventKind { Attached, Detached }

/// <summary>A plug or unplug. <see cref="Device"/> is set for Attached; Detached only carries the id.</summary>
public sealed record UsbMuxEvent(UsbMuxEventKind Kind, int DeviceId, UsbMuxDevice? Device);

/// <summary>Where usbmuxd listens: TCP 127.0.0.1:27015 on Windows (Apple Devices / iTunes), a Unix socket elsewhere.</summary>
public sealed class UsbMuxEndpoint
{
    private readonly EndPoint _endPoint;

    private UsbMuxEndpoint(EndPoint endPoint) => _endPoint = endPoint;

    public static UsbMuxEndpoint Default => OperatingSystem.IsWindows()
        ? Tcp(new IPEndPoint(IPAddress.Loopback, 27015))
        : Unix("/var/run/usbmuxd");

    public static UsbMuxEndpoint Tcp(IPEndPoint endPoint) => new(endPoint);

    public static UsbMuxEndpoint Unix(string path) => new(new UnixDomainSocketEndPoint(path));

    public override string ToString() => _endPoint.ToString() ?? "usbmuxd";

    internal async Task<Stream> OpenAsync(TimeSpan timeout, CancellationToken ct)
    {
        var protocol = _endPoint is UnixDomainSocketEndPoint ? ProtocolType.Unspecified : ProtocolType.Tcp;
        var socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, protocol);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (protocol == ProtocolType.Tcp)
                socket.NoDelay = true;
            await socket.ConnectAsync(_endPoint, cts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new DeviceException(DeviceErrors.ServiceMissing, e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new DeviceException(DeviceErrors.ServiceMissing);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A client for usbmuxd's plist protocol: a 16-byte little-endian header (total length, version 1, message type 8 =
/// plist, tag) followed by an XML plist. Each call opens its own connection; Connect hands that connection over as
/// a raw pipe to a port on the iPhone.
/// </summary>
public sealed class UsbMuxClient(UsbMuxEndpoint? endpoint = null, TimeSpan? timeout = null)
{
    private const int HeaderSize = 16;
    private const int PlistMessage = 8;
    private const string ClientName = "RigControl";

    public UsbMuxEndpoint Endpoint { get; } = endpoint ?? UsbMuxEndpoint.Default;
    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<UsbMuxDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        var reply = await c.RequestAsync(Request("ListDevices"), Timeout, ct).ConfigureAwait(false);
        ThrowIfResultError("ListDevices", reply);
        var list = new List<UsbMuxDevice>();
        foreach (var item in reply.Array("DeviceList") ?? [])
            if (item is PlistDict entry && ParseDevice(entry) is { } device)
                list.Add(device);
        return list;
    }

    /// <summary>Plug and unplug events until cancelled. The devices already present come first as Attached events.</summary>
    public async IAsyncEnumerable<UsbMuxEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        var reply = await c.RequestAsync(Request("Listen"), Timeout, ct).ConfigureAwait(false);
        ThrowIfResultError("Listen", reply);
        if (reply.String("MessageType") != "Result")
            throw new DeviceException(DeviceErrors.BadReply);
        while (!ct.IsCancellationRequested)
        {
            var message = await c.ReceiveAsync(System.Threading.Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            switch (message.String("MessageType"))
            {
                case "Attached" when ParseDevice(message) is { } device:
                    yield return new UsbMuxEvent(UsbMuxEventKind.Attached, device.DeviceId, device);
                    break;
                case "Detached" when message.Long("DeviceID") is { } id:
                    yield return new UsbMuxEvent(UsbMuxEventKind.Detached, (int)id, null);
                    break;
            }
        }
    }

    /// <summary>The pair record Apple's service keeps for this iPhone. Missing = the iPhone hasn't trusted this PC.</summary>
    public async Task<PairRecord> ReadPairRecordAsync(string udid, CancellationToken ct = default)
    {
        var request = Request("ReadPairRecord");
        request["PairRecordID"] = udid;
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        var reply = await c.RequestAsync(request, Timeout, ct).ConfigureAwait(false);
        var data = reply.Data("PairRecordData");
        if (data is null)
            throw new DeviceException(DeviceErrors.NotTrusted);
        return PairRecord.FromPlist(DeviceIo.ParseReply(data));
    }

    /// <summary>This PC's system id as Apple's service knows it.</summary>
    public async Task<string> ReadBuidAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        var reply = await c.RequestAsync(Request("ReadBUID"), Timeout, ct).ConfigureAwait(false);
        ThrowIfResultError("ReadBUID", reply);
        return reply.String("BUID") ?? throw new DeviceException(DeviceErrors.BadReply);
    }

    /// <summary>Opens a pipe to a TCP port on the iPhone. The caller owns (and disposes) the returned stream.</summary>
    public async Task<Stream> ConnectAsync(int deviceId, int port, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var request = Request("Connect");
        request["DeviceID"] = (long)deviceId;
        request["PortNumber"] = (long)NetworkOrder(port);
        var c = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var reply = await c.RequestAsync(request, Timeout, ct).ConfigureAwait(false);
            if (reply.String("MessageType") != "Result")
                throw new DeviceException(DeviceErrors.BadReply);
            ThrowIfResultError($"port {port}", reply);
            return c.Stream;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>usbmuxd wants the port as it would sit in memory in network byte order, read back as a little-endian number.</summary>
    internal static int NetworkOrder(int port) => ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);

    private static PlistDict Request(string type) => new()
    {
        ["MessageType"] = type,
        ["ClientVersionString"] = ClientName,
        ["ProgName"] = ClientName,
        ["kLibUSBMuxVersion"] = 3L,
    };

    private static void ThrowIfResultError(string request, PlistDict reply)
    {
        if (reply.String("MessageType") == "Result" && reply.Long("Number") is { } n && n != 0)
            throw DeviceErrors.FromUsbMux(request, n);
    }

    internal static UsbMuxDevice? ParseDevice(PlistDict entry)
    {
        var props = entry.Dict("Properties");
        var id = entry.Long("DeviceID") ?? props?.Long("DeviceID");
        var udid = props?.String("SerialNumber") ?? props?.String("UDID");
        if (id is null || string.IsNullOrEmpty(udid))
            return null;
        return new UsbMuxDevice((int)id, udid, props?.String("ConnectionType") is null or "USB");
    }

    private async Task<Connection> OpenAsync(CancellationToken ct) =>
        new(await Endpoint.OpenAsync(Timeout, ct).ConfigureAwait(false));

    private sealed class Connection(Stream stream) : IAsyncDisposable
    {
        private uint _tag;

        public Stream Stream => stream;

        public async Task<PlistDict> RequestAsync(PlistDict message, TimeSpan timeout, CancellationToken ct)
        {
            var tag = ++_tag;
            var body = PlistFile.ToXml(message);
            var buffer = new byte[HeaderSize + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)buffer.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), PlistMessage);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), tag);
            body.CopyTo(buffer, HeaderSize);
            await DeviceIo.WriteAsync(stream, buffer, timeout, ct).ConfigureAwait(false);
            return await ReceiveAsync(timeout, ct).ConfigureAwait(false);
        }

        public async Task<PlistDict> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
        {
            var header = new byte[HeaderSize];
            await DeviceIo.ReadExactAsync(stream, header, timeout, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (length <= HeaderSize || length - HeaderSize > DeviceIo.MaxPlistBytes || type != PlistMessage)
                throw new DeviceException(DeviceErrors.BadReply);
            var body = new byte[length - HeaderSize];
            await DeviceIo.ReadExactAsync(stream, body, timeout, ct).ConfigureAwait(false);
            return DeviceIo.ParseReply(body);
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
