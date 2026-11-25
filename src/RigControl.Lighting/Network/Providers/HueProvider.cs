using System.Globalization;
using System.Text.Json;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// Philips Hue through the bridge's own local API (version 2, https). One paired "light" is one Hue Bridge; its
/// light points are the bridge's colour lights. Built from open-source descriptions of the protocol (Hyperion.ng,
/// aiohue / Home Assistant, Q42 HueApi, the OpenHue specification); no code was copied, and it has not yet been tried
/// on a real bridge.
///
/// What it does: found by its Bonjour name and checked by asking who it is; paired by the round button on the bridge.
/// Nothing is touched until the user places the bridge on the layout. Then the colour lights of the bridge's
/// entertainment area (every colour light when the bridge has no such area) are sent one colour each, a few times a
/// second, and get their old state back when the sync stops.
/// What it does not do yet: Hue's Entertainment stream (fast sync, gradient strips piece by piece). The key that
/// stream needs is asked for and kept at pairing, so it can be added later without pressing the button again.
///
/// Certificate: a bridge carries its own certificate whose name is the bridge's id. At pairing the name must match
/// and the certificate's fingerprint is remembered; afterwards only that exact certificate is accepted. (The first
/// meeting is "trust on first use": Signify's own authority is not checked.) The cloud is never contacted.
/// </summary>
public sealed class HueProvider : INetworkLightProvider
{
    internal const string ButtonMessage = "Press the round button on top of the Hue Bridge, then press Pair within 30 seconds.";
    internal const string RemovedMessage = "The Hue Bridge no longer accepts RigControl. Press Forget, then pair it again.";
    internal const string TooOldMessage = "This Hue Bridge's software is too old for RigControl. Update it in the Hue app, then pair it again.";
    internal const string NoColourLightsMessage = "This Hue Bridge has no colour lights, so there is nothing for RigControl to colour.";
    internal const string NoAreaNote = "This Hue Bridge has no entertainment area, so every colour light it has follows, in every room, and RigControl can't know where they are. Make an entertainment area in the Hue app to choose which lights follow and where they are.";
    internal const string EmptyAreaMessage = "The entertainment area on this Hue Bridge has no colour lights in it. Add the lights that should follow to it in the Hue app.";

    /// <summary>Said on the paired bridge when some colour lights are outside the entertainment area.</summary>
    internal static string LeftAloneNote(int count) => count == 1
        ? "1 colour light is outside the entertainment area and is left alone. Add it to the area in the Hue app to have it follow."
        : $"{count} colour lights are outside the entertainment area and are left alone. Add them to the area in the Hue app to have them follow.";

    // The first bridge software that has the version 2 API.
    private const long OldestSoftware = 1948086000;
    // What the libraries repeat as Signify's guidance: about ten light commands a second for the whole bridge.
    private const double BridgeCommandsPerSecond = 10;
    private const string KeyHeader = "hue-application-key";

    /// <summary>How waiting is done; tests swap it so nothing really waits.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;

