using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Lighting.Tests;

internal static class Hex
{
    public static byte[] Parse(string hex)
        => hex.Split(new[] { ' ', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();
}

/// <summary>Example packets from docs/protocols/openrgb-integration.md, section 3.10.</summary>
public class OpenRgbProtocolTests
{
    [Fact]
    public void Requests_match_the_spec_examples()
    {
        Assert.Equal(Hex.Parse("4F 52 47 42 00 00 00 00 28 00 00 00 04 00 00 00 05 00 00 00"), OpenRgbProtocol.ProtocolVersionRequest(5));
        Assert.Equal(Hex.Parse("4F 52 47 42 00 00 00 00 32 00 00 00 0B 00 00 00 52 69 67 43 6F 6E 74 72 6F 6C 00"), OpenRgbProtocol.ClientName("RigControl"));
        Assert.Equal(Hex.Parse("4F 52 47 42 00 00 00 00 00 00 00 00 00 00 00 00"), OpenRgbProtocol.ControllerCountRequest());
        Assert.Equal(Hex.Parse("4F 52 47 42 02 00 00 00 01 00 00 00 04 00 00 00 05 00 00 00"), OpenRgbProtocol.ControllerDataRequest(2, 5));
    }

    [Fact]
    public void UpdateLeds_example_red_then_green()
    {
        var red = OpenRgbProtocol.ColorFromHex("#FF0000");
        var green = OpenRgbProtocol.ColorFromHex("#00FF00");
        Assert.Equal(Hex.Parse("4F 52 47 42 00 00 00 00 1A 04 00 00 0E 00 00 00 0E 00 00 00 02 00 FF 00 00 00 00 FF 00 00"),
            OpenRgbProtocol.UpdateLedsPacket(0, new[] { red, green }));
    }

    [Fact]
    public void UpdateZoneLeds_example_three_blue()
    {
        var blue = OpenRgbProtocol.ColorFromHex("#0000FF");
        Assert.Equal(Hex.Parse("4F 52 47 42 01 00 00 00 1B 04 00 00 16 00 00 00 16 00 00 00 00 00 00 00 03 00 00 00 FF 00 00 00 FF 00 00 00 FF 00"),
            OpenRgbProtocol.UpdateZoneLedsPacket(1, 0, new[] { blue, blue, blue }));
    }

    [Fact]
    public void UpdateMode_example_static_green_at_protocol_5()
    {
        var mode = new RgbMode { Name = "Static", Value = 1, Flags = 0x50, BrightnessMin = 0, BrightnessMax = 255, ColorsMin = 1, ColorsMax = 1,
            Speed = 0, Brightness = 128, Direction = 0, ColorMode = 2, Colors = { OpenRgbProtocol.ColorFromHex("#00FF00") } };
        var expected = Hex.Parse(@"
            4F 52 47 42 03 00 00 00 4D 04 00 00 47 00 00 00
            47 00 00 00 01 00 00 00 07 00 53 74 61 74 69 63 00
            01 00 00 00 50 00 00 00 00 00 00 00 00 00 00 00
            00 00 00 00 FF 00 00 00 01 00 00 00 01 00 00 00
            00 00 00 00 80 00 00 00 00 00 00 00 02 00 00 00
            01 00 00 FF 00 00");
        Assert.Equal(expected, OpenRgbProtocol.UpdateModePacket(3, 1, mode, 5));
    }

    [Fact]
    public void Colours_convert_both_ways()
    {
        Assert.Equal(0x00332211u, OpenRgbProtocol.ColorFromHex("#112233"));
        Assert.Equal("#112233", OpenRgbProtocol.ColorToHex(0x00332211));
        Assert.Throws<FormatException>(() => OpenRgbProtocol.ColorFromHex("red"));
        Assert.Throws<FormatException>(() => OpenRgbProtocol.ColorFromHex("#12345"));
    }

    [Fact]
    public void Header_validation()
    {
        Assert.True(OpenRgbProtocol.TryParseHeader(Hex.Parse("4F 52 47 42 00 00 00 00 64 00 00 00 00 00 00 00"), out _, out var id, out var size));
        Assert.Equal(100u, id);
        Assert.Equal(0, size);
        Assert.False(OpenRgbProtocol.TryParseHeader(Hex.Parse("4F 52 47 43 00 00 00 00 64 00 00 00 00 00 00 00"), out _, out _, out _));
        Assert.False(OpenRgbProtocol.TryParseHeader(Hex.Parse("4F 52 47 42 00 00 00 00 64 00 00 00 FF FF FF 7F"), out _, out _, out _));
    }

    /// <summary>Builds a controller block the way the server does (section 3.5) so the parser is checked field by field.</summary>
    internal static byte[] ControllerBlock(int version)
    {
        var ms = new MemoryStream();
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); U16((ushort)b.Length); ms.Write(b); }

        U32(0);                       // data_size (patched below)
        U32(5);                       // type: keyboard
        Str("Razer BlackWidow V4 75%"); Str("Razer"); Str("desc"); Str("1.0"); Str("SER123"); Str("HID: path");
        U16(2);                       // modes
        U32(0);                       // active mode
        // Direct: per-LED colour
        Str("Direct"); U32(0); U32(0x20); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(1); U16(0);
        // Static: speed + brightness + mode-specific colour, one colour
        Str("Static"); U32(7); U32(0x51); U32(1); U32(9); U32(0); U32(255); U32(1); U32(1); U32(4); U32(200); U32(0); U32(2); U16(1); U32(0x00FF8040);
        U16(2);                       // zones
        Str("Keys"); U32(2); U32(3); U32(3); U32(3);
        U16(8 + 4 * 3); U32(1); U32(3); U32(0); U32(1); U32(2); // matrix 1x3
        if (version >= 4) { U16(1); Str("seg"); U32(1); U32(0); U32(3); }
        if (version >= 5) U32(0);
        Str("Logo"); U32(0); U32(1); U32(1); U32(1); U16(0);
        if (version >= 4) U16(0);
        if (version >= 5) U32(1);     // effects-only zone
        U16(4);                       // leds
        foreach (var n in new[] { "A", "B", "C", "Logo" }) { Str(n); U32(0); }
        U16(4);                       // colours
        U32(0x000000FF); U32(0x000000FF); U32(0x0000FF00); U32(0x000000FF);
        if (version >= 5) { U16(0); U32(0); }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Controller_block_is_parsed_for_every_supported_version(int version)
    {
        var c = OpenRgbProtocol.ParseController(ControllerBlock(version), version, 2);
        Assert.Equal(2, c.Index);
        Assert.Equal(5, c.Type);
        Assert.Equal("Razer BlackWidow V4 75%", c.Name);
        Assert.Equal("Razer", c.Vendor);
        Assert.Equal("SER123", c.Serial);
        Assert.Equal(2, c.Modes.Count);
        Assert.Equal("Static", c.Modes[1].Name);
        Assert.Equal(7, c.Modes[1].Value);
        Assert.Equal(0x51u, c.Modes[1].Flags);
        Assert.Equal(9u, c.Modes[1].SpeedMax);
        Assert.Equal(255u, c.Modes[1].BrightnessMax);
        Assert.Equal(200u, c.Modes[1].Brightness);
        Assert.Equal(0x00FF8040u, c.Modes[1].Colors.Single());
        Assert.Equal(2, c.Zones.Count);
        Assert.Equal(3u, c.Zones[0].LedsCount);
        Assert.Equal("Logo", c.Zones[1].Name);
        Assert.Equal(4, c.LedCount);
        Assert.Equal(new uint[] { 0xFF, 0xFF, 0xFF00, 0xFF }, c.Colors);
        Assert.Equal(version >= 5 ? 1 : 1, c.Zones[1].ColorSlots);
        Assert.Equal(3, c.Zones[0].MatrixWidth);
        Assert.Equal(1, c.Zones[0].MatrixHeight);
        Assert.Equal(new uint[] { 0, 1, 2 }, c.Zones[0].Matrix);
        Assert.Null(c.Zones[1].Matrix);
    }

    [Fact]
    public void A_truncated_block_is_rejected_not_misread()
    {
        var block = ControllerBlock(5);
        Assert.Throws<InvalidDataException>(() => OpenRgbProtocol.ParseController(block.AsSpan(0, block.Length / 2), 5, 0));
    }
}

// ---------------------------------------------------------------- module behaviour with a fake OpenRGB

internal sealed class FakeRgb : IOpenRgb
{
    public string? Executable { get; set; }
    public bool Listening { get; set; }
    public bool StartWorks { get; set; } = true;
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public bool ListChanged { get; set; }
    public List<RgbController> Devices { get; } = new();
    public List<string> Sent { get; } = new();
    /// <summary>Set to make every colour and effect write fail, as when OpenRGB stops answering.</summary>
    public Exception? FailWrites { get; set; }

    public string? FindExecutable() => Executable;
    public bool IsListening() => Listening;

    public bool StartServer(string executable)
    {
        if (!StartWorks) return false;
        Started = Listening = true;
        return true;
    }

    public bool OwnsServer => Started;
    /// <summary>False = as on Linux and macOS: OpenRGB is never looked for or started, only connected to.</summary>
    public bool CanStartServer { get; set; } = true;
    public bool Connected { get; private set; }

    public void Connect()
    {
        if (!Listening) throw new IOException("OpenRGB is not answering.");
        Connected = true;
    }

    public void Disconnect() => Connected = false;

    public IReadOnlyList<RgbController> ReadControllers() => Devices.ToList();

    public void UpdateMode(int device, int modeIndex, RgbMode mode)
    {
        if (FailWrites is not null) throw FailWrites;
        Sent.Add($"mode {device} {modeIndex} cm={mode.ColorMode} speed={mode.Speed} bright={mode.Brightness} dir={mode.Direction} colors=[{string.Join(",", mode.Colors.Select(OpenRgbProtocol.ColorToHex))}]");
        var c = Devices[device];
        c.ActiveMode = modeIndex;
        c.Modes[modeIndex] = mode.Clone();
    }

    public void UpdateLeds(int device, IReadOnlyList<uint> colors)
    {
        if (FailWrites is not null) throw FailWrites;
        Sent.Add($"leds {device} n={colors.Count} {OpenRgbProtocol.ColorToHex(colors[0])}");
        Devices[device].Colors = colors.ToList();
    }

    public void UpdateZoneLeds(int device, int zone, IReadOnlyList<uint> colors)
        => Sent.Add($"zone {device} {zone} n={colors.Count} {OpenRgbProtocol.ColorToHex(colors[0])}");

    public void ResizeZone(int device, int zone, int leds)
    {
        Sent.Add($"resize {device} {zone} {leds}");
        // As OpenRGB does: the zone gets that many lights and the device's colour list is rebuilt.
        var c = Devices[device];
        c.Zones[zone].LedsCount = (uint)leds;
        c.Colors = Enumerable.Repeat(0u, c.Zones.Sum(z => (int)z.LedsCount)).ToList();
        c.LedCount = c.Colors.Count;
    }

    public bool DeviceListChanged()
    {
        var c = ListChanged;
        ListChanged = false;
        return c;
    }

    public void Dispose() => Disposed = true;

    public static RgbController Keyboard(int index) => new()
    {
        Index = index, Type = 5, Name = "Razer BlackWidow V4 75%", Vendor = "Razer", Serial = "K1", Location = "HID: kb",
        ActiveMode = 0,
        Modes =
        {
            new RgbMode { Name = "Direct", Flags = RgbMode.PerLedColor, ColorMode = 1 },
            new RgbMode { Name = "Breathing", Flags = RgbMode.HasSpeed | RgbMode.HasBrightness | RgbMode.ModeSpecificColor, SpeedMin = 1, SpeedMax = 9, Speed = 5,
                BrightnessMin = 0, BrightnessMax = 255, Brightness = 255, ColorsMin = 1, ColorsMax = 2, ColorMode = 2, Colors = { 0xFF } },
        },
        Zones = { new RgbZone { Name = "Keys", LedsCount = 3 }, new RgbZone { Name = "Logo", LedsCount = 5, Flags = 1 } },
        LedCount = 4,
        Colors = { 0xFF, 0xFF, 0xFF, 0xFF },
    };

    public static RgbController Board(int index) => new()
    {
        Index = index, Type = 0, Name = "ASUS ROG STRIX Z790-A", Vendor = "ASUS", Serial = "", Location = "HID: aura",
        ActiveMode = 0,
        Modes = { new RgbMode { Name = "Static", Flags = RgbMode.ModeSpecificColor, ColorsMin = 1, ColorsMax = 1, ColorMode = 2, Colors = { 0xFF00 } } },
        Zones = { new RgbZone { Name = "Aura Mainboard", LedsCount = 4 } },
        LedCount = 4,
        Colors = { 0xFF00, 0xFF00, 0xFF00, 0xFF00 },
    };

    public static RgbController EffectsOnly(int index) => new()
    {
        Index = index, Type = 3, Name = "Fan hub", Vendor = "X", Location = "HID: hub",
        Modes = { new RgbMode { Name = "Rainbow", Flags = RgbMode.HasSpeed, SpeedMin = 0, SpeedMax = 4, Speed = 2 } },
    };
}

public class LightingModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-lighting-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRgb _rgb = new();
    private readonly HashSet<string> _running = new();

    public LightingModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private async Task<LightingModule> Start()
    {
        var m = new LightingModule(_rgb, name => _running.Contains(name), background: false);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    private static async Task<JsonElement> Call(LightingModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task<string> FailCode(LightingModule m, string method, object? args = null)
        => (await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args))).Code;

    private void Installed()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(FakeRgb.Keyboard(0));
        _rgb.Devices.Add(FakeRgb.Board(1));
        _rgb.Devices.Add(FakeRgb.EffectsOnly(2));
    }

