using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Safety;

namespace RigControl.Cooling;

// ---------------------------------------------------------------- contract types (docs/API.md, "cooling")

public sealed record CurveRange(int Min, int Max);

public sealed record CoolingChannelDto(
    string Id, string Name, string Kind, int? Rpm, int? DutyPercent, int MinDutyPercent,
    CurveRange? CurveTempRange, int? MaxCurvePoints, string? CurveSource, bool StoredOnDevice,
    string Mode, int? FixedDutyPercent, IReadOnlyList<CurvePoint>? Curve);

public sealed record LcdDto(int Brightness, int Orientation);

/// <summary>What RigControl is showing on the cooler's screen. Mode: "unset" (RigControl has not touched it), "liquid", "infographic" or "media".</summary>
public sealed record LcdScreenDto(
    string Mode, string Layout, string Primary, string Secondary, string Accent, string? MediaName, bool Live, string? Problem,
    IReadOnlyList<string> Layouts, IReadOnlyList<string> Sources,
    string Tertiary = "liquidTemp", string? BackgroundName = null, string? Url = null, IReadOnlyList<string>? Modes = null,
    bool TextFollowsLights = false);

public sealed record LcdPreviewDto(string Image);

public sealed record CoolingDeviceDto(
    string Id, string Name, string Product, string? Firmware, bool Connected, double? LiquidTempC,
    IReadOnlyList<CoolingChannelDto> Channels, LcdDto? Lcd, IReadOnlyList<string> Notes, LcdScreenDto? Screen = null);

public sealed record CamDto(bool AppRunning, bool ServiceRunning, bool StartsWithWindows);

public sealed record CoolingStatusDto(IReadOnlyList<string> BlockedBy, IReadOnlyList<CoolingDeviceDto> Devices, CamDto Cam);

// ---------------------------------------------------------------- persisted settings

public sealed class ChannelSetting
{
    public string Mode { get; set; } = "fixed";          // "fixed" | "curve"
    public int Fixed { get; set; }
    public List<CurvePoint>? Points { get; set; }
}

public sealed class LcdSettings
{
    public string Mode { get; set; } = "unset";           // "unset" | "liquid" | "infographic" | "media" | "clock" | "visualizer" | "web"
    public string Layout { get; set; } = "single";        // "single" | "dual"
    public string Primary { get; set; } = "cpuTemp";
    public string Secondary { get; set; } = "gpuTemp";
    public string Accent { get; set; } = "#8B5CF6";
    public string Tertiary { get; set; } = "liquidTemp";
    /// <summary>Optional picture or GIF shown behind an infographic or the clock.</summary>
    public string? Background { get; set; }
    public string? WebUrl { get; set; }
    public string? MediaPath { get; set; }
    public int Rotation { get; set; }
    public int Brightness { get; set; } = 80;
    /// <summary>The infographic's numbers and labels take the lights' colour while Lighting follows the screen.</summary>
    public bool TextFollowsLights { get; set; }
}

public sealed class CoolingSettings
{
    public const string Section = "cooling";
    public LcdSettings Lcd { get; set; } = new();
    public Dictionary<string, ChannelSetting> Kraken { get; set; } = new();       // "pump", "fan"
    public Dictionary<string, int> ControllerDuty { get; set; } = new();          // "fan1".."fan3"
    /// <summary>Case fans that follow the liquid temperature. RigControl runs these curves itself (the controller cannot).</summary>
    public Dictionary<string, List<CurvePoint>> ControllerCurves { get; set; } = new();
}

/// <summary>
/// NZXT cooling: the Kraken Elite (pump + radiator fans, curves stored on the device) and the RGB &amp; Fan
/// Controller (three fan channels, fixed speeds). See docs/protocols/nzxt-kraken-elite.md and nzxt-controllers.md.
///
/// Safety rules enforced here:
///  - While any part of NZXT CAM runs, nothing is ever written to a device (readings are only listened for).
///  - Only allow-listed packets can be sent, and only when the device reports 64-byte reports.
///  - The pump never goes below 20 %, Kraken fans below 20 %, controller fans below 25 %.
///  - Every Kraken curve ends at 100 % at 59 °C, so whatever is left in the cooler stays safe if RigControl stops.
///  - On exit nothing is sent that changes cooling: the Kraken keeps running its stored curve; the controller keeps
///    its last duty. The only thing sent is "show the liquid temperature again" when a live infographic was on the
///    screen, so it does not freeze on stale numbers.
///  - The screen is only ever changed by a user action (or by re-starting the infographic the user chose).
///  - Case-fan curves are run by RigControl. If the liquid temperature is lost, those fans go to 60 %; when
///    RigControl closes they are left at 50 % or their current speed, whichever is higher, never at a low speed.
/// </summary>
public sealed class CoolingModule : IAppModule
{
    public const string KrakenId = "kraken";
    public const string ControllerId = "fan-controller";
    public const int KrakenFanMinDuty = 20;
    private const int MaxUserCurvePoints = 8;
    /// <summary>Speed for curve-driven case fans when the liquid temperature is not known.</summary>
    public const int ControllerFallbackDuty = 60;
    /// <summary>Lowest speed curve-driven case fans are left at when RigControl closes.</summary>
    public const int ControllerExitDuty = 50;
    private readonly int[] _controllerSent = { -1, -1, -1 };
    private readonly DateTime[] _controllerSentAt = new DateTime[3];
    private readonly int[] _controllerResends = new int[3];
    private DateTime _controllerCurveAt = DateTime.MinValue;

    private readonly IHidBus _bus;
    private readonly ICamControl _cam;
    private readonly object _io = new();
    private readonly CancellationTokenSource _stop = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private Thread? _thread;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private int _disposed;
    private readonly bool _runLoop;

    // Everything below is guarded by _io.
    private CamState _camState = new(false, false, false);
    private DateTime _unblockedSince = DateTime.MinValue;
    private IHidLink? _kraken;
    private bool _krakenPresent;
    private bool _controllerPresent;
    private bool _krakenWritable;
    private bool _krakenInitialised;
    private string? _krakenFirmware;
    private KrakenProtocol.Status? _krakenStatus;
    private DateTime _krakenStatusAt;
    private KrakenProtocol.LcdInfo? _lcd;
    private readonly List<string> _krakenNotes = new();
    private IHidLink? _controller;
    private bool _controllerWritable;
    private bool _controllerInitialised;
    private string? _controllerFirmware;
    private FanControllerProtocol.FanStatus[]? _fans;
    private DateTime _fansAt;
    private int _badControllerReports;
    private CoolingSettings _settings = new();
    private readonly ILcdPipeSource _pipes;
    private ILcdPipe? _pipe;
    private bool _live;
    private int _liveFailures;
    private string? _lcdProblem;
    private volatile LcdMetrics _metrics = LcdMetrics.Empty;
    private readonly LcdComposer _composer;
    private readonly LcdComposer _previewComposer = new(new SampleSpectrum());
    private Thread? _lcdThread;
    // Diagnostics: say each thing once in the log so a takeover can be followed step by step.
    private bool _krakenOpenFailureLogged;
    private bool _controllerOpenFailureLogged;
    private bool _krakenFirstStatusLogged;
    private bool _controllerFirstStatusLogged;
    private bool _krakenSilentLogged;
    private int _unknownReportsLogged;
    private DateTime? _logReadingsAt;   // a few seconds after a change, log what the devices then report

    public CoolingModule() : this(new HidSharpBus(), new NzxtCam(), true, new WinUsbLcdPipeSource(), new LoopbackSpectrum()) { }

