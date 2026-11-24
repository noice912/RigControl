using System.Text.Json;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// WLED strips and matrices (the open firmware for ESP boards), through WLED's own local interfaces: its JSON page
/// for "what are you" and its "UDP realtime" port for colours. Built from WLED's published protocol; not yet tried on
/// a real device. Nothing is paired and nothing is saved on the WLED: when the colours stop, it goes back to its own
/// effect by itself. Only /json/info and /json/state are ever read (never the settings, never a PIN).
/// </summary>
public sealed class WledProvider : INetworkLightProvider
{
    internal static readonly WledDialect Dialect = new(
        What: "a WLED",
        MdnsType: "_wled._tcp.local.",
        InfoPath: "/json/info",
        StatePath: "/json/state",
        LedsPerPacket: 489, // WLED drops packets over 1472 bytes
        NeedsVersionAndLeds: true,
        DefaultName: "WLED",
        ModelName: "WLED");

    public string Brand => "wled";
    public string DisplayName => "WLED";
    public bool Tested => false;
    public string Notes =>
        "Strips and matrices running WLED. Every LED takes its own part of the picture. "
        + "Built from WLED's published protocol and not yet tried on a real device.";
    public double MaxFramesPerSecond => 30;
    public string PointName => "LED";

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "The WLED must be on the same network as this PC.",
            "Press Search for lights, or type the WLED's address.",
            "If nothing lights up, open the WLED's own page, go to Config, then Sync Interfaces, and check that \"Receive UDP realtime\" is ticked.",
        },
        ReturnsSecret: false,
        NothingFound: "No WLED answered the search. A WLED with an empty mDNS name doesn't announce itself: type its address instead.");

    public Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct) =>
        WledWire.DiscoverAsync(net, Dialect, ct);

    public Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct) =>
        WledWire.PairAsync(net, Dialect, request, ct);

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        var info = await WledWire.RequireInfoAsync(net, Dialect, light, ct).ConfigureAwait(false);
        var state = await ReadStateAsync(net, light, ct).ConfigureAwait(false);

        string? note = null;
        var mainSegment = 0;
        if (state is { } s)
        {
            if (WledWire.Int(s, "lor", 0) != 0)
                note = "Live override is on in WLED, so it ignores colours sent to it. Turn it off in WLED.";
            // "Use main segment only": the colours land on that segment, so the frame is as long as it is.
            if (info.LiveSegment >= 0) mainSegment = SegmentLength(s, info.LiveSegment);
        }

        if (mainSegment > 0)
            return LightDescription.Strip(Math.Min(mainSegment, WledWire.MostLeds)) with { Note = note };

        var count = Math.Clamp(info.Count, 1, WledWire.MostLeds);
        int w = info.MatrixWidth, h = info.MatrixHeight;
        if (info.LiveSegment < 0 && w > 0 && h > 0 && (long)w * h == info.Count && info.Count <= WledWire.MostLeds)
        {
            var points = new List<LightPoint>(count);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    points.Add(new LightPoint(x + y * w, (x + 0.5) / w, (y + 0.5) / h));
            return new LightDescription(LightKind.Points, points) { PointSize = 1.0 / Math.Max(w, h), Note = note };
        }
        return LightDescription.Strip(count) with { Note = note };
    }

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        // Read again: the realtime port can be changed in WLED's settings.
        var info = await WledWire.RequireInfoAsync(net, Dialect, light, ct).ConfigureAwait(false);
        var count = Math.Min(description.Points.Count, WledWire.MostLeds);
        return new WledSession(net.OpenUdp(), light.Address, info.UdpPort, Dialect.LedsPerPacket, count, (session, _) =>
        {
            // Nothing to put back: WLED returns to its own effect when realtime ends. This ends it at once
            // instead of after the two-second timeout.
            session.SendRaw(WledWire.HandBack());
            return Task.CompletedTask;
        });
    }

    /// <summary>The state, or null when it can't be read (it only adds a note and the main segment's length).</summary>
    private static async Task<JsonElement?> ReadStateAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        try
        {
            var url = WledWire.Url(light.Address, WledWire.PortOf(light.Data), Dialect.StatePath);
            var reply = await net.HttpAsync(HttpAsk.Get(url), ct).ConfigureAwait(false);
            return reply.Ok ? WledWire.Object(reply.Body) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static int SegmentLength(JsonElement state, int id)
    {
        if (!state.TryGetProperty("seg", out var segs) || segs.ValueKind != JsonValueKind.Array) return 0;
        var index = 0;
        foreach (var seg in segs.EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Object && WledWire.Int(seg, "id", index) == id)
            {
                var length = WledWire.Int(seg, "stop", 0) - WledWire.Int(seg, "start", 0);
                var rows = WledWire.Int(seg, "stopY", 0) - WledWire.Int(seg, "startY", 0);
                if (length <= 0) length = WledWire.Int(seg, "len", 0);
                return length <= 0 ? 0 : (int)Math.Min((long)length * Math.Max(1, rows), int.MaxValue);
            }
            index++;
        }
        return 0;
    }
}

