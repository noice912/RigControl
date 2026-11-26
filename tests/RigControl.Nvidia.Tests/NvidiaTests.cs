using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Nvidia.Tests;

internal sealed class FakeBackend : INvidiaBackend
{
    public bool Available { get; set; } = true;
    public HashSet<uint> Unknown { get; } = new();
    public Dictionary<(string? Profile, uint Id), uint> Stored { get; } = new();
    public PowerReading Power { get; set; } = new(111.1, 285, 285, 100, 340);
    public List<string> Log { get; } = new();
    public ModeInfo Current { get; set; } = new(2560, 1440, 240);
    public ModeInfo Saved { get; set; } = new(2560, 1440, 240);
    public int Vibrance { get; set; } = 50;
    public bool RejectMode { get; set; }
    public Func<LatestDriver>? Latest { get; set; }
    public bool Disposed { get; private set; }

    public static readonly ModeInfo[] AllModes = { new(2560, 1440, 240), new(2560, 1440, 144), new(1920, 1080, 240) };

    public DriverInfo Initialize() => Available
        ? new DriverInfo(true, "621.24", "r621_20", new[] { new GpuInfo(0, "NVIDIA GeForce RTX 4070 Ti", 12282, "1") }, null)
        : new DriverInfo(false, null, null, Array.Empty<GpuInfo>(), "No NVIDIA graphics card was found.");

    public PowerReading GetPower() => Power;

    public void SetPowerLimit(double watts)
    {
        Log.Add($"power {watts}");
        Power = Power with { LimitW = watts };
    }

    public ClockOffsetReading Offsets { get; set; } = new(225, -1000, 1000, 0, -2000, 6000);

    public ClockOffsetReading GetClockOffsets() => Offsets;

    public void SetClockOffset(bool memory, int mhz)
    {
        Log.Add($"{(memory ? "memory" : "core")} {mhz}");
        Offsets = memory ? Offsets with { MemoryMHz = mhz } : Offsets with { CoreMHz = mhz };
    }

    public bool IsSettingKnown(uint id) => !Unknown.Contains(id);

    public uint? ReadSetting(string? profile, uint id) => Stored.TryGetValue((profile, id), out var v) ? v : null;

    public void WriteSetting(string? profile, uint id, uint value)
    {
        Log.Add($"write {profile ?? "global"} {id:X} {value:X}");
        Stored[(profile, id)] = value;
    }

    public void ResetSetting(string? profile, uint id)
    {
        Log.Add($"reset {profile ?? "global"} {id:X}");
        Stored.Remove((profile, id));
    }

