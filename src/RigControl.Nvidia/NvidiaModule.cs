using System.Globalization;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Nvidia;

// ---------------------------------------------------------------- contract types (docs/API.md, "nvidia")

public sealed record NvidiaInfoDto(bool Available, string? DriverVersion, string? DriverBranch, IReadOnlyList<GpuInfo> Gpus, IReadOnlyList<string> Limitations);

public sealed record NvidiaPowerDto(double? PowerW, double? LimitW, double? DefaultLimitW, double? MinLimitW, double? MaxLimitW, bool CanSet, string? Reason);

public sealed record OverclockDto(bool Available, int? CoreMHz, int CoreMin, int CoreMax, int? MemoryMHz, int MemoryMin, int MemoryMax,
    bool CanSet, string? Reason, bool ApplyAtStart, string? Notice);

/// <summary>Clock offsets RigControl set, kept so they can be put back at start (off unless the user turns it on).</summary>
public sealed class OverclockSettings
{
    public const string Section = "nvidiaOverclock";
    public int CoreMHz { get; set; }
    public int MemoryMHz { get; set; }
    public bool ApplyAtStart { get; set; }
    /// <summary>Set while offsets applied at start are still being proven; still set at the next start = the PC crashed.</summary>
    public bool StartApplyPending { get; set; }
}

public sealed record SettingOptionDto(long Value, string Label);

public sealed record DriverSettingDto(string Key, string Name, string Group, string Description, string Kind, long Value, long? DefaultValue,
    bool IsDefault, IReadOnlyList<SettingOptionDto>? Options, long? Min, long? Max, string? Unit);

public sealed record DisplayModeResultDto(bool Applied, string RevertToken, int RevertInSeconds);

public sealed record DriverUpdateDto(string? Installed, string? Latest, bool? Newer, string? DownloadUrl, string? CheckedAt, string? Error, bool CanInstall = false);

public sealed record DriverInstallDto(bool Started, string Version);

public sealed record DriverDownloadProgressDto(string Version, double ReceivedMB, double? TotalMB, int? Percent);

/// <summary>One curated driver setting: a stable key, a plain-English name, and the exact driver id and values.</summary>
public sealed record SettingDef(string Key, uint Id, string Name, string Group, string Description, string Kind, uint Default,
    (uint Value, string Label)[]? Options = null, uint Min = 0, uint Max = 0, string? Unit = null);