/// <summary>Where one kind of WLED-based device differs from another (WLED itself, or a renamed one).</summary>
/// <param name="What">The device in a sentence, with its article ("a WLED").</param>
/// <param name="NeedsVersionAndLeds">True when an answer without "ver" and "leds" is not this device.</param>
internal sealed record WledDialect(string What, string MdnsType, string InfoPath, string StatePath, int LedsPerPacket,
    bool NeedsVersionAndLeds, string DefaultName, string ModelName);

/// <summary>What a WLED says about itself. <see cref="Count"/> is 0 when it didn't say.</summary>
internal sealed record WledInfo(string Mac, string Name, string Version, string Arch, int UdpPort, int Count,
    int MatrixWidth, int MatrixHeight, int LiveSegment);

/// <summary>The wire protocol WLED-based lights share: the info answer, the search, and the realtime colour packets.</summary>
internal static class WledWire
{
    /// <summary>No strip is treated as longer than this, whatever the device says.</summary>
    public const int MostLeds = 4096;
    public const int DefaultUdpPort = 21324;
    /// <summary>Seconds the light keeps the colours after the last packet. Never 255: that would stick.</summary>
    public const byte TimeoutSeconds = 2;

    public static string Url(string address, int port, string path) =>
        port == 80 ? $"http://{address}{path}" : $"http://{address}:{port}{path}";

    /// <summary>The web port remembered at pairing or found by the search; 80 when none.</summary>
    public static int PortOf(IReadOnlyDictionary<string, string>? data) =>
        data is not null && data.TryGetValue("port", out var text) && int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : 80;

