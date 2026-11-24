using System.Buffers.Binary;
using System.Text.Json;
using RigControl.Core.Hardware;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Cpu;

namespace RigControl.Cpu.Tests;

internal sealed class FakeProbe(CpuFacts facts) : ICpuProbe
{
    public int Reads { get; private set; }
    public CpuFacts Read()
    {
        Reads++;
        return facts;
    }
}

/// <summary>Power plans kept in memory. Nothing here reaches Windows.</summary>
internal sealed class FakePowerPlan : IPowerPlan
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Fast = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public Guid Active { get; set; } = Balanced;
    public Dictionary<Guid, string> Names { get; } = new() { [Balanced] = "Balanced", [Fast] = "High performance" };
    public Dictionary<(Guid Scheme, Guid Setting), (uint Ac, uint Dc)> Values { get; } = new();
    public bool Battery { get; set; }
    public uint? RefuseWritesWith { get; set; }
    public uint? FailActiveWith { get; set; }
    public List<string> Writes { get; } = new();
    public List<Guid> Applied { get; } = new();

    public FakePowerPlan(bool twoKindsOfCores = false)
    {
        foreach (var scheme in new[] { Balanced, Fast })
        {
            foreach (var def in CpuModule.Defs)
            {
                var value = def.Key switch { "boostMode" => 2u, "maxStatePercent" => 100u, "minStatePercent" => 5u, "maxFrequencyMHz" => 0u, _ => 10u };
                Values[(scheme, def.Setting)] = (value, value);
                if (twoKindsOfCores && def.Class1 is { } class1) Values[(scheme, class1)] = (value, value);
            }
        }
    }

    public Guid ActiveScheme() => FailActiveWith is { } code ? throw new PowerPlanException("Windows did not say which power plan is in use", code) : Active;
    public IReadOnlyList<Guid> Schemes() => Names.Keys.ToList();
    public string? SchemeName(Guid scheme) => Names.GetValueOrDefault(scheme);
    public bool HasBattery() => Battery;
    public uint? ReadAc(Guid scheme, Guid setting) => Values.TryGetValue((scheme, setting), out var v) ? v.Ac : null;
    public uint? ReadDc(Guid scheme, Guid setting) => Values.TryGetValue((scheme, setting), out var v) ? v.Dc : null;

    public void WriteAc(Guid scheme, Guid setting, uint value)
    {
        if (RefuseWritesWith is { } code) throw new PowerPlanException("Windows did not save the power plan value", code);
        Values[(scheme, setting)] = (value, Values[(scheme, setting)].Dc);
        Writes.Add($"ac {setting} {value}");
    }

    public void WriteDc(Guid scheme, Guid setting, uint value)
    {
        if (RefuseWritesWith is { } code) throw new PowerPlanException("Windows did not save the power plan value", code);
        Values[(scheme, setting)] = (Values[(scheme, setting)].Ac, value);
        Writes.Add($"dc {setting} {value}");
    }

    public void Apply(Guid scheme) => Applied.Add(scheme);
}

