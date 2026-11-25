using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;
using RigControl.Lighting.Tests.Network.Providers;

namespace RigControl.Lighting.Tests.Network;

/// <summary>Switching, dimming and colouring a network light by hand, and giving it back (NetworkLights).</summary>
public class DirectControlTests : IDisposable
{
    private readonly LightingSettings _settings = new();
    private readonly object _gate = new();
    private readonly FakeLightNetwork _net = new();
    private readonly ListLog _log = new();
    private readonly FakeProvider _a = new("alpha");
    private readonly FakeProvider _b = new("beta");
    private readonly List<NetworkLights> _made = new();

    public void Dispose()
    {
        foreach (var m in _made) m.Dispose();
    }

    private NetworkLights Lights(TimeSpan? readEvery = null)
    {
        var store = new NetworkLightStore(() => _settings, () => { }, _gate, new FakeProtector());
        var lights = new NetworkLights(_net, store, _log, new INetworkLightProvider[] { _a, _b })
        {
            StopTime = TimeSpan.FromSeconds(1),
            ReadEvery = readEvery ?? TimeSpan.FromSeconds(15),
        };
        _made.Add(lights);
        return lights;
    }

    [Fact]
    public async Task The_first_change_remembers_what_the_light_did_and_give_back_puts_it_back()
    {
        var lights = Lights();
        var lamp = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        Assert.False(lights.Status(Array.Empty<LayoutItem>()).Paired.Single().TakenOver);

        await lights.SetAsync(lamp.LayoutKey, new LightChange(Colour: 0xFF0000), default);
        await lights.SetAsync(lamp.LayoutKey, new LightChange(Brightness: 30), default);
        // Remembered once, before the first change only; kept in the settings so it survives a restart.
        Assert.Equal(1, _a.Saves);
        Assert.Equal("was: effect Aurora", _settings.NetworkLights.Single().Before);
        Assert.Equal(new[] { new LightChange(Colour: 0xFF0000), new LightChange(Brightness: 30) }, _a.Changes);
        var row = lights.Status(Array.Empty<LayoutItem>()).Paired.Single();
        Assert.True(row.TakenOver);
        Assert.Equal((true, 30, "#FF0000"), (row.On, row.Brightness, row.Color));
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp: switched on, colour #FF0000 by hand.");

        await lights.GiveBackAsync(lamp.LayoutKey, default);
        Assert.Equal("was: effect Aurora", Assert.Single(_a.GivenBack));
        Assert.Null(_settings.NetworkLights.Single().Before);
        Assert.False(lights.Status(Array.Empty<LayoutItem>()).Paired.Single().TakenOver);
        // Nothing to give back any more.
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => lights.GiveBackAsync(lamp.LayoutKey, default));
        Assert.Contains("nothing to give back", again.Message);
    }

    [Fact]
    public async Task A_light_an_effect_drives_is_let_go_and_put_back_before_the_change_is_sent()
    {
        var lights = Lights();
        var lamp = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        lights.Frame(new NetworkFrame(Array.Empty<LayoutItem>(), null, 0, 0, c => c, 0x123456));
        await Eventually.True(() => !_a.Frames.IsEmpty, "the effect to reach the light");

        await lights.SetAsync(lamp.LayoutKey, new LightChange(On: false), default);
        Assert.Equal(1, _a.Stops);                       // the effect's session put it back first
        Assert.Equal(1, Assert.Single(_a.StopsAtChange)); // and only then was the change sent
        Assert.False(lights.IsStreaming("alpha"));
    }

    [Fact]
    public async Task Something_a_brand_cannot_do_is_refused_with_its_reason_and_nothing_is_sent()
    {
        _a.Can = _a.Can with { Colour = LightAbility.No("No colour on this one.") };
        var lights = Lights();
        var lamp = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => lights.SetAsync(lamp.LayoutKey, new LightChange(Colour: 0x00FF00), default));
        Assert.Equal("No colour on this one.", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => lights.SetAsync(lamp.LayoutKey, new LightChange(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => lights.SetAsync("net|alpha|nope", new LightChange(On: true), default));
        Assert.Empty(_a.Changes);
        Assert.Equal(0, _a.Saves);
        // Off still works, and black is off.
        await lights.SetAsync(lamp.LayoutKey, new LightChange(Colour: 0), default);
        Assert.True(Assert.Single(_a.Changes).TurnsOff);
    }

    [Fact]
    public async Task A_light_that_fails_says_so_on_its_row_in_plain_words()
    {
        _a.SetError = new IOException("socket closed");
        var lights = Lights();
        var lamp = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => lights.SetAsync(lamp.LayoutKey, new LightChange(On: true), default));
        Assert.StartsWith("It doesn't answer at 172.16.0.20.", ex.Message);
        Assert.Equal(ex.Message, lights.Status(Array.Empty<LayoutItem>()).Paired.Single().Problem);
    }

    [Fact]
    public async Task Everything_at_once_reaches_every_brand_that_takes_part_and_can_do_it()
    {
        _b.JoinsEverything = false; // like a Hue Bridge: a whole house
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        await lights.PairAsync("beta", "172.16.0.21", null, default);
        var (tried, problems) = await lights.SetAllAsync(new LightChange(Colour: 0x0000FF), default);
        Assert.Equal((1, 0), (tried, problems.Count));
        Assert.Equal(new LightChange(Colour: 0x0000FF), Assert.Single(_a.Changes));
        Assert.Empty(_b.Changes);

        // A brand that takes no colour is left out of a colour, but still switched off.
        _a.Can = _a.Can with { Colour = LightAbility.No("No colour.") };
        (tried, _) = await lights.SetAllAsync(new LightChange(Colour: 0x00FF00), default);
        Assert.Equal(0, tried);
        (tried, problems) = await lights.SetAllAsync(new LightChange(On: false), default);
        Assert.Equal((1, 0), (tried, problems.Count));
        Assert.True(_a.Changes.Last().TurnsOff);

        _a.SetError = new InvalidOperationException("It is busy.");
        (tried, problems) = await lights.SetAllAsync(new LightChange(On: false), default);
        Assert.Equal("Alpha Lamp: It is busy.", Assert.Single(problems));
    }

    [Fact]
    public async Task What_a_light_shows_is_read_in_the_background_and_shown_on_its_row()
    {
        var lights = Lights(readEvery: TimeSpan.FromMilliseconds(1));
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        _a.ReadResult = new LightReading(true, 55, 0x00FF00);
        lights.Status(Array.Empty<LayoutItem>());
        await Eventually.True(() => lights.Status(Array.Empty<LayoutItem>()).Paired.Single().Brightness == 55, "the reading to show");
        var row = lights.Status(Array.Empty<LayoutItem>()).Paired.Single();
        Assert.Equal((true, "#00FF00"), (row.On, row.Color));
        Assert.NotNull(row.Controls);
        Assert.True(row.Controls!.GiveBack.Can);

        // A brand that can't say what it shows is never asked.
        _b.Can = _b.Can with { ReadBack = LightAbility.No("It can't say.") };
        await lights.PairAsync("beta", "172.16.0.21", null, default);
        lights.Status(Array.Empty<LayoutItem>());
        await Task.Delay(50);
        Assert.Equal(0, _b.Reads);
        Assert.Null(lights.Status(Array.Empty<LayoutItem>()).Paired.Single(p => p.Brand == "beta").On);
    }

    [Fact]
    public void A_change_reads_plainly()
    {
        Assert.True(new LightChange(Colour: 0).TurnsOff);
        Assert.True(new LightChange(On: false, Colour: 0xFF0000).TurnsOff);
        Assert.False(new LightChange(Brightness: 0).TurnsOff);
        Assert.True(new LightChange().IsEmpty);
        Assert.Equal((0, 100, 100), LightColour.ToHsv(0xFF0000));
        Assert.Equal((240, 100, 100), LightColour.ToHsv(0x0000FF));
        Assert.Equal(0x00FF00, LightColour.FromHsv(120, 100, 100));
        Assert.Equal(0xFF8000, LightColour.Full(0x804000));
    }
}