    public static JsonElement? Object(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    public static int Int(JsonElement obj, string name, int fallback) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
        : obj.TryGetProperty(name, out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? (v.GetBoolean() ? 1 : 0)
        : fallback;

    public static string Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var clean = new string((v.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 48 ? clean[..48] : clean;
    }

    /// <summary>Reads an info answer; null when it is not from this kind of device.</summary>
    public static WledInfo? ParseInfo(string body, WledDialect dialect)
    {
        if (Object(body) is not { } root) return null;
        // Some answers carry the info inside { "state": ..., "info": ... }.
        if (!root.TryGetProperty("mac", out _) && root.TryGetProperty("info", out var inner) && inner.ValueKind == JsonValueKind.Object) root = inner;

        var mac = Text(root, "mac").Replace(":", "").Replace("-", "").ToLowerInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit)) return null;
        var version = Text(root, "ver");
        var hasLeds = root.TryGetProperty("leds", out var leds) && leds.ValueKind == JsonValueKind.Object;
        if (dialect.NeedsVersionAndLeds && (version == "" || !hasLeds)) return null;

        int count = 0, w = 0, h = 0;
        if (hasLeds)
        {
            count = Math.Max(0, Int(leds, "count", 0));
            if (leds.TryGetProperty("matrix", out var matrix) && matrix.ValueKind == JsonValueKind.Object)
            {
                w = Math.Max(0, Int(matrix, "w", 0));
                h = Math.Max(0, Int(matrix, "h", 0));
            }
        }
        var udp = Int(root, "udpport", DefaultUdpPort);
        if (udp is <= 0 or > 65535) udp = DefaultUdpPort;
        return new WledInfo(mac, Text(root, "name"), version, Text(root, "arch"), udp, count, w, h, Int(root, "liveseg", -1));
    }

    /// <summary>Asks the device what it is. Null when something answered that is not this kind of device; throws when nothing answered.</summary>
    public static async Task<WledInfo?> ReadInfoAsync(ILightNetwork net, WledDialect dialect, string address, int port, TimeSpan timeout, CancellationToken ct)
    {
        var reply = await net.HttpAsync(HttpAsk.Get(Url(address, port, dialect.InfoPath)) with { Timeout = timeout }, ct).ConfigureAwait(false);
        return reply.Ok ? ParseInfo(reply.Body, dialect) : null;
    }

    public static async Task<WledInfo> RequireInfoAsync(ILightNetwork net, WledDialect dialect, PairedLight light, CancellationToken ct) =>
        await ReadInfoAsync(net, dialect, light.Address, PortOf(light.Data), TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"The device at {light.Address} no longer answers like {dialect.What}. If the light got a new address, press Search for lights.");

    public static string NameOf(WledInfo info, WledDialect dialect) => info.Name != "" ? info.Name : dialect.DefaultName;

    public static string ModelOf(WledInfo info, WledDialect dialect) =>
        (dialect.ModelName + (info.Version != "" ? " " + info.Version : "") + (info.Arch != "" ? $" ({info.Arch})" : "")).Trim();

    /// <summary>
    /// Asks who announces the service, then asks each one what it is. Done well inside six seconds. An announcement
    /// only says "something at this address": the device is asked on the web port these lights always use (80),
    /// whatever port the announcement names, and only when its address is on a network that was searched.
    /// </summary>
    public static async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, WledDialect dialect, CancellationToken ct,
        Func<WledInfo, string>? name = null)
    {
        var services = await net.BrowseAsync(dialect.MdnsType, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        var asked = services
            .Where(s => LanAddress.OnSearchedNetwork(net, s.Address))
            .GroupBy(s => LanAddress.Normalise(s.Address)).Select(g => g.First())
            .Take(NetworkLights.MostFoundPerBrand)
            .Select(async s =>
            {
                var address = LanAddress.Normalise(s.Address);
                const int port = 80;
                try
                {
                    var info = await ReadInfoAsync(net, dialect, address, port, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
                    if (info is null) return null;
                    return new FoundLight(info.Mac, name?.Invoke(info) ?? NameOf(info, dialect), ModelOf(info, dialect), address)
                    {
                        Data = new Dictionary<string, string> { ["port"] = port.ToString() },
                    };
                }
                catch { return null; } // it didn't answer, or the search was stopped: it is just not listed
            })
            .ToList();
        var found = await Task.WhenAll(asked).ConfigureAwait(false);
        return found.Where(f => f is not null).Select(f => f!).GroupBy(f => f.Id).Select(g => g.First()).ToList();
    }

    /// <summary>No pairing: checks that the address is this kind of device and notes its id.</summary>
    public static async Task<PairResult> PairAsync(ILightNetwork net, WledDialect dialect, PairRequest request, CancellationToken ct,
        Func<WledInfo, string>? name = null)
    {
        var port = PortOf(request.Found?.Data);
        var info = await ReadInfoAsync(net, dialect, request.Address, port, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Something answered at {request.Address}, but it isn't {dialect.What}. Check the address and try again.");
        return new PairResult(info.Mac, name?.Invoke(info) ?? NameOf(info, dialect), ModelOf(info, dialect), request.Address)
        {
            Data = new Dictionary<string, string> { ["port"] = port.ToString() },
        };
    }

    /// <summary>
    /// One frame as "DNRGB" realtime packets: 4, the timeout in seconds, the first LED's number (high byte, low byte),
    /// then red, green, blue for each LED. A long strip is cut into several packets, in order.
    /// </summary>
    public static List<byte[]> Dnrgb(IReadOnlyList<int> colours, int ledsPerPacket, int most = MostLeds)
    {
        var total = Math.Min(colours.Count, Math.Min(most, MostLeds));
        var per = Math.Clamp(ledsPerPacket, 1, 489);
        var packets = new List<byte[]>((total + per - 1) / per);
        for (var start = 0; start < total; start += per)
        {
            var n = Math.Min(per, total - start);
            var packet = new byte[4 + n * 3];
            packet[0] = 4;
            packet[1] = TimeoutSeconds;
            packet[2] = (byte)(start >> 8);
            packet[3] = (byte)start;
            for (var i = 0; i < n; i++)
            {
                var c = colours[start + i];
                packet[4 + i * 3] = (byte)(c >> 16);
                packet[5 + i * 3] = (byte)(c >> 8);
                packet[6 + i * 3] = (byte)c;
            }
            packets.Add(packet);
        }
        return packets;
    }

    /// <summary>A "DRGB" packet with timeout 0 and one dark LED: tells WLED the realtime colours are over now.</summary>
    public static byte[] HandBack() => new byte[] { 2, 0, 0, 0, 0 };
}

/// <summary>Colours going to one WLED-based light over its realtime UDP port. No web requests are made per frame.</summary>
internal sealed class WledSession : ILightSession
{
    private readonly IUdpPort _udp;
    private readonly string _address;
    private readonly int _port;
    private readonly int _ledsPerPacket;
    private readonly Func<WledSession, CancellationToken, Task> _stop;

    public WledSession(IUdpPort udp, string address, int port, int ledsPerPacket, int count, Func<WledSession, CancellationToken, Task> stop)
    {
        _udp = udp;
        _address = address;
        _port = port;
        _ledsPerPacket = ledsPerPacket;
        Count = Math.Clamp(count, 1, WledWire.MostLeds);
        _stop = stop;
    }

    /// <summary>How many LEDs a frame has.</summary>
    public int Count { get; }

    // The light drops the colours two seconds after the last packet, so a still picture is sent again every second.
    public TimeSpan KeepAlive => TimeSpan.FromSeconds(1);

    public Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var packet in WledWire.Dnrgb(colours, _ledsPerPacket, Count)) _udp.Send(packet, _address, _port);
        return Task.CompletedTask;
    }

    public void SendRaw(byte[] packet) => _udp.Send(packet, _address, _port);

    /// <summary>One frame with every LED dark.</summary>
    public void SendBlack()
    {
        foreach (var packet in WledWire.Dnrgb(new int[Count], _ledsPerPacket, Count)) _udp.Send(packet, _address, _port);
    }

    public Task StopAsync(CancellationToken ct) => _stop(this, ct);

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
