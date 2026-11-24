using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Sensors.Tests;

internal sealed class FakeSource : IHardwareSource
{
    public bool DriverInstalled { get; set; }
    public bool? OpenedLowLevel { get; private set; }
    public bool Disposed { get; private set; }
    public HardwareNames Names { get; set; } = new("Intel Core i9-13900KF", "NVIDIA GeForce RTX 4070 Ti", null, null);
    public List<RawSensor> Sensors { get; } = new();
    public List<(string Id, double? Percent)> Writes { get; } = new();
    /// <summary>When true the chip ignores writes (another program is in control).</summary>
    public bool IgnoreWrites { get; set; }

    public void Open(bool lowLevel) => OpenedLowLevel = lowLevel;

    public IReadOnlyList<RawSensor> Read() => Sensors.ToList();

    public bool SetControl(string controlId, double? percent)
    {
        var i = Sensors.FindIndex(s => s.Id == controlId && s.Kind == "control");
        if (i < 0) return false;
        Writes.Add((controlId, percent));
        if (!IgnoreWrites && percent is { } p) Sensors[i] = Sensors[i] with { Value = p };
        return true;
    }

    public void Dispose() => Disposed = true;

    public void Set(string id, double? value)
    {
        var i = Sensors.FindIndex(s => s.Id == id);
        Sensors[i] = Sensors[i] with { Value = value };
    }

    public static FakeSource Basic()
    {
        var f = new FakeSource();
        f.Sensors.AddRange(new[]
        {
            new RawSensor("/intelcpu/0/load/0", "CPU", "cpu", "load", "CPU Total", 0, 12.34),
            new RawSensor("/intelcpu/0/temperature/32", "CPU", "cpu", "temperature", "CPU Package", 32, null),
            new RawSensor("/intelcpu/0/power/0", "CPU", "cpu", "power", "CPU Package", 0, 0),          // bogus 0 W without the driver
            new RawSensor("/gpu-nvidia/0/temperature/0", "GPU", "gpu", "temperature", "GPU Core", 0, 41),
            new RawSensor("/gpu-nvidia/0/temperature/2", "GPU", "gpu", "temperature", "GPU Hot Spot", 2, 50.5),
            new RawSensor("/gpu-nvidia/0/load/0", "GPU", "gpu", "load", "GPU Core", 0, 6),
            new RawSensor("/gpu-nvidia/0/power/0", "GPU", "gpu", "power", "GPU Package", 0, 28.2),
            new RawSensor("/gpu-nvidia/0/fan/1", "GPU", "gpu", "fan", "GPU Fan 1", 1, 0),
            new RawSensor("/gpu-nvidia/0/control/1", "GPU", "gpu", "control", "GPU Fan 1", 1, 0),
            new RawSensor("/gpu-nvidia/0/smalldata/1", "GPU", "gpu", "smallData", "GPU Memory Used", 1, 1840),
            new RawSensor("/gpu-nvidia/0/smalldata/2", "GPU", "gpu", "smallData", "GPU Memory Total", 2, 12282),
            // The real library lists virtual memory first, with the same sensor names. It must not be picked.
            new RawSensor("/vram/data/2", "Virtual Memory", "memory", "data", "Memory Used", 2, 28.3),
            new RawSensor("/vram/data/3", "Virtual Memory", "memory", "data", "Memory Available", 3, 39.5),
            new RawSensor("/vram/load/1", "Virtual Memory", "memory", "load", "Memory", 1, 41.8),
            new RawSensor("/ram/data/0", "Memory", "memory", "data", "Memory Used", 0, 17.6),
            new RawSensor("/ram/data/1", "Memory", "memory", "data", "Memory Available", 1, 46.2),
            new RawSensor("/ram/load/0", "Memory", "memory", "load", "Memory", 0, 27.6),
        });
        return f;
    }

