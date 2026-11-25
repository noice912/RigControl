using System.Text.Json;
using RigControl.Lighting.Network;

namespace RigControl.Lighting;

/// <summary>
/// A Nanoleaf paired with RigControl (its own local API, port 16021). The token is kept protected by the system's
/// own means (<see cref="ISecretProtector"/>): on Windows that is DPAPI for the signed-in user, as it always was.
/// </summary>
public sealed class NanoleafSettings
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>The auth token, protected for this user (base64).</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>What it was doing before RigControl first changed it by hand (for "Give back"); null when it hasn't.</summary>
    public string? Before { get; set; }

    /// <summary>
    /// What the token is protected for. Its UTF-8 bytes are the extra bytes DPAPI was always given for it, so a token
    /// saved by an older version still reads.
    /// </summary>
    internal const string TokenPurpose = "RigControl.Nanoleaf";

    /// <summary>The token, or null when there is none or it can't be read (another account, another PC, damaged).</summary>
    internal string? Token(ISecretProtector protector) => ProtectedToken == "" ? null : protector.Unprotect(ProtectedToken, TokenPurpose);

    internal void SetToken(string token, ISecretProtector protector) => ProtectedToken = protector.Protect(token, TokenPurpose);
}

/// <summary>
/// One panel: its id and centre, 0-1 across the whole Nanoleaf (y down, like the layout picture), and for a Lines bar
/// its two ends (for drawing; the same point twice for other shapes).
/// </summary>
public sealed record NanoleafPanel(int Id, double X, double Y, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0);

/// <summary>
/// Nanoleaf's local API: find it on the network, pair (the user holds the power button), read where the panels are,
/// and stream a colour per panel ("extControl" v2 over UDP port 60222, as Nanoleaf documents for screen mirroring).
/// Everything goes through <see cref="ILightNetwork"/>; the token is only ever filled in for the paired address.
/// </summary>
public sealed class NanoleafClient : IDisposable
{
    public const int ApiPort = 16021;
    public const int StreamPort = 60222;

    // Shapes that carry no light of their own: Rhythm module, Shapes controller, Lines connector, controller cap, power connector.
    private static readonly HashSet<int> NoLight = new() { 1, 12, 16, 19, 20 };

    private readonly ILightNetwork _net;
    private readonly BoundSecret _token;
    private IUdpPort? _udp;

    public string Ip { get; }

    public NanoleafClient(ILightNetwork net, string ip, BoundSecret token)
    {
        _net = net;
        Ip = ip;
        _token = token;
    }

    private string Api(string path) => $"http://{Ip}:{ApiPort}/api/v1/{HttpAsk.SecretMark}/{path}";

    private async Task<string> GetAsync(string path, CancellationToken ct) =>
        (await _net.HttpAsync(HttpAsk.Get(Api(path)) with { Secret = _token }, ct).ConfigureAwait(false)).EnsureOk().Body;

    private Task<HttpReply> PutAsync(string path, object body, CancellationToken ct) =>
        _net.HttpAsync(HttpAsk.Put(Api(path), JsonSerializer.Serialize(body)) with { Secret = _token }, ct);

    // ---------------------------------------------------------------- finding and pairing

    /// <summary>Every address on this PC's home networks that answers like a Nanoleaf (port 16021, 401 without a token).</summary>
    public static async Task<List<string>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        // Only networks with a router (leaves out VPN adapters and virtual switches without one), and only small ones.
        var hosts = net.LocalNetworks().Where(n => n.HasGateway).SelectMany(n => n.OtherHosts()).Distinct().ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (addr, token) =>
        {
            try
            {
                using var tcp = await net.ConnectTcpAsync(addr, ApiPort, TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return;
            }
            try
            {
                var r = await net.HttpAsync(HttpAsk.Get($"http://{addr}:{ApiPort}/api/v1/") with { Timeout = TimeSpan.FromSeconds(2) }, token).ConfigureAwait(false);
                if (r.Status is 401 or 403) found.Add(addr);
            }
            catch (Exception) when (!token.IsCancellationRequested)
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
    public static async Task<string> PairAsync(ILightNetwork net, string ip, CancellationToken ct)
    {
        var r = await net.HttpAsync(HttpAsk.Post($"http://{ip}:{ApiPort}/api/v1/new") with { Timeout = TimeSpan.FromSeconds(5) }, ct).ConfigureAwait(false);
        if (r.Status == 403)
            throw new InvalidOperationException("The Nanoleaf isn't ready to pair. Hold its power button for 5 to 7 seconds until the lights flash, then press Pair within 30 seconds.");
        r.EnsureOk();
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.GetProperty("auth_token").GetString() ?? throw new InvalidOperationException("The Nanoleaf answered without a token.");
    }

    // ---------------------------------------------------------------- reading

    public async Task<(string Name, string Serial)> InfoAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync("", ct).ConfigureAwait(false));
        var root = doc.RootElement;
        return (root.TryGetProperty("name", out var n) ? n.GetString() ?? "Nanoleaf" : "Nanoleaf",
                root.TryGetProperty("serialNo", out var s) ? s.GetString() ?? "" : "");
    }

    public async Task<List<NanoleafPanel>> PanelsAsync(CancellationToken ct)
    {
        var layoutJson = await GetAsync("panelLayout/layout", ct).ConfigureAwait(false);
        var orientation = 0.0;
        try
        {
            using var o = JsonDocument.Parse(await GetAsync("panelLayout/globalOrientation", ct).ConfigureAwait(false));
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
            var s = await GetAsync("effects/select", ct).ConfigureAwait(false);
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
        await PutAsync("effects", new { select = name }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- direct control (Nanoleaf's documented OpenAPI)

    /// <summary>Everything the Nanoleaf says about itself in one answer (GET /api/v1/{token}/): its state and selected effect among it.</summary>
    public Task<string> AllInfoAsync(CancellationToken ct) => GetAsync("", ct);

    /// <summary>PUT /state with a body such as {"on":{"value":true}} or {"hue":{"value":120},"sat":{"value":100}}.</summary>
    public async Task SetStateAsync(string json, CancellationToken ct) =>
        (await _net.HttpAsync(HttpAsk.Put(Api("state"), json) with { Secret = _token }, ct).ConfigureAwait(false)).EnsureOk();

    /// <summary>PUT /effects {"select": name}, checked.</summary>
    public async Task SelectEffectCheckedAsync(string name, CancellationToken ct) =>
        (await PutAsync("effects", new { select = name }, ct).ConfigureAwait(false)).EnsureOk();

    // ---------------------------------------------------------------- streaming

    /// <summary>Turns the Nanoleaf on and into external control; colours then go by <see cref="Send"/>.</summary>
    public async Task StartStreamAsync(CancellationToken ct)
    {
        await PutAsync("state", new { on = new { value = true } }, ct).ConfigureAwait(false);
        var r = await PutAsync("effects",
            new { write = new { command = "display", animType = "extControl", extControlVersion = "v2" } }, ct).ConfigureAwait(false);
        r.EnsureOk();
        _udp ??= _net.OpenUdp();
    }

    /// <summary>One frame: a colour (0xRRGGBB) per panel, fading over <paramref name="transitionTenths"/> x 100 ms.</summary>
    public void Send(IReadOnlyList<(int Id, int Rgb)> panels, int transitionTenths = 1)
    {
        var packet = Packet(panels, transitionTenths);
        (_udp ??= _net.OpenUdp()).Send(packet, Ip, StreamPort);
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

    public void Dispose() => _udp?.Dispose();
}
