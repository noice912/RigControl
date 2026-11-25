using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Photino.NET;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Hosting;

namespace RigControl.Desktop.Tests;

/// <summary>Stands in for the window: remembers what it was told.</summary>
internal sealed class FakeWindow : IAppWindow
{
    public ConcurrentQueue<string> Sent { get; } = new();
    public ConcurrentQueue<Uri> Opened { get; } = new();
    public int Closed;

    /// <summary>Like the real window: a close asked for before the window exists does nothing.</summary>
    public volatile bool Created = true;
    public int ClosedTooEarly;

    public void Send(string json) => Sent.Enqueue(json);
    public void Navigate(Uri address) => Opened.Enqueue(address);
    public void Close()
    {
        if (Created) Interlocked.Increment(ref Closed);
        else Interlocked.Increment(ref ClosedTooEarly);
    }

    public JsonElement? Answer(long id)
    {
        foreach (var json in Sent)
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt64() == id) return root;
        }
        return null;
    }
}

/// <summary>A module that works and counts what happened to it.</summary>
internal sealed class CountingModule : IAppModule
{
    public CountingModule(string name) => Name = name;
    public string Name { get; }
    public string DisplayName => Name;
    public ModuleStatus Status { get; private set; } = ModuleStatus.NotStarted;
    public int Started;
    public int Stopped;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Started);
        Status = new ModuleStatus(ModuleState.Ready);
        return Task.CompletedTask;
    }

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
        => Task.FromResult<object?>(new { method });

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref Stopped);
        Status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAutostart : IAutostart
{
    public bool Supported { get; init; }
    public bool Enabled;
    public bool IsEnabled() => Enabled;
    public string? SetEnabled(bool enabled, string executablePath)
    {
        Enabled = enabled;
        return null;
    }
}

