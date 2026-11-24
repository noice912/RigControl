using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Safety;

namespace RigControl.Sensors;

// ---------------------------------------------------------------- contract types (docs/API.md, "sensors")

public sealed record CpuDto(string Name, double? PackageTempC, double? CoreMaxTempC, double? LoadPercent, double? PowerW, double? ClockMHz);

public sealed record GpuDto(string Name, double? TempC, double? HotspotTempC, double? LoadPercent, double? PowerW,
    double? CoreClockMHz, double? MemoryClockMHz, double? FanPercent, double? FanRpm, double? VramUsedMB, double? VramTotalMB);

public sealed record MemoryDto(double? UsedGB, double? TotalGB, double? LoadPercent);

public sealed record BoardDto(string Name, string? SuperIo);

public sealed record TemperatureDto(string Id, string Name, string Hardware, double? Value);

public sealed record FanDto(string Id, string Name, string Hardware, int? Rpm, string? ControlId);

public sealed record SensorSnapshotDto(string Time, bool Elevated, CpuDto? Cpu, GpuDto? Gpu, MemoryDto Memory, BoardDto? Board,
    IReadOnlyList<TemperatureDto> Temperatures, IReadOnlyList<FanDto> Fans, IReadOnlyList<string> Notes);

public sealed record FanCurveDto(string SourceId, IReadOnlyList<CurvePoint> Points);

public sealed record FanControlDto(string Id, string Name, string? RpmSensorId, string Mode, int? DutyPercent, FanCurveDto? Curve,
    int MinDutyPercent, bool CanControl, string? Reason);

public sealed record TemperatureSourceDto(string Id, string Name, string Hardware);

// ---------------------------------------------------------------- persisted settings

public sealed class FanSetting
{
    public string Mode { get; set; } = "fixed";   // "fixed" | "curve"
    public int Fixed { get; set; }
    public string? SourceId { get; set; }
    public List<CurvePoint>? Points { get; set; }
}

public sealed class FanSettings
{
    public const string Section = "sensors.fans";
    public Dictionary<string, FanSetting> Headers { get; set; } = new();
}

/// <summary>
/// Temperatures, loads and motherboard fan headers. See docs/protocols/sensors-and-fans.md.
///
/// Two modes:
///  - Basic (always): processor load, memory, and everything the graphics driver reports. No kernel driver.
///  - Full (administrator + the PawnIO driver installed): processor temperatures and power, motherboard
///    temperatures, fan speeds and fan control.
///
/// Fan safety rules enforced here:
///  - No header goes below 30 %. The header that is the AIO pump header on this board family is never controlled.
///  - A curve whose temperature cannot be read runs the fan at 100 %.
///  - Curves run only while RigControl runs; on exit every header is handed back to the BIOS.
///  - If RigControl dies while controlling fans, the next start says so (the chip keeps the last speed until a reboot).
///  - Graphics card fans follow the same rules (30 % floor, 100 % if the temperature is lost) and are handed back to
///    the card's own automatic control when RigControl closes or the user picks "automatic".
/// </summary>
public sealed class SensorsModule : IAppModule
{
    public const int MinDuty = 30;

    /// <summary>Control index that is the AIO pump header on ASUS Z790 boards (spec section 4.3). Never controlled there.</summary>
    public const int PumpControlIndex = 6;

