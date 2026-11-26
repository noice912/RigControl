using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Capture.Listen;

// ---------------------------------------------------------------- contract types (docs/API.md, "listen")

public sealed class ListenSettings
{
    public const string Section = "listen";
    public const int MinGain = 100, MaxGain = 600, DefaultGain = 500;
    public const int MinLatency = 10, MaxLatency = 200, DefaultLatency = 30;

    public bool Enabled { get; set; }
    /// <summary>null = automatic: the capture card's sound input.</summary>
    public string? InputId { get; set; }
    /// <summary>null = Windows' default output.</summary>
    public string? OutputId { get; set; }
    public int GainPercent { get; set; } = DefaultGain;
    public int LatencyMs { get; set; } = DefaultLatency;

    public static ListenSettings Sanitise(ListenSettings? s)
    {
        s ??= new ListenSettings();
        s.GainPercent = Math.Clamp(s.GainPercent, MinGain, MaxGain);
        s.LatencyMs = Math.Clamp(s.LatencyMs, MinLatency, MaxLatency);
        if (string.IsNullOrWhiteSpace(s.InputId)) s.InputId = null;
        if (string.IsNullOrWhiteSpace(s.OutputId)) s.OutputId = null;
        return s;
    }
}

public sealed record ListenSettingsDto(bool Enabled, string? InputId, string? OutputId, int GainPercent, int LatencyMs);

/// <summary>State: "off", "running" or "waiting" (switched on, but the input or output is missing; tried again every 2 s).</summary>
public sealed record ListenStatusDto(
    bool Enabled, string State, string? InputName, string? OutputName, float Level, string? Problem, ListenSettingsDto Settings);

public sealed record ListenDeviceDto(string Id, string Name, bool CaptureCard, bool IsDefault);

public sealed record ListenDevicesDto(IReadOnlyList<ListenDeviceDto> Inputs, IReadOnlyList<ListenDeviceDto> Outputs);

/// <summary>
/// "Capture card sound": plays a sound input (normally the capture card the console is plugged into) on an output,
/// boosted and limited, with little delay. Rules: off until the user switches it on; the sound is only ever held in
/// memory on its way to the output, never recorded, saved or sent anywhere.
/// </summary>
public sealed class ListenModule : IAppModule
{
    private const int TickMs = 100;
    private const int CheckEveryTicks = 20; // 2 s

    private readonly IListenEngine _engine;
    private readonly bool _runLoop;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private Timer? _timer;
    private int _ticking;
    private int _ticks;
    private int _disposed;

    // Guarded by _gate.
    private ListenSettings _settings = new();
    private IListenSession? _session;
    private AudioEndpoint? _input, _output;
    private string? _problem;
    private float _level;
    private string? _lastLogged;

    /// <param name="runLoop">False in tests, which call <see cref="Check"/> themselves.</param>
    public ListenModule(IListenEngine engine, bool runLoop = true)
    {
        _engine = engine;
        _runLoop = runLoop;
    }

