using System.Text.Json;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network.Providers;

/// <summary>A made-up Hue Bridge at 172.16.0.80 that answers the way the open-source libraries describe a real one.</summary>
internal sealed class FakeHueBridge
{
    public const string Ip = "172.16.0.80";
    public const string BridgeId = "001788fffe4a5b6c";
    public const string AppKey = "app-key-made-up-for-tests";
    // Built in pieces: the public-export check refuses long runs of hex digits written out in one go.
    public static readonly string ClientKey = string.Concat(Enumerable.Repeat("AB12", 8));
    public static readonly string Cert = string.Concat(Enumerable.Repeat("C0FFEE11", 8));

    public sealed record Light(string Id, string Device, string Name, bool On = true, double Brightness = 60, double X = 0.4, double Y = 0.4,
        int? Mirek = null, bool Colour = true);

    public sealed record Channel(double X, double Y, double Z, params string[] Services);
    public sealed record Area(string Id, string Type, params Channel[] Channels);

    public List<Light> Lights { get; } = new();
    /// <summary>Entertainment service id -> device id.</summary>
    public Dictionary<string, string> Services { get; } = new();
    public List<Area> Areas { get; } = new();
    public bool ButtonPressed { get; set; }
    public bool HasBridgeId { get; set; } = true;
    public string Software { get; set; } = "1968096020";
    public bool KeyAccepted { get; set; } = true;
    /// <summary>Statuses to answer the next light commands with, before going back to 200.</summary>
    public Queue<int> NextPutStatuses { get; } = new();
    public List<(string Id, string Body)> Puts { get; } = new();
    public int PairTries { get; private set; }

    public FakeHueBridge(FakeLightNetwork net)
    {
        net.Mdns["_hue._tcp.local."] = new()
        {
            new MdnsService("Hue Bridge - 4A5B6C._hue._tcp.local.", "hue.local.", Ip, 443,
                new Dictionary<string, string> { ["bridgeid"] = BridgeId.ToUpperInvariant(), ["modelid"] = "BSB002" }),
        };
        net.OnHttp = Answer;
    }

    private HttpReply? Answer(HttpAsk ask)
    {
        if (ask.Url == $"http://{Ip}/api/config")
        {
            return FakeLightNetwork.Json(JsonSerializer.Serialize(HasBridgeId
                ? new Dictionary<string, string> { ["name"] = "Living room bridge", ["bridgeid"] = BridgeId.ToUpperInvariant(), ["modelid"] = "BSB002", ["swversion"] = Software, ["apiversion"] = "1.60.0" }
                : new Dictionary<string, string> { ["name"] = "An emulator", ["modelid"] = "BSB002", ["swversion"] = Software }));
        }
        if (!ask.Url.StartsWith($"https://{Ip}/", StringComparison.Ordinal)) return null;
        var path = ask.Url[$"https://{Ip}/".Length..];
        if (path == "api" && ask.Method == "POST")
        {
            PairTries++;
            var body = ButtonPressed
                ? $"[{{\"success\":{{\"username\":\"{AppKey}\",\"clientkey\":\"{ClientKey}\"}}}}]"
                : "[{\"error\":{\"type\":101,\"address\":\"\",\"description\":\"link button not pressed\"}}]";
            return FakeLightNetwork.Json(body) with { CertSha256 = Cert, CertCommonName = BridgeId };
        }
        const string root = "clip/v2/resource/";
        if (!path.StartsWith(root, StringComparison.Ordinal)) return FakeLightNetwork.Status(404);
        if (!KeyAccepted || ask.Headers is null || !ask.Headers.TryGetValue("hue-application-key", out var key) || key != AppKey)
            return FakeLightNetwork.Status(403);
        var resource = path[root.Length..];
        if (ask.Method == "GET") return resource switch
        {
            "light" => Data(Lights.Select(LightJson)),
            "entertainment" => Data(Services.Select(s => (object)new { id = s.Key, owner = new { rid = s.Value, rtype = "device" } })),
            "entertainment_configuration" => Data(Areas.Select(a => (object)new
            {
                id = a.Id,
                metadata = new { name = a.Id },
                configuration_type = a.Type,
                channels = a.Channels.Select((c, i) => new
                {
                    channel_id = i,
                    position = new { x = c.X, y = c.Y, z = c.Z },
                    members = c.Services.Select(s => new { service = new { rid = s, rtype = "entertainment" }, index = 0 }),
                }),
            })),
            _ => FakeLightNetwork.Status(404),
        };
        if (ask.Method == "PUT" && resource.StartsWith("light/", StringComparison.Ordinal))
        {
            if (NextPutStatuses.TryDequeue(out var status)) return FakeLightNetwork.Status(status);
            Puts.Add((resource["light/".Length..], ask.Body ?? ""));
            return FakeLightNetwork.Json("{\"errors\":[],\"data\":[]}");
        }
        return FakeLightNetwork.Status(404);
    }

