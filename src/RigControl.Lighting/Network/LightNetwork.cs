using System.Net;
using System.Net.Sockets;

namespace RigControl.Lighting.Network;

// Everything a light provider may do on the network goes through ILightNetwork, so that
//  - tests swap in a fake and never touch the real network,
//  - packets only ever go to addresses on the user's own network (never the internet), and
//  - a stored pairing secret can only be sent to the address it was paired with (SecretRule).

/// <summary>One of this PC's local IPv4 networks.</summary>
public sealed record LocalNetwork(string Address, int PrefixLength, bool HasGateway)
{
    /// <summary>The network's broadcast address (the last address of the network).</summary>
    public string Broadcast
    {
        get
        {
            var (ip, mask) = Parts();
            return LanAddress.FromUInt(ip | ~mask);
        }
    }

    /// <summary>Every other address on this network (empty for networks bigger than about 1000 addresses).</summary>
    public IEnumerable<string> OtherHosts()
    {
        if (PrefixLength is < 22 or > 30) yield break;
        var (ip, mask) = Parts();
        var net = ip & mask;
        for (var h = net + 1; h < (net | ~mask); h++)
            if (h != ip) yield return LanAddress.FromUInt(h);
    }

    /// <summary>True when <paramref name="host"/> is an address on this network.</summary>
    public bool Contains(string? host)
    {
        if (!IPAddress.TryParse(host, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var (ip, mask) = Parts();
        return (LanAddress.ToUInt(a) & mask) == (ip & mask);
    }

    private (uint Ip, uint Mask) Parts()
    {
        var ip = LanAddress.ToUInt(IPAddress.Parse(Address));
        var mask = PrefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(32, PrefixLength));
        return (ip, mask);
    }
}

public sealed record UdpDatagram(string Host, int Port, byte[] Data);

/// <summary>How a UDP port is opened. All fields are optional.</summary>
public sealed record UdpOptions
{
    /// <summary>The local address to send from (also the network that multicast and broadcast packets leave on); null = any.</summary>
    public string? LocalAddress { get; init; }
    /// <summary>The local port; 0 = any free port.</summary>
    public int LocalPort { get; init; }
    /// <summary>Let other programs use the same port too (needed for well-known reply ports another program may hold).</summary>
    public bool Share { get; init; }
    /// <summary>A multicast group to join, to hear packets sent to it.</summary>
    public string? JoinGroup { get; init; }
}

public interface IUdpPort : IDisposable
{
    int LocalPort { get; }

    /// <summary>Sends one packet. Quick, never waits for an answer. Throws if the address is not on a local network.</summary>
    void Send(byte[] data, string host, int port);

    /// <summary>The next packet received, or null when none arrived within <paramref name="wait"/>.</summary>
    Task<UdpDatagram?> ReceiveAsync(TimeSpan wait, CancellationToken ct);
}

public interface ITcpLink : IDisposable
{
    /// <summary>The other end's address.</summary>
    string RemoteHost { get; }
    /// <summary>This PC's address on that connection (what the device would use to reach this PC).</summary>
    string LocalHost { get; }

    Task WriteAsync(byte[] data, CancellationToken ct);

    /// <summary>Reads into <paramref name="buffer"/>: the number of bytes, 0 when the other end closed, -1 when nothing arrived in time.</summary>
    Task<int> ReadAsync(byte[] buffer, TimeSpan wait, CancellationToken ct);
}

public interface ITcpListener : IDisposable
{
    int Port { get; }

    /// <summary>The next device that connects, or null when none did within <paramref name="wait"/>.</summary>
    Task<ITcpLink?> AcceptAsync(TimeSpan wait, CancellationToken ct);
}

/// <summary>
/// For https to a device that carries its own certificate (no public authority signed it): the certificate's name must
/// be <paramref name="CommonName"/> when given, and its SHA-256 must be <paramref name="LeafSha256"/> when given
/// (the fingerprint remembered at pairing). At least one must be given: "any certificate" is never accepted.
/// </summary>
public sealed record TlsRule(string? CommonName, string? LeafSha256);

public sealed record HttpAsk(string Method, string Url)
{
    /// <summary>Text put where <c>{secret}</c> stands in the address, a header or the body, after the address was checked.</summary>
    public const string SecretMark = "{secret}";