public class CpuTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly CpuFacts RaptorLake = new("13th Gen Intel(R) Core(TM) i9-13900KF",
        new CpuSignature(CpuDatabase.IntelVendorId, 6, 0xB7, 1), 24, 32, 8, 16);
    private static readonly CpuFacts Ryzen = new("AMD Ryzen 7 7800X3D 8-Core Processor",
        new CpuSignature(CpuDatabase.AmdVendorId, 0x19, 0x61, 2), 8, 16, null, null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-cpu-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePowerPlan _power = new();

    public CpuTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private async Task<CpuModule> Start(CpuFacts? facts = null, FakePowerPlan? power = null)
    {
        var m = new CpuModule(new FakeProbe(facts ?? RaptorLake), power ?? _power);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance, new SettingsStore(SettingsPath, NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    private static async Task<JsonElement> Call(CpuModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, Web);
        var r = await m.InvokeAsync(method, a, CancellationToken.None);
        return JsonSerializer.SerializeToElement(r, RigControl.Core.Json.JsonDefaults.Options);
    }

    private static JsonElement Setting(JsonElement power, string key)
        => power.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    private static Guid Id(string key) => CpuModule.Defs.Single(d => d.Key == key).Setting;
    private static Guid Class1(string key) => CpuModule.Defs.Single(d => d.Key == key).Class1!.Value;

    // ---------------------------------------------------------------- identity

    [Fact]
    public async Task An_Intel_processor_shows_its_name_cores_and_generation_and_counts_as_tested()
    {
        var m = await Start();
        Assert.Equal(ModuleState.Ready, m.Status.State);
        var info = await Call(m, "getInfo");
        Assert.Equal("13th Gen Intel(R) Core(TM) i9-13900KF", info.GetProperty("name").GetString());
        Assert.Equal("Intel", info.GetProperty("vendor").GetString());
        Assert.Equal("GenuineIntel", info.GetProperty("vendorId").GetString());
        Assert.Equal(6, info.GetProperty("family").GetInt32());
        Assert.Equal(0xB7, info.GetProperty("model").GetInt32());
        Assert.Equal(1, info.GetProperty("stepping").GetInt32());
        Assert.Equal(24, info.GetProperty("cores").GetInt32());
        Assert.Equal(32, info.GetProperty("threads").GetInt32());
        Assert.Equal(8, info.GetProperty("performanceCores").GetInt32());
        Assert.Equal(16, info.GetProperty("efficiencyCores").GetInt32());
        Assert.True(info.GetProperty("known").GetBoolean());
        Assert.Equal("13th gen Core", info.GetProperty("generation").GetString());
        Assert.Equal("Raptor Lake", info.GetProperty("codeName").GetString());
        Assert.True(info.GetProperty("tested").GetBoolean());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task An_AMD_processor_is_recognised_and_plainly_marked_as_not_tested()
    {
        var m = await Start(Ryzen);
        var info = await Call(m, "getInfo");
        Assert.Equal("AMD", info.GetProperty("vendor").GetString());
        Assert.Equal("Zen 4", info.GetProperty("generation").GetString());
        Assert.Contains("Raphael", info.GetProperty("codeName").GetString());
        Assert.False(info.GetProperty("tested").GetBoolean());
        Assert.Equal("AMD support is built but has not been tested on real hardware", info.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("performanceCores").ValueKind);
    }

    [Fact]
    public async Task A_processor_newer_than_the_table_shows_its_own_name()
    {
        var m = await Start(new CpuFacts("Intel(R) Core(TM) Ultra 9 999K", new CpuSignature(CpuDatabase.IntelVendorId, 6, 0xFE, 0), 8, 8, null, null));
        var info = await Call(m, "getInfo");
        Assert.False(info.GetProperty("known").GetBoolean());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("generation").ValueKind);
        Assert.Equal("Intel(R) Core(TM) Ultra 9 999K: newer than this version knows", info.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task The_processor_is_read_once()
    {
        var probe = new FakeProbe(RaptorLake);
        var m = new CpuModule(probe, _power);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance, new SettingsStore(SettingsPath, NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe")), CancellationToken.None);
        await Call(m, "getInfo");
        await Call(m, "getInfo");
        Assert.Equal(1, probe.Reads);
    }

    [Fact]
    public void A_processor_from_another_maker_is_not_called_tested()
    {
        var info = CpuModule.Describe(new CpuFacts("Some CPU", new CpuSignature("HygonGenuine", 0x18, 0, 1), 4, 8, null, null));
        Assert.Equal("Other", info.Vendor);
        Assert.False(info.Tested);
        Assert.Equal(CpuModule.OtherNote, info.Note);
    }

    [Fact]
    public void The_name_a_processor_gives_is_tidied()
    {
        Assert.Equal("Intel(R) Core(TM) i7-2600K CPU @ 3.40GHz", WindowsCpuProbe.CleanName("       Intel(R) Core(TM) i7-2600K CPU @ 3.40GHz\0\0\0junk"));
        Assert.Equal("", WindowsCpuProbe.CleanName("\0\0\0"));
    }

    private static byte[] CoreRecord(byte efficiencyClass, ulong mask)
    {
        var r = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(r, 0);            // one core
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), 48); // record size
        r[9] = efficiencyClass;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), mask);
        return r;
    }

    [Fact]
    public void Cores_and_threads_are_counted_and_fast_and_efficient_cores_told_apart()
    {
        // Two fast cores with two threads each, four efficient cores with one thread each.
        var data = new[] { CoreRecord(1, 0b11), CoreRecord(1, 0b1100), CoreRecord(0, 0x10), CoreRecord(0, 0x20), CoreRecord(0, 0x40), CoreRecord(0, 0x80) }
            .SelectMany(x => x).ToArray();
        Assert.Equal((6, 8, (int?)2, (int?)4), WindowsCpuProbe.ParseCores(data));

        // All cores alike: no split is reported.
        var same = new[] { CoreRecord(0, 0b11), CoreRecord(0, 0b1100) }.SelectMany(x => x).ToArray();
        Assert.Equal((2, 4, (int?)null, (int?)null), WindowsCpuProbe.ParseCores(same));

        // A cut-off list is read as far as it is whole.
        Assert.Equal((1, 2, (int?)null, (int?)null), WindowsCpuProbe.ParseCores(same.AsSpan(0, 60)));
        Assert.Equal((0, 0, (int?)null, (int?)null), WindowsCpuProbe.ParseCores(ReadOnlySpan<byte>.Empty));
    }

    // ---------------------------------------------------------------- power plan

    [Fact]
    public async Task The_power_plan_in_use_is_shown_with_its_values_and_allowed_ranges()
    {
        var m = await Start();
        var p = await Call(m, "getPower");
        Assert.True(p.GetProperty("available").GetBoolean());
        Assert.Equal("Balanced", p.GetProperty("planName").GetString());
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", p.GetProperty("planId").GetString());
        Assert.False(p.GetProperty("hasBattery").GetBoolean());
        Assert.False(p.GetProperty("changed").GetBoolean());
        Assert.Equal(5, p.GetProperty("settings").GetArrayLength());

        var max = Setting(p, "maxStatePercent");
        Assert.True(max.GetProperty("available").GetBoolean());
        Assert.Equal(100, max.GetProperty("ac").GetInt32());
        Assert.Equal(30, max.GetProperty("min").GetInt32());
        Assert.Equal(100, max.GetProperty("max").GetInt32());
        Assert.Equal("%", max.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, max.GetProperty("originalAc").ValueKind);

        var boost = Setting(p, "boostMode");
        Assert.Equal(7, boost.GetProperty("options").GetArrayLength());
        Assert.Equal("Aggressive", boost.GetProperty("options")[2].GetProperty("label").GetString());

        var freq = Setting(p, "maxFrequencyMHz");
        Assert.True(freq.GetProperty("zeroMeansNoLimit").GetBoolean());
        Assert.Equal(0, freq.GetProperty("ac").GetInt32());
        Assert.Empty(_power.Writes);
    }

    [Fact]
    public async Task A_setting_Windows_does_not_have_is_marked_and_cannot_be_set()
    {
        _power.Values.Remove((FakePowerPlan.Balanced, Id("maxFrequencyMHz")));
        var m = await Start();
        var freq = Setting(await Call(m, "getPower"), "maxFrequencyMHz");
        Assert.False(freq.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, freq.GetProperty("ac").ValueKind);

        var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { maxFrequencyMHz = 4000 } }));
        Assert.Equal(RpcErrorCodes.Unavailable, ex.Code);
        Assert.Empty(_power.Writes);
    }

    [Fact]
    public async Task Changing_a_value_saves_the_original_first_writes_it_and_applies_the_plan()
    {
        var m = await Start();
        var p = await Call(m, "setPower", new { ac = new { maxStatePercent = 90, boostMode = 0 } });

        Assert.Equal(90u, _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Ac);
        Assert.Equal(100u, _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Dc);
        Assert.Equal(0u, _power.Values[(FakePowerPlan.Balanced, Id("boostMode"))].Ac);
        Assert.Equal(2, _power.Writes.Count);
        Assert.Equal([FakePowerPlan.Balanced], _power.Applied);

        Assert.True(p.GetProperty("changed").GetBoolean());
        Assert.Equal("Balanced", p.GetProperty("changedPlans")[0].GetString());
        var max = Setting(p, "maxStatePercent");
        Assert.Equal(90, max.GetProperty("ac").GetInt32());
        Assert.Equal(100, max.GetProperty("originalAc").GetInt32());
        Assert.Equal(100, max.GetProperty("originalDc").GetInt32());

        // The originals are in the settings file, so they survive a restart of the app.
        var saved = new SettingsStore(SettingsPath, NullLog.Instance).Get<CpuSettings>("cpu");
        Assert.Equal(100u, saved.PowerOriginals["381b4222-f694-41f0-9685-ff5bb260df2e"][Id("maxStatePercent").ToString()].Ac);
        Assert.Equal(2u, saved.PowerOriginals["381b4222-f694-41f0-9685-ff5bb260df2e"][Id("boostMode").ToString()].Ac);
    }

    [Fact]
    public async Task A_second_change_keeps_the_first_original()
    {
        var m = await Start();
        await Call(m, "setPower", new { ac = new { maxStatePercent = 90 } });
        var p = await Call(m, "setPower", new { ac = new { maxStatePercent = 80 } });
        Assert.Equal(80, Setting(p, "maxStatePercent").GetProperty("ac").GetInt32());
        Assert.Equal(100, Setting(p, "maxStatePercent").GetProperty("originalAc").GetInt32());
    }

    [Fact]
    public async Task Setting_the_value_that_is_already_there_changes_nothing()
    {
        var m = await Start();
        var p = await Call(m, "setPower", new { ac = new { maxStatePercent = 100, minStatePercent = 5 } });
        Assert.Empty(_power.Writes);
        Assert.Empty(_power.Applied);
        Assert.False(p.GetProperty("changed").GetBoolean());
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Put_back_how_it_was_restores_every_original_and_forgets_them()
    {
        var m = await Start();
        await Call(m, "setPower", new { ac = new { maxStatePercent = 70, minStatePercent = 50, maxFrequencyMHz = 3600, minCoresPercent = 100, boostMode = 0 } });
        _power.Applied.Clear();

        var p = await Call(m, "resetPower");
        Assert.False(p.GetProperty("changed").GetBoolean());
        Assert.Equal(0, p.GetProperty("changedPlans").GetArrayLength());
        Assert.Equal((100u, 100u), _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))]);
        Assert.Equal((5u, 5u), _power.Values[(FakePowerPlan.Balanced, Id("minStatePercent"))]);
        Assert.Equal((0u, 0u), _power.Values[(FakePowerPlan.Balanced, Id("maxFrequencyMHz"))]);
        Assert.Equal((10u, 10u), _power.Values[(FakePowerPlan.Balanced, Id("minCoresPercent"))]);
        Assert.Equal((2u, 2u), _power.Values[(FakePowerPlan.Balanced, Id("boostMode"))]);
        Assert.Equal([FakePowerPlan.Balanced], _power.Applied);
        Assert.Empty(new SettingsStore(SettingsPath, NullLog.Instance).Get<CpuSettings>("cpu").PowerOriginals);

        // Nothing left to put back: a second press writes nothing.
        var before = _power.Writes.Count;
        await Call(m, "resetPower");
        Assert.Equal(before, _power.Writes.Count);
    }

    [Fact]
    public async Task Put_back_also_restores_a_plan_that_is_no_longer_the_one_in_use_and_drops_a_deleted_one()
    {
        var m = await Start();
        await Call(m, "setPower", new { ac = new { maxStatePercent = 70 } });
        _power.Active = FakePowerPlan.Fast;
        await Call(m, "setPower", new { ac = new { maxStatePercent = 60 } });
        var deleted = Guid.NewGuid();
        new SettingsStore(SettingsPath, NullLog.Instance).Update<CpuSettings>("cpu", s =>
            s.PowerOriginals[deleted.ToString()] = new() { [Id("maxStatePercent").ToString()] = new PowerOriginal { Ac = 50, Dc = 50 } });
        // The module reads settings through its own store, so start a fresh one on the same file.
        m = await Start();
        Assert.Equal(3, (await Call(m, "getPower")).GetProperty("changedPlans").GetArrayLength());

        await Call(m, "resetPower");
        Assert.Equal(100u, _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Ac);
        Assert.Equal(100u, _power.Values[(FakePowerPlan.Fast, Id("maxStatePercent"))].Ac);
        Assert.DoesNotContain(_power.Values.Keys, k => k.Scheme == deleted);
        Assert.Equal(FakePowerPlan.Fast, _power.Applied[^1]);
    }

    [Fact]
    public async Task Put_back_never_writes_a_setting_this_page_does_not_own_or_a_value_Windows_could_not_have()
    {
        var foreign = Guid.NewGuid();
        new SettingsStore(SettingsPath, NullLog.Instance).Update<CpuSettings>("cpu", s =>
            s.PowerOriginals[FakePowerPlan.Balanced.ToString()] = new()
            {
                [foreign.ToString()] = new PowerOriginal { Ac = 1, Dc = 1 },
                [Id("maxStatePercent").ToString()] = new PowerOriginal { Ac = 5000, Dc = 90 },
                ["not an id"] = new PowerOriginal { Ac = 1 },
            });
        var m = await Start();
        await Call(m, "resetPower");
        Assert.Equal(["dc " + Id("maxStatePercent") + " 90"], _power.Writes);
    }

    [Theory]
    [InlineData("maxStatePercent", 29)]
    [InlineData("maxStatePercent", 101)]
    [InlineData("minStatePercent", -1)]
    [InlineData("minStatePercent", 101)]
    [InlineData("maxFrequencyMHz", 799)]
    [InlineData("maxFrequencyMHz", 10001)]
    [InlineData("minCoresPercent", 101)]
    [InlineData("boostMode", 7)]
    [InlineData("boostMode", -1)]
    public async Task A_value_outside_its_range_is_refused_and_nothing_is_written(string key, int value)
    {
        var m = await Start();
        var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new Dictionary<string, int> { [key] = value } }));
        Assert.Equal(RpcErrorCodes.InvalidArgs, ex.Code);
        Assert.Empty(_power.Writes);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task The_frequency_cap_takes_zero_for_no_limit_and_the_edges_of_its_range()
    {
        var m = await Start();
        await Call(m, "setPower", new { ac = new { maxFrequencyMHz = 800 } });
        await Call(m, "setPower", new { ac = new { maxFrequencyMHz = 10000 } });
        await Call(m, "setPower", new { ac = new { maxFrequencyMHz = 0 } });
        Assert.Equal(0u, _power.Values[(FakePowerPlan.Balanced, Id("maxFrequencyMHz"))].Ac);
        Assert.Equal(3, _power.Writes.Count);
    }

    [Fact]
    public async Task A_minimum_above_the_maximum_is_refused_also_against_the_value_already_there()
    {
        var m = await Start();
        var both = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { maxStatePercent = 50, minStatePercent = 60 } }));
        Assert.Equal(RpcErrorCodes.InvalidArgs, both.Code);
        // The maximum that is there is 100, the minimum 5: a maximum of 30 is fine, a minimum of 100 too.
        await Call(m, "setPower", new { ac = new { maxStatePercent = 30 } });
        var one = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { minStatePercent = 31 } }));
        Assert.Contains("can't be higher", one.Message);
        Assert.Single(_power.Writes);
    }

    [Fact]
    public async Task On_battery_values_are_refused_on_a_PC_without_a_battery_and_set_on_one_with()
    {
        var m = await Start();
        var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { dc = new { maxStatePercent = 60 } }));
        Assert.Equal(RpcErrorCodes.InvalidArgs, ex.Code);
        Assert.Empty(_power.Writes);

        _power.Battery = true;
        var p = await Call(m, "setPower", new { ac = new { maxStatePercent = 95 }, dc = new { maxStatePercent = 60 } });
        Assert.True(p.GetProperty("hasBattery").GetBoolean());
        Assert.Equal((95u, 60u), _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))]);
    }

    [Fact]
    public async Task On_a_processor_with_two_kinds_of_cores_both_get_the_value_and_both_are_put_back()
    {
        var power = new FakePowerPlan(twoKindsOfCores: true);
        power.Values[(FakePowerPlan.Balanced, Class1("maxStatePercent"))] = (95, 95);
        var m = await Start(power: power);
        Assert.True(Setting(await Call(m, "getPower"), "maxStatePercent").GetProperty("coreTypesDiffer").GetBoolean());
        Assert.False(Setting(await Call(m, "getPower"), "minStatePercent").GetProperty("coreTypesDiffer").GetBoolean());

        var p = await Call(m, "setPower", new { ac = new { maxStatePercent = 80, maxFrequencyMHz = 4200 } });
        Assert.Equal(80u, power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Ac);
        Assert.Equal(80u, power.Values[(FakePowerPlan.Balanced, Class1("maxStatePercent"))].Ac);
        Assert.Equal(4200u, power.Values[(FakePowerPlan.Balanced, Class1("maxFrequencyMHz"))].Ac);
        Assert.False(Setting(p, "maxStatePercent").GetProperty("coreTypesDiffer").GetBoolean());

        await Call(m, "resetPower");
        Assert.Equal((100u, 100u), power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))]);
        Assert.Equal((95u, 95u), power.Values[(FakePowerPlan.Balanced, Class1("maxStatePercent"))]);
        Assert.Equal((0u, 0u), power.Values[(FakePowerPlan.Balanced, Class1("maxFrequencyMHz"))]);
    }

    [Fact]
    public async Task On_a_processor_with_one_kind_of_core_the_second_copy_of_a_setting_is_not_reported_as_different()
    {
        var power = new FakePowerPlan(twoKindsOfCores: true); // Windows has the second copy on every PC
        power.Values[(FakePowerPlan.Balanced, Id("minStatePercent"))] = (50, 50); // as set in Windows' Power Options
        var m = await Start(Ryzen, power);
        foreach (var s in (await Call(m, "getPower")).GetProperty("settings").EnumerateArray())
            Assert.False(s.GetProperty("coreTypesDiffer").GetBoolean());
    }

    [Fact]
    public async Task When_the_originals_cannot_be_saved_nothing_is_changed_however_often_Save_is_pressed()
    {
        var m = await Start();
        // A folder where the settings file's temporary copy goes makes every save fail.
        Directory.CreateDirectory(SettingsPath + ".tmp");
        for (var press = 0; press < 3; press++)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { maxStatePercent = 90 } }));
            Assert.Contains("nothing was changed", ex.Message);
            Assert.Equal(100u, _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Ac);
            Assert.Empty(_power.Writes);
        }

        // Once the file can be written again the change goes through and the original is on disk.
        Directory.Delete(SettingsPath + ".tmp");
        await Call(m, "setPower", new { ac = new { maxStatePercent = 90 } });
        Assert.Equal(90u, _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Ac);
        var saved = new SettingsStore(SettingsPath, NullLog.Instance).Get<CpuSettings>("cpu");
        Assert.Equal(100u, saved.PowerOriginals[FakePowerPlan.Balanced.ToString("D")][Id("maxStatePercent").ToString("D")].Ac);
    }

    [Fact]
    public async Task When_Windows_refuses_the_change_the_error_says_to_run_as_administrator_and_put_back_still_works_later()
    {
        var m = await Start();
        _power.RefuseWritesWith = 5; // access denied
        var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { maxStatePercent = 90 } }));
        Assert.Equal(RpcErrorCodes.NeedsAdmin, ex.Code);
        Assert.Contains("administrator", ex.Message);
        Assert.Equal(100u, _power.Values[(FakePowerPlan.Balanced, Id("maxStatePercent"))].Ac);

        var reset = await Assert.ThrowsAsync<RpcException>(() => Call(m, "resetPower"));
        Assert.Equal(RpcErrorCodes.NeedsAdmin, reset.Code);

        _power.RefuseWritesWith = null;
        var p = await Call(m, "resetPower");
        Assert.False(p.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task Another_Windows_error_is_reported_as_unavailable()
    {
        var m = await Start();
        _power.RefuseWritesWith = 31;
        var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { maxStatePercent = 90 } }));
        Assert.Equal(RpcErrorCodes.Unavailable, ex.Code);
    }

    [Fact]
    public async Task When_the_power_plan_cannot_be_read_the_processor_is_still_shown()
    {
        _power.FailActiveWith = 1168;
        var m = await Start();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        Assert.Equal("Intel", (await Call(m, "getInfo")).GetProperty("vendor").GetString());
        var p = await Call(m, "getPower");
        Assert.False(p.GetProperty("available").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, p.GetProperty("problem").ValueKind);
        Assert.Equal(0, p.GetProperty("settings").GetArrayLength());
        var ex = await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { ac = new { maxStatePercent = 90 } }));
        Assert.Equal(RpcErrorCodes.Unavailable, ex.Code);
    }

    [Fact]
    public async Task Empty_or_unknown_requests_are_refused()
    {
        var m = await Start();
        Assert.Equal(RpcErrorCodes.InvalidArgs, (await Assert.ThrowsAsync<RpcException>(() => Call(m, "setPower", new { }))).Code);
        Assert.Equal(RpcErrorCodes.InvalidArgs, (await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("setPower", null, CancellationToken.None))).Code);
        Assert.Equal(RpcErrorCodes.UnknownMethod, (await Assert.ThrowsAsync<RpcException>(() => Call(m, "writeMsr"))).Code);
        Assert.Empty(_power.Writes);
    }

    [Fact]
    public void Only_processor_power_settings_are_on_the_list()
    {
        Assert.Equal(["boostMode", "maxStatePercent", "minStatePercent", "maxFrequencyMHz", "minCoresPercent"], CpuModule.Defs.Select(d => d.Key));
        var ids = CpuModule.Defs.SelectMany(d => d.Class1 is { } c ? new[] { d.Setting, c } : [d.Setting]).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), Id("maxStatePercent"));
        Assert.Equal(new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), Id("maxFrequencyMHz"));
    }
}
