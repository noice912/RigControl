using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RigControl.Lighting.Network;

/// <summary>
/// The real network. Every packet and request is checked first: it must go to an address on the user's own network
/// (<see cref="LanAddress.MaySendTo"/>), and a pairing secret only goes to its paired address (<see cref="SecretRule"/>).
/// Requests never follow redirects and never go through a proxy, so a secret can't be led elsewhere.
/// </summary>
public sealed class RealLightNetwork : ILightNetwork, IDisposable
{
    /// <summary>
    /// The longest answer a device may give. Real answers are a few thousand characters (a Hue Bridge's list of
    /// lights is the longest); anything bigger is not a light, and is dropped instead of filling the PC's memory.
    /// </summary>
    public const int MostReplyBytes = 1024 * 1024;

    internal const string TooLong = "The device sent an answer that was too long, so it was ignored.";

    private readonly HttpClient _plain = NewClient(null);
    // Only for rules that name a remembered certificate (a paired device): a handful at most.
    private readonly ConcurrentDictionary<TlsRule, HttpClient> _tls = new();

    private static HttpClient NewClient(TlsRule? rule) =>
        new(NewHandler(rule)) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MostReplyBytes };

    public IReadOnlyList<LocalNetwork> LocalNetworks()
    {
        var list = new List<LocalNetwork>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                // A network with a router is a home network; without one it is usually a VPN adapter or a virtual switch.
                var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && LanAddress.IsPrivate(ua.Address.ToString()))
                        list.Add(new LocalNetwork(ua.Address.ToString(), ua.PrefixLength, gateway));
            }
        }
        catch
        {
            // no network information: nothing to search
        }
        return list;
    }

    public IUdpPort OpenUdp(UdpOptions? options = null) => new UdpPort(options ?? new UdpOptions());

    public async Task<ITcpLink> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        LanAddress.RequireLocal(host);
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(IPAddress.Parse(host), port, cts.Token).ConfigureAwait(false);
            return new TcpLink(tcp);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException("The device didn't answer in time.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public ITcpListener ListenTcp(string localAddress, int port = 0)
    {
        LanAddress.RequireLocal(localAddress);
        return new Listener(IPAddress.Parse(localAddress), port);
    }

    public async Task<HttpReply> HttpAsync(HttpAsk ask, CancellationToken ct)
    {
        // The host is checked before the secret is put in.
        LanAddress.RequireLocal(SecretRule.HostOf(ask.Url));
        var filled = SecretRule.Fill(ask);
        var uri = new Uri(filled.Url);
        if (uri.Scheme == "https" && filled.Tls?.CommonName is null && filled.Tls?.LeafSha256 is null)
            throw new InvalidOperationException("A secure request to a device must say which certificate to expect.");
        // A rule without a remembered certificate comes from a search or a first meeting, where the name is whatever
        // a device announced: such a client is used once and thrown away, so made-up names can't pile up.
        var once = uri.Scheme == "https" && filled.Tls!.LeafSha256 is null;
        var client = uri.Scheme != "https" ? _plain
            : once ? NewClient(filled.Tls)
            : _tls.GetOrAdd(filled.Tls!, NewClient);
        using var request = new HttpRequestMessage(new HttpMethod(filled.Method), uri);
        if (filled.Body is not null) request.Content = new StringContent(filled.Body, Encoding.UTF8, filled.ContentType);
        if (filled.Headers is not null)
            foreach (var (k, v) in filled.Headers) request.Headers.TryAddWithoutValidation(k, v);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(filled.Timeout);
        try
        {
            // Headers first, so an answer that announces itself as too long is dropped before any of it is kept.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MostReplyBytes) throw new IOException(TooLong);
            var body = await ReadCappedAsync(response.Content, cts.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers.Concat(response.Content.Headers)) headers[h.Key] = string.Join(", ", h.Value);
            var reply = new HttpReply((int)response.StatusCode, body, headers);
            return uri.Scheme == "https" && LastSeen.TryGetValue(filled.Tls!, out var seen)
                ? reply with { CertSha256 = seen.Sha, CertCommonName = seen.Cn }
                : reply;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The device didn't answer in time.");
        }
        finally
        {
            if (once)
            {
                client.Dispose();
                LastSeen.TryRemove(filled.Tls!, out _);
            }
        }
    }

    /// <summary>The answer's text, read only up to <see cref="MostReplyBytes"/>; a longer one is refused.</summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) break;
            if (kept.Length + n > MostReplyBytes) throw new IOException(TooLong);
            kept.Write(buffer, 0, n);
        }
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try { encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        var bytes = kept.GetBuffer().AsSpan(0, (int)kept.Length);
        var preamble = encoding.Preamble;
        if (preamble.Length > 0 && bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
        return encoding.GetString(bytes);
    }

    public Task<IReadOnlyList<MdnsService>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken ct) =>
        Mdns.BrowseAsync(this, serviceType, wait, ct);

    private static SocketsHttpHandler NewHandler(TlsRule? rule)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        if (rule is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var c2 = new X509Certificate2(cert);
                    var sha = Convert.ToHexString(SHA256.HashData(c2.RawData));
                    var cn = c2.GetNameInfo(X509NameType.SimpleName, false);
                    if (!CertMatches(rule, cn, sha)) return false;
                    LastSeen[rule] = (sha, cn);
                    return true;
                },
            };
        }
        return handler;
    }

    // What the device showed, per rule (the handshake has no link to one request; the newest is reported).
    private static readonly ConcurrentDictionary<TlsRule, (string Sha, string Cn)> LastSeen = new();

    /// <summary>The certificate check for devices: the name when one is expected, the fingerprint when one is remembered.</summary>
    internal static bool CertMatches(TlsRule rule, string? commonName, string sha256)
    {
        if (rule.CommonName is null && rule.LeafSha256 is null) return false;
        if (rule.CommonName is { } cn && !cn.Equals(commonName, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.LeafSha256 is { } pin && !pin.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public void Dispose()
    {
        _plain.Dispose();
        foreach (var c in _tls.Values) c.Dispose();
    }

    // ---------------------------------------------------------------- UDP

    private sealed class UdpPort : IUdpPort
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public UdpPort(UdpOptions o)
        {
            try
            {
                var local = o.LocalAddress is null ? IPAddress.Any : IPAddress.Parse(o.LocalAddress);
                if (o.Share)
                {
                    _socket.ExclusiveAddressUse = false;
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                _socket.EnableBroadcast = true;
                // Windows otherwise turns an "unreachable" answer to one packet into an error on the next read.
                try { _socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null); } catch { /* not Windows */ }
                // With a group to hear, the port is opened for every address (packets to the group don't arrive otherwise).
                _socket.Bind(new IPEndPoint(o.JoinGroup is null ? local : IPAddress.Any, o.LocalPort));
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                if (!local.Equals(IPAddress.Any))
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                if (o.JoinGroup is { } group)
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse(group), local));
            }
            catch
            {
                _socket.Dispose();
                throw;
            }
        }

        public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public void Send(byte[] data, string host, int port)
        {
            LanAddress.RequireLocal(host);
            _socket.SendTo(data, new IPEndPoint(IPAddress.Parse(host), port));
        }

        public async Task<UdpDatagram?> ReceiveAsync(TimeSpan wait, CancellationToken ct)
        {
            var buffer = new byte[65536];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try
            {
                var r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token).ConfigureAwait(false);
                var from = (IPEndPoint)r.RemoteEndPoint;
                return new UdpDatagram(from.Address.ToString(), from.Port, buffer.AsSpan(0, r.ReceivedBytes).ToArray());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose() => _socket.Dispose();
    }

    // ---------------------------------------------------------------- TCP

    private sealed class TcpLink : ITcpLink
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        public TcpLink(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            RemoteHost = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString();
            LocalHost = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address.ToString();
        }

        public string RemoteHost { get; }
        public string LocalHost { get; }

        public async Task WriteAsync(byte[] data, CancellationToken ct) => await _stream.WriteAsync(data, ct).ConfigureAwait(false);

        public async Task<int> ReadAsync(byte[] buffer, TimeSpan wait, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try
            {
                return await _stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return -1;
            }
        }

        public void Dispose() => _tcp.Dispose();
    }

    private sealed class Listener : ITcpListener
    {
        private readonly TcpListener _listener;

        public Listener(IPAddress local, int port)
        {
            _listener = new TcpListener(local, port);
            _listener.Start(4);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<ITcpLink?> AcceptAsync(TimeSpan wait, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                return new TcpLink(tcp);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