/// <summary>The lighting module's calls for changing network lights by hand.</summary>
public class DirectControlModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-direct-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRgb _rgb = new();
    private readonly FakeLightNetwork _net = new();
    private readonly FakeProvider _fake = new("fake");
    private readonly FakeProvider _house = new("house") { JoinsEverything = false };

    public DirectControlModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private async Task<LightingModule> Start()
    {
        var m = new LightingModule(_rgb, _ => false, background: false, null, _net,
            new INetworkLightProvider[] { _fake, _house }, new FakeProtector()) { SyncIntervalMs = 10 };
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance, new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
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

    private const string Lamp = "net|fake|id-172.16.0.20";

    [Fact]
    public async Task Every_argument_is_checked_before_anything_is_sent()
    {
        await using var m = await Start();
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });

        Assert.Equal(("invalidArgs", "That light isn't paired."), Pick(await Fail(m, "setNetworkLight", new { id = "net|fake|other", on = true })));
        Assert.Equal("invalidArgs", (await Fail(m, "setNetworkLight", new { on = true })).Code);
        Assert.Equal(("invalidArgs", "Say what to change: on, brightness or colour."), Pick(await Fail(m, "setNetworkLight", new { id = Lamp })));
        Assert.Equal(("invalidArgs", "Brightness is 0 to 100%."), Pick(await Fail(m, "setNetworkLight", new { id = Lamp, brightness = 101 })));
        Assert.Equal("invalidArgs", (await Fail(m, "setNetworkLight", new { id = Lamp, brightness = -1 })).Code);
        Assert.Equal(("invalidArgs", "Colours are #RRGGBB."), Pick(await Fail(m, "setNetworkLight", new { id = Lamp, color = "red" })));
        Assert.Equal("invalidArgs", (await Fail(m, "setNetworkLight", new { id = Lamp, color = "#12345" })).Code);
        Assert.Equal("invalidArgs", (await Fail(m, "setNetworkLight", new { id = Lamp, color = "#GG0000" })).Code);
        _fake.Can = _fake.Can with { Brightness = LightAbility.No("This one has no dimmer.") };
        Assert.Equal(("unavailable", "This one has no dimmer."), Pick(await Fail(m, "setNetworkLight", new { id = Lamp, brightness = 50 })));
        Assert.Equal(("invalidArgs", "That light isn't paired."), Pick(await Fail(m, "giveBackNetworkLight", new { id = "nope" })));
        Assert.Equal("invalidArgs", (await Fail(m, "giveBackNetworkLight", new { id = Lamp })).Code); // nothing taken over yet
        Assert.Empty(_fake.Changes);
        Assert.Equal(0, _fake.Saves);

        var s = await Call(m, "setNetworkLight", new { id = Lamp, on = true, color = "#00ff80" });
        Assert.Equal(new LightChange(true, null, 0x00FF80), Assert.Single(_fake.Changes));
        var row = s.GetProperty("paired").EnumerateArray().Single();
        Assert.True(row.GetProperty("takenOver").GetBoolean());
        Assert.Equal("#00FF80", row.GetProperty("color").GetString());
        Assert.False(row.GetProperty("controls").GetProperty("brightness").GetProperty("can").GetBoolean());
        Assert.Equal("This one has no dimmer.", row.GetProperty("controls").GetProperty("brightness").GetProperty("why").GetString());

        s = await Call(m, "giveBackNetworkLight", new { id = Lamp });
        Assert.Single(_fake.GivenBack);
        Assert.False(s.GetProperty("paired").EnumerateArray().Single().GetProperty("takenOver").GetBoolean());
    }

    private static (string, string) Pick(RpcException e) => (e.Code, e.Message);

    [Fact]
    public async Task A_change_by_hand_on_a_network_light_stops_the_light_sync()
    {
        Installed();
        await using var m = await Start();
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "setSync", new { enabled = true, effect = "solid", @params = new { speed = 50, size = 50, direction = "right", color1 = "#FF0000", color2 = "#0000FF" } });
        await Eventually.True(() => !_fake.Frames.IsEmpty, "the effect to reach the light");

        await Call(m, "setNetworkLight", new { id = Lamp, color = "#0000FF" });
        Assert.False((await Call(m, "getSync")).GetProperty("enabled").GetBoolean());
        Assert.Equal(1, _fake.Stops);                         // the effect let go of it (and put it back) first
        Assert.Equal(1, Assert.Single(_fake.StopsAtChange));   // then the hand change went out
        Assert.Equal(new LightChange(Colour: 0x0000FF), Assert.Single(_fake.Changes));
    }

    [Fact]
    public async Task Everything_at_once_and_off_reach_the_lights_on_the_network_but_not_a_whole_house()
    {
        Installed();
        await using var m = await Start();
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "pairNetworkLight", new { brand = "house", address = "172.16.0.30" });
        _rgb.Sent.Clear();

        await Call(m, "setColor", new { color = "#FF8800" });
        Assert.Contains(_rgb.Sent, x => x.Contains("FF8800", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new LightChange(Colour: 0xFF8800), Assert.Single(_fake.Changes));
        Assert.Empty(_house.Changes);

        await Call(m, "off");
        Assert.True(_fake.Changes.Last().TurnsOff);
        Assert.Empty(_house.Changes);

        // Naming devices means only those PC devices: the network lights are left alone.
        await Call(m, "setColor", new { color = "#00FF00", deviceIds = new[] { 0 } });
        Assert.Equal(2, _fake.Changes.Count);
    }

    [Fact]
    public async Task Without_openrgb_everything_at_once_still_reaches_the_lights_on_the_network()
    {
        await using var m = await Start();
        Assert.Equal("unavailable", (await Fail(m, "setColor", new { color = "#FF0000" })).Code); // nothing at all to colour
        await Call(m, "pairNetworkLight", new { brand = "fake", address = "172.16.0.20" });
        await Call(m, "setColor", new { color = "#FF0000" });
        Assert.Equal(new LightChange(Colour: 0xFF0000), Assert.Single(_fake.Changes));
        await Call(m, "off");
        Assert.True(_fake.Changes.Last().TurnsOff);

        _fake.SetError = new InvalidOperationException("It is busy.");
        var e = await Fail(m, "off");
        Assert.Equal(("unavailable", "Fake Lamp: It is busy."), (e.Code, e.Message));
        Assert.Equal("invalidArgs", (await Fail(m, "setColor", new { color = "blue" })).Code);
    }
}