public class DesktopSessionTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public TempFolder Folder { get; } = new();
        public FakeWindow Window { get; } = new();
        public CountingModule Lighting { get; } = new("lighting");
        public CountingModule Presence { get; } = new("presence");
        public int MakersCalled;
        public HostRuntime Runtime { get; }
        public DesktopSession Session { get; }

        public Rig(StartOptions options, bool autostart = false, HostPlatform platform = HostPlatform.Linux, TimeSpan? pageWait = null,
            string? storedMode = null, TimeSpan? pageSlowLimit = null, Func<long>? clockMs = null)
        {
            var paths = new AppPaths(Folder.Path, Path.Combine(Folder.Path, "logs"));
            if (storedMode is not null)
                File.WriteAllText(paths.SettingsFile, "{\"desktop\":{\"uiMode\":\"" + storedMode + "\"}}");
            // The standard parts, but saying "Linux" (or macOS) whatever system the test runs on, and with a
            // start-at-sign-in that writes nothing.
            var standard = PlatformServices.Create(Folder.Path, isElevated: false);
            var services = standard with { Platform = platform, Autostart = new FakeAutostart { Supported = autostart } };
            Runtime = HostRuntime.Create(paths, "9.9.9", "/opt/rigcontrol/RigControl", services);
            var makers = new KeyValuePair<string, Func<IAppModule>>[]
            {
                new("lighting", () => { Interlocked.Increment(ref MakersCalled); return Lighting; }),
                new("presence", () => { Interlocked.Increment(ref MakersCalled); return Presence; }),
            };
            Session = new DesktopSession(Runtime, options, Window, makers, pageWait, pageSlowLimit, clockMs);
        }

        /// <summary>A message as RigControl's own page sends it: this run's key in front.</summary>
        public void Say(string json) => Session.FromPage(Session.PageKeyValue + json);

        public async Task<JsonElement> CallAsync(long id, string method, string paramsJson = "null")
        {
            Say($"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}");
            Assert.True(await Wait.UntilAsync(() => Window.Answer(id) is not null), method + " was not answered");
            return Window.Answer(id)!.Value;
        }

        public string SettingsText() => File.Exists(Path.Combine(Folder.Path, "settings.json")) ? File.ReadAllText(Path.Combine(Folder.Path, "settings.json")) : "";

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Folder.Dispose();
        }
    }

    private static readonly StartOptions Normal = new(Smoke: false, Ui: null, Help: false, Version: false, Unknown: null);
    private static readonly StartOptions Smoke = Normal with { Smoke = true };

    [Fact]
    public async Task The_page_asks_for_getInfo_and_gets_this_systems_answer()
    {
        await using var rig = new Rig(Normal);
        rig.Session.WindowCreated();
        var answer = await rig.CallAsync(1, "app.getInfo");
        Assert.True(answer.GetProperty("ok").GetBoolean());
        var info = answer.GetProperty("result");
        Assert.Equal("9.9.9", info.GetProperty("version").GetString());
        Assert.Equal("linux", info.GetProperty("platform").GetString());
        var caps = info.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("lighting", caps);
        Assert.Contains("presence", caps);
        Assert.DoesNotContain("sensors", caps);
        Assert.DoesNotContain("cooling", caps);
        Assert.DoesNotContain(caps, c => c!.StartsWith("host."));
    }

    [Fact]
    public async Task Start_at_sign_in_is_offered_only_where_it_can_be_set_up()
    {
        await using var with = new Rig(Normal, autostart: true);
        Assert.Equal(new[] { HostCapabilities.Autostart }, with.Session.Features);
        await using var without = new Rig(Normal, autostart: false);
        Assert.Empty(without.Session.Features);
    }

    [Fact]
    public async Task Everything_but_lighting_and_Discord_status_says_not_available_on_this_system()
    {
        await using var rig = new Rig(Normal);
        var modules = (await rig.CallAsync(1, "app.getModules")).GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(ModuleCatalog.All.Count, modules.Count);
        foreach (var m in modules)
        {
            var name = m.GetProperty("name").GetString();
            if (name is "lighting" or "presence") continue;
            Assert.Equal("failed", m.GetProperty("state").GetString());
            Assert.StartsWith(ModuleAvailability.NotOnThisSystem, m.GetProperty("message").GetString());
        }
        var refused = await rig.CallAsync(2, "cooling.getStatus");
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("unavailable", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.StartsWith(ModuleAvailability.NotOnThisSystem, refused.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_real_modules_are_started_called_and_stopped_before_the_app_ends()
    {
        var rig = new Rig(Normal);
        rig.Session.StartModules();
        Assert.True(await Wait.UntilAsync(() => rig.Lighting.Started == 1 && rig.Presence.Started == 1));
        var answer = await rig.CallAsync(5, "lighting.getStatus");
        Assert.Equal("getStatus", answer.GetProperty("result").GetProperty("method").GetString());
        await rig.Session.DisposeAsync();
        Assert.Equal(1, rig.Lighting.Stopped);
        Assert.Equal(1, rig.Presence.Stopped);
        await rig.DisposeAsync();
        Assert.Equal(1, rig.Lighting.Stopped);
    }

    [Fact]
    public async Task Events_reach_the_page_only_once_it_has_spoken()
    {
        await using var rig = new Rig(Normal);
        rig.Runtime.Services.Events.Publish("lighting.changed", new { early = true });
        await Task.Delay(100);
        Assert.Empty(rig.Window.Sent);
        await rig.CallAsync(1, "app.getInfo");
        rig.Runtime.Services.Events.Publish("lighting.changed", new { early = false });
        Assert.True(await Wait.UntilAsync(() => rig.Window.Sent.Any(s => s.Contains("\"event\":\"lighting.changed\""))));
    }

    [Fact]
    public async Task Nothing_is_sent_to_a_page_that_has_gone_quiet_until_RigControls_own_page_speaks_again()
    {
        long now = 0;
        await using var rig = new Rig(Normal, clockMs: () => Interlocked.Read(ref now));
        await rig.CallAsync(1, "app.getInfo");

        // Longer than the limit without a keyed message: the window may be showing some other page now.
        Interlocked.Exchange(ref now, (long)DesktopSession.PageQuietLimit.TotalMilliseconds + 1000);
        rig.Runtime.Services.Events.Publish("lighting.changed", new { secret = "quiet" });
        await Task.Delay(200);
        Assert.DoesNotContain(rig.Window.Sent, m => m.Contains("quiet"));

        // A message without the key (another page) does not wake it up.
        rig.Session.FromPage("{\"note\":\"alive\"}");
        rig.Runtime.Services.Events.Publish("lighting.changed", new { secret = "stranger" });
        await Task.Delay(200);
        Assert.DoesNotContain(rig.Window.Sent, m => m.Contains("stranger"));

        // RigControl's own page says it is still there: messages flow again.
        rig.Say(DesktopSession.AliveNote);
        rig.Runtime.Services.Events.Publish("lighting.changed", new { secret = "back" });
        Assert.True(await Wait.UntilAsync(() => rig.Window.Sent.Any(m => m.Contains("back"))));
        Assert.DoesNotContain(rig.Runtime.Services.Log.Recent(), l => l.Message.Contains("alive") && l.Message.Contains("method"));
    }

    [Fact]
    public async Task The_still_here_note_is_not_treated_as_a_call()
    {
        await using var rig = new Rig(Normal);
        rig.Say(DesktopSession.AliveNote);
        await Task.Delay(200);
        Assert.Empty(rig.Window.Sent); // no answer, no error back to the page
    }

    [Fact]
    public async Task Quit_from_the_page_closes_the_window_once_and_after_closing_nothing_is_sent()
    {
        await using var rig = new Rig(Normal);
        var answer = await rig.CallAsync(1, "app.quit");
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1));
        rig.Session.RequestExit();
        await Task.Delay(100);
        Assert.Equal(1, rig.Window.Closed);

        rig.Session.WindowClosing();
        var before = rig.Window.Sent.Count;
        rig.Say("{\"id\":9,\"method\":\"app.getInfo\",\"params\":null}");
        await Task.Delay(200);
        Assert.Equal(before, rig.Window.Sent.Count);
    }

    [Fact]
    public async Task Closing_asked_for_before_the_window_exists_happens_as_soon_as_it_does()
    {
        // Ctrl+C during start-up: the window is still being made, so there is nothing to close yet.
        await using var rig = new Rig(Normal, pageWait: TimeSpan.FromMilliseconds(60));
        rig.Window.Created = false;
        rig.Session.RequestExit();
        Assert.True(await Wait.UntilAsync(() => rig.Window.ClosedTooEarly == 1));
        Assert.Equal(0, rig.Window.Closed);

        rig.Window.Created = true;
        rig.Session.WindowCreated();
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1), "the window stayed open");
        // A second Ctrl+C changes nothing, and no page is waited for or loaded another way.
        rig.Session.RequestExit();
        await Task.Delay(250);
        Assert.Equal(1, rig.Window.Closed);
        Assert.Empty(rig.Window.Opened);
        Assert.Equal(0, rig.Session.ExitCode);
    }

    // ----- only RigControl's own page is listened to -----

    [Fact]
    public async Task A_message_without_this_runs_key_is_dropped_and_does_not_count_as_the_page()
    {
        await using var rig = new Rig(Normal, pageWait: TimeSpan.FromMilliseconds(150));
        rig.Session.WindowCreated();
        // What another page in the window could send: a plain call, a wrong key, a key cut short, a key alone.
        var wrongKey = new string('0', PageKey.Length);
        rig.Session.FromPage("{\"id\":1,\"method\":\"app.quit\",\"params\":null}");
        rig.Session.FromPage(wrongKey + "{\"id\":2,\"method\":\"app.quit\",\"params\":null}");
        rig.Session.FromPage(rig.Session.PageKeyValue[..^1] + "{\"id\":3,\"method\":\"app.quit\",\"params\":null}");
        rig.Session.FromPage(rig.Session.PageKeyValue.ToUpperInvariant() + "{\"id\":4,\"method\":\"app.setSettings\",\"params\":{\"temperatureUnit\":\"F\"}}");
        rig.Session.FromPage(rig.Session.PageKeyValue);

        // Nothing is answered, nothing is closed, no event starts to flow.
        rig.Runtime.Services.Events.Publish("lighting.changed", new { });
        await Task.Delay(100);
        Assert.Empty(rig.Window.Sent);
        Assert.Equal(0, rig.Window.Closed);
        Assert.DoesNotContain("temperatureUnit", rig.SettingsText());
        // It does not count as "the page connected" either: the next way of loading the page is still tried.
        Assert.True(await Wait.UntilAsync(() => rig.Window.Opened.Count >= 1));
        Assert.Contains("never sent a message", rig.Session.Probe.WhereItStopped(true));
        // Said once in the log, without the key and without the message.
        var notes = rig.Runtime.Services.Log.Recent().Where(l => l.Message.Contains("did not come from RigControl's own page")).ToList();
        Assert.Single(notes);
        Assert.DoesNotContain(rig.Runtime.Services.Log.Recent(), l => l.Message.Contains(rig.Session.PageKeyValue));

        // The real page, with the key, is answered as always.
        var answer = await rig.CallAsync(5, "app.getInfo");
        Assert.True(answer.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task The_key_is_new_for_every_run_and_is_in_the_page_the_window_gets()
    {
        await using var one = new Rig(Normal);
        await using var two = new Rig(Normal);
        Assert.Matches("^[0-9a-f]{64}$", one.Session.PageKeyValue);
        Assert.NotEqual(one.Session.PageKeyValue, two.Session.PageKeyValue);
        if (one.Session.Site.ForSchemeUrl("app://rigcontrol/index.html") is { } page) // absent only in a build made without the interface
        {
            var html = System.Text.Encoding.UTF8.GetString(page.Body);
            Assert.Contains($"<meta name=\"{PageKey.MetaName}\" content=\"{one.Session.PageKeyValue}\" />", html);
            Assert.DoesNotContain(two.Session.PageKeyValue, html);
        }
    }

    [Fact]
    public async Task Windows_only_calls_answer_not_available_and_the_page_can_change_the_temperature_unit()
    {
        await using var rig = new Rig(Normal);
        foreach (var (id, method) in new[] { (1L, "app.restartAsAdmin"), (2L, "app.getFirstRun"), (3L, "app.getBundled"), (4L, "app.pickFile") })
        {
            var refused = await rig.CallAsync(id, method);
            Assert.Equal("unavailable", refused.GetProperty("error").GetProperty("code").GetString());
        }
        var set = await rig.CallAsync(10, "app.setSettings", "{\"temperatureUnit\":\"C\"}");
        Assert.Equal("C", set.GetProperty("result").GetProperty("temperatureUnit").GetString());
        Assert.False(set.GetProperty("result").GetProperty("minimizeToTray").GetBoolean());
    }

    // ----- the page and the ways of loading it -----

    [Fact]
    public async Task The_window_starts_on_RigControls_own_address()
    {
        await using var rig = new Rig(Normal);
        Assert.Equal("app://rigcontrol/index.html", rig.Session.StartAddress.ToString());
    }

    [Fact]
    public async Task A_page_that_stays_silent_is_reopened_the_next_way_and_that_way_is_remembered()
    {
        await using var rig = new Rig(Normal, pageWait: TimeSpan.FromMilliseconds(120));
        rig.Session.WindowCreated();
        Assert.True(await Wait.UntilAsync(() => rig.Window.Opened.Count == 1));
        Assert.Equal("app://rigcontrol/index.html?classic=1", rig.Window.Opened.First().ToString());
        await rig.CallAsync(1, "app.getInfo");
        await Task.Delay(300);
        Assert.Single(rig.Window.Opened);
        Assert.True(await Wait.UntilAsync(() => rig.SettingsText().Contains("\"classic\"")));
    }

    [Fact]
    public async Task Every_way_of_loading_the_page_is_RigControls_own_address_and_none_needs_a_port()
    {
        await using var rig = new Rig(Normal, pageWait: TimeSpan.FromMilliseconds(80));
        rig.Session.WindowCreated();
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1), "the window stayed open");
        Assert.Equal(new[] { "app://rigcontrol/index.html?classic=1" }, rig.Window.Opened.Select(u => u.ToString()));
        Assert.DoesNotContain(rig.Runtime.Services.Log.Recent(), l => l.Message.Contains("127.0.0.1") || l.Message.Contains("http://"));
    }

    [Fact]
    public async Task The_remembered_way_is_used_first_at_the_next_start()
    {
        await using var rig = new Rig(Normal, storedMode: "classic");
        Assert.Equal("app://rigcontrol/index.html?classic=1", rig.Session.StartAddress.ToString());
    }

    [Fact]
    public async Task A_slow_start_keeps_the_usual_way_when_the_window_took_its_files_and_remembers_nothing()
    {
        if (!UiAssets.HasUi) return; // a build made with -p:SkipUiBuild=true and no built interface
        await using var rig = new Rig(Normal, pageWait: TimeSpan.FromMilliseconds(80), pageSlowLimit: TimeSpan.FromSeconds(10));
        rig.Session.WindowCreated();
        // The window asks for the page and its script, as the real one does through the app:// address.
        var page = rig.Session.ServeFile("app://rigcontrol/index.html");
        Assert.NotNull(page);
        var script = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.UTF8.GetString(page!.Body), "<script\\b[^>]*\\bsrc=\"([^\"]+)\"");
        Assert.True(script.Success, "the built page has no script");
        Assert.NotNull(rig.Session.ServeFile("app://rigcontrol" + script.Groups[1].Value));
        // Well past the first wait the page is still given time, and nothing else is opened.
        await Task.Delay(500);
        Assert.Empty(rig.Window.Opened);
        await rig.CallAsync(1, "app.getInfo");
        Assert.True(await Wait.UntilAsync(() => rig.Runtime.Services.Log.Recent().Any(l => l.Message == "The page connected (scheme).")));
        await Task.Delay(150);
        Assert.DoesNotContain("uiMode", rig.SettingsText());
        Assert.Equal(0, rig.Window.Closed);
    }

    [Fact]
    public async Task A_remembered_local_server_from_an_early_build_is_not_used_and_is_taken_out_of_the_settings()
    {
        await using var rig = new Rig(Normal, storedMode: "loopback");
        Assert.Equal("app://rigcontrol/index.html", rig.Session.StartAddress.ToString());
        rig.Session.WindowCreated();
        await rig.CallAsync(1, "app.getInfo");
        Assert.True(await Wait.UntilAsync(() => !rig.SettingsText().Contains("loopback")));
        Assert.Empty(rig.Window.Opened);
    }

    [Fact]
    public async Task When_the_page_never_connects_the_window_is_closed_instead_of_left_open_and_empty()
    {
        await using var rig = new Rig(Normal, pageWait: TimeSpan.FromMilliseconds(60));
        rig.Session.WindowCreated();
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1), "the window stayed open");
        Assert.Single(rig.Window.Opened);
        Assert.True(rig.Session.PageNeverConnected);
        Assert.Equal(1, rig.Session.ExitCode);
        Assert.Contains(rig.Runtime.Services.Log.Recent(), l => l.Message == "RigControl is closing because its window never showed the interface.");
        Assert.DoesNotContain("uiMode", rig.SettingsText());
    }

    [Fact]
    public async Task The_usual_way_working_writes_nothing_to_the_settings()
    {
        await using var rig = new Rig(Normal);
        rig.Session.WindowCreated();
        await rig.CallAsync(1, "app.getInfo");
        await Task.Delay(150);
        Assert.DoesNotContain("uiMode", rig.SettingsText());
    }

    // ----- smoke check -----

    [Fact]
    public async Task A_smoke_check_passes_when_the_page_says_its_getInfo_answer_arrived_and_makes_no_real_module()
    {
        await using var rig = new Rig(Smoke);
        rig.Session.StartModules();
        rig.Session.WindowCreated();
        var check = rig.Session.RunSmokeCheckAsync(TimeSpan.FromSeconds(20));
        Assert.False(check.IsCompleted);
        rig.Say("{\"id\":1,\"method\":\"app.getInfo\",\"params\":null}");
        // The answer being handed to the window is not enough: nothing shows yet that it reached the page.
        Assert.True(await Wait.UntilAsync(() => rig.Window.Answer(1) is not null));
        await Task.Delay(200);
        Assert.False(check.IsCompleted);
        // The page's note that the answer arrived settles it, and is not answered itself.
        var sent = rig.Window.Sent.Count;
        rig.Say("{\"note\":\"answered\",\"id\":1}");
        Assert.True(await check.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(sent, rig.Window.Sent.Count);
        Assert.Equal(0, rig.Session.ExitCode);
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1));
        Assert.Equal(0, rig.MakersCalled);
        Assert.Equal(0, rig.Lighting.Started);
        // Even lighting and Discord status are stand-ins in a smoke check.
        var modules = (await rig.CallAsync(2, "app.getModules")).GetProperty("result").EnumerateArray().ToList();
        Assert.All(modules, m => Assert.StartsWith(ModuleAvailability.NotOnThisSystem, m.GetProperty("message").GetString()));
        Assert.Contains(rig.Runtime.Services.Log.Recent(), l => l.Message.StartsWith("Smoke check passed"));
        Assert.DoesNotContain("uiMode", rig.SettingsText());
    }

    [Fact]
    public async Task A_smoke_check_fails_with_exit_code_1_and_says_how_far_it_got()
    {
        await using var rig = new Rig(Smoke with { Ui = UiMode.Scheme });
        rig.Session.WindowCreated();
        Assert.False(await rig.Session.RunSmokeCheckAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, rig.Session.ExitCode);
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1));
        Assert.Contains(rig.Runtime.Services.Log.Recent(), l => l.Message.StartsWith("Smoke check failed") && l.Message.Contains("never sent a message"));
    }

    [Fact]
    public async Task A_smoke_check_fails_when_the_answer_never_reaches_the_page()
    {
        // Page to app works, app to page does not: the page asks, RigControl answers, and the page never hears it.
        await using var rig = new Rig(Smoke with { Ui = UiMode.Scheme });
        rig.Session.WindowCreated();
        await rig.CallAsync(1, "app.getInfo");
        Assert.False(await rig.Session.RunSmokeCheckAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, rig.Session.ExitCode);
        Assert.Contains(rig.Runtime.Services.Log.Recent(), l => l.Message.StartsWith("Smoke check failed") && l.Message.Contains("do not reach the page"));
    }

    [Fact]
    public async Task A_smoke_check_keeps_its_own_clock_when_no_way_of_loading_the_page_connects()
    {
        await using var rig = new Rig(Smoke, pageWait: TimeSpan.FromMilliseconds(40));
        rig.Session.WindowCreated();
        Assert.True(await Wait.UntilAsync(() => rig.Window.Opened.Count == 1));
        await Task.Delay(200);
        Assert.Equal(0, rig.Window.Closed);
        Assert.False(await rig.Session.RunSmokeCheckAsync(TimeSpan.FromMilliseconds(100)));
        Assert.True(await Wait.UntilAsync(() => rig.Window.Closed == 1));
    }

    [Fact]
    public async Task A_smoke_check_does_not_pass_on_some_other_call()
    {
        await using var rig = new Rig(Smoke);
        rig.Session.WindowCreated();
        await rig.CallAsync(1, "app.getModules");
        Assert.False(await rig.Session.RunSmokeCheckAsync(TimeSpan.FromMilliseconds(250)));
        Assert.Contains(rig.Runtime.Services.Log.Recent(), l => l.Message.Contains("never asked for app.getInfo"));
    }
}

