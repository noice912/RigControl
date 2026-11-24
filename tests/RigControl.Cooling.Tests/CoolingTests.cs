using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Cooling.Tests;

internal static class Hex
{
    public static byte[] Parse(string hex, int length = 64)
    {
        var bytes = hex.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();
        var result = new byte[Math.Max(length, bytes.Length)];
        bytes.CopyTo(result, 0);
        return result;
    }
}

/// <summary>Reference packets from docs/protocols/nzxt-kraken-elite.md, section 13.</summary>
public class KrakenProtocolTests
{
    [Fact]
    public void Simple_requests_match_the_spec()
    {
        Assert.Equal(Hex.Parse("10 01"), KrakenProtocol.GetFirmware());
        Assert.Equal(Hex.Parse("74 01"), KrakenProtocol.GetStatus());
        Assert.Equal(Hex.Parse("30 01"), KrakenProtocol.GetLcdInfo());
    }

    [Fact]
    public void TV5_pump_fixed_84()
    {
        var expected = Hex.Parse("72 01 01 00 " + string.Join(" ", Enumerable.Repeat("54", 39)) + " 64");
        Assert.Equal(expected, KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Pump, KrakenProtocol.BuildFixedDuties(84, 20)));
    }

    [Fact]
    public void TV6_fan_fixed_50()
    {
        var expected = Hex.Parse("72 02 01 01 " + string.Join(" ", Enumerable.Repeat("32", 39)) + " 64");
        Assert.Equal(expected, KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Fan, KrakenProtocol.BuildFixedDuties(50, 0)));
    }

    [Fact]
    public void TV7_pump_curve_with_round_half_to_even()
    {
        var duties = KrakenProtocol.BuildDuties(new[] { (20, 30), (30, 50), (34, 80), (40, 90), (50, 100) }, 20);
        var expected = new byte[]
        {
            30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 58, 65, 72, 80, 82, 83, 85, 87, 88,
            90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100,
        };
        Assert.Equal(expected, duties);
        var packet = KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Pump, duties);
        Assert.Equal(new byte[] { 0x72, 0x01, 0x01, 0x00, 0x1E }, packet[..5]);
        Assert.All(packet[44..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void TV9_pump_below_minimum_is_clamped_to_20()
    {
        var duties = KrakenProtocol.BuildFixedDuties(10, 20);
        Assert.All(duties[..39], d => Assert.Equal(20, d));
        Assert.Equal(100, duties[39]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(100)]
    public void Every_profile_ends_at_100_and_never_decreases(int duty)
    {
        var duties = KrakenProtocol.BuildFixedDuties(duty, 20);
        Assert.Equal(100, duties[^1]);
        for (var i = 1; i < duties.Length; i++) Assert.True(duties[i] >= duties[i - 1]);
        Assert.All(duties, d => Assert.InRange(d, (byte)20, (byte)100));
    }

    [Fact]
    public void A_curve_that_dips_is_made_non_decreasing()
    {
        var duties = KrakenProtocol.BuildDuties(new[] { (30, 40), (35, 30), (40, 35) }, 20);
        for (var i = 1; i < duties.Length; i++) Assert.True(duties[i] >= duties[i - 1]);
    }

    [Fact]
    public void Unsafe_profiles_cannot_be_turned_into_packets()
    {
        var low = Enumerable.Repeat((byte)10, 39).Append((byte)100).ToArray();
        Assert.Throws<ArgumentException>(() => KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Pump, low));
        var noFailsafe = Enumerable.Repeat((byte)50, 40).ToArray();
        Assert.Throws<ArgumentException>(() => KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Pump, noFailsafe));
        Assert.Throws<ArgumentException>(() => KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Fan, new byte[39]));
    }

    [Fact]
    public void TV12_and_TV13_lcd()
    {
        Assert.Equal(Hex.Parse("30 02 01 3C 00 00 01 00"), KrakenProtocol.SetLcd(60, 0));
        Assert.Equal(Hex.Parse("30 02 01 32 00 00 01 01"), KrakenProtocol.SetLcd(50, 90));
        Assert.Throws<ArgumentOutOfRangeException>(() => KrakenProtocol.SetLcd(101, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => KrakenProtocol.SetLcd(50, 45));
    }

    [Fact]
    public void TV14_status_from_a_real_capture()
    {
        var s = KrakenProtocol.ParseStatus(Hex.Parse("75 01 2E 00 18 00 10 51 39 34 34 36 37 31 01 18 03 69 03 14 14 01 02 00 00 00"))!;
        Assert.Equal(24.3, s.LiquidTempC!.Value, 3);
        Assert.Equal(873, s.PumpRpm);
        Assert.Equal(20, s.PumpDuty);
        Assert.Equal(0, s.FanRpm);
        Assert.Equal(0, s.FanDuty);
        Assert.False(s.SensorFault);
    }

    [Fact]
    public void TV19_pushed_status_from_a_real_Elite()
    {
        var s = KrakenProtocol.ParseStatus(Hex.Parse("75 02 bb 8c 90 82 0e 58 0a 31 00 00 00 00 01 1e 02 12 05 1e 1e 01 02 fc 03 32 32"))!;
        Assert.Equal(30.2, s.LiquidTempC!.Value, 3);
        Assert.Equal(1298, s.PumpRpm);
        Assert.Equal(30, s.PumpDuty);
        Assert.Equal(1020, s.FanRpm);
        Assert.Equal(50, s.FanDuty);
    }

    [Fact]
    public void Sensor_fault_is_reported_not_shown_as_a_temperature()
    {
        var r = Hex.Parse("75 01");
        r[15] = 0xFF;
        r[16] = 0xFF;
        var s = KrakenProtocol.ParseStatus(r)!;
        Assert.True(s.SensorFault);
        Assert.Null(s.LiquidTempC);
    }

    [Fact]
    public void Other_reports_are_not_mistaken_for_status()
    {
        Assert.Null(KrakenProtocol.ParseStatus(Hex.Parse("73 01")));
        Assert.Null(KrakenProtocol.ParseStatus(new byte[10]));
        Assert.Null(KrakenProtocol.ParseFirmware(Hex.Parse("75 01")));
    }

    [Fact]
    public void Firmware_and_lcd_replies()
    {
        var fw = Hex.Parse("11 01");
        fw[17] = 2; fw[18] = 1; fw[19] = 0;
        Assert.Equal("2.1.0", KrakenProtocol.ParseFirmware(fw));
        var lcd = Hex.Parse("31 01");
        lcd[24] = 100; lcd[26] = 3;
        Assert.Equal(new KrakenProtocol.LcdInfo(100, 270), KrakenProtocol.ParseLcdInfo(lcd));
    }

    [Theory]
    [InlineData("70 02 01 B8 01")]   // init: deliberately not used
    [InlineData("36 03")]            // LCD bucket commands
    [InlineData("32 01")]
    [InlineData("72 01 00 00")]      // old-model header
    [InlineData("72 03 01 01")]      // unknown channel
    [InlineData("20 03")]
    public void Anything_off_the_allow_list_is_refused(string hex)
        => Assert.False(KrakenProtocol.IsAllowed(Hex.Parse(hex)));

    [Fact]
    public void Built_packets_are_on_the_allow_list()
    {
        Assert.True(KrakenProtocol.IsAllowed(KrakenProtocol.GetStatus()));
        Assert.True(KrakenProtocol.IsAllowed(KrakenProtocol.SetLcd(50, 0)));
        Assert.True(KrakenProtocol.IsAllowed(KrakenProtocol.SetSpeedProfile(KrakenProtocol.Channel.Fan, KrakenProtocol.BuildFixedDuties(40, 20))));
        Assert.False(KrakenProtocol.IsAllowed(new byte[65]));
    }
}