    public IReadOnlyList<ProfileInfo> ListProfiles(string? query, int max)
        => new[] { new ProfileInfo("Roblox", new[] { "robloxplayerbeta.exe" }, true, 0) }
            .Where(p => string.IsNullOrEmpty(query) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

    public ProfileInfo AddProgram(string exePath)
    {
        Log.Add("add " + exePath);
        return new ProfileInfo("RigControl - " + Path.GetFileNameWithoutExtension(exePath), new[] { Path.GetFileName(exePath).ToLowerInvariant() }, false, 0);
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
        => new[] { new DisplayInfo("DISPLAY1", "Display 1", true, Current.Width, Current.Height, Current.RefreshHz, AllModes, new VibranceInfo(Vibrance, 0, 100, 50)) };

    public bool TestMode(string displayId, ModeInfo mode) => !RejectMode;

    public void ApplyModeTemporarily(string displayId, ModeInfo mode)
    {
        Log.Add("apply " + mode);
        Current = mode;
    }

    public void KeepMode(string displayId, ModeInfo mode)
    {
        Log.Add("keep " + mode);
        Current = Saved = mode;
    }

    public void RevertMode(string displayId)
    {
        Log.Add("revert");
        Current = Saved;
    }

    public void SetVibrance(string displayId, int value)
    {
        Log.Add("vibrance " + value);
        Vibrance = value;
    }

    public Task<LatestDriver> GetLatestDriverAsync(CancellationToken ct)
        => Task.FromResult((Latest ?? (() => new LatestDriver("621.24", "https://www.nvidia.com/en-us/drivers/details/285320/")))());

    public void Dispose() => Disposed = true;
}

public class NvidiaModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-nvidia-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBackend _backend = new();

    public NvidiaModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private async Task<NvidiaModule> Start(bool elevated = false, int revertSeconds = 15)
    {
        var m = new NvidiaModule(_ => _backend, revertSeconds);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    [Fact]
    public async Task A_driver_that_is_not_ready_at_startup_is_tried_again()
    {
        // What happens right after Windows starts: the first attempt fails (here even by throwing), a later one works.
        var attempts = 0;
        var m = new NvidiaModule(_ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Function identification number is invalid or not supported.");
            return _backend;
        }) { RetryMilliseconds = 40 };
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        Assert.Equal(ModuleState.Failed, m.Status.State);
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => Call(m, "getPower"))).Code);

        for (var i = 0; i < 100 && m.Status.State != ModuleState.Ready; i++) await Task.Delay(20);
        Assert.Equal(ModuleState.Ready, m.Status.State);
        Assert.True((await Call(m, "getInfo")).GetProperty("available").GetBoolean());
        await m.DisposeAsync();
    }

    private static async Task<JsonElement> Call(NvidiaModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task<string> FailCode(NvidiaModule m, string method, object? args = null)
        => (await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args))).Code;

    [Fact]
    public async Task After_a_driver_update_the_version_Windows_has_installed_is_shown()
    {
        // The driver session opened at start keeps saying 621.24; Windows already has 621.40 installed.
        var installed = "621.40";
        var m = new NvidiaModule(_ => _backend, installedVersion: () => installed);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        Assert.Equal("621.40", (await Call(m, "getInfo")).GetProperty("driverVersion").GetString());
        await m.DisposeAsync();

        // Windows could not be asked: the version from start is shown.
        var unknown = new NvidiaModule(_ => _backend, installedVersion: () => null);
        await unknown.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings2.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        Assert.Equal("621.24", (await Call(unknown, "getInfo")).GetProperty("driverVersion").GetString());
        await unknown.DisposeAsync();
    }

    [Fact]
    public async Task Info_and_settings_match_the_contract()
    {
        await using var m = await Start();
        Assert.Equal(ModuleState.Ready, m.Status.State);
        var info = await Call(m, "getInfo");
        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.Equal("621.24", info.GetProperty("driverVersion").GetString());
        Assert.Equal(12282, info.GetProperty("gpus")[0].GetProperty("vramMB").GetInt32());
        Assert.True(info.GetProperty("limitations").GetArrayLength() > 0);

        var settings = await Call(m, "getDriverSettings");
        Assert.Equal(NvidiaModule.Settings.Count, settings.GetArrayLength());
        var vsync = settings.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "vsync");
        Assert.Equal("enum", vsync.GetProperty("kind").GetString());
        Assert.Equal(0x60925292L, vsync.GetProperty("value").GetInt64());
        Assert.True(vsync.GetProperty("isDefault").GetBoolean());
        Assert.Contains(vsync.GetProperty("options").EnumerateArray(), o => o.GetProperty("label").GetString() == "Off");
        var cap = settings.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "maxFrameRate");
        Assert.Equal("number", cap.GetProperty("kind").GetString());
        Assert.Equal(1023, cap.GetProperty("max").GetInt32());
        Assert.Equal(JsonValueKind.Null, cap.GetProperty("options").ValueKind);
    }

    [Fact]
    public void Curated_settings_are_internally_consistent()
    {
        Assert.Equal(NvidiaModule.Settings.Count, NvidiaModule.Settings.Select(s => s.Key).Distinct().Count());
        Assert.Equal(NvidiaModule.Settings.Count, NvidiaModule.Settings.Select(s => s.Id).Distinct().Count());
        foreach (var s in NvidiaModule.Settings)
        {
            if (s.Kind == "number")
            {
                Assert.InRange(s.Default, s.Min, s.Max);
            }
            else
            {
                Assert.NotNull(s.Options);
                Assert.Contains(s.Options!, o => o.Value == s.Default);                   // the default is selectable
                Assert.Equal(s.Options!.Length, s.Options.Select(o => o.Value).Distinct().Count());
            }
        }
        // Spot-check ids and values against docs/protocols/nvidia.md section 4.3.
        var byKey = NvidiaModule.Settings.ToDictionary(s => s.Key);
        Assert.Equal(0x00A879CFu, byKey["vsync"].Id);
        Assert.Equal(0x10835002u, byKey["maxFrameRate"].Id);
        Assert.Equal(0x007BA09Eu, byKey["lowLatency"].Id);
        Assert.Equal(0x1057EB71u, byKey["powerMode"].Id);
        Assert.Equal(0x00CE2691u, byKey["textureQuality"].Id);
        Assert.Contains(byKey["vsync"].Options!, o => o is { Value: 0x08416747, Label: "Off" });
        Assert.Contains(byKey["vsync"].Options!, o => o is { Value: 0x47814940, Label: "On" });
        Assert.Contains(byKey["textureQuality"].Options!, o => o is { Value: 0xFFFFFFF6, Label: "High quality" });
    }

    [Fact]
    public async Task Settings_the_driver_does_not_know_are_hidden_and_refused()
    {
        _backend.Unknown.Add(0x10835002);
        await using var m = await Start();
        var settings = await Call(m, "getDriverSettings");
        Assert.DoesNotContain(settings.EnumerateArray(), s => s.GetProperty("key").GetString() == "maxFrameRate");
        Assert.Equal("invalidArgs", await FailCode(m, "setDriverSetting", new { key = "maxFrameRate", value = 120 }));
        Assert.Empty(_backend.Log);
    }

    [Fact]
    public async Task Only_listed_values_can_be_written()
    {
        await using var m = await Start();
        Assert.Equal("invalidArgs", await FailCode(m, "setDriverSetting", new { key = "vsync", value = 1 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setDriverSetting", new { key = "maxFrameRate", value = 5000 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setDriverSetting", new { key = "maxFrameRate", value = -1 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setDriverSetting", new { key = "nope", value = 1 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setDriverSetting", new { key = "vsync" }));
        Assert.Empty(_backend.Log);

        var s = await Call(m, "setDriverSetting", new { key = "vsync", value = 0x08416747L });
        Assert.Equal(0x08416747L, s.GetProperty("value").GetInt64());
        Assert.False(s.GetProperty("isDefault").GetBoolean());
        Assert.Equal("write global A879CF 8416747", _backend.Log.Single());

        var perGame = await Call(m, "setDriverSetting", new { key = "maxFrameRate", value = 237, profile = "Roblox" });
        Assert.Equal(237, perGame.GetProperty("value").GetInt32());
        Assert.True((await Call(m, "getDriverSettings")).EnumerateArray().Single(x => x.GetProperty("key").GetString() == "maxFrameRate").GetProperty("isDefault").GetBoolean());

        var reset = await Call(m, "resetDriverSetting", new { key = "vsync" });
        Assert.True(reset.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Power_limit_needs_admin_and_stays_in_the_drivers_range()
    {
        await using (var m = await Start(elevated: false))
        {
            var p = await Call(m, "getPower");
            Assert.False(p.GetProperty("canSet").GetBoolean());
            Assert.Contains("administrator", p.GetProperty("reason").GetString());
            Assert.Equal("needsAdmin", await FailCode(m, "setPowerLimit", new { watts = 250 }));
        }
        await using (var m = await Start(elevated: true))
        {
            Assert.True((await Call(m, "getPower")).GetProperty("canSet").GetBoolean());
            Assert.Equal("unsafe", await FailCode(m, "setPowerLimit", new { watts = 99 }));
            Assert.Equal("unsafe", await FailCode(m, "setPowerLimit", new { watts = 341 }));
            Assert.Empty(_backend.Log);
            var p = await Call(m, "setPowerLimit", new { watts = 250 });
            Assert.Equal(250, p.GetProperty("limitW").GetDouble());
        }
    }

    [Fact]
    public async Task Clock_offsets_need_admin_and_stay_inside_RigControls_limits()
    {
        await using (var m = await Start(elevated: false))
        {
            var o = await Call(m, "getOverclock");
            Assert.True(o.GetProperty("available").GetBoolean());
            Assert.Equal(225, o.GetProperty("coreMHz").GetInt32());
            Assert.False(o.GetProperty("canSet").GetBoolean());
            Assert.Equal("needsAdmin", await FailCode(m, "setOverclock", new { coreMHz = 100 }));
        }
        await using (var m = await Start(elevated: true))
        {
            var o = await Call(m, "getOverclock");
            Assert.Equal(NvidiaModule.CoreCapMax, o.GetProperty("coreMax").GetInt32());
            Assert.Equal(NvidiaModule.MemoryCapMax, o.GetProperty("memoryMax").GetInt32());
            Assert.Equal("unsafe", await FailCode(m, "setOverclock", new { coreMHz = 301 }));
            Assert.Equal("unsafe", await FailCode(m, "setOverclock", new { memoryMHz = 1501 }));
            Assert.Equal("unsafe", await FailCode(m, "setOverclock", new { coreMHz = -501 }));
            Assert.Empty(_backend.Log);

            o = await Call(m, "setOverclock", new { coreMHz = 150, memoryMHz = 500 });
            Assert.Equal(150, o.GetProperty("coreMHz").GetInt32());
            Assert.Equal(500, o.GetProperty("memoryMHz").GetInt32());
            Assert.Equal(new[] { "core 150", "memory 500" }, _backend.Log);

            o = await Call(m, "resetOverclock");
            Assert.Equal(0, o.GetProperty("coreMHz").GetInt32());
            Assert.Equal(0, o.GetProperty("memoryMHz").GetInt32());
        }
    }

    [Fact]
    public async Task Clock_offsets_are_put_back_at_start_only_when_asked()
    {
        await using (var m = await Start(elevated: true))
            await Call(m, "setOverclock", new { coreMHz = 100, memoryMHz = 400 });
        _backend.Offsets = new(0, -1000, 1000, 0, -2000, 6000); // a restart clears them
        _backend.Log.Clear();
        await using (var m = await Start(elevated: true))
        {
            Assert.Empty(_backend.Log);
            // Turning it on keeps the offsets in effect at that moment.
            await Call(m, "setOverclock", new { coreMHz = 100, memoryMHz = 400, applyAtStart = true });
        }
        _backend.Offsets = new(0, -1000, 1000, 0, -2000, 6000);
        _backend.Log.Clear();
        await using (var m = await Start(elevated: true))
            Assert.Equal(new[] { "core 100", "memory 400" }, _backend.Log);
    }

    [Fact]
    public async Task Clock_offsets_are_not_put_back_after_a_crash_right_after_applying_them()
    {
        await using (var m = await Start(elevated: true))
            await Call(m, "setOverclock", new { coreMHz = 100, applyAtStart = true });
        _backend.Offsets = new(0, -1000, 1000, 0, -2000, 6000);
        _backend.Log.Clear();
        // Start and "crash": the module is never closed, so the start is never proven.
        var crashed = await Start(elevated: true);
        Assert.Equal(new[] { "core 100" }, _backend.Log);
        _backend.Offsets = new(0, -1000, 1000, 0, -2000, 6000);
        _backend.Log.Clear();
        await using (var m = await Start(elevated: true))
        {
            Assert.Empty(_backend.Log);
            var o = await Call(m, "getOverclock");
            Assert.False(o.GetProperty("applyAtStart").GetBoolean());
            Assert.Contains("went down", o.GetProperty("notice").GetString());
        }
        GC.KeepAlive(crashed);
    }

    [Fact]
    public async Task A_display_mode_is_kept_only_when_confirmed()
    {
        await using var m = await Start();
        Assert.Equal("invalidArgs", await FailCode(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 800, height = 600, refreshHz = 60 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setDisplayMode", new { displayId = "DISPLAY9", width = 2560, height = 1440, refreshHz = 144 }));

        var r = await Call(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 2560, height = 1440, refreshHz = 144 });
        Assert.Equal(15, r.GetProperty("revertInSeconds").GetInt32());
        Assert.Equal(new ModeInfo(2560, 1440, 144), _backend.Current);
        Assert.Equal(new ModeInfo(2560, 1440, 240), _backend.Saved); // not saved yet
        Assert.Equal("unavailable", await FailCode(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 1920, height = 1080, refreshHz = 240 }));
        Assert.Equal("invalidArgs", await FailCode(m, "confirmDisplayMode", new { revertToken = "wrong" }));

        await Call(m, "confirmDisplayMode", new { revertToken = r.GetProperty("revertToken").GetString() });
        Assert.Equal(new ModeInfo(2560, 1440, 144), _backend.Saved);
    }

    [Fact]
    public async Task An_unconfirmed_display_mode_goes_back_by_itself()
    {
        await using var m = await Start(revertSeconds: 1);
        var r = await Call(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 1920, height = 1080, refreshHz = 240 });
        Assert.Equal(new ModeInfo(1920, 1080, 240), _backend.Current);
        for (var i = 0; i < 40 && !_backend.Log.Contains("revert"); i++) await Task.Delay(100);
        Assert.Contains("revert", _backend.Log);
        Assert.Equal(new ModeInfo(2560, 1440, 240), _backend.Current);
        Assert.Equal("invalidArgs", await FailCode(m, "confirmDisplayMode", new { revertToken = r.GetProperty("revertToken").GetString() }));
    }

    [Fact]
    public async Task Reverting_by_hand_and_on_exit()
    {
        var m = await Start();
        var r = await Call(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 1920, height = 1080, refreshHz = 240 });
        await Call(m, "revertDisplayMode", new { revertToken = r.GetProperty("revertToken").GetString() });
        Assert.Equal(new ModeInfo(2560, 1440, 240), _backend.Current);

        await Call(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 1920, height = 1080, refreshHz = 240 });
        await m.DisposeAsync();
        await m.DisposeAsync();
        Assert.Equal(new ModeInfo(2560, 1440, 240), _backend.Current); // never left on an unconfirmed mode
        Assert.True(_backend.Disposed);
    }

    [Fact]
    public async Task A_mode_windows_rejects_is_never_applied()
    {
        _backend.RejectMode = true;
        await using var m = await Start();
        Assert.Equal("unavailable", await FailCode(m, "setDisplayMode", new { displayId = "DISPLAY1", width = 1920, height = 1080, refreshHz = 240 }));
        Assert.Empty(_backend.Log);
    }

    [Fact]
    public async Task Vibrance_is_range_checked()
    {
        await using var m = await Start();
        Assert.Equal("invalidArgs", await FailCode(m, "setVibrance", new { displayId = "DISPLAY1", value = 101 }));
        var d = await Call(m, "setVibrance", new { displayId = "DISPLAY1", value = 70 });
        Assert.Equal(70, d.GetProperty("vibrance").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Driver_update_check_reports_newer_same_and_failure()
    {
        await using var m = await Start();
        var same = await Call(m, "checkDriverUpdate");
        Assert.False(same.GetProperty("newer").GetBoolean());
        Assert.Equal("621.24", same.GetProperty("installed").GetString());

        _backend.Latest = () => new LatestDriver("622.10", null);
        var newer = await Call(m, "checkDriverUpdate");
        Assert.True(newer.GetProperty("newer").GetBoolean());
        Assert.StartsWith("https://www.nvidia.com/", newer.GetProperty("downloadUrl").GetString());

        _backend.Latest = () => throw new HttpRequestException("offline");
        var failed = await Call(m, "checkDriverUpdate");
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("newer").ValueKind);
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("error").GetString()));
        Assert.StartsWith("https://www.nvidia.com/", failed.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task Programs_and_profiles()
    {
        await using var m = await Start();
        Assert.Equal(1, (await Call(m, "listProfiles", new { query = "rob" })).GetArrayLength());
        Assert.Equal("invalidArgs", await FailCode(m, "addProgram", new { exePath = "notes.txt" }));
        Assert.Equal("invalidArgs", await FailCode(m, "addProgram", new { exePath = @"C:\does\not\exist\game.exe" }));
        var exe = Path.Combine(_dir, "Game.exe");
        File.WriteAllText(exe, "x");
        var p = await Call(m, "addProgram", new { exePath = exe });
        Assert.Equal("RigControl - Game", p.GetProperty("name").GetString());
    }

    [Fact]
    public async Task No_nvidia_card_fails_cleanly()
    {
        _backend.Available = false;
        await using var m = await Start();
        Assert.Equal(ModuleState.Failed, m.Status.State);
        var info = await Call(m, "getInfo");
        Assert.False(info.GetProperty("available").GetBoolean());
        Assert.Equal("unavailable", await FailCode(m, "getDriverSettings"));
        Assert.Equal("unknownMethod", await FailCode(m, "nope"));
    }
}