/// <summary>Each brand's direct control, packet by packet, against made-up devices.</summary>
public class DirectControlProviderTests
{
    private readonly FakeLightNetwork _net = new();

    // ---- Nanoleaf

    private const string LeafRoot = "http://172.16.0.70:16021/api/v1/TOKabc123/";

    private static PairedLight Leaf() =>
        new("nanoleaf", "S1", "Lines", "", "172.16.0.70", "nanoleaf|Lines|S1", null, new Dictionary<string, string> { ["token"] = "TOKabc123" });

    private void LeafAnswers(string all)
    {
        _net.OnHttp = ask => ask.Url.StartsWith(LeafRoot, StringComparison.Ordinal)
            ? ask.Method == "GET" && ask.Url == LeafRoot ? FakeLightNetwork.Json(all) : FakeLightNetwork.Status(204)
            : null;
    }

    private const string LeafEffect =
        "{\"name\":\"Lines\",\"state\":{\"on\":{\"value\":true},\"brightness\":{\"value\":70,\"max\":100,\"min\":0},\"hue\":{\"value\":120,\"max\":360,\"min\":0},"
        + "\"sat\":{\"value\":100,\"max\":100,\"min\":0},\"ct\":{\"value\":4000},\"colorMode\":\"effect\"},\"effects\":{\"select\":\"Northern Lights\",\"effectsList\":[]}}";