    public static FakeSource Full()
    {
        var f = Basic();
        f.DriverInstalled = true;
        f.Names = new("Intel Core i9-13900KF", "NVIDIA GeForce RTX 4070 Ti", "ROG STRIX Z790-A GAMING WIFI II", "Nuvoton NCT6798D");
        f.Set("/intelcpu/0/temperature/32", 47);
        f.Set("/intelcpu/0/power/0", 41.5);
        for (var i = 0; i < 7; i++)
        {
            f.Sensors.Add(new RawSensor($"/lpc/nct6798d/0/fan/{i}", "NCT", "superio", "fan", $"Fan #{i + 1}", i, i == 2 ? 0 : 800 + i * 10));
            f.Sensors.Add(new RawSensor($"/lpc/nct6798d/0/control/{i}", "NCT", "superio", "control", $"Fan #{i + 1}", i, 45));
        }
        f.Sensors.Add(new RawSensor("/lpc/nct6798d/0/temperature/1", "NCT", "superio", "temperature", "Temperature #1", 1, 33));
        f.Sensors.Add(new RawSensor("/lpc/nct6798d/0/temperature/2", "NCT", "superio", "temperature", "Temperature #2", 2, 127)); // unconnected
        return f;
    }
}

public class SensorsModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-sensors-tests-" + Guid.NewGuid().ToString("N"));

    public SensorsModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private const string Fan1 = "/lpc/nct6798d/0/control/0";
    private const string Pump = "/lpc/nct6798d/0/control/6";
    private const string CpuTemp = "/intelcpu/0/temperature/32";
    private string Marker => Path.Combine(_dir, "fan-override.active");

    private async Task<SensorsModule> Start(FakeSource source, bool elevated)
    {
        var m = new SensorsModule(source, runLoop: false);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "x.exe")), CancellationToken.None);
        m.Tick();
        return m;
    }

    private static async Task<JsonElement> Call(SensorsModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task<string> FailCode(SensorsModule m, string method, object? args = null)
        => (await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args))).Code;

    private static object[] Curve(params (int t, int d)[] pts) => pts.Select(p => (object)new { tempC = p.t, dutyPercent = p.d }).ToArray();

    [Fact]
    public async Task A_driver_installed_while_RigControl_runs_is_picked_up_without_a_restart()
    {
        var source = FakeSource.Basic();
        source.DriverInstalled = false;
        await using var m = await Start(source, elevated: true);
        Assert.False(source.OpenedLowLevel);
        Assert.Contains("not installed", (await Call(m, "getSnapshot")).GetProperty("notes")[0].GetString());

        // The first-time setup installs the driver and says so.
        source.DriverInstalled = true;
        Assert.True((await Call(m, "driverInstalled")).GetProperty("reopened").GetBoolean());
        Assert.True(source.OpenedLowLevel);
        m.Tick();
        var notes = (await Call(m, "getSnapshot")).GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        Assert.DoesNotContain(notes, n => n.Contains("not installed") || n.Contains("Restart RigControl"));

        // Asking again changes nothing.
        Assert.False((await Call(m, "driverInstalled")).GetProperty("reopened").GetBoolean());
    }

    [Fact]
    public async Task The_sensors_notice_a_new_driver_by_themselves_within_a_few_seconds()
    {
        var source = FakeSource.Basic();
        source.DriverInstalled = false;
        await using var m = await Start(source, elevated: true);
        source.DriverInstalled = true;
        for (var i = 0; i < 6 && source.OpenedLowLevel != true; i++) m.Tick();
        Assert.True(source.OpenedLowLevel);
    }

    [Fact]
    public async Task Without_administrator_rights_a_new_driver_changes_nothing()
    {
        var source = FakeSource.Basic();
        await using var m = await Start(source, elevated: false);
        source.DriverInstalled = true;
        for (var i = 0; i < 12; i++) m.Tick();
        Assert.False((await Call(m, "driverInstalled")).GetProperty("reopened").GetBoolean());
        Assert.False(source.OpenedLowLevel);
    }

    [Fact]
    public async Task Not_elevated_gives_basic_readings_and_never_opens_the_motherboard_chip()
    {
        var source = FakeSource.Basic();
        source.DriverInstalled = true;
        await using var m = await Start(source, elevated: false);

        Assert.False(source.OpenedLowLevel);
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        var s = await Call(m, "getSnapshot");
        Assert.False(s.GetProperty("elevated").GetBoolean());
        Assert.Equal(12.3, s.GetProperty("cpu").GetProperty("loadPercent").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("cpu").GetProperty("packageTempC").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("cpu").GetProperty("powerW").ValueKind); // the bogus 0 W is hidden
        Assert.Equal(41, s.GetProperty("gpu").GetProperty("tempC").GetDouble());
        Assert.Equal(50.5, s.GetProperty("gpu").GetProperty("hotspotTempC").GetDouble());
        Assert.Equal(1840, s.GetProperty("gpu").GetProperty("vramUsedMB").GetDouble());
        Assert.Equal(17.6, s.GetProperty("memory").GetProperty("usedGB").GetDouble());
        Assert.Equal(63.8, s.GetProperty("memory").GetProperty("totalGB").GetDouble(), 3);
        Assert.Equal(27.6, s.GetProperty("memory").GetProperty("loadPercent").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("board").ValueKind);
        // Only the graphics card's own fan is listed; no motherboard header.
        Assert.Equal(1, s.GetProperty("fans").GetArrayLength());
        Assert.Equal("Graphics", s.GetProperty("fans")[0].GetProperty("hardware").GetString());
        Assert.Contains("administrator", s.GetProperty("notes")[0].GetString());

        Assert.Equal("needsAdmin", await FailCode(m, "getFanControls"));
        Assert.Equal("needsAdmin", await FailCode(m, "setFanFixed", new { id = Fan1, dutyPercent = 50 }));
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task Elevated_without_the_driver_says_so_and_stays_basic()
    {
        var source = FakeSource.Basic();
        await using var m = await Start(source, elevated: true);
        Assert.False(source.OpenedLowLevel);
        var s = await Call(m, "getSnapshot");
        Assert.Contains("PawnIO", s.GetProperty("notes")[0].GetString());
        Assert.Equal("unavailable", await FailCode(m, "getFanControls"));
    }

    [Fact]
    public async Task Full_mode_reports_board_fans_and_controls()
    {
        var source = FakeSource.Full();
        await using var m = await Start(source, elevated: true);
        Assert.True(source.OpenedLowLevel);
        Assert.Equal(ModuleState.Ready, m.Status.State);

        var s = await Call(m, "getSnapshot");
        Assert.Equal(47, s.GetProperty("cpu").GetProperty("packageTempC").GetDouble());
        Assert.Equal(41.5, s.GetProperty("cpu").GetProperty("powerW").GetDouble());
        Assert.Equal("Nuvoton NCT6798D", s.GetProperty("board").GetProperty("superIo").GetString());
        Assert.Equal(7 + 1, s.GetProperty("fans").GetArrayLength());   // seven headers and the graphics card fan
        Assert.Equal(Fan1, s.GetProperty("fans")[0].GetProperty("controlId").GetString());
        Assert.Equal(0, s.GetProperty("notes").GetArrayLength());
        // The unconnected 127 °C input is not offered.
        Assert.DoesNotContain(s.GetProperty("temperatures").EnumerateArray(), t => t.GetProperty("value").GetDouble() > 120);

        var controls = await Call(m, "getFanControls");
        Assert.Equal(7, controls.GetArrayLength());
        Assert.Equal("auto", controls[0].GetProperty("mode").GetString());
        Assert.Equal(30, controls[0].GetProperty("minDutyPercent").GetInt32());
        Assert.True(controls[0].GetProperty("canControl").GetBoolean());
        Assert.False(controls[6].GetProperty("canControl").GetBoolean());
        Assert.Contains("ump", controls[6].GetProperty("reason").GetString());

        var sources = await Call(m, "getTemperatureSources");
        Assert.Contains(sources.EnumerateArray(), x => x.GetProperty("id").GetString() == CpuTemp);
        Assert.Empty(source.Writes); // nothing is changed until the user asks
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task Limits_and_the_pump_header_are_enforced()
    {
        var source = FakeSource.Full();
        await using var m = await Start(source, elevated: true);
        Assert.Equal("unsafe", await FailCode(m, "setFanFixed", new { id = Fan1, dutyPercent = 29 }));
        Assert.Equal("unsafe", await FailCode(m, "setFanFixed", new { id = Fan1, dutyPercent = 101 }));
        Assert.Equal("unsafe", await FailCode(m, "setFanFixed", new { id = Pump, dutyPercent = 60 }));
        Assert.Equal("unsafe", await FailCode(m, "setFanCurve", new { id = Pump, sourceId = CpuTemp, points = Curve((40, 40), (80, 100)) }));
        Assert.Equal("unsafe", await FailCode(m, "setFanCurve", new { id = Fan1, sourceId = CpuTemp, points = Curve((40, 20), (80, 100)) }));
        Assert.Equal("unsafe", await FailCode(m, "setFanCurve", new { id = Fan1, sourceId = CpuTemp, points = Curve((40, 80), (80, 60)) }));
        Assert.Equal("invalidArgs", await FailCode(m, "setFanCurve", new { id = Fan1, sourceId = "/nope", points = Curve((40, 40), (80, 100)) }));
        Assert.Equal("unsafe", await FailCode(m, "setFanFixed", new { id = GpuFan, dutyPercent = 10 }));   // the graphics card fan has the same floor
        Assert.Equal("invalidArgs", await FailCode(m, "setFanFixed", new { id = "/gpu-nvidia/9/control/9", dutyPercent = 60 }));
        Assert.Equal("unknownMethod", await FailCode(m, "nope"));
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task On_other_boards_no_header_is_assumed_to_be_the_pump_but_the_floor_still_holds()
    {
        var source = FakeSource.Full();
        source.Names = source.Names with { Board = "MAG B760 TOMAHAWK WIFI" };
        await using var m = await Start(source, elevated: true);
        var controls = await Call(m, "getFanControls");
        var seventh = controls.EnumerateArray().Single(c => c.GetProperty("id").GetString() == Pump);
        Assert.Equal("Fan header 7", seventh.GetProperty("name").GetString());
        Assert.Equal(30, seventh.GetProperty("minDutyPercent").GetInt32());
        Assert.Equal("unsafe", await FailCode(m, "setFanFixed", new { id = Pump, dutyPercent = 20 }));
        await Call(m, "setFanFixed", new { id = Pump, dutyPercent = 60 });
        Assert.Equal((Pump, 60.0), source.Writes[^1]);
        var notes = (await Call(m, "getSnapshot")).GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains(notes, n => n!.Contains("leave that header on Automatic"));
    }

    private const string GpuFan = "/gpu-nvidia/0/control/1";
    private const string GpuTemp = "/gpu-nvidia/0/temperature/0";

    [Fact]
    public async Task Graphics_card_fans_work_without_the_motherboard_driver_and_go_back_to_automatic()
    {
        var source = FakeSource.Basic();                       // not elevated, no driver: motherboard fans are off limits
        var m = await Start(source, elevated: false);
        Assert.Equal("needsAdmin", await FailCode(m, "getFanControls"));

        var fans = await Call(m, "getGpuFans");
        var fan = Assert.Single(fans.EnumerateArray());
        Assert.Equal(GpuFan, fan.GetProperty("id").GetString());
        Assert.Equal("auto", fan.GetProperty("mode").GetString());
        Assert.Equal(30, fan.GetProperty("minDutyPercent").GetInt32());
        Assert.Empty(source.Writes);                            // nothing is touched until asked

        var set = await Call(m, "setFanFixed", new { id = GpuFan, dutyPercent = 55 });
        Assert.Equal("fixed", set.GetProperty("mode").GetString());
        Assert.Equal((GpuFan, 55.0), source.Writes[^1]);

        // A curve on the card's own temperature: 41 degrees on a 40->80 / 30->100 line is about 32 %.
        set = await Call(m, "setFanCurve", new { id = GpuFan, sourceId = GpuTemp, points = Curve((40, 30), (80, 100)) });
        Assert.Equal("curve", set.GetProperty("mode").GetString());
        Assert.InRange(source.Writes[^1].Percent!.Value, 31, 33);
        source.Set(GpuTemp, 70);
        m.Tick();
        Assert.InRange(source.Writes[^1].Percent!.Value, 81, 84);
        // If the temperature cannot be read, the fan goes to full speed.
        source.Set(GpuTemp, null);
        m.Tick();
        Assert.Equal(100, source.Writes[^1].Percent);

        // Closing RigControl hands the fan back to the card.
        await m.DisposeAsync();
        Assert.Equal((GpuFan, (double?)null), source.Writes[^1]);

        // The next start puts the saved curve back; "automatic" removes it for good.
        source.Set(GpuTemp, 41);
        await using var again = await Start(source, elevated: false);
        Assert.InRange(source.Writes[^1].Percent!.Value, 31, 33);
        set = await Call(again, "setFanAuto", new { id = GpuFan });
        Assert.Equal("auto", set.GetProperty("mode").GetString());
        Assert.Equal((GpuFan, (double?)null), source.Writes[^1]);
        var before = source.Writes.Count;
        again.Tick();
        Assert.Equal(before, source.Writes.Count);
    }

    [Fact]
    public async Task A_curve_follows_its_temperature_and_goes_full_speed_when_it_cannot_be_read()
    {
        var source = FakeSource.Full();
        await using var m = await Start(source, elevated: true);
        var c = await Call(m, "setFanCurve", new { id = Fan1, sourceId = CpuTemp, points = Curve((40, 40), (80, 100)) });
        Assert.Equal("curve", c.GetProperty("mode").GetString());
        Assert.Equal(51, c.GetProperty("dutyPercent").GetInt32()); // 47 °C on this curve is 50.5 %, rounded up
        source.Set(CpuTemp, 60);
        m.Tick();
        Assert.Equal(70, source.Writes[^1].Percent);
        source.Set(CpuTemp, null);
        m.Tick();
        Assert.Equal(100, source.Writes[^1].Percent);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task Exit_hands_fans_back_and_clears_the_marker_and_a_restart_reapplies()
    {
        var source = FakeSource.Full();
        var m = await Start(source, elevated: true);
        await Call(m, "setFanFixed", new { id = Fan1, dutyPercent = 60 });
        Assert.True(File.Exists(Marker));
        await m.DisposeAsync();
        await m.DisposeAsync();
        Assert.True(source.Disposed);
        Assert.False(File.Exists(Marker));

        var second = FakeSource.Full();
        await using var again = await Start(second, elevated: true);
        Assert.Contains(second.Writes, w => w.Id == Fan1 && w.Percent == 60);
    }

    [Fact]
    public async Task After_a_crash_nothing_is_reapplied_and_the_user_is_told()
    {
        var first = FakeSource.Full();
        var m = await Start(first, elevated: true);
        await Call(m, "setFanFixed", new { id = Fan1, dutyPercent = 60 });
        // No DisposeAsync: simulates the process being killed. The marker file stays.

        var second = FakeSource.Full();
        await using var again = await Start(second, elevated: true);
        again.Tick();
        Assert.Empty(second.Writes);
        var s = await Call(again, "getSnapshot");
        Assert.Contains(s.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("did not shut down cleanly"));
        GC.KeepAlive(m);
    }

    [Fact]
    public async Task Handing_a_header_back_to_the_bios()
    {
        var source = FakeSource.Full();
        await using var m = await Start(source, elevated: true);
        await Call(m, "setFanFixed", new { id = Fan1, dutyPercent = 60 });
        var c = await Call(m, "setFanAuto", new { id = Fan1 });
        Assert.Equal("auto", c.GetProperty("mode").GetString());
        Assert.Null(source.Writes[^1].Percent);
        var count = source.Writes.Count;
        m.Tick();
        Assert.Equal(count, source.Writes.Count); // no longer driven
    }

    [Fact]
    public async Task Another_program_overriding_a_fan_is_noticed()
    {
        var source = FakeSource.Full();
        await using var m = await Start(source, elevated: true);
        await Call(m, "setFanFixed", new { id = Fan1, dutyPercent = 60 });
        source.IgnoreWrites = true;
        for (var i = 0; i < 6; i++)
        {
            source.Set(Fan1, 35); // something else keeps putting it back
            m.Tick();
        }
        var s = await Call(m, "getSnapshot");
        Assert.Contains(s.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("not following RigControl"));
    }
}
