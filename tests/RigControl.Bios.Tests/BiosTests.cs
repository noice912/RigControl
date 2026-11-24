using System.Text.Json;
using RigControl.Bios;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Bios.Tests;

internal sealed class FakeFirmware : IFirmware
{
    public bool Available { get; set; } = true;
    public string? Problem { get; set; }
    public Dictionary<(string, Guid), byte[]> Vars { get; } = new();
    public IReadOnlyList<FirmwareVariable> List() => Vars.Keys.Select(k => new FirmwareVariable(k.Item1, k.Item2)).ToList();
    public (byte[] Data, uint Attributes)? Read(string name, Guid vendor) => Vars.TryGetValue((name, vendor), out var d) ? (d.ToArray(), 7u) : null;
    public string? Refuse { get; set; }
    public int Writes { get; private set; }
    public string? Write(string name, Guid vendor, byte[] data, uint attributes)
    {
        if (Refuse is not null) return Refuse;
        Writes++;
        Vars[(name, vendor)] = data.ToArray();
        return null;
    }
}

public class BiosTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-bios-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFirmware _fw = new();
    private readonly Dictionary<string, byte[]> _originals = new();
    private static readonly BiosMap Map = BiosMap.Bundled().Single();

    public BiosTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private async Task<BiosModule> Start(string board = "ROG STRIX Z790-A GAMING WIFI II", string bios = "2301", bool elevated = true)
    {
        var m = new BiosModule(() => _fw, () => (board, bios), BiosMap.Bundled()) { ReadOriginal = b => _originals.GetValueOrDefault(b), WriteOriginal = (b, d) => _originals[b] = d.ToArray() };
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    private static async Task<JsonElement> Status(BiosModule m)
    {
        var r = await m.InvokeAsync("getStatus", null, CancellationToken.None);
        return JsonSerializer.SerializeToElement(r, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private byte[] Stock()
    {
        var data = new byte[Map.Store.Size];
        _fw.Vars[("OcmrSettings", Map.Store.Guid)] = data;
        return data;
    }

    [Fact]
    public void The_bundled_map_is_for_this_board_and_holds_the_main_CPU_settings()
    {
        Assert.Equal("ROG STRIX Z790-A GAMING WIFI II", Map.Board);
        Assert.Equal("2301", Map.Bios);
        Assert.Equal(17546, Map.Store.Size);
        var names = Map.Settings.Select(s => s.Name).ToHashSet();
        foreach (var n in new[] { "Ai Overclock Tuner", "Performance Core Ratio", "Long Duration Package Power Limit", "Short Duration Package Power Limit", "SVID Behavior", "IA AC Load Line" })
            Assert.Contains(n, names);
        // Every setting fits inside the variable and none overlap.
        var used = new bool[Map.Store.Size];
        foreach (var s in Map.Settings)
        {
            Assert.InRange(s.Offset + s.Size, 1, Map.Store.Size);
            for (var i = s.Offset; i < s.Offset + s.Size; i++)
            {
                Assert.False(used[i], $"{s.Name} overlaps another setting at byte {i}");
                used[i] = true;
            }
        }
    }

    [Fact]
    public async Task Settings_are_decoded_from_the_variable()
    {
        var data = Stock();
        var tuner = Map.Settings.First(s => s.Name == "Ai Overclock Tuner");
        var pl1 = Map.Settings.First(s => s.Name == "Long Duration Package Power Limit");
        data[tuner.Offset] = 5; // XMP I
        data[pl1.Offset] = 0x2C; data[pl1.Offset + 1] = 0x01; // 300
        await using var m = await Start();
        var s = await Status(m);
        Assert.True(s.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("problem").ValueKind);
        Assert.Equal("OcmrSettings", s.GetProperty("variableName").GetString());
        var settings = s.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal("XMP I", settings.First(x => x.GetProperty("name").GetString() == "Ai Overclock Tuner").GetProperty("label").GetString());
        Assert.Equal(300, settings.First(x => x.GetProperty("name").GetString() == "Long Duration Package Power Limit").GetProperty("value").GetInt64());
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "bios-backups")));
    }

    [Fact]
    public async Task Another_BIOS_version_or_board_is_not_decoded()
    {
        Stock();
        await using (var m = await Start(bios: "2202"))
        {
            var s = await Status(m);
            Assert.Contains("2301", s.GetProperty("problem").GetString());
            Assert.Empty(s.GetProperty("settings").EnumerateArray());
        }
        await using (var m = await Start(board: "PRIME Z790-P"))
            Assert.Empty((await Status(m)).GetProperty("settings").EnumerateArray());
    }

    [Fact]
    public async Task A_variable_of_the_wrong_size_is_not_decoded()
    {
        _fw.Vars[("OcmrSettings", Map.Store.Guid)] = new byte[Map.Store.Size - 1];
        await using var m = await Start();
        var s = await Status(m);
        Assert.Contains("expected size", s.GetProperty("problem").GetString());
        Assert.Empty(s.GetProperty("settings").EnumerateArray());
    }

    [Fact]
    public async Task Nothing_is_read_without_administrator_rights()
    {
        Stock();
        await using var m = await Start(elevated: false);
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        Assert.Contains("administrator", (await Status(m)).GetProperty("problem").GetString());
    }

    // ---------------------------------------------------------------- writing

    private static readonly CpuEdits Edits = CpuEdits.For(Map, out _)!;

    private static async Task<JsonElement> Call(BiosModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var r = await m.InvokeAsync(method, a, CancellationToken.None);
        return JsonSerializer.SerializeToElement(r, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private byte[] Shipped()
    {
        // As read on the developer's PC: Intel Default Settings, Extreme; everything Auto.
        var d = Stock();
        d[348] = 0; d[352] = 2;
        return d;
    }

    [Fact]
    public void The_four_edits_map_onto_the_right_bytes()
    {
        var lines = new List<string>();
        var d = Edits.Apply(Shipped(), new CpuChange { UndervoltMv = 50, AllCoreRatio = 55, Pl1 = 200, Pl2 = 253 }, lines);
        Assert.Equal(2, d[348]);            // power limits need the ASUS Advanced OC Profile
        Assert.Equal(200, d[1358] | d[1359] << 8);
        Assert.Equal(253, d[1362] | d[1363] << 8);
        Assert.Equal(2, d[375]);            // Offset Mode
        Assert.Equal(1, d[378]);            // minus
        Assert.Equal(50, d[373] | d[374] << 8);
        Assert.Equal(1, d[55]);             // Sync All Cores
        Assert.Equal(55, d[56]);
        var now = Edits.Read(d);
        Assert.Equal(("asus", 200, 253, 50, 55), (now.Profile, now.Pl1, now.Pl2, now.UndervoltMv, now.AllCoreRatio));
        Assert.Equal(5, lines.Count);

        d = Edits.Apply(d, new CpuChange { Profile = "intelPerformance", Pl1 = 0, Pl2 = 0, UndervoltMv = 0, AllCoreRatio = 0 }, lines);
        Assert.Equal((0, 1, 0, 0, 0, 0), (d[348], d[352], d[375], d[378], d[55], d[56]));
    }

    [Theory]
    [InlineData(null, 300, null, null)]   // over Intel's 253 W
    [InlineData(null, 20, null, null)]    // too low
    [InlineData(null, null, -10, null)]   // never adds voltage
    [InlineData(null, null, 200, null)]   // too much undervolt
    [InlineData(null, null, null, 70)]    // ratio too high
    [InlineData("intelExtreme", 150, null, null)] // custom limits with Intel's own profile
    public void Outside_the_limits_is_refused(string? profile, int? pl1, int? uv, int? ratio) =>
        Assert.Throws<ArgumentException>(() => Edits.Apply(Shipped(), new CpuChange { Profile = profile, Pl1 = pl1, UndervoltMv = uv, AllCoreRatio = ratio }, new List<string>()));

    [Fact]
    public async Task A_change_is_written_checked_and_can_be_put_back()
    {
        _fw.Vars[("OcmrSettings", Map.Store.Guid)] = Shipped();
        await using var m = await Start();
        var original = _fw.Vars[("OcmrSettings", Map.Store.Guid)].ToArray();

        var s = await Call(m, "testWrite");
        Assert.True(s.GetProperty("writable").GetBoolean());
        Assert.Equal(original, _fw.Vars[("OcmrSettings", Map.Store.Guid)]);

        s = await Call(m, "applyCpu", new { undervoltMv = 40 });
        Assert.True(s.GetProperty("restartNeeded").GetBoolean());
        Assert.Equal(40, s.GetProperty("cpu").GetProperty("undervoltMv").GetInt32());
        var after = _fw.Vars[("OcmrSettings", Map.Store.Guid)];
        var diff = Enumerable.Range(0, after.Length).Where(i => after[i] != original[i]).ToList();
        Assert.Equal(new[] { 373, 375, 378 }, diff);

        s = await Call(m, "restoreOriginal");
        Assert.Equal(original, _fw.Vars[("OcmrSettings", Map.Store.Guid)]);
        Assert.Equal(0, s.GetProperty("cpu").GetProperty("undervoltMv").GetInt32());
    }

    [Fact]
    public async Task A_refused_write_says_so_and_changes_nothing()
    {
        _fw.Vars[("OcmrSettings", Map.Store.Guid)] = Shipped();
        await using var m = await Start();
        _fw.Refuse = "the BIOS keeps these settings write-protected from Windows";
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(m, "applyCpu", new { undervoltMv = 40 }));
        Assert.Contains("write-protected", e.Message);
        var s = await Call(m, "getStatus");
        Assert.False(s.GetProperty("writable").GetBoolean());
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task The_original_is_saved_once_and_never_overwritten_by_later_reads()
    {
        _fw.Vars[("OcmrSettings", Map.Store.Guid)] = Shipped();
        await using var m = await Start();
        Assert.Equal(Shipped(), _originals["2301"]);
        await Call(m, "applyCpu", new { undervoltMv = 40 });
        await Call(m, "getStatus");
        Assert.Equal(Shipped(), _originals["2301"]); // still the settings from before the change
    }

    [Theory]
    [InlineData("PRIME Z790-P", "2301", "no BIOS settings map for this motherboard")]
    [InlineData("ROG STRIX Z790-A GAMING WIFI II", "2202", "is for BIOS 2301")]
    [InlineData("ROG STRIX Z790-A GAMING WIFI", "2301", "no BIOS settings map for this motherboard")]
    [InlineData(null, null, "does not say which motherboard")]
    public async Task On_any_other_board_or_BIOS_the_module_says_it_is_not_available_and_never_opens_the_firmware(string? board, string? bios, string why)
    {
        Stock();
        var opened = 0;
        var m = new BiosModule(() => { opened++; return _fw; }, () => (board, bios), BiosMap.Bundled());
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, true, "1.0.0", "x.exe")), CancellationToken.None);
        await using (m)
        {
            Assert.Equal(ModuleState.Degraded, m.Status.State);
            Assert.StartsWith(BiosModule.NotAvailablePrefix, m.Status.Message);
            Assert.Contains(why, m.Status.Message);
            Assert.Equal(0, opened);

            var a = await Call(m, "getAvailability");
            Assert.False(a.GetProperty("available").GetBoolean());
            Assert.Contains(why, a.GetProperty("reason").GetString());

            var s = await Status(m);
            Assert.False(s.GetProperty("supported").GetBoolean());
            Assert.StartsWith(BiosModule.NotAvailablePrefix, s.GetProperty("problem").GetString());
            Assert.Empty(s.GetProperty("settings").EnumerateArray());
            await Assert.ThrowsAsync<RpcException>(() => Call(m, "applyCpu", new { undervoltMv = 40 }));
            await Assert.ThrowsAsync<RpcException>(() => Call(m, "testWrite"));
            Assert.Equal(0, _fw.Writes);
        }
    }

    [Fact]
    public async Task The_board_and_BIOS_the_map_was_made_for_is_available()
    {
        Stock();
        await using var m = await Start(board: " rog strix z790-a gaming wifi ii ", bios: "2301 ");
        Assert.Equal(ModuleState.Ready, m.Status.State);
        var a = await Call(m, "getAvailability");
        Assert.True(a.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("reason").ValueKind);
        Assert.True(BiosModule.CheckPc("ROG STRIX Z790-A GAMING WIFI II", "2301", BiosMap.Bundled()).Available);
    }
}