    [Fact]
    public async Task Nanoleaf_reads_sets_and_gives_back_its_effect_through_its_state()
    {
        var p = new NanoleafProvider();
        LeafAnswers(LeafEffect);
        Assert.Equal(new LightReading(true, 70, null), await p.ReadAsync(_net, Leaf(), default)); // an effect has no one colour
        var saved = await p.SaveAsync(_net, Leaf(), default);

        await p.SetAsync(_net, Leaf(), new LightChange(Brightness: 40, Colour: 0x0000FF), default);
        await p.SetAsync(_net, Leaf(), new LightChange(On: false), default);
        var puts = _net.Asks.Where(a => a.Method == "PUT").Select(a => (a.Url[LeafRoot.Length..], a.Body ?? "")).ToList();
        Assert.Equal(new[]
        {
            ("state", "{\"on\":{\"value\":true},\"brightness\":{\"value\":40},\"hue\":{\"value\":240},\"sat\":{\"value\":100}}"),
            ("state", "{\"on\":{\"value\":false}}"),
        }, puts);

        while (_net.Asks.TryDequeue(out _)) { }
        await p.GiveBackAsync(_net, Leaf(), saved, default);
        puts = _net.Asks.Select(a => (a.Url[LeafRoot.Length..], a.Body ?? "")).ToList();
        Assert.Equal(new[]
        {
            ("effects", "{\"select\":\"Northern Lights\"}"),
            ("state", "{\"brightness\":{\"value\":70}}"),
            ("state", "{\"on\":{\"value\":true}}"),
        }, puts);
        // The key only ever goes to the Nanoleaf.
        Assert.All(_net.Asks, a => Assert.StartsWith(LeafRoot, a.Url));
    }