    private static HttpReply Data(IEnumerable<object> items) =>
        FakeLightNetwork.Json(JsonSerializer.Serialize(new { errors = Array.Empty<object>(), data = items }));

    private static object LightJson(Light l)
    {
        var o = new Dictionary<string, object?>
        {
            ["id"] = l.Id,
            ["owner"] = new { rid = l.Device, rtype = "device" },
            ["metadata"] = new { name = l.Name },
            ["on"] = new { on = l.On },
            ["dimming"] = new { brightness = l.Brightness },
            ["color_temperature"] = new { mirek = l.Mirek },
        };
        if (l.Colour)
        {
            o["color"] = new
            {
                xy = new { x = l.X, y = l.Y },
                // Hue's widest colour triangle ("gamut C").
                gamut = new { red = new { x = 0.6915, y = 0.3083 }, green = new { x = 0.17, y = 0.7 }, blue = new { x = 0.1532, y = 0.0475 } },
            };
        }
        return o;
    }
}

public class HueProviderTests
{
    private readonly FakeLightNetwork _net = new();
    private readonly List<TimeSpan> _waits = new();
    private readonly HueProvider _provider;

    public HueProviderTests()
    {
        _provider = new HueProvider
        {
            Wait = (time, _) =>
            {
                _waits.Add(time);
                return Task.CompletedTask;
            },
        };
    }

    private static PairedLight Paired(string address = FakeHueBridge.Ip, bool withCert = true)
    {
        var data = new Dictionary<string, string> { ["bridgeid"] = FakeHueBridge.BridgeId };
        if (withCert) data["cert"] = FakeHueBridge.Cert;
        return new PairedLight("hue", FakeHueBridge.BridgeId, "Living room bridge", "BSB002", address, "net|hue|" + FakeHueBridge.BridgeId, data,
            new Dictionary<string, string> { ["token"] = FakeHueBridge.AppKey, ["clientkey"] = FakeHueBridge.ClientKey });
    }

    private static PairRequest Request(string address = FakeHueBridge.Ip) => new(address, null, new Dictionary<string, string>());

