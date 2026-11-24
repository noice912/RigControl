using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RigControl.Lighting;

/// <summary>A Nanoleaf paired with RigControl (its own local API, port 16021). The token is kept DPAPI-protected.</summary>
public sealed class NanoleafSettings
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>The auth token, protected for this Windows user (base64).</summary>
    public string ProtectedToken { get; set; } = "";

    internal string? Token()
    {
        if (ProtectedToken == "") return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    internal void SetToken(string token) =>
        ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RigControl.Nanoleaf");
}

/// <summary>
/// One panel: its id and centre, 0-1 across the whole Nanoleaf (y down, like the layout picture), and for a Lines bar
/// its two ends (for drawing; the same point twice for other shapes).
/// </summary>
public sealed record NanoleafPanel(int Id, double X, double Y, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0);

/// <summary>
/// Nanoleaf's local API: find it on the network, pair (the user holds the power button), read where the panels are,
/// and stream a colour per panel ("extControl" v2 over UDP port 60222, as Nanoleaf documents for screen mirroring).
/// </summary>
public sealed class NanoleafClient : IDisposable
{
    public const int ApiPort = 16021;
    public const int StreamPort = 60222;

    // Shapes that carry no light of their own: Rhythm module, Shapes controller, Lines connector, controller cap, power connector.
    private static readonly HashSet<int> NoLight = new() { 1, 12, 16, 19, 20 };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private UdpClient? _udp;

    public string Ip { get; }
    private readonly string _token;

    public NanoleafClient(string ip, string token)
    {
        Ip = ip;
        _token = token;
    }

    private Uri Api(string path) => new($"http://{Ip}:{ApiPort}/api/v1/{_token}/{path}");

    // ---------------------------------------------------------------- finding and pairing

