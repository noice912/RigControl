using System.Collections.Concurrent;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;

namespace RigControl.Hosting.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigcontrol-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>A module that works, for tests.</summary>
internal sealed class FakeModule : IAppModule
{
    public FakeModule(string name) => Name = name;
    public string Name { get; }
    public string DisplayName => Name + " (fake)";
    public ModuleStatus Status { get; private set; } = ModuleStatus.NotStarted;
    public ModuleContext? Context { get; private set; }

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        Context = context;
        Status = new ModuleStatus(ModuleState.Ready);
        return Task.CompletedTask;
    }

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
        => Task.FromResult<object?>(new { method });

    public ValueTask DisposeAsync()
    {
        Status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }
}

internal sealed class PartsModule : IAppModule, IModuleCapabilities
{
    public string Name => "lighting";
    public string DisplayName => "Lighting (OpenRGB)";
    public ModuleStatus Status => new(ModuleState.Ready);
    public IReadOnlyCollection<string> Capabilities { get; init; } = Array.Empty<string>();
    public Task StartAsync(ModuleContext context, CancellationToken ct) => Task.CompletedTask;
    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct) => Task.FromResult<object?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeShell : IHostShell
{
    public IReadOnlyCollection<string> Features { get; init; } = new[] { HostCapabilities.Autostart };
    public int ExitRequests;
    public void RequestExit() => Interlocked.Increment(ref ExitRequests);
}

internal sealed class FakeAutostart : IAutostart
{
    public bool Supported { get; init; } = true;
    public bool Enabled;
    public string? Error;
    public string? LastPath;
    public bool IsEnabled() => Enabled;

    public string? SetEnabled(bool enabled, string executablePath)
    {
        LastPath = executablePath;
        if (Error is not null) return Error;
        Enabled = enabled;
        return null;
    }
}

internal sealed class FakeLinks : ILinkOpener
{
    public readonly List<string> Opened = new();

    public bool OpenHttpsUrl(string? url)
    {
        if (url is null || !url.StartsWith("https://", StringComparison.Ordinal)) return false;
        Opened.Add(url);
        return true;
    }

    public void OpenFolder(string path) => Opened.Add("folder:" + path);
}

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
        => key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Windows_keeps_the_folders_it_always_had()
    {
        var p = AppPaths.For(HostPlatform.Windows, Env(), @"C:\Users\someone", @"C:\Users\someone\AppData\Local");
        Assert.Equal(Path.Combine(@"C:\Users\someone\AppData\Local", "RigControl"), p.DataDirectory);
        Assert.Equal(Path.Combine(p.DataDirectory, "logs"), p.LogDirectory);
        Assert.Equal(Path.Combine(p.DataDirectory, "settings.json"), p.SettingsFile);
    }

    [Fact]
    public void Linux_uses_the_usual_folders_when_nothing_is_set()
    {
        var p = AppPaths.For(HostPlatform.Linux, Env(), "/home/someone", "/ignored");
        Assert.Equal("/home/someone/.config/RigControl", p.DataDirectory);
        Assert.Equal("/home/someone/.local/state/RigControl/logs", p.LogDirectory);
    }

    [Fact]
    public void Linux_follows_the_XDG_folders_when_they_are_full_paths()
    {
        var p = AppPaths.For(HostPlatform.Linux, Env(("XDG_CONFIG_HOME", "/data/config/"), ("XDG_STATE_HOME", "/data/state")), "/home/someone", "/ignored");
        Assert.Equal("/data/config/RigControl", p.DataDirectory);
        Assert.Equal("/data/state/RigControl/logs", p.LogDirectory);
    }

    [Fact]
    public void Linux_ignores_XDG_folders_that_are_empty_or_not_full_paths()
    {
        var p = AppPaths.For(HostPlatform.Linux, Env(("XDG_CONFIG_HOME", "relative/config"), ("XDG_STATE_HOME", "")), "/home/someone", "/ignored");
        Assert.Equal("/home/someone/.config/RigControl", p.DataDirectory);
        Assert.Equal("/home/someone/.local/state/RigControl/logs", p.LogDirectory);
    }

    [Fact]
    public void MacOS_uses_the_Library_folders()
    {
        var p = AppPaths.For(HostPlatform.MacOS, Env(("XDG_CONFIG_HOME", "/data/config")), "/Users/someone", "/ignored");
        Assert.Equal("/Users/someone/Library/Application Support/RigControl", p.DataDirectory);
        Assert.Equal("/Users/someone/Library/Logs/RigControl", p.LogDirectory);
    }

    [Fact]
    public void The_folders_for_this_system_are_full_paths_and_can_be_made()
    {
        var here = AppPaths.ForThisSystem();
        Assert.True(Path.IsPathRooted(here.DataDirectory));
        Assert.True(Path.IsPathRooted(here.LogDirectory));

        using var dir = new TempFolder();
        var p = new AppPaths(Path.Combine(dir.Path, "a", "data"), Path.Combine(dir.Path, "b", "logs"));
        p.EnsureCreated();
        p.EnsureCreated(); // a second time changes nothing
        Assert.True(Directory.Exists(p.DataDirectory));
        Assert.True(Directory.Exists(p.LogDirectory));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(p.DataDirectory));
    }
}