    public string Brand => "hue";
    public string DisplayName => "Philips Hue";
    public bool Tested => false;
    public string Notes =>
        "A Hue Bridge and its colour lights. Effects leave it alone until you place the bridge on the layout. Then every colour light in the " +
        "bridge's entertainment area follows and is switched on while it does (with no entertainment area: every colour light on the bridge, in every room). " +
        "The bridge's own switch, brightness and colour here change those same lights; Everything at once and Turn all lights off leave Hue alone. " +
        "When the sync stops, each light goes back to how it was, on or off. Each light follows as one colour a few times a second. " +
        "Gradient strips piece by piece and fast sync need Hue's Entertainment stream, which is not built yet. " +
        "Built from open-source descriptions of the protocol; not yet tried on a real bridge.";
    public double MaxFramesPerSecond => 5;
    public string PointName => "light";
    // A bridge can hold the lights of a whole house: none of them is touched until the user places the bridge.
    public bool FollowsWhenNotPlaced => false;

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Press Search for lights and wait for your Hue Bridge to show up (or type the bridge's address).",
            "Press the round button on top of the Hue Bridge.",
            "Press Pair within 30 seconds.",
            "In the Hue app, make an entertainment area with the lights that should follow (Settings > Entertainment areas). Without one, every colour light on the bridge follows.",
            "To have the effects play on Hue: turn Per LED on and place the bridge on the Layout. Until then no effect touches a Hue light. Lights that follow are switched on, and go back to how they were when the sync stops.",
        },
        ReturnsSecret: true,
        ManualAddress: true,
        NothingFound: "No Hue Bridge answered on this network. Check it's plugged into the same router as this PC, or type its address (the Hue app shows it in the bridge's details).",
        UnusedKeyHint: "An unused entry called \"rigcontrol#pc\" stays on the bridge from this try; it does no harm.");

    // ---- search

    public async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        var services = await net.BrowseAsync("_hue._tcp.local.", TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        var asked = services
            // Only addresses on a network that was searched are asked, and no more than a home could hold.
            .Where(s => LanAddress.OnSearchedNetwork(net, s.Address))
            .GroupBy(s => LanAddress.Normalise(s.Address))
            .Select(g => g.First())
            .Take(NetworkLights.MostFoundPerBrand)
            .Select(async s =>
            {
                // Anything can announce itself as a bridge; only one that says who it is counts.
                var hint = s.Txt.FirstOrDefault(t => t.Key.Equals("bridgeid", StringComparison.OrdinalIgnoreCase)).Value;
                var config = await ConfigAsync(net, s.Address, hint, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
                return config is null ? null : new FoundLight(config.Id, config.Name, config.Model, s.Address)
                {
                    Data = new Dictionary<string, string> { ["bridgeid"] = config.Id },
                };
            })
            .ToList();
        var found = await Task.WhenAll(asked).ConfigureAwait(false);
        return found.Where(f => f is not null).Select(f => f!).GroupBy(f => f.Id).Select(g => g.First()).ToList();
    }

    internal sealed record BridgeConfig(string Id, string Name, string Model, long Software);

    /// <summary>Asks the bridge who it is (needs no key). Null when nothing there says it is a Hue Bridge.</summary>
    internal static async Task<BridgeConfig?> ConfigAsync(ILightNetwork net, string address, string? expectedId, TimeSpan timeout, CancellationToken ct)
    {
        var reply = await TryAsync(net, HttpAsk.Get($"http://{address}/api/config") with { Timeout = timeout }, ct).ConfigureAwait(false);
        // Should a bridge stop answering this without https: ask again securely, which needs its id for the certificate.
        if (reply is not { Ok: true } && TidyBridgeId(expectedId) is { } id)
            reply = await TryAsync(net, HttpAsk.Get($"https://{address}/api/config") with { Timeout = timeout, Tls = new TlsRule(id, null) }, ct).ConfigureAwait(false);
        if (reply is not { Ok: true }) return null;
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (TidyBridgeId(Text(root, "bridgeid")) is not { } bridgeId) return null;
            // The bridge's own words are tidied: no control characters, no endless names.
            var name = DeviceText.Tidy(Text(root, "name"), 64) is { Length: > 0 } n ? n : "Hue Bridge";
            long.TryParse(Text(root, "swversion"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var software);
            return new BridgeConfig(bridgeId, name, DeviceText.Tidy(Text(root, "modelid"), 40), software);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<HttpReply?> TryAsync(ILightNetwork net, HttpAsk ask, CancellationToken ct)
    {
        try { return await net.HttpAsync(ask, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>A bridge id as the certificate carries it: 16 hex digits, lower case. Null for anything else.</summary>
    internal static string? TidyBridgeId(string? id)
    {
        var s = id?.Trim().ToLowerInvariant();
        return s is { Length: 16 } && s.All(Uri.IsHexDigit) ? s : null;
    }

    // ---- pairing

    public async Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        var address = request.Address;
        string? hint = null;
        if (request.Found is { } found) hint = found.Data.TryGetValue("bridgeid", out var known) ? known : found.Id;
        var config = await ConfigAsync(net, address, hint, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"There is no Hue Bridge at {address}. Check the address, or press Search for lights.");
        if (config.Software < OldestSoftware) throw new InvalidOperationException(TooOldMessage);

        // First meeting: the certificate must carry this bridge's id. Its fingerprint is remembered below.
        var tls = new TlsRule(config.Id, null);
        // The name the Hue app shows for this pairing. Nothing about the PC or its user goes in it.
        const string body = "{\"devicetype\":\"rigcontrol#pc\",\"generateclientkey\":true}";
        var started = DateTime.UtcNow;
        for (var attempt = 0; attempt < 30 && (DateTime.UtcNow - started).TotalSeconds < 30; attempt++)
        {
            if (attempt > 0) await Wait(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            var reply = await net.HttpAsync(HttpAsk.Post($"https://{address}/api", body) with { Tls = tls }, ct).ConfigureAwait(false);
            reply.EnsureOk();
            using var doc = JsonDocument.Parse(reply.Body);
            var first = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0] : default;
            if (Child(first, "success") is { } success && Text(success, "username") is { Length: > 0 } key)
            {
                if (reply.CertSha256 is not { Length: > 0 } cert)
                    throw new InvalidOperationException("The Hue Bridge didn't show its certificate, so it was not paired.");
                var secrets = new Dictionary<string, string> { ["token"] = key };
                // Kept for Hue's Entertainment stream, which is not built yet.
                if (Text(success, "clientkey") is { Length: > 0 } clientKey) secrets["clientkey"] = clientKey;
                return new PairResult(config.Id, config.Name, config.Model, address)
                {
                    Secrets = secrets,
                    Data = new Dictionary<string, string> { ["bridgeid"] = config.Id, ["cert"] = cert },
                };
            }
            // 101 = the button has not been pressed yet: keep asking.
            var error = Child(first, "error");
            if (error is { } e && Number(e, "type") is 101) continue;
            var why = error is { } e2 ? DeviceText.Tidy(Text(e2, "description"), 120) : null;
            throw new InvalidOperationException(why is { Length: > 0 }
                ? $"The Hue Bridge refused to pair: {why.TrimEnd('.')}."
                : "The Hue Bridge gave an answer RigControl doesn't understand, so it was not paired.");
        }
        throw new InvalidOperationException(ButtonMessage);
    }

    // ---- the bridge's API with the key

    private static TlsRule RuleFor(PairedLight light)
    {
        var id = TidyBridgeId(light.Data.TryGetValue("bridgeid", out var b) ? b : light.Id);
        // Without the remembered certificate there is no way to know it is the same bridge: never fall back to less.
        if (id is null || !light.Data.TryGetValue("cert", out var cert) || cert.Length == 0)
            throw new InvalidOperationException("The Hue Bridge's pairing is incomplete. Press Forget, then pair it again.");
        return new TlsRule(id, cert);
    }

    /// <summary>One call to the bridge. The key goes in a header, and only to the address it was paired with.</summary>
    internal static async Task<HttpReply> ClipAsync(ILightNetwork net, string address, BoundSecret key, TlsRule tls,
        string method, string path, string? body, CancellationToken ct)
    {
        var ask = new HttpAsk(method, $"https://{address}/clip/v2/resource/{path}")
        {
            Body = body,
            Headers = new Dictionary<string, string> { [KeyHeader] = HttpAsk.SecretMark },
            Secret = key,
            Tls = tls,
        };
        var reply = await net.HttpAsync(ask, ct).ConfigureAwait(false);
        // The app was removed in the Hue app (or the bridge was reset).
        if (reply.Status is 401 or 403) throw new InvalidOperationException(RemovedMessage);
        return reply;
    }

    /// <summary>A colour light as the bridge reports it. Gamut = red x, y, green x, y, blue x, y.</summary>
    internal sealed record HueLight(string Id, string Owner, string Name, bool On, double Brightness, double X, double Y, int? Mirek, double[]? Gamut);

    /// <summary>The bridge's colour lights, always in the same order (by id), so a frame's colours reach the same lights every time.</summary>
    private static async Task<List<HueLight>> ColourLightsAsync(ILightNetwork net, string address, BoundSecret key, TlsRule tls, CancellationToken ct)
    {
        var reply = (await ClipAsync(net, address, key, tls, "GET", "light", null, ct).ConfigureAwait(false)).EnsureOk();
        using var doc = JsonDocument.Parse(reply.Body);
        var lights = new List<HueLight>();
        foreach (var l in Items(doc.RootElement))
        {
            if (Text(l, "id") is not { Length: > 0 } id) continue;
            if (Child(l, "color") is not { } colour) continue; // white-only lights and plugs are left alone
            var xy = Child(colour, "xy");
            double[]? gamut = null;
            if (Child(colour, "gamut") is { } g && Point(g, "red") is { } r && Point(g, "green") is { } gr && Point(g, "blue") is { } bl)
                gamut = new[] { r.X, r.Y, gr.X, gr.Y, bl.X, bl.Y };
            lights.Add(new HueLight(
                id,
                Child(l, "owner") is { } o ? Text(o, "rid") ?? "" : "",
                Child(l, "metadata") is { } m ? Text(m, "name") ?? "" : "",
                Child(l, "on") is { } on && Child(on, "on") is { ValueKind: JsonValueKind.True },
                Child(l, "dimming") is { } dim && Number(dim, "brightness") is { } b ? b : 100,
                xy is { } p1 && Number(p1, "x") is { } x ? x : WhiteX,
                xy is { } p2 && Number(p2, "y") is { } y ? y : WhiteY,
                Child(l, "color_temperature") is { } t && Number(t, "mirek") is { } mirek ? (int)Math.Round(mirek) : null,
                gamut));
        }
        lights.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return lights;
    }

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        var key = light.RequireSecret("token");
        var tls = RuleFor(light);
        var lights = await ColourLightsAsync(net, light.Address, key, tls, ct).ConfigureAwait(false);
        if (lights.Count == 0) throw new InvalidOperationException(NoColourLightsMessage);

        var (places, hasArea) = await PlacesAsync(net, light.Address, key, tls, lights, ct).ConfigureAwait(false);
        var points = new List<LightPoint>(lights.Count);
        // A point's id is the light's place in the bridge's list (StartAsync reads the same list in the same order).
        if (hasArea)
        {
            // Only the lights the user put in the entertainment area follow; the rest of the house is left alone.
            for (var i = 0; i < lights.Count; i++)
                if (places.TryGetValue(lights[i].Id, out var at)) points.Add(new LightPoint(i, at.X, at.Y));
            if (points.Count == 0) throw new InvalidOperationException(EmptyAreaMessage);
            var left = lights.Count - points.Count;
            return new LightDescription(LightKind.Points, points) { Note = left > 0 ? LeftAloneNote(left) : null };
        }
        // No entertainment area at all: every colour light, in a row along the bottom (where they are isn't known).
        for (var i = 0; i < lights.Count; i++)
            points.Add(new LightPoint(i, Math.Round((i + 1) / (double)(lights.Count + 1), 4), 0.95));
        return new LightDescription(LightKind.Points, points) { Note = NoAreaNote };
    }

    /// <summary>
    /// Where the lights are, from an entertainment area made in the Hue app (the screen one when there is one).
    /// An area lists channels with a position; a channel belongs to a device's entertainment service; the light of
    /// that device gets the position. A light with several channels (a gradient strip) gets the middle of them.
    /// </summary>
    private static async Task<(Dictionary<string, (double X, double Y)> Places, bool HasArea)> PlacesAsync(ILightNetwork net, string address,
        BoundSecret key, TlsRule tls, List<HueLight> lights, CancellationToken ct)
    {
        var places = new Dictionary<string, (double X, double Y)>();
        var hasArea = false;
        var areas = await ClipAsync(net, address, key, tls, "GET", "entertainment_configuration", null, ct).ConfigureAwait(false);
        var services = await ClipAsync(net, address, key, tls, "GET", "entertainment", null, ct).ConfigureAwait(false);
        // The bridge must say whether there is an area: guessing "none" would light every room.
        areas.EnsureOk();
        services.EnsureOk();
        try
        {
            using var areaDoc = JsonDocument.Parse(areas.Body);
            using var serviceDoc = JsonDocument.Parse(services.Body);

            var deviceOfService = new Dictionary<string, string>();
            foreach (var s in Items(serviceDoc.RootElement))
                if (Text(s, "id") is { } id && Child(s, "owner") is { } o && Text(o, "rid") is { } device) deviceOfService[id] = device;
            var lightOfDevice = new Dictionary<string, string>();
            foreach (var l in lights)
                if (l.Owner.Length > 0) lightOfDevice.TryAdd(l.Owner, l.Id);

            var all = Items(areaDoc.RootElement).ToList();
            if (all.Count == 0) return (places, false);
            hasArea = true;
            var area = all.FirstOrDefault(a => Text(a, "configuration_type") == "screen");
            if (area.ValueKind != JsonValueKind.Object) area = all[0];

            var sums = new Dictionary<string, (double X, double Y, int N)>();
            if (Child(area, "channels") is { ValueKind: JsonValueKind.Array } channels)
            {
                foreach (var channel in channels.EnumerateArray())
                {
                    if (Child(channel, "position") is not { } pos || Number(pos, "x") is not { } px || Number(pos, "z") is not { } pz) continue;
                    // UNVERIFIED (from memory, no bridge to check against): x runs left (-1) to right (+1), z is the
                    // height from the floor (-1) to the ceiling (+1), y is the depth and is not used.
                    var x = Math.Clamp((px + 1) / 2, 0, 1);
                    var y = Math.Clamp((1 - pz) / 2, 0, 1);
                    if (Child(channel, "members") is not { ValueKind: JsonValueKind.Array } members) continue;
                    foreach (var member in members.EnumerateArray())
                    {
                        if (Child(member, "service") is not { } service || Text(service, "rid") is not { } rid) continue;
                        if (!deviceOfService.TryGetValue(rid, out var device) || !lightOfDevice.TryGetValue(device, out var lightId)) continue;
                        var sum = sums.TryGetValue(lightId, out var had) ? had : (0, 0, 0);
                        sums[lightId] = (sum.X + x, sum.Y + y, sum.N + 1);
                    }
                }
            }
            foreach (var (id, sum) in sums) places[id] = (Math.Round(sum.X / sum.N, 4), Math.Round(sum.Y / sum.N, 4));
        }
        catch (JsonException)
        {
            // An answer that can't be read: which lights are in the area isn't known, so none is touched.
            throw new IOException("the bridge's list of entertainment areas couldn't be read");
        }
        return (places, hasArea);
    }

    // ---- direct control: the same CLIP v2 light commands, for the lights that follow (the entertainment area's)

    public LightControls Controls(PairedLight light) =>
        new(LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.Yes);

    // One Hue Bridge can run every light in a house: it is only changed from its own controls, never by "everything".
    public bool JoinsEverythingAtOnce => false;

    // A change is one command per light, and the bridge takes about ten a second.
    public TimeSpan ControlGap => TimeSpan.FromSeconds(1);

    /// <summary>The colour lights that follow: the entertainment area's (every colour light when the bridge has no area).</summary>
    private static async Task<List<HueLight>> FollowingAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        var key = light.RequireSecret("token");
        var tls = RuleFor(light);
        var lights = await ColourLightsAsync(net, light.Address, key, tls, ct).ConfigureAwait(false);
        if (lights.Count == 0) throw new InvalidOperationException(NoColourLightsMessage);
        var (places, hasArea) = await PlacesAsync(net, light.Address, key, tls, lights, ct).ConfigureAwait(false);
        if (!hasArea) return lights;
        var inArea = lights.Where(l => places.ContainsKey(l.Id)).ToList();
        return inArea.Count > 0 ? inArea : throw new InvalidOperationException(EmptyAreaMessage);
    }

    public async Task<LightReading> ReadAsync(ILightNetwork net, PairedLight light, CancellationToken ct) =>
        Reading(await FollowingAsync(net, light, ct).ConfigureAwait(false));

    /// <summary>
    /// The lights as one: on when any is on, the average brightness of those that are on, and their colour when they
    /// all show (about) the same one; null where they differ or show a white.
    /// </summary>
    internal static LightReading Reading(IReadOnlyList<HueLight> lights)
    {
        var on = lights.Where(l => l.On).ToList();
        if (on.Count == 0) return new LightReading(false, null, null);
        var brightness = (int)Math.Round(on.Average(l => Math.Clamp(l.Brightness, 0, 100)));
        int? colour = null;
        var first = on[0];
        if (on.All(l => l.Mirek is null && Math.Abs(l.X - first.X) < 0.01 && Math.Abs(l.Y - first.Y) < 0.01))
            colour = FromXy(first.X, first.Y);
        return new LightReading(true, brightness, colour);
    }

    public async Task<string> SaveAsync(ILightNetwork net, PairedLight light, CancellationToken ct) =>
        JsonSerializer.Serialize((await FollowingAsync(net, light, ct).ConfigureAwait(false))
            .Select(l => new SavedLight(l.Id, l.On, l.Brightness, l.X, l.Y, l.Mirek)).ToList());

    /// <summary>One light as it was before RigControl changed it.</summary>
    internal sealed record SavedLight(string Id, bool On, double Brightness, double X, double Y, int? Mirek);

    public async Task SetAsync(ILightNetwork net, PairedLight light, LightChange change, CancellationToken ct)
    {
        var key = light.RequireSecret("token");
        var tls = RuleFor(light);
        var lights = await FollowingAsync(net, light, ct).ConfigureAwait(false);
        var failed = 0;
        for (var i = 0; i < lights.Count; i++)
        {
            if (i > 0) await Wait(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            if (!await PutLightAsync(net, light.Address, key, tls, lights[i].Id, ChangeBody(change, lights[i].Gamut), Wait, ct).ConfigureAwait(false)) failed++;
        }
        if (failed > 0) throw new IOException(failed == lights.Count ? "the bridge stayed too busy to take it" : $"{failed} of {lights.Count} lights didn't take it");
    }

    public async Task GiveBackAsync(ILightNetwork net, PairedLight light, string saved, CancellationToken ct)
    {
        List<SavedLight>? was;
        try { was = JsonSerializer.Deserialize<List<SavedLight>>(saved); }
        catch (JsonException) { was = null; }
        if (was is null) throw new InvalidOperationException("What the Hue lights showed before couldn't be read.");
        var key = light.RequireSecret("token");
        var tls = RuleFor(light);
        var failed = 0;
        for (var i = 0; i < was.Count; i++)
        {
            if (i > 0) await Wait(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            var w = was[i];
            var body = RestoreBody(new HueLight(w.Id, "", "", w.On, w.Brightness, w.X, w.Y, w.Mirek, null));
            if (!await PutLightAsync(net, light.Address, key, tls, w.Id, body, Wait, ct).ConfigureAwait(false)) failed++;
        }
        if (failed > 0) throw new IOException($"{failed} of {was.Count} lights didn't take their old state back");
    }

    /// <summary>The command for one change: off, or on with a brightness (1-100) and a colour as Hue's x and y.</summary>
    internal static string ChangeBody(LightChange change, double[]? gamut)
    {
        if (change.TurnsOff) return "{\"on\":{\"on\":false}}";
        var parts = new List<string> { "\"on\":{\"on\":true}" };
        if (change.Brightness is { } b) parts.Add($"\"dimming\":{{\"brightness\":{N(Math.Clamp(b, 1, 100))}}}");
        if (change.Colour is { } c)
        {
            var (x, y) = ToXy(LightColour.Full(c), gamut);
            parts.Add($"\"color\":{{\"xy\":{{\"x\":{N(x)},\"y\":{N(y)}}}}}");
        }
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>One PUT to one light. True when it took it (or the light is gone); false when the bridge stayed too busy.</summary>
    private static async Task<bool> PutLightAsync(ILightNetwork net, string address, BoundSecret key, TlsRule tls, string id, string body,
        Func<TimeSpan, CancellationToken, Task> wait, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var reply = await ClipAsync(net, address, key, tls, "PUT", "light/" + Uri.EscapeDataString(id), body, ct).ConfigureAwait(false);
            if (reply.Ok) return true;
            if (reply.Status == 404) return true; // the light was removed from the bridge
            // Too many commands at once: give the bridge a moment and carry on.
            if (reply.Status is 429 or 503) { await wait(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); continue; }
            reply.EnsureOk();
        }
        return false;
    }

    /// <summary>Hue's x and y back to a screen colour at full strength (the inverse of <see cref="ToXy"/>).</summary>
    internal static int FromXy(double x, double y)
    {
        if (y <= 1e-6) return 0xFFFFFF;
        var bigY = 1.0;
        var bigX = x / y;
        var bigZ = (1 - x - y) / y;
        var r = bigX * 3.2404542 - bigY * 1.5371385 - bigZ * 0.4985314;
        var g = -bigX * 0.9692660 + bigY * 1.8760108 + bigZ * 0.0415560;
        var b = bigX * 0.0556434 - bigY * 0.2040259 + bigZ * 1.0572252;
        r = Math.Max(0, r);
        g = Math.Max(0, g);
        b = Math.Max(0, b);
        var max = Math.Max(r, Math.Max(g, b));
        if (max <= 1e-9) return 0xFFFFFF;
        static int Gamma(double c) => (int)Math.Round(Math.Clamp(c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055, 0, 1) * 255);
        return (Gamma(r / max) << 16) | (Gamma(g / max) << 8) | Gamma(b / max);
    }

    // ---- sending colours

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        var key = light.RequireSecret("token");
        var tls = RuleFor(light);
        // Read again, in the same order as when it was described; this is also the state to put back afterwards.
        var lights = await ColourLightsAsync(net, light.Address, key, tls, ct).ConfigureAwait(false);
        if (lights.Count == 0) throw new InvalidOperationException(NoColourLightsMessage);
        // A light was added or removed since it was described: the numbers no longer mean the same lights. Read again.
        if (description.Points.Any(p => p.Id < 0 || p.Id >= lights.Count))
            throw new IOException("the bridge's lights changed; reading them again");
        return new Session(net, light.Address, key, tls, lights, description, Wait);
    }

    private sealed class Session : ILightSession
    {
        private readonly ILightNetwork _net;
        private readonly string _address;
        private readonly BoundSecret _key;
        private readonly TlsRule _tls;
        private readonly List<HueLight> _lights;
        private readonly LightDescription _description;
        private readonly Func<TimeSpan, CancellationToken, Task> _wait;
        private readonly int?[] _sent;      // the colour each light was last told
        private readonly int _fadeMs;
        private bool _any;

        public Session(ILightNetwork net, string address, BoundSecret key, TlsRule tls, List<HueLight> lights,
            LightDescription description, Func<TimeSpan, CancellationToken, Task> wait)
        {
            _net = net;
            _address = address;
            _key = key;
            _tls = tls;
            _lights = lights;
            _description = description;
            _wait = wait;
            _sent = new int?[lights.Count];
            // Only the lights that follow count: each of them is one command a frame.
            MaxFramesPerSecond = Math.Clamp(Math.Min(5, BridgeCommandsPerSecond / Math.Max(1, description.Points.Count)), 0.1, 5);
            // The light fades to the new colour over one frame gap, so slow updates still look smooth.
            _fadeMs = (int)Math.Clamp(Math.Round(1000.0 / MaxFramesPerSecond / 100) * 100, 100, 2000);
        }

        // The whole bridge takes about ten commands a second, so more lights means fewer frames.
        public double MaxFramesPerSecond { get; }

        // A Hue light keeps the colour it was given; nothing needs repeating.
        public TimeSpan KeepAlive => TimeSpan.FromMinutes(5);

        // Putting back is one command a light, one after the other: a bridge with many lights needs longer than one lamp.
        public TimeSpan StopBudget => TimeSpan.FromMilliseconds(2000 + 300 * _sent.Count(s => s is not null));

        public async Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            for (var i = 0; i < colours.Count && i < _description.Points.Count; i++)
            {
                var index = _description.Points[i].Id;
                if (index < 0 || index >= _lights.Count) continue;
                var colour = colours[i] & 0xFFFFFF;
                if (_sent[index] is { } last && Close(last, colour)) continue;
                if (_any) await _wait(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
                _any = true;
                if (await PutAsync(_lights[index], ColourBody(colour, _lights[index].Gamut, _fadeMs), ct).ConfigureAwait(false))
                    _sent[index] = colour;
            }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            var touched = _sent.Count(s => s is not null);
            for (var i = 0; i < _lights.Count; i++)
            {
                if (_sent[i] is null) continue; // never touched: it is still as it was
                try
                {
                    await _wait(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
                    if (await PutAsync(_lights[i], RestoreBody(_lights[i]), ct).ConfigureAwait(false)) _sent[i] = null;
                }
                catch (OperationCanceledException)
                {
                    break; // out of time: said below, with how many are left
                }
                catch (Exception)
                {
                    // this one keeps its last colour; the others are still put back
                }
            }
            var left = _sent.Count(s => s is not null);
            if (left > 0)
                throw new IOException(left == 1 && touched == 1 ? "the light didn't take its old state back"
                    : $"{left} of {touched} lights didn't take their old state back");
        }

        /// <summary>True when the light took it. False when the bridge stayed too busy, or the light is gone.</summary>
        private async Task<bool> PutAsync(HueLight light, string body, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reply = await ClipAsync(_net, _address, _key, _tls, "PUT", "light/" + Uri.EscapeDataString(light.Id), body, ct).ConfigureAwait(false);
                if (reply.Ok) return true;
                if (reply.Status == 404) return true; // the light was removed from the bridge: nothing to colour
                // Too many commands at once: give the bridge a moment and carry on.
                if (reply.Status is 429 or 503) { await _wait(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); continue; }
                reply.EnsureOk();
            }
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Differences too small to see are not worth one of the bridge's ten commands a second.</summary>
    internal static bool Close(int a, int b)
    {
        if (IsDark(a) != IsDark(b)) return false;
        return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= 3
            && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= 3
            && Math.Abs((a & 255) - (b & 255)) <= 3;
    }

    private static bool IsDark(int rgb) => Math.Max((rgb >> 16) & 255, Math.Max((rgb >> 8) & 255, rgb & 255)) < 6;

    /// <summary>The command for one colour: on, as bright as its brightest channel, the colour as Hue's x and y. Black = off.</summary>
    internal static string ColourBody(int rgb, double[]? gamut, int fadeMs)
    {
        if (IsDark(rgb)) return "{\"on\":{\"on\":false}}";
        var brightest = Math.Max((rgb >> 16) & 255, Math.Max((rgb >> 8) & 255, rgb & 255));
        var brightness = Math.Clamp(Math.Round(brightest / 255.0 * 100, 1), 1, 100);
        var (x, y) = ToXy(rgb, gamut);
        return $"{{\"on\":{{\"on\":true}},\"dimming\":{{\"brightness\":{N(brightness)}}},\"color\":{{\"xy\":{{\"x\":{N(x)},\"y\":{N(y)}}}}},\"dynamics\":{{\"duration\":{fadeMs}}}}}";
    }

    /// <summary>The command that puts a light back as it was read before the sync.</summary>
    internal static string RestoreBody(HueLight was)
    {
        if (!was.On) return "{\"on\":{\"on\":false}}";
        var brightness = N(Math.Clamp(was.Brightness, 1, 100));
        // A light that was showing a white (it had a colour temperature) goes back to that white.
        return was.Mirek is { } mirek
            ? $"{{\"on\":{{\"on\":true}},\"dimming\":{{\"brightness\":{brightness}}},\"color_temperature\":{{\"mirek\":{mirek}}},\"dynamics\":{{\"duration\":400}}}}"
            : $"{{\"on\":{{\"on\":true}},\"dimming\":{{\"brightness\":{brightness}}},\"color\":{{\"xy\":{{\"x\":{N(was.X)},\"y\":{N(was.Y)}}}}},\"dynamics\":{{\"duration\":400}}}}";
    }

    private static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    // ---- colour

    private const double WhiteX = 0.3127, WhiteY = 0.329; // D65

    /// <summary>
    /// A screen colour (sRGB) as the x and y Hue uses: the gamma is undone, the light is turned into CIE XYZ (D65),
    /// and x = X / (X+Y+Z), y = Y / (X+Y+Z). A colour the light can't make is moved to the closest one it can.
    /// </summary>
    internal static (double X, double Y) ToXy(int rgb, double[]? gamut)
    {
        var r = Linear((rgb >> 16) & 255);
        var g = Linear((rgb >> 8) & 255);
        var b = Linear(rgb & 255);
        var bigX = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
        var bigY = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        var bigZ = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;
        var sum = bigX + bigY + bigZ;
        if (sum <= 1e-9) return (WhiteX, WhiteY);
        var x = bigX / sum;
        var y = bigY / sum;
        return gamut is { Length: 6 } ? IntoGamut(x, y, gamut) : (x, y);
    }

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>The point itself when it is inside the light's colour triangle, else the closest point on the triangle's edge.</summary>
    internal static (double X, double Y) IntoGamut(double x, double y, double[] gamut)
    {
        double rx = gamut[0], ry = gamut[1], gx = gamut[2], gy = gamut[3], bx = gamut[4], by = gamut[5];
        var d1 = Cross(x, y, rx, ry, gx, gy);
        var d2 = Cross(x, y, gx, gy, bx, by);
        var d3 = Cross(x, y, bx, by, rx, ry);
        var negative = d1 < 0 || d2 < 0 || d3 < 0;
        var positive = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(negative && positive)) return (x, y);

        var best = OnSegment(x, y, rx, ry, gx, gy);
        foreach (var candidate in new[] { OnSegment(x, y, gx, gy, bx, by), OnSegment(x, y, bx, by, rx, ry) })
            if (Distance(x, y, candidate) < Distance(x, y, best)) best = candidate;
        return best;
    }

    private static double Cross(double px, double py, double ax, double ay, double bx, double by) =>
        (px - bx) * (ay - by) - (ax - bx) * (py - by);

    private static (double X, double Y) OnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        var length = dx * dx + dy * dy;
        var t = length <= 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / length, 0, 1);
        return (ax + t * dx, ay + t * dy);
    }

    private static double Distance(double x, double y, (double X, double Y) p) => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);

    // ---- reading answers

    private static JsonElement? Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    private static string? Text(JsonElement e, string name) =>
        Child(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        Child(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;

    private static (double X, double Y)? Point(JsonElement e, string name) =>
        Child(e, name) is { } p && Number(p, "x") is { } x && Number(p, "y") is { } y ? (x, y) : null;

    /// <summary>The "data" list every answer of the version 2 API carries.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        Child(root, "data") is { ValueKind: JsonValueKind.Array } data
            ? data.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            : Enumerable.Empty<JsonElement>();
}