    [Fact]
    public async Task Not_installed_is_degraded_and_nothing_is_started()
    {
        await using var m = await Start();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        var s = await Call(m, "getStatus");
        Assert.False(s.GetProperty("openRgb").GetProperty("installed").GetBoolean());
        Assert.False(s.GetProperty("openRgb").GetProperty("connected").GetBoolean());
        Assert.Equal(0, s.GetProperty("devices").GetArrayLength());
        Assert.False(_rgb.Started);
        Assert.Equal("unavailable", await FailCode(m, "setColor", new { color = "#FF0000" }));
        Assert.Equal("unavailable", await FailCode(m, "startOpenRgb"));
    }

    [Fact]
    public async Task Installed_openrgb_is_started_hidden_at_startup_and_devices_listed()
    {
        Installed();
        _running.Add("NZXT CAM");
        _running.Add("GCC");
        await using var m = await Start();
        Assert.True(_rgb.Started);
        Assert.Equal(ModuleState.Ready, m.Status.State);

        var s = await Call(m, "getStatus");
        Assert.True(s.GetProperty("openRgb").GetProperty("connected").GetBoolean());
        Assert.Equal(6742, s.GetProperty("openRgb").GetProperty("port").GetInt32());
        Assert.Equal(3, s.GetProperty("devices").GetArrayLength());
        var kb = s.GetProperty("devices")[0];
        Assert.Equal("Keyboard", kb.GetProperty("type").GetString());
        Assert.Equal(4, kb.GetProperty("ledCount").GetInt32());
        Assert.Equal("#FF0000", kb.GetProperty("color").GetString());
        Assert.Equal(2, kb.GetProperty("zones").GetArrayLength());
        var breathing = kb.GetProperty("modes")[1];
        Assert.True(breathing.GetProperty("supportsSpeed").GetBoolean());
        Assert.Equal(9, breathing.GetProperty("speedMax").GetInt32());
        Assert.Equal(1, breathing.GetProperty("colorCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, kb.GetProperty("modes")[0].GetProperty("speedMin").ValueKind);
        Assert.Equal(new[] { "NZXT CAM", "GIGABYTE Control Center" }, s.GetProperty("conflicts").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task An_openrgb_already_running_is_used_not_restarted_or_stopped_by_us()
    {
        Installed();
        _rgb.Listening = true;
        var m = await Start();
        Assert.False(_rgb.Started);
        Assert.True((await Call(m, "getStatus")).GetProperty("openRgb").GetProperty("connected").GetBoolean());
        await m.DisposeAsync();
    }

    [Fact]
    public async Task One_colour_for_everything_uses_the_right_mode_per_device()
    {
        Installed();
        await using var m = await Start();
        var s = await Call(m, "setColor", new { color = "#00E5FF" });
        Assert.Contains("mode 0 0 cm=1 speed=0 bright=0 dir=0 colors=[]", _rgb.Sent);         // keyboard: Direct, per-LED
        Assert.Contains("leds 0 n=4 #00E5FF", _rgb.Sent);
        Assert.Contains("mode 1 0 cm=2 speed=0 bright=0 dir=0 colors=[#00E5FF]", _rgb.Sent);  // board: Static, one mode colour
        Assert.DoesNotContain(_rgb.Sent, x => x.StartsWith("mode 2") || x.StartsWith("leds 2")); // effects-only device is skipped
        Assert.Equal("#00E5FF", s.GetProperty("devices")[0].GetProperty("color").GetString());
        Assert.Equal("#00E5FF", s.GetProperty("devices")[1].GetProperty("color").GetString());
    }

    [Fact]
    public async Task Asking_one_device_that_cannot_take_a_colour_says_so()
    {
        Installed();
        await using var m = await Start();
        Assert.Equal("unavailable", await FailCode(m, "setColor", new { deviceIds = new[] { 2 }, color = "#FF0000" }));
        Assert.Equal("invalidArgs", await FailCode(m, "setColor", new { deviceIds = new[] { 9 }, color = "#FF0000" }));
        Assert.Equal("invalidArgs", await FailCode(m, "setColor", new { color = "blue" }));
        Assert.Empty(_rgb.Sent);
    }

    [Fact]
    public async Task Zone_colour_respects_effects_only_zones()
    {
        Installed();
        await using var m = await Start();
        await Call(m, "setZoneColor", new { deviceId = 0, zoneId = 0, color = "#FF0000" });
        Assert.Contains("zone 0 0 n=3 #FF0000", _rgb.Sent);
        await Call(m, "setZoneColor", new { deviceId = 0, zoneId = 1, color = "#FF0000" });
        Assert.Contains("zone 0 1 n=1 #FF0000", _rgb.Sent); // never more than the one slot it owns
        Assert.Equal("invalidArgs", await FailCode(m, "setZoneColor", new { deviceId = 0, zoneId = 7, color = "#FF0000" }));
        Assert.Equal("unavailable", await FailCode(m, "setZoneColor", new { deviceId = 1, zoneId = 0, color = "#FF0000" }));
    }

    [Fact]
    public async Task Effects_stay_inside_the_devices_own_limits()
    {
        Installed();
        await using var m = await Start();
        Assert.Equal("invalidArgs", await FailCode(m, "setMode", new { deviceId = 0, modeId = 1, speed = 10 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setMode", new { deviceId = 0, modeId = 1, brightness = 256 }));
        Assert.Equal("invalidArgs", await FailCode(m, "setMode", new { deviceId = 0, modeId = 5 }));
        Assert.Empty(_rgb.Sent);

        var s = await Call(m, "setMode", new { deviceId = 0, modeId = 1, colors = new[] { "#FF0000", "#00FF00", "#0000FF" }, speed = 3, brightness = 128 });
        Assert.Equal("mode 0 1 cm=2 speed=3 bright=128 dir=0 colors=[#FF0000,#00FF00]", _rgb.Sent.Single()); // third colour dropped: the effect takes 2
        Assert.Equal(1, s.GetProperty("devices")[0].GetProperty("activeModeId").GetInt32());

        _rgb.Sent.Clear();
        await Call(m, "setMode", new { deviceId = 2, modeId = 0, colors = new[] { "#FF0000" }, speed = 4 });
        Assert.Equal("mode 2 0 cm=0 speed=4 bright=0 dir=0 colors=[]", _rgb.Sent.Single()); // an effect with no colour gets none
    }

    [Fact]
    public async Task Scenes_save_apply_and_delete()
    {
        Installed();
        var m = await Start();
        await Call(m, "setColor", new { color = "#112233" });
        var list = await Call(m, "saveScene", new { name = "Gaming" });
        Assert.Equal("Gaming", list[0].GetProperty("name").GetString());
        Assert.Equal("invalidArgs", await FailCode(m, "saveScene", new { name = "  " }));

        await Call(m, "setColor", new { color = "#FFFFFF" });
        _rgb.Sent.Clear();
        var s = await Call(m, "applyScene", new { name = "gaming" });
        Assert.Contains("leds 0 n=4 #112233", _rgb.Sent);
        Assert.Contains("mode 1 0 cm=2 speed=0 bright=0 dir=0 colors=[#112233]", _rgb.Sent);
        Assert.Equal("#112233", s.GetProperty("devices")[0].GetProperty("color").GetString());
        await m.DisposeAsync();

        // Scenes survive a restart.
        await using var again = await Start();
        Assert.Equal(1, (await Call(again, "listScenes")).GetArrayLength());
        Assert.Equal(0, (await Call(again, "deleteScene", new { name = "Gaming" })).GetArrayLength());
        Assert.Equal("invalidArgs", await FailCode(again, "applyScene", new { name = "Gaming" }));
    }

    [Fact]
    public async Task Devices_found_later_show_up_through_poll()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        await using var m = await Start();
        Assert.Equal(0, (await Call(m, "getStatus")).GetProperty("devices").GetArrayLength());
        _rgb.Devices.Add(FakeRgb.Keyboard(0));
        _rgb.ListChanged = true;
        m.Poll();
        Assert.Equal(1, (await Call(m, "getStatus")).GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public async Task Install_never_runs_an_installer_when_already_installed_and_exit_is_idempotent()
    {
        Installed();
        var m = await Start();
        var r = await Call(m, "installOpenRgb");
        Assert.False(r.GetProperty("started").GetBoolean());
        Assert.Equal("unknownMethod", await FailCode(m, "nope"));
        await m.DisposeAsync();
        await m.DisposeAsync();
        Assert.True(_rgb.Disposed);
        Assert.Equal(ModuleState.Stopped, m.Status.State);
    }

    [Fact]
    public async Task A_failed_start_is_reported_not_thrown_at_startup()
    {
        Installed();
        _rgb.StartWorks = false;
        await using var m = await Start();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        var s = await Call(m, "getStatus");
        Assert.False(s.GetProperty("openRgb").GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrEmpty(s.GetProperty("openRgb").GetProperty("message").GetString()));
    }
}

public class LightSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-sync-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRgb _rgb = new();

    public LightSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Colourful_pixels_count_more_than_grey_ones()
    {
        // 3 grey pixels and 1 pure red one: a plain average is pinkish grey, the weighted one is clearly red.
        var px = new byte[] { 128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, 255, 0, 0, 255, 255 };
        var c = ColorMath.Average(px, 4, 1);
        Assert.True(((c >> 16) & 255) > 220, ColorMath.Hex(c));
        Assert.True(((c >> 8) & 255) < 40, ColorMath.Hex(c));
        Assert.Equal(0, ColorMath.Average(null, 4, 1));
    }

    [Fact]
    public void Smoothing_moves_35_percent_and_brightness_scales()
    {
        var c = ColorMath.Smooth((0, 0, 0), 0xFF0000, 100);
        Assert.Equal(255 * 0.35, c.R, 3);
        c = ColorMath.Smooth((0, 0, 0), 0xFF0000, 50);
        Assert.Equal(127.5 * 0.35, c.R, 3);
        Assert.Equal(0x0000FFu, ColorMath.ToOpenRgb((255, 0, 0))); // OpenRGB packs 0x00BBGGRR
    }

    private async Task<LightingModule> Start()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(FakeRgb.Keyboard(0));
        _rgb.Devices.Add(FakeRgb.Board(1));
        var m = new LightingModule(_rgb, _ => false, background: false) { SyncIntervalMs = 10 };
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    private static async Task<JsonElement> Call(LightingModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task Until(Func<bool> ok)
    {
        for (var i = 0; i < 200 && !ok(); i++) await Task.Delay(20);
        Assert.True(ok());
    }

    [Fact]
    public async Task Turning_the_sync_off_puts_the_lights_back()
    {
        await using var m = await Start();
        var s = await Call(m, "setSync", new { enabled = true });
        Assert.True(s.GetProperty("enabled").GetBoolean());
        await Until(() => { lock (_rgb.Sent) return _rgb.Sent.Any(x => x.StartsWith("leds 0", StringComparison.Ordinal)); });

        s = await Call(m, "setSync", new { enabled = false });
        Assert.False(s.GetProperty("enabled").GetBoolean());
        // The keyboard is back on its own colours and the board on its Static green.
        Assert.All(_rgb.Devices[0].Colors, c => Assert.Equal(0xFFu, c));
        Assert.Equal(0xFF00u, _rgb.Devices[1].Modes[0].Colors[0]);
    }

    [Fact]
    public async Task Changing_a_light_by_hand_stops_the_sync_and_keeps_the_hand_change()
    {
        await using var m = await Start();
        await Call(m, "setSync", new { enabled = true });
        await Task.Delay(100);
        await Call(m, "setColor", new { color = "#FF0000" });
        var s = await Call(m, "getSync");
        Assert.False(s.GetProperty("enabled").GetBoolean());
        await Task.Delay(100);
        Assert.Equal("#FF0000", OpenRgbProtocol.ColorToHex(_rgb.Devices[0].Colors[0]));
    }

    [Fact]
    public void Vivid_keeps_the_hue_but_brightens_and_saturates()
    {
        var v = ColorMath.Vivid(0x723536); // the dull pink-brown of a dim room
        Assert.Equal(255, (v >> 16) & 255);
        Assert.True(((v >> 8) & 255) < 100 && (v & 255) < 100, ColorMath.Hex(v));
        Assert.Equal(0, ColorMath.Vivid(0));
        Assert.True(((ColorMath.Vivid(0x0A0000) >> 16) & 255) < 80); // a dark picture stays dim
        Assert.Equal(0xFFFFFF, ColorMath.Vivid(0x808080)); // grey becomes white, not a colour
    }

    [Fact]
    public void Small_colour_changes_are_not_a_difference()
    {
        Assert.False(ColorMath.Differs(new uint[] { 0x00102030 }, new uint[] { 0x00162636 }, 12));
        Assert.True(ColorMath.Differs(new uint[] { 0x00102030 }, new uint[] { 0x00102040 }, 12));
        Assert.True(ColorMath.Differs(new uint[] { 0 }, new uint[] { 0, 0 }, 12));
    }

    [Fact]
    public async Task A_graphics_card_gets_colours_at_most_once_a_second()
    {
        var gpu = FakeRgb.Board(2);
        gpu.Type = 2;
        gpu.Name = "Graphics card";
        gpu.Modes.Insert(0, new RgbMode { Name = "Direct", Flags = RgbMode.PerLedColor, ColorMode = 1 });
        _rgb.Devices.Add(gpu);
        await using var m = await Start();
        await Call(m, "setSync", new { enabled = true });
        await Task.Delay(600);
        int toGpu, toKeyboard;
        lock (_rgb.Sent)
        {
            toGpu = _rgb.Sent.Count(x => x.StartsWith("leds 2", StringComparison.Ordinal));
            toKeyboard = _rgb.Sent.Count(x => x.StartsWith("leds 0", StringComparison.Ordinal));
        }
        Assert.True(toGpu <= 1, $"graphics card got {toGpu} updates");
        Assert.True(toKeyboard > 3, $"keyboard got {toKeyboard} updates");
        await Call(m, "setSync", new { enabled = false });
    }

    [Fact]
    public async Task An_unknown_capture_device_or_brightness_is_refused()
    {
        await using var m = await Start();
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setSync", new { source = "nope" }));
#if WINDOWS
        Assert.Equal("invalidArgs", e.Code);
#else
        // No capture cards in the flavour for Linux and macOS: any capture device is refused as not available.
        Assert.Equal("unavailable", e.Code);
        Assert.StartsWith(ModuleAvailability.NotOnThisSystem, e.Message);
#endif
        e = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setSync", new { brightness = 0 }));
        Assert.Equal("invalidArgs", e.Code);
    }
}

public class CaptureFrameTests
{
    [Fact]
    public void Studio_range_HD_red_becomes_red()
    {
        var (r, g, b) = ColorMath.Yuv(63, 102, 240);
        Assert.True(r > 245 && g < 10 && b < 10, $"{r},{g},{b}");
        Assert.Equal((0, 0, 0), ColorMath.Yuv(16, 128, 128));
    }

    [Fact]
    public void An_NV12_frame_is_read_in_its_own_layout()
    {
        // 64x32 red picture: Y plane then the half-size interleaved U/V plane, with a wider stride than the picture.
        const int w = 64, h = 32, stride = 80;
        var data = new byte[stride * h + stride * h / 2];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) data[y * stride + x] = 63;
        for (var y = 0; y < h / 2; y++) for (var x = 0; x < w / 2; x++) { data[stride * h + y * stride + 2 * x] = 102; data[stride * h + y * stride + 2 * x + 1] = 240; }
        var c = ColorMath.AverageFrame(data, "NV12", w, h, 0, stride, stride * h, stride);
        Assert.True(((c >> 16) & 255) > 240 && ((c >> 8) & 255) < 15, ColorMath.Hex(c));
    }

    [Fact]
    public void A_YUY2_frame_is_read_in_its_own_layout_and_is_not_green()
    {
        // 64x32 mid grey: Y0 U Y1 V per two pixels. Read as if it were BGRA this came out green-ish.
        const int w = 64, h = 32, stride = w * 2;
        var data = new byte[stride * h];
        for (var i = 0; i < data.Length; i += 4) { data[i] = 126; data[i + 1] = 128; data[i + 2] = 126; data[i + 3] = 128; }
        var c = ColorMath.AverageFrame(data, "YUY2", w, h, 0, stride);
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        Assert.InRange(Math.Abs(r - g) + Math.Abs(g - b), 0, 3);
        Assert.Equal(-1, ColorMath.AverageFrame(data, "MJPG", w, h, 0, stride));
    }
}

internal sealed class FakeGcc : IGigabyteControlCenter
{
    public bool Running = true, Starts = true, ByUs;
    public GccState GetState() => new(true, Running, Starts, ByUs);
    public bool Close() { Running = false; return true; }
    public void Start() { if (Starts) Running = true; }
    public void SetStartsWithWindows(bool enabled)
    {
        if (!enabled && Starts) { Starts = false; ByUs = true; }
        else if (enabled && ByUs) { Starts = true; ByUs = false; }
    }
}

public class GccTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-gcc-tests-" + Guid.NewGuid().ToString("N"));
    public GccTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private async Task<LightingModule> Start(FakeGcc gcc, bool elevated, FakeRgb? rgb = null)
    {
        var m = new LightingModule(rgb ?? new FakeRgb(), _ => false, background: false, gcc);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    [Fact]
    public async Task Take_over_needs_admin_closes_GCC_and_only_gives_back_what_it_took()
    {
        var gcc = new FakeGcc();
        await using (var m = await Start(gcc, elevated: false))
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("takeOverGcc", null, CancellationToken.None));
            Assert.Equal("needsAdmin", e.Code);
            Assert.True(gcc.Running);
        }
        await using (var m = await Start(gcc, elevated: true))
        {
            await m.InvokeAsync("takeOverGcc", null, CancellationToken.None);
            Assert.False(gcc.Running);
            Assert.False(gcc.Starts);
            await m.InvokeAsync("giveBackGcc", null, CancellationToken.None);
            Assert.True(gcc.Starts);
            Assert.True(gcc.Running); // started again through its own task
        }
    }

    [Fact]
    public async Task Giving_GCC_back_leaves_the_Gigabyte_card_alone_until_taken_over_again()
    {
        var rgb = new FakeRgb { Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe" };
        rgb.Devices.Add(FakeRgb.Keyboard(0));
        var gpu = FakeRgb.Board(1);
        gpu.Type = 2;
        gpu.Name = "Gigabyte GeForce RTX 4070 Ti Eagle OC";
        rgb.Devices.Add(gpu);
        var gcc = new FakeGcc { Running = false, Starts = false, ByUs = true };
        await using var m = await Start(gcc, elevated: true, rgb);
        await m.InvokeAsync("giveBackGcc", null, CancellationToken.None);

        lock (rgb.Sent) rgb.Sent.Clear();
        await m.InvokeAsync("setColor", JsonSerializer.SerializeToElement(new { color = "#FF0000" }, JsonDefaults.Options), CancellationToken.None);
        lock (rgb.Sent)
        {
            Assert.Contains(rgb.Sent, x => x.Contains(" 0 ", StringComparison.Ordinal) || x.StartsWith("leds 0", StringComparison.Ordinal) || x.StartsWith("mode 0", StringComparison.Ordinal));
            Assert.DoesNotContain(rgb.Sent, x => x.StartsWith("leds 1", StringComparison.Ordinal) || x.StartsWith("mode 1", StringComparison.Ordinal));
        }
        var e = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("setColor",
            JsonSerializer.SerializeToElement(new { deviceIds = new[] { 1 }, color = "#FF0000" }, JsonDefaults.Options), CancellationToken.None));
        Assert.Equal("unavailable", e.Code);
        var status = JsonSerializer.SerializeToElement(await m.InvokeAsync("getStatus", null, CancellationToken.None), JsonDefaults.Options);
        Assert.True(status.GetProperty("devices")[1].GetProperty("leftOut").GetBoolean());

        await m.InvokeAsync("takeOverGcc", null, CancellationToken.None);
        status = JsonSerializer.SerializeToElement(await m.InvokeAsync("getStatus", null, CancellationToken.None), JsonDefaults.Options);
        Assert.False(status.GetProperty("devices")[1].GetProperty("leftOut").GetBoolean());
    }
}

public class LedLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-layout-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRgb _rgb = new();

    public LedLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A 48x27 picture: the left half red, the right half blue.</summary>
    private static byte[] HalfAndHalf()
    {
        var g = new byte[ColorMath.GridW * ColorMath.GridH * 4];
        for (var y = 0; y < ColorMath.GridH; y++)
            for (var x = 0; x < ColorMath.GridW; x++)
            {
                var i = 4 * (y * ColorMath.GridW + x);
                if (x < ColorMath.GridW / 2) g[i + 2] = 255; else g[i] = 255;
                g[i + 3] = 255;
            }
        return g;
    }

    [Fact]
    public void A_strip_spreads_along_its_longer_side_and_can_run_backwards()
    {
        var zone = new RgbZone { LedsCount = 4 };
        var across = LedLayout.LedRects(new LayoutItem { X = 0, Y = 0.5, W = 0.8, H = 0.1 }, zone).ToList();
        Assert.Equal(new[] { 0.0, 0.2, 0.4, 0.6 }, across.Select(r => Math.Round(r.X, 6)));
        var back = LedLayout.LedRects(new LayoutItem { X = 0, Y = 0, W = 0.8, H = 0.1, Reverse = true }, zone).ToList();
        Assert.Equal(0.6, back[0].X, 6);
        var down = LedLayout.LedRects(new LayoutItem { X = 0.9, Y = 0, W = 0.05, H = 1 }, zone).ToList();
        Assert.Equal(new[] { 0.0, 0.25, 0.5, 0.75 }, down.Select(r => Math.Round(r.Y, 6)));
    }

    [Fact]
    public void A_grid_zone_uses_its_grid_and_skips_empty_cells()
    {
        var zone = new RgbZone { LedsCount = 3, MatrixWidth = 2, MatrixHeight = 2, Matrix = new uint[] { 0, 0xFFFFFFFF, 1, 2 } };
        var rects = LedLayout.LedRects(new LayoutItem { X = 0, Y = 0, W = 1, H = 1 }, zone).ToList();
        Assert.Equal(3, rects.Count);
        Assert.Contains(rects, r => r.Led == 2 && r.X == 0.5 && r.Y == 0.5);
    }

    [Fact]
    public void Each_placed_led_takes_the_colour_under_it_and_the_rest_the_average()
    {
        var dev = new RgbController
        {
            Name = "Strip", Serial = "S", Location = "L",
            Zones = { new RgbZone { Name = "Front", LedsCount = 2 }, new RgbZone { Name = "Back", LedsCount = 1 } },
            Colors = { 0, 0, 0 },
        };
        var items = new[] { new LayoutItem { Key = dev.Key, Zone = 0, X = 0, Y = 0, W = 1, H = 0.2 } };
        var t = LedLayout.Targets(dev, items, HalfAndHalf(), ColorMath.GridW, ColorMath.GridH, 0x123456)!;
        Assert.Equal(0xFF0000, t[0]);
        Assert.Equal(0x0000FF, t[1]);
        Assert.Equal(0x123456, t[2]);
        Assert.Null(LedLayout.Targets(dev, Array.Empty<LayoutItem>(), HalfAndHalf(), ColorMath.GridW, ColorMath.GridH, 0));
        Assert.Equal(0x0000FF, LedLayout.SingleColor(dev, new[] { new LayoutItem { Key = dev.Key, Zone = 1, X = 0.9, Y = 0, W = 0.05, H = 0.05 } }, HalfAndHalf(), ColorMath.GridW, ColorMath.GridH));
    }

    [Fact]
    public void A_whole_device_box_spreads_every_led_and_a_zone_box_wins_over_it()
    {
        var dev = new RgbController
        {
            Name = "GPU", Serial = "S", Location = "L",
            Zones = { new RgbZone { LedsCount = 1 }, new RgbZone { LedsCount = 1 }, new RgbZone { LedsCount = 0 }, new RgbZone { LedsCount = 2 } },
            Colors = { 0, 0, 0, 0 },
        };
        var whole = new LayoutItem { Key = dev.Key, Zone = LedLayout.WholeDevice, X = 0, Y = 0, W = 1, H = 0.2 };
        var t = LedLayout.Targets(dev, new[] { whole }, HalfAndHalf(), ColorMath.GridW, ColorMath.GridH, 0x123456)!;
        Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF }, t); // 4 LEDs across: two on red, two on blue
        var zoneOnRed = new LayoutItem { Key = dev.Key, Zone = 3, X = 0, Y = 0, W = 0.1, H = 0.1 };
        t = LedLayout.Targets(dev, new[] { zoneOnRed, whole }, HalfAndHalf(), ColorMath.GridW, ColorMath.GridH, 0)!;
        Assert.Equal(0xFF0000, t[3]);

        // A device with one lit zone keeps that zone's grid.
        var kb = new RgbController { Name = "KB", Zones = { new RgbZone { LedsCount = 2, MatrixWidth = 2, MatrixHeight = 1, Matrix = new uint[] { 1, 0 } } }, Colors = { 0, 0 } };
        var (zone, start) = LedLayout.WholeZone(kb);
        Assert.NotNull(zone.Matrix);
        Assert.Equal(0, start);
        Assert.Equal(-1, Assert.Single(LedLayout.Clean(new[] { new LayoutItem { Key = "k", Zone = -1, W = 1, H = 1 } })).Zone);
        Assert.Throws<ArgumentException>(() => LedLayout.Clean(new[] { new LayoutItem { Key = "k", Zone = -2, W = 1, H = 1 } }));
    }

    [Fact]
    public void Placements_are_kept_inside_the_picture_and_bad_ones_refused()
    {
        var clean = LedLayout.Clean(new[]
        {
            new LayoutItem { Key = "k", Zone = 0, X = 0.95, Y = -1, W = 0.2, H = 0 },
            new LayoutItem { Key = "k", Zone = 0, X = 0.9, Y = 0.5, W = 0.1, H = 0.1 }, // the same zone again: the last one wins
        });
        var one = Assert.Single(clean);
        Assert.Equal(0.9, one.X, 6);
        clean = LedLayout.Clean(new[] { new LayoutItem { Key = "k", Zone = 0, X = 0.95, Y = -1, W = 0.2, H = 0 } });
        Assert.Equal(0.8, clean[0].X, 6);
        Assert.Equal(0, clean[0].Y);
        Assert.Equal(0.01, clean[0].H, 6);
        Assert.Throws<ArgumentException>(() => LedLayout.Clean(new[] { new LayoutItem { Key = "k", X = double.NaN, W = 1, H = 1 } }));
        Assert.Throws<ArgumentException>(() => LedLayout.Clean(new[] { new LayoutItem { Key = "", W = 1, H = 1 } }));
    }

    [Fact]
    public async Task The_layout_is_saved_and_listed_with_every_zone()
    {
        _rgb.Executable = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        _rgb.Devices.Add(FakeRgb.Keyboard(0));
        _rgb.Devices.Add(FakeRgb.Board(1));
        await using var m = new LightingModule(_rgb, _ => false, background: false);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        var key = _rgb.Devices[0].Key;
        var r = JsonSerializer.SerializeToElement(await m.InvokeAsync("setLayout",
            JsonSerializer.SerializeToElement(new { perLed = true, items = new[] { new { key, zone = 0, x = 0.1, y = 0.1, w = 0.5, h = 0.1, reverse = false } } }, JsonDefaults.Options),
            CancellationToken.None), JsonDefaults.Options);
        Assert.True(r.GetProperty("perLed").GetBoolean());
        var zones = r.GetProperty("zones").EnumerateArray().ToList();
        Assert.Equal(5, zones.Count); // keyboard whole + keys + logo, board whole + its zone
        Assert.Equal(0.5, zones.First(z => z.GetProperty("zone").GetInt32() == 0 && z.GetProperty("key").GetString() == key).GetProperty("placed").GetProperty("w").GetDouble(), 6);
        var e = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("setLayout",
            JsonSerializer.SerializeToElement(new { items = new[] { new { key = "", zone = 0, x = 0, y = 0, w = 1, h = 1 } } }, JsonDefaults.Options), CancellationToken.None));
        Assert.Equal("invalidArgs", e.Code);
        Assert.Null(await m.InvokeAsync("getSyncPreview", null, CancellationToken.None)); // nothing to show while the sync is off
    }
}

#if WINDOWS // OBS stands in for a capture card, which only the Windows flavour has.
public class ObsSourceTests
{
    [Fact]
    public void Auth_response_matches_the_obs_websocket_example()
    {
        // obs-websocket protocol docs: password "supersecretpassword", salt and challenge as published there.
        var r = ObsSource.AuthResponse("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", r);
    }