/// <summary>Reference packets from docs/protocols/nzxt-controllers.md, section 14.</summary>
public class FanControllerProtocolTests
{
    [Fact]
    public void TV4_to_TV7()
    {
        Assert.Equal(Hex.Parse("62 01 04 00 00 32"), FanControllerProtocol.SetDuty(2, 50));
        Assert.Equal(Hex.Parse("62 01 01 64"), FanControllerProtocol.SetDuty(0, 100));
        Assert.Equal(Hex.Parse("62 01 02 00 1E"), FanControllerProtocol.SetDuty(1, 30));
        Assert.Equal(Hex.Parse("60 02 01 E8 03 01 E8 03"), FanControllerProtocol.SetIntervalOneSecond());
        Assert.Equal(Hex.Parse("60 03"), FanControllerProtocol.DetectFans());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 24)]
    [InlineData(0, 101)]
    [InlineData(3, 50)]
    [InlineData(-1, 50)]
    public void A_fan_can_never_be_stopped_or_addressed_out_of_range(int channel, int duty)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FanControllerProtocol.SetDuty(channel, duty));

    [Theory]
    [InlineData("62 01 01 00")]          // mask set, duty 0 => would stop the fan
    [InlineData("62 01 03 32 32")]       // two channels in one packet
    [InlineData("62 01 01 32 32")]       // stray duty byte for another channel
    [InlineData("62 01 08 00 00 00 32")] // channel that does not exist
    [InlineData("60 02 01 E8 00 01 E8 00")]
    [InlineData("20 03")]
    [InlineData("22 10")]
    public void Anything_off_the_allow_list_is_refused(string hex)
        => Assert.False(FanControllerProtocol.IsAllowed(Hex.Parse(hex)));

    [Fact]
    public void Status_parse()
    {
        var r = Hex.Parse("67 02");
        r[16] = 2; r[17] = 1; r[18] = 0;
        r[24] = 0x34; r[25] = 0x03;       // fan1 820 rpm
        r[26] = 0x10; r[27] = 0x02;       // fan2 528 rpm
        r[40] = 40; r[41] = 55; r[42] = 30;
        var fans = FanControllerProtocol.ParseStatus(r)!;
        Assert.Equal(new FanControllerProtocol.FanStatus(2, 820, 40), fans[0]);
        Assert.Equal(new FanControllerProtocol.FanStatus(1, 528, 55), fans[1]);
        Assert.False(fans[2].Present);
    }

    [Fact]
    public void Implausible_status_is_rejected()
    {
        var r = Hex.Parse("67 02");
        r[16] = 3;
        Assert.Null(FanControllerProtocol.ParseStatus(r));
        r[16] = 2;
        r[40] = 101;
        Assert.Null(FanControllerProtocol.ParseStatus(r));
        Assert.Null(FanControllerProtocol.ParseStatus(Hex.Parse("67 04")));
    }
}