public class ModuleCatalogTests
{
    private static KeyValuePair<string, Func<IAppModule>> Maker(string name, List<string>? made = null)
        => new(name, () =>
        {
            made?.Add(name);
            return new FakeModule(name);
        });

    [Fact]
    public void The_catalogue_has_every_module_once_with_parts_named_after_it()
    {
        var names = ModuleCatalog.All.Select(m => m.Name).ToArray();
        Assert.Equal(new[] { "sensors", "nvidia", "cooling", "lighting", "capture", "drivers", "bios", "cpu", "presence", "health" }, names);
        foreach (var m in ModuleCatalog.All)
        {
            Assert.Contains(HostPlatform.Windows, m.Platforms);
            Assert.All(m.Parts.Keys, part => Assert.StartsWith(m.Name + ".", part));
            // A part cannot work where its module does not.
            Assert.All(m.Parts.Values, where => Assert.All(where, p => Assert.Contains(p, m.Platforms)));
        }
        Assert.Equal("Lighting (OpenRGB)", ModuleCatalog.Find("LIGHTING")!.DisplayName);
        Assert.Null(ModuleCatalog.Find("someaddon"));
    }

    [Theory]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    public void On_Linux_and_macOS_only_lights_and_Discord_are_real(HostPlatform platform)
    {
        var made = new List<string>();
        // The host offers a maker for everything; only the ones with a version for this system may be called.
        var makers = ModuleCatalog.All.Select(m => Maker(m.Name, made)).ToList();

        var modules = ModuleCatalog.Build(platform, makers);

        Assert.Equal(ModuleCatalog.All.Select(m => m.Name), modules.Select(m => m.Name));
        Assert.Equal(new[] { "lighting", "presence" }, made);
        Assert.Equal(new[] { "lighting", "presence" }, modules.Where(m => m is not UnavailableModule).Select(m => m.Name));
        var sensors = modules.Single(m => m.Name == "sensors");
        Assert.Equal("Sensors and motherboard fans", sensors.DisplayName);
        Assert.Equal("Not available on this system: it works on Windows only.", sensors.Status.Message);
    }

    [Fact]
    public void A_module_the_host_has_no_maker_for_is_a_stand_in_and_add_ons_come_last()
    {
        var modules = ModuleCatalog.Build(HostPlatform.Linux, new[] { Maker("presence"), Maker("someaddon") });

        Assert.Equal(ModuleCatalog.All.Count + 1, modules.Count);
        Assert.Equal("someaddon", modules[^1].Name);
        Assert.IsType<FakeModule>(modules.Single(m => m.Name == "presence"));
        var lighting = Assert.IsType<UnavailableModule>(modules.Single(m => m.Name == "lighting"));
        Assert.Equal("Not available on this system: this build was made without it.", lighting.Status.Message);
    }