    /// <param name="runLoop">False in tests, which call <see cref="Tick"/> themselves instead of using the background loop.</param>
    /// <param name="lcd">The screen's data channel. Left out (tests), the channel is never available.</param>
    /// <param name="spectrum">What the PC is playing, for the audio visualizer. Left out (tests), a fixed pattern is used.</param>
    public CoolingModule(IHidBus bus, ICamControl cam, bool runLoop = true, ILcdPipeSource? lcd = null, ISpectrumSource? spectrum = null)
    {
        _bus = bus;
        _cam = cam;
        _runLoop = runLoop;
        _pipes = lcd ?? new NoLcdPipeSource();
        _composer = new LcdComposer(spectrum ?? new SampleSpectrum());
        _composer.TextColor = LettersColor;
        _previewComposer.TextColor = LettersColor; // the preview shows the lights' colour too
    }

    /// <summary>
    /// Where pictures of a web page come from (the app's hidden browser). Given a web address it returns the newest
    /// picture as PNG bytes, or null while the page loads; given null it closes the page.
    /// </summary>
    public void SetWebFrameSource(Func<string?, byte[]?> source) => _composer.WebFrames = source;

    private int _syncColor = -1;

    /// <summary>The colour the lights show right now while they follow the screen (0xRRGGBB), or null when they don't.</summary>
    public void SetLightsColor(int? rgb) => Volatile.Write(ref _syncColor, rgb ?? -1);

    private System.Drawing.Color? LettersColor()
    {
        var c = Volatile.Read(ref _syncColor);
        if (c < 0 || !_settings.Lcd.TextFollowsLights) return null;
        // Very dark colours would make the numbers unreadable: keep them at least a little bright.
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255, max = Math.Max(r, Math.Max(g, b));
        if (max < 90)
        {
            if (max == 0) return System.Drawing.Color.FromArgb(255, 90, 90, 90);
            var k = 90.0 / max;
            (r, g, b) = ((int)(r * k), (int)(g * k), (int)(b * k));
        }
        return System.Drawing.Color.FromArgb(255, r, g, b);
    }

    private volatile bool _fahrenheit = true;

    /// <summary>Temperatures on the cooler's screen in °F (true) or °C. Safe to call from any thread.</summary>
    public void SetFahrenheit(bool fahrenheit) => _fahrenheit = fahrenheit;

    /// <summary>Latest readings from the rest of the app, for the infographic. Safe to call from any thread.</summary>
    public void SetMetrics(LcdMetrics metrics) => _metrics = metrics;