// ---------------------------------------------------------------- module behaviour with fakes

internal sealed class FakeLink : IHidLink
{
    public int MaxInputLength { get; init; } = 64;
    public int MaxOutputLength { get; init; } = 64;
    public List<byte[]> Written { get; } = new();
    public Queue<byte[]> Incoming { get; } = new();
    public Func<byte[], byte[]?>? Reply { get; set; }
    public bool Disposed { get; private set; }

    public void Write(byte[] report)
    {
        Written.Add(report);
        if (Reply?.Invoke(report) is { } r) Incoming.Enqueue(r);
    }

    public byte[]? Read(int timeoutMs) => Incoming.Count > 0 ? Incoming.Dequeue() : null;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeBus : IHidBus
{
    public FakeLink? Kraken { get; set; }
    public FakeLink? Controller { get; set; }

    /// <summary>True = the device is plugged in but another program (NZXT CAM) holds it exclusively.</summary>
    public bool KrakenHeldByOther { get; set; }
    public bool ControllerHeldByOther { get; set; }

    public bool IsPresent(int vendorId, int productId) => productId switch
    {
        KrakenProtocol.ProductId => Kraken is not null || KrakenHeldByOther,
        FanControllerProtocol.ProductId => Controller is not null || ControllerHeldByOther,
        _ => throw new InvalidOperationException("RigControl must never look for any other NZXT device."),
    };

    public IHidLink? OpenSingle(int vendorId, int productId) => productId switch
    {
        KrakenProtocol.ProductId => Kraken,
        FanControllerProtocol.ProductId => Controller,
        _ => throw new InvalidOperationException("RigControl must never open any other NZXT device."),
    };
}

internal sealed class FakeCam : ICamControl
{
    public CamState State { get; set; } = new(false, false, false);
    public bool CloseAppCalled { get; private set; }
    public bool StopServiceCalled { get; private set; }
    public bool? AutoStartSetTo { get; private set; }