    [Fact]
    public void On_a_system_nothing_was_made_for_everything_is_a_stand_in()
    {
        var modules = ModuleCatalog.Build(HostPlatform.Other, ModuleCatalog.All.Select(m => Maker(m.Name)).ToList());
        Assert.All(modules, m => Assert.IsType<UnavailableModule>(m));
        Assert.Equal("Not available on this system: there is no version of it for this system yet.",
            modules.Single(m => m.Name == "lighting").Status.Message);
    }
}

public class HostCapabilitiesTests
{
    [Fact]
    public void Windows_lists_every_module_every_part_and_the_host_features()
    {
        var modules = ModuleCatalog.All.Select(m => (IAppModule)new FakeModule(m.Name)).Append(new FakeModule("someaddon")).ToList();

        var caps = HostCapabilities.For(HostPlatform.Windows, modules, HostCapabilities.WindowsHost);

        foreach (var m in ModuleCatalog.All)
        {
            Assert.Contains(m.Name, caps);
            Assert.All(m.Parts.Keys, part => Assert.Contains(part, caps));
        }
        Assert.Contains("someaddon", caps);
        Assert.All(HostCapabilities.WindowsHost, f => Assert.Contains(f, caps));
        Assert.Equal(caps.OrderBy(c => c, StringComparer.Ordinal), caps);
        Assert.Equal(caps.Distinct(), caps);
    }

    [Fact]
    public void Linux_lists_only_what_works_there()
    {
        var modules = ModuleCatalog.Build(HostPlatform.Linux, new[]
        {
            new KeyValuePair<string, Func<IAppModule>>("lighting", () => new FakeModule("lighting")),
            new KeyValuePair<string, Func<IAppModule>>("presence", () => new FakeModule("presence")),
        });

        var caps = HostCapabilities.For(HostPlatform.Linux, modules, new[] { HostCapabilities.Autostart });

        Assert.Equal(new[]
        {
            "host.autostart",
            "lighting", "lighting.effects", "lighting.layout", "lighting.network", "lighting.openrgb", "lighting.scenes",
            "presence", "presence.apps", "presence.custom",
        }, caps);
    }

    [Fact]
    public void A_module_that_says_its_own_parts_is_believed_but_only_for_its_own_name()
    {
        var module = new PartsModule { Capabilities = new[] { "lighting.effects", "lighting.screen", "capture.record", "lighting" } };

        var caps = HostCapabilities.For(HostPlatform.Linux, new IAppModule[] { module }, Array.Empty<string>());

        Assert.Equal(new[] { "lighting", "lighting.effects", "lighting.screen" }, caps);
    }
}

public class UiMessagesTests
{
    [Fact]
    public void An_event_has_the_shape_the_UI_expects()
    {
        Assert.Equal("{\"event\":\"sensors.snapshot\",\"data\":{\"tempC\":41.5}}", UiMessages.Event("sensors.snapshot", new { TempC = 41.5 }));
        Assert.Equal("{\"event\":\"app.ping\",\"data\":null}", UiMessages.Event("app.ping", null));
    }
}

public class UiBridgeTests
{
    private static async Task<List<string>> Collect(BlockingCollection<string> posted, int count)
    {
        var list = new List<string>();
        await Task.Run(() =>
        {
            while (list.Count < count && posted.TryTake(out var item, TimeSpan.FromSeconds(10))) list.Add(item);
        });
        return list;
    }