    public string Name => "cooling";
    public string DisplayName => "NZXT cooling";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        _settings = context.Settings.Get<CoolingSettings>(CoolingSettings.Section);
        _status = new ModuleStatus(ModuleState.Starting);
        if (_runLoop)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "RigControl cooling" };
            _thread.Start();
            _lcdThread = new Thread(LcdLoop) { IsBackground = true, Name = "RigControl cooler screen" };
            _lcdThread.Start();
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- background loop

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.Error("Cooling update failed.", ex);
            }
            if (_stop.Token.WaitHandle.WaitOne(1000)) break;
        }
    }

    /// <summary>
    /// Moving pictures (a GIF background, the visualizer, a web page) need more than the one frame a second the main
    /// loop sends. This loop sends the extra frames while such a mode is live, and otherwise just waits.
    /// </summary>
    private void LcdLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var wait = 500;
            var started = Environment.TickCount64;
            try
            {
                lock (_io)
                {
                    if (_live && !_camState.Active && _lcdProblem is null && _kraken is not null)
                    {
                        var fps = _composer.FramesPerSecond(_settings.Lcd);
                        if (fps > 1)
                        {
                            UpdateLiveScreen();
                            wait = Math.Max(15, 1000 / fps - (int)(Environment.TickCount64 - started));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Cooler screen update failed.", ex);
            }
            if (_stop.Token.WaitHandle.WaitOne(wait)) break;
        }
    }

    /// <summary>One update: refresh CAM state, open devices, read status, publish. Public for tests.</summary>
    public void Tick()
    {
        CoolingStatusDto dto;
        lock (_io)
        {
            RefreshCam();
            EnsureOpen();
            var blocked = _camState.Active;
            try
            {
                if (_kraken is not null)
                {
                    if (blocked)
                    {
                        StopLive();
                        Drain(_kraken, 6);
                    }
                    else
                    {
                        UpdateKraken();
                        UpdateLiveScreen();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Kraken communication failed; will reconnect.", ex);
                CloseKraken();
            }
            try
            {
                if (_controller is not null)
                {
                    if (!blocked) InitController();
                    Drain(_controller, 6);
                    if (!blocked) RunControllerCurves();
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Fan controller communication failed; will reconnect.", ex);
                CloseController();
            }
            dto = BuildStatus();
            UpdateModuleStatus(dto);
            if (_logReadingsAt is { } at && DateTime.UtcNow >= at)
            {
                _logReadingsAt = null;
                var k = Fresh(_krakenStatusAt) ? _krakenStatus : null;
                var f = Fresh(_fansAt) ? _fans : null;
                _log.Info("After the change: "
                    + (k is null ? "Kraken gives no reading" : $"liquid {k.LiquidTempC?.ToString("0.0") ?? "?"} °C, pump {k.PumpRpm} rpm at {k.PumpDuty}%, radiator fans {k.FanRpm} rpm at {k.FanDuty}%")
                    + "; "
                    + (f is null ? "fan controller gives no reading" : string.Join(", ", f.Select((x, i) => x.Present ? $"fan {i + 1} {x.Rpm} rpm at {x.Duty}%" : null).Where(x => x is not null)))
                    + ".");
            }
        }
        _ctx?.Events.Publish("cooling.status", dto);
    }

    private void RefreshCam()
    {
        var before = _camState.Active;
        _camState = _cam.GetState();
        if (before && !_camState.Active)
        {
            _unblockedSince = DateTime.UtcNow;
            _krakenInitialised = false;
            _controllerInitialised = false;
            _log.Info("NZXT CAM stopped; RigControl can control cooling.");
        }
        else if (!before && _camState.Active)
        {
            _log.Info("NZXT CAM is running; cooling is read-only.");
        }
    }

    private bool Present(int vid, int pid)
    {
        try { return _bus.IsPresent(vid, pid); }
        catch { return false; }
    }

    private void EnsureOpen()
    {
        _krakenPresent = _kraken is not null || Present(KrakenProtocol.VendorId, KrakenProtocol.ProductId);
        _controllerPresent = _controller is not null || Present(FanControllerProtocol.VendorId, FanControllerProtocol.ProductId);
        if (_kraken is null && _krakenPresent)
        {
            _kraken = TryOpen(KrakenProtocol.VendorId, KrakenProtocol.ProductId);
            if (_kraken is null && !_krakenOpenFailureLogged)
            {
                _krakenOpenFailureLogged = true;
                _log.Info("Kraken: plugged in, but Windows would not open it" + (_camState.Active ? " (NZXT CAM holds it)." : " (another program holds it)."));
            }
            if (_kraken is not null)
            {
                _krakenOpenFailureLogged = false;
                _krakenFirstStatusLogged = false;
                _krakenSilentLogged = false;
                _log.Info($"Kraken: opened (report sizes {_kraken.MaxInputLength}/{_kraken.MaxOutputLength}).");
                _krakenWritable = _kraken.MaxInputLength == 64 && _kraken.MaxOutputLength == 64;
                _krakenInitialised = false;
                _krakenNotes.Clear();
                if (!_krakenWritable)
                {
                    _krakenNotes.Add("This cooler reports an unexpected data format, so RigControl only shows readings and will not change its speeds.");
                    _log.Warn($"Kraken report lengths are {_kraken.MaxInputLength}/{_kraken.MaxOutputLength}, expected 64/64. Writes disabled.");
                }
            }
        }
        if (_controller is null && _controllerPresent)
        {
            _controller = TryOpen(FanControllerProtocol.VendorId, FanControllerProtocol.ProductId);
            if (_controller is null && !_controllerOpenFailureLogged)
            {
                _controllerOpenFailureLogged = true;
                _log.Info("Fan controller: plugged in, but Windows would not open it" + (_camState.Active ? " (NZXT CAM holds it)." : " (another program holds it)."));
            }
            if (_controller is not null)
            {
                _controllerOpenFailureLogged = false;
                _controllerFirstStatusLogged = false;
                _log.Info($"Fan controller: opened (report sizes {_controller.MaxInputLength}/{_controller.MaxOutputLength}).");
                _controllerWritable = _controller.MaxInputLength == 64 && _controller.MaxOutputLength == 64;
                _controllerInitialised = false;
                _badControllerReports = 0;
                if (!_controllerWritable)
                    _log.Warn($"Fan controller report lengths are {_controller.MaxInputLength}/{_controller.MaxOutputLength}, expected 64/64. Writes disabled.");
            }
        }
    }

    private IHidLink? TryOpen(int vid, int pid)
    {
        try
        {
            return _bus.OpenSingle(vid, pid);
        }
        catch (Exception ex)
        {
            _log.Warn($"Opening device {vid:X4}:{pid:X4} failed.", ex);
            return null;
        }
    }

    private void CloseKraken()
    {
        StopLive();
        try { _kraken?.Dispose(); } catch { /* ignore */ }
        _kraken = null;
        _krakenInitialised = false;
    }

    /// <summary>Forgets live mode and lets go of the screen's data channel (so NZXT CAM can use it). Sends nothing.</summary>
    private void StopLive()
    {
        _live = false;
        _composer.Release();
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;
    }

    private void CloseController()
    {
        try { _controller?.Dispose(); } catch { /* ignore */ }
        _controller = null;
        _controllerInitialised = false;
    }

    /// <summary>The single place a packet leaves for a device. Refuses anything that is not safe to send.</summary>
    private void Send(IHidLink link, byte[] packet)
    {
        var isKraken = ReferenceEquals(link, _kraken);
        if (_camState.Active) throw RpcException.Unavailable("NZXT CAM is running, so RigControl is not changing anything on the cooler.");
        if (isKraken ? !_krakenWritable : !_controllerWritable)
            throw RpcException.Unavailable("This device reports an unexpected data format, so RigControl will not send to it.");
        var allowed = isKraken ? KrakenProtocol.IsAllowed(packet) : FanControllerProtocol.IsAllowed(packet);
        if (!allowed) throw new InvalidOperationException("Refusing to send a packet that is not on the allow-list.");
        link.Write(packet);
    }

    /// <summary>Reads whatever reports are waiting and keeps the ones we understand.</summary>
    private void Drain(IHidLink link, int max)
    {
        for (var i = 0; i < max; i++)
        {
            var report = link.Read(i == 0 ? 120 : 20);
            if (report is null) return;
            Absorb(link, report);
        }
    }

    private void Absorb(IHidLink link, byte[] report)
    {
        if (ReferenceEquals(link, _kraken))
        {
            if (KrakenProtocol.ParseStatus(report) is { } s)
            {
                _krakenStatus = s;
                _krakenStatusAt = DateTime.UtcNow;
                if (!_krakenFirstStatusLogged)
                {
                    _krakenFirstStatusLogged = true;
                    _log.Info($"Kraken: first reading: liquid {s.LiquidTempC?.ToString("0.0") ?? "?"} °C, pump {s.PumpRpm} rpm at {s.PumpDuty}%, fans {s.FanRpm} rpm at {s.FanDuty}%.");
                }
            }
            else if (KrakenProtocol.ParseFirmware(report) is { } fw) _krakenFirmware = fw;
            else if (!_live && KrakenProtocol.ParseLcdInfo(report) is { } lcd) _lcd = lcd;
            else if (!_live && report.Length >= 2 && report[0] is 0x75 or 0x11 or 0x31 && report[1] == 0x01 && _unknownReportsLogged < 6)
            {
                // A reply of a kind we ask for, but not in the layout we expect: record it so the layout can be corrected.
                _unknownReportsLogged++;
                _log.Warn("Kraken: reply not understood: " + Convert.ToHexString(report.AsSpan(0, Math.Min(report.Length, 32))));
            }
        }
        else
        {
            if (report.Length >= 2 && report[0] == 0x67 && report[1] == 0x02)
            {
                if (FanControllerProtocol.ParseStatus(report) is { } fans)
                {
                    _fans = fans;
                    _fansAt = DateTime.UtcNow;
                    _badControllerReports = 0;
                    if (!_controllerFirstStatusLogged)
                    {
                        _controllerFirstStatusLogged = true;
                        _log.Info("Fan controller: first reading: " + string.Join(", ", fans.Select((f, i) => f.Present ? $"fan {i + 1} {f.Rpm} rpm at {f.Duty}%" : $"fan {i + 1} none")) + ".");
                    }
                }
                else if (++_badControllerReports >= 10 && _controllerWritable)
                {
                    _log.Warn("Fan controller: status not understood: " + Convert.ToHexString(report.AsSpan(0, Math.Min(report.Length, 48))));
                    _controllerWritable = false;
                    _log.Warn("The fan controller's status reports do not match the expected layout. Writes disabled.");
                }
            }
            else if (FanControllerProtocol.ParseFirmware(report) is { } fw) _controllerFirmware = fw;
        }
    }

    /// <summary>Sends a request and reads until a report satisfying <paramref name="got"/> arrives (other reports are still absorbed).</summary>
    private bool Request(IHidLink link, byte[] packet, Func<bool> got, int attempts = 12, int timeoutMs = 400)
    {
        Send(link, packet);
        for (var i = 0; i < attempts; i++)
        {
            var report = link.Read(timeoutMs);
            if (report is null) return got();
            Absorb(link, report);
            if (got()) return true;
        }
        return got();
    }

    private void UpdateKraken()
    {
        var link = _kraken!;
        if (!_krakenWritable)
        {
            Drain(link, 6);
            return;
        }
        // After CAM stops, give it a moment to let go of the device before we start talking.
        if (DateTime.UtcNow - _unblockedSince < TimeSpan.FromSeconds(2)) return;

        if (!_krakenInitialised)
        {
            _krakenFirmware = null;
            Request(link, KrakenProtocol.GetFirmware(), () => _krakenFirmware is not null);
            _lcd = null;
            Request(link, KrakenProtocol.GetLcdInfo(), () => _lcd is not null);
            // The cooler forgets its curve when it loses power, so put back what the user last chose here.
            foreach (var (channel, setting) in _settings.Kraken)
            {
                if (TryParseKrakenChannel(channel, out var ch))
                {
                    Send(link, KrakenProtocol.SetSpeedProfile(ch, DutiesFor(ch, setting)));
                    _log.Info($"Kraken {channel}: re-applied saved {setting.Mode} setting.");
                }
            }
            _krakenInitialised = true;
            _log.Info($"Kraken: firmware {_krakenFirmware ?? "did not answer"}; screen " +
                      (_lcd is null ? "did not answer" : $"brightness {_lcd.BrightnessPercent}%, rotation {_lcd.OrientationDegrees}°") + ".");
        }

        var before = _krakenStatusAt;
        var answered = Request(link, KrakenProtocol.GetStatus(), () => _krakenStatusAt != before, attempts: 12, timeoutMs: 300);
        if (!answered && !_krakenSilentLogged && DateTime.UtcNow - _krakenStatusAt > TimeSpan.FromSeconds(10))
        {
            _krakenSilentLogged = true;
            _log.Warn("Kraken: no readings are coming back when asked.");
        }
        else if (answered) _krakenSilentLogged = false;
    }

    private void InitController()
    {
        if (_controllerInitialised || !_controllerWritable || _controller is null) return;
        if (DateTime.UtcNow - _unblockedSince < TimeSpan.FromSeconds(2)) return;
        var link = _controller;
        Send(link, FanControllerProtocol.DetectFans());
        Send(link, FanControllerProtocol.SetIntervalOneSecond());
        Request(link, FanControllerProtocol.GetFirmware(), () => _controllerFirmware is not null, attempts: 8, timeoutMs: 200);
        Drain(link, 8);
        // This controller keeps a duty only while powered, so put back what the user last chose here.
        foreach (var (channel, duty) in _settings.ControllerDuty)
        {
            if (!TryParseControllerChannel(channel, out var index)) continue;
            if (_fans is not null && !_fans[index].Present) continue;
            Send(link, FanControllerProtocol.SetDuty(index, Math.Clamp(duty, FanControllerProtocol.MinDuty, 100)));
        }
        _controllerInitialised = true;
        Array.Fill(_controllerSent, -1);
    }

    /// <summary>What a curve-driven case fan should run at right now.</summary>
    private int ControllerCurveTarget(List<CurvePoint> points)
    {
        var liquid = Fresh(_krakenStatusAt) ? _krakenStatus?.LiquidTempC : null;
        if (liquid is null) return ControllerFallbackDuty;
        var curve = FanCurve.Create(points, FanControllerProtocol.MinDuty, out _);
        return curve is null ? ControllerFallbackDuty : Math.Clamp(curve.Evaluate(liquid.Value), FanControllerProtocol.MinDuty, 100);
    }

    /// <summary>
    /// Keeps the case fans where the user wants them: curve fans follow the liquid temperature (at most one change
    /// every two seconds), and any fan the controller reports at a different speed than was asked for is set again.
    /// The controller re-detects its fans when it is started up and can drop a speed sent during that, so asking once is not enough.
    /// </summary>
    private void RunControllerCurves()
    {
        if (_controller is null || !_controllerInitialised || !_controllerWritable) return;
        if (_settings.ControllerCurves.Count == 0 && _settings.ControllerDuty.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now - _controllerCurveAt < TimeSpan.FromSeconds(2)) return;
        _controllerCurveAt = now;
        var fans = _fans is not null && Fresh(_fansAt) ? _fans : null;
        for (var index = 0; index < FanControllerProtocol.ChannelCount; index++)
        {
            var channel = "fan" + (index + 1);
            int target;
            var isCurve = _settings.ControllerCurves.TryGetValue(channel, out var points);
            if (isCurve) target = ControllerCurveTarget(points!);
            else if (_settings.ControllerDuty.TryGetValue(channel, out var duty)) target = Math.Clamp(duty, FanControllerProtocol.MinDuty, 100);
            else continue;
            if (fans is not null && !fans[index].Present) continue;

            var sent = _controllerSent[index];
            // Small wobbles in temperature should not make curve fans hunt up and down.
            var changed = sent < 0 || (isCurve ? Math.Abs(target - sent) >= 2 : target != sent);
            // What the controller reports has had time to catch up, and it is not what was asked for.
            var ignored = !changed && fans is not null && now - _controllerSentAt[index] > TimeSpan.FromSeconds(5) && Math.Abs(fans[index].Duty - sent) > 2;
            if (!changed && !ignored) continue;
            if (ignored)
            {
                if (_controllerResends[index] >= 6) continue; // it will not take it; do not keep hammering
                _controllerResends[index]++;
                _log.Warn($"Fan controller {channel}: reports {fans![index].Duty}% but {sent}% was asked for; sending it again"
                          + (_controllerResends[index] >= 6 ? " (last try)." : "."));
            }
            else
            {
                _controllerResends[index] = 0;
            }
            Send(_controller, FanControllerProtocol.SetDuty(index, target));
            _controllerSent[index] = target;
            _controllerSentAt[index] = now;
        }
    }

    // ---------------------------------------------------------------- status

    private static bool Fresh(DateTime at) => DateTime.UtcNow - at < TimeSpan.FromSeconds(6);

    private CoolingStatusDto BuildStatus()
    {
        var devices = new List<CoolingDeviceDto>();
        var blocked = _camState.Active;

        if (_krakenPresent)
        {
            var open = _kraken is not null;
            var s = open && Fresh(_krakenStatusAt) ? _krakenStatus : null;
            var notes = new List<string>(_krakenNotes);
            if (!open)
                notes.Add(blocked
                    ? "NZXT CAM has this cooler to itself, so its readings show here once RigControl takes over."
                    : "Another program has this cooler open, so RigControl cannot read or control it.");
            if (s is { SensorFault: true }) notes.Add("The cooler reports a temperature sensor fault. Check that its SATA power cable is connected.");
            if (s is { PumpRpm: 0 } && !blocked) notes.Add("The pump reports 0 rpm. Check the cooler's power cable.");
            if (open && !blocked && _krakenWritable && _settings.Kraken.Count == 0)
                notes.Add("RigControl has not set this cooler yet. It is running whatever was last sent to it; apply a curve so it reacts to temperature.");
            var range = new CurveRange(KrakenProtocol.CurveMinTemp, KrakenProtocol.CurveMaxTemp);
            devices.Add(new CoolingDeviceDto(
                KrakenId, "Kraken Elite", "NZXT Kraken Elite", open ? _krakenFirmware : null, open || blocked, s?.LiquidTempC,
                new[]
                {
                    KrakenChannel("pump", "Pump", "pump", s?.PumpRpm, s?.PumpDuty, KrakenProtocol.PumpMinDuty, range),
                    KrakenChannel("fan", "Radiator fans", "fan", s?.FanRpm, s?.FanDuty, KrakenFanMinDuty, range),
                },
                !open ? null
                    : _live ? new LcdDto(_settings.Lcd.Brightness, _settings.Lcd.Rotation)
                    : _lcd is null ? null : new LcdDto(_lcd.BrightnessPercent, _lcd.OrientationDegrees),
                notes,
                open ? ScreenDto() : null));
        }

        if (_controllerPresent)
        {
            var open = _controller is not null;
            var fans = open && Fresh(_fansAt) ? _fans : null;
            var channels = new List<CoolingChannelDto>();
            for (var i = 0; i < FanControllerProtocol.ChannelCount; i++)
            {
                var f = fans?[i];
                if (f is { Present: false }) continue;
                var id = "fan" + (i + 1);
                var hasSaved = _settings.ControllerDuty.TryGetValue(id, out var saved);
                var hasCurve = _settings.ControllerCurves.TryGetValue(id, out var curvePoints);
                channels.Add(new CoolingChannelDto(id, "Fan " + (i + 1), "fan", f?.Rpm, f?.Duty, FanControllerProtocol.MinDuty,
                    new CurveRange(KrakenProtocol.CurveMinTemp, KrakenProtocol.CurveMaxTemp), MaxUserCurvePoints, "liquid", false,
                    hasCurve ? "curve" : hasSaved ? "fixed" : "unknown", hasCurve ? null : hasSaved ? saved : null, hasCurve ? curvePoints : null));
            }
            var notes = new List<string>
            {
                "A fixed speed stays set even when RigControl is closed. A curve follows the cooler's liquid temperature and is run by RigControl, "
                + $"so it needs RigControl open; when RigControl closes, those fans are left at {ControllerExitDuty}% or faster.",
            };
            if (!open)
                notes.Add(blocked
                    ? "NZXT CAM has this controller to itself, so its readings show here once RigControl takes over."
                    : "Another program has this controller open, so RigControl cannot read or control it.");
            else if (!_controllerWritable) notes.Add("This controller reports an unexpected data format, so RigControl only shows readings.");
            devices.Add(new CoolingDeviceDto(ControllerId, "RGB & Fan Controller", "NZXT RGB & Fan Controller", open ? _controllerFirmware : null,
                open || blocked, null, channels, null, notes));
        }

        return new CoolingStatusDto(
            blocked ? new[] { "NZXT CAM" } : Array.Empty<string>(),
            devices,
            new CamDto(_camState.AppRunning, _camState.ServiceRunning, _camState.StartsWithWindows));
    }

    private CoolingChannelDto KrakenChannel(string id, string name, string kind, int? rpm, int? duty, int min, CurveRange range)
    {
        _settings.Kraken.TryGetValue(id, out var saved);
        var mode = saved is null ? "unknown" : saved.Mode == "curve" ? "curve" : "fixed";
        return new CoolingChannelDto(id, name, kind, rpm, duty, min, range, MaxUserCurvePoints, "liquid", true, mode,
            saved is { Mode: "fixed" } ? saved.Fixed : null,
            saved is { Mode: "curve" } ? saved.Points : null);
    }

    private void UpdateModuleStatus(CoolingStatusDto dto)
    {
        if (dto.Devices.Count == 0)
            _status = new ModuleStatus(ModuleState.Degraded, "No NZXT cooler or fan controller was found.");
        else if (dto.BlockedBy.Count > 0)
            _status = new ModuleStatus(ModuleState.Degraded, "NZXT CAM is running, so cooling is read-only.");
        else
            _status = new ModuleStatus(ModuleState.Ready);
    }

    // ---------------------------------------------------------------- RPC

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getDevices" => GetDevices(),
            "rescan" => Rescan(),
            "setFixed" => SetFixed(RpcArgs.Require<SetFixedArgs>(args)),
            "setCurve" => SetCurve(RpcArgs.Require<SetCurveArgs>(args)),
            "setLcd" => SetLcd(RpcArgs.Require<SetLcdArgs>(args)),
            "setLcdScreen" => SetLcdScreen(RpcArgs.Require<SetLcdScreenArgs>(args)),
            "getLcdPreview" => GetLcdPreview(RpcArgs.Optional<SetLcdScreenArgs>(args)),
            "closeCam" => CloseCam(RpcArgs.Optional<CloseCamArgs>(args)),
            "setCamAutoStart" => SetCamAutoStart(RpcArgs.Require<CamAutoStartArgs>(args)),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult<object?>(result);
    }

    private CoolingStatusDto GetDevices()
    {
        lock (_io) return BuildStatus();
    }

    private CoolingStatusDto Rescan()
    {
        lock (_io)
        {
            CloseKraken();
            CloseController();
        }
        Tick();
        return GetDevices();
    }

    private static bool TryParseKrakenChannel(string? id, out KrakenProtocol.Channel channel)
    {
        channel = KrakenProtocol.Channel.Pump;
        if (id == "pump") return true;
        if (id == "fan")
        {
            channel = KrakenProtocol.Channel.Fan;
            return true;
        }
        return false;
    }

    private static bool TryParseControllerChannel(string? id, out int index)
    {
        index = id switch { "fan1" => 0, "fan2" => 1, "fan3" => 2, _ => -1 };
        return index >= 0;
    }

    private static int MinDuty(KrakenProtocol.Channel ch) => ch == KrakenProtocol.Channel.Pump ? KrakenProtocol.PumpMinDuty : KrakenFanMinDuty;

    private static byte[] DutiesFor(KrakenProtocol.Channel ch, ChannelSetting s)
    {
        var min = MinDuty(ch);
        return s.Mode == "curve" && s.Points is { Count: >= 2 }
            ? KrakenProtocol.BuildDuties(s.Points.Select(p => (p.TempC, p.DutyPercent)), min)
            : KrakenProtocol.BuildFixedDuties(Math.Clamp(s.Fixed, min, 100), min);
    }

    private void RequireUnblocked()
    {
        RefreshCam();
        if (_camState.Active)
            throw RpcException.Unavailable("NZXT CAM is running. Close it first so two programs don't both control your cooler.");
    }

    private CoolingDeviceDto DeviceDto(string id)
        => BuildStatus().Devices.FirstOrDefault(d => d.Id == id) ?? throw RpcException.Unavailable("That device is not connected.");

    private CoolingDeviceDto SetFixed(SetFixedArgs a)
    {
        lock (_io)
        {
            RequireUnblocked();
            if (a.DeviceId == KrakenId)
            {
                if (!TryParseKrakenChannel(a.Channel, out var ch)) throw RpcException.InvalidArgs("Unknown channel.");
                var min = MinDuty(ch);
                if (a.DutyPercent < min || a.DutyPercent > 100)
                    throw RpcException.Unsafe($"{(ch == KrakenProtocol.Channel.Pump ? "The pump" : "These fans")} must stay between {min}% and 100%.");
                var setting = new ChannelSetting { Mode = "fixed", Fixed = a.DutyPercent };
                ApplyKraken(ch, a.Channel!, setting);
                return DeviceDto(KrakenId);
            }
            if (a.DeviceId == ControllerId)
            {
                if (!TryParseControllerChannel(a.Channel, out var index)) throw RpcException.InvalidArgs("Unknown channel.");
                if (a.DutyPercent < FanControllerProtocol.MinDuty || a.DutyPercent > 100)
                    throw RpcException.Unsafe($"These fans must stay between {FanControllerProtocol.MinDuty}% and 100%.");
                var link = _controller ?? throw RpcException.Unavailable("The fan controller is not connected.");
                if (_fans is not null && Fresh(_fansAt) && !_fans[index].Present)
                    throw RpcException.Unavailable("No fan is detected on that channel.");
                try
                {
                    InitController();
                    Send(link, FanControllerProtocol.SetDuty(index, a.DutyPercent));
                }
                catch (Exception ex) when (ex is not RpcException)
                {
                    CloseController();
                    throw RpcException.Unavailable("The fan controller did not accept the command: " + ex.Message);
                }
                _settings.ControllerDuty[a.Channel!] = a.DutyPercent;
                _settings.ControllerCurves.Remove(a.Channel!);
                _controllerSent[index] = a.DutyPercent;
                _controllerSentAt[index] = DateTime.UtcNow;
                _controllerResends[index] = 0;
                Save();
                _log.Info($"Fan controller {a.Channel}: fixed at {a.DutyPercent}%.");
                return DeviceDto(ControllerId);
            }
            throw RpcException.InvalidArgs("Unknown device.");
        }
    }

    private CoolingDeviceDto SetCurve(SetCurveArgs a)
    {
        lock (_io)
        {
            RequireUnblocked();
            if (a.DeviceId == ControllerId) return SetControllerCurve(a);
            if (a.DeviceId != KrakenId) throw RpcException.InvalidArgs("Unknown device.");
            if (!TryParseKrakenChannel(a.Channel, out var ch)) throw RpcException.InvalidArgs("Unknown channel.");
            var min = MinDuty(ch);
            if (a.Points is { Count: > MaxUserCurvePoints }) throw RpcException.InvalidArgs($"A curve can have at most {MaxUserCurvePoints} points.");
            var curve = FanCurve.Create(a.Points, min, out var error);
            if (curve is null) throw RpcException.Unsafe(error ?? "The curve is not valid.");
            if (curve.Points.Any(p => p.TempC < KrakenProtocol.CurveMinTemp || p.TempC > KrakenProtocol.CurveMaxTemp))
                throw RpcException.InvalidArgs($"Curve temperatures must be between {KrakenProtocol.CurveMinTemp} and {KrakenProtocol.CurveMaxTemp} °C ({KrakenProtocol.CurveMinTemp * 9 / 5 + 32} and {KrakenProtocol.CurveMaxTemp * 9 / 5 + 32} °F) liquid temperature.");
            ApplyKraken(ch, a.Channel!, new ChannelSetting { Mode = "curve", Points = curve.Points.ToList() });
            return DeviceDto(KrakenId);
        }
    }

    /// <summary>A case fan follows the liquid temperature from now on. RigControl sends each change of speed itself.</summary>
    private CoolingDeviceDto SetControllerCurve(SetCurveArgs a)
    {
        if (!TryParseControllerChannel(a.Channel, out var index)) throw RpcException.InvalidArgs("Unknown channel.");
        if (a.Points is { Count: > MaxUserCurvePoints }) throw RpcException.InvalidArgs($"A curve can have at most {MaxUserCurvePoints} points.");
        var curve = FanCurve.Create(a.Points, FanControllerProtocol.MinDuty, out var error);
        if (curve is null) throw RpcException.Unsafe(error ?? "The curve is not valid.");
        if (curve.Points.Any(p => p.TempC < KrakenProtocol.CurveMinTemp || p.TempC > KrakenProtocol.CurveMaxTemp))
            throw RpcException.InvalidArgs($"Curve temperatures must be between {KrakenProtocol.CurveMinTemp} and {KrakenProtocol.CurveMaxTemp} °C ({KrakenProtocol.CurveMinTemp * 9 / 5 + 32} and {KrakenProtocol.CurveMaxTemp * 9 / 5 + 32} °F) liquid temperature.");
        var link = _controller ?? throw RpcException.Unavailable("The fan controller is not connected.");
        if (_fans is not null && Fresh(_fansAt) && !_fans[index].Present) throw RpcException.Unavailable("No fan is detected on that channel.");
        var points = curve.Points.ToList();
        var target = ControllerCurveTarget(points);
        try
        {
            InitController();
            Send(link, FanControllerProtocol.SetDuty(index, target));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            CloseController();
            throw RpcException.Unavailable("The fan controller did not accept the command: " + ex.Message);
        }
        _controllerSent[index] = target;
        _controllerSentAt[index] = DateTime.UtcNow;
        _controllerResends[index] = 0;
        _settings.ControllerCurves[a.Channel!] = points;
        _settings.ControllerDuty.Remove(a.Channel!);
        Save();
        _log.Info($"Fan controller {a.Channel}: curve applied (now {target}%).");
        return DeviceDto(ControllerId);
    }

    private void ApplyKraken(KrakenProtocol.Channel ch, string channelId, ChannelSetting setting)
    {
        var link = _kraken ?? throw RpcException.Unavailable("The Kraken is not connected.");
        try
        {
            Send(link, KrakenProtocol.SetSpeedProfile(ch, DutiesFor(ch, setting)));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            CloseKraken();
            throw RpcException.Unavailable("The cooler did not accept the command: " + ex.Message);
        }
        _settings.Kraken[channelId] = setting;
        Save();
        _log.Info($"Kraken {channelId}: applied {setting.Mode} setting.");
    }

    private CoolingDeviceDto SetLcd(SetLcdArgs a)
    {
        lock (_io)
        {
            RequireUnblocked();
            if (a.DeviceId != KrakenId) throw RpcException.InvalidArgs("That device has no screen.");
            var link = _kraken ?? throw RpcException.Unavailable("The Kraken is not connected.");
            if (a.Brightness is < 0 or > 100) throw RpcException.InvalidArgs("Brightness must be between 0 and 100.");
            if (a.Orientation is not (null or 0 or 90 or 180 or 270)) throw RpcException.InvalidArgs("Rotation must be 0, 90, 180 or 270.");
            if (_live)
            {
                // In live mode RigControl draws the picture, so rotation is applied to the frames and only brightness goes to the cooler.
                if (a.Orientation is { } o) _settings.Lcd.Rotation = o;
                if (a.Brightness is { } b)
                {
                    _settings.Lcd.Brightness = b;
                    try { LcdCommand(KrakenLcdProtocol.LiveBrightness(b), 300); }
                    catch (Exception ex) when (ex is not RpcException) { throw RpcException.Unavailable("The cooler did not accept the command: " + ex.Message); }
                }
                Save();
                return DeviceDto(KrakenId);
            }
            try
            {
                // Both values travel in one command, so read the current ones first and resend the one not being changed.
                _lcd = null;
                if (!Request(link, KrakenProtocol.GetLcdInfo(), () => _lcd is not null))
                    throw RpcException.Unavailable("The cooler did not report its screen settings, so nothing was changed.");
                var wantB = a.Brightness ?? _lcd!.BrightnessPercent;
                var wantO = a.Orientation ?? _lcd!.OrientationDegrees;
                Send(link, KrakenProtocol.SetLcd(wantB, wantO));
                _lcd = null;
                Request(link, KrakenProtocol.GetLcdInfo(), () => _lcd is not null);
                if (_lcd is null || _lcd.BrightnessPercent != wantB || _lcd.OrientationDegrees != wantO)
                    throw RpcException.Unavailable("The cooler did not take the new screen setting.");
                var turned = _settings.Lcd.Rotation != wantO;
                _settings.Lcd.Rotation = wantO;
                _settings.Lcd.Brightness = wantB;
                Save();
                // A stored picture is rotated by RigControl before it is sent, so send it again the new way up.
                if (turned && _settings.Lcd.Mode == "media" && _settings.Lcd.MediaPath is { } path && File.Exists(path))
                    UploadStored(LcdRenderer.PrepareGif(path, wantO));
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                CloseKraken();
                throw RpcException.Unavailable("The cooler did not accept the command: " + ex.Message);
            }
            return DeviceDto(KrakenId);
        }
    }

    // ---------------------------------------------------------------- screen (pictures, GIFs, infographics)

    private LcdScreenDto ScreenDto()
    {
        var l = _settings.Lcd;
        return new LcdScreenDto(l.Mode, l.Layout, l.Primary, l.Secondary, l.Accent,
            string.IsNullOrEmpty(l.MediaPath) ? null : Path.GetFileName(l.MediaPath), _live, _lcdProblem,
            LcdRenderer.Layouts, LcdRenderer.Sources, l.Tertiary,
            string.IsNullOrEmpty(l.Background) ? null : Path.GetFileName(l.Background), l.WebUrl, ScreenModes, l.TextFollowsLights);
    }

    private static readonly string[] ScreenModes = { "liquid", "infographic", "media", "clock", "visualizer", "web" };

    private LcdMetrics CurrentMetrics()
    {
        var liquid = Fresh(_krakenStatusAt) ? _krakenStatus?.LiquidTempC : null;
        return _metrics with { LiquidTempC = liquid, Fahrenheit = _fahrenheit };
    }

    /// <summary>The single place a screen command leaves for the cooler.</summary>
    private void SendLcd(byte[] packet)
    {
        var link = _kraken ?? throw RpcException.Unavailable("The Kraken is not connected.");
        if (_camState.Active) throw RpcException.Unavailable("NZXT CAM is running, so RigControl is not changing anything on the cooler.");
        if (!_krakenWritable) throw RpcException.Unavailable("This device reports an unexpected data format, so RigControl will not send to it.");
        if (!KrakenLcdProtocol.IsAllowed(packet) && !KrakenProtocol.IsAllowed(packet))
            throw new InvalidOperationException("Refusing to send a packet that is not on the allow-list.");
        link.Write(packet);
    }

    /// <summary>Sends a screen command and waits for its own reply (status reports that arrive in between are kept). Null = no reply.</summary>
    private byte[]? LcdCommand(byte[] packet, int timeoutMs)
    {
        SendLcd(packet);
        var link = _kraken!;
        for (var i = 0; i < 24; i++)
        {
            var report = link.Read(timeoutMs);
            if (report is null) return null;
            Absorb(link, report);
            if (KrakenLcdProtocol.IsReplyTo(packet, report)) return report;
        }
        return null;
    }

    private ILcdPipe OpenPipe()
        => _pipe ??= _pipes.Open()
           ?? throw RpcException.Unavailable("Windows would not open the cooler's screen. Another program (such as NZXT CAM) may still be using it.");

    private void WritePipe(byte[] data, int chunk)
    {
        var pipe = OpenPipe();
        for (var offset = 0; offset < data.Length; offset += chunk)
            pipe.Write(data, offset, Math.Min(chunk, data.Length - offset));
    }

    /// <summary>Puts the screen into the mode where it shows whatever frames it is sent (what NZXT CAM does for infographics).</summary>
    private void EnterLive()
    {
        OpenPipe();
        _live = true;
        foreach (var packet in KrakenLcdProtocol.EnterLiveMode(_settings.Lcd.Brightness)) LcdCommand(packet, 200);
        Drain(_kraken!, 8);
    }

    private void PushFrame()
    {
        var l = _settings.Lcd;
        byte[] frame;
        using (var picture = _composer.Compose(l, CurrentMetrics(), DateTime.Now))
            frame = LcdRenderer.ToLiveFrame(picture, l.Rotation);
        // Wait for each acknowledgement so picture data never arrives before the cooler expects it.
        if (LcdCommand(KrakenLcdProtocol.StartLiveFrame(), 300) is null) throw new IOException("The screen did not answer when a picture was announced.");
        WritePipe(KrakenLcdProtocol.BulkHeader(KrakenLcdProtocol.AssetLiveFrame, frame.Length), 64);
        WritePipe(frame, KrakenLcdProtocol.StreamChunk);
        if (LcdCommand(KrakenLcdProtocol.EndTransfer(), 2000) is null) throw new IOException("The screen did not confirm the picture.");
    }

    /// <summary>Once per tick while the infographic is chosen: (re)enter live mode if needed and send a fresh picture.</summary>
    private void UpdateLiveScreen()
    {
        if (!LcdComposer.IsLive(_settings.Lcd.Mode) || _lcdProblem is not null || !_krakenInitialised || !_krakenWritable || _kraken is null) return;
        try
        {
            if (!_live) EnterLive();
            PushFrame();
            _liveFailures = 0;
        }
        catch (Exception ex)
        {
            StopLive();
            _liveFailures++;
            _log.Warn($"Sending a picture to the cooler's screen failed ({_liveFailures}).", ex);
            if (ex is RpcException || _liveFailures >= 3)
                _lcdProblem = "The screen stopped accepting pictures: " + ex.Message + " Apply the screen setting again to retry.";
        }
    }

    /// <summary>Stores a GIF in a free slot of the cooler and switches the screen to it (same steps as liquidctl).</summary>
    private void UploadStored(byte[] gif)
    {
        OpenPipe();
        LcdCommand(KrakenLcdProtocol.PrepareTransfer(), 300);
        var buckets = new List<KrakenLcdProtocol.BucketInfo>();
        var answered = 0;
        for (var i = 0; i < KrakenLcdProtocol.BucketCount; i++)
        {
            var reply = LcdCommand(KrakenLcdProtocol.QueryBucket(i), 300);
            if (reply is not null) answered++;
            buckets.Add(KrakenLcdProtocol.BucketInfo.Parse(reply ?? Array.Empty<byte>()));
        }
        if (answered == 0) throw RpcException.Unavailable("The cooler did not answer questions about its screen storage, so nothing was sent.");

        var free = buckets.FindIndex(b => !b.Occupied);
        var index = free >= 0 ? free : 0;
        var filled = free < 0;
        // A slot is ready once deleting it reports success; a slot that held something is deleted twice.
        while (true)
        {
            if (index >= KrakenLcdProtocol.BucketCount) throw RpcException.Unavailable("The cooler has no usable screen storage slot.");
            var deleted = LcdCommand(KrakenLcdProtocol.DeleteBucket(index), 500);
            if (deleted is null || deleted[14] != 0x01)
            {
                index++;
                filled = true;
                continue;
            }
            if (!filled) break;
            filled = false;
        }

        var sizeKb = (int)Math.Ceiling((20 + gif.Length) / 1024.0);
        var offsetKb = KrakenLcdProtocol.PlanOffset(buckets, index, sizeKb);
        if (offsetKb < 0)
        {
            LcdCommand(KrakenLcdProtocol.ShowLiquid(), 300);
            for (var i = 0; i < KrakenLcdProtocol.BucketCount; i++) LcdCommand(KrakenLcdProtocol.DeleteBucket(i), 500);
            index = 0;
            offsetKb = 0;
        }
        if (LcdCommand(KrakenLcdProtocol.SetupBucket(index, offsetKb, sizeKb), 500) is null)
            throw RpcException.Unavailable("The cooler did not make room for the picture, so nothing was sent.");
        if (LcdCommand(KrakenLcdProtocol.StartBucketTransfer(index), 500) is null)
            throw RpcException.Unavailable("The cooler did not get ready to receive the picture, so nothing was sent.");
        WritePipe(KrakenLcdProtocol.BulkHeader(KrakenLcdProtocol.AssetGif, gif.Length), 64);
        WritePipe(gif, KrakenLcdProtocol.UploadChunk);
        SendLcd(KrakenLcdProtocol.EndTransfer());
        if (LcdCommand(KrakenLcdProtocol.ShowBucket(index), 2000) is null)
            throw RpcException.Unavailable("The picture was sent, but the cooler did not confirm switching to it.");
        _log.Info($"Screen: stored {gif.Length / 1024} KB in slot {index} and switched to it.");
    }

    private static string Pick(string? wanted, string[] allowed, string current)
        => wanted is null ? current : allowed.Contains(wanted) ? wanted : throw RpcException.InvalidArgs("Unknown choice: " + wanted);

    private static string PickAccent(string? wanted, string current)
    {
        if (wanted is null) return current;
        if (wanted.Length == 7 && wanted[0] == '#' && wanted.Skip(1).All(Uri.IsHexDigit)) return wanted;
        throw RpcException.InvalidArgs("The colour must look like #8B5CF6.");
    }

    /// <summary>User-triggered: choose what the cooler's screen shows.</summary>
    private CoolingDeviceDto SetLcdScreen(SetLcdScreenArgs a)
    {
        lock (_io)
        {
            RequireUnblocked();
            if (a.DeviceId != KrakenId) throw RpcException.InvalidArgs("That device has no screen.");
            if (_kraken is null) throw RpcException.Unavailable("The Kraken is not connected.");
            var l = _settings.Lcd;
            var mode = a.Mode ?? l.Mode;
            if (!ScreenModes.Contains(mode)) throw RpcException.InvalidArgs("Unknown screen mode.");
            var layout = Pick(a.Layout, LcdRenderer.Layouts, l.Layout);
            var primary = Pick(a.Primary, LcdRenderer.Sources, l.Primary);
            var secondary = Pick(a.Secondary, LcdRenderer.Sources, l.Secondary);
            var tertiary = Pick(a.Tertiary, LcdRenderer.Sources, l.Tertiary);
            var accent = PickAccent(a.Accent, l.Accent);
            if (a.TextFollowsLights is { } follow) l.TextFollowsLights = follow;
            // "" removes the background; leaving it out keeps the current one.
            var background = a.Background is null ? l.Background : a.Background.Length == 0 ? null : a.Background;
            if (background is not null && mode is "infographic" or "clock" && !File.Exists(background))
                throw RpcException.InvalidArgs("The background picture could not be found.");
            var url = a.Url ?? l.WebUrl;
            if (mode == "web" && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
                throw RpcException.InvalidArgs("Enter a full web address starting with http:// or https://.");
            var path = a.Path ?? l.MediaPath;
            if (mode == "media" && (string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
                throw RpcException.InvalidArgs("Choose a picture or GIF first.");

            // While the cooler still runs its own screen, what it reports is the truth about brightness and rotation.
            if (!_live && _lcd is not null && l.Mode is "unset" or "liquid")
            {
                l.Rotation = _lcd.OrientationDegrees;
                l.Brightness = _lcd.BrightnessPercent;
            }
            _lcdProblem = null;
            _liveFailures = 0;
            try
            {
                switch (mode)
                {
                    case "infographic" or "clock" or "visualizer" or "web":
                        // These are drawn by RigControl and sent as live pictures; what differs is only what is drawn.
                        var previous = l.Mode;
                        (l.Layout, l.Primary, l.Secondary, l.Tertiary, l.Accent, l.Background, l.WebUrl) = (layout, primary, secondary, tertiary, accent, background, url);
                        l.Mode = mode;
                        try
                        {
                            if (!_live) EnterLive();
                            PushFrame();
                        }
                        catch
                        {
                            l.Mode = previous;
                            throw;
                        }
                        break;
                    case "media":
                        byte[] gif;
                        try
                        {
                            gif = LcdRenderer.PrepareGif(path!, l.Rotation);
                        }
                        catch (InvalidOperationException ex)
                        {
                            throw RpcException.InvalidArgs(ex.Message);
                        }
                        catch (Exception ex) when (ex is not RpcException)
                        {
                            throw RpcException.InvalidArgs("That file could not be read as a picture or GIF.");
                        }
                        UploadStored(gif);
                        l.MediaPath = path;
                        StopLive();
                        break;
                    default:
                        ShowLiquidScreen();
                        break;
                }
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                StopLive();
                _log.Warn("Changing the cooler's screen failed.", ex);
                throw RpcException.Unavailable("The cooler's screen did not accept that: " + ex.Message);
            }
            catch (RpcException)
            {
                if (mode != "liquid") StopLive();
                throw;
            }
            l.Mode = mode;
            Save();
            _log.Info($"Screen: now showing {mode}" + (mode == "infographic" ? $" ({l.Layout}: {l.Primary}{(l.Layout != "single" ? " + " + l.Secondary : "")}{(l.Layout == "triple" ? " + " + l.Tertiary : "")})." : "."));
            return DeviceDto(KrakenId);
        }
    }

    /// <summary>Back to the cooler's own liquid-temperature screen, the right way up.</summary>
    private void ShowLiquidScreen()
    {
        var wasLive = _live;
        LcdCommand(KrakenLcdProtocol.ShowLiquid(), 500);
        StopLive();
        // Live mode leaves the cooler's own rotation setting changed; put back what the user had.
        if (wasLive) SendLcd(KrakenProtocol.SetLcd(Math.Clamp(_settings.Lcd.Brightness, 0, 100), _settings.Lcd.Rotation));
    }

    /// <summary>Draws an infographic with the current readings for the UI. Nothing is sent to the cooler.</summary>
    private LcdPreviewDto GetLcdPreview(SetLcdScreenArgs? a)
    {
        LcdSettings l;
        LcdMetrics metrics;
        lock (_io)
        {
            l = _settings.Lcd;
            metrics = CurrentMetrics();
        }
        var mode = a?.Mode ?? l.Mode;
        if (!LcdComposer.IsLive(mode)) mode = "infographic";
        var draft = new LcdSettings
        {
            Mode = mode,
            Layout = Pick(a?.Layout, LcdRenderer.Layouts, l.Layout),
            Primary = Pick(a?.Primary, LcdRenderer.Sources, l.Primary),
            Secondary = Pick(a?.Secondary, LcdRenderer.Sources, l.Secondary),
            Tertiary = Pick(a?.Tertiary, LcdRenderer.Sources, l.Tertiary),
            Accent = PickAccent(a?.Accent, l.Accent),
            Background = a?.Background is null ? l.Background : a.Background.Length == 0 ? null : a.Background,
            WebUrl = a?.Url ?? l.WebUrl,
        };
        lock (_previewComposer)
        {
            // The preview never opens a web page or listens to sound itself; a page is shown only once it is live on the cooler.
            _previewComposer.WebFrames = url => url is not null && _live && l.Mode == "web" && url == l.WebUrl ? _composer.WebFrames?.Invoke(url) : null;
            using var picture = _previewComposer.Compose(draft, metrics, DateTime.Now);
            return new LcdPreviewDto(LcdRenderer.ToPngDataUrl(picture));
        }
    }

    /// <summary>User-triggered only: close NZXT CAM (and optionally stop it starting with Windows) so RigControl can take over.</summary>
    private CoolingStatusDto CloseCam(CloseCamArgs a)
    {
        var elevated = _ctx?.Environment.IsElevated ?? false;
        var state = _cam.GetState();
        if ((state.ServiceRunning || a.DisableAutoStart) && !elevated)
            throw RpcException.NeedsAdmin("NZXT CAM runs a background service that only an administrator can stop. Restart RigControl as administrator, then try again.");

        _log.Info("Closing NZXT CAM at the user's request" + (a.DisableAutoStart ? " and turning off its start with Windows." : "."));
        if (!_cam.CloseApp())
            throw RpcException.Unavailable("NZXT CAM could not be closed. Close it from its tray icon and try again.");
        if (state.ServiceRunning && !_cam.StopService())
            throw RpcException.Unavailable("NZXT CAM's background service could not be stopped.");
        if (a.DisableAutoStart) SetCamAutoStartCore(false);
        Tick();
        return GetDevices();
    }

    private CoolingStatusDto SetCamAutoStart(CamAutoStartArgs a)
    {
        if (!(_ctx?.Environment.IsElevated ?? false))
            throw RpcException.NeedsAdmin("Changing how NZXT CAM starts needs administrator rights. Restart RigControl as administrator, then try again.");
        SetCamAutoStartCore(a.Enabled);
        Tick();
        return GetDevices();
    }

    private void SetCamAutoStartCore(bool enabled)
    {
        try
        {
            _cam.SetStartsWithWindows(enabled);
            _log.Info("NZXT CAM start with Windows: " + (enabled ? "on" : "off") + " (user request).");
        }
        catch (Exception ex)
        {
            throw new RpcException(RpcErrorCodes.Internal, "Could not change how NZXT CAM starts: " + ex.Message, ex);
        }
    }

    private void Save()
    {
        _ctx?.Settings.Set(CoolingSettings.Section, _settings);
        _logReadingsAt = DateTime.UtcNow.AddSeconds(8);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _stop.Cancel();
        _thread?.Join(3000);
        _lcdThread?.Join(3000);
        lock (_io)
        {
            // Deliberately nothing about cooling is sent on exit: the Kraken keeps its stored curve and the controller its duty.
            // A live infographic would freeze on old numbers, so hand the screen back to the cooler's own display.
            if (_live && _kraken is not null)
            {
                try
                {
                    _camState = _cam.GetState();
                    if (!_camState.Active) ShowLiquidScreen();
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not hand the screen back to the cooler on exit.", ex);
                }
            }
            CloseKraken();
            // Curve-driven case fans would otherwise stay at whatever speed the last temperature called for.
            if (_controller is not null && _controllerInitialised && _settings.ControllerCurves.Count > 0)
            {
                try
                {
                    _camState = _cam.GetState();
                    foreach (var channel in _settings.ControllerCurves.Keys)
                    {
                        if (_camState.Active || !TryParseControllerChannel(channel, out var index)) continue;
                        if (_fans is not null && !_fans[index].Present) continue;
                        var duty = Math.Max(ControllerExitDuty, _controllerSent[index]);
                        Send(_controller, FanControllerProtocol.SetDuty(index, duty));
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not set the case fans to their closing speed.", ex);
                }
            }
            CloseController();
        }
        _composer.Dispose();
        _previewComposer.Dispose();
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    private sealed class SetFixedArgs
    {
        public string? DeviceId { get; set; }
        public string? Channel { get; set; }
        public int DutyPercent { get; set; }
    }

    private sealed class SetCurveArgs
    {
        public string? DeviceId { get; set; }
        public string? Channel { get; set; }
        public List<CurvePoint>? Points { get; set; }
    }

    private sealed class SetLcdArgs
    {
        public string? DeviceId { get; set; }
        public int? Brightness { get; set; }
        public int? Orientation { get; set; }
    }

    private sealed class SetLcdScreenArgs
    {
        public string? DeviceId { get; set; }
        public string? Mode { get; set; }
        public string? Layout { get; set; }
        public string? Primary { get; set; }
        public string? Secondary { get; set; }
        public string? Accent { get; set; }
        public string? Path { get; set; }
        public string? Tertiary { get; set; }
        public string? Background { get; set; }
        public string? Url { get; set; }
        public bool? TextFollowsLights { get; set; }
    }

    private sealed class CloseCamArgs
    {
        public bool DisableAutoStart { get; set; }
    }

    private sealed class CamAutoStartArgs
    {
        public bool Enabled { get; set; }
    }
}