public class ProgramTests
{
    [Fact]
    public void The_app_carries_lighting_and_Discord_status_and_both_are_in_the_catalogue_for_Linux_and_macOS()
    {
        var names = Program.Makers().Select(m => m.Key).ToList();
        Assert.Equal(new[] { "lighting", "presence" }, names);
        foreach (var name in names)
        {
            var entry = ModuleCatalog.Find(name);
            Assert.NotNull(entry);
            Assert.True(entry!.AvailableOn(HostPlatform.Linux));
            Assert.True(entry.AvailableOn(HostPlatform.MacOS));
        }
    }

    [Fact]
    public void The_modules_it_carries_are_the_builds_without_Windows_parts()
    {
        // The neutral flavour of both projects: no Windows-only type may have come along.
        Assert.Null(typeof(RigControl.Lighting.LightingModule).Assembly.GetType("RigControl.Lighting.OpenRgbServer"));
        Assert.Null(typeof(RigControl.Presence.PresenceModule).Assembly.GetType("RigControl.Presence.Launcher"));
    }
}

/// <summary>
/// What RigControl's window code takes for granted about Photino.NET, checked against the library itself
/// (by reading its code, without opening a window).
/// </summary>
public class PhotinoAssumptionsTests
{
    private static HashSet<string> Calls(MethodBase method)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var found = new HashSet<string>();
        // 0x28 = call, 0x6F = callvirt, 0x73 = newobj, 0xFE 0x06 = ldftn: each is followed by a 4-byte method token.
        for (var i = 0; i + 4 < il.Length; i++)
        {
            var isCall = il[i] is 0x28 or 0x6F or 0x73 || (i > 0 && il[i - 1] == 0xFE && il[i] == 0x06);
            if (!isCall) continue;
            try
            {
                var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                if (target is not null) found.Add(target.DeclaringType?.Name + "." + target.Name);
            }
            catch
            {
                // not a method token: the byte was part of something else
            }
        }
        return found;
    }

    private static HashSet<string> CallsDeep(Type type, string name, Type[]? args = null)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var method = args is null ? type.GetMethods(flags).First(m => m.Name == name) : type.GetMethod(name, flags, args)!;
        var all = Calls(method);
        // Lambdas the method hands on (compiler-made classes and methods) count as its own code.
        foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic).SelectMany(t => t.GetMethods(flags)).Concat(type.GetMethods(flags).Where(m => m.Name.Contains('<'))))
        {
            if (!nested.Name.Contains("<" + name + ">")) continue;
            if (nested.GetMethodBody() is not null) all.UnionWith(Calls(nested));
        }
        return all;
    }

    [Fact]
    public void The_window_library_is_the_version_the_notices_name()
        => Assert.Equal("4.0.16", typeof(PhotinoWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);

    [Fact]
    public void Invoke_runs_the_work_at_once_when_already_on_the_windows_thread()
    {
        // RigControl calls SendWebMessage, Load and Close inside Invoke. That is only safe when Invoke notices it is
        // already on the window's thread instead of queueing and waiting for itself.
        var calls = CallsDeep(typeof(PhotinoWindow), "Invoke");
        Assert.Contains("Environment.get_CurrentManagedThreadId", calls);
        Assert.Contains("Action.Invoke", calls);
    }

    [Theory]
    [InlineData("SendWebMessage")]
    [InlineData("Close")]
    public void These_go_to_the_windows_thread_by_themselves(string name)
        => Assert.Contains("PhotinoWindow.Invoke", CallsDeep(typeof(PhotinoWindow), name));

    [Fact]
    public void The_page_talks_through_window_external()
    {
        // bridge.ts uses window.external.sendMessage and receiveMessage. The names live in the native library; here
        // only that the managed side still has the matching handler and sender.
        Assert.NotNull(typeof(PhotinoWindow).GetMethod("RegisterWebMessageReceivedHandler"));
        Assert.NotNull(typeof(PhotinoWindow).GetMethod("SendWebMessage"));
        Assert.NotNull(typeof(PhotinoWindow).GetMethod("RegisterCustomSchemeHandler"));
    }

    [Fact]
    public void The_web_view_settings_can_be_passed_and_only_switch_things_off()
    {
        Assert.NotNull(typeof(PhotinoWindow).GetMethod("SetBrowserControlInitParameters", new[] { typeof(string) }));
        Assert.NotNull(typeof(PhotinoWindow).GetMethod("SetFileSystemAccessEnabled", new[] { typeof(bool) }));
        // The window library reads this as JSON and sets each name on the web view's settings.
        var linux = JsonDocument.Parse(PhotinoAppWindow.LinuxWebViewSettings).RootElement;
        Assert.Equal(new[]
            {
                "allow_universal_access_from_file_urls", "allow_file_access_from_file_urls",
                "allow_top_navigation_to_data_urls", "javascript_can_open_windows_automatically",
            },
            linux.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.All(linux.EnumerateObject(), p => Assert.Equal(JsonValueKind.False, p.Value.ValueKind));
        // On macOS each name is set on WKPreferences by key; an unknown key would stop the program, so only this one.
        var mac = JsonDocument.Parse(PhotinoAppWindow.MacWebViewSettings).RootElement;
        Assert.Equal(new[] { "javaScriptCanOpenWindowsAutomatically" }, mac.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.All(mac.EnumerateObject(), p => Assert.Equal(JsonValueKind.False, p.Value.ValueKind));
    }
}