    public CamState GetState() => State;

    public bool CloseApp()
    {
        CloseAppCalled = true;
        State = State with { AppRunning = false };
        return true;
    }

    public bool StopService()
    {
        StopServiceCalled = true;
        State = State with { ServiceRunning = false };
        return true;
    }

    public void SetStartsWithWindows(bool enabled)
    {
        AutoStartSetTo = enabled;
        State = State with { StartsWithWindows = enabled };
    }
}

public class CoolingModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-cooling-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBus _bus = new();
    private readonly FakeCam _cam = new();

    public CoolingModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static byte[] StatusReport(double temp = 31.4, int pumpRpm = 2380, int pumpDuty = 70, int fanRpm = 940, int fanDuty = 45)
    {
        var r = Hex.Parse("75 01");
        r[15] = (byte)Math.Floor(temp);
        r[16] = (byte)Math.Round((temp - Math.Floor(temp)) * 10);
        r[17] = (byte)(pumpRpm & 0xFF); r[18] = (byte)(pumpRpm >> 8); r[19] = (byte)pumpDuty;
        r[23] = (byte)(fanRpm & 0xFF); r[24] = (byte)(fanRpm >> 8); r[25] = (byte)fanDuty;
        return r;
    }

    private static FakeLink KrakenLink()
    {
        var link = new FakeLink();
        var brightness = 70;
        var orientation = 0;
        link.Reply = w =>
        {
            if (w[0] == 0x74) return StatusReport();
            if (w[0] == 0x10)
            {
                var fw = Hex.Parse("11 01");
                fw[17] = 2; fw[18] = 1; fw[19] = 0;
                return fw;
            }
            if (w[0] == 0x30 && w[1] == 0x02)
            {
                brightness = w[3];
                orientation = w[7];
                return null;
            }
            if (w[0] == 0x30 && w[1] == 0x01)
            {
                var lcd = Hex.Parse("31 01");
                lcd[24] = (byte)brightness; lcd[26] = (byte)orientation;
                return lcd;
            }
            return null;
        };
        return link;
    }

    private static FakeLink ControllerLink()
    {
        var link = new FakeLink();
        var r = Hex.Parse("67 02");
        r[16] = 2; r[17] = 2; r[18] = 0;
        r[24] = 0x34; r[25] = 0x03; r[26] = 0x40; r[27] = 0x03;
        r[40] = 40; r[41] = 40; r[42] = 40;
        link.Incoming.Enqueue(r);
        return link;
    }