    private FakeHueBridge BridgeWithTwoLights()
    {
        var bridge = new FakeHueBridge(_net);
        // Given out of order on purpose: the provider keeps them sorted by id.
        bridge.Lights.Add(new("light-b", "device-b", "Lamp", On: false));
        bridge.Lights.Add(new("light-a", "device-a", "Strip", On: true, Brightness: 42.5, X: 0.21, Y: 0.31));
        return bridge;
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task A_bridge_is_found_by_its_bonjour_name_and_confirmed_by_asking_who_it_is()
    {
        _ = new FakeHueBridge(_net);
        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));
        Assert.Equal((FakeHueBridge.BridgeId, "Living room bridge", "BSB002", FakeHueBridge.Ip), (found.Id, found.Name, found.Model, found.Address));
        Assert.Equal("_hue._tcp.local.", Assert.Single(_net.Browsed));
        // Asked without any key, and nothing but the bridge was asked.
        var ask = Assert.Single(_net.Asks);
        Assert.Equal(("GET", "http://172.16.0.80/api/config"), (ask.Method, ask.Url));
        Assert.Null(ask.Headers);
        Assert.Empty(_net.Sent);
        Assert.Empty(_net.TcpTried);
    }

    [Fact]
    public async Task Something_that_only_pretends_to_be_a_bridge_is_dropped()
    {
        _ = new FakeHueBridge(_net) { HasBridgeId = false };
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
    }

    [Fact]
    public async Task A_bridge_announced_at_an_address_outside_the_home_network_is_never_asked()
    {
        _ = new FakeHueBridge(_net);
        _net.Mdns["_hue._tcp.local."] = new() { new MdnsService("x", "x.local.", "8.8.8.8", 443, new Dictionary<string, string>()) };
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Empty(_net.Asks);
    }

    [Fact]
    public async Task Pairing_keeps_asking_while_the_button_is_not_pressed_then_says_what_to_do()
    {
        var bridge = new FakeHueBridge(_net);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request(), default));
        Assert.Equal("Press the round button on top of the Hue Bridge, then press Pair within 30 seconds.", ex.Message);
        Assert.Equal(30, bridge.PairTries);
        Assert.Equal(29, _waits.Count);
        Assert.All(_waits, w => Assert.Equal(TimeSpan.FromSeconds(1), w));
    }

    [Fact]
    public async Task Pairing_succeeds_once_the_button_is_pressed_and_keeps_both_keys_and_the_certificate()
    {
        var bridge = new FakeHueBridge(_net);
        _provider.Wait = (_, _) =>
        {
            bridge.ButtonPressed = true; // pressed while RigControl waits
            return Task.CompletedTask;
        };
        var r = await _provider.PairAsync(_net, Request(), default);
        Assert.Equal(2, bridge.PairTries);
        Assert.Equal((FakeHueBridge.BridgeId, "Living room bridge", "BSB002", FakeHueBridge.Ip), (r.Id, r.Name, r.Model, r.Address));
        Assert.Equal(FakeHueBridge.AppKey, r.Secrets["token"]);
        Assert.Equal(FakeHueBridge.ClientKey, r.Secrets["clientkey"]);
        Assert.Equal(FakeHueBridge.BridgeId, r.Data["bridgeid"]);
        Assert.Equal(FakeHueBridge.Cert, r.Data["cert"]);
        // The keys are nowhere but in Secrets.
        Assert.DoesNotContain(r.Data.Values, v => v.Contains(FakeHueBridge.AppKey) || v.Contains(FakeHueBridge.ClientKey));

        var post = _net.Asks.First(a => a.Method == "POST");
        Assert.Equal("https://172.16.0.80/api", post.Url);
        Assert.Equal("{\"devicetype\":\"rigcontrol#pc\",\"generateclientkey\":true}", post.Body);
        Assert.Null(post.Headers);
        // First meeting: the certificate must carry the bridge's id; no fingerprint is known yet.
        Assert.Equal(new TlsRule(FakeHueBridge.BridgeId, null), post.Tls);
    }

    [Fact]
    public async Task A_bridge_with_software_too_old_for_the_new_api_is_refused_in_plain_words()
    {
        var bridge = new FakeHueBridge(_net) { Software = "1935144040", ButtonPressed = true };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request(), default));
        Assert.Equal("This Hue Bridge's software is too old for RigControl. Update it in the Hue app, then pair it again.", ex.Message);
        Assert.Equal(0, bridge.PairTries);
    }

    [Fact]
    public async Task Pairing_with_an_address_that_is_not_a_bridge_says_so()
    {
        _ = new FakeHueBridge(_net);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request("172.16.0.81"), default));
        Assert.Equal("There is no Hue Bridge at 172.16.0.81. Check the address, or press Search for lights.", ex.Message);
        Assert.DoesNotContain(_net.Asks, a => a.Method == "POST");
    }

    [Fact]
    public async Task Every_secure_request_names_the_bridge_and_after_pairing_its_remembered_certificate()
    {
        var bridge = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00 }, default);
        await session.StopAsync(default);

        var secure = _net.Asks.Where(a => a.Url.StartsWith("https://", StringComparison.Ordinal)).ToList();
        Assert.True(secure.Count >= 8);
        Assert.All(secure, a => Assert.Equal(new TlsRule(FakeHueBridge.BridgeId, FakeHueBridge.Cert), a.Tls));
        Assert.Equal(4, bridge.Puts.Count);
    }

    [Fact]
    public async Task A_pairing_without_a_remembered_certificate_is_not_used()
    {
        _ = BridgeWithTwoLights();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(withCert: false), default));
        Assert.Equal("The Hue Bridge's pairing is incomplete. Press Forget, then pair it again.", ex.Message);
        Assert.Empty(_net.Asks);
    }

    [Fact]
    public async Task The_key_only_ever_goes_to_the_paired_address()
    {
        _ = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new[] { 0xFF0000, 0x0000FF }, default);
        var withKey = _net.Asks.Where(a => a.Headers?.Values.Any(v => v.Contains(FakeHueBridge.AppKey)) == true).ToList();
        Assert.NotEmpty(withKey);
        Assert.All(withKey, a => Assert.StartsWith("https://172.16.0.80/clip/v2/resource/", a.Url));
        // Never in the address or the body, and the streaming key is not sent at all.
        Assert.DoesNotContain(_net.Asks, a => a.Url.Contains(FakeHueBridge.AppKey) || (a.Body?.Contains(FakeHueBridge.AppKey) ?? false));
        Assert.DoesNotContain(_net.Asks, a => (a.Body?.Contains(FakeHueBridge.ClientKey) ?? false)
            || (a.Headers?.Values.Any(v => v.Contains(FakeHueBridge.ClientKey)) ?? false));

        // A call pointed at another address with this bridge's key is stopped before anything is sent.
        var before = _net.Asks.Count;
        var key = Paired().RequireSecret("token");
        var rule = new TlsRule(FakeHueBridge.BridgeId, FakeHueBridge.Cert);
        await Assert.ThrowsAsync<SecretRuleException>(() => HueProvider.ClipAsync(_net, "172.16.0.99", key, rule, "GET", "light", null, default));
        await Assert.ThrowsAsync<SecretRuleException>(() => HueProvider.ClipAsync(_net, "172.16.0.99", key, rule, "PUT", "light/light-a", "{}", default));
        Assert.Equal(before, _net.Asks.Count);
    }

    [Fact]
    public async Task Lights_are_placed_from_the_screen_entertainment_area()
    {
        var bridge = BridgeWithTwoLights();
        bridge.Lights.Add(new("light-c", "device-c", "White bulb", Colour: false)); // no colour: left alone
        bridge.Services["ent-a"] = "device-a";
        bridge.Services["ent-b"] = "device-b";
        bridge.Areas.Add(new("music-area", "music", new FakeHueBridge.Channel(0, 0, 0, "ent-a")));
        bridge.Areas.Add(new("tv-area", "screen",
            // light-a is a strip with two channels: it sits in the middle of them.
            new FakeHueBridge.Channel(-1, 0.3, 1, "ent-a"),
            new FakeHueBridge.Channel(0, 0.3, 1, "ent-a"),
            new FakeHueBridge.Channel(0.5, -0.7, -1, "ent-b")));

        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal(LightKind.Points, d.Kind);
        Assert.Null(d.Note);
        Assert.Equal(new[] { 0, 1 }, d.Points.Select(p => p.Id));
        Assert.Equal((0.25, 0.0), (d.Points[0].X, d.Points[0].Y));  // light-a: left of the middle, at the top
        Assert.Equal((0.75, 1.0), (d.Points[1].X, d.Points[1].Y));  // light-b: right, at the bottom
    }

    [Fact]
    public async Task Only_the_lights_in_the_entertainment_area_follow_and_the_rest_of_the_house_is_left_alone()
    {
        var bridge = BridgeWithTwoLights();
        bridge.Lights.Add(new("light-d", "device-d", "Bedroom spot", On: false));
        bridge.Services["ent-b"] = "device-b";
        bridge.Areas.Add(new("tv-area", "screen", new FakeHueBridge.Channel(1, 0, 0, "ent-b")));

        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal("2 colour lights are outside the entertainment area and are left alone. Add them to the area in the Hue app to have them follow.", d.Note);
        var point = Assert.Single(d.Points);
        Assert.Equal((1, 1.0, 0.5), (point.Id, point.X, point.Y)); // light-b, the second light in the bridge's list

        // The one colour that light gets reaches light-b and no other light, and only light-b is put back.
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        Assert.Equal(5, session.MaxFramesPerSecond); // one light follows, so the bridge's ten commands a second are not shared
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.StopAsync(default);
        Assert.All(bridge.Puts, p => Assert.Equal("light-b", p.Id));
        Assert.Equal(2, bridge.Puts.Count);
    }

    [Fact]
    public async Task A_bridge_with_no_entertainment_area_follows_with_every_colour_light_and_says_so()
    {
        var bridge = BridgeWithTwoLights();
        bridge.Lights.Add(new("light-d", "device-d", "Spot"));

        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal(HueProvider.NoAreaNote, d.Note);
        Assert.Contains("every colour light it has follows, in every room", d.Note);
        Assert.Equal(new[] { 0, 1, 2 }, d.Points.Select(p => p.Id));
        Assert.Equal((0.25, 0.95), (d.Points[0].X, d.Points[0].Y)); // where they are isn't known: a row along the bottom
        Assert.Equal((0.75, 0.95), (d.Points[2].X, d.Points[2].Y));
    }

    [Fact]
    public async Task An_entertainment_area_without_colour_lights_says_so_instead_of_lighting_every_room()
    {
        var bridge = BridgeWithTwoLights();
        bridge.Services["ent-x"] = "device-of-a-white-bulb";
        bridge.Areas.Add(new("tv-area", "screen", new FakeHueBridge.Channel(0, 0, 0, "ent-x")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(), default));
        Assert.Equal("The entertainment area on this Hue Bridge has no colour lights in it. Add the lights that should follow to it in the Hue app.", ex.Message);
    }

    [Fact]
    public async Task Putting_many_lights_back_gets_more_time_and_lights_that_were_not_put_back_are_counted()
    {
        var bridge = new FakeHueBridge(_net);
        for (var i = 10; i < 40; i++) bridge.Lights.Add(new($"light-{i}", $"device-{i}", "Light"));
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        Assert.Equal(TimeSpan.FromSeconds(2), session.StopBudget); // nothing touched yet
        await session.SendAsync(Enumerable.Repeat(0xFF0000, 30).ToArray(), default);
        Assert.Equal(TimeSpan.FromSeconds(11), session.StopBudget); // 30 lights, one command each

        // The bridge stays too busy for the first two lights: they are counted, the other 28 are still put back.
        for (var i = 0; i < 6; i++) bridge.NextPutStatuses.Enqueue(429);
        bridge.Puts.Clear();
        var ex = await Assert.ThrowsAsync<IOException>(() => session.StopAsync(default));
        Assert.Equal("2 of 30 lights didn't take their old state back", ex.Message);
        Assert.Equal(28, bridge.Puts.Count);
    }

    [Fact]
    public async Task A_stop_that_runs_out_of_time_says_how_many_lights_are_left()
    {
        var bridge = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00 }, default);
        using var over = new CancellationTokenSource();
        over.Cancel();
        var ex = await Assert.ThrowsAsync<IOException>(() => session.StopAsync(over.Token));
        Assert.Equal("2 of 2 lights didn't take their old state back", ex.Message);
    }

    [Fact]
    public async Task A_bridge_name_with_line_breaks_or_endless_length_is_tidied()
    {
        _net.OnHttp = ask => ask.Url == "http://172.16.0.80/api/config"
            ? FakeLightNetwork.Json(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["name"] = "Bridge\r\n2026-11-24 21:00:00 [info] Helper programs installed" + new string('x', 5000),
                ["bridgeid"] = FakeHueBridge.BridgeId, ["modelid"] = new string('M', 900), ["swversion"] = "1968096020",
            }))
            : null;
        var config = await HueProvider.ConfigAsync(_net, FakeHueBridge.Ip, null, TimeSpan.FromSeconds(1), default);
        Assert.NotNull(config);
        Assert.DoesNotContain(config!.Name, c => char.IsControl(c));
        Assert.True(config.Name.Length <= 64 && config.Model.Length <= 40);
    }

    [Fact]
    public async Task Bridges_announced_on_a_network_that_was_not_searched_are_never_asked_and_at_most_64_are()
    {
        _net.Networks.Add(new LocalNetwork("10.0.0.2", 24, false)); // a VPN adapter
        _net.Mdns["_hue._tcp.local."] = new[] { "10.0.0.1", "192.168.1.100" }
            .Concat(Enumerable.Range(1, 200).Select(i => "172.16.0." + i))
            .Select(a => new MdnsService("x", "x.local.", a, 443, new Dictionary<string, string>())).ToList();
        _net.OnHttp = _ => null;
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Equal(64, _net.Asks.Count);
        Assert.All(_net.Asks, a => Assert.StartsWith("http://172.16.0.", a.Url));
    }

    [Fact]
    public async Task A_bridge_without_colour_lights_says_so()
    {
        var bridge = new FakeHueBridge(_net);
        bridge.Lights.Add(new("light-c", "device-c", "White bulb", Colour: false));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(), default));
        Assert.Equal("This Hue Bridge has no colour lights, so there is nothing for RigControl to colour.", ex.Message);
    }

    [Fact]
    public async Task A_red_frame_turns_the_light_on_at_full_brightness_with_reds_x_and_y()
    {
        var bridge = new FakeHueBridge(_net);
        bridge.Lights.Add(new("light-a", "device-a", "Strip"));
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        Assert.Empty(bridge.Puts); // starting changes nothing

        await session.SendAsync(new[] { 0xFF0000 }, default);
        var put = Assert.Single(bridge.Puts);
        Assert.Equal("light-a", put.Id);
        Assert.Equal("{\"on\":{\"on\":true},\"dimming\":{\"brightness\":100},\"color\":{\"xy\":{\"x\":0.64,\"y\":0.33}},\"dynamics\":{\"duration\":200}}", put.Body);
        var body = Json(put.Body);
        Assert.Equal(0.64, body.GetProperty("color").GetProperty("xy").GetProperty("x").GetDouble(), 3);
        Assert.Equal(0.33, body.GetProperty("color").GetProperty("xy").GetProperty("y").GetDouble(), 3);
        var ask = _net.Asks.Last();
        Assert.Equal(("PUT", "https://172.16.0.80/clip/v2/resource/light/light-a"), (ask.Method, ask.Url));

        // Half as bright a red: same colour, half the brightness.
        await session.SendAsync(new[] { 0x800000 }, default);
        var dim = Json(bridge.Puts[1].Body);
        Assert.Equal(50.2, dim.GetProperty("dimming").GetProperty("brightness").GetDouble(), 1);
        Assert.Equal(0.64, dim.GetProperty("color").GetProperty("xy").GetProperty("x").GetDouble(), 3);
    }

    [Fact]
    public async Task Black_turns_the_light_off()
    {
        var bridge = new FakeHueBridge(_net);
        bridge.Lights.Add(new("light-a", "device-a", "Strip"));
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new[] { 0x020101 }, default);
        Assert.Equal("{\"on\":{\"on\":false}}", Assert.Single(bridge.Puts).Body);
    }

    [Fact]
    public async Task Only_lights_whose_colour_really_changed_are_sent_again()
    {
        var bridge = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00 }, default);
        Assert.Equal(new[] { "light-a", "light-b" }, bridge.Puts.Select(p => p.Id));

        await session.SendAsync(new[] { 0xFF0000, 0x00FF00 }, default);   // the same
        await session.SendAsync(new[] { 0xFD0201, 0x00FF00 }, default);   // too small to see
        Assert.Equal(2, bridge.Puts.Count);

        await session.SendAsync(new[] { 0xFF0000, 0x0000FF }, default);   // the second light turns blue
        Assert.Equal(3, bridge.Puts.Count);
        Assert.Equal("light-b", bridge.Puts[2].Id);
    }

    [Fact]
    public async Task More_lights_means_fewer_frames_a_second_and_a_longer_fade()
    {
        Assert.Equal(5, _provider.MaxFramesPerSecond);
        var bridge = new FakeHueBridge(_net);
        bridge.Lights.Add(new("light-01", "device-01", "One"));
        var one = await _provider.StartAsync(_net, Paired(), await _provider.DescribeAsync(_net, Paired(), default), default);
        Assert.Equal(5, one.MaxFramesPerSecond);
        Assert.Equal(TimeSpan.FromMinutes(5), one.KeepAlive); // a Hue light keeps its colour

        for (var i = 2; i <= 8; i++) bridge.Lights.Add(new($"light-0{i}", $"device-0{i}", "More"));
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        var eight = await _provider.StartAsync(_net, Paired(), d, default);
        Assert.Equal(1.25, eight.MaxFramesPerSecond);
        await eight.SendAsync(Enumerable.Repeat(0xFFFFFF, 8).ToArray(), default);
        Assert.Equal(8, bridge.Puts.Count);
        Assert.All(bridge.Puts, p => Assert.Equal(800, Json(p.Body).GetProperty("dynamics").GetProperty("duration").GetInt32()));
    }

    [Fact]
    public async Task Stopping_puts_back_each_lights_saved_state()
    {
        var bridge = BridgeWithTwoLights();
        bridge.Lights.Add(new("light-w", "device-w", "Reading lamp", On: true, Brightness: 80, Mirek: 366));
        bridge.Lights.Add(new("light-z", "device-z", "Untouched", On: true));
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        // a, b and w get a colour; z's part of the picture is black, so it is turned off.
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00, 0x0000FF, 0x000000 }, default);
        bridge.Puts.Clear();

        await session.StopAsync(default);
        Assert.Equal(new[] { "light-a", "light-b", "light-w", "light-z" }, bridge.Puts.Select(p => p.Id));
        Assert.Equal("{\"on\":{\"on\":true},\"dimming\":{\"brightness\":42.5},\"color\":{\"xy\":{\"x\":0.21,\"y\":0.31}},\"dynamics\":{\"duration\":400}}", bridge.Puts[0].Body);
        Assert.Equal("{\"on\":{\"on\":false}}", bridge.Puts[1].Body); // it was off: off again
        Assert.Equal("{\"on\":{\"on\":true},\"dimming\":{\"brightness\":80},\"color_temperature\":{\"mirek\":366},\"dynamics\":{\"duration\":400}}", bridge.Puts[2].Body); // it showed a white
        Assert.StartsWith("{\"on\":{\"on\":true}", bridge.Puts[3].Body);
    }

    [Fact]
    public async Task A_light_that_was_never_sent_anything_is_not_touched_when_stopping()
    {
        var bridge = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new[] { 0xFF0000 }, default); // only the first light
        bridge.Puts.Clear();
        await session.StopAsync(default);
        Assert.Equal("light-a", Assert.Single(bridge.Puts).Id);
    }

    [Fact]
    public async Task A_busy_bridge_is_given_a_moment_and_the_session_carries_on()
    {
        var bridge = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        bridge.NextPutStatuses.Enqueue(429);
        bridge.NextPutStatuses.Enqueue(503);
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00 }, default);
        Assert.Equal(new[] { "light-a", "light-b" }, bridge.Puts.Select(p => p.Id));
        Assert.Equal(2, _waits.Count(w => w == TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_bridge_that_no_longer_knows_the_key_asks_to_pair_again()
    {
        var bridge = BridgeWithTwoLights();
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        bridge.KeyAccepted = false; // RigControl was removed in the Hue app
        const string message = "The Hue Bridge no longer accepts RigControl. Press Forget, then pair it again.";
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(new[] { 0xFF0000, 0 }, default))).Message);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(), default))).Message);
    }

    [Fact]
    public async Task A_pairing_that_cannot_be_read_on_this_account_asks_to_pair_again()
    {
        var unreadable = new PairedLight("hue", FakeHueBridge.BridgeId, "Bridge", "", FakeHueBridge.Ip, "net|hue|" + FakeHueBridge.BridgeId,
            new Dictionary<string, string> { ["bridgeid"] = FakeHueBridge.BridgeId, ["cert"] = FakeHueBridge.Cert });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, unreadable, default));
        Assert.Equal("Its pairing can't be read on this Windows account; pair it again.", ex.Message);
        Assert.Empty(_net.Asks);
    }

    [Fact]
    public void A_colour_the_light_cannot_make_becomes_the_closest_one_it_can()
    {
        // A simple triangle: red at (1,0), green at (0,1), blue at (0,0).
        var gamut = new[] { 1.0, 0, 0, 1, 0, 0 };
        var inside = HueProvider.IntoGamut(0.2, 0.3, gamut);
        Assert.Equal((0.2, 0.3), inside);
        var (x, y) = HueProvider.IntoGamut(1, 1, gamut);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);
        var corner = HueProvider.IntoGamut(-1, -1, gamut);
        Assert.Equal((0.0, 0.0), corner);

        // White is the same white with or without a triangle; pure blue of a screen is inside Hue's widest one.
        var white = HueProvider.ToXy(0xFFFFFF, null);
        Assert.Equal(0.3127, white.X, 3);
        Assert.Equal(0.3290, white.Y, 3);
        var blue = HueProvider.ToXy(0x0000FF, null);
        Assert.Equal(0.15, blue.X, 3);
        Assert.Equal(0.06, blue.Y, 3);
    }

    [Fact]
    public void It_is_marked_as_not_yet_tried_on_a_real_bridge()
    {
        Assert.Equal("hue", _provider.Brand);
        Assert.Equal("Philips Hue", _provider.DisplayName);
        Assert.False(_provider.Tested);
        Assert.Contains("not yet tried on a real bridge", _provider.Notes);
        Assert.Contains("open-source descriptions of the protocol", _provider.Notes);
        Assert.Contains("one colour a few times a second", _provider.Notes);
        Assert.Contains("Entertainment stream, which is not built yet", _provider.Notes);
        Assert.True(_provider.Pairing.ReturnsSecret);
        Assert.True(_provider.Pairing.ManualAddress);
        Assert.Equal(5, _provider.Pairing.Steps.Count);
        Assert.Equal("light", _provider.PointName);
        // A bridge can hold a whole house of lights: pairing alone touches none of them, and the page says who follows.
        Assert.False(_provider.FollowsWhenNotPlaced);
        Assert.Contains("Effects leave it alone until you place the bridge on the layout", _provider.Notes);
        Assert.Contains("entertainment area", _provider.Notes);
        Assert.Contains("switched on", _provider.Notes);
        Assert.Contains("every colour light on the bridge, in every room", _provider.Notes);
        Assert.Contains(_provider.Pairing.Steps, s => s.Contains("Until then no effect touches a Hue light"));
        Assert.Contains(NetworkLights.BuiltIn(), p => p is HueProvider);
    }
}