/// <summary>
/// NVIDIA graphics: driver 3D settings (the NVIDIA Control Panel's "Manage 3D settings"), power limit, display
/// mode and digital vibrance, and a driver update check. See docs/protocols/nvidia.md.
///
/// Safety rules enforced here:
///  - Only the curated settings below can be written, and only with values from their lists or ranges.
///  - A setting the installed driver does not know is not offered at all.
///  - A display mode change is temporary until confirmed; after 15 s without confirmation the old mode returns.
///  - The power limit can only be set within the range the driver reports.
/// </summary>
public sealed class NvidiaModule : IAppModule
{
    /// <summary>Settings confirmed against the wrapper source and NVIDIA's own header (spec section 4.3).</summary>
    public static readonly IReadOnlyList<SettingDef> Settings = new[]
    {
        new SettingDef("vsync", 0x00A879CF, "Vertical sync", "Responsiveness",
            "Stops screen tearing by waiting for the display. Adds a little input delay.", "enum", 0x60925292,
            new (uint, string)[] { (0x60925292, "Let the game decide"), (0x08416747, "Off"), (0x47814940, "On"), (0x18888888, "Fast"), (0x32610244, "Half refresh rate") }),
        new SettingDef("maxFrameRate", 0x10835002, "Max frame rate", "Responsiveness",
            "Caps frames per second. 0 means no cap.", "number", 0, null, 0, 1023, "fps"),
        new SettingDef("lowLatency", 0x007BA09E, "Low latency mode", "Responsiveness",
            "On limits how many frames the processor queues ahead, which cuts input delay.", "enum", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("preferredRefresh", 0x0064B541, "Preferred refresh rate", "Responsiveness",
            "Highest available makes games use your display's top refresh rate.", "enum", 0,
            new (uint, string)[] { (0, "Let the game decide"), (1, "Highest available") }),
        new SettingDef("gsyncMode", 0x1194F158, "G-SYNC mode", "Responsiveness",
            "Where variable refresh rate is used.", "enum", 1,
            new (uint, string)[] { (0, "Off"), (1, "Full screen only"), (2, "Full screen and windowed") }),
        new SettingDef("powerMode", 0x1057EB71, "Power management mode", "Performance",
            "Prefer maximum performance keeps the card at full clocks, using more power when idle.", "enum", 5,
            new (uint, string)[] { (5, "Normal"), (1, "Prefer maximum performance"), (0, "Adaptive") }),
        new SettingDef("threadedOptimization", 0x20C1221E, "Threaded optimization", "Performance",
            "Lets the driver spread its work over several processor cores.", "enum", 0,
            new (uint, string)[] { (0, "Auto"), (1, "On"), (2, "Off") }),
        new SettingDef("shaderCacheSize", 0x00AC8497, "Shader cache size", "Performance",
            "Disk space for compiled shaders. A bigger cache means fewer stutters after the first run of a game.", "enum", 0x4000,
            new (uint, string)[] { (0x4000, "Driver default (16 GB)"), (0x1400, "5 GB"), (0x2800, "10 GB"), (0x19000, "100 GB"), (0xFFFFFFFF, "Unlimited"), (0, "Off") }),
        new SettingDef("textureQuality", 0x00CE2691, "Texture filtering quality", "Image quality",
            "High performance trades a little sharpness for speed.", "enum", 0,
            new (uint, string)[] { (0xFFFFFFF6, "High quality"), (0, "Quality"), (0xA, "Performance"), (0x14, "High performance") }),
        new SettingDef("fxaa", 0x1074C972, "FXAA antialiasing", "Image quality",
            "A fast edge-smoothing filter applied to every game. Can blur text.", "toggle", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("ambientOcclusion", 0x00667329, "Ambient occlusion", "Image quality",
            "Adds soft shadows where surfaces meet, in games the driver supports.", "enum", 0,
            new (uint, string)[] { (0, "Off"), (1, "Performance"), (2, "Quality") }),

        // DLSS overrides and RTX features. Ids and values as listed by NVIDIA Profile Inspector's CustomSettingNames.xml.
        new SettingDef("dlssOverride", 0x10E41E01, "DLSS: use the newest version", "DLSS and RTX",
            "Makes games that have DLSS Super Resolution use the newest DLSS the driver has, instead of the older one they shipped with.", "toggle", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("dlssPreset", 0x10E41DF3, "DLSS: model", "DLSS and RTX",
            "Which DLSS model games use. Recommended lets the driver pick the newest suitable one; K, L and M are the newer transformer models.", "enum", 0,
            new (uint, string)[] { (0, "Let the game decide"), (0x00FFFFFF, "Recommended (newest)"), (0x0B, "Model K"), (0x0C, "Model L"), (0x0D, "Model M"), (0x05, "Model E (older)") }),
        new SettingDef("dlssRayReconstruction", 0x10E41E02, "DLSS Ray Reconstruction: use the newest version", "DLSS and RTX",
            "Same as above for games with DLSS Ray Reconstruction.", "toggle", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("dlssFrameGen", 0x10E41E03, "DLSS Frame Generation: use the newest version", "DLSS and RTX",
            "Same as above for games with DLSS Frame Generation.", "toggle", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("smoothMotion", 0xB0D384C0, "Smooth Motion", "DLSS and RTX",
            "Driver-level frame generation for games without DLSS Frame Generation. Adds a little input delay.", "toggle", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("rtxHdr", 0x00DD48FB, "RTX HDR", "DLSS and RTX",
            "Converts games without HDR to HDR. Needs an HDR display with HDR switched on in Windows, and RTX HDR processing below set to something other than Off.", "toggle", 0,
            new (uint, string)[] { (0, "Off"), (1, "On") }),
        new SettingDef("rtxHdrProcessing", 0x00432F84, "RTX HDR processing", "DLSS and RTX",
            "Lets the driver do RTX HDR by itself (without the NVIDIA App's overlay). Debanding smooths colour steps in skies and gradients.", "enum", 0,
            new (uint, string)[] { (0, "Off"), (0x06, "On, no debanding"), (0x0A, "On, medium debanding"), (0x02, "On, strong debanding") }),
    };

    private static readonly string[] Limitations =
    {
        "RTX Video (sharper and HDR web video): switch it on in NVIDIA Control Panel under Video",
        "NVIDIA's own per-game recommended in-game settings: RigControl has quick setups for the driver side instead",
    };

    private int _installing;

    private const string DriverPage = "https://www.nvidia.com/en-us/drivers/";

    private readonly Func<string, INvidiaBackend> _backendFactory;
    private readonly int _revertSeconds;
    private readonly object _gate = new();
    private INvidiaBackend? _backend;
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private DriverInfo _info = new(false, null, null, Array.Empty<GpuInfo>(), "Starting.");
    private HashSet<string> _knownKeys = new();
    private int _disposed;

    // One pending display change at a time. Guarded by _gate.
    private string? _pendingToken;
    private string? _pendingDisplay;
    private ModeInfo? _pendingMode;
    private Timer? _revertTimer;

    public NvidiaModule() : this(dataDir => new NvidiaBackend(dataDir)) { }

    public NvidiaModule(Func<string, INvidiaBackend> backendFactory, int revertSeconds = 15)
    {
        _backendFactory = backendFactory;
        _revertSeconds = revertSeconds;
    }

    public string Name => "nvidia";
    public string DisplayName => "NVIDIA graphics";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        _status = new ModuleStatus(ModuleState.Starting);
        if (!TryInitialize())
        {
            // Right after Windows starts, the graphics driver can still be waking up. Keep trying for a few minutes
            // instead of leaving the whole Graphics section dead until RigControl is restarted.
            _log.Warn("The graphics driver did not answer yet; trying again every few seconds. " + _info.Problem);
            _retryTimer = new Timer(_ => RetryInitialize(), null, RetryMilliseconds, Timeout.Infinite);
        }
        return Task.CompletedTask;
    }

    /// <summary>How long to wait between attempts while the driver is not ready (short in tests).</summary>
    public int RetryMilliseconds { get; init; } = 5000;
    private const int MaxRetries = 36;
    private Timer? _retryTimer;
    private int _retries;

    private void RetryInitialize()
    {
        if (_disposed != 0) return;
        if (TryInitialize())
        {
            _log.Info("The graphics driver is answering now.");
            return;
        }
        if (++_retries >= MaxRetries)
        {
            _log.Error("The graphics driver never answered; the Graphics section stays off until RigControl is restarted. " + _info.Problem);
            return;
        }
        try { _retryTimer?.Change(RetryMilliseconds, Timeout.Infinite); } catch (ObjectDisposedException) { /* closing */ }
    }

    /// <summary>Connects to the driver. Returns false (with the reason in the status) if it is not usable yet.</summary>
    private bool TryInitialize()
    {
        lock (_gate)
        {
            try
            {
                try { _backend?.Dispose(); } catch { /* starting over */ }
                _backend = _backendFactory(_ctx!.Environment.DataDirectory);
                _info = _backend.Initialize();
            }
            catch (Exception ex)
            {
                _info = new DriverInfo(false, null, null, Array.Empty<GpuInfo>(), ex.Message);
            }
            if (_info.Available)
            {
                _knownKeys = Settings.Where(s => _backend!.IsSettingKnown(s.Id)).Select(s => s.Key).ToHashSet();
                var missing = Settings.Count - _knownKeys.Count;
                if (missing > 0) _log.Info($"{missing} driver setting(s) are not known to this driver version and are hidden.");
                _status = new ModuleStatus(ModuleState.Ready);
                _oc = _ctx!.Settings.Get<OverclockSettings>(OverclockSettings.Section);
                ApplyOverclockAtStart();
                _log.Info($"NVIDIA driver {_info.Version}, {_info.Gpus.FirstOrDefault()?.Name}.");
                return true;
            }
            _status = new ModuleStatus(ModuleState.Failed, _info.Problem ?? "No NVIDIA graphics card was found.");
            return false;
        }
    }

    private INvidiaBackend Backend()
    {
        if (_backend is null || !_info.Available)
            throw RpcException.Unavailable(_info.Problem ?? "NVIDIA graphics is not available.");
        return _backend;
    }

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        if (method == "checkDriverUpdate") return await CheckDriverUpdate(ct).ConfigureAwait(false);
        if (method == "installDriver") return await InstallDriver(ct).ConfigureAwait(false);
        lock (_gate)
        {
            return method switch
            {
                "getInfo" => new NvidiaInfoDto(_info.Available, _info.Version, _info.Branch, _info.Gpus,
                    _info.Available ? Limitations : new[] { _info.Problem ?? "No NVIDIA graphics card was found." }),
                "getPower" => GetPower(),
                "setPowerLimit" => SetPowerLimit(RpcArgs.Require<PowerArgs>(args)),
                "getOverclock" => GetOverclock(),
                "setOverclock" => SetOverclock(RpcArgs.Require<OverclockArgs>(args)),
                "resetOverclock" => SetOverclock(new OverclockArgs { CoreMHz = 0, MemoryMHz = 0 }),
                "getDriverSettings" => GetDriverSettings(RpcArgs.Optional<ProfileArgs>(args)),
                "setDriverSetting" => SetDriverSetting(RpcArgs.Require<SetSettingArgs>(args)),
                "resetDriverSetting" => ResetDriverSetting(RpcArgs.Require<SetSettingArgs>(args)),
                "listProfiles" => Backend().ListProfiles(RpcArgs.Optional<QueryArgs>(args).Query, 200),
                "addProgram" => AddProgram(RpcArgs.Require<AddProgramArgs>(args)),
                "getDisplays" => Backend().GetDisplays(),
                "setDisplayMode" => SetDisplayMode(RpcArgs.Require<DisplayModeArgs>(args)),
                "confirmDisplayMode" => ConfirmDisplayMode(RpcArgs.Require<TokenArgs>(args)),
                "revertDisplayMode" => RevertDisplayMode(RpcArgs.Require<TokenArgs>(args)),
                "setVibrance" => SetVibrance(RpcArgs.Require<VibranceArgs>(args)),
                _ => throw RpcException.UnknownMethod(Name, method),
            };
        }
    }

    // ---------------------------------------------------------------- power

    private NvidiaPowerDto GetPower()
    {
        var p = Backend().GetPower();
        var elevated = _ctx?.Environment.IsElevated ?? false;
        var hasRange = p.MinLimitW is not null && p.MaxLimitW is not null && p.MaxLimitW > p.MinLimitW;
        var reason = !hasRange ? "This graphics card does not report an adjustable power limit."
            : !elevated ? "Run RigControl as administrator to change the power limit."
            : null;
        return new NvidiaPowerDto(p.PowerW, p.LimitW, p.DefaultLimitW, p.MinLimitW, p.MaxLimitW, reason is null, reason);
    }

    private NvidiaPowerDto SetPowerLimit(PowerArgs a)
    {
        var backend = Backend();
        var p = backend.GetPower();
        if (p.MinLimitW is not { } min || p.MaxLimitW is not { } max || max <= min)
            throw RpcException.Unavailable("This graphics card does not report an adjustable power limit.");
        if (!(_ctx?.Environment.IsElevated ?? false))
            throw RpcException.NeedsAdmin("Run RigControl as administrator to change the power limit.");
        if (double.IsNaN(a.Watts) || a.Watts < min || a.Watts > max)
            throw RpcException.Unsafe($"The power limit must be between {min:0} and {max:0} W.");
        backend.SetPowerLimit(a.Watts);
        _log.Info($"GPU power limit set to {a.Watts:0} W.");
        return GetPower();
    }

    // ---------------------------------------------------------------- overclock (clock offsets)

    /// <summary>RigControl's own limits, inside whatever the driver allows. Voltage is never touched.</summary>
    public const int CoreCapMin = -500, CoreCapMax = 300, MemoryCapMin = -1000, MemoryCapMax = 1500;

    /// <summary>How long offsets applied at start must survive before they count as stable.</summary>
    public TimeSpan StartProofTime { get; init; } = TimeSpan.FromMinutes(3);

    private OverclockSettings _oc = new();
    private string? _ocNotice;
    private Timer? _ocProofTimer;

    private static (int Min, int Max) Range(int? driverMin, int? driverMax, int capMin, int capMax)
        => (Math.Max(driverMin ?? 0, capMin), Math.Min(driverMax ?? 0, capMax));

    private OverclockDto GetOverclock()
    {
        var r = Backend().GetClockOffsets();
        var core = Range(r.CoreMin, r.CoreMax, CoreCapMin, CoreCapMax);
        var mem = Range(r.MemoryMin, r.MemoryMax, MemoryCapMin, MemoryCapMax);
        var available = r.CoreMHz is not null && core.Max > core.Min;
        var elevated = _ctx?.Environment.IsElevated ?? false;
        var reason = !available ? "This graphics card or driver does not report adjustable clock offsets."
            : !elevated ? "Run RigControl as administrator to change clock offsets."
            : null;
        return new OverclockDto(available, r.CoreMHz, core.Min, core.Max, r.MemoryMHz, mem.Min, mem.Max,
            reason is null, reason, _oc.ApplyAtStart, _ocNotice);
    }

    private OverclockDto SetOverclock(OverclockArgs a)
    {
        var backend = Backend();
        var r = backend.GetClockOffsets();
        if (r.CoreMHz is null)
            throw RpcException.Unavailable("This graphics card or driver does not report adjustable clock offsets.");
        if (!(_ctx?.Environment.IsElevated ?? false))
            throw RpcException.NeedsAdmin("Run RigControl as administrator to change clock offsets.");
        var core = Range(r.CoreMin, r.CoreMax, CoreCapMin, CoreCapMax);
        var mem = Range(r.MemoryMin, r.MemoryMax, MemoryCapMin, MemoryCapMax);
        if (a.CoreMHz is { } c && (c < core.Min || c > core.Max))
            throw RpcException.Unsafe($"The core clock offset must be between {core.Min} and +{core.Max} MHz.");
        if (a.MemoryMHz is { } m && (r.MemoryMHz is null || m < mem.Min || m > mem.Max))
            throw RpcException.Unsafe($"The memory clock offset must be between {mem.Min} and +{mem.Max} MHz.");

        if (a.CoreMHz is { } newCore && newCore != r.CoreMHz)
        {
            backend.SetClockOffset(false, newCore);
            _log.Info($"GPU core clock offset set to {newCore:+0;-0;0} MHz (was {r.CoreMHz:+0;-0;0}).");
        }
        if (a.MemoryMHz is { } newMem && newMem != r.MemoryMHz)
        {
            backend.SetClockOffset(true, newMem);
            _log.Info($"GPU memory clock offset set to {newMem:+0;-0;0} MHz (was {r.MemoryMHz:+0;-0;0}).");
        }

        var now = backend.GetClockOffsets();
        _oc.CoreMHz = now.CoreMHz ?? 0;
        _oc.MemoryMHz = now.MemoryMHz ?? 0;
        if (a.ApplyAtStart is { } at) _oc.ApplyAtStart = at;
        _ocNotice = null;
        SaveOverclock();
        if ((a.CoreMHz is { } wc && wc != now.CoreMHz) || (a.MemoryMHz is { } wm && wm != now.MemoryMHz))
            throw RpcException.Unavailable("The graphics driver accepted the offset but reads back a different value.");
        return GetOverclock();
    }

    private void SaveOverclock() => _ctx?.Settings.Set(OverclockSettings.Section, _oc);

    /// <summary>
    /// Puts saved offsets back at start, only when the user turned that on. If the previous start applied them and
    /// the PC went down before they were proven (a crash or freeze), they are not applied again.
    /// </summary>
    private void ApplyOverclockAtStart()
    {
        if (!_oc.ApplyAtStart) return;
        if (_oc.StartApplyPending)
        {
            _oc.ApplyAtStart = false;
            _oc.StartApplyPending = false;
            SaveOverclock();
            _ocNotice = "The PC went down soon after RigControl applied your clock offsets last time, so they were not applied this time and \"Apply when RigControl starts\" was switched off.";
            _log.Warn("Clock offsets not applied at start: the previous start applied them and did not reach the stable point.");
            return;
        }
        if (_oc.CoreMHz == 0 && _oc.MemoryMHz == 0) return;
        if (!(_ctx?.Environment.IsElevated ?? false))
        {
            _log.Warn("Clock offsets not applied at start: RigControl is not running as administrator.");
            return;
        }
        try
        {
            _oc.StartApplyPending = true;
            SaveOverclock();
            var r = _backend!.GetClockOffsets();
            var core = Range(r.CoreMin, r.CoreMax, CoreCapMin, CoreCapMax);
            var mem = Range(r.MemoryMin, r.MemoryMax, MemoryCapMin, MemoryCapMax);
            var c = Math.Clamp(_oc.CoreMHz, core.Min, Math.Max(core.Min, core.Max));
            var m = Math.Clamp(_oc.MemoryMHz, mem.Min, Math.Max(mem.Min, mem.Max));
            if (r.CoreMHz is not null && c != r.CoreMHz) _backend.SetClockOffset(false, c);
            if (r.MemoryMHz is not null && m != r.MemoryMHz) _backend.SetClockOffset(true, m);
            _log.Info($"GPU clock offsets applied at start: core {c:+0;-0;0} MHz, memory {m:+0;-0;0} MHz.");
            _ocProofTimer = new Timer(_ => ClearStartPending(), null, StartProofTime, Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not apply the saved clock offsets at start.", ex);
            _oc.StartApplyPending = false;
            SaveOverclock();
        }
    }

    private void ClearStartPending()
    {
        lock (_gate)
        {
            if (!_oc.StartApplyPending) return;
            _oc.StartApplyPending = false;
            SaveOverclock();
        }
    }

    // ---------------------------------------------------------------- driver settings

    private SettingDef Def(string? key)
    {
        var def = Settings.FirstOrDefault(s => s.Key == key);
        if (def is null || !_knownKeys.Contains(def.Key)) throw RpcException.InvalidArgs("Unknown setting.");
        return def;
    }

    private static DriverSettingDto ToDto(SettingDef d, uint? stored)
    {
        var value = stored ?? d.Default;
        return new DriverSettingDto(d.Key, d.Name, d.Group, d.Description, d.Kind, value, d.Default,
            stored is null || stored == d.Default,
            d.Options?.Select(o => new SettingOptionDto(o.Value, o.Label)).ToList(),
            d.Kind == "number" ? d.Min : null, d.Kind == "number" ? d.Max : null, d.Unit);
    }

    private IReadOnlyList<DriverSettingDto> GetDriverSettings(ProfileArgs a)
    {
        var backend = Backend();
        return Settings.Where(s => _knownKeys.Contains(s.Key)).Select(s => ToDto(s, backend.ReadSetting(a.Profile, s.Id))).ToList();
    }

    private DriverSettingDto SetDriverSetting(SetSettingArgs a)
    {
        var def = Def(a.Key);
        if (a.Value is not { } raw || raw < 0 || raw > uint.MaxValue) throw RpcException.InvalidArgs("A value is required.");
        var value = (uint)raw;
        var ok = def.Kind == "number" ? value >= def.Min && value <= def.Max : def.Options!.Any(o => o.Value == value);
        if (!ok) throw RpcException.InvalidArgs($"That value is not valid for {def.Name}.");
        var backend = Backend();
        backend.WriteSetting(a.Profile, def.Id, value);
        _log.Info($"Driver setting {def.Key} = 0x{value:X} ({a.Profile ?? "all programs"}).");
        return ToDto(def, backend.ReadSetting(a.Profile, def.Id));
    }

    private DriverSettingDto ResetDriverSetting(SetSettingArgs a)
    {
        var def = Def(a.Key);
        var backend = Backend();
        backend.ResetSetting(a.Profile, def.Id);
        _log.Info($"Driver setting {def.Key} reset to default ({a.Profile ?? "all programs"}).");
        return ToDto(def, backend.ReadSetting(a.Profile, def.Id));
    }

    private ProfileInfo AddProgram(AddProgramArgs a)
    {
        if (string.IsNullOrWhiteSpace(a.ExePath) || !a.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw RpcException.InvalidArgs("Choose a program (.exe file).");
        if (!Path.IsPathFullyQualified(a.ExePath) || !File.Exists(a.ExePath))
            throw RpcException.InvalidArgs("That program file was not found.");
        return Backend().AddProgram(a.ExePath);
    }

    // ---------------------------------------------------------------- displays

    private DisplayInfo FindDisplay(string? id)
        => Backend().GetDisplays().FirstOrDefault(d => d.Id == id) ?? throw RpcException.InvalidArgs("Unknown display.");

    private DisplayModeResultDto SetDisplayMode(DisplayModeArgs a)
    {
        var backend = Backend();
        if (_pendingToken is not null) throw RpcException.Unavailable("Another display change is still waiting for you to keep or undo it.");
        var display = FindDisplay(a.DisplayId);
        var mode = new ModeInfo(a.Width, a.Height, a.RefreshHz);
        if (!display.Modes.Contains(mode)) throw RpcException.InvalidArgs("That display does not offer this mode.");
        if (!backend.TestMode(display.Id, mode)) throw RpcException.Unavailable("Windows says this display cannot use that mode.");

        backend.ApplyModeTemporarily(display.Id, mode);
        _pendingToken = Guid.NewGuid().ToString("N");
        _pendingDisplay = display.Id;
        _pendingMode = mode;
        var token = _pendingToken;
        _revertTimer = new Timer(_ => AutoRevert(token), null, TimeSpan.FromSeconds(_revertSeconds), Timeout.InfiniteTimeSpan);
        _log.Info($"Display {display.Id}: trying {mode.Width}x{mode.Height} at {mode.RefreshHz} Hz.");
        return new DisplayModeResultDto(true, token, _revertSeconds);
    }

    private void AutoRevert(string token)
    {
        lock (_gate)
        {
            if (_pendingToken != token) return;
            _log.Info("Display change was not confirmed; going back to the previous mode.");
            RevertPending();
        }
    }

    private void RevertPending()
    {
        var display = _pendingDisplay;
        ClearPending();
        if (display is null) return;
        try { _backend?.RevertMode(display); }
        catch (Exception ex) { _log.Error("Reverting the display mode failed.", ex); }
    }

    private void ClearPending()
    {
        _revertTimer?.Dispose();
        _revertTimer = null;
        _pendingToken = null;
        _pendingDisplay = null;
        _pendingMode = null;
    }

    private object ConfirmDisplayMode(TokenArgs a)
    {
        if (_pendingToken is null || a.RevertToken != _pendingToken)
            throw RpcException.InvalidArgs("That display change is no longer pending (it may have already been undone).");
        var display = _pendingDisplay!;
        var mode = _pendingMode!;
        ClearPending();
        Backend().KeepMode(display, mode);
        return new { kept = true };
    }

    private object RevertDisplayMode(TokenArgs a)
    {
        if (_pendingToken is not null && a.RevertToken == _pendingToken) RevertPending();
        return new { reverted = true };
    }

    private DisplayInfo SetVibrance(VibranceArgs a)
    {
        var display = FindDisplay(a.DisplayId);
        if (display.Vibrance is not { } v) throw RpcException.Unavailable("This display does not offer digital vibrance.");
        if (a.Value < v.Min || a.Value > v.Max) throw RpcException.InvalidArgs($"Vibrance must be between {v.Min} and {v.Max}.");
        Backend().SetVibrance(display.Id, a.Value);
        return FindDisplay(a.DisplayId);
    }

    // ---------------------------------------------------------------- driver update

    private async Task<DriverUpdateDto> CheckDriverUpdate(CancellationToken ct)
    {
        INvidiaBackend backend;
        string? installed;
        lock (_gate)
        {
            backend = Backend();
            installed = _info.Version;
        }
        var now = DateTimeOffset.Now.ToString("o");
        try
        {
            var latest = await backend.GetLatestDriverAsync(ct).ConfigureAwait(false);
            bool? newer = null;
            if (decimal.TryParse(latest.Version, NumberStyles.Number, CultureInfo.InvariantCulture, out var l)
                && decimal.TryParse(installed, NumberStyles.Number, CultureInfo.InvariantCulture, out var i))
                newer = l > i;
            return new DriverUpdateDto(installed, latest.Version, newer, latest.DetailsUrl ?? DriverPage, now, null, latest.DownloadUrl is not null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverLookupException ex)
        {
            // No driver can safely be offered for this card or this Windows; the message says why in plain words.
            _log.Info("Driver update check: " + ex.Message);
            return new DriverUpdateDto(installed, null, null, DriverPage, now, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Warn("Driver update check failed.", ex);
            return new DriverUpdateDto(installed, null, null, DriverPage, now,
                "NVIDIA's update service did not answer. You can check on NVIDIA's driver page instead.");
        }
    }

    /// <summary>
    /// User-triggered: downloads the newest driver from NVIDIA, checks NVIDIA signed it, and starts NVIDIA's installer.
    /// The installer asks for permission and shows its own steps; RigControl does not install anything silently.
    /// </summary>
    private async Task<DriverInstallDto> InstallDriver(CancellationToken ct)
    {
        INvidiaBackend backend;
        lock (_gate) backend = Backend();
        if (Interlocked.Exchange(ref _installing, 1) != 0) throw RpcException.Unavailable("A driver download is already running.");
        try
        {
            LatestDriver latest;
            try
            {
                latest = await backend.GetLatestDriverAsync(ct).ConfigureAwait(false);
            }
            catch (DriverLookupException ex)
            {
                throw RpcException.Unavailable(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw RpcException.Unavailable("NVIDIA's update service did not answer, so the driver could not be downloaded.");
            }
            if (latest.DownloadUrl is null || !DriverInstaller.IsAllowedUrl(latest.DownloadUrl))
                throw RpcException.Unavailable("NVIDIA did not give a download link for this driver. Use NVIDIA's driver page instead.");

            var dir = Path.Combine(_ctx?.Environment.DataDirectory ?? Path.GetTempPath(), "drivers");
            var safeVersion = new string(latest.Version.Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());
            var path = Path.Combine(dir, $"nvidia-driver-{safeVersion}.exe");
            if (!File.Exists(path) || !DriverInstaller.IsSignedByNvidia(path))
            {
                _log.Info($"Downloading NVIDIA driver {latest.Version}.");
                try
                {
                    await DriverInstaller.DownloadAsync(latest.DownloadUrl, path, (received, total) =>
                        _ctx?.Events.Publish("nvidia.driverDownload", new DriverDownloadProgressDto(latest.Version,
                            Math.Round(received / 1048576.0, 1), total is null ? null : Math.Round(total.Value / 1048576.0, 1),
                            total is > 0 ? (int)(received * 100 / total.Value) : null)), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("Driver download failed.", ex);
                    throw RpcException.Unavailable("The driver download failed: " + ex.Message);
                }
            }
            if (!DriverInstaller.IsSignedByNvidia(path))
            {
                try { File.Delete(path); } catch { /* ignore */ }
                _log.Error("The downloaded driver file is not signed by NVIDIA; it was deleted.");
                throw RpcException.Unsafe("The downloaded file is not signed by NVIDIA, so it was deleted and not run.");
            }
            try
            {
                DriverInstaller.Start(path);
            }
            catch (Exception ex)
            {
                throw RpcException.Unavailable("The installer could not be started: " + ex.Message);
            }
            _log.Info($"Started NVIDIA's installer for driver {latest.Version}.");
            return new DriverInstallDto(true, latest.Version);
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        try { _retryTimer?.Dispose(); } catch { /* ignore */ }
        try { _ocProofTimer?.Dispose(); } catch { /* ignore */ }
        // A clean close is not a crash: the offsets may be applied again at the next start.
        try { ClearStartPending(); } catch { /* ignore */ }
        lock (_gate)
        {
            // An unconfirmed display change must not outlive the app.
            if (_pendingToken is not null) RevertPending();
            try { _backend?.Dispose(); } catch { /* ignore */ }
            _backend = null;
        }
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    private sealed class ProfileArgs { public string? Profile { get; set; } }
    private sealed class QueryArgs { public string? Query { get; set; } }
    private sealed class PowerArgs { public double Watts { get; set; } }

    private sealed class OverclockArgs
    {
        public int? CoreMHz { get; set; }
        public int? MemoryMHz { get; set; }
        public bool? ApplyAtStart { get; set; }
    }
    private sealed class AddProgramArgs { public string? ExePath { get; set; } }
    private sealed class TokenArgs { public string? RevertToken { get; set; } }

    private sealed class SetSettingArgs
    {
        public string? Key { get; set; }
        public double? Value { get; set; }
        public string? Profile { get; set; }
    }

    private sealed class DisplayModeArgs
    {
        public string? DisplayId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshHz { get; set; }
    }

    private sealed class VibranceArgs
    {
        public string? DisplayId { get; set; }
        public int Value { get; set; }
    }
}
