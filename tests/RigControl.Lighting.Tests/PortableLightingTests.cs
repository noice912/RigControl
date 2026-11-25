using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;
using RigControl.Lighting.Tests.Network;
using CoreSecrets = RigControl.Core.Platform.ISecretProtector;
using PlatformProtector = RigControl.Lighting.Network.PlatformProtector;

namespace RigControl.Lighting.Tests;

/// <summary>
/// Lighting as it is on Linux and macOS: an OpenRGB the user started (never looked for, never started), the lights on
/// the network, layout, scenes and the effects that need neither a picture nor sound. Everything is tried with
/// stand-ins, so these run on Windows too, in both flavours of the project. Nothing here was run on a real Linux or Mac.
/// </summary>
public class PortableLightingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-portable-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRgb _rgb = new() { CanStartServer = false };
    private readonly FakeLightNetwork _net = new();
    private readonly FakeLinks _links = new();
    private readonly ListLog _log = new();

    public PortableLightingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private sealed class FakeLinks : ILinkOpener
    {
        public List<string> Opened { get; } = new();
        public bool Works { get; set; } = true;
        public bool OpenHttpsUrl(string? url)
        {
            if (!Works || url is null) return false;
            Opened.Add(url);
            return true;
        }
        public void OpenFolder(string path) { }
    }

    /// <summary>The module as the Linux and macOS app makes it: connect-only OpenRGB, nothing to follow, no GCC.</summary>
    private async Task<LightingModule> Start(CoreSecrets? secrets = null)
    {
        var m = new LightingModule(_rgb, _ => false, background: false, new NoGigabyteControlCenter(), _net,
            new INetworkLightProvider[] { new NanoleafProvider() })
        { SyncIntervalMs = 10, Here = LightingSystem.Portable() };
        var platform = PlatformServices.Create(_dir, false) with { Links = _links, Secrets = secrets ?? new FileKeySecretProtector(_dir) };
        await m.StartAsync(new ModuleContext(new EventBus(), _log, new SettingsStore(SettingsPath, NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "rigcontrol")) { Platform = platform }, CancellationToken.None);
        return m;
    }

    private void OpenRgbIsRunning()
    {
        _rgb.Listening = true;
        _rgb.Devices.Add(FakeRgb.Keyboard(0));
        _rgb.Devices.Add(FakeRgb.Board(1));
    }

    private static async Task<JsonElement> Call(LightingModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task<RpcException> NotHere(LightingModule m, string method, object? args = null)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args));
        Assert.Equal("unavailable", e.Code);
        Assert.StartsWith(ModuleAvailability.NotOnThisSystem, e.Message);
        return e;
    }

    private bool SentToKeyboard()
    {
        lock (_rgb.Sent) return _rgb.Sent.Any(x => x.StartsWith("leds 0", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- what is offered

    [Fact]
    public async Task Only_the_five_parts_that_work_everywhere_are_offered()
    {
        await using var m = await Start();
        Assert.Equal(new[] { "lighting.effects", "lighting.layout", "lighting.network", "lighting.openrgb", "lighting.scenes" },
            ((IModuleCapabilities)m).Capabilities.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void A_system_with_nothing_to_follow_offers_the_effects_that_need_no_picture_and_no_sound()
    {
        var here = LightingSystem.Portable();
        Assert.Equal(new[] { "solid", "breathing", "cycle", "wave", "spiral", "gradient", "stars", "rain" }, here.Effects.Select(e => e.Id));
        Assert.All(EffectRenderer.Catalog.Where(e => e.Audio), e => Assert.False(here.Offers(e.Id)));
        Assert.False(here.Offers(EffectRenderer.Screen));
        Assert.False(here.HasCaptureCard);
        Assert.Equal("solid", here.EffectOr(EffectRenderer.Screen));
        Assert.Equal("solid", here.EffectOr("visualizer"));
        Assert.Equal("rain", here.EffectOr("rain"));
        Assert.True(EffectRenderer.Exists(LightingSystem.FallbackEffect));
    }

#if WINDOWS
    [Fact]
    public async Task The_windows_flavour_offers_everything()
    {
        var here = LightingSystem.ThisBuild();
        Assert.Equal(EffectRenderer.Catalog.Select(e => e.Id), here.Effects.Select(e => e.Id));
        Assert.True(here.HasCaptureCard);
        Assert.Equal("screen", here.EffectOr("screen"));
        await using var m = new LightingModule(new FakeRgb(), _ => false, background: false, new FakeGcc());
        Assert.Equal(new[]
        {
            "lighting.captureCard", "lighting.audio", "lighting.effects", "lighting.gcc", "lighting.layout", "lighting.network",
            "lighting.openrgb", "lighting.openrgbServer", "lighting.scenes", "lighting.screen",
        }.OrderBy(c => c, StringComparer.Ordinal), m.Capabilities.OrderBy(c => c, StringComparer.Ordinal));
    }
#else
    [Fact]
    public void The_flavour_for_linux_and_macos_has_nothing_to_follow_built_in()
    {
        var here = LightingSystem.ThisBuild();
        Assert.Null(here.Screen);
        Assert.Null(here.CaptureCard);
        Assert.Null(here.CaptureDevices);
        Assert.Null(here.Obs);
        Assert.Null(here.Audio);
        Assert.Equal(new[] { "lighting.openrgb", "lighting.network", "lighting.effects", "lighting.layout", "lighting.scenes" },
            here.Capabilities(startsOpenRgb: false, gcc: false));
    }
#endif

    // ---------------------------------------------------------------- OpenRGB is the user's own

    [Fact]
    public async Task OpenRGB_is_never_started_and_the_user_is_told_to_start_it()
    {
        // Even a path is ignored: this side only ever connects.
        _rgb.Executable = "/usr/bin/openrgb";
        await using var m = await Start();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        Assert.Contains("turn on its SDK server", m.Status.Message);
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(m, "startOpenRgb"));
        Assert.Equal("unavailable", e.Code);
        Assert.Contains("turn on its SDK server", e.Message);
        Assert.False(_rgb.Started);

        Assert.DoesNotContain(_log.Lines, l => l.Contains("auto-start failed", StringComparison.Ordinal));
        m.ConnectIfStarted(); // nothing answers yet: nothing happens
        Assert.Equal(ModuleState.Degraded, m.Status.State);

        // The user starts it; "Start OpenRGB" then only connects.
        OpenRgbIsRunning();
        var s = await Call(m, "startOpenRgb");
        Assert.True(s.GetProperty("openRgb").GetProperty("connected").GetBoolean());
        Assert.Equal(2, s.GetProperty("devices").GetArrayLength());
        Assert.Equal(ModuleState.Ready, m.Status.State);
        Assert.False(_rgb.Started);
    }

    [Fact]
    public async Task An_OpenRGB_that_already_runs_is_connected_at_start_and_colours_reach_it()
    {
        OpenRgbIsRunning();
        await using var m = await Start();
        Assert.Equal(ModuleState.Ready, m.Status.State);
        await Call(m, "setColor", new { color = "#FF0000" });
        Assert.Equal("#FF0000", OpenRgbProtocol.ColorToHex(_rgb.Devices[0].Colors[0]));
        Assert.False(_rgb.Started);
    }

    [Fact]
    public async Task An_OpenRGB_started_after_RigControl_is_picked_up_without_starting_anything()
    {
        await using var m = await Start();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        OpenRgbIsRunning();
        m.ConnectIfStarted(); // what the background loop does every 10 s
        Assert.Equal(ModuleState.Ready, m.Status.State);
        Assert.Equal(2, (await Call(m, "getStatus")).GetProperty("devices").GetArrayLength());
        Assert.False(_rgb.Started);

        // Where RigControl starts OpenRGB itself (Windows) this does nothing: starting stays the module's own business.
        var windows = new FakeRgb { Listening = true };
        await using var w = new LightingModule(windows, _ => false, background: false);
        w.ConnectIfStarted();
        Assert.False(windows.Connected);
    }

    [Fact]
    public async Task Getting_OpenRGB_opens_its_page_through_the_system_and_installs_nothing()
    {
        await using var m = await Start();
        var r = await Call(m, "installOpenRgb");
        Assert.True(r.GetProperty("started").GetBoolean());
        Assert.DoesNotContain("Windows", r.GetProperty("message").GetString());
        Assert.Equal(new[] { "https://openrgb.org/" }, _links.Opened);

        _links.Works = false;
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(m, "installOpenRgb"));
        Assert.Contains("openrgb.org", e.Message);

        _links.Works = true;
        _rgb.Listening = true;
        r = await Call(m, "installOpenRgb");
        Assert.False(r.GetProperty("started").GetBoolean());
        Assert.Single(_links.Opened);
    }

    // ---------------------------------------------------------------- effects

    [Fact]
    public async Task The_screen_capture_cards_and_sound_are_left_out_of_the_lists()
    {
        OpenRgbIsRunning();
        await using var m = await Start();
        foreach (var method in new[] { "getSync", "listSyncSources" })
        {
            var s = await Call(m, method);
            var effects = s.GetProperty("effects").EnumerateArray().ToList();
            Assert.Equal(8, effects.Count);
            Assert.DoesNotContain(effects, e => e.GetProperty("id").GetString() == "screen" || e.GetProperty("audio").GetBoolean());
            Assert.Equal(effects.Select(e => e.GetProperty("id").GetString()), s.GetProperty("effectParams").EnumerateObject().Select(p => p.Name));
            Assert.Equal(0, s.GetProperty("sources").GetArrayLength());
            // The saved effect is still the default ("screen"), which isn't offered here: a plain colour stands in.
            Assert.Equal("solid", s.GetProperty("effect").GetString());
            Assert.Equal("", s.GetProperty("source").GetString());
        }
    }

    [Fact]
    public async Task Asking_for_a_part_that_is_not_here_says_so_and_changes_nothing()
    {
        OpenRgbIsRunning();
        await using var m = await Start();
        await NotHere(m, "setSync", new { effect = "screen" });
        foreach (var audio in new[] { "visualizer", "ripple", "audiocolor" }) await NotHere(m, "setSync", new { effect = audio, enabled = true });
        await NotHere(m, "setSync", new { bass = true });
        await NotHere(m, "setSync", new { mid = true });
        await NotHere(m, "setSync", new { treble = true, brightness = 10 });
        await NotHere(m, "setSync", new { source = "a-capture-card" });

        var s = await Call(m, "getSync");
        Assert.False(s.GetProperty("enabled").GetBoolean());
        Assert.Equal(100, s.GetProperty("brightness").GetInt32());
        Assert.False(s.GetProperty("bass").GetBoolean());
        Assert.False(SentToKeyboard());
        // Turning them off (what a page does when it saves everything it shows) is fine, and an unknown effect is still a mistake.
        await Call(m, "setSync", new { bass = false, mid = false, treble = false, source = "" });
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setSync", new { effect = "no-such-effect" }));
        Assert.Equal("invalidArgs", e.Code);
    }

    [Fact]
    public async Task An_effect_plays_through_OpenRGB_and_the_lights_are_put_back_after()
    {
        OpenRgbIsRunning();
        await using var m = await Start();
        var s = await Call(m, "setSync", new { effect = "wave", enabled = true });
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal("wave", s.GetProperty("effect").GetString());
        await Eventually.True(SentToKeyboard, "the keyboard to get a colour");
        await Eventually.True(() => Call(m, "getSync").GetAwaiter().GetResult().GetProperty("status").GetString()!.StartsWith("Playing Rainbow wave", StringComparison.Ordinal),
            "the status to say what plays");
        Assert.NotNull(await m.InvokeAsync("getSyncPreview", null, CancellationToken.None));

        s = await Call(m, "setSync", new { enabled = false });
        Assert.False(s.GetProperty("enabled").GetBoolean());
        Assert.All(_rgb.Devices[0].Colors, c => Assert.Equal(0xFFu, c));
        Assert.Equal(0xFF00u, _rgb.Devices[1].Modes[0].Colors[0]);
        Assert.False(_rgb.Started);
    }

    [Fact]
    public async Task Turning_the_lights_on_with_nothing_chosen_plays_a_plain_colour()
    {
        OpenRgbIsRunning();
        await using var m = await Start();
        await Call(m, "setSync", new { enabled = true });
        await Eventually.True(SentToKeyboard, "the keyboard to get a colour");
        await Eventually.True(() => Call(m, "getSync").GetAwaiter().GetResult().GetProperty("status").GetString()!.StartsWith("Playing Solid colour", StringComparison.Ordinal),
            "the status to say what plays");
        await Call(m, "setSync", new { enabled = false });
    }

    [Fact]
    public async Task Settings_that_ask_for_sound_or_a_capture_card_are_played_without_them()
    {
        // As a settings file edited by hand, or carried over from a Windows PC, could say.
        var settings = new LightingSettings();
        settings.Sync.Effect = "visualizer";
        settings.Sync.Bass = settings.Sync.Mid = settings.Sync.Treble = true;
        settings.Sync.Source = "a-capture-card";
        settings.Sync.SourceName = "USB Video";
        new SettingsStore(SettingsPath, NullLog.Instance).Set(LightingSettings.Section, settings);

        OpenRgbIsRunning();
        await using var m = await Start();
        var s = await Call(m, "setSync", new { enabled = true });
        Assert.Equal("solid", s.GetProperty("effect").GetString());
        Assert.False(s.GetProperty("bass").GetBoolean() || s.GetProperty("mid").GetBoolean() || s.GetProperty("treble").GetBoolean());
        Assert.Equal("", s.GetProperty("source").GetString());
        Assert.Equal(0, s.GetProperty("sources").GetArrayLength());
        await Eventually.True(SentToKeyboard, "the keyboard to get a colour");
        await Eventually.True(() => Call(m, "getSync").GetAwaiter().GetResult().GetProperty("status").GetString()!.StartsWith("Playing Solid colour", StringComparison.Ordinal),
            "the status to say what plays");
        Assert.Equal(JsonValueKind.Null, (await Call(m, "getSync")).GetProperty("bassProblem").ValueKind);
        await Call(m, "setSync", new { enabled = false });
        Assert.DoesNotContain(_log.Lines, l => l.Contains("can't be heard", StringComparison.Ordinal) || l.Contains("capture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scenes_and_the_layout_work_as_everywhere()
    {
        OpenRgbIsRunning();
        await using var m = await Start();
        await Call(m, "setColor", new { color = "#00FF00" });
        Assert.Equal(1, (await Call(m, "saveScene", new { name = "Green" })).GetArrayLength());
        await Call(m, "setColor", new { color = "#FF0000" });
        await Call(m, "applyScene", new { name = "Green" });
        Assert.Equal("#00FF00", OpenRgbProtocol.ColorToHex(_rgb.Devices[0].Colors[0]));

        var key = _rgb.Devices[0].Key;
        var layout = await Call(m, "setLayout", new { perLed = true, items = new[] { new { key, zone = -1, x = 0.1, y = 0.1, w = 0.5, h = 0.2, reverse = false } } });
        Assert.True(layout.GetProperty("perLed").GetBoolean());
        Assert.Contains(layout.GetProperty("zones").EnumerateArray(), z => z.GetProperty("key").GetString() == key && z.GetProperty("placed").ValueKind == JsonValueKind.Object);
    }

    // ---------------------------------------------------------------- GIGABYTE Control Center

    [Fact]
    public async Task GIGABYTE_Control_Center_reads_as_not_installed_and_cannot_be_taken_over()
    {
        await using var m = await Start();
        var g = await Call(m, "getGcc");
        Assert.False(g.GetProperty("installed").GetBoolean() || g.GetProperty("running").GetBoolean());
        await NotHere(m, "takeOverGcc");
        await NotHere(m, "giveBackGcc");
    }

    // ---------------------------------------------------------------- pairing keys

    private sealed class RecordingSecrets : CoreSecrets
    {
        public List<(string Data, string Purpose)> Protected { get; } = new();
        public string Description => "test";
        public byte[] Protect(byte[] data, byte[] purpose)
        {
            Protected.Add((Encoding.UTF8.GetString(data), Encoding.UTF8.GetString(purpose)));
            return purpose.Concat(new byte[] { 0 }).Concat(Enumerable.Reverse(data)).ToArray();
        }
        public byte[] Unprotect(byte[] protectedData, byte[] purpose)
        {
            if (!protectedData.AsSpan().StartsWith(purpose) || protectedData.Length <= purpose.Length || protectedData[purpose.Length] != 0)
                throw new CryptographicException("not for this purpose");
            return protectedData.Skip(purpose.Length + 1).Reverse().ToArray();
        }
    }

    [Fact]
    public void Keys_go_to_the_systems_protector_as_bytes_with_the_purpose_and_come_back_as_text()
    {
        var core = new RecordingSecrets();
        var p = new PlatformProtector(core);
        var stored = p.Protect("tok-1", "RigControl.NetworkLight.hue.token");
        Assert.Equal(("tok-1", "RigControl.NetworkLight.hue.token"), Assert.Single(core.Protected));
        Assert.DoesNotContain("tok-1", stored);
        Assert.Equal("tok-1", p.Unprotect(stored, "RigControl.NetworkLight.hue.token"));
        // Anything that can't be read is "no key" (the light is paired again), never an error and never half a key.
        Assert.Null(p.Unprotect(stored, "RigControl.NetworkLight.wled.token"));
        Assert.Null(p.Unprotect("not base64 at all !", "x"));
        Assert.Null(p.Unprotect("", "x"));
    }

    [Fact]
    public void The_key_file_used_on_linux_and_macos_keeps_a_token_and_ties_it_to_its_purpose()
    {
        var p = new PlatformProtector(new FileKeySecretProtector(_dir));
        var n = new NanoleafSettings();
        n.SetToken("nl-token", p);
        Assert.DoesNotContain("nl-token", n.ProtectedToken);
        Assert.Equal("nl-token", n.Token(p));
        Assert.Equal("nl-token", n.Token(new PlatformProtector(new FileKeySecretProtector(_dir)))); // read again from the key file
        Assert.Null(p.Unprotect(n.ProtectedToken, "RigControl.NetworkLight.hue.token"));
        // Without the key file (the settings copied to another computer) the token reads as "not paired".
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Assert.Null(n.Token(new PlatformProtector(new FileKeySecretProtector(elsewhere))));
        Assert.Null(new NanoleafSettings().Token(p));
    }

    [Fact]
    public async Task A_light_paired_with_the_key_file_is_saved_without_its_key_and_read_back_after_a_restart()
    {
        _ = new FakeNanoleaf(_net);
        await using (var m = await Start())
        {
            var s = await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });
            Assert.True(s.GetProperty("paired").GetBoolean());
            Assert.Equal(3, s.GetProperty("panels").GetInt32());
        }
        var file = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain(FakeNanoleaf.Token, file);
        Assert.True(File.Exists(Path.Combine(_dir, FileKeySecretProtector.KeyFileName)));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(FakeNanoleaf.Token, StringComparison.Ordinal));

        // A new start (a new module, the same folder): the token is read with the key file, so the panels answer.
        await using var again = await Start();
        Assert.True((await Call(again, "getNanoleaf")).GetProperty("paired").GetBoolean());
        await Eventually.True(() => Call(again, "getNanoleaf").GetAwaiter().GetResult().GetProperty("panels").GetInt32() == 3, "its panels to be read");
        // Every request went to the address it was paired at.
        Assert.All(_net.Asks, a => Assert.StartsWith("http://172.16.0.70:16021/", a.Url));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(FakeNanoleaf.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_module_keeps_keys_with_whatever_protector_the_app_hands_it()
    {
        var core = new RecordingSecrets();
        _ = new FakeNanoleaf(_net);
        await using var m = await Start(core);
        await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip });
        Assert.Equal((FakeNanoleaf.Token, NanoleafSettings.TokenPurpose), Assert.Single(core.Protected));
    }

    [Fact]
    public void Keys_saved_by_older_versions_on_windows_still_read_and_new_ones_read_the_old_way()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows' own protection (DPAPI)

        // Exactly the calls the lighting code made before it went through the system's protector.
        static string Old(string secret, string entropy) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser));
        static string OldRead(string stored, string entropy) =>
            Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser));

        // What the module uses when it is given no protector: the standard set for this system.
        var now = new PlatformProtector(PlatformServices.Create(_dir, false).Secrets);
        var settings = new LightingSettings
        {
            Nanoleaf = new NanoleafSettings { Ip = "172.16.0.70", Name = "Lines", Serial = "S1", ProtectedToken = Old("old-nl-token", "RigControl.Nanoleaf") },
            NetworkLights =
            {
                new StoredNetworkLight
                {
                    Brand = "hue", Id = "bridge1", Name = "Hue", Address = "172.16.0.80",
                    ProtectedSecrets = { ["token"] = Old("old-hue-key", "RigControl.NetworkLight.hue.token") },
                },
            },
        };
        var store = new NetworkLightStore(() => settings, () => { }, new object(), now);
        Assert.Equal("old-nl-token", store.Find("nanoleaf", "S1")!.RequireSecret().RevealFor("172.16.0.70"));
        Assert.Equal("old-hue-key", store.Find("hue", "bridge1")!.RequireSecret().RevealFor("172.16.0.80"));
        // The store's own stand-in before the module has started reads them the same way.
        var early = new NetworkLightStore(() => settings, () => { }, new object());
        Assert.Equal("old-nl-token", early.Find("nanoleaf", "S1")!.RequireSecret().RevealFor("172.16.0.70"));

        // And the other way round: what is saved now, an older version reads.
        store.Save("nanoleaf", new PairResult("S2", "Shapes", "", "172.16.0.71") { Secrets = new Dictionary<string, string> { ["token"] = "new-nl-token" } });
        store.Save("hue", new PairResult("bridge2", "Hue 2", "", "172.16.0.81") { Secrets = new Dictionary<string, string> { ["token"] = "new-hue-key" } });
        Assert.Equal("new-nl-token", OldRead(settings.Nanoleaf!.ProtectedToken, "RigControl.Nanoleaf"));
        Assert.Equal("new-hue-key", OldRead(settings.NetworkLights.Single(l => l.Id == "bridge2").ProtectedSecrets["token"], "RigControl.NetworkLight.hue.token"));
        // No key file is made on Windows.
        Assert.False(File.Exists(Path.Combine(_dir, FileKeySecretProtector.KeyFileName)));
    }

    [Fact]
    public async Task On_linux_and_macos_the_standard_set_keeps_keys_in_a_file_only_the_user_can_read()
    {
        if (OperatingSystem.IsWindows()) return; // file modes are a Linux/macOS thing; this runs on the CI machines

        _ = new FakeNanoleaf(_net);
        var m = new LightingModule(_rgb, _ => false, background: false, new NoGigabyteControlCenter(), _net,
            new INetworkLightProvider[] { new NanoleafProvider() }) { Here = LightingSystem.Portable() };
        await using (m)
        {
            // No Platform given: the context makes the standard set for this system, as the app does.
            await m.StartAsync(new ModuleContext(new EventBus(), _log, new SettingsStore(SettingsPath, NullLog.Instance),
                new AppEnvironment(_dir, _dir, false, "1.0.0", "rigcontrol")), CancellationToken.None);
            Assert.True((await Call(m, "pairNanoleaf", new { ip = FakeNanoleaf.Ip })).GetProperty("paired").GetBoolean());
        }
        var key = Path.Combine(_dir, FileKeySecretProtector.KeyFileName);
        Assert.True(File.Exists(key));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        Assert.DoesNotContain(FakeNanoleaf.Token, File.ReadAllText(SettingsPath));
    }
}