    public string Name => "listen";
    public string DisplayName => "Capture card sound";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        lock (_gate) _settings = ListenSettings.Sanitise(context.Settings.Get<ListenSettings>(ListenSettings.Section));
        _status = new ModuleStatus(ModuleState.Ready);
        // Opening sound devices can take a moment: do the first try on the timer, not here.
        if (_runLoop) _timer = new Timer(_ => Tick(), null, 500, TickMs);
        return Task.CompletedTask;
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            if (++_ticks % CheckEveryTicks == 1) Check();
            bool running;
            lock (_gate)
            {
                running = _session is not null;
                if (running) _level = _session!.TakePeak();
            }
            if (running) Publish();
        }
        catch (Exception ex)
        {
            _log.Error("Capture card sound check failed.", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Starts, stops or restarts the path so it matches the settings and the devices there now. Public for tests.</summary>
    public void Check()
    {
        bool changed;
        lock (_gate) changed = Reconcile();
        if (changed) Publish();
    }

    /// <summary>Returns true when what the user sees changed.</summary>
    private bool Reconcile()
    {
        var before = (_session is not null, _problem, _input?.Id, _output?.Id);
        if (!_settings.Enabled)
        {
            StopSession();
            _problem = null;
            return before != (false, null, null, null);
        }

        if (_session is { Faulted: true } dead)
        {
            Note("Capture card sound stopped: " + dead.Problem);
            StopSession();
        }

        var (input, output, why) = Resolve();
        if (input is null || output is null)
        {
            StopSession();
            _problem = why;
            Note("Capture card sound waiting: " + why);
        }
        else if (_session is null || input.Id != _input?.Id || output.Id != _output?.Id)
        {
            StopSession();
            try
            {
                _session = _engine.Start(input, output, _settings.GainPercent / 100f, _settings.LatencyMs);
                _input = input;
                _output = output;
                _problem = null;
                _lastLogged = null;
                _log.Info($"Capture card sound playing: \"{input.Name}\" on \"{output.Name}\" at {_settings.GainPercent} %.");
            }
            catch (Exception ex)
            {
                _session = null;
                _problem = ex.Message + " Trying again every 2 seconds.";
                Note("Capture card sound could not start: " + ex.Message);
            }
        }
        return before != (_session is not null, _problem, _input?.Id, _output?.Id);
    }

    /// <summary>Logs a problem once, not every 2 seconds.</summary>
    private void Note(string text)
    {
        if (text == _lastLogged) return;
        _lastLogged = text;
        _log.Warn(text);
    }

    private (AudioEndpoint? Input, AudioEndpoint? Output, string? Why) Resolve()
    {
        IReadOnlyList<AudioEndpoint> inputs, outputs;
        string? defaultOut;
        try
        {
            inputs = _engine.Inputs();
            outputs = _engine.Outputs();
            defaultOut = _engine.DefaultOutputId();
        }
        catch (Exception ex)
        {
            return (null, null, "Windows' sound devices could not be read: " + ex.Message);
        }

        AudioEndpoint? input;
        if (_settings.InputId is { } inId)
        {
            input = inputs.FirstOrDefault(d => d.Id == inId);
            if (input is null) return (null, null, "The chosen input is not connected. Plug it in, or choose another one.");
        }
        else
        {
            input = CaptureCardPicker.Pick(inputs);
            if (input is null) return (null, null, "No capture card sound input was found. Plug in the capture card, or choose the input by hand.");
        }

        AudioEndpoint? output;
        if (_settings.OutputId is { } outId)
        {
            output = outputs.FirstOrDefault(d => d.Id == outId);
            if (output is null) return (input, null, "The chosen output is not connected. Plug it in, or choose another one.");
        }
        else
        {
            output = defaultOut is null ? null : outputs.FirstOrDefault(d => d.Id == defaultOut);
            if (output is null) return (input, null, "Windows has no sound output right now.");
        }
        return (input, output, null);
    }

    private void StopSession()
    {
        if (_session is null) return;
        try { _session.Dispose(); }
        catch (Exception ex) { _log.Warn("Stopping capture card sound failed.", ex); }
        _session = null;
        _input = null;
        _output = null;
        _level = 0;
    }

    private ListenStatusDto BuildStatus()
    {
        var s = _settings;
        var state = !s.Enabled ? "off" : _session is not null ? "running" : "waiting";
        return new ListenStatusDto(s.Enabled, state, _input?.Name, _output?.Name, _session is null ? 0 : _level, _problem,
            new ListenSettingsDto(s.Enabled, s.InputId, s.OutputId, s.GainPercent, s.LatencyMs));
    }

    private void Publish()
    {
        ListenStatusDto dto;
        lock (_gate) dto = BuildStatus();
        _ctx?.Events.Publish("listen.status", dto);
    }

    // ---------------------------------------------------------------- RPC

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getStatus" => GetStatus(),
            "setSettings" => SetSettings(RpcArgs.Require<SettingsArgs>(args)),
            "listDevices" => ListDevices(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult<object?>(result);
    }

    public ListenStatusDto GetStatus()
    {
        lock (_gate) return BuildStatus();
    }

    /// <summary>Each field is optional. An empty <c>inputId</c> means automatic, an empty <c>outputId</c> Windows' default.</summary>
    public ListenStatusDto SetSettings(SettingsArgs a)
    {
        lock (_gate)
        {
            var s = _settings;
            if (a.Enabled is { } on) s.Enabled = on;
            if (a.InputId is not null) s.InputId = a.InputId;
            if (a.OutputId is not null) s.OutputId = a.OutputId;
            if (a.GainPercent is { } g) s.GainPercent = g;
            var latencyChanged = a.LatencyMs is { } l && l != s.LatencyMs;
            if (a.LatencyMs is { } l2) s.LatencyMs = l2;
            ListenSettings.Sanitise(s);
            if (_session is not null) _session.Gain = s.GainPercent / 100f;
            if (latencyChanged) StopSession();
            _lastLogged = null;
            _ctx?.Settings.Set(ListenSettings.Section, s);
            if (a.Enabled == false) _log.Info("Capture card sound switched off.");
            Reconcile();
        }
        Publish();
        return GetStatus();
    }

    private ListenDevicesDto ListDevices()
    {
        try
        {
            var defaultOut = _engine.DefaultOutputId();
            return new ListenDevicesDto(
                _engine.Inputs().Select(d => new ListenDeviceDto(d.Id, d.Name, CaptureCardPicker.IsCaptureCard(d), false)).ToList(),
                _engine.Outputs().Select(d => new ListenDeviceDto(d.Id, d.Name, false, d.Id == defaultOut)).ToList());
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable("Windows' sound devices could not be read: " + ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _timer?.Dispose();
        lock (_gate)
        {
            _settings.Enabled = false; // in memory only: a late tick must not start it again
            StopSession();
        }
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    public sealed class SettingsArgs
    {
        public bool? Enabled { get; set; }
        public string? InputId { get; set; }
        public string? OutputId { get; set; }
        public int? GainPercent { get; set; }
        public int? LatencyMs { get; set; }
    }
}
