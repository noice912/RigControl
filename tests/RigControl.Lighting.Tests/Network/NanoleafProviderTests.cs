using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network;

/// <summary>A made-up Nanoleaf at 172.16.0.70 that answers the way a real one does.</summary>
internal sealed class FakeNanoleaf
{
    public const string Ip = "172.16.0.70";
    public const string Token = "TOKabc123";
    private const string Root = "http://172.16.0.70:16021/api/v1/";

    public bool ReadyToPair { get; set; } = true;
    public string Effect { get; set; } = "Northern Lights";
    /// <summary>The name the user gave it in the Nanoleaf app.</summary>
    public string Name { get; set; } = "Lines";
    public bool Answers { get; set; } = true;

    public const string Layout = """
        {"numPanels":3,"sideLength":0,"positionData":[
          {"panelId":11,"x":0,"y":0,"o":0,"shapeType":2},
          {"panelId":22,"x":200,"y":0,"o":0,"shapeType":2},
          {"panelId":33,"x":100,"y":100,"o":0,"shapeType":2}]}
        """;

    public FakeNanoleaf(FakeLightNetwork net)
    {
        net.OnTcp = (host, port) => Answers && host == Ip && port == 16021 ? new FakeTcpLink(host) : null;
        net.OnHttp = ask =>
        {
            if (!Answers || !ask.Url.StartsWith(Root, StringComparison.Ordinal)) return null;
            var path = ask.Url[Root.Length..];
            if (path == "") return FakeLightNetwork.Status(401);
            if (path == "new") return ReadyToPair ? FakeLightNetwork.Json($"{{\"auth_token\":\"{Token}\"}}") : FakeLightNetwork.Status(403);
            if (!path.StartsWith(Token + "/", StringComparison.Ordinal)) return FakeLightNetwork.Status(401);
            return (ask.Method, path[(Token.Length + 1)..]) switch
            {
                ("GET", "") => FakeLightNetwork.Json("{\"name\":" + JsonSerializer.Serialize(Name) + ",\"serialNo\":\"S1\"}"),
                ("GET", "panelLayout/layout") => FakeLightNetwork.Json(Layout),
                ("GET", "panelLayout/globalOrientation") => FakeLightNetwork.Json("{\"value\":0,\"max\":360,\"min\":0}"),
                ("GET", "effects/select") => FakeLightNetwork.Json(JsonSerializer.Serialize(Effect)),
                ("PUT", "state") => FakeLightNetwork.Status(204),
                ("PUT", "effects") => FakeLightNetwork.Status(204),
                _ => FakeLightNetwork.Status(404),
            };
        };
    }
}

public class NanoleafProviderTests
{
    private readonly FakeLightNetwork _net = new();
    private readonly NanoleafProvider _provider = new();

    private static PairedLight Paired(string address = FakeNanoleaf.Ip) =>
        new("nanoleaf", "S1", "Lines", "", address, "nanoleaf|Lines|S1", null, new Dictionary<string, string> { ["token"] = FakeNanoleaf.Token });