/// <summary>
/// The connection to OpenRGB's SDK server, against a small stand-in server on a free port of this computer.
/// The real OpenRGB (port 6742) is never touched.
/// </summary>
public class OpenRgbClientTests
{
    /// <summary>Answers the few requests the client makes; records what it was sent.</summary>
    private sealed class StandInServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly int _version;
        public List<(uint Device, uint Id, byte[] Payload)> Got { get; } = new();
        public int Port { get; }
        /// <summary>Sent ahead of the next answer, as OpenRGB does when a device is plugged in.</summary>
        public volatile bool AnnounceListChange;

        public StandInServer(int version = 4)
        {
            _version = version;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch { /* stopped */ }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var header = new byte[OpenRgbProtocol.HeaderSize];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(header, _stop.Token);
                        if (!OpenRgbProtocol.TryParseHeader(header, out var device, out var id, out var size)) return;
                        var payload = new byte[size];
                        if (size > 0) await stream.ReadExactlyAsync(payload, _stop.Token);
                        lock (Got) Got.Add((device, id, payload));
                        if (AnnounceListChange)
                        {
                            AnnounceListChange = false;
                            await stream.WriteAsync(OpenRgbProtocol.Packet(0, OpenRgbProtocol.DeviceListUpdated), _stop.Token);
                        }
                        if (id == OpenRgbProtocol.RequestProtocolVersion)
                            await stream.WriteAsync(OpenRgbProtocol.Packet(0, id, BitConverter.GetBytes((uint)_version)), _stop.Token);
                        else if (id == OpenRgbProtocol.RequestControllerCount)
                            await stream.WriteAsync(OpenRgbProtocol.Packet(0, id, BitConverter.GetBytes(0u)), _stop.Token);
                    }
                }
            }
            catch { /* the client went away, or the test ended */ }
        }

        public bool GotId(uint id)
        {
            lock (Got) return Got.Any(g => g.Id == id);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task It_connects_to_a_running_server_reads_its_devices_and_sends_colours()
    {
        using var server = new StandInServer();
        using var client = new OpenRgbClient(server.Port);
        Assert.True(client.IsListening());
        Assert.False(client.Connected);
        client.Connect();
        Assert.True(client.Connected);
        Assert.Empty(client.ReadControllers());
        Assert.False(client.DeviceListChanged());

        client.UpdateLeds(0, new uint[] { 0xFF, 0xFF00 });
        await Eventually.True(() => server.GotId(OpenRgbProtocol.UpdateLeds), "the colours to arrive");
        Assert.True(server.GotId(OpenRgbProtocol.SetClientName));

        // A device was plugged in: the server says so ahead of its next answer, and the client notes it once.
        server.AnnounceListChange = true;
        Assert.Empty(client.ReadControllers());
        Assert.True(client.DeviceListChanged());
        Assert.False(client.DeviceListChanged());

        client.Disconnect();
        Assert.False(client.Connected);
        Assert.Throws<IOException>(() => client.UpdateLeds(0, new uint[] { 0 }));
    }

    [Fact]
    public void It_never_looks_for_or_starts_OpenRGB()
    {
        using var server = new StandInServer();
        using var client = new OpenRgbClient(server.Port);
        Assert.False(client.CanStartServer);
        Assert.Null(client.FindExecutable());
        Assert.False(client.StartServer("openrgb"));
        Assert.False(client.OwnsServer);
        Assert.Equal(6742, OpenRgbClient.Port);
    }

    [Fact]
    public void A_server_that_is_too_old_or_not_there_is_said_plainly()
    {
        using (var old = new StandInServer(version: 2))
        {
            using var client = new OpenRgbClient(old.Port);
            Assert.Contains("too old", Assert.Throws<IOException>(client.Connect).Message);
            Assert.False(client.Connected);
        }

        // A port nothing listens on (taken for a moment to find a free one, then let go).
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var nobody = new OpenRgbClient(free);
        Assert.False(nobody.IsListening());
        Assert.ThrowsAny<Exception>(nobody.Connect);
        Assert.False(nobody.Connected);
    }
}