    public string? Body { get; init; }
    public string ContentType { get; init; } = "application/json";
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>The pairing secret this request needs. It is only filled in when the request goes to the paired address.</summary>
    public BoundSecret? Secret { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(4);
    public TlsRule? Tls { get; init; }

    public static HttpAsk Get(string url) => new("GET", url);
    public static HttpAsk Put(string url, string json) => new("PUT", url) { Body = json };
    public static HttpAsk Post(string url, string? json = null) => new("POST", url) { Body = json };
}

public sealed record HttpReply(int Status, string Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>For https with a <see cref="TlsRule"/>: the SHA-256 of the certificate the device showed (hex, upper case).</summary>
    public string? CertSha256 { get; init; }
    public string? CertCommonName { get; init; }

    public bool Ok => Status is >= 200 and < 300;

    /// <summary>Throws (in plain words) unless the device answered "fine".</summary>
    public HttpReply EnsureOk()
    {
        if (!Ok) throw new IOException($"The device answered with an error ({Status}).");
        return this;
    }

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>One service found by an mDNS / Bonjour browse (how WLED, Hue bridges and others announce themselves).</summary>
public sealed record MdnsService(string Instance, string Host, string Address, int Port, IReadOnlyDictionary<string, string> Txt);

/// <summary>The network as light providers see it. The real one is <see cref="RealLightNetwork"/>; tests use a fake.</summary>
public interface ILightNetwork
{
    IReadOnlyList<LocalNetwork> LocalNetworks();
    IUdpPort OpenUdp(UdpOptions? options = null);
    Task<ITcpLink> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
    /// <summary>Waits for devices that connect to this PC (some lights call back for fast updates).</summary>
    ITcpListener ListenTcp(string localAddress, int port = 0);
    Task<HttpReply> HttpAsync(HttpAsk ask, CancellationToken ct);
    /// <summary>Asks the local network who offers a service such as "_wled._tcp.local." and collects the answers for <paramref name="wait"/>.</summary>
    Task<IReadOnlyList<MdnsService>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken ct);
}

/// <summary>Which addresses count as "the user's own network". Light control is local only: nothing else is ever contacted.</summary>
public static class LanAddress
{
    /// <summary>A private IPv4 address a light can be paired at (10.x, 172.16-31.x, 192.168.x, or self-assigned 169.254.x).</summary>
    public static bool IsPrivate(string? host) =>
        IPAddress.TryParse(host, out var a) && a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is var b
        && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));