    private async Task<CoolingModule> Start(bool elevated = false)
    {
        var module = new CoolingModule(_bus, _cam, runLoop: false);
        var ctx = new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "x.exe"));
        // Do not start the background thread's timing in tests: drive Tick() by hand.
        await module.StartAsync(ctx, CancellationToken.None);
        module.Tick();
        return module;
    }

    private static async Task<JsonElement> Call(CoolingModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        var result = await m.InvokeAsync(method, a, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
    }

    private static async Task<RpcException> Fails(CoolingModule m, string method, object? args = null)
        => await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args));

    [Fact]
    public async Task While_cam_runs_nothing_is_ever_written()
    {
        _cam.State = new CamState(true, true, true);
        _bus.Kraken = KrakenLink();
        _bus.Controller = ControllerLink();
        _bus.Kraken.Incoming.Enqueue(StatusReport(temp: 30.2));
        await using var m = await Start();
        m.Tick();

        var status = await Call(m, "getDevices");
        Assert.Equal("NZXT CAM", status.GetProperty("blockedBy")[0].GetString());
        // Readings that CAM's own polling produced are still shown.
        Assert.Equal(30.2, status.GetProperty("devices")[0].GetProperty("liquidTempC").GetDouble(), 3);

        Assert.Equal("unavailable", (await Fails(m, "setFixed", new { deviceId = "kraken", channel = "pump", dutyPercent = 80 })).Code);
        Assert.Equal("unavailable", (await Fails(m, "setCurve", new { deviceId = "kraken", channel = "fan", points = new[] { new { tempC = 25, dutyPercent = 30 }, new { tempC = 50, dutyPercent = 100 } } })).Code);
        Assert.Equal("unavailable", (await Fails(m, "setLcd", new { deviceId = "kraken", brightness = 10 })).Code);
        Assert.Equal("unavailable", (await Fails(m, "setFixed", new { deviceId = "fan-controller", channel = "fan1", dutyPercent = 50 })).Code);

        Assert.Empty(_bus.Kraken.Written);
        Assert.Empty(_bus.Controller.Written);
    }

    [Fact]
    public async Task Devices_cam_holds_exclusively_are_still_listed_with_an_explanation()
    {
        // What really happens on this PC: while CAM runs, Windows refuses to open the NZXT devices at all.
        _cam.State = new CamState(true, true, true);
        _bus.KrakenHeldByOther = true;
        _bus.ControllerHeldByOther = true;
        await using var m = await Start();
        var status = await Call(m, "getDevices");
        Assert.Equal(2, status.GetProperty("devices").GetArrayLength());
        var kraken = status.GetProperty("devices")[0];
        Assert.Equal("kraken", kraken.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, kraken.GetProperty("liquidTempC").ValueKind);
        Assert.Equal(JsonValueKind.Null, kraken.GetProperty("channels")[0].GetProperty("rpm").ValueKind);
        Assert.Contains("NZXT CAM", kraken.GetProperty("notes")[0].GetString());
        Assert.Equal(3, status.GetProperty("devices")[1].GetProperty("channels").GetArrayLength());
        Assert.Equal("unavailable", (await Fails(m, "setFixed", new { deviceId = "kraken", channel = "pump", dutyPercent = 80 })).Code);
    }

    [Fact]
    public async Task Without_cam_status_is_polled_and_shapes_match_the_contract()
    {
        _bus.Kraken = KrakenLink();
        _bus.Controller = ControllerLink();
        await using var m = await Start();
        m.Tick();

        var status = await Call(m, "getDevices");
        Assert.Equal(0, status.GetProperty("blockedBy").GetArrayLength());
        var kraken = status.GetProperty("devices")[0];
        Assert.Equal("kraken", kraken.GetProperty("id").GetString());
        Assert.Equal("2.1.0", kraken.GetProperty("firmware").GetString());
        Assert.Equal(31.4, kraken.GetProperty("liquidTempC").GetDouble(), 3);
        Assert.Equal(70, kraken.GetProperty("lcd").GetProperty("brightness").GetInt32());
        var pump = kraken.GetProperty("channels")[0];
        Assert.Equal("pump", pump.GetProperty("kind").GetString());
        Assert.Equal(2380, pump.GetProperty("rpm").GetInt32());
        Assert.Equal(20, pump.GetProperty("minDutyPercent").GetInt32());
        Assert.Equal(20, pump.GetProperty("curveTempRange").GetProperty("min").GetInt32());
        Assert.Equal(59, pump.GetProperty("curveTempRange").GetProperty("max").GetInt32());
        Assert.Equal("liquid", pump.GetProperty("curveSource").GetString());
        Assert.True(pump.GetProperty("storedOnDevice").GetBoolean());
        Assert.Equal("unknown", pump.GetProperty("mode").GetString());

        var controller = status.GetProperty("devices")[1];
        Assert.Equal(2, controller.GetProperty("channels").GetArrayLength()); // fan3 has no fan and is hidden
        var fan1 = controller.GetProperty("channels")[0];
        Assert.Equal(820, fan1.GetProperty("rpm").GetInt32());
        Assert.Equal(25, fan1.GetProperty("minDutyPercent").GetInt32());
        Assert.Equal(59, fan1.GetProperty("curveTempRange").GetProperty("max").GetInt32());   // case fans can follow the liquid temperature
        Assert.False(fan1.GetProperty("storedOnDevice").GetBoolean());

        // Nothing but allow-listed requests went out, and no speed was changed on its own.
        Assert.All(_bus.Kraken.Written, p => Assert.True(KrakenProtocol.IsAllowed(p)));
        Assert.DoesNotContain(_bus.Kraken.Written, p => p[0] == 0x72);
        Assert.All(_bus.Controller.Written, p => Assert.True(FanControllerProtocol.IsAllowed(p)));
        Assert.DoesNotContain(_bus.Controller.Written, p => p[0] == 0x62);
    }

    [Fact]
    public async Task Case_fans_follow_the_liquid_temperature_and_are_left_safe_on_exit()
    {
        var kraken = KrakenLink();
        var temp = 31.4;
        var inner = kraken.Reply!;
        kraken.Reply = w => w[0] == 0x74 ? StatusReport(temp: temp) : inner(w);
        _bus.Kraken = kraken;
        var controller = ControllerLink();
        _bus.Controller = controller;
        var m = await Start();
        m.Tick();

        var points = new[] { new { tempC = 25, dutyPercent = 30 }, new { tempC = 55, dutyPercent = 90 } };
        var device = await Call(m, "setCurve", new { deviceId = "fan-controller", channel = "fan1", points });
        var ch = device.GetProperty("channels")[0];
        Assert.Equal("curve", ch.GetProperty("mode").GetString());
        Assert.False(ch.GetProperty("storedOnDevice").GetBoolean());
        // 31.4 degrees on a 25->55 / 30->90 line is about 43 %.
        var first = controller.Written[^1];
        Assert.Equal(new byte[] { 0x62, 0x01, 0x01 }, first[..3]);
        Assert.InRange(first[3], (byte)42, (byte)44);

        // Hotter liquid: the fan speeds up on a later tick (changes are at most every two seconds).
        temp = 50.0;
        var before = controller.Written.Count;
        m.Tick();
        await Task.Delay(2100);
        m.Tick();
        Assert.True(controller.Written.Count > before);
        Assert.InRange(controller.Written[^1][3], (byte)79, (byte)81);

        // Cooler again, then RigControl closes: the fan is not left slow.
        temp = 26.0;
        await Task.Delay(2100);
        m.Tick();
        Assert.InRange(controller.Written[^1][3], (byte)31, (byte)33);
        await m.DisposeAsync();
        Assert.Equal(new byte[] { 0x62, 0x01, 0x01, CoolingModule.ControllerExitDuty }, controller.Written[^1][..4]);

        // A fixed speed replaces the curve.
        _bus.Kraken = KrakenLink();
        _bus.Controller = ControllerLink();
        await using var again = await Start();
        device = await Call(again, "setFixed", new { deviceId = "fan-controller", channel = "fan1", dutyPercent = 45 });
        Assert.Equal("fixed", device.GetProperty("channels")[0].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Pump_and_fan_limits_are_enforced()
    {
        _bus.Kraken = KrakenLink();
        _bus.Controller = ControllerLink();
        await using var m = await Start();

        Assert.Equal("unsafe", (await Fails(m, "setFixed", new { deviceId = "kraken", channel = "pump", dutyPercent = 19 })).Code);
        Assert.Equal("unsafe", (await Fails(m, "setFixed", new { deviceId = "kraken", channel = "fan", dutyPercent = 0 })).Code);
        Assert.Equal("unsafe", (await Fails(m, "setFixed", new { deviceId = "fan-controller", channel = "fan1", dutyPercent = 24 })).Code);
        Assert.Equal("unsafe", (await Fails(m, "setCurve", new { deviceId = "kraken", channel = "pump", points = new[] { new { tempC = 25, dutyPercent = 10 }, new { tempC = 50, dutyPercent = 100 } } })).Code);
        Assert.Equal("unsafe", (await Fails(m, "setCurve", new { deviceId = "kraken", channel = "pump", points = new[] { new { tempC = 25, dutyPercent = 80 }, new { tempC = 50, dutyPercent = 60 } } })).Code);
        Assert.Equal("invalidArgs", (await Fails(m, "setCurve", new { deviceId = "kraken", channel = "pump", points = new[] { new { tempC = 10, dutyPercent = 40 }, new { tempC = 50, dutyPercent = 100 } } })).Code);
        // Case fans can follow a curve too, but never below their minimum and only within the liquid range.
        Assert.Equal("unsafe", (await Fails(m, "setCurve", new { deviceId = "fan-controller", channel = "fan1", points = new[] { new { tempC = 25, dutyPercent = 10 }, new { tempC = 50, dutyPercent = 100 } } })).Code);
        Assert.Equal("invalidArgs", (await Fails(m, "setCurve", new { deviceId = "fan-controller", channel = "fan1", points = new[] { new { tempC = 25, dutyPercent = 40 }, new { tempC = 80, dutyPercent = 100 } } })).Code);
        Assert.Equal("unavailable", (await Fails(m, "setCurve", new { deviceId = "fan-controller", channel = "fan3", points = new[] { new { tempC = 25, dutyPercent = 40 }, new { tempC = 50, dutyPercent = 100 } } })).Code);
        Assert.Equal("unavailable", (await Fails(m, "setFixed", new { deviceId = "fan-controller", channel = "fan3", dutyPercent = 50 })).Code); // no fan there
        Assert.Equal("invalidArgs", (await Fails(m, "setFixed", new { deviceId = "kraken", channel = "nope", dutyPercent = 50 })).Code);

        Assert.DoesNotContain(_bus.Kraken.Written, p => p[0] == 0x72);
        Assert.DoesNotContain(_bus.Controller.Written, p => p[0] == 0x62);
    }

    [Fact]
    public async Task Applying_a_curve_sends_the_right_packet_and_is_remembered()
    {
        _bus.Kraken = KrakenLink();
        var m = await Start();
        var points = new[] { new { tempC = 20, dutyPercent = 30 }, new { tempC = 30, dutyPercent = 50 }, new { tempC = 34, dutyPercent = 80 }, new { tempC = 40, dutyPercent = 90 }, new { tempC = 50, dutyPercent = 100 } };
        var device = await Call(m, "setCurve", new { deviceId = "kraken", channel = "pump", points });

        var sent = _bus.Kraken.Written.Single(p => p[0] == 0x72);
        Assert.Equal(new byte[] { 0x72, 0x01, 0x01, 0x00, 0x1E, 0x20 }, sent[..6]);
        Assert.Equal(100, sent[43]);
        Assert.Equal("curve", device.GetProperty("channels")[0].GetProperty("mode").GetString());
        Assert.Equal(5, device.GetProperty("channels")[0].GetProperty("curve").GetArrayLength());
        await m.DisposeAsync();

        // A new session re-applies the saved curve (the cooler forgets it when it loses power).
        _bus.Kraken = KrakenLink();
        await using var again = await Start();
        var resent = _bus.Kraken.Written.Single(p => p[0] == 0x72);
        Assert.Equal(sent, resent);
    }

    [Fact]
    public async Task Fixed_fan_duty_on_the_controller()
    {
        _bus.Controller = ControllerLink();
        await using var m = await Start();
        var device = await Call(m, "setFixed", new { deviceId = "fan-controller", channel = "fan2", dutyPercent = 60 });
        Assert.Equal(Hex.Parse("62 01 02 00 3C"), _bus.Controller.Written.Single(p => p[0] == 0x62));
        Assert.Equal(60, device.GetProperty("channels")[1].GetProperty("fixedDutyPercent").GetInt32());
    }

    [Fact]
    public async Task Lcd_change_keeps_the_other_value_and_is_verified()
    {
        _bus.Kraken = KrakenLink();
        await using var m = await Start();
        var device = await Call(m, "setLcd", new { deviceId = "kraken", orientation = 90 });
        Assert.Equal(Hex.Parse("30 02 01 46 00 00 01 01"), _bus.Kraken.Written.Single(p => p[0] == 0x30 && p[1] == 0x02));
        Assert.Equal(90, device.GetProperty("lcd").GetProperty("orientation").GetInt32());
        Assert.Equal("invalidArgs", (await Fails(m, "setLcd", new { deviceId = "kraken", orientation = 45 })).Code);
    }

    [Fact]
    public async Task A_device_with_an_unexpected_report_size_is_never_written_to()
    {
        _bus.Kraken = new FakeLink { MaxInputLength = 65, MaxOutputLength = 65 };
        await using var m = await Start();
        m.Tick();
        Assert.Equal("unavailable", (await Fails(m, "setFixed", new { deviceId = "kraken", channel = "pump", dutyPercent = 80 })).Code);
        Assert.Empty(_bus.Kraken.Written);
    }

    [Fact]
    public async Task Closing_cam_needs_admin_when_its_service_runs()
    {
        _cam.State = new CamState(true, true, true);
        await using var m = await Start(elevated: false);
        Assert.Equal("needsAdmin", (await Fails(m, "closeCam")).Code);
        Assert.False(_cam.CloseAppCalled);
    }

    [Fact]
    public async Task Closing_cam_as_admin_unblocks_and_can_stop_its_autostart()
    {
        _cam.State = new CamState(true, true, true);
        _bus.Kraken = KrakenLink();
        await using var m = await Start(elevated: true);
        var status = await Call(m, "closeCam", new { disableAutoStart = true });
        Assert.True(_cam.CloseAppCalled);
        Assert.True(_cam.StopServiceCalled);
        Assert.False(_cam.AutoStartSetTo);
        Assert.Equal(0, status.GetProperty("blockedBy").GetArrayLength());
        Assert.False(status.GetProperty("cam").GetProperty("startsWithWindows").GetBoolean());

        await Call(m, "setCamAutoStart", new { enabled = true });
        Assert.True(_cam.AutoStartSetTo);
    }

    [Fact]
    public async Task Exit_sends_nothing_and_is_idempotent()
    {
        _bus.Kraken = KrakenLink();
        var m = await Start();
        await m.DisposeAsync();
        await m.DisposeAsync();
        // Only read requests were ever sent: nothing that changes a speed or the screen, at exit or before.
        Assert.All(_bus.Kraken.Written, p => Assert.Contains(p[0], new byte[] { 0x74, 0x10, 0x30 }));
        Assert.DoesNotContain(_bus.Kraken.Written, p => p[0] == 0x30 && p[1] == 0x02);
        Assert.True(_bus.Kraken.Disposed);
        Assert.True(_bus.Kraken.Disposed);
        Assert.Equal(ModuleState.Stopped, m.Status.State);
    }

    [Fact]
    public async Task No_devices_is_degraded_not_failed()
    {
        await using var m = await Start();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        Assert.Equal("unknownMethod", (await Fails(m, "nope")).Code);
    }
}