    [Fact]
    public async Task Calls_are_answered_and_events_are_passed_on_until_it_is_closed()
    {
        var dispatcher = new RpcDispatcher(NullLog.Instance);
        dispatcher.Register(new FakeModule("fake"));
        var events = new EventBus();
        using var posted = new BlockingCollection<string>();
        var bridge = new UiBridge(dispatcher, events, NullLog.Instance, posted.Add);

        await bridge.OnMessageAsync("{\"id\":7,\"method\":\"fake.hello\"}");
        events.Publish("fake.changed", new { Value = 1 });

        var got = await Collect(posted, 2);
        Assert.Contains("{\"id\":7,\"ok\":true,\"result\":{\"method\":\"hello\"},\"error\":null}", got);
        Assert.Contains("{\"event\":\"fake.changed\",\"data\":{\"value\":1}}", got);

        bridge.Dispose();
        bridge.Dispose();
        events.Publish("fake.changed", new { Value = 2 });
        await bridge.OnMessageAsync("{\"id\":8,\"method\":\"fake.hello\"}");
        Assert.Empty(posted);
    }

    [Fact]
    public async Task Bad_input_and_a_failing_window_do_no_harm()
    {
        var dispatcher = new RpcDispatcher(NullLog.Instance);
        var events = new EventBus();
        var calls = 0;
        using var bridge = new UiBridge(dispatcher, events, NullLog.Instance, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("the window is closing");
        });

        await bridge.OnMessageAsync(null);
        await bridge.OnMessageAsync("");
        await bridge.OnMessageAsync(new string('x', UiBridge.MaxRequestChars + 1));
        Assert.Equal(0, calls);

        await bridge.OnMessageAsync("this is not json"); // answered with a "badRequest" error; posting it throws; nothing escapes
        events.Publish("x.y", 1);
        Assert.Equal(2, calls);

        // A payload that cannot be written as JSON is dropped, not thrown at the publisher.
        var loop = new Dictionary<string, object>();
        loop["self"] = loop;
        events.Publish("x.loop", loop);
        Assert.Equal(2, calls);
    }
}

public class HostRuntimeTests
{
    [Fact]
    public async Task It_makes_the_folders_starts_the_modules_and_stops_them()
    {
        using var dir = new TempFolder();
        var paths = new AppPaths(Path.Combine(dir.Path, "data"), Path.Combine(dir.Path, "state", "logs"));
        var autostart = new FakeAutostart();
        var platform = PlatformServices.Create(paths.DataDirectory, isElevated: false) with { Autostart = autostart };

        var runtime = HostRuntime.Create(paths, "1.2.3", "/opt/RigControl/RigControl", platform);
        var real = new FakeModule("presence");
        runtime.AddModules(ModuleCatalog.Build(HostPlatform.Linux, new[] { new KeyValuePair<string, Func<IAppModule>>("presence", () => real) }));
        string? announced = null;
        runtime.Services.Events.Published += (topic, _) => announced = topic;

        await runtime.StartModulesAsync();

        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.Equal("app.modulesChanged", announced);
        Assert.Equal("1.2.3", runtime.Services.Environment.Version);
        Assert.False(runtime.Services.Environment.IsElevated);
        Assert.Same(platform, runtime.Services.Platform);
        // The module got the host's platform services, its own log, and the shared settings.
        Assert.Same(platform, real.Context!.Platform);
        Assert.Same(runtime.Services.Settings, real.Context.Settings);
        var described = runtime.Services.Host.Describe();
        Assert.Equal(ModuleState.Ready, described.Single(m => m.Name == "presence").State);
        Assert.Equal(ModuleState.Failed, described.Single(m => m.Name == "sensors").State);
        Assert.Contains("presence", runtime.Services.Dispatcher.ModuleNames);
        Assert.Contains("sensors", runtime.Services.Dispatcher.ModuleNames);

        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.Equal(ModuleState.Stopped, real.Status.State);
        Assert.True(runtime.Lifetime.IsCancellationRequested);
    }
}

