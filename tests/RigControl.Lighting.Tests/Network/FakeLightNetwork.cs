using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using RigControl.Lighting.Network;

namespace RigControl.Lighting.Tests.Network;

/// <summary>
/// A network that exists only in memory, for tests of light providers. Nothing here opens a socket.
/// It applies the same two rules as the real one: packets only go to local addresses, and a pairing secret is only
/// filled in for its paired address. A test scripts the devices:
///   net.OnHttp = ask => ask.Url.EndsWith("/json/info") ? FakeLightNetwork.Json("{...}") : null;   (null = no answer)
///   net.OnUdp = sent => new[] { new UdpDatagram("172.16.0.50", 4003, reply) };                    (answers to the sender)
///   net.Deliver(4002, datagram);                                                                   (a packet to whoever listens on a port)
///   net.OnTcp = (host, port) => new FakeTcpLink(host) { OnWrite = text => "reply\r\n" };           (null = refused)
///   net.Mdns["_wled._tcp.local."] = new() { new MdnsService(...) };
/// and then looks at net.Asks, net.Sent, link.WrittenText, net.Listeners.
/// </summary>
internal sealed class FakeLightNetwork : ILightNetwork
{
    public sealed record UdpSent(int LocalPort, string Host, int Port, byte[] Data)
    {
        public string Text => Encoding.UTF8.GetString(Data);
    }

    public List<LocalNetwork> Networks { get; } = new() { new LocalNetwork("172.16.0.10", 24, true) };

    /// <summary>Every request as it would have gone on the wire (secrets filled in).</summary>
    public ConcurrentQueue<HttpAsk> Asks { get; } = new();
    public Func<HttpAsk, HttpReply?>? OnHttp { get; set; }

    public ConcurrentQueue<UdpSent> Sent { get; } = new();
    public Func<UdpSent, IEnumerable<UdpDatagram>?>? OnUdp { get; set; }

    public ConcurrentQueue<(string Host, int Port)> TcpTried { get; } = new();
    public Func<string, int, FakeTcpLink?>? OnTcp { get; set; }
    public ConcurrentQueue<FakeTcpListener> Listeners { get; } = new();

    public Dictionary<string, List<MdnsService>> Mdns { get; } = new();
    public ConcurrentQueue<string> Browsed { get; } = new();

    private readonly ConcurrentDictionary<FakeUdpPort, byte> _ports = new();
    private int _nextPort = 50000;

    public static HttpReply Json(string body, int status = 200) => new(status, body, new Dictionary<string, string>());
    public static HttpReply Status(int status) => new(status, "", new Dictionary<string, string>());

    public IReadOnlyList<LocalNetwork> LocalNetworks() => Networks.ToList();

    public IUdpPort OpenUdp(UdpOptions? options = null)
    {
        var o = options ?? new UdpOptions();
        var port = new FakeUdpPort(this, o.LocalPort != 0 ? o.LocalPort : Interlocked.Increment(ref _nextPort));
        _ports[port] = 0;
        return port;
    }

    /// <summary>Hands a packet to every open port with that local port number (as if a device sent it there).</summary>
    public void Deliver(int localPort, UdpDatagram datagram)
    {
        foreach (var p in _ports.Keys.Where(p => p.LocalPort == localPort)) p.Inbox.Writer.TryWrite(datagram);
    }

    public Task<ITcpLink> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LanAddress.RequireLocal(host);
        TcpTried.Enqueue((host, port));
        var link = OnTcp?.Invoke(host, port) ?? throw new IOException("Nothing answered at " + host + ".");
        return Task.FromResult<ITcpLink>(link);
    }

    public ITcpListener ListenTcp(string localAddress, int port = 0)
    {
        LanAddress.RequireLocal(localAddress);
        var l = new FakeTcpListener(localAddress, port != 0 ? port : Interlocked.Increment(ref _nextPort));
        Listeners.Enqueue(l);
        return l;
    }

    public Task<HttpReply> HttpAsync(HttpAsk ask, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LanAddress.RequireLocal(SecretRule.HostOf(ask.Url));
        var filled = SecretRule.Fill(ask);
        Asks.Enqueue(filled);
        var reply = OnHttp?.Invoke(filled) ?? throw new HttpRequestException("Nothing answered at " + SecretRule.HostOf(ask.Url) + ".");
        return Task.FromResult(reply);
    }

    public Task<IReadOnlyList<MdnsService>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Browsed.Enqueue(serviceType);
        return Task.FromResult<IReadOnlyList<MdnsService>>(Mdns.TryGetValue(serviceType, out var list) ? list.ToList() : new List<MdnsService>());
    }

    internal sealed class FakeUdpPort : IUdpPort
    {
        private readonly FakeLightNetwork _net;
        public Channel<UdpDatagram> Inbox { get; } = Channel.CreateUnbounded<UdpDatagram>();
        public bool Disposed { get; private set; }

        public FakeUdpPort(FakeLightNetwork net, int port)
        {
            _net = net;
            LocalPort = port;
        }

        public int LocalPort { get; }

        public void Send(byte[] data, string host, int port)
        {
            LanAddress.RequireLocal(host);
            var sent = new UdpSent(LocalPort, host, port, data.ToArray());
            _net.Sent.Enqueue(sent);
            foreach (var reply in _net.OnUdp?.Invoke(sent) ?? Array.Empty<UdpDatagram>()) Inbox.Writer.TryWrite(reply);
        }

        public async Task<UdpDatagram?> ReceiveAsync(TimeSpan wait, CancellationToken ct)
        {
            if (Inbox.Reader.TryRead(out var ready)) return ready;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try { return await Inbox.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }

        public void Dispose()
        {
            Disposed = true;
            _net._ports.TryRemove(this, out _);
        }
    }
}