    [Fact]
    public async Task Nanoleaf_with_a_plain_colour_reads_it_and_gets_it_back()
    {
        var p = new NanoleafProvider();
        LeafAnswers(LeafEffect.Replace("\"colorMode\":\"effect\"", "\"colorMode\":\"hs\"").Replace("Northern Lights", "*Solid*").Replace("{\"value\":true}", "{\"value\":false}"));
        Assert.Equal(new LightReading(false, 70, 0x00FF00), await p.ReadAsync(_net, Leaf(), default));
        var saved = await p.SaveAsync(_net, Leaf(), default);
        while (_net.Asks.TryDequeue(out _)) { }
        await p.GiveBackAsync(_net, Leaf(), saved, default);
        Assert.Equal(new[]
        {
            "{\"hue\":{\"value\":120},\"sat\":{\"value\":100}}", // *Solid* can't be picked: its colour is set instead
            "{\"brightness\":{\"value\":70}}",
            "{\"on\":{\"value\":false}}",
        }, _net.Asks.Select(a => a.Body));
    }

    [Fact]
    public async Task An_unplaced_Nanoleaf_follows_the_one_colour_while_an_effect_plays()
    {
        _ = new FakeNanoleaf(_net);
        var settings = new LightingSettings();
        var store = new NetworkLightStore(() => settings, () => { }, new object(), new FakeProtector());
        using var lights = new NetworkLights(_net, store, NullLog.Instance, new INetworkLightProvider[] { new NanoleafProvider() });
        await lights.PairAsync("nanoleaf", FakeNanoleaf.Ip, null, default);
        lights.Frame(new NetworkFrame(Array.Empty<LayoutItem>(), null, 0, 0, c => c, 0x123456));
        await Eventually.True(() => _net.Sent.Any(s => s.Port == NanoleafClient.StreamPort), "a frame to reach the Nanoleaf");
        var frame = _net.Sent.First(s => s.Port == NanoleafClient.StreamPort).Data;
        // Three panels, every one the same colour: 12 34 56.
        Assert.Equal(3, frame[1]);
        for (var i = 0; i < 3; i++) Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, frame[(4 + i * 8)..(7 + i * 8)]);
        Assert.True(lights.IsStreaming("nanoleaf"));
    }

    // ---- WLED and LeetDesk

    private static PairedLight Wled() =>
        new("wled", FakeWled.Mac, "Shelf", "WLED", FakeWled.Ip, "net|wled|" + FakeWled.Mac, new Dictionary<string, string> { ["port"] = "80" });

    [Fact]
    public async Task WLED_is_set_through_its_json_state_and_given_back_its_look_but_never_its_sizes()
    {
        var p = new WledProvider();
        _ = new FakeWled(_net)
        {
            State = "{\"on\":true,\"bri\":128,\"mainseg\":0,\"pl\":3,\"seg\":[{\"id\":0,\"start\":0,\"stop\":30,\"grp\":1,\"fx\":0,\"sx\":128,\"pal\":2,\"col\":[[255,0,0],[0,0,0],[0,0,0]]}]}",
        };
        Assert.Equal(new LightReading(true, 50, 0xFF0000), await p.ReadAsync(_net, Wled(), default));
        var saved = await p.SaveAsync(_net, Wled(), default);

        await p.SetAsync(_net, Wled(), new LightChange(Brightness: 100, Colour: 0x00FF80), default);
        await p.SetAsync(_net, Wled(), new LightChange(On: false), default);
        await p.SetAsync(_net, Wled(), new LightChange(Brightness: 0), default);
        var posts = _net.Asks.Where(a => a.Method == "POST").ToList();
        Assert.All(posts, a => Assert.Equal("http://172.16.0.50/json/state", a.Url));
        Assert.Equal(new[]
        {
            "{\"on\":true,\"bri\":255,\"seg\":{\"fx\":0,\"col\":[[0,255,128]]}}",
            "{\"on\":false}",
            "{\"on\":true,\"bri\":1}",
        }, posts.Select(a => a.Body));

        while (_net.Asks.TryDequeue(out _)) { }
        await p.GiveBackAsync(_net, Wled(), saved, default);
        Assert.Equal(new[]
        {
            "{\"on\":true,\"bri\":128,\"seg\":[{\"id\":0,\"col\":[[255,0,0],[0,0,0],[0,0,0]],\"fx\":0,\"sx\":128,\"pal\":2}]}",
            "{\"ps\":3}", // the playlist that was running starts again
        }, _net.Asks.Select(a => a.Body));
    }

    [Fact]
    public async Task WLED_running_an_effect_has_no_one_colour()
    {
        _ = new FakeWled(_net) { State = "{\"on\":false,\"bri\":255,\"seg\":[{\"id\":0,\"fx\":9,\"col\":[[1,2,3]]}]}" };
        Assert.Equal(new LightReading(false, 100, null), await new WledProvider().ReadAsync(_net, Wled(), default));
    }

    private static PairedLight Desk() =>
        new("leetdesk", FakeAura.Mac, "aura-3d4e5f", "Aura", FakeAura.Ip, "net|leetdesk|" + FakeAura.Mac, new Dictionary<string, string> { ["port"] = "80" });

    [Fact]
    public async Task LeetDesk_takes_on_off_and_brightness_only_and_says_why_not_a_colour()
    {
        var p = new LeetDeskProvider();
        _ = new FakeAura(_net) { StateJson = "{\"on\":false,\"bri\":77}" };
        var can = p.Controls(Desk());
        Assert.True(can.Power.Can && can.Brightness.Can && can.ReadBack.Can && can.GiveBack.Can);
        Assert.False(can.Colour.Can);
        Assert.Contains("LeetDesk publishes nothing", can.Colour.Why);

        Assert.Equal(new LightReading(false, 30, null), await p.ReadAsync(_net, Desk(), default));
        var saved = await p.SaveAsync(_net, Desk(), default);
        await p.SetAsync(_net, Desk(), new LightChange(Brightness: 50), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.SetAsync(_net, Desk(), new LightChange(Colour: 0xFF0000), default));
        await p.GiveBackAsync(_net, Desk(), saved, default);
        Assert.Equal(new[] { "{\"on\":true,\"bri\":128}", "{\"on\":false,\"bri\":77,\"live\":false}" },
            _net.Asks.Where(a => a.Method == "POST").Select(a => a.Body));
        Assert.All(_net.Asks.Where(a => a.Method == "POST"), a => Assert.Equal("http://172.16.0.60/jsonaura/state/", a.Url));
    }

    // ---- Hue

    private static PairedLight Bridge() =>
        new("hue", FakeHueBridge.BridgeId, "Living room bridge", "BSB002", FakeHueBridge.Ip, "net|hue|" + FakeHueBridge.BridgeId,
            new Dictionary<string, string> { ["bridgeid"] = FakeHueBridge.BridgeId, ["cert"] = FakeHueBridge.Cert },
            new Dictionary<string, string> { ["token"] = FakeHueBridge.AppKey });

    [Fact]
    public async Task Hue_changes_only_the_lights_of_the_entertainment_area_and_gives_each_its_old_state_back()
    {
        var waits = new List<TimeSpan>();
        var p = new HueProvider { Wait = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        var bridge = new FakeHueBridge(_net);
        bridge.Lights.Add(new("light-a", "device-a", "Strip", On: true, Brightness: 40, X: 0.3, Y: 0.3));
        bridge.Lights.Add(new("light-b", "device-b", "Lamp", On: false, Mirek: 300));
        bridge.Lights.Add(new("light-c", "device-c", "Kitchen", On: true)); // outside the area: never touched
        bridge.Services["ent-a"] = "device-a";
        bridge.Services["ent-b"] = "device-b";
        bridge.Areas.Add(new("area-1", "screen", new FakeHueBridge.Channel(0, 0, 0, "ent-a", "ent-b")));
        Assert.False(p.JoinsEverythingAtOnce);

        var reading = await p.ReadAsync(_net, Bridge(), default);
        Assert.Equal((true, 40), (reading.On, reading.Brightness));
        var saved = await p.SaveAsync(_net, Bridge(), default);

        await p.SetAsync(_net, Bridge(), new LightChange(Brightness: 80, Colour: 0xFF0000), default);
        Assert.Equal(new[] { "light-a", "light-b" }, bridge.Puts.Select(x => x.Id));
        var body = JsonDocument.Parse(bridge.Puts[0].Body).RootElement;
        Assert.True(body.GetProperty("on").GetProperty("on").GetBoolean());
        Assert.Equal(80, body.GetProperty("dimming").GetProperty("brightness").GetDouble());
        Assert.InRange(body.GetProperty("color").GetProperty("xy").GetProperty("x").GetDouble(), 0.6, 0.7);
        Assert.Contains(TimeSpan.FromMilliseconds(100), waits); // the bridge's ten commands a second
        bridge.Puts.Clear();

        await p.SetAsync(_net, Bridge(), new LightChange(On: false), default);
        Assert.All(bridge.Puts, x => Assert.Equal("{\"on\":{\"on\":false}}", x.Body));
        bridge.Puts.Clear();

        await p.GiveBackAsync(_net, Bridge(), saved, default);
        Assert.Equal(new[]
        {
            ("light-a", "{\"on\":{\"on\":true},\"dimming\":{\"brightness\":40},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.3}},\"dynamics\":{\"duration\":400}}"),
            ("light-b", "{\"on\":{\"on\":false}}"),
        }, bridge.Puts);
        // The key went in a header, to the bridge only.
        Assert.All(_net.Asks.Where(a => a.Url.StartsWith("https://", StringComparison.Ordinal)), a => Assert.Equal(FakeHueBridge.AppKey, a.Headers!["hue-application-key"]));
    }

    [Fact]
    public void Hue_colours_turn_into_x_and_y_and_back()
    {
        foreach (var rgb in new[] { 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF, 0xFF8800 })
        {
            var (x, y) = HueProvider.ToXy(rgb, null);
            var back = HueProvider.FromXy(x, y);
            Assert.InRange(Math.Abs(((back >> 16) & 255) - ((rgb >> 16) & 255)), 0, 3);
            Assert.InRange(Math.Abs(((back >> 8) & 255) - ((rgb >> 8) & 255)), 0, 3);
            Assert.InRange(Math.Abs((back & 255) - (rgb & 255)), 0, 3);
        }
    }

    // ---- Govee

    private static PairedLight Govee() =>
        new("govee", "1F:80:C5:32:32:36:72:4E", "Govee H6159", "H6159", "172.16.0.40", "net|govee|x", new Dictionary<string, string> { ["sku"] = "H6159", ["segments"] = "1" });

    [Fact]
    public async Task Govee_is_sent_the_official_commands_and_never_waits_for_an_answer()
    {
        var p = new GoveeProvider(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.Zero);
        var can = p.Controls(Govee());
        Assert.True(can.Power.Can && can.Brightness.Can && can.Colour.Can);
        Assert.False(can.ReadBack.Can);
        Assert.False(can.GiveBack.Can);
        Assert.Contains("Govee Home app", can.GiveBack.Why);

        await p.SetAsync(_net, Govee(), new LightChange(Brightness: 60, Colour: 0x112233), default);
        await p.SetAsync(_net, Govee(), new LightChange(On: false), default);
        Assert.Equal(new[]
        {
            "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}",
            "{\"msg\":{\"cmd\":\"brightness\",\"data\":{\"value\":60}}}",
            "{\"msg\":{\"cmd\":\"colorwc\",\"data\":{\"color\":{\"r\":17,\"g\":34,\"b\":51},\"colorTemInKelvin\":0}}}",
            "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}",
        }, _net.Sent.Select(s => s.Text));
        Assert.All(_net.Sent, s => Assert.Equal(("172.16.0.40", 4003), (s.Host, s.Port)));
        // Sent from a port of its own: Govee's reply port (4002) is never opened for this.
        Assert.All(_net.Sent, s => Assert.NotEqual(4002, s.LocalPort));
    }

    // ---- Yeelight

    private static PairedLight Yee(string support = FakeYeelight.ColourSupport) =>
        new("yeelight", FakeYeelight.Id, "Desk lamp", "color", FakeYeelight.Ip, "net|yeelight|" + FakeYeelight.Id,
            new Dictionary<string, string> { ["port"] = "55443", ["support"] = support, ["model"] = "color" });

    private static YeelightProvider Yeelight() => new() { ReplyTime = TimeSpan.FromMilliseconds(500) };

    [Fact]
    public async Task Yeelight_uses_its_documented_commands_on_a_normal_connection_and_gives_back_its_scene()
    {
        var light = new FakeYeelight(_net);
        light.Props["power"] = "off";
        var p = Yeelight();
        Assert.Equal(TimeSpan.FromSeconds(2.5), p.ControlGap); // within the 144 commands a minute a light takes

        Assert.Equal(new LightReading(false, 80, 0x0000FF), await p.ReadAsync(_net, Yee(), default));
        var saved = await p.SaveAsync(_net, Yee(), default);

        await p.SetAsync(_net, Yee(), new LightChange(Colour: 0xFF0000), default);
        await p.SetAsync(_net, Yee(), new LightChange(Brightness: 25), default);
        await p.SetAsync(_net, Yee(), new LightChange(On: false), default);
        var lines = light.ControlLines.Select(l => JsonDocument.Parse(l).RootElement).Select(e => e.GetProperty("method").GetString() + " " + e.GetProperty("params").GetRawText()).ToList();
        Assert.Equal(new[]
        {
            "get_prop [\"power\",\"bright\",\"color_mode\",\"rgb\",\"hue\",\"sat\"]",
            "get_prop [\"power\",\"bright\",\"color_mode\",\"ct\",\"rgb\",\"hue\",\"sat\",\"flowing\",\"flow_params\"]",
            "get_prop [\"power\",\"bright\"]",
            "set_scene [\"color\",16711680,80]",
            "get_prop [\"power\",\"bright\"]",
            "set_power [\"on\",\"smooth\",300]",   // the fake stays off: it doesn't change its properties
            "set_bright [25,\"smooth\",300]",
            "set_power [\"off\",\"smooth\",300]",
        }, lines);
        Assert.Empty(_net.Listeners); // music mode (a listening port) is never used for this

        var before = light.Links.Count;
        await p.GiveBackAsync(_net, Yee(), saved, default);
        Assert.Equal("{\"id\":1,\"method\":\"set_power\",\"params\":[\"off\",\"smooth\",300]}", Assert.Single(FakeYeelight.Lines(light.Links.Skip(before).Single().WrittenText)));
    }

    [Fact]
    public async Task Yeelight_with_a_background_light_sets_both_and_says_when_it_refuses()
    {
        var light = new FakeYeelight(_net) { QuotaRefusals = 1 };
        var p = Yeelight();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.SetAsync(_net, Yee(FakeYeelight.BackgroundSupport), new LightChange(On: false), default));
        Assert.Equal("The Yeelight refused: client quota exceeded.", ex.Message);
        await p.SetAsync(_net, Yee(FakeYeelight.BackgroundSupport), new LightChange(On: false), default);
        Assert.Contains("\"method\":\"bg_set_power\"", light.ControlLines.Last());
    }

    // ---- Cololight

    [Fact]
    public async Task Cololight_reads_its_state_and_sends_the_captured_power_and_colour_packets()
    {
        var light = new FakeCololight(_net) { On = true, Brightness = 60, Shown = "00 10 20 30" };
        var p = new CololightProvider { AnswerWait = TimeSpan.FromMilliseconds(60) };
        var lamp = new PairedLight("cololight", "a1b2c3d4e5f6", "Cololight", "hexagon", FakeCololight.Ip, "net|cololight|a1b2c3d4e5f6",
            new Dictionary<string, string> { ["type"] = "hexagon" });

        Assert.Equal(new LightReading(true, 60, LightColour.Full(0x102030)), await p.ReadAsync(_net, lamp, default));
        var saved = await p.SaveAsync(_net, lamp, default);
        while (_net.Sent.TryDequeue(out _)) { }

        await p.SetAsync(_net, lamp, new LightChange(Brightness: 30, Colour: 0xFF0000), default);
        var bodies = _net.Sent.Select(s => Convert.ToHexString(s.Data[36..])).ToList();
        Assert.Equal(new[] { "03020100", "040103 01CF1E".Replace(" ", ""), "04010602FF00FF0000" }, bodies);
        while (_net.Sent.TryDequeue(out _)) { }

        await p.SetAsync(_net, lamp, new LightChange(On: false), default);
        Assert.Equal("040103 01CE3C".Replace(" ", ""), Convert.ToHexString(_net.Sent.Last().Data[36..])); // off, keeping its brightness
        while (_net.Sent.TryDequeue(out _)) { }

        await p.GiveBackAsync(_net, lamp, saved, default);
        Assert.Equal(new[] { "04010602FF00102030", "04010301CF3C" }, _net.Sent.Select(s => Convert.ToHexString(s.Data[36..])));
        Assert.All(_net.Sent, s => Assert.Equal((FakeCololight.Ip, 8900), (s.Host, s.Port)));
    }
}