    [Fact]
    public void A_bottom_up_bmp_is_read_top_down()
    {
        // 2x2, 24-bit, bottom-up: bottom row blue, top row red.
        var bmp = new byte[54 + 2 * 8];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(2).CopyTo(bmp, 18);
        BitConverter.GetBytes(2).CopyTo(bmp, 22);
        BitConverter.GetBytes((ushort)24).CopyTo(bmp, 28);
        bmp[54] = 255; bmp[57] = 255;           // row 0 in the file (bottom): blue, blue
        bmp[62 + 2] = 255; bmp[62 + 5] = 255;   // row 1 in the file (top): red, red
        var g = ObsSource.ParseBmp(bmp, 2, 2)!;
        Assert.Equal(255, g[2]);  // top-left is red
        Assert.Equal(255, g[8]);  // bottom-left is blue
        Assert.Null(ObsSource.ParseBmp(bmp, 3, 2));
    }
}
#endif

public class BassFollowerTests
{
#if WINDOWS // The band filters listen to the PC's sound, which only the Windows flavour does.
    private static float[] Sine(double hz, int rate, double seconds, float amp)
    {
        var n = (int)(rate * seconds);
        var a = new float[n];
        for (var i = 0; i < n; i++) a[i] = amp * (float)Math.Sin(2 * Math.PI * hz * i / rate);
        return a;
    }