public class HostAppModuleTests : IAsyncLifetime
{
    private readonly TempFolder _dir = new();
    private readonly FakeAutostart _autostart = new();
    private readonly FakeLinks _links = new();
    private readonly FakeShell _shell = new();
    private HostRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        var paths = new AppPaths(Path.Combine(_dir.Path, "data"), Path.Combine(_dir.Path, "logs"));
        var platform = PlatformServices.Create(paths.DataDirectory, isElevated: false) with
        {
            Platform = HostPlatform.Linux,
            Autostart = _autostart,
            Links = _links,
        };
        _runtime = HostRuntime.Create(paths, "1.2.3", "/opt/RigControl/RigControl", platform);
        _runtime.AddModules(ModuleCatalog.Build(HostPlatform.Linux, new[]
        {
            new KeyValuePair<string, Func<IAppModule>>("lighting", () => new FakeModule("lighting")),
        }));
        _runtime.Services.Dispatcher.Register(new HostAppModule(_runtime.Services, _shell));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        _dir.Dispose();
    }

    private async Task<JsonElement> Call(string method, string? paramsJson = null)
    {
        var request = "{\"id\":1,\"method\":\"app." + method + "\"" + (paramsJson is null ? "" : ",\"params\":" + paramsJson) + "}";
        return JsonDocument.Parse(await _runtime.Services.Dispatcher.HandleAsync(request)).RootElement.Clone();
    }

    private async Task<JsonElement> Ok(string method, string? paramsJson = null)
    {
        var answer = await Call(method, paramsJson);
        Assert.True(answer.GetProperty("ok").GetBoolean(), answer.ToString());
        return answer.GetProperty("result");
    }

    private async Task<(string Code, string Message)> Fails(string method, string? paramsJson = null)
    {
        var answer = await Call(method, paramsJson);
        Assert.False(answer.GetProperty("ok").GetBoolean(), answer.ToString());
        var error = answer.GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task GetInfo_says_which_system_this_is_and_what_works()
    {
        var info = await Ok("getInfo");

        Assert.Equal("1.2.3", info.GetProperty("version").GetString());
        Assert.False(info.GetProperty("isElevated").GetBoolean());
        Assert.Equal(_runtime.Paths.DataDirectory, info.GetProperty("dataDirectory").GetString());
        Assert.Equal(_runtime.Paths.LogDirectory, info.GetProperty("logDirectory").GetString());
        Assert.Equal(Environment.MachineName, info.GetProperty("machineName").GetString());
        Assert.Equal("linux", info.GetProperty("platform").GetString());
        var caps = info.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Contains("lighting", caps);
        Assert.Contains("lighting.effects", caps);
        Assert.Contains("host.autostart", caps);
        Assert.DoesNotContain("lighting.screen", caps);
        Assert.DoesNotContain("sensors", caps);
        Assert.DoesNotContain("presence", caps); // this build has no maker for it
        Assert.DoesNotContain("host.tray", caps);
    }

    [Fact]
    public async Task GetModules_lists_every_page_with_its_state()
    {
        var modules = (await Ok("getModules")).EnumerateArray().ToArray();
        Assert.Equal(ModuleCatalog.All.Count, modules.Length);
        var capture = modules.Single(m => m.GetProperty("name").GetString() == "capture");
        Assert.Equal("failed", capture.GetProperty("state").GetString());
        Assert.StartsWith("Not available on this system", capture.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Settings_keep_the_unit_and_switch_start_at_sign_in()
    {
        var before = await Ok("getSettings");
        Assert.False(before.GetProperty("minimizeToTray").GetBoolean());
        Assert.False(before.GetProperty("startWithWindows").GetBoolean());
        Assert.Equal("F", before.GetProperty("temperatureUnit").GetString());

        var after = await Ok("setSettings", "{\"temperatureUnit\":\"C\",\"startWithWindows\":true,\"minimizeToTray\":true}");
        Assert.Equal("C", after.GetProperty("temperatureUnit").GetString());
        Assert.True(after.GetProperty("startWithWindows").GetBoolean());
        Assert.False(after.GetProperty("minimizeToTray").GetBoolean()); // there is no tray here
        Assert.True(_autostart.Enabled);
        Assert.Equal("/opt/RigControl/RigControl", _autostart.LastPath);
        Assert.Equal("C", _runtime.Services.Settings.Get<AppSettings>(AppSettings.Section).TemperatureUnit);

        Assert.Equal("invalidArgs", (await Fails("setSettings", "{\"temperatureUnit\":\"K\"}")).Code);

        _autostart.Error = "The startup file could not be changed: disk full";
        var failed = await Fails("setSettings", "{\"startWithWindows\":false}");
        Assert.Equal(("internal", "The startup file could not be changed: disk full"), failed);
        Assert.True(_autostart.Enabled);
    }

    [Fact]
    public async Task Links_folders_logs_and_quit_go_through_the_host()
    {
        await Ok("openExternal", "{\"url\":\"https://example.com/a\"}");
        Assert.Equal("invalidArgs", (await Fails("openExternal", "{\"url\":\"http://example.com\"}")).Code);
        await Ok("openLogFolder");
        Assert.Equal(new[] { "https://example.com/a", "folder:" + _runtime.Paths.LogDirectory }, _links.Opened);

        _runtime.Services.Log.Info("a line for the test");
        var logs = (await Ok("getLogs", "{\"max\":1}")).EnumerateArray().ToArray();
        var line = Assert.Single(logs);
        Assert.Equal("info", line.GetProperty("level").GetString());

        Assert.True((await Ok("quit")).GetProperty("quitting").GetBoolean());
        Assert.Equal(1, _shell.ExitRequests);
    }

    [Theory]
    [InlineData("restartAsAdmin")]
    [InlineData("pickFile")]
    [InlineData("pickFolder")]
    [InlineData("getBundled")]
    [InlineData("uninstallOldApp")]
    [InlineData("restartPc")]
    [InlineData("cancelRestartPc")]
    [InlineData("getFirstRun")]
    [InlineData("installHelpers")]
    [InlineData("completeFirstRun")]
    public async Task What_only_Windows_has_answers_calmly(string method)
    {
        Assert.Equal(("unavailable", "Not available on this system."), await Fails(method));
    }

    [Fact]
    public async Task The_native_view_and_the_Audio_section_have_quiet_answers()
    {
        Assert.Equal(JsonValueKind.Null, (await Ok("setNativeView", "{\"view\":\"audio\"}")).GetProperty("view").ValueKind);
        var audio = await Ok("getAudioStatus");
        Assert.Equal("unavailable", audio.GetProperty("state").GetString());
        Assert.StartsWith("Not available on this system", audio.GetProperty("message").GetString());
        Assert.Equal("unknownMethod", (await Fails("noSuchThing")).Code);
    }
}

public class UiAssetsTests
{
    [Fact]
    public void The_start_page_always_opens_and_paths_outside_the_UI_never_do()
    {
        Assert.True(UiAssets.TryOpen("/", out var index, out var type));
        using (index)
        {
            Assert.Equal("text/html; charset=utf-8", type);
            Assert.True(index.Length > 0);
        }
        Assert.True(UiAssets.TryOpen("/index.html", out var again, out _));
        again.Dispose();

        Assert.False(UiAssets.TryOpen("/../secret.txt", out _, out _));
        Assert.False(UiAssets.TryOpen("/assets/../../x", out _, out _));
        Assert.False(UiAssets.TryOpen("/no-such-file.js", out _, out _));
    }

    [Fact]
    public void Every_file_of_the_built_UI_can_be_opened_with_a_known_type()
    {
        // A build made with -p:SkipUiBuild=true and an empty src/ui/dist has no files; then there is nothing to check.
        foreach (var path in UiAssets.Paths)
        {
            Assert.True(UiAssets.TryOpen("/" + path, out var stream, out var type), path);
            stream.Dispose();
            Assert.Equal(UiAssets.ContentTypeFor(path), type);
        }
        Assert.Equal(UiAssets.HasUi, UiAssets.Paths.Contains("index.html"));
    }

    [Theory]
    [InlineData("a.js", "text/javascript; charset=utf-8")]
    [InlineData("a.CSS", "text/css; charset=utf-8")]
    [InlineData("a.woff2", "font/woff2")]
    [InlineData("a.unknown", "application/octet-stream")]
    public void Content_types(string path, string expected) => Assert.Equal(expected, UiAssets.ContentTypeFor(path));
}