    [Fact]
    public async Task It_is_found_by_trying_its_port_on_home_networks_only()
    {
        _net.Networks.Add(new LocalNetwork("10.0.0.2", 24, false)); // a VPN adapter: no router, never searched
        _ = new FakeNanoleaf(_net);
        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));
        Assert.Equal(FakeNanoleaf.Ip, found.Address);
        Assert.Equal(253, _net.TcpTried.Count);
        Assert.All(_net.TcpTried, t => Assert.StartsWith("172.16.0.", t.Host));
        Assert.All(_net.TcpTried, t => Assert.Equal(16021, t.Port));
        // Only the one that answered on the port is asked anything, and without any token.
        var ask = Assert.Single(_net.Asks);
        Assert.Equal("http://172.16.0.70:16021/api/v1/", ask.Url);
    }

    [Fact]
    public async Task Pairing_before_the_button_was_held_says_what_to_do()
    {
        var nl = new FakeNanoleaf(_net) { ReadyToPair = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.PairAsync(_net, new PairRequest(FakeNanoleaf.Ip, null, new Dictionary<string, string>()), default));
        Assert.Contains("Hold its power button for 5 to 7 seconds", ex.Message);
    }

    [Fact]
    public async Task Pairing_gives_the_token_name_and_serial_and_the_panels_can_be_read()
    {
        _ = new FakeNanoleaf(_net);
        var r = await _provider.PairAsync(_net, new PairRequest(FakeNanoleaf.Ip, null, new Dictionary<string, string>()), default);
        Assert.Equal(("S1", "Lines", FakeNanoleaf.Ip), (r.Id, r.Name, r.Address));
        Assert.Equal(FakeNanoleaf.Token, r.Secrets["token"]);
        Assert.Equal("POST", _net.Asks.First().Method);
        Assert.Equal("http://172.16.0.70:16021/api/v1/new", _net.Asks.First().Url);

        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal(LightKind.Points, d.Kind);
        Assert.True(d.DrawAsBars);
        Assert.Equal(new[] { 11, 22, 33 }, d.Points.Select(p => p.Id));
        Assert.Equal((0.5, 0.0), (d.Points[2].X, d.Points[2].Y)); // the highest panel is at the top of the picture
    }

    [Fact]
    public async Task The_token_only_ever_goes_to_the_paired_address()
    {
        _ = new FakeNanoleaf(_net);
        await _provider.DescribeAsync(_net, Paired(), default);
        Assert.All(_net.Asks.Where(a => a.Url.Contains(FakeNanoleaf.Token)), a => Assert.StartsWith("http://172.16.0.70:16021/", a.Url));

        // A client pointed at another address with this token is stopped before anything is sent.
        var before = _net.Asks.Count;
        using var wrong = new NanoleafClient(_net, "172.16.0.99", BoundSecret.For(FakeNanoleaf.Token, FakeNanoleaf.Ip));
        await Assert.ThrowsAsync<SecretRuleException>(() => wrong.InfoAsync(default));
        await Assert.ThrowsAsync<SecretRuleException>(() => wrong.StartStreamAsync(default));
        Assert.Equal(before, _net.Asks.Count);
    }

    [Fact]
    public async Task A_pairing_that_cannot_be_read_on_this_account_asks_to_pair_again()
    {
        var unreadable = new PairedLight("nanoleaf", "S1", "Lines", "", FakeNanoleaf.Ip, "nanoleaf|Lines|S1");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, unreadable, default));
        Assert.Equal("Its pairing can't be read on this Windows account; pair it again.", ex.Message);
    }

    [Fact]
    public async Task Streaming_asks_for_external_control_sends_a_colour_per_panel_and_puts_the_old_effect_back()
    {
        _ = new FakeNanoleaf(_net);
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        Assert.Equal(TimeSpan.Zero, session.KeepAlive); // every frame is sent
        Assert.Empty(_net.Sent);

        await session.SendAsync(new[] { 0xAABBCC, 0x000000, 0x112233 }, default);
        var puts = _net.Asks.Where(a => a.Method == "PUT").ToList();
        Assert.Equal(2, puts.Count);
        Assert.EndsWith("/state", puts[0].Url);
        Assert.Equal("{\"on\":{\"value\":true}}", puts[0].Body);
        Assert.EndsWith("/effects", puts[1].Url);
        Assert.Equal("{\"write\":{\"command\":\"display\",\"animType\":\"extControl\",\"extControlVersion\":\"v2\"}}", puts[1].Body);
        var packet = Assert.Single(_net.Sent);
        Assert.Equal((FakeNanoleaf.Ip, 60222), (packet.Host, packet.Port));
        Assert.Equal(Hex.Parse("00 03 | 00 0B AA BB CC 00 00 01 | 00 16 00 00 00 00 00 01 | 00 21 11 22 33 00 00 01"), packet.Data);

        // The next frame goes straight out: external control is only asked for again after 20 seconds.
        await session.SendAsync(new[] { 1, 2, 3 }, default);
        Assert.Equal(2, _net.Sent.Count);
        Assert.Equal(2, _net.Asks.Count(a => a.Method == "PUT"));

        await session.StopAsync(default);
        var last = _net.Asks.Last();
        Assert.Equal(("PUT", "{\"select\":\"Northern Lights\"}"), (last.Method, last.Body));
    }

    [Fact]
    public async Task A_temporary_effect_is_not_put_back()
    {
        _ = new FakeNanoleaf(_net) { Effect = "*Dynamic*" };
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        await using var session = await _provider.StartAsync(_net, Paired(), d, default);
        var before = _net.Asks.Count;
        await session.StopAsync(default);
        Assert.Equal(before, _net.Asks.Count);
    }

    [Fact]
    public void It_is_marked_as_tried_on_real_hardware_and_left_alone_when_not_placed()
    {
        Assert.True(_provider.Tested);
        Assert.False(_provider.FollowsWhenNotPlaced);
        Assert.True(_provider.Pairing.ReturnsSecret);
        Assert.Equal(10, _provider.MaxFramesPerSecond);
        Assert.Equal("panel", _provider.PointName);
    }
}