/// <summary>One end of a made-up TCP connection. The test plays the device.</summary>
internal sealed class FakeTcpLink : ITcpLink
{
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
    private byte[] _rest = Array.Empty<byte>();

    public FakeTcpLink(string remoteHost, string localHost = "172.16.0.10")
    {
        RemoteHost = remoteHost;
        LocalHost = localHost;
    }

    public string RemoteHost { get; }
    public string LocalHost { get; }
    public ConcurrentQueue<byte[]> Written { get; } = new();
    public string WrittenText => string.Concat(Written.Select(b => Encoding.UTF8.GetString(b)));
    public bool Disposed { get; private set; }
    /// <summary>Called with each thing written (as text); what it returns is sent back.</summary>
    public Func<string, string?>? OnWrite { get; set; }

    /// <summary>The device says something.</summary>
    public void Say(string text) => _inbox.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    public void Say(byte[] bytes) => _inbox.Writer.TryWrite(bytes);
    /// <summary>The device hangs up.</summary>
    public void HangUp() => _inbox.Writer.TryComplete();

    public Task WriteAsync(byte[] data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Disposed) throw new IOException("The connection is closed.");
        Written.Enqueue(data.ToArray());
        if (OnWrite?.Invoke(Encoding.UTF8.GetString(data)) is { } reply) Say(reply);
        return Task.CompletedTask;
    }

    public async Task<int> ReadAsync(byte[] buffer, TimeSpan wait, CancellationToken ct)
    {
        if (_rest.Length == 0)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try
            {
                if (!await _inbox.Reader.WaitToReadAsync(cts.Token)) return 0;
                if (!_inbox.Reader.TryRead(out var next)) return -1;
                _rest = next;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return -1;
            }
        }
        var n = Math.Min(buffer.Length, _rest.Length);
        Array.Copy(_rest, buffer, n);
        _rest = _rest[n..];
        return n;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeTcpListener : ITcpListener
{
    private readonly Channel<ITcpLink> _waiting = Channel.CreateUnbounded<ITcpLink>();

    public FakeTcpListener(string localAddress, int port)
    {
        LocalAddress = localAddress;
        Port = port;
    }

    public string LocalAddress { get; }
    public int Port { get; }
    public bool Disposed { get; private set; }

    /// <summary>A device connects to this PC; the returned link is the device's side for the test to inspect.</summary>
    public FakeTcpLink Connect(string fromHost)
    {
        var link = new FakeTcpLink(fromHost, LocalAddress);
        _waiting.Writer.TryWrite(link);
        return link;
    }

    public async Task<ITcpLink?> AcceptAsync(TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try { return await _waiting.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Keeps "secrets" readable for tests (no Windows data protection), but marked, so a test can tell protected from plain.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public string Protect(string secret, string purpose) => $"protected[{purpose}]:{Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))}";

    public string? Unprotect(string stored, string purpose)
    {
        var head = $"protected[{purpose}]:";
        return stored.StartsWith(head, StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[head.Length..])) : null;
    }
}

/// <summary>Collects log lines, so tests can check that nothing secret is ever logged.</summary>
internal sealed class ListLog : RigControl.Core.Logging.ILog
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Info(string message) => Lines.Enqueue(message);
    public void Warn(string message, Exception? ex = null) => Lines.Enqueue(message + (ex is null ? "" : " " + ex));
    public void Error(string message, Exception? ex = null) => Lines.Enqueue(message + (ex is null ? "" : " " + ex));
    public RigControl.Core.Logging.ILog For(string source) => this;
}

internal static class Eventually
{
    /// <summary>Waits (up to five seconds) until something that happens on another thread has happened.</summary>
    public static async Task True(Func<bool> condition, string what = "the expected thing")
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting for " + what + ".");
            await Task.Delay(10);
        }
    }
}
