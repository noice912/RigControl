using System.Text.Json;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// Nanoleaf panels and Lines through their local API, panel by panel. Tried on real Nanoleaf Lines.
/// Behaviour is what RigControl has always done for a paired Nanoleaf: found by trying port 16021 on the home
/// network, paired by holding the power button, about 10 frames a second, external control asked for again every
/// 20 s, and the effect that was on before comes back when the sync stops. Not placed on the layout = the whole
/// Nanoleaf follows the one overall colour while an effect plays.
/// Direct control (on/off, brightness, one colour, read back, give back) uses /state and /effects.
/// </summary>
public sealed class NanoleafProvider : INetworkLightProvider
{
    public string Brand => NetworkLightStore.NanoleafBrand;
    public string DisplayName => "Nanoleaf";
    public bool Tested => true;
    public string Notes => "Panels, Shapes and Lines. Every panel takes its own part of the picture. Tried on Nanoleaf Lines.";
    public double MaxFramesPerSecond => 10;
    public string PointName => "panel";
    // Owner (2026-11-25): an unplaced Nanoleaf follows the one colour during an effect, like the other brands.
    public bool FollowsWhenNotPlaced => true;
    // Tries every address of a small home network, which takes a while.
    public TimeSpan SearchTime => TimeSpan.FromSeconds(25);

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Press Search for lights and wait for your Nanoleaf to show up.",
            "Hold the Nanoleaf's power button for 5 to 7 seconds, until its lights flash.",
            "Press Pair within 30 seconds.",
        },
        ReturnsSecret: true,
        NothingFound: "No Nanoleaf answered on this network. Check it's on the same Wi-Fi or router as this PC, then search again.");

    public async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        var found = await NanoleafClient.DiscoverAsync(net, ct).ConfigureAwait(false);
        // A Nanoleaf only says who it is after pairing, so until then it is known by where it is.
        return found.Select(ip => new FoundLight("at " + ip, "Nanoleaf at " + ip, "", ip)).ToList();
    }

    public async Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        var token = await NanoleafClient.PairAsync(net, request.Address, ct).ConfigureAwait(false);
        using var client = new NanoleafClient(net, request.Address, BoundSecret.For(token, request.Address));
        var (name, serial) = await client.InfoAsync(ct).ConfigureAwait(false);
        return new PairResult(serial != "" ? serial : name, name, "", request.Address)
        {
            Secrets = new Dictionary<string, string> { ["token"] = token },
        };
    }

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        var panels = await client.PanelsAsync(ct).ConfigureAwait(false);
        return new LightDescription(LightKind.Points, panels.Select(p => new LightPoint(p.Id, p.X, p.Y, p.X1, p.Y1, p.X2, p.Y2)).ToList())
        {
            DrawAsBars = true,
        };
    }

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        try
        {
            // Its own effect, to put back afterwards.
            var before = await client.SelectedEffectAsync(ct).ConfigureAwait(false);
            return new Session(client, description, before);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---- direct control: Nanoleaf's OpenAPI /state and /effects (the same calls aionanoleaf and nanoleafapi make)

    public LightControls Controls(PairedLight light) =>
        new(LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.Yes);

    /// <summary>What GET /api/v1/{token}/ says: its state and the effect that is selected.</summary>
    internal sealed record NanoleafNow(bool? On, int? Brightness, string? Mode, int? Hue, int? Sat, int? Ct, string? Effect)
    {
        public static NanoleafNow Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement state = default;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object) state = s;
            bool? on = Value(state, "on") is { ValueKind: JsonValueKind.True or JsonValueKind.False } o ? o.GetBoolean() : null;
            string? mode = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("colorMode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string? effect = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("effects", out var e) && e.ValueKind == JsonValueKind.Object
                             && e.TryGetProperty("select", out var sel) && sel.ValueKind == JsonValueKind.String ? sel.GetString() : null;
            return new NanoleafNow(on, Number(state, "brightness"), mode, Number(state, "hue"), Number(state, "sat"), Number(state, "ct"), effect);
        }

        private static JsonElement? Value(JsonElement state, string name) =>
            state.ValueKind == JsonValueKind.Object && state.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("value", out var v) ? v : null;

        private static int? Number(JsonElement state, string name) =>
            Value(state, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? (int)Math.Round(d) : null;
    }

    public async Task<LightReading> ReadAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        var now = NanoleafNow.Parse(await client.AllInfoAsync(ct).ConfigureAwait(false));
        // A colour only when it shows one plain colour; an effect has many.
        int? colour = now.Mode == "hs" && now.Hue is { } h && now.Sat is { } s ? LightColour.FromHsv(h, s, 100) : null;
        return new LightReading(now.On, now.Brightness is { } b ? Math.Clamp(b, 0, 100) : null, colour);
    }

    public async Task<string> SaveAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        var now = NanoleafNow.Parse(await client.AllInfoAsync(ct).ConfigureAwait(false));
        return JsonSerializer.Serialize(now);
    }

    public async Task SetAsync(ILightNetwork net, PairedLight light, LightChange change, CancellationToken ct)
    {
        using var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        await client.SetStateAsync(StateBody(change), ct).ConfigureAwait(false);
    }

    /// <summary>One PUT /state body for a change: on, brightness (0-100), and a colour as hue (0-359) and saturation (0-100).</summary>
    internal static string StateBody(LightChange change)
    {
        if (change.TurnsOff) return "{\"on\":{\"value\":false}}";
        var parts = new List<string> { "\"on\":{\"value\":true}" };
        if (change.Brightness is { } b) parts.Add($"\"brightness\":{{\"value\":{Math.Clamp(b, 0, 100)}}}");
        if (change.Colour is { } c)
        {
            var (hue, sat, _) = LightColour.ToHsv(c);
            parts.Add($"\"hue\":{{\"value\":{hue}}}");
            parts.Add($"\"sat\":{{\"value\":{sat}}}");
        }
        return "{" + string.Join(",", parts) + "}";
    }

    public async Task GiveBackAsync(ILightNetwork net, PairedLight light, string saved, CancellationToken ct)
    {
        var was = JsonSerializer.Deserialize<NanoleafNow>(saved) ?? throw new InvalidOperationException("What the Nanoleaf showed before couldn't be read.");
        using var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        // Names starting with * are Nanoleaf's own temporary ones (*Solid*, *ExtControl*); they can't be picked again.
        if (was.Mode == "effect" && was.Effect is { Length: > 0 } effect && !effect.StartsWith("*", StringComparison.Ordinal))
            await client.SelectEffectCheckedAsync(effect, ct).ConfigureAwait(false);
        else if (was.Mode == "ct" && was.Ct is { } kelvin)
            await client.SetStateAsync($"{{\"ct\":{{\"value\":{kelvin}}}}}", ct).ConfigureAwait(false);
        else if (was.Hue is { } hue && was.Sat is { } sat)
            await client.SetStateAsync($"{{\"hue\":{{\"value\":{Math.Clamp(hue, 0, 359)}}},\"sat\":{{\"value\":{Math.Clamp(sat, 0, 100)}}}}}", ct).ConfigureAwait(false);
        if (was.Brightness is { } b)
            await client.SetStateAsync($"{{\"brightness\":{{\"value\":{Math.Clamp(b, 0, 100)}}}}}", ct).ConfigureAwait(false);
        // Last, so a light that was off stays off whatever the steps above did.
        if (was.On is { } on)
            await client.SetStateAsync(on ? "{\"on\":{\"value\":true}}" : "{\"on\":{\"value\":false}}", ct).ConfigureAwait(false);
    }

    private sealed class Session : ILightSession
    {
        private readonly NanoleafClient _client;
        private readonly LightDescription _description;
        private readonly string? _before;
        private DateTime _asked = DateTime.MinValue;

        public Session(NanoleafClient client, LightDescription description, string? before)
        {
            _client = client;
            _description = description;
            _before = before;
        }

        // Every frame is sent, changed or not, as before.
        public TimeSpan KeepAlive => TimeSpan.Zero;

        public async Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            // Asked again every 20 s, in case the Nanoleaf app or a hub switched it to something else.
            if ((DateTime.UtcNow - _asked).TotalSeconds >= 20)
            {
                await _client.StartStreamAsync(ct).ConfigureAwait(false);
                _asked = DateTime.UtcNow;
            }
            var frame = new List<(int, int)>(colours.Count);
            for (var i = 0; i < colours.Count && i < _description.Points.Count; i++) frame.Add((_description.Points[i].Id, colours[i]));
            _client.Send(frame, 1);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            // Names starting with * are Nanoleaf's own temporary ones (such as the stream itself); they can't be picked again.
            if (_before is { Length: > 0 } before && !before.StartsWith("*", StringComparison.Ordinal))
                await _client.SelectEffectAsync(before, ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
