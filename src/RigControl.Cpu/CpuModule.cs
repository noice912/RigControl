using System.Text.Json;
using RigControl.Core.Hardware;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Cpu;

// ---------------------------------------------------------------- contract

/// <summary>
/// Who the processor is. Readings (temperature, clock, load, power) are not here: the UI takes those from the
/// sensors module. <see cref="Tested"/> is false for a maker RigControl was not tried on; <see cref="Note"/> says so.
/// </summary>
public sealed record CpuInfoDto(string Name, string Vendor, string VendorId, int Family, int Model, int Stepping,
    int Cores, int Threads, int? PerformanceCores, int? EfficiencyCores,
    bool Known, string? Generation, string? CodeName, string? Line, string Summary, bool Tested, string? Note);

public sealed record CpuPowerOptionDto(uint Value, string Label);

/// <summary>
/// One power plan setting. Ac is the value while plugged in, Dc on battery; both null when Windows on this PC does
/// not have the setting (Available is false). OriginalAc/OriginalDc are set once RigControl has changed it.
/// </summary>
public sealed record CpuPowerSettingDto(string Key, bool Available, uint? Ac, uint? Dc, uint Min, uint Max, bool ZeroMeansNoLimit,
    string Unit, IReadOnlyList<CpuPowerOptionDto>? Options, bool CoreTypesDiffer, uint? OriginalAc, uint? OriginalDc);

public sealed record CpuPowerDto(bool Available, string? Problem, string? PlanId, string? PlanName, bool HasBattery, bool Changed,
    IReadOnlyList<string> ChangedPlans, IReadOnlyList<CpuPowerSettingDto> Settings);

/// <summary>The values to set for one power source. A field left out is not changed.</summary>
public sealed class CpuPowerValues
{
    public long? BoostMode { get; set; }
    public long? MaxStatePercent { get; set; }
    public long? MinStatePercent { get; set; }
    public long? MaxFrequencyMHz { get; set; }
    public long? MinCoresPercent { get; set; }
}

public sealed class CpuPowerChange
{
    /// <summary>While plugged in.</summary>
    public CpuPowerValues? Ac { get; set; }
    /// <summary>On battery. Refused on a PC without a battery.</summary>
    public CpuPowerValues? Dc { get; set; }
}

// ---------------------------------------------------------------- settings

/// <summary>A power plan value as it was before RigControl first changed it.</summary>
public sealed class PowerOriginal
{
    public uint? Ac { get; set; }
    public uint? Dc { get; set; }
}

/// <summary>Settings section "cpu". PowerOriginals: power plan id -> Windows setting id -> the values to put back.</summary>
public sealed class CpuSettings
{
    public Dictionary<string, Dictionary<string, PowerOriginal>> PowerOriginals { get; set; } = new();
}

/// <summary>
/// The CPU page for any PC: who the processor is (from the CPU itself and RigControl's table of generations), and
/// tuning through Windows' own power plan settings.
///
/// Safety rules:
///  - No driver is used and nothing is written to the processor: no MSR, no voltage, no AMD SMU. The only thing
///    changed is the Windows power plan in use, through Windows' documented power functions.
///  - Before a setting is changed for the first time, the values that are there are saved to RigControl's settings.
///    If that save fails, nothing is changed. "Put back how it was" writes exactly those values back.
///  - Every value is checked against a fixed range first.
/// </summary>
public sealed class CpuModule : IAppModule
{
    public const string SettingsSection = "cpu";
    public const string AmdNote = "AMD support is built but has not been tested on real hardware";
    public const string OtherNote = "RigControl has not been tried on a processor from this maker";

    /// <summary>
    /// One setting on the page. Class1 is the same setting for the second kind of core on a processor with fast and
    /// efficient cores; where Windows has it, both are set to the same value.
    /// </summary>
    internal sealed record SettingDef(string Key, string Label, Guid Setting, Guid? Class1, uint Min, uint Max, bool ZeroMeansNoLimit, string Unit);

    internal static readonly IReadOnlyList<CpuPowerOptionDto> BoostModes =
    [
        new(0, "Off"),
        new(1, "On"),
        new(2, "Aggressive"),
        new(3, "Efficient"),
        new(4, "Efficient aggressive"),
        new(5, "Aggressive at guaranteed"),
        new(6, "Efficient aggressive at guaranteed"),
    ];