    /// <summary>Where packets may go: private addresses, this PC itself, and the local multicast and broadcast addresses.</summary>
    public static bool MaySendTo(string? host)
    {
        if (IsPrivate(host)) return true;
        if (!IPAddress.TryParse(host, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 127) return true;                                   // this PC
        if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true; // local broadcast
        return (b[0] == 224 && b[1] == 0 && b[2] == 0) || b[0] == 239;  // link-local and site-local multicast
    }

    /// <summary>
    /// The networks a search may use: the ones with a router. A network without one is usually a VPN adapter or a
    /// virtual switch, and is never searched.
    /// </summary>
    public static IReadOnlyList<LocalNetwork> Searchable(ILightNetwork net) => net.LocalNetworks().Where(n => n.HasGateway).ToList();

    /// <summary>
    /// True when a search may ask the device at <paramref name="host"/> what it is: a private address that lies on one
    /// of the searched networks. An announcement can name any address; one that is somewhere else (a VPN, a virtual
    /// switch, another site) is never contacted because of it.
    /// </summary>
    public static bool OnSearchedNetwork(ILightNetwork net, string? host) =>
        IsPrivate(host) && Searchable(net).Any(n => n.Contains(host));

    /// <summary>Throws unless <paramref name="host"/> is an address on the user's own network.</summary>
    public static void RequireLocal(string? host)
    {
        if (!MaySendTo(host))
            throw new InvalidOperationException("Lights are only controlled on your own network; that address is not on it.");
    }

    /// <summary>The same address written the same way (extra zeros and spaces don't make it a different address).</summary>
    public static string Normalise(string host) => IPAddress.TryParse(host.Trim(), out var a) ? a.ToString() : host.Trim().ToLowerInvariant();

    public static bool Same(string? a, string? b) => a is not null && b is not null && Normalise(a) == Normalise(b);

    internal static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    internal static string FromUInt(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
}

/// <summary>Thrown when code tries to send a pairing secret anywhere but the address it was paired with.</summary>
public sealed class SecretRuleException : InvalidOperationException
{
    public SecretRuleException(string message) : base(message) { }
}

/// <summary>
/// A pairing secret (token, key) tied to the address of the device that gave it. The text can only be taken out for
/// that address; printing or logging the object never shows it.
/// </summary>
public sealed class BoundSecret
{
    private readonly string _value;

    /// <summary>The address the secret was paired with.</summary>
    public string Address { get; }

    private BoundSecret(string value, string address)
    {
        _value = value;
        Address = LanAddress.Normalise(address);
    }

    /// <summary>A secret a device at <paramref name="address"/> just handed over, or one read back from the store for that address.</summary>
    public static BoundSecret For(string value, string address)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("An empty secret.", nameof(value));
        return new BoundSecret(value, address);
    }

    /// <summary>The secret, for sending to <paramref name="host"/>. Throws unless that is the paired address.</summary>
    public string RevealFor(string host)
    {
        if (!LanAddress.Same(host, Address))
            throw new SecretRuleException("A pairing key is only ever sent to the device it was paired with. This request was going somewhere else, so it was stopped.");
        return _value;
    }

    public override string ToString() => "(pairing key)";
}

/// <summary>The one place a secret gets into an HTTP request. Both the real and the fake network call it.</summary>
public static class SecretRule
{
    /// <summary>The host of a device address (http or https, with the device's address in it).</summary>
    public static string HostOf(string url)
    {
        // The mark is not a legal part of an address for Uri in every position; check the host on a copy without it.
        if (!Uri.TryCreate(url.Replace(HttpAsk.SecretMark, "x"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("That is not a device address.");
        return uri.Host;
    }

    /// <summary>
    /// Returns the request with <c>{secret}</c> filled in, after checking that it goes to the paired address.
    /// A request that names the mark but carries no secret, or goes elsewhere, is refused.
    /// </summary>
    public static HttpAsk Fill(HttpAsk ask)
    {
        var host = HostOf(ask.Url);
        var marked = ask.Url.Contains(HttpAsk.SecretMark, StringComparison.Ordinal)
                     || (ask.Body?.Contains(HttpAsk.SecretMark, StringComparison.Ordinal) ?? false)
                     || (ask.Headers?.Values.Any(v => v.Contains(HttpAsk.SecretMark, StringComparison.Ordinal)) ?? false);
        if (ask.Secret is null)
        {
            if (marked) throw new SecretRuleException("This request needs a pairing key, but none was given.");
            return ask;
        }
        var value = ask.Secret.RevealFor(host);
        if (!marked) return ask with { Secret = null };
        return ask with
        {
            Secret = null,
            Url = ask.Url.Replace(HttpAsk.SecretMark, Uri.EscapeDataString(value), StringComparison.Ordinal),
            Body = ask.Body?.Replace(HttpAsk.SecretMark, value, StringComparison.Ordinal),
            Headers = ask.Headers?.ToDictionary(h => h.Key, h => h.Value.Replace(HttpAsk.SecretMark, value, StringComparison.Ordinal)),
        };
    }
}