    /// <summary>
    /// True when RigControl knows which header is the pump on this board (ASUS Z790 boards). On other boards no
    /// header is singled out: every header keeps the 30 % floor, and the fan panel tells the user to leave a
    /// pump's header on Automatic.
    /// </summary>
    private bool KnowsPumpHeader
    {
        get
        {
            var board = _source.Names.Board ?? "";
            return board.Contains("Z790", StringComparison.OrdinalIgnoreCase)
                   && (board.StartsWith("ROG", StringComparison.OrdinalIgnoreCase) || board.StartsWith("TUF", StringComparison.OrdinalIgnoreCase)
                       || board.StartsWith("PRIME", StringComparison.OrdinalIgnoreCase) || board.StartsWith("ProArt", StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool IsPumpHeader(int index) => KnowsPumpHeader && index == PumpControlIndex;

    private const string MarkerFile = "fan-override.active";

    private readonly IHardwareSource _source;
    private readonly bool _runLoop;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private Thread? _thread;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private int _disposed;

    // Guarded by _gate.
    private bool _open;
    private bool _elevated;
    private bool _lowLevel;
    private bool _crashedLastTime;
    private bool _wroteThisSession;
    private int _driverChecks;
    private bool _reopenFailed;
    private IReadOnlyList<RawSensor> _sensors = Array.Empty<RawSensor>();
    private SensorSnapshotDto? _snapshot;
    private FanSettings _settings = new();
    private readonly Dictionary<string, int> _lastDuty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _mismatch = new(StringComparer.Ordinal);

    private readonly IArmouryCrate _armoury;

    public SensorsModule() : this(new LibreHardwareSource(), true, new AsusArmouryCrate()) { }

    /// <param name="runLoop">False in tests, which call <see cref="Tick"/> themselves.</param>
    /// <param name="armoury">ASUS Armoury Crate control. Left out (tests), nothing is looked at or touched.</param>
    public SensorsModule(IHardwareSource source, bool runLoop = true, IArmouryCrate? armoury = null)
    {
        _source = source;
        _runLoop = runLoop;
        _armoury = armoury ?? new NoArmouryCrate();
    }

    public string Name => "sensors";
    public string DisplayName => "Sensors and motherboard fans";
    public ModuleStatus Status => _status;

    private string MarkerPath => Path.Combine(_ctx?.Environment.DataDirectory ?? Path.GetTempPath(), MarkerFile);

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        _elevated = context.Environment.IsElevated;
        _settings = context.Settings.Get<FanSettings>(FanSettings.Section);
        _status = new ModuleStatus(ModuleState.Starting);
        if (_runLoop)
        {
            // Opening the monitoring library takes a few seconds, so it happens on the worker thread.
            _thread = new Thread(Loop) { IsBackground = true, Name = "RigControl sensors" };
            _thread.Start();
        }
        else
        {
            Open();
        }
        return Task.CompletedTask;
    }

    private void Open()
    {
        lock (_gate)
        {
            if (_open) return;
            _lowLevel = _elevated && _source.DriverInstalled;
            _crashedLastTime = File.Exists(MarkerPath);
            _source.Open(_lowLevel);
            _open = true;
            _sensors = _source.Read();
            ReapplySaved();
        }
    }

    private void Loop()
    {
        try
        {
            Open();
        }
        catch (Exception ex)
        {
            _log.Error("Hardware monitoring could not start.", ex);
            _status = new ModuleStatus(ModuleState.Failed, "Hardware monitoring could not start: " + ex.Message);
            return;
        }
        while (!_stop.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { _log.Error("Sensor update failed.", ex); }
            if (_stop.Token.WaitHandle.WaitOne(1000)) break;
        }
    }

    /// <summary>RigControl is administrator but opened the sensors without the driver, because it was not installed then.</summary>
    private bool WaitingForDriver => _elevated && !_lowLevel && !_reopenFailed;

    /// <summary>
    /// The PawnIO driver was installed after the sensors were opened (the first-time setup does that): open them
    /// again with it, so processor temperatures and motherboard fans appear without restarting RigControl.
    /// Without the driver no motherboard fan can have been set, so nothing is left half-changed.
    /// Call with <see cref="_gate"/> held. True when the sensors were opened again.
    /// </summary>
    private bool ReopenIfDriverAppeared()
    {
        if (!_open || !WaitingForDriver || _wroteThisSession || !_source.DriverInstalled) return false;
        try
        {
            // Graphics card fans go back to the card's own control while the sensors are closed.
            foreach (var id in _settings.Headers.Keys.Where(IsGpuControl).ToList())
            {
                try { _source.SetControl(id, null); }
                catch { /* the card keeps its own control anyway once the sensors close */ }
            }
            _source.Dispose();
            _source.Open(true);
            _lowLevel = true;
            _sensors = _source.Read();
            ReapplySaved();
            _log.Info("The PawnIO driver is installed now: processor temperatures and motherboard fans are read without restarting RigControl.");
            return true;
        }
        catch (Exception ex)
        {
            _reopenFailed = true;
            _log.Warn("The PawnIO driver was installed while RigControl was running, but the sensors could not be opened again. Restart RigControl to see processor temperatures and motherboard fans.", ex);
            try
            {
                _source.Open(false);
                _sensors = _source.Read();
            }
            catch (Exception again)
            {
                _open = false;
                _status = new ModuleStatus(ModuleState.Failed, "Hardware monitoring stopped. Restart RigControl.");
                _log.Error("Hardware monitoring could not be opened again.", again);
            }
            return false;
        }
    }

    /// <summary>"sensors.driverInstalled": the setup just installed the driver, so look now instead of at the next check.</summary>
    private object DriverInstalledNow()
    {
        lock (_gate) return new { reopened = ReopenIfDriverAppeared() };
    }

    /// <summary>True when the motherboard's sensor chip was actually found (the only proof the driver works).</summary>
    private bool FullMode => _lowLevel && _sensors.Any(s => s.HardwareKind == "superio");

    /// <summary>One update: read everything, run fan curves, publish. Public for tests.</summary>
    public void Tick()
    {
        SensorSnapshotDto snapshot;
        lock (_gate)
        {
            if (!_open) return;
            // Every few seconds: has the driver been installed since RigControl started?
            if (WaitingForDriver && ++_driverChecks % 5 == 0) ReopenIfDriverAppeared();
            _sensors = _source.Read();
            RunFanControl();
            snapshot = _snapshot = BuildSnapshot();
            _status = FullMode
                ? new ModuleStatus(ModuleState.Ready)
                : new ModuleStatus(ModuleState.Degraded, snapshot.Notes.FirstOrDefault() ?? "Only basic readings are available.");
        }
        _ctx?.Events.Publish("sensors.snapshot", snapshot);
    }

    // ---------------------------------------------------------------- snapshot

    private double? Find(string hardwareKind, string kind, params string[] names)
    {
        foreach (var name in names)
        {
            // "memory" also contains a virtual-memory node with identical sensor names; only physical RAM ("/ram") counts.
            var s = _sensors.FirstOrDefault(x => x.HardwareKind == hardwareKind && x.Kind == kind
                                                 && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                                                 && (hardwareKind != "memory" || x.Id.StartsWith("/ram/", StringComparison.OrdinalIgnoreCase)));
            if (s?.Value is { } v) return v;
        }
        return null;
    }

    private static double? Round(double? v, int digits = 1) => v is { } x ? Math.Round(x, digits) : null;

    private SensorSnapshotDto BuildSnapshot()
    {
        var names = _source.Names;
        var full = FullMode;

        CpuDto? cpu = null;
        if (names.Cpu is not null)
        {
            var clocks = _sensors
                .Where(s => s.HardwareKind == "cpu" && s.Kind == "clock" && s.Value is > 0 && !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Value!.Value).ToList();
            cpu = new CpuDto(names.Cpu,
                Round(Find("cpu", "temperature", "CPU Package", "Core (Tctl/Tdie)")),
                Round(Find("cpu", "temperature", "Core Max")),
                Round(Find("cpu", "load", "CPU Total")),
                // Without the driver the library reports a meaningless 0 W; show "unknown" instead.
                full ? Round(Find("cpu", "power", "CPU Package", "Package")) : null,
                clocks.Count > 0 ? Round(clocks.Max(), 0) : null);
        }

        GpuDto? gpu = null;
        if (names.Gpu is not null)
        {
            var rpms = _sensors.Where(s => s.HardwareKind == "gpu" && s.Kind == "fan" && s.Value is not null).Select(s => s.Value!.Value).ToList();
            var pcts = _sensors.Where(s => s.HardwareKind == "gpu" && s.Kind == "control" && s.Value is not null).Select(s => s.Value!.Value).ToList();
            gpu = new GpuDto(names.Gpu,
                Round(Find("gpu", "temperature", "GPU Core")),
                Round(Find("gpu", "temperature", "GPU Hot Spot")),
                Round(Find("gpu", "load", "GPU Core")),
                Round(Find("gpu", "power", "GPU Package")),
                Round(Find("gpu", "clock", "GPU Core"), 0),
                Round(Find("gpu", "clock", "GPU Memory"), 0),
                pcts.Count > 0 ? Round(pcts.Max(), 0) : null,
                rpms.Count > 0 ? Round(rpms.Max(), 0) : null,
                Round(Find("gpu", "smallData", "GPU Memory Used"), 0),
                Round(Find("gpu", "smallData", "GPU Memory Total"), 0));
        }

        var used = Find("memory", "data", "Memory Used");
        var available = Find("memory", "data", "Memory Available");
        var memory = new MemoryDto(Round(used), used is not null && available is not null ? Round(used + available) : null,
            Round(Find("memory", "load", "Memory")));

        var temps = TemperatureSensors().Select(s => new TemperatureDto(s.Id, FriendlyTempName(s), FriendlyHardware(s), Round(s.Value))).ToList();

        var fans = new List<FanDto>();
        foreach (var s in _sensors.Where(s => s.HardwareKind == "superio" && s.Kind == "fan").OrderBy(s => s.Index))
        {
            var control = _sensors.FirstOrDefault(c => c.HardwareKind == "superio" && c.Kind == "control" && c.Index == s.Index);
            fans.Add(new FanDto(s.Id, HeaderName(s.Index), "Motherboard", s.Value is { } v ? (int)Math.Round(v) : null, control?.Id));
        }
        foreach (var s in _sensors.Where(s => s.HardwareKind == "gpu" && s.Kind == "fan").OrderBy(s => s.Index))
        {
            var control = _sensors.FirstOrDefault(c => c.HardwareKind == "gpu" && c.Kind == "control" && c.Index == s.Index);
            fans.Add(new FanDto(s.Id, control is null ? "Graphics card fan" : GpuFanName(control), "Graphics", s.Value is { } v ? (int)Math.Round(v) : null, control?.Id));
        }

        var notes = new List<string>();
        if (!_elevated)
            notes.Add("Run RigControl as administrator to read processor temperatures and motherboard fans.");
        else if (!_source.DriverInstalled)
            notes.Add("Processor temperatures and motherboard fans need the free PawnIO driver (pawnio.eu), which is not installed. Without it RigControl shows processor load, memory and the graphics card.");
        else if (!_lowLevel)
            notes.Add("The PawnIO driver was installed after RigControl started. Restart RigControl to see processor temperatures and motherboard fans.");
        else if (!full)
            notes.Add("The PawnIO driver is installed but the motherboard's sensor chip was not found, so motherboard fans are not available.");
        if (full && !KnowsPumpHeader)
            notes.Add("RigControl does not know which motherboard header is a pump header on this board. If a liquid cooler's pump is plugged into the motherboard, leave that header on Automatic.");
        if (_crashedLastTime)
            notes.Add("RigControl did not shut down cleanly while it was controlling motherboard fans. They may be stuck at a fixed speed until you restart Windows.");
        foreach (var (id, count) in _mismatch)
        {
            if (count >= 5) notes.Add($"{HeaderNameFor(id)} is not following RigControl. Another program (such as Armoury Crate) may be controlling it.");
        }

        return new SensorSnapshotDto(DateTimeOffset.Now.ToString("o"), _elevated, cpu, gpu, memory,
            string.IsNullOrWhiteSpace(names.Board) ? null : new BoardDto(names.Board, string.IsNullOrWhiteSpace(names.SuperIo) ? null : names.SuperIo),
            temps, fans, notes);
    }

    private IEnumerable<RawSensor> TemperatureSensors()
    {
        foreach (var s in _sensors)
        {
            if (s.Kind != "temperature" || s.Value is null) continue;
            var keep = s.HardwareKind switch
            {
                "cpu" => s.Name is "CPU Package" or "Core Max" or "Core (Tctl/Tdie)",
                "gpu" => s.Name is "GPU Core" or "GPU Hot Spot",
                "superio" => s.Value is > 5 and < 120, // unconnected inputs read garbage
                _ => false,
            };
            if (keep) yield return s;
        }
    }

    private static string FriendlyTempName(RawSensor s) => s.HardwareKind switch
    {
        "cpu" => s.Name == "Core Max" ? "Processor (hottest core)" : "Processor",
        "gpu" => s.Name == "GPU Hot Spot" ? "Graphics card (hot spot)" : "Graphics card",
        _ => s.Name.StartsWith("Temperature #", StringComparison.Ordinal) ? "Motherboard sensor " + s.Name[13..] : s.Name,
    };

    private static string FriendlyHardware(RawSensor s) => s.HardwareKind switch
    {
        "cpu" => "Processor",
        "gpu" => "Graphics",
        _ => "Motherboard",
    };

    private string HeaderName(int index) => IsPumpHeader(index) ? "AIO pump header" : "Fan header " + (index + 1);

    private string HeaderNameFor(string controlId)
        => _sensors.FirstOrDefault(s => s.Id == controlId) is { } s ? (s.HardwareKind == "gpu" ? GpuFanName(s) : HeaderName(s.Index)) : "A fan header";

    // ---------------------------------------------------------------- graphics card fans

    private List<RawSensor> GpuControlSensors()
        => _sensors.Where(s => s.HardwareKind == "gpu" && s.Kind == "control").OrderBy(s => s.Index).ToList();

    private bool IsGpuControl(string? id) => id is not null && _sensors.Any(s => s.Id == id && s.HardwareKind == "gpu" && s.Kind == "control");

    private string GpuFanName(RawSensor control)
    {
        var all = GpuControlSensors();
        var n = all.FindIndex(s => s.Id == control.Id);
        return all.Count <= 1 ? "Graphics card fans" : "Graphics card fan " + (n + 1);
    }

    private FanControlDto GpuControlDto(RawSensor c)
    {
        _settings.Headers.TryGetValue(c.Id, out var saved);
        var rpm = _sensors.FirstOrDefault(s => s.HardwareKind == "gpu" && s.Kind == "fan" && s.Index == c.Index);
        return new FanControlDto(c.Id, GpuFanName(c), rpm?.Id,
            saved is null ? "auto" : saved.Mode == "curve" ? "curve" : "fixed",
            c.Value is { } v ? (int)Math.Round(v) : null,
            saved is { Mode: "curve", SourceId: not null, Points: not null } ? new FanCurveDto(saved.SourceId, saved.Points) : null,
            MinDuty, true, null);
    }

    private IReadOnlyList<FanControlDto> GetGpuFans()
    {
        lock (_gate)
        {
            if (!_open) throw RpcException.Unavailable("Sensors are still starting. Try again in a moment.");
            return GpuControlSensors().Select(GpuControlDto).ToList();
        }
    }

    /// <summary>Finds a controllable fan by id: a graphics card fan (no driver needed) or a motherboard header (full mode).</summary>
    private RawSensor ResolveControl(string? id, out bool gpu)
    {
        if (!_open) throw RpcException.Unavailable("Sensors are still starting. Try again in a moment.");
        gpu = IsGpuControl(id);
        if (gpu) return GpuControlSensors().First(s => s.Id == id);
        RequireFull();
        return RequireControl(id);
    }

    private FanControlDto Dto(RawSensor c, bool gpu) => gpu ? GpuControlDto(c) : ControlDto(c);

    // ---------------------------------------------------------------- fan control

    private IEnumerable<RawSensor> ControlSensors()
        => _sensors.Where(s => s.HardwareKind == "superio" && s.Kind == "control").OrderBy(s => s.Index);

    private FanControlDto ControlDto(RawSensor c)
    {
        _settings.Headers.TryGetValue(c.Id, out var saved);
        var rpm = _sensors.FirstOrDefault(s => s.HardwareKind == "superio" && s.Kind == "fan" && s.Index == c.Index);
        var isPump = IsPumpHeader(c.Index);
        return new FanControlDto(c.Id, HeaderName(c.Index), rpm?.Id,
            saved is null ? "auto" : saved.Mode == "curve" ? "curve" : "fixed",
            c.Value is { } v ? (int)Math.Round(v) : null,
            saved is { Mode: "curve", SourceId: not null, Points: not null } ? new FanCurveDto(saved.SourceId, saved.Points) : null,
            isPump ? 100 : MinDuty,
            !isPump,
            isPump ? "Pump headers are never controlled from software, so a pump can't be slowed by mistake." : null);
    }

    private void RequireFull()
    {
        if (!_open) throw RpcException.Unavailable("Sensors are still starting. Try again in a moment.");
        if (!_elevated) throw RpcException.NeedsAdmin("Run RigControl as administrator to control motherboard fans.");
        if (!_source.DriverInstalled) throw RpcException.Unavailable("Motherboard fans need the PawnIO driver (pawnio.eu), which is not installed.");
        if (!_lowLevel) throw RpcException.Unavailable("The PawnIO driver was installed after RigControl started. Restart RigControl to control motherboard fans.");
        if (!FullMode) throw RpcException.Unavailable("The motherboard's fan chip was not found.");
    }

    private RawSensor RequireControl(string? id)
    {
        var c = ControlSensors().FirstOrDefault(s => s.Id == id) ?? throw RpcException.InvalidArgs("Unknown fan header.");
        if (IsPumpHeader(c.Index)) throw RpcException.Unsafe("Pump headers are never controlled from software.");
        return c;
    }

    private void Write(string controlId, int duty)
    {
        duty = Math.Clamp(duty, MinDuty, 100);
        // The warning file is about the motherboard chip, which keeps a speed until a reboot. A graphics card fan is
        // simply set again (or handed back) the next time RigControl starts.
        if (!_wroteThisSession && !IsGpuControl(controlId))
        {
            try { File.WriteAllText(MarkerPath, DateTime.Now.ToString("o")); } catch { /* the warning is a nicety */ }
            _wroteThisSession = true;
        }
        if (!_source.SetControl(controlId, duty)) throw RpcException.Unavailable("That fan header is no longer available.");
        _lastDuty[controlId] = duty;
    }

    private void ReapplySaved()
    {
        foreach (var (id, setting) in _settings.Headers.ToList())
        {
            var gpu = IsGpuControl(id);
            if (!gpu && (!FullMode || _crashedLastTime)) continue;
            var c = gpu ? GpuControlSensors().First(s => s.Id == id) : ControlSensors().FirstOrDefault(s => s.Id == id);
            if (c is null || (!gpu && IsPumpHeader(c.Index))) continue;
            try
            {
                Write(id, setting.Mode == "curve" ? CurveDuty(setting) : setting.Fixed);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not re-apply the saved setting for {HeaderNameFor(id)}.", ex);
            }
        }
    }

    private int CurveDuty(FanSetting setting)
    {
        var curve = FanCurve.Create(setting.Points, MinDuty, out _);
        if (curve is null) return 100;
        var temp = _sensors.FirstOrDefault(s => s.Id == setting.SourceId && s.Kind == "temperature")?.Value;
        return curve.Evaluate(temp ?? double.NaN); // unreadable temperature => 100 %
    }

    private void RunFanControl()
    {
        foreach (var (id, setting) in _settings.Headers)
        {
            var c = _sensors.FirstOrDefault(s => s.Id == id && s.Kind == "control");
            if (c is null) continue;
            var gpu = c.HardwareKind == "gpu";
            if (!gpu)
            {
                if (!FullMode || IsPumpHeader(c.Index)) continue;
                if (_crashedLastTime && !_wroteThisSession) continue; // after a crash, wait for the user to act
            }
            var want = setting.Mode == "curve" ? CurveDuty(setting) : Math.Clamp(setting.Fixed, MinDuty, 100);
            var had = _lastDuty.TryGetValue(id, out var last);
            // The chip can silently drop a write, and other software can overwrite it: check the read-back.
            var drifted = had && c.Value is { } actual && Math.Abs(actual - last) > 3;
            _mismatch[id] = drifted ? _mismatch.GetValueOrDefault(id) + 1 : 0;
            if (!had || want != last || drifted)
            {
                try { Write(id, want); }
                catch (Exception ex) { _log.Warn($"Writing {HeaderNameFor(id)} failed.", ex); }
            }
        }
    }

    // ---------------------------------------------------------------- RPC

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getSnapshot" => GetSnapshot(),
            "driverInstalled" => DriverInstalledNow(),
            "getFanControls" => GetFanControls(),
            "getGpuFans" => GetGpuFans(),
            "getTemperatureSources" => GetTemperatureSources(),
            "setFanFixed" => SetFanFixed(RpcArgs.Require<FanFixedArgs>(args)),
            "setFanCurve" => SetFanCurve(RpcArgs.Require<FanCurveArgs>(args)),
            "setFanAuto" => SetFanAuto(RpcArgs.Require<FanIdArgs>(args)),
            "getArmouryCrate" => _armoury.GetState(),
            "closeArmouryCrate" => CloseArmouryCrate(RpcArgs.Optional<CloseArmouryArgs>(args)),
            "setArmouryCrateAutoStart" => SetArmouryCrateAutoStart(RpcArgs.Require<ArmouryAutoStartArgs>(args)),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult<object?>(result);
    }

    private SensorSnapshotDto GetSnapshot()
    {
        lock (_gate)
        {
            if (!_open) throw RpcException.Unavailable("Sensors are still starting. Try again in a moment.");
            return _snapshot ??= BuildSnapshot();
        }
    }

    private IReadOnlyList<FanControlDto> GetFanControls()
    {
        lock (_gate)
        {
            RequireFull();
            return ControlSensors().Select(ControlDto).ToList();
        }
    }

    private IReadOnlyList<TemperatureSourceDto> GetTemperatureSources()
    {
        lock (_gate)
            return TemperatureSensors().Select(s => new TemperatureSourceDto(s.Id, FriendlyTempName(s), FriendlyHardware(s))).ToList();
    }

    private FanControlDto SetFanFixed(FanFixedArgs a)
    {
        lock (_gate)
        {
            var c = ResolveControl(a.Id, out var gpu);
            if (a.DutyPercent < MinDuty || a.DutyPercent > 100) throw RpcException.Unsafe($"Fans must stay between {MinDuty}% and 100%.");
            Write(c.Id, a.DutyPercent);
            _settings.Headers[c.Id] = new FanSetting { Mode = "fixed", Fixed = a.DutyPercent };
            Save();
            _log.Info($"{HeaderNameFor(c.Id)}: fixed at {a.DutyPercent}%.");
            return Dto(c, gpu) with { DutyPercent = a.DutyPercent };
        }
    }

    private FanControlDto SetFanCurve(FanCurveArgs a)
    {
        lock (_gate)
        {
            var c = ResolveControl(a.Id, out var gpu);
            if (!TemperatureSensors().Any(s => s.Id == a.SourceId)) throw RpcException.InvalidArgs("Choose a temperature for the curve to follow.");
            var curve = FanCurve.Create(a.Points, MinDuty, out var error);
            if (curve is null) throw RpcException.Unsafe(error ?? "The curve is not valid.");
            var setting = new FanSetting { Mode = "curve", SourceId = a.SourceId, Points = curve.Points.ToList() };
            var duty = CurveDuty(setting);
            Write(c.Id, duty);
            _settings.Headers[c.Id] = setting;
            Save();
            _log.Info($"{HeaderNameFor(c.Id)}: curve applied (now {duty}%).");
            return Dto(c, gpu) with { DutyPercent = duty };
        }
    }

    private FanControlDto SetFanAuto(FanIdArgs a)
    {
        lock (_gate)
        {
            var c = ResolveControl(a.Id, out var gpu);
            _source.SetControl(c.Id, null);
            _settings.Headers.Remove(c.Id);
            _lastDuty.Remove(c.Id);
            _mismatch.Remove(c.Id);
            Save();
            _log.Info($"{HeaderNameFor(c.Id)}: handed back to automatic control.");
            return Dto(c, gpu);
        }
    }

    private void Save() => _ctx?.Settings.Set(FanSettings.Section, _settings);

    // ---------------------------------------------------------------- ASUS Armoury Crate (user-triggered only)

    private void RequireAdminForArmoury()
    {
        if (!(_ctx?.Environment.IsElevated ?? false))
            throw RpcException.NeedsAdmin("Stopping Armoury Crate needs administrator rights. Restart RigControl as administrator, then try again.");
    }

    /// <summary>Stops Armoury Crate's background parts so they stop fighting RigControl over the board's fans and lights.</summary>
    private ArmouryState CloseArmouryCrate(CloseArmouryArgs a)
    {
        RequireAdminForArmoury();
        _log.Info("Stopping Armoury Crate at the user's request" + (a.DisableAutoStart ? " and turning off its automatic start." : "."));
        // Switch off its ways of starting first, so nothing brings it back while it is being stopped.
        if (a.DisableAutoStart) SetArmouryAutoStartCore(false);
        if (!_armoury.Close())
            throw RpcException.Unavailable("Some parts of Armoury Crate are still running: " + string.Join(", ", _armoury.GetState().Running) + ".");
        return _armoury.GetState();
    }

    private ArmouryState SetArmouryCrateAutoStart(ArmouryAutoStartArgs a)
    {
        RequireAdminForArmoury();
        SetArmouryAutoStartCore(a.Enabled);
        return _armoury.GetState();
    }

    private void SetArmouryAutoStartCore(bool enabled)
    {
        try
        {
            _armoury.SetStartsWithWindows(enabled);
            _log.Info("Armoury Crate automatic start: " + (enabled ? "restored" : "off") + " (user request).");
        }
        catch (Exception ex)
        {
            throw new RpcException(RpcErrorCodes.Internal, "Could not change how Armoury Crate starts: " + ex.Message, ex);
        }
    }

    private sealed class CloseArmouryArgs
    {
        public bool DisableAutoStart { get; set; }
    }

    private sealed class ArmouryAutoStartArgs
    {
        public bool Enabled { get; set; }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _stop.Cancel();
        _thread?.Join(4000);
        lock (_gate)
        {
            try
            {
                // Graphics card fans go back to the card's own automatic control: RigControl's curves stop here.
                foreach (var id in _settings.Headers.Keys.Where(IsGpuControl).ToList())
                {
                    try { _source.SetControl(id, null); }
                    catch (Exception ex) { _log.Warn("Could not hand a graphics card fan back to automatic control.", ex); }
                }
                _source.Dispose(); // hands every header this process changed back to the BIOS
                // A clean hand-back of fans this session wrote clears the marker. A marker left by an earlier
                // crash stays until then, because only a Windows restart restores the BIOS curves.
                if (_wroteThisSession)
                {
                    try { File.Delete(MarkerPath); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Handing fans back to the BIOS failed.", ex);
            }
            _open = false;
        }
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    private sealed class FanIdArgs
    {
        public string? Id { get; set; }
    }

    private sealed class FanFixedArgs
    {
        public string? Id { get; set; }
        public int DutyPercent { get; set; }
    }

    private sealed class FanCurveArgs
    {
        public string? Id { get; set; }
        public string? SourceId { get; set; }
        public List<CurvePoint>? Points { get; set; }
    }
}