    /// <summary>
    /// The settings, with the ids Windows gives them (powercfg names in brackets). The maximum processor state is
    /// kept at 30 % or more so a wrong click cannot make the PC too slow to use.
    /// </summary>
    internal static readonly IReadOnlyList<SettingDef> Defs =
    [
        // PERFBOOSTMODE
        new("boostMode", "Boost mode", new("be337238-0d82-4146-a960-4f3749d470c7"), null, 0, 6, false, "choice"),
        // PROCTHROTTLEMAX / PROCTHROTTLEMAX1
        new("maxStatePercent", "Maximum processor state", new("bc5038f7-23e0-4960-96da-33abaf5935ec"), new("bc5038f7-23e0-4960-96da-33abaf5935ed"), 30, 100, false, "%"),
        // PROCTHROTTLEMIN / PROCTHROTTLEMIN1
        new("minStatePercent", "Minimum processor state", new("893dee8e-2bef-41e0-89c6-b55d0929964c"), new("893dee8e-2bef-41e0-89c6-b55d0929964d"), 0, 100, false, "%"),
        // PROCFREQMAX / PROCFREQMAX1
        new("maxFrequencyMHz", "Maximum processor frequency", new("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), new("75b0ae3f-bce0-45a7-8c89-c9611c25e101"), 800, 10000, true, "MHz"),
        // CPMINCORES / CPMINCORES1
        new("minCoresPercent", "Cores kept awake", new("0cc5b647-c1df-4637-891a-dec35c318583"), new("0cc5b647-c1df-4637-891a-dec35c318584"), 0, 100, false, "%"),
    ];

    private readonly ICpuProbe _probe;
    private readonly IPowerPlan _power;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private CpuInfoDto? _info;

    public CpuModule() : this(new WindowsCpuProbe(), new WindowsPowerPlan()) { }

    public CpuModule(ICpuProbe probe, IPowerPlan power)
    {
        _probe = probe;
        _power = power;
    }

    public string Name => "cpu";
    public string DisplayName => "Processor";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        var info = GetInfo();
        _log.Info($"Processor: {info.Name}, {info.Cores} cores, {info.Threads} threads. {info.Summary}.");
        if (info.Note is not null) _log.Info(info.Note + ".");
        var power = GetPower();
        _status = power.Available
            ? new ModuleStatus(ModuleState.Ready)
            : new ModuleStatus(ModuleState.Degraded, "The processor is shown, but Windows' power plan could not be read: " + power.Problem);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        lock (_gate)
        {
            object? result = method switch
            {
                "getInfo" => GetInfo(),
                "getPower" => GetPower(),
                "setPower" => SetPower(RpcArgs.Require<CpuPowerChange>(args)),
                "resetPower" => ResetPower(),
                _ => throw RpcException.UnknownMethod(Name, method),
            };
            return Task.FromResult<object?>(result);
        }
    }

    // ---------------------------------------------------------------- identity

    private CpuInfoDto GetInfo()
    {
        if (_info is not null) return _info;
        CpuFacts facts;
        try
        {
            facts = _probe.Read();
        }
        catch (Exception ex)
        {
            _log.Warn("The processor could not be read.", ex);
            facts = new CpuFacts("Unknown processor", new CpuSignature("", 0, 0, 0), 0, Environment.ProcessorCount, null, null);
        }
        return _info = Describe(facts);
    }

    /// <summary>Puts what the processor says together with what RigControl's table knows about it.</summary>
    public static CpuInfoDto Describe(CpuFacts facts)
    {
        var s = facts.Signature;
        var known = CpuDatabase.Lookup(s, facts.Name);
        var note = known.Vendor switch
        {
            "Intel" => null,
            "AMD" => AmdNote,
            _ => OtherNote,
        };
        return new CpuInfoDto(facts.Name, known.Vendor, s.VendorId ?? "", s.Family, s.Model, s.Stepping,
            facts.Cores, facts.Threads, facts.PerformanceCores, facts.EfficiencyCores,
            known.Known, known.Generation, known.CodeName, known.Line, known.Summary, Tested: note is null, note);
    }

    // ---------------------------------------------------------------- power plan: reading

    private static string Id(Guid g) => g.ToString("D");

    private CpuSettings Stored() => _ctx?.Settings.Get<CpuSettings>(SettingsSection) ?? new CpuSettings();

    private static readonly IReadOnlyList<CpuPowerSettingDto> NoSettings = [];

    private CpuPowerDto GetPower()
    {
        try
        {
            var scheme = _power.ActiveScheme();
            var stored = Stored();
            stored.PowerOriginals.TryGetValue(Id(scheme), out var originals);
            var battery = _power.HasBattery();
            // Windows keeps a second copy of some settings for "the other kind of core" on every PC. It only
            // means something on a processor that really has fast and efficient cores.
            var info = GetInfo();
            var twoKinds = info.PerformanceCores is > 0 && info.EfficiencyCores is > 0;
            var rows = new List<CpuPowerSettingDto>(Defs.Count);
            foreach (var def in Defs)
            {
                var ac = _power.ReadAc(scheme, def.Setting);
                var dc = _power.ReadDc(scheme, def.Setting);
                bool differ = false;
                if (twoKinds && ac is not null && def.Class1 is { } class1)
                {
                    var ac1 = _power.ReadAc(scheme, class1);
                    var dc1 = _power.ReadDc(scheme, class1);
                    // The "on battery" values only count on a PC that has a battery.
                    differ = (ac1 is not null && ac1 != ac) || (battery && dc1 is not null && dc is not null && dc1 != dc);
                }
                PowerOriginal? original = null;
                originals?.TryGetValue(Id(def.Setting), out original);
                rows.Add(new CpuPowerSettingDto(def.Key, ac is not null, ac, dc, def.Min, def.Max, def.ZeroMeansNoLimit, def.Unit,
                    def.Key == "boostMode" ? BoostModes : null, differ, original?.Ac, original?.Dc));
            }
            var changedPlans = stored.PowerOriginals.Where(p => p.Value.Count > 0)
                .Select(p => Guid.TryParse(p.Key, out var g) ? _power.SchemeName(g) ?? p.Key : p.Key).ToList();
            return new CpuPowerDto(true, null, Id(scheme), _power.SchemeName(scheme) ?? "Power plan", battery,
                originals is { Count: > 0 }, changedPlans, rows);
        }
        catch (PowerPlanException ex)
        {
            return new CpuPowerDto(false, ex.Message, null, null, false, false, [], NoSettings);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new CpuPowerDto(false, "This Windows does not have the power plan functions RigControl uses.", null, null, false, false, [], NoSettings);
        }
    }

    // ---------------------------------------------------------------- power plan: changing

    private sealed record Write(SettingDef Def, Guid Setting, bool OnBattery, uint From, uint To);

    private static long? Wanted(CpuPowerValues v, string key) => key switch
    {
        "boostMode" => v.BoostMode,
        "maxStatePercent" => v.MaxStatePercent,
        "minStatePercent" => v.MinStatePercent,
        "maxFrequencyMHz" => v.MaxFrequencyMHz,
        "minCoresPercent" => v.MinCoresPercent,
        _ => null,
    };

    private static uint Checked(SettingDef def, long value)
    {
        if (def.ZeroMeansNoLimit && value == 0) return 0;
        if (value < def.Min || value > def.Max)
        {
            throw RpcException.InvalidArgs(def.Key == "boostMode"
                ? "That boost mode is not one Windows has."
                : $"{def.Label} must be between {def.Min} and {def.Max} {def.Unit}{(def.ZeroMeansNoLimit ? ", or 0 for no limit" : "")}.");
        }
        return (uint)value;
    }

    /// <summary>Works out every write for one power source, checking each value. Nothing is written here.</summary>
    private List<Write> Plan(Guid scheme, CpuPowerValues values, bool onBattery)
    {
        uint? Read(Guid setting) => onBattery ? _power.ReadDc(scheme, setting) : _power.ReadAc(scheme, setting);

        var writes = new List<Write>();
        var result = new Dictionary<string, uint>();
        foreach (var def in Defs)
        {
            var now = Read(def.Setting);
            if (Wanted(values, def.Key) is not { } wanted)
            {
                if (now is { } current) result[def.Key] = current;
                continue;
            }
            var to = Checked(def, wanted);
            if (now is null) throw RpcException.Unavailable($"Windows on this PC does not have the setting \"{def.Label}\".");
            result[def.Key] = to;
            if (now.Value != to) writes.Add(new Write(def, def.Setting, onBattery, now.Value, to));
            // The same value for the other kind of core, where Windows has that setting.
            if (def.Class1 is { } class1 && Read(class1) is { } now1 && now1 != to)
                writes.Add(new Write(def, class1, onBattery, now1, to));
        }
        if (result.TryGetValue("minStatePercent", out var min) && result.TryGetValue("maxStatePercent", out var max) && min > max
            && (values.MinStatePercent is not null || values.MaxStatePercent is not null))
            throw RpcException.InvalidArgs("The minimum processor state can't be higher than the maximum.");
        return writes;
    }

    private static RpcException Refused(PowerPlanException ex) => ex.AccessDenied
        ? RpcException.NeedsAdmin("Windows did not let RigControl change this power plan. Start RigControl as administrator and try again.")
        : RpcException.Unavailable(ex.Message);

    private CpuPowerDto SetPower(CpuPowerChange change)
    {
        if (_ctx is null) throw RpcException.Unavailable("The processor page has not started yet.");
        if (change.Ac is null && change.Dc is null) throw RpcException.InvalidArgs("Nothing to change was given.");
        try
        {
            if (change.Dc is not null && !_power.HasBattery())
                throw RpcException.InvalidArgs("This PC has no battery, so there are no \"on battery\" values to change.");
            var scheme = _power.ActiveScheme();
            var writes = new List<Write>();
            if (change.Ac is not null) writes.AddRange(Plan(scheme, change.Ac, onBattery: false));
            if (change.Dc is not null) writes.AddRange(Plan(scheme, change.Dc, onBattery: true));
            if (writes.Count == 0) return GetPower();

            // First remember what is there now, for every setting about to be touched for the first time.
            var stored = Stored();
            if (!stored.PowerOriginals.TryGetValue(Id(scheme), out var originals))
                stored.PowerOriginals[Id(scheme)] = originals = new Dictionary<string, PowerOriginal>();
            foreach (var setting in writes.Select(w => w.Setting).Distinct())
            {
                if (originals.ContainsKey(Id(setting))) continue;
                originals[Id(setting)] = new PowerOriginal { Ac = _power.ReadAc(scheme, setting), Dc = _power.ReadDc(scheme, setting) };
            }
            // Saved before every change, not only when an original was just added: after a save that failed the
            // originals are still in memory, and they must be in the file before the power plan is touched.
            if (!_ctx.Settings.Set(SettingsSection, stored))
                throw RpcException.Unavailable("RigControl could not save the values that are there now, so nothing was changed.");

            var name = _power.SchemeName(scheme) ?? Id(scheme);
            try
            {
                foreach (var w in writes)
                {
                    if (w.OnBattery) _power.WriteDc(scheme, w.Setting, w.To);
                    else _power.WriteAc(scheme, w.Setting, w.To);
                    _log.Info($"Power plan \"{name}\": {w.Def.Label}{(w.Setting == w.Def.Setting ? "" : " (second kind of core)")} " +
                              $"{(w.OnBattery ? "on battery" : "plugged in")} changed from {w.From} to {w.To}.");
                }
            }
            finally
            {
                // Whatever was written is made live, also when a later write was refused.
                try { _power.Apply(scheme); }
                catch (PowerPlanException ex) { _log.Warn("The power plan was saved but Windows did not apply it yet.", ex); }
            }
            return GetPower();
        }
        catch (PowerPlanException ex)
        {
            _log.Warn("The power plan could not be changed.", ex);
            throw Refused(ex);
        }
    }

    private CpuPowerDto ResetPower()
    {
        if (_ctx is null) throw RpcException.Unavailable("The processor page has not started yet.");
        try
        {
            var stored = Stored();
            if (stored.PowerOriginals.Count == 0) return GetPower();
            var active = _power.ActiveScheme();
            var existing = _power.Schemes().ToHashSet();
            foreach (var (schemeId, originals) in stored.PowerOriginals.ToList())
            {
                // A plan that was deleted since has nothing to put back.
                if (!Guid.TryParse(schemeId, out var scheme) || !existing.Contains(scheme))
                {
                    stored.PowerOriginals.Remove(schemeId);
                    _log.Info($"Power plan {schemeId} no longer exists; its saved values were dropped.");
                    continue;
                }
                foreach (var (settingId, original) in originals)
                {
                    // Only the settings this page owns are ever written, whatever the settings file says.
                    if (!Guid.TryParse(settingId, out var setting)) continue;
                    var def = Defs.FirstOrDefault(d => d.Setting == setting || d.Class1 == setting);
                    if (def is null) continue;
                    // A saved value Windows itself could never have had is not written back.
                    bool Sane(uint v) => def.Unit == "MHz" || v <= (def.Unit == "%" ? 100u : def.Max);
                    if (original.Ac is { } ac && Sane(ac)) _power.WriteAc(scheme, setting, ac);
                    if (original.Dc is { } dc && Sane(dc)) _power.WriteDc(scheme, setting, dc);
                }
                stored.PowerOriginals.Remove(schemeId);
                _log.Info($"Power plan \"{_power.SchemeName(scheme) ?? schemeId}\": the processor settings were put back how they were.");
            }
            _power.Apply(active);
            _ctx.Settings.Set(SettingsSection, stored);
            return GetPower();
        }
        catch (PowerPlanException ex)
        {
            _log.Warn("The power plan could not be put back.", ex);
            throw Refused(ex);
        }
    }
}