    /// <summary>Every address on this PC's local networks that answers like a Nanoleaf (port 16021, 401 without a token).</summary>
    public static async Task<List<string>> DiscoverAsync(CancellationToken ct)
    {
        var hosts = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            // Only networks with a router (leaves out VPN adapters and virtual switches without one).
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength is < 22 or > 30) continue;
                var ip = BitConverter.ToUInt32(Enumerable.Reverse(ua.Address.GetAddressBytes()).ToArray());
                var mask = uint.MaxValue << (32 - ua.PrefixLength);
                var net = ip & mask;
                for (var h = net + 1; h < (net | ~mask); h++)
                    if (h != ip) hosts.Add(new IPAddress(Enumerable.Reverse(BitConverter.GetBytes(h)).ToArray()));
            }
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var found = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(hosts.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (addr, token) =>
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(500);
                await tcp.ConnectAsync(addr, ApiPort, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            try
            {
                using var r = await http.GetAsync($"http://{addr}:{ApiPort}/api/v1/", token).ConfigureAwait(false);
                if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) found.Add(addr.ToString());
            }
            catch
            {
                // not a Nanoleaf
            }
        }).ConfigureAwait(false);
        return found.OrderBy(a => a, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Asks the Nanoleaf for a token. It only gives one in the 30 s after its power button was held for 5-7 s
    /// (the lights flash); otherwise it answers 403.
    /// </summary>
    public static async Task<string> PairAsync(string ip, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var r = await http.PostAsync($"http://{ip}:{ApiPort}/api/v1/new", null, ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("The Nanoleaf isn't ready to pair. Hold its power button for 5 to 7 seconds until the lights flash, then press Pair within 30 seconds.");
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("auth_token").GetString() ?? throw new InvalidOperationException("The Nanoleaf answered without a token.");
    }

    // ---------------------------------------------------------------- reading

    public async Task<(string Name, string Serial)> InfoAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(Api(""), ct).ConfigureAwait(false));
        var root = doc.RootElement;
        return (root.TryGetProperty("name", out var n) ? n.GetString() ?? "Nanoleaf" : "Nanoleaf",
                root.TryGetProperty("serialNo", out var s) ? s.GetString() ?? "" : "");
    }

    public async Task<List<NanoleafPanel>> PanelsAsync(CancellationToken ct)
    {
        var layoutJson = await _http.GetStringAsync(Api("panelLayout/layout"), ct).ConfigureAwait(false);
        var orientation = 0.0;
        try
        {
            using var o = JsonDocument.Parse(await _http.GetStringAsync(Api("panelLayout/globalOrientation"), ct).ConfigureAwait(false));
            if (o.RootElement.TryGetProperty("value", out var v)) orientation = v.GetDouble();
        }
        catch
        {
            // no orientation: as laid out
        }
        return ParseLayout(layoutJson, orientation);
    }

    /// <summary>Panel centres turned by the wall orientation, then fitted to 0-1 (y down). Pieces without light are left out.</summary>
    internal static List<NanoleafPanel> ParseLayout(string json, double orientationDegrees)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var raw = new List<(int Id, double X, double Y, bool Line, double O, double Len)>();
        var a = -orientationDegrees * Math.PI / 180;
        var topLen = root.TryGetProperty("sideLength", out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetDouble() : 0;
        foreach (var p in root.GetProperty("positionData").EnumerateArray())
        {
            var shape = p.TryGetProperty("shapeType", out var st) ? st.GetInt32() : 0;
            if (NoLight.Contains(shape)) continue;
            double x = p.GetProperty("x").GetDouble(), y = p.GetProperty("y").GetDouble();
            var o = p.TryGetProperty("o", out var ov) ? ov.GetDouble() : 0;
            var len = p.TryGetProperty("sideLength", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetDouble() : topLen;
            raw.Add((p.GetProperty("panelId").GetInt32(), x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a),
                shape is 17 or 18, o, len));
        }
        if (raw.Count == 0) return new();
        // A Lines bar without a stated length: about 80% of the gap to the nearest other bar.
        double Nearest(int i) => raw.Where((_, j) => j != i).Select(r => Math.Sqrt(Math.Pow(r.X - raw[i].X, 2) + Math.Pow(r.Y - raw[i].Y, 2)))
            .DefaultIfEmpty(100).Min();
        var ends = raw.Select((r, i) =>
        {
            if (!r.Line) return (r.X, r.Y, r.X, r.Y);
            var half = (r.Len > 0 ? r.Len : 0.8 * Nearest(i)) / 2;
            var ang = r.O * Math.PI / 180 - a;
            return (r.X - half * Math.Cos(ang), r.Y - half * Math.Sin(ang), r.X + half * Math.Cos(ang), r.Y + half * Math.Sin(ang));
        }).ToList();
        var xs = ends.SelectMany(e => new[] { e.Item1, e.Item3 }).ToList();
        var ys = ends.SelectMany(e => new[] { e.Item2, e.Item4 }).ToList();
        double minX = xs.Min(), maxX = xs.Max(), minY = ys.Min(), maxY = ys.Max();
        double Fx(double v) => maxX - minX < 1e-6 ? 0.5 : (v - minX) / (maxX - minX);
        // Nanoleaf's y grows upward; the layout picture's grows downward.
        double Fy(double v) => maxY - minY < 1e-6 ? 0.5 : 1 - (v - minY) / (maxY - minY);
        return raw.Select((r, i) => new NanoleafPanel(r.Id, Fx(r.X), Fy(r.Y), Fx(ends[i].Item1), Fy(ends[i].Item2), Fx(ends[i].Item3), Fy(ends[i].Item4))).ToList();
    }

    public async Task<string?> SelectedEffectAsync(CancellationToken ct)
    {
        try
        {
            var s = await _http.GetStringAsync(Api("effects/select"), ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<string>(s);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Puts an effect back (the one that was on before RigControl streamed).</summary>
    public async Task SelectEffectAsync(string name, CancellationToken ct)
    {
        using var r = await _http.PutAsJsonAsync(Api("effects"), new { select = name }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- streaming

    /// <summary>Turns the Nanoleaf on and into external control; colours then go by <see cref="Send"/>.</summary>
    public async Task StartStreamAsync(CancellationToken ct)
    {
        using (await _http.PutAsJsonAsync(Api("state"), new { on = new { value = true } }, ct).ConfigureAwait(false)) { }
        using var r = await _http.PutAsJsonAsync(Api("effects"),
            new { write = new { command = "display", animType = "extControl", extControlVersion = "v2" } }, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        _udp ??= new UdpClient();
    }

    /// <summary>One frame: a colour (0xRRGGBB) per panel, fading over <paramref name="transitionTenths"/> x 100 ms.</summary>
    public void Send(IReadOnlyList<(int Id, int Rgb)> panels, int transitionTenths = 1)
    {
        var packet = Packet(panels, transitionTenths);
        (_udp ??= new UdpClient()).Send(packet, packet.Length, Ip, StreamPort);
    }

    /// <summary>extControl v2: panel count (2 bytes), then per panel id (2), R, G, B, W, transition (2); big-endian.</summary>
    internal static byte[] Packet(IReadOnlyList<(int Id, int Rgb)> panels, int transitionTenths)
    {
        var b = new byte[2 + panels.Count * 8];
        b[0] = (byte)(panels.Count >> 8);
        b[1] = (byte)panels.Count;
        var i = 2;
        foreach (var (id, rgb) in panels)
        {
            b[i++] = (byte)(id >> 8);
            b[i++] = (byte)id;
            b[i++] = (byte)(rgb >> 16);
            b[i++] = (byte)(rgb >> 8);
            b[i++] = (byte)rgb;
            b[i++] = 0;
            b[i++] = (byte)(transitionTenths >> 8);
            b[i++] = (byte)transitionTenths;
        }
        return b;
    }

    public void Dispose()
    {
        _http.Dispose();
        _udp?.Dispose();
    }
}