    [Fact]
    public void Bass_reads_high_and_treble_reads_low()
    {
        var bassy = BandFollower.Bass(48000);
        bassy.Add(Sine(60, 48000, 1, 0.5f));
        Assert.True(bassy.Level > 0.6f, $"60 Hz level {bassy.Level}");

        var treble = BandFollower.Bass(48000);
        treble.Add(Sine(60, 48000, 1, 0.5f));   // the same loud bass sets "full" ...
        treble.Add(Sine(3000, 48000, 0.5, 0.5f)); // ... then only treble plays
        Assert.True(treble.Level < 0.1f, $"3 kHz level {treble.Level}");
    }

    [Fact]
    public void Silence_is_zero_and_the_level_works_at_any_volume()
    {
        var quiet = BandFollower.Bass(48000);
        quiet.Add(new float[48000]);
        Assert.Equal(0f, quiet.Level);
        var soft = BandFollower.Bass(48000);
        soft.Add(Sine(60, 48000, 1, 0.05f)); // a quiet song still pulses fully
        Assert.True(soft.Level > 0.6f, $"soft level {soft.Level}");
    }

    [Fact]
    public void Mid_hears_vocals_not_kicks_or_hats()
    {
        var voice = BandFollower.Mid(48000);
        voice.Add(Sine(800, 48000, 0.5, 0.3f));
        Assert.True(voice.Level > 0.6f, $"800 Hz level {voice.Level}");
        var kick = BandFollower.Mid(48000);
        kick.Add(Sine(800, 48000, 0.5, 0.3f));
        kick.Add(Sine(40, 48000, 0.5, 0.3f));
        Assert.True(kick.Level < 0.2f, $"40 Hz on the mids {kick.Level}");
        var hats = BandFollower.Mid(48000);
        hats.Add(Sine(800, 48000, 0.5, 0.3f));
        hats.Add(Sine(10000, 48000, 0.5, 0.3f));
        Assert.True(hats.Level < 0.2f, $"10 kHz on the mids {hats.Level}");
    }