/// <summary>The lighting module's calls for network lights, the older Nanoleaf calls on top of them, and header sizes.</summary>
public class NetworkLightsModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-netlights-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRgb _rgb = new();
    private readonly FakeLightNetwork _net = new();
    private readonly FakeProvider _fake = new("fake");

    public NetworkLightsModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private async Task<LightingModule> Start()
    {
        var m = new LightingModule(_rgb, _ => false, background: false, null, _net,
            new INetworkLightProvider[] { new NanoleafProvider(), _fake }, new FakeProtector()) { SyncIntervalMs = 10 };
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance, new SettingsStore(SettingsPath, NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    private static async Task<JsonElement> Call(LightingModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task<RpcException> Fail(LightingModule m, string method, object? args = null)
        => await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args));

    private void Installed()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(FakeRgb.Keyboard(0));
    }

    // ---------------------------------------------------------------- network lights

    [Fact]
    public async Task The_brand_list_says_how_to_pair_each_one_and_which_were_tried_on_real_hardware()
    {
        await using var m = await Start();
        var s = await Call(m, "getNetworkLights");
        var brands = s.GetProperty("brands").EnumerateArray().ToList();
        Assert.Equal(new[] { "nanoleaf", "fake" }, brands.Select(b => b.GetProperty("brand").GetString()));
        Assert.True(brands[0].GetProperty("tested").GetBoolean());
        Assert.True(brands[0].GetProperty("returnsSecret").GetBoolean());
        Assert.Equal(3, brands[0].GetProperty("steps").GetArrayLength());
        Assert.False(brands[1].GetProperty("tested").GetBoolean());
        Assert.Equal(0, s.GetProperty("paired").GetArrayLength());
        Assert.False(s.GetProperty("scanning").GetBoolean());
        Assert.True(s.GetProperty("notSupported").GetArrayLength() > 0);
        Assert.Empty(_net.Asks); // looking at the list sends nothing
        Assert.Empty(_net.TcpTried);
    }

    [Fact]
    public async Task Every_brand_is_registered_and_with_nothing_paired_nothing_is_sent_until_the_user_searches()
    {
        Installed();
        // No provider list given: the module finds the brands itself, as the app does.
        await using var m = new LightingModule(_rgb, _ => false, background: false, null, _net, null, new FakeProtector()) { SyncIntervalMs = 10 };
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance, new SettingsStore(SettingsPath, NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);

        var s = await Call(m, "getNetworkLights");
        var brands = s.GetProperty("brands").EnumerateArray().ToList();
        Assert.Equal(new[] { "nanoleaf", "hue", "wled", "govee", "yeelight", "cololight", "leetdesk" },
            brands.Select(b => b.GetProperty("brand").GetString()));
        // Only Nanoleaf has been tried on a real device; the page says so for each of the others.
        Assert.Equal(new[] { "nanoleaf" }, brands.Where(b => b.GetProperty("tested").GetBoolean()).Select(b => b.GetProperty("brand").GetString()));
        Assert.All(brands, b =>
        {
            Assert.True(b.GetProperty("steps").GetArrayLength() > 0);
            if (!b.GetProperty("tested").GetBoolean()) Assert.Contains("ot yet tried", b.GetProperty("notes").GetString());
        });
        // Every brand that has a limit is on the "not supported, and why" list.
        var gaps = s.GetProperty("notSupported").EnumerateArray().Select(g => g.GetProperty("brand").GetString()).ToList();
        Assert.Equal(new[] { "hue", "hue", "govee", "yeelight", "cololight", "leetdesk", "wled", "openrgb", "cloud" }, gaps);
        // The page hides the search button for a brand that can't search, and says which brands wait to be placed.
        Assert.Equal(new[] { "cololight" }, brands.Where(b => !b.GetProperty("canSearch").GetBoolean()).Select(b => b.GetProperty("brand").GetString()));
        Assert.Equal(new[] { "nanoleaf", "hue" }, brands.Where(b => !b.GetProperty("followsUnplaced").GetBoolean()).Select(b => b.GetProperty("brand").GetString()));
        Assert.False(s.GetProperty("scanStopped").GetBoolean());

        // Looking at the page, the status, the layout, and playing an effect: none of it searches or sends anything.
        await Call(m, "getStatus");
        await Call(m, "getLayout");
        await Call(m, "getNanoleaf");
        await Call(m, "setSync", new { enabled = true, effect = "solid" });
        await Task.Delay(150);
        await Call(m, "setSync", new { enabled = false });
        Assert.Empty(_net.Asks);
        Assert.Empty(_net.Sent);
        Assert.Empty(_net.TcpTried);
        Assert.Empty(_net.Browsed);
        Assert.Empty(_net.Listeners);
    }

    [Fact]
    public async Task Search_pair_place_and_forget_a_light_through_the_calls()
    {
        _fake.Lights.Add(new FoundLight("mac1", "Desk strip", "M9", "172.16.0.20"));
        await using var m = await Start();
        var s = await Call(m, "scanNetworkLights", new { brand = "fake" });
        Assert.Equal("172.16.0.20", s.GetProperty("found")[0].GetProperty("address").GetString());

        s = await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        var light = s.GetProperty("paired")[0];
        Assert.Equal("net|fake|mac1", light.GetProperty("id").GetString());
        Assert.Equal("strip", light.GetProperty("kind").GetString());
        Assert.Equal(4, light.GetProperty("points").GetInt32());

        // It is a box on the layout under the same key, and stays there (not connected) once forgotten.
        var layout = await Call(m, "setLayout", new { perLed = true, items = new[] { new { key = "net|fake|mac1", zone = -1, x = 0.0, y = 0.0, w = 1.0, h = 0.1 } } });
        var zone = layout.GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "net|fake|mac1");
        Assert.Equal("Desk strip (Fake)", zone.GetProperty("device").GetString());
        Assert.Equal(4, zone.GetProperty("leds").GetInt32());
        Assert.True(zone.GetProperty("connected").GetBoolean());
        Assert.True((await Call(m, "getNetworkLights")).GetProperty("paired")[0].GetProperty("placed").GetBoolean());

        s = await Call(m, "forgetNetworkLight", new { id = "net|fake|mac1" });
        Assert.Equal(0, s.GetProperty("paired").GetArrayLength());
        zone = (await Call(m, "getLayout")).GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "net|fake|mac1");
        Assert.False(zone.GetProperty("connected").GetBoolean());
        Assert.Equal("invalidArgs", (await Fail(m, "forgetNetworkLight", new { id = "net|fake|mac1" })).Code);
    }

    [Fact]
    public async Task Bad_pairing_requests_are_refused_in_plain_words()
    {
        await using var m = await Start();
        Assert.Equal("invalidArgs", (await Fail(m, "pairNetworkLight", new { brand = "fake", address = "8.8.8.8" })).Code);
        Assert.Equal("invalidArgs", (await Fail(m, "pairNetworkLight", new { brand = "nope", address = "172.16.0.20" })).Code);
        Assert.Equal("invalidArgs", (await Fail(m, "scanNetworkLights", new { brand = "nope" })).Code);
        _fake.OnPair = _ => throw new InvalidOperationException("Switch on LAN Control first.");
        var e = await Fail(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        Assert.Equal(("unavailable", "Switch on LAN Control first."), (e.Code, e.Message));
    }

    [Fact]
    public async Task While_an_effect_plays_a_paired_light_gets_frames_and_is_put_back_when_it_stops()
    {
        Installed();
        await using var m = await Start();
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "setLayout", new { perLed = true, items = new[] { new { key = "net|fake|id-172.16.0.20", zone = -1, x = 0.0, y = 0.0, w = 1.0, h = 0.1 } } });
        await Call(m, "setSync", new { enabled = true, effect = "solid", @params = new { speed = 50, size = 50, direction = "right", color1 = "#FF0000", color2 = "#0000FF" } });
        await Eventually.True(() => _fake.Frames.Count >= 2, "frames to reach the light");
        var frame = _fake.Frames.Last();
        Assert.Equal(4, frame.Length);
        Assert.All(frame, c => Assert.True((c >> 16) > 0 && (c & 0xFFFF) == 0, $"expected a red, got {c:X6}"));
        Assert.True((await Call(m, "getNetworkLights")).GetProperty("paired")[0].GetProperty("streaming").GetBoolean());

        await Call(m, "setSync", new { enabled = false });
        await Eventually.True(() => _fake.Stops == 1, "the light to be put back");
        Assert.False((await Call(m, "getNetworkLights")).GetProperty("paired")[0].GetProperty("streaming").GetBoolean());
    }

    // ---------------------------------------------------------------- Nanoleaf: the older calls and saved settings still work

    [Fact]
    public async Task The_older_nanoleaf_calls_find_pair_and_forget_as_before()
    {
        _ = new FakeNanoleaf(_net);
        await using var m = await Start();
        var s = await Call(m, "getNanoleaf");
        Assert.False(s.GetProperty("paired").GetBoolean());

        s = await Call(m, "findNanoleaf");
        Assert.Equal(FakeNanoleaf.Ip, s.GetProperty("found")[0].GetString());

        Assert.Equal("invalidArgs", (await Fail(m, "pairNanoleaf", new { ip = "8.8.8.8" })).Code);
        s = await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });
        Assert.True(s.GetProperty("paired").GetBoolean());
        Assert.Equal((FakeNanoleaf.Ip, "Lines", 3), (s.GetProperty("ip").GetString(), s.GetProperty("name").GetString(), s.GetProperty("panels").GetInt32()));
        Assert.False(s.GetProperty("streaming").GetBoolean());

        // Saved exactly where and how it always was; the token is not in the file.
        var file = File.ReadAllText(SettingsPath);
        using var doc = JsonDocument.Parse(file);
        var saved = doc.RootElement.GetProperty("lighting").GetProperty("nanoleaf");
        Assert.Equal((FakeNanoleaf.Ip, "Lines", "S1"), (saved.GetProperty("ip").GetString(), saved.GetProperty("name").GetString(), saved.GetProperty("serial").GetString()));
        Assert.NotEqual("", saved.GetProperty("protectedToken").GetString());
        Assert.DoesNotContain(FakeNanoleaf.Token, file);
        Assert.Equal(0, doc.RootElement.GetProperty("lighting").GetProperty("networkLights").GetArrayLength());

        // On the layout under its old key, drawn as bars; and in the new list too.
        var zone = (await Call(m, "getLayout")).GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "nanoleaf|Lines|S1");
        Assert.Equal(("Lines (Nanoleaf)", -1, 3, 20, 12), (zone.GetProperty("device").GetString(), zone.GetProperty("zone").GetInt32(),
            zone.GetProperty("leds").GetInt32(), zone.GetProperty("matrixWidth").GetInt32(), zone.GetProperty("matrixHeight").GetInt32()));
        Assert.Equal(3, zone.GetProperty("segments").GetArrayLength());
        Assert.Equal("nanoleaf|Lines|S1", (await Call(m, "getNetworkLights")).GetProperty("paired")[0].GetProperty("id").GetString());

        s = await Call(m, "forgetNanoleaf");
        Assert.False(s.GetProperty("paired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("lighting").GetProperty("nanoleaf").ValueKind);
    }

    [Fact]
    public async Task A_nanoleaf_that_is_not_ready_to_pair_says_to_hold_the_button()
    {
        _ = new FakeNanoleaf(_net) { ReadyToPair = false };
        await using var m = await Start();
        var e = await Fail(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });
        Assert.Equal("unavailable", e.Code);
        Assert.Contains("Hold its power button", e.Message);
    }

    [Fact]
    public async Task A_nanoleaf_paired_by_an_older_version_and_its_place_on_the_layout_are_kept()
    {
        var old = new NanoleafSettings { Ip = FakeNanoleaf.Ip, Name = "Lines", Serial = "S1" };
        old.SetToken(FakeNanoleaf.Token);
        var settings = new LightingSettings { Nanoleaf = old };
        settings.Layout.PerLed = true;
        settings.Layout.Items.Add(new LayoutItem { Key = "nanoleaf|Lines|S1", Zone = -1, X = 0.6, Y = 0.1, W = 0.3, H = 0.4 });
        new SettingsStore(SettingsPath, NullLog.Instance).Set(LightingSettings.Section, settings);

        _ = new FakeNanoleaf(_net);
        await using var m = await Start();
        Assert.True((await Call(m, "getNanoleaf")).GetProperty("paired").GetBoolean());
        JsonElement zone = default;
        await Eventually.True(() =>
        {
            zone = Call(m, "getLayout").GetAwaiter().GetResult().GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "nanoleaf|Lines|S1");
            return zone.GetProperty("connected").GetBoolean();
        }, "its panels to be read");
        Assert.Equal(3, zone.GetProperty("leds").GetInt32());
        Assert.Equal(0.6, zone.GetProperty("placed").GetProperty("x").GetDouble());
        Assert.Equal(3, (await Call(m, "getNanoleaf")).GetProperty("panels").GetInt32());
        Assert.All(_net.Asks, a => Assert.StartsWith("http://172.16.0.70:16021/", a.Url));
    }

    [Fact]
    public async Task Pairing_a_nanoleaf_moves_its_room_light_box_to_its_panels()
    {
        var settings = new LightingSettings();
        settings.Layout.Items.Add(new LayoutItem { Key = "homelab|Nanoleaf Lines|nanoleaf-lines", Zone = -1, X = 0.2, Y = 0.2, W = 0.2, H = 0.2 });
        new SettingsStore(SettingsPath, NullLog.Instance).Set(LightingSettings.Section, settings);
        _ = new FakeNanoleaf(_net);
        await using var m = await Start();
        await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });
        var zones = (await Call(m, "getLayout")).GetProperty("zones").EnumerateArray().ToList();
        var zone = Assert.Single(zones);
        Assert.Equal("nanoleaf|Lines|S1", zone.GetProperty("key").GetString());
        Assert.Equal(0.2, zone.GetProperty("placed").GetProperty("x").GetDouble());
    }

    // ---------------------------------------------------------------- network lights do not need OpenRGB

    private const string Red = "#FF0000", Blue = "#0000FF";

    private static object Solid(bool? enabled = null, string effect = "solid") =>
        new { enabled, effect, @params = new { speed = 50, size = 50, direction = "right", color1 = Red, color2 = Blue } };

    [Fact]
    public async Task With_a_light_on_the_network_paired_the_effects_play_without_openrgb()
    {
        // OpenRGB is not installed and not running: nothing of the PC's own can be lit.
        await using var m = await Start();
        Assert.False((await Call(m, "getStatus")).GetProperty("openRgb").GetProperty("connected").GetBoolean());
        var e = await Fail(m, "setSync", Solid(true));
        Assert.Equal(("unavailable", "OpenRGB is not running. Start it first."), (e.Code, e.Message)); // nothing at all to play on

        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "setSync", Solid(true));
        await Eventually.True(() => _fake.Frames.Count >= 2, "frames to reach the light without OpenRGB");
        await Eventually.True(() => Call(m, "getSync").GetAwaiter().GetResult().GetProperty("status").GetString()!.StartsWith("Playing"), "the status to show");
        Assert.EndsWith("(the PC's own lights are waiting for OpenRGB)", (await Call(m, "getSync")).GetProperty("status").GetString());

        await Call(m, "setSync", new { enabled = false });
        await Eventually.True(() => _fake.Stops == 1, "the light to be put back");
    }

    [Fact]
    public async Task When_openrgb_stops_answering_the_lights_on_the_network_carry_on()
    {
        Installed();
        _fake.KeepAlive = TimeSpan.Zero; // every frame is sent, so frames can be counted
        await using var m = await Start();
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "setSync", Solid(true));
        await Eventually.True(() => _fake.Frames.Count >= 2, "frames to reach the light");

        // OpenRGB goes away and can't be started again.
        _rgb.Listening = false;
        _rgb.StartWorks = false;
        _rgb.FailWrites = new IOException("The connection was closed.");
        await Eventually.True(() => !_rgb.Connected, "the lost connection to be noticed");
        var then = _fake.Frames.Count;
        await Eventually.True(() => _fake.Frames.Count >= then + 5, "the light to keep getting frames");
        Assert.Equal(0, _fake.Stops);
        Assert.Equal(1, _fake.Starts);
        Assert.EndsWith("(the PC's own lights are waiting for OpenRGB)", (await Call(m, "getSync")).GetProperty("status").GetString());

        // OpenRGB comes back: the sync notices by itself and the PC's lights follow again.
        _rgb.FailWrites = null;
        _rgb.Listening = true;
        await Eventually.True(() => _rgb.Connected, "OpenRGB to be connected again");
        await Call(m, "setSync", new { enabled = false });
    }

    [Fact]
    public async Task Switching_the_effect_keeps_the_lights_on_the_network_playing_without_handing_them_back_in_between()
    {
        Installed();
        _fake.KeepAlive = TimeSpan.Zero;
        await using var m = await Start();
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "setSync", Solid(true));
        await Eventually.True(() => _fake.Frames.Count >= 2, "frames to reach the light");

        // A new run starts with the new effect while the old run is still on its way out.
        await Call(m, "setSync", Solid(effect: "breathing"));
        var then = _fake.Frames.Count;
        await Eventually.True(() => _fake.Frames.Count >= then + 5, "frames of the new effect");
        await Task.Delay(300); // the old run has ended by now
        Assert.Equal(0, _fake.Stops);
        Assert.Equal(1, _fake.Starts);
        Assert.True((await Call(m, "getNetworkLights")).GetProperty("paired")[0].GetProperty("streaming").GetBoolean());

        await Call(m, "setSync", new { enabled = false });
        await Eventually.True(() => _fake.Stops == 1, "the light to be put back once, at the real stop");
    }

    [Fact]
    public async Task Asking_whether_the_nanoleaf_plays_never_waits_for_the_lighting_lock()
    {
        // The room lights' module asks this while it holds its own lock, and the lighting module calls into that
        // module while holding the lighting lock. If this question waited for the lighting lock, both would hang.
        await using var m = await Start();
        var answered = false;
        m.ExternalLights = () =>
        {
            // Called with the lighting lock held (getLayout). Another thread asks the question meanwhile.
            var asked = Task.Run(() => m.NanoleafStreaming);
            answered = asked.Wait(TimeSpan.FromSeconds(3));
            return Array.Empty<LightingModule.ExternalLight>();
        };
        await Call(m, "getLayout");
        Assert.True(answered, "the answer came while the lighting lock was held by somebody else");
    }

    [Fact]
    public async Task A_nanoleaf_paired_again_under_a_new_name_keeps_its_place_and_leaves_no_second_copy()
    {
        var nanoleaf = new FakeNanoleaf(_net);
        await using var m = await Start();
        await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });
        await Call(m, "setLayout", new { perLed = true, items = new[] { new { key = "nanoleaf|Lines|S1", zone = -1, x = 0.6, y = 0.1, w = 0.3, h = 0.4 } } });

        nanoleaf.Name = "Shelf lines"; // renamed in the Nanoleaf app
        await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });

        var paired = (await Call(m, "getNetworkLights")).GetProperty("paired");
        Assert.Equal(1, paired.GetArrayLength());
        Assert.Equal("nanoleaf|Shelf lines|S1", paired[0].GetProperty("id").GetString());
        Assert.True(paired[0].GetProperty("placed").GetBoolean());
        var zone = Assert.Single((await Call(m, "getLayout")).GetProperty("zones").EnumerateArray());
        Assert.Equal("nanoleaf|Shelf lines|S1", zone.GetProperty("key").GetString());
        Assert.Equal(0.6, zone.GetProperty("placed").GetProperty("x").GetDouble());
    }

    // ---------------------------------------------------------------- colours after a header was resized

    [Fact]
    public void Saved_colours_are_fitted_to_a_header_that_changed_size()
    {
        uint r = 0xFF, g = 0xFF00, b = 0xFF0000, w = 0xFFFFFF;
        // 2 fixed lights, a header of 2 that became 4, 1 fixed light after it: the header's colours are stretched, the rest stay.
        Assert.Equal(new[] { r, r, g, g, b, b, w }, LightingModule.FitLedColors(new[] { r, r, g, b, w }, 7, start: 2, was: 2, now: 4));
        // Shrunk to one light.
        Assert.Equal(new[] { r, r, g, w }, LightingModule.FitLedColors(new[] { r, r, g, b, w }, 4, start: 2, was: 2, now: 1));
        // A header that had no lights takes the colour next to it.
        Assert.Equal(new[] { r, r, r, r, r }, LightingModule.FitLedColors(new[] { r, r }, 5, start: 2, was: 0, now: 3));
        // Which header changed isn't known: the whole row is stretched.
        Assert.Equal(new[] { r, r, g, g }, LightingModule.FitLedColors(new[] { r, g }, 4, -1, -1, -1));
        Assert.Equal(new[] { r, g }, LightingModule.FitLedColors(new[] { r, g }, 2, -1, -1, -1));
        Assert.Empty(LightingModule.FitLedColors(new[] { r, g }, 0, -1, -1, -1));
    }

    [Fact]
    public async Task After_a_header_is_resized_the_device_gets_its_colours_back_instead_of_going_dark()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0));
        await using var m = await Start();
        await Call(m, "setColor", new { color = Red });
        var red = _rgb.Devices[0].Colors[0];
        Assert.NotEqual(0u, red);

        var s = await Call(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 4 });
        // OpenRGB emptied the colours on the resize; they were sent again, the new lights taking the colour next to them.
        Assert.Equal(12, _rgb.Devices[0].Colors.Count);
        Assert.All(_rgb.Devices[0].Colors, c => Assert.Equal(red, c));
        Assert.StartsWith("leds 0 n=12 ", _rgb.Sent.Last(x => x.StartsWith("leds 0 ")));
        Assert.Equal(Red, s.GetProperty("devices")[0].GetProperty("color").GetString());
    }

    [Fact]
    public async Task Lights_are_put_back_after_the_sync_even_when_a_header_was_resized_while_it_ran()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0));
        await using var m = await Start();
        await Call(m, "setColor", new { color = Red });
        var red = _rgb.Devices[0].Colors[0];

        await Call(m, "setSync", Solid(true));
        await Eventually.True(() => _rgb.Devices[0].Colors.Any(c => c != red), "the sync to colour the device");
        await Call(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 4 }); // allowed while the sync runs
        await Call(m, "setSync", new { enabled = false });

        // The colours from before the sync were for 8 lights; the device has 12 now. It still gets them back.
        Assert.Equal(12, _rgb.Devices[0].Colors.Count);
        Assert.All(_rgb.Devices[0].Colors, c => Assert.Equal(red, c));
    }

    [Fact]
    public async Task A_scene_saved_before_a_header_was_resized_still_colours_that_device()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0));
        await using var m = await Start();
        await Call(m, "setColor", new { color = Red });
        var red = _rgb.Devices[0].Colors[0];
        await Call(m, "saveScene", new { name = "Evening" });
        await Call(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 6 });
        await Call(m, "setColor", new { color = Blue });

        await Call(m, "applyScene", new { name = "Evening" });
        Assert.Equal(14, _rgb.Devices[0].Colors.Count);
        Assert.All(_rgb.Devices[0].Colors, c => Assert.Equal(red, c));
    }

    // ---------------------------------------------------------------- header sizes (OpenRGB zone resize)

    private static RgbController Hub(int index, string name = "Corsair Commander Pro", int protocol = 0, uint flags = 0) => new()
    {
        Index = index, Type = 3, Name = name, Vendor = name.Split(' ')[0], Location = "HID: hub", Protocol = protocol,
        Modes = { new RgbMode { Name = "Direct", Flags = RgbMode.PerLedColor, ColorMode = 1 } },
        Zones =
        {
            new RgbZone { Name = "Pump", LedsMin = 8, LedsMax = 8, LedsCount = 8, Flags = 0 },
            new RgbZone { Name = "RGB Header 1", LedsMin = 0, LedsMax = 204, LedsCount = 0, Flags = flags },
        },
        LedCount = 8,
        Colors = Enumerable.Repeat(0u, 8).ToList(),
    };

    [Fact]
    public void The_resize_request_is_the_zone_and_the_new_size()
    {
        Assert.Equal(Hex.Parse("4F 52 47 42 02 00 00 00 E8 03 00 00 08 00 00 00 | 01 00 00 00 | 3C 00 00 00"), OpenRgbProtocol.ResizeZonePacket(2, 1, 60));
    }

    [Fact]
    public async Task The_user_can_set_how_many_lights_hang_on_a_header()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0));
        await using var m = await Start();
        var zones = (await Call(m, "getStatus")).GetProperty("devices")[0].GetProperty("zones");
        Assert.False(zones[0].GetProperty("resizable").GetBoolean());
        Assert.True(zones[1].GetProperty("resizable").GetBoolean());
        Assert.Equal((0, 204), (zones[1].GetProperty("ledsMin").GetInt32(), zones[1].GetProperty("ledsMax").GetInt32()));
        Assert.DoesNotContain(_rgb.Sent, x => x.StartsWith("resize")); // never by itself

        var s = await Call(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 60 });
        Assert.Equal("resize 0 1 60", Assert.Single(_rgb.Sent, x => x.StartsWith("resize")));
        Assert.Equal(60, s.GetProperty("devices")[0].GetProperty("zones")[1].GetProperty("ledCount").GetInt32());
        Assert.Equal(68, s.GetProperty("devices")[0].GetProperty("ledCount").GetInt32()); // read again after the change
    }

    [Fact]
    public async Task A_size_outside_the_devices_limits_or_on_a_fixed_zone_is_refused()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0));
        await using var m = await Start();
        var e = await Fail(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 205 });
        Assert.Equal(("invalidArgs", "RGB Header 1 takes 0 to 204 lights."), (e.Code, e.Message));
        Assert.Equal("invalidArgs", (await Fail(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = -1 })).Code);
        Assert.Equal("invalidArgs", (await Fail(m, "setZoneSize", new { deviceId = 0, zone = 7, leds = 10 })).Code);
        Assert.Equal("invalidArgs", (await Fail(m, "setZoneSize", new { deviceId = 9, zone = 0, leds = 10 })).Code);
        e = await Fail(m, "setZoneSize", new { deviceId = 0, zone = 0, leds = 8 });
        Assert.Equal(("unavailable", "Pump on Corsair Commander Pro has a fixed number of lights."), (e.Code, e.Message));
        Assert.DoesNotContain(_rgb.Sent, x => x.StartsWith("resize"));
    }

    [Fact]
    public async Task A_newer_openrgb_says_itself_which_zones_can_be_sized()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0, protocol: 5, flags: 0));                         // limits differ, but OpenRGB doesn't mark it
        _rgb.Devices.Add(Hub(1, protocol: 5, flags: RgbZone.SizeIsSetByUser));
        await using var m = await Start();
        Assert.Equal("unavailable", (await Fail(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 30 })).Code);
        await Call(m, "setZoneSize", new { deviceId = 1, zone = 1, leds = 30 });
        Assert.Equal("resize 1 1 30", Assert.Single(_rgb.Sent, x => x.StartsWith("resize")));
    }

    [Fact]
    public async Task A_lian_li_fan_hub_only_takes_whole_fans()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(Hub(0, name: "Lian Li Uni Hub - SL V2"));
        await using var m = await Start();
        var e = await Fail(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 40 });
        Assert.Equal(("invalidArgs", "This hub counts 16 lights for each fan: pick a multiple of 16."), (e.Code, e.Message));
        await Call(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 48 });
        Assert.Contains("resize 0 1 48", _rgb.Sent);
    }

    [Fact]
    public async Task Header_sizes_need_openrgb_running()
    {
        await using var m = await Start();
        Assert.Equal("unavailable", (await Fail(m, "setZoneSize", new { deviceId = 0, zone = 1, leds = 10 })).Code);
    }
}