    [Fact]
    public void Treble_hears_hi_hats_not_bass()
    {
        var hats = BandFollower.Treble(48000);
        hats.Add(Sine(8000, 48000, 0.5, 0.3f));
        Assert.True(hats.Level > 0.6f, $"8 kHz level {hats.Level}");
        var kick = BandFollower.Treble(48000);
        kick.Add(Sine(8000, 48000, 0.5, 0.3f));
        kick.Add(Sine(60, 48000, 0.5, 0.5f));
        Assert.True(kick.Level < 0.1f, $"60 Hz on the treble {kick.Level}");
    }
#endif

    [Fact]
    public void Mids_turn_the_hue_and_keep_the_brightness()
    {
        var c = ColorMath.RotateHue((255, 0, 0), 120);
        Assert.Equal(0, c.R, 3);
        Assert.Equal(255, c.G, 3);
        Assert.Equal((128.0, 128.0, 128.0), ColorMath.RotateHue((128, 128, 128), 90)); // grey stays grey
        var full = BassFollower.Shift((255, 0, 0), 1f, 100); // 60 degrees: red to yellow
        Assert.Equal(255, full.R, 3);
        Assert.Equal(255, full.G, 3);
        Assert.Equal((255.0, 0.0, 0.0), BassFollower.Shift((255, 0, 0), 0f, 100));
    }

    [Fact]
    public void Gain_lifts_or_lowers_the_level_and_treble_mixes_toward_white()
    {
        Assert.Equal(0.6f, BandFollower.WithGain(0.3f, 200), 4);
        Assert.Equal(1f, BandFollower.WithGain(0.8f, 400));
        Assert.Equal(0.15f, BandFollower.WithGain(0.3f, 50), 4);
        var c = BassFollower.Sparkle((255, 0, 0), 1f, 50, 100);
        Assert.Equal((255.0, 127.5, 127.5), c);
        Assert.Equal((255.0, 0.0, 0.0), BassFollower.Sparkle((255, 0, 0), 0f, 50, 100));
    }

    [Fact]
    public void Strength_sets_how_dark_it_gets_between_hits()
    {
        Assert.Equal(1.0, BassFollower.Factor(1f, 70), 6);
        Assert.Equal(0.3, BassFollower.Factor(0f, 70), 6);
        Assert.Equal(1.0, BassFollower.Factor(0f, 0), 6);
    }
}
