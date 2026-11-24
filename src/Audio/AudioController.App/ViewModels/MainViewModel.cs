using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AudioController.App.Services;
using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Devices;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Core.Routing;

namespace AudioController.App.ViewModels;

/// <summary>
/// The main window's state: devices, boards/slots, playback, hotkeys, engine routing and persistence.
/// All members run on the UI thread unless noted; background work (device enumeration, vault reads,
/// engine start/stop, imports, battery reads) is marshalled back with the dispatcher.
/// </summary>
/// <remarks>
/// Routing is plan-driven: every device scan or Setup change computes a <see cref="RoutingPlan"/> from the two
/// picks (microphone + headset), the "EQ everything I hear" switch and Equalizer APO's state, then
/// <see cref="ApplyRoutingAsync"/> applies it — mic engine, soundboard monitor (and its double-EQ guard), the
/// Equalizer APO file, System EQ router, the "Also play on" mirror (<see cref="MirrorRouter"/>) and (through
/// <see cref="RoutingCoordinator"/>) the Windows default output.
/// </remarks>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Guid StopAllOwnerId = new("5d2c4f0e-7a51-4c1b-9b0e-3f6a2d8e1c47");

    /// <summary>How long the app waits on exit for the Windows default output to be moved back to the headset.</summary>
    private static readonly TimeSpan ExitRestoreTimeout = TimeSpan.FromSeconds(3);

    public const string CableMissingText =
        "Virtual microphone driver not found. Install VB-Audio Virtual Cable (vb-audio.com/Cable), then click Recheck.";

    public const string FfmpegMissingText =
        "FFmpeg was not found, so importing sounds is disabled. Put ffmpeg.exe in an \"ffmpeg\" folder next to " +
        "AudioController.exe, then restart. Sounds already in your library still play.";

    private readonly IVault _vault;
    private readonly Library _library;
    private readonly ImportService? _importer;
    private readonly IDeviceManager _devices;
    private readonly IAudioEngine _engine;
    private readonly SystemEqRouter _systemEq;
    private readonly MirrorRouter _mirror;
    private readonly Dispatcher _dispatcher;
    private readonly SoundCache _cache = new(32);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusClearTimer;
    private readonly DispatcherTimer _batteryTimer;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly SemaphoreSlim _systemEqGate = new(1, 1);
    private readonly SemaphoreSlim _planGate = new(1, 1);
    private readonly SemaphoreSlim _mirrorGate = new(1, 1);
    private readonly DispatcherTimer _devicesChangedTimer;
    private readonly DispatcherTimer _mirrorApplyTimer;
    private readonly RoutingCoordinator _routing;
    private readonly HashSet<string> _reportedConflicts = new(StringComparer.OrdinalIgnoreCase);

    private RoutingPlan? _plan;
    private EqualizerApoState _apo = EqualizerApoState.NotInstalled;
    private IEqualizerApoWriter? _apoWriter;
    private string? _headsetFormatId;
    private (int SampleRate, int Channels)? _headsetFormat;
    private readonly Func<EqualizerApoState> _detectEqualizerApo;
    private readonly Func<string, IEqualizerApoWriter> _apoWriterFactory;
    private string? _windowsDefaultOutputId;
    private IReadOnlyList<DeviceOption> _micChoices = Array.Empty<DeviceOption>();
    private IReadOnlyList<DeviceOption> _headsetChoices = Array.Empty<DeviceOption>();
    private bool _rebuildingChoices;
    private bool _restoredForExit;

    private HotkeyManager? _hotkeys;
    private IUiService? _ui;
    private IReadOnlyList<AudioDeviceInfo> _deviceSnapshot = Array.Empty<AudioDeviceInfo>();
    private string? _cableInputId;
    private int _refreshVersion;
    private EngineStatus? _pendingStatus;
    private int _statusScheduled;
    private long _lastActivationRefresh;
    private string? _runningInputId;
    private string? _runningOutputId;
    private double _displayPeak;
    private bool _engineMicMuted;
    private Task _micMuteApply = Task.CompletedTask;
    private int _micMuteVersion;
    private bool _disposed;

    private BoardViewModel? _selectedBoard;
    private SlotViewModel? _selectedSlot;
    private bool _showInactiveDevices;
    private bool _hasHeadphones;
    private int _inactiveDeviceCount;
    private string? _deviceError;
    private bool _cableMissing;
    private bool _cableChecked;
    private bool _cableBannerDismissed;
    private bool _ffmpegBannerDismissed;
    private bool _engineRunning;
    private bool _soundPlaying;
    private double _peakLevel;
    private string _peakText = "−∞ dB";
    private string _stopReason = "checking devices…";
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _isImporting;
    private string? _importProgressText;
    private string? _stopAllHotkeyConflict;
    private bool _systemEqRunning;
    private string _systemEqReason = "checking devices…";
    private MirrorPlan? _mirrorPlan;
    private bool _mirrorRefreshPending;
    private MirrorRouterStatus _mirrorStatus = MirrorRouterStatus.Stopped;
    private string? _mirrorError;

    /// <param name="detectEqualizerApo">How Equalizer APO's state is read on every device scan (thread pool); defaults
    /// to <see cref="EqualizerApoDetector.Detect"/>. Must never throw.</param>
    /// <param name="apoWriterFactory">How a writer is built for a config folder; defaults to the real
    /// <see cref="EqualizerApoWriter"/>. Tests swap in a fake to count the writer calls behind the EQ UI.</param>
    /// <param name="mirror">The "Also play on" router; a new (stopped) one when null. Disposed by <see cref="Shutdown"/>.</param>
    public MainViewModel(IVault vault, Library library, IAudioDecoder? decoder, IDeviceManager devices, IAudioEngine engine,
        SystemEqRouter systemEq, Dispatcher dispatcher, Func<EqualizerApoState>? detectEqualizerApo = null,
        Func<string, IEqualizerApoWriter>? apoWriterFactory = null, MirrorRouter? mirror = null)
    {
        _detectEqualizerApo = detectEqualizerApo ?? EqualizerApoDetector.Detect;
        _apoWriterFactory = apoWriterFactory ?? (dir => new EqualizerApoWriter(dir));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _systemEq = systemEq ?? throw new ArgumentNullException(nameof(systemEq));
        _mirror = mirror ?? new MirrorRouter();
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _importer = decoder is null ? null : new ImportService(decoder, vault);

        _library.Settings ??= new AppSettings();

        // Effects panel: migrates a 1.4 library (EqGainsDb → EqBands on the default frequencies) and repairs ranges.
        FxSettings.Normalize(_library.Settings);
        _library.UserPresets ??= new List<FxPreset>();
        FxPresets.NormalizeUserPresets(_library.UserPresets);

        _library.Settings.StopAllHotkey ??= "";
        MirrorPlanner.Normalize(_library.Settings);

        Boards = new ObservableCollection<BoardViewModel>(_library.Boards.Select(b => new BoardViewModel(this, b)));
        if (Boards.Count == 0)
        {
            var board = new Board { Name = "Board 1" };
            _library.Boards.Add(board);
            Boards.Add(new BoardViewModel(this, board));
        }

        _selectedBoard = Boards[0];

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _statusClearTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(8) };
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer.Stop();
            StatusMessage = null;
        };

        _batteryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(60) };
        _batteryTimer.Tick += (_, _) => RefreshBatteries();

        // Windows fires bursts of endpoint notifications (a Bluetooth headset drops two endpoints and a default
        // change at once); coalesce them on top of the device manager's own debounce.
        _devicesChangedTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _devicesChangedTimer.Tick += (_, _) =>
        {
            _devicesChangedTimer.Stop();
            if (!_disposed) _ = RefreshDevicesAsync();
        };

        // "Also play on": slider and checkbox changes are applied ~150 ms after the last one (delay and volume change
        // live in the router, nothing is reopened for them).
        _mirrorApplyTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _mirrorApplyTimer.Tick += (_, _) =>
        {
            _mirrorApplyTimer.Stop();
            if (!_disposed) _ = EnsureMirrorAsync();
        };

        _routing = new RoutingCoordinator(devices);

        AddBoardCommand = new RelayCommand(AddBoard);
        FixMisrouteCommand = new RelayCommand(() => _ = ApplyRoutingAsync(DefaultOutputMode.Explicit));
        OpenDeviceSelectorCommand = new RelayCommand(OpenDeviceSelector, () => EqualizerApoDetector.DeviceSelectorPath(_apo) is not null);
        StopAllCommand = new RelayCommand(StopAll);
        RecheckCableCommand = new RelayCommand(() => _ = RecheckCableAsync());
        DismissCableBannerCommand = new RelayCommand(() =>
        {
            _cableBannerDismissed = true;
            OnPropertyChanged(nameof(ShowCableBanner));
        });
        DismissFfmpegBannerCommand = new RelayCommand(() =>
        {
            _ffmpegBannerDismissed = true;
            OnPropertyChanged(nameof(ShowFfmpegBanner));
        });
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync());
        DeleteSelectedSlotCommand = new RelayCommand(() =>
        {
            if (SelectedSlot is { } s) DeleteSlot(s);
        }, () => SelectedSlot is not null);
        PreviewSelectedSlotCommand = new RelayCommand(() =>
        {
            if (SelectedSlot is { } s) PreviewSlot(s);
        }, () => SelectedSlot is not null);

        _engine.StatusChanged += OnEngineStatus;
        _engine.EngineError += OnEngineError;
        _systemEq.StatusChanged += OnSystemEqStatus;
        _systemEq.RouterError += OnSystemEqError;
        _mirror.StatusChanged += OnMirrorStatus;
        _mirror.RouterError += OnMirrorError;
        _devices.DevicesChanged += OnDevicesChanged;

        Effects = new EffectsViewModel(this, _library);
    }

    /// <summary>The main window's Effects panel (power, presets, effects, graphic EQ, spectrum).</summary>
    public EffectsViewModel Effects { get; }

    // ------------------------------------------------------------------ bindable state

    public ObservableCollection<BoardViewModel> Boards { get; }

    public ObservableCollection<DeviceViewModel> Outputs { get; } = new();

    public ObservableCollection<DeviceViewModel> Inputs { get; } = new();

    public ObservableCollection<DeviceViewModel> Headphones { get; } = new();

    public AppSettings Settings => _library.Settings;

    public IReadOnlyList<string> PaletteColors => Palette.Colors;

    public IReadOnlyList<PlayModeOption> PlayModes => PlayModeOption.All;

    public ICommand AddBoardCommand { get; }

    public ICommand StopAllCommand { get; }

    public ICommand RecheckCableCommand { get; }

    public ICommand DismissCableBannerCommand { get; }

    public ICommand DismissFfmpegBannerCommand { get; }

    public ICommand RefreshDevicesCommand { get; }

    public ICommand DeleteSelectedSlotCommand { get; }

    public ICommand PreviewSelectedSlotCommand { get; }

    /// <summary>The misrouting banner's "Fix it": the Windows default goes to the inlet (routing) or the headset.</summary>
    public ICommand FixMisrouteCommand { get; }

    /// <summary>Launches Equalizer APO's Device Selector (it elevates itself) so the user can tick the headset.</summary>
    public ICommand OpenDeviceSelectorCommand { get; }

    public BoardViewModel? SelectedBoard
    {
        get => _selectedBoard;
        set => SetProperty(ref _selectedBoard, value);
    }

    public SlotViewModel? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            var old = _selectedSlot;
            if (!SetProperty(ref _selectedSlot, value)) return;
            if (old is not null)
            {
                old.IsSelected = false;
                old.NormalizeName();
            }

            if (value is not null) value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelectedSlot));
            RelayCommand.Refresh();
        }
    }

    public bool HasSelectedSlot => _selectedSlot is not null;

    public bool ShowInactiveDevices
    {
        get => _showInactiveDevices;
        set
        {
            if (SetProperty(ref _showInactiveDevices, value))
                ApplyDeviceSnapshot();
        }
    }

    public bool HasHeadphones
    {
        get => _hasHeadphones;
        private set => SetProperty(ref _hasHeadphones, value);
    }

    public int InactiveDeviceCount
    {
        get => _inactiveDeviceCount;
        private set
        {
            if (SetProperty(ref _inactiveDeviceCount, value))
                OnPropertyChanged(nameof(InactiveToggleText));
        }
    }

    public string InactiveToggleText => $"Show disabled / unplugged ({_inactiveDeviceCount})";

    public string? DeviceError
    {
        get => _deviceError;
        private set => SetProperty(ref _deviceError, value);
    }

    public bool CableMissing
    {
        get => _cableMissing;
        private set
        {
            if (SetProperty(ref _cableMissing, value))
                OnPropertyChanged(nameof(ShowCableBanner));
        }
    }

    public bool ShowCableBanner => _cableChecked && _cableMissing && !_cableBannerDismissed;

    public string CableBannerText => CableMissingText;

    public bool FfmpegMissing => _importer is null;

    public bool ShowFfmpegBanner => FfmpegMissing && !_ffmpegBannerDismissed;

    public string FfmpegBannerText => FfmpegMissingText;

    public bool CanImport => _importer is not null && !_isImporting;

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
                OnPropertyChanged(nameof(CanImport));
        }
    }

    public string? ImportProgressText
    {
        get => _importProgressText;
        private set => SetProperty(ref _importProgressText, value);
    }

    public bool EngineRunning
    {
        get => _engineRunning;
        private set
        {
            if (SetProperty(ref _engineRunning, value))
            {
                OnPropertyChanged(nameof(MicStatusText));
                OnPropertyChanged(nameof(MicStatusDetail));
                OnPropertyChanged(nameof(SetupMicStatus));
            }
        }
    }

    /// <summary>
    /// "Mic: &lt;name&gt; -> Virtual mic feed" while routing ("Mic: muted -> …" while the engine has the microphone
    /// released), otherwise "Mic: stopped". The cable is named by its plain label, whatever Windows calls it.
    /// </summary>
    public string MicStatusText => _engineRunning && _runningInputId is not null
        ? _engineMicMuted
            ? $"Mic: muted -> {OutputLabel(_runningOutputId)}"
            : $"Mic: {DeviceName(_runningInputId)} -> {OutputLabel(_runningOutputId)}"
        : "Mic: stopped";

    /// <summary>Why routing is stopped; while running, a note when the microphone is released (else empty).</summary>
    public string MicStatusDetail => _engineRunning
        ? _engineMicMuted ? "microphone released — soundboard only" : ""
        : _stopReason;

    /// <summary>
    /// The bottom bar's "Mute Mic" toggle (persisted). Muting makes the engine release the microphone
    /// completely; the soundboard keeps playing into the virtual mic.
    /// </summary>
    public bool MicMuted
    {
        get => _library.Settings.MicMuted;
        set
        {
            var s = _library.Settings;
            if (value == s.MicMuted) return;
            s.MicMuted = value;
            OnPropertyChanged();
            MarkDirty();
            ApplyMicMuted(value);
        }
    }

    public bool SoundPlaying
    {
        get => _soundPlaying;
        private set => SetProperty(ref _soundPlaying, value);
    }

    /// <summary>Meter position 0–1 (dBFS mapped from −60..0), display only.</summary>
    public double PeakLevel
    {
        get => _peakLevel;
        private set
        {
            if (SetProperty(ref _peakLevel, value))
                OnPropertyChanged(nameof(PeakRemainder));
        }
    }

    /// <summary>1 − PeakLevel, drives the meter's dark mask.</summary>
    public double PeakRemainder => 1.0 - _peakLevel;

    public string PeakText
    {
        get => _peakText;
        private set => SetProperty(ref _peakText, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>"Hear soundboard locally" (persisted): the soundboard also plays on the headset (the plan's monitor).</summary>
    public bool MonitorEnabled
    {
        get => _library.Settings.MonitorEnabled;
        set
        {
            var s = _library.Settings;
            if (value == s.MonitorEnabled) return;
            s.MonitorEnabled = value;
            if (value && _plan?.MonitorOutputId is null)
                ShowStatus("Pick your headset in Setup to hear the soundboard locally.", isError: true);

            OnPropertyChanged();
            MarkDirty();
            ApplyMonitor();
        }
    }

    public string? StopAllHotkeyConflict
    {
        get => _stopAllHotkeyConflict;
        private set => SetProperty(ref _stopAllHotkeyConflict, value);
    }

    /// <summary>True while the System EQ router is routing audio (drives the bottom bar's "SYS EQ" badge).</summary>
    public bool SystemEqRunning
    {
        get => _systemEqRunning;
        private set
        {
            if (SetProperty(ref _systemEqRunning, value))
            {
                OnPropertyChanged(nameof(SystemEqStatusText));
                OnPropertyChanged(nameof(SetupHearStatus));
            }
        }
    }

    /// <summary>"System EQ: &lt;source&gt; -> &lt;output&gt;" while running, "System EQ: off", or why it is stopped.</summary>
    public string SystemEqStatusText => _systemEqRunning && _systemEq.SourceDeviceId is { } src
        ? $"System EQ: {DeviceName(src)} -> {DeviceName(_systemEq.OutputDeviceId)}"
        : _plan is not { EqAllRequested: true, EqAllMechanism: EqAllMechanism.Router }
            ? _plan is { ApoActive: true } ? "System EQ: off (Equalizer APO does it)" : "System EQ: off"
            : $"System EQ: stopped — {_systemEqReason}";

    /// <summary>"EQ everything I hear" runs through Equalizer APO with the EQ on (drives the bottom bar's "EQ ALL" badge).</summary>
    public bool ApoEqRunning => _plan is { ApoActive: true } && FxAudible;

    /// <summary>Tooltip of the "EQ ALL" badge.</summary>
    public string ApoEqBadgeText => _plan?.HeadsetId is { } h
        ? $"EQ everything I hear: Equalizer APO equalizes all PC audio on \"{DeviceName(h)}\" (stays on when this app is closed)"
        : "EQ everything I hear: via Equalizer APO";

    // ------------------------------------------------------------------ Setup (the two picks)

    /// <summary>Microphone choices: connected real microphones (never CABLE Output), plus the saved one if it is not.</summary>
    public IReadOnlyList<DeviceOption> MicChoices
    {
        get => _micChoices;
        private set => SetProperty(ref _micChoices, value);
    }

    /// <summary>Headset choices: connected real playback devices (never a virtual cable), plus the saved one if it is not.</summary>
    public IReadOnlyList<DeviceOption> HeadsetChoices
    {
        get => _headsetChoices;
        private set => SetProperty(ref _headsetChoices, value);
    }

    /// <summary>Setup pick 1: the real microphone Discord hears (through CABLE Output).</summary>
    public string SelectedMicId
    {
        get => _library.Settings.InputDeviceId ?? "";
        set
        {
            if (_rebuildingChoices || string.IsNullOrEmpty(value)) return; // a ComboBox clearing itself
            var s = _library.Settings;
            if (SameId(value, s.InputDeviceId)) return;
            s.InputDeviceId = value;
            s.InputDeviceName = SnapshotName(value);
            OnPropertyChanged();
            MarkDirty();
            QueueRouting(DefaultOutputMode.Passive);
        }
    }

    /// <summary>
    /// Setup pick 2: the headset. Everything you hear follows it — soundboard monitor, the PC-audio route and the
    /// Windows default output — so picking it also resets the Advanced monitor / System EQ output overrides.
    /// </summary>
    public string SelectedHeadsetId
    {
        get => _library.Settings.HeadsetDeviceId ?? "";
        set
        {
            if (_rebuildingChoices || string.IsNullOrEmpty(value)) return;
            var s = _library.Settings;
            if (SameId(value, s.HeadsetDeviceId)) return;
            s.HeadsetDeviceId = value;
            s.HeadsetDeviceName = SnapshotName(value);
            s.MonitorDeviceId = value;
            s.SystemEqOutputDeviceId = value;
            OnPropertyChanged();
            MarkDirty();
            QueueRouting(DefaultOutputMode.Explicit);
        }
    }

    /// <summary>
    /// "EQ everything I hear" (persisted as <see cref="AppSettings.EqAllAudio"/>): the EQ sliders apply to all PC audio on
    /// the headset — through Equalizer APO when it is attached to the headset (nothing else changes, and it stays on
    /// when the app is closed), otherwise through the System EQ router when a spare virtual playback device exists.
    /// Shown unticked while neither is possible (the saved choice is kept for when one is).
    /// </summary>
    public bool EqAllAudio
    {
        get => CanEqAllAudio && (_library.Settings.EqAllAudio ?? _plan?.EqAllRequested ?? false);
        set
        {
            var s = _library.Settings;
            if (s.EqAllAudio == value) return;
            s.EqAllAudio = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SystemEqStatusText));
            MarkDirty();

            // Only the router path moves Windows' default output (to/from the inlet); Equalizer APO never does.
            QueueRouting(_plan?.EqAllMechanism == EqAllMechanism.Router ? DefaultOutputMode.Explicit : DefaultOutputMode.Passive);
        }
    }

    /// <summary>"EQ everything I hear" has a way to work right now (Equalizer APO on the headset, or an inlet).</summary>
    public bool CanEqAllAudio => _plan is { EqAllMechanism: not EqAllMechanism.None };

    /// <summary>How "EQ everything I hear" works ("via Equalizer APO — …"), or why it can't (with guidance).</summary>
    public string EqAllStatusText => _plan?.EqAllStatusText ?? "checking devices…";

    /// <summary>Equalizer APO is installed but not on the headset: show the "Open Device Selector" button.</summary>
    public bool ShowApoDeviceSelectorHint => _plan?.ShowApoDeviceSelectorHint == true;

    /// <summary>Equalizer APO is also on a microphone or the virtual cable (one note per line), or null.</summary>
    public string? ApoInfoText => _plan is { ApoInfoNotes.Count: > 0 } p ? string.Join("\n", p.ApoInfoNotes) : null;

    /// <summary>The microphone or the headset is not picked yet: the Setup card is highlighted.</summary>
    public bool SetupNeeded => _plan?.NeedsSetup ?? (string.IsNullOrEmpty(_library.Settings.InputDeviceId) || string.IsNullOrEmpty(_library.Settings.HeadsetDeviceId));

    public string SetupNeededText => RoutingPlanner.PickDevicesText;

    /// <summary>The Bluetooth headset's own mic is the microphone (call mode).</summary>
    public bool ShowCallModeWarning => _plan?.BluetoothCallMode == true;

    public string CallModeWarningText => RoutingPlanner.BluetoothCallModeWarning;

    /// <summary>Problems with the picks / route, one per line (null when none).</summary>
    public string? SetupWarningText => _plan is { Warnings.Count: > 0 } p ? string.Join("\n", p.Warnings) : null;

    /// <summary>The name other apps see for the virtual mic: "CABLE Output" (or whatever the user renamed it to).</summary>
    public string VirtualMicName => _plan?.VirtualMicCaptureId is { } id
        ? VirtualDeviceDetector.ShortCableName(DeviceName(id))
        : VirtualCableDetector.CableOutputName;

    /// <summary>The hint under the Setup status lines.</summary>
    public string VirtualMicHint => $"In Discord, OBS or your game, choose \"{VirtualMicName}\" as the microphone.";

    /// <summary>"Discord mic: &lt;mic&gt; → CABLE Output" (the virtual mic's current name), or why it is not.</summary>
    public string SetupMicStatus
    {
        get
        {
            if (string.IsNullOrEmpty(_library.Settings.InputDeviceId)) return "Discord mic: pick your microphone above";
            if (!_engineRunning || _runningInputId is null) return $"Discord mic: stopped — {_stopReason}";
            return _engineMicMuted
                ? $"Discord mic: muted → {VirtualMicName} (soundboard only)"
                : $"Discord mic: {DeviceName(_runningInputId)} → {VirtualMicName}";
        }
    }

    /// <summary>"You hear: &lt;headset&gt;" (plus how PC audio is equalized), or what is wrong.</summary>
    public string SetupHearStatus
    {
        get
        {
            var plan = _plan;
            if (string.IsNullOrEmpty(_library.Settings.HeadsetDeviceId)) return "You hear: pick your headset above";
            if (plan is null) return "You hear: checking devices…";
            if (plan.HeadsetMissing)
            {
                return plan.FallbackOutputId is { } fb
                    ? $"You hear: {DeviceName(fb)} — your headset isn't connected"
                    : "You hear: nothing — your headset isn't connected";
            }

            if (plan.HeadsetId is not { } headset) return "You hear: pick your headset above";
            string name = DeviceName(headset);
            if (plan.RoutingActive && _systemEqRunning && SameId(_windowsDefaultOutputId, plan.InletId))
                return $"You hear: {name} (PC audio through the app's EQ)";
            if (_windowsDefaultOutputId is { } def && !SameId(def, headset) && !SameId(def, plan.InletId))
                return $"You hear: {name} · PC audio is on \"{DeviceName(def)}\"";
            if (plan.ApoActive && FxAudible)
                return $"You hear: {name} (EQ'd by Equalizer APO)";
            return $"You hear: {name}";
        }
    }

    /// <summary>Windows' default output is the virtual-mic cable: show the red banner with "Fix it".</summary>
    public bool ShowMisrouteBanner => _plan?.WindowsDefaultIsVirtualMic == true;

    public string MisrouteBannerText => RoutingPlanner.VirtualMicDefaultWarning;

    /// <summary>The automatically chosen inlet, for Settings → Advanced ("(none)" when there is none).</summary>
    public string InletName => _plan?.InletId is { } id ? DeviceName(id) : "(none)";

    /// <summary>The virtual-mic cable's playback endpoint the app plays into (rename-proof), or null.</summary>
    internal string? CableFeedId => VirtualDeviceDetector.ChooseCableFeed(_deviceSnapshot, _library.Settings.OutputDeviceId)?.Id;

    /// <summary>The virtual-mic cable's recording endpoint (what Discord picks), or null.</summary>
    internal string? CableMicId => VirtualDeviceDetector.ChooseCableMic(_deviceSnapshot)?.Id;

    // ------------------------------------------------------------------ lifecycle

    public void AttachUi(IUiService ui) => _ui = ui;

    /// <summary>Kicks off the first device scan (which starts routing when possible) and applies EQ/monitor settings.</summary>
    public void Start()
    {
        var s = _library.Settings;
        try
        {
            var fx = FxState.From(s);
            _engine.SetFx(fx);
            _systemEq.SetFx(fx);
            _engine.SetMonitor(s.MonitorDeviceId, s.MonitorEnabled);

            // Still stopped here, so this only records the choice: a saved mute starts routing muted.
            _engine.MicMuted = s.MicMuted;
            _engineMicMuted = _engine.MicMuted;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Initial engine settings failed: " + ex.Message);
        }

        _batteryTimer.Start();
        _ = RefreshDevicesAsync();
    }

    /// <summary>Called once the main window has an HWND: registers all global hotkeys.</summary>
    public void AttachHotkeys(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        hotkeys.RegistrationsLost += OnHotkeysLost;
        RegisterAllHotkeys(report: true);
    }

    public void OnWindowActivated()
    {
        // Pick up volume/mute changes made elsewhere in Windows (endpoint notifications don't cover them).
        if (Stopwatch.GetElapsedTime(_lastActivationRefresh) < TimeSpan.FromSeconds(3)) return;
        _lastActivationRefresh = Stopwatch.GetTimestamp();
        _ = RefreshDevicesAsync();
    }

    /// <summary>Saves everything and releases hotkeys, the engine and the device manager. Idempotent.</summary>
    public void Shutdown()
    {
        if (_disposed) return;

        _saveTimer.Stop();
        _statusClearTimer.Stop();
        _batteryTimer.Stop();
        _devicesChangedTimer.Stop();
        _mirrorApplyTimer.Stop();
        _selectedSlot?.NormalizeName();
        SaveNow();

        // Before anything is disposed (engine, router, devices; the vault after us): never leave Windows playing
        // into an inlet nobody listens to once the app is gone. Runs whether or not the engine is running.
        RestoreWindowsDefaultForExit();

        // A slider move still waiting for its debounce is written now; the Equalizer APO file then stays as it is
        // (Equalizer APO keeps applying it after the app is closed — that is the point of it).
        try { _apoWriter?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }

        _disposed = true;
        _engine.StatusChanged -= OnEngineStatus;
        _engine.EngineError -= OnEngineError;
        _systemEq.StatusChanged -= OnSystemEqStatus;
        _systemEq.RouterError -= OnSystemEqError;
        _mirror.StatusChanged -= OnMirrorStatus;
        _mirror.RouterError -= OnMirrorError;
        _devices.DevicesChanged -= OnDevicesChanged;

        try { _hotkeys?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { _engine.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { _systemEq.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { _mirror.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { _devices.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        _cache.ClearAndWipe();
    }

    public void Dispose() => Shutdown();

    /// <summary>
    /// Moves Windows' default output (and communications device) back to the headset when it is still on an
    /// inlet the app put it on. Called on window close / app exit / session end / crash; blocks at most a few seconds,
    /// never throws.
    /// </summary>
    /// <param name="final">True for the real exit (idempotent; afterwards the app no longer switches the default).
    /// False for a Windows logoff/shutdown another program can still cancel: the default is restored, but if the
    /// session goes on the app keeps working and the real exit restores again.</param>
    /// <returns>What was done (diagnostics).</returns>
    public string RestoreWindowsDefaultForExit(bool final = true)
    {
        if (_restoredForExit) return "already restored";
        if (final) _restoredForExit = true;

        var plan = _plan;
        var s = _library.Settings;
        string? routerSource = null;
        try
        {
            routerSource = _systemEq.SourceDeviceId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        string result = _routing.RestoreForExit(
            new[] { plan?.InletId, s.SystemEqSourceDeviceId, routerSource },
            plan?.HeadsetId ?? s.HeadsetDeviceId,
            plan?.FallbackOutputId,
            ExitRestoreTimeout,
            final);
        Debug.WriteLine((final ? "Exit: " : "Session ending: ") + result);
        return result;
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Schedules a debounced (~1 s) save of the encrypted library.</summary>
    public void MarkDirty()
    {
        if (_disposed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Saves the library now. Returns false (and shows why) on failure.</summary>
    public bool SaveNow()
    {
        if (_disposed) return false;
        _saveTimer.Stop();
        try
        {
            _vault.SaveLibrary(_library);
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save your library: " + ex.Message, isError: true);
            return false;
        }
    }

    // ------------------------------------------------------------------ boards

    private void AddBoard()
    {
        int n = Boards.Count + 1;
        string name;
        do
        {
            name = $"Board {n++}";
        }
        while (Boards.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)));

        var board = new Board { Name = name };
        _library.Boards.Add(board);
        var vm = new BoardViewModel(this, board);
        Boards.Add(vm);
        SelectedBoard = vm;
        MarkDirty();
    }

    public void DeleteBoard(BoardViewModel board)
    {
        int count = board.Slots.Count;
        string message = count == 0
            ? $"Delete the board \"{board.Name}\"?"
            : $"Delete the board \"{board.Name}\" and its {count} sound{(count == 1 ? "" : "s")}?\n\n" +
              "Sounds not used on any other board are removed from the encrypted library. This cannot be undone.";
        if (_ui is not null && !_ui.Confirm("Delete board", message)) return;

        foreach (var slot in board.Slots)
        {
            SafeEngine(() => _engine.StopSound(slot.Id));
            _hotkeys?.Unregister(slot.Id);
        }

        if (_selectedSlot is not null && _selectedSlot.Board == board) SelectedSlot = null;

        var soundIds = board.Slots.Select(s => s.Model.SoundId).Distinct().ToList();
        int index = Boards.IndexOf(board);
        _library.Boards.Remove(board.Model);
        Boards.Remove(board);

        if (Boards.Count == 0)
        {
            var fresh = new Board { Name = "Board 1" };
            _library.Boards.Add(fresh);
            Boards.Add(new BoardViewModel(this, fresh));
        }

        SelectedBoard = Boards[Math.Clamp(index, 0, Boards.Count - 1)];

        // Persist first so the library never references a deleted sound file.
        if (SaveNow())
            DeleteUnreferencedSounds(soundIds);

        RegisterAllHotkeys(report: false);
    }

    private BoardViewModel EnsureBoard()
    {
        if (SelectedBoard is not null) return SelectedBoard;
        if (Boards.Count == 0) AddBoard();
        SelectedBoard ??= Boards[0];
        return SelectedBoard;
    }

    // ------------------------------------------------------------------ slots

    internal void OnSlotEdited(SlotViewModel slot) => MarkDirty();

    internal void OnSlotHotkeyChanged(SlotViewModel slot)
    {
        MarkDirty();
        RegisterAllHotkeys(report: true);
    }

    internal void OnSlotPlayModeChanged(SlotViewModel slot)
    {
        // Hold and non-hold hotkeys register differently (auto-repeat on/off).
        SafeEngine(() => _engine.StopSound(slot.Id));
        slot.HoldActive = false;
        MarkDirty();
        if (slot.Hotkey is not null) RegisterAllHotkeys(report: false);
    }

    public void DeleteSlot(SlotViewModel slot)
    {
        if (_ui is not null && !_ui.Confirm("Delete sound",
                $"Delete \"{slot.DisplayName}\"?\n\nThe sound is removed from the encrypted library unless another slot uses it. This cannot be undone."))
            return;

        SafeEngine(() => _engine.StopSound(slot.Id));
        _hotkeys?.Unregister(slot.Id);
        if (_selectedSlot == slot) SelectedSlot = null;

        slot.Board.RemoveSlot(slot);

        if (SaveNow())
            DeleteUnreferencedSounds(new[] { slot.Model.SoundId });

        RegisterAllHotkeys(report: false);
    }

    private void DeleteUnreferencedSounds(IEnumerable<Guid> soundIds)
    {
        var stillUsed = _library.Boards.SelectMany(b => b.Slots).Select(s => s.SoundId).ToHashSet();
        foreach (var id in soundIds)
        {
            if (id == Guid.Empty || stillUsed.Contains(id)) continue;
            _cache.Remove(id);
            try
            {
                _vault.DeleteSound(id);
            }
            catch (Exception ex)
            {
                ShowStatus("The sound was removed from the board, but its encrypted file could not be deleted: " + ex.Message, isError: true);
            }
        }
    }

    // ------------------------------------------------------------------ playback

    /// <summary>A click (or non-hold hotkey) on a slot, per its play mode.</summary>
    public void ActivateSlot(SlotViewModel slot)
    {
        switch (slot.PlayMode)
        {
            case PlayMode.Toggle:
                if (slot.IsStarting) return;
                if (_engine.IsSlotPlaying(slot.Id))
                {
                    SafeEngine(() => _engine.StopSound(slot.Id));
                    slot.IsPlaying = false;
                }
                else
                {
                    _ = PlayAsync(slot, requireHold: false);
                }

                break;

            case PlayMode.HoldToPlay:
                // A plain click on a hold slot (or the context menu) plays it once.
                _ = PlayAsync(slot, requireHold: false);
                break;

            default:
                _ = PlayAsync(slot, requireHold: false);
                break;
        }
    }

    /// <summary>The editor's Preview button: plays the sound like a click (toggle slots toggle).</summary>
    public void PreviewSlot(SlotViewModel slot)
    {
        if (slot.PlayMode == PlayMode.Toggle) ActivateSlot(slot);
        else _ = PlayAsync(slot, requireHold: false);
    }

    public void BeginHold(SlotViewModel slot)
    {
        if (slot.HoldActive) return;
        slot.HoldActive = true;
        _ = PlayAsync(slot, requireHold: true);
    }

    public void EndHold(SlotViewModel slot)
    {
        if (!slot.HoldActive) return;
        slot.HoldActive = false;
        SafeEngine(() => _engine.StopSound(slot.Id));
        slot.IsPlaying = false;
    }

    public void StopAll()
    {
        SafeEngine(_engine.StopAll);
        foreach (var slot in AllSlots())
        {
            slot.HoldActive = false;
            slot.IsPlaying = false;
        }
    }

    private async Task PlayAsync(SlotViewModel slot, bool requireHold)
    {
        if (_disposed) return;

        if (!_engine.IsRunning)
        {
            ShowStatus($"The virtual mic is stopped ({_stopReason}). Sounds play once routing is running.", isError: true);
            if (requireHold) slot.HoldActive = false;
            return;
        }

        var soundId = slot.Model.SoundId;
        if (!_cache.TryGet(soundId, out var wav))
        {
            slot.IsStarting = true;
            try
            {
                // Decrypt in memory only; never written anywhere.
                wav = await Task.Run(() => _vault.GetSound(soundId));
                _cache.Put(soundId, wav);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not load \"{slot.DisplayName}\": {DescribeSoundError(ex)}", isError: true);
                if (requireHold) slot.HoldActive = false;
                return;
            }
            finally
            {
                slot.IsStarting = false;
            }
        }

        if (_disposed || (requireHold && !slot.HoldActive)) return;

        try
        {
            _engine.PlaySound(slot.Model, wav);
            slot.IsPlaying = _engine.IsSlotPlaying(slot.Id);
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not play \"{slot.DisplayName}\": {ex.Message}", isError: true);
        }
    }

    private static string DescribeSoundError(Exception ex) => ex switch
    {
        FileNotFoundException => "its encrypted file is missing from the vault.",
        System.Security.Cryptography.CryptographicException => "its encrypted file is tampered/undecryptable, so nothing was played.",
        InvalidDataException => "its encrypted file is damaged, so nothing was played.",
        _ => ex.Message,
    };

    private IEnumerable<SlotViewModel> AllSlots() => Boards.SelectMany(b => b.Slots);

    // ------------------------------------------------------------------ import

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        if (_importer is null)
        {
            _ui?.ShowError("Import unavailable", FfmpegMissingText);
            return;
        }

        if (IsImporting)
        {
            ShowStatus("An import is already running.");
            return;
        }

        var board = EnsureBoard();
        IReadOnlyList<string> files;
        try
        {
            files = await Task.Run(() => ImportService.ExpandPaths(paths));
        }
        catch (Exception ex)
        {
            _ui?.ShowError("Import", "Could not read the dropped items: " + ex.Message);
            return;
        }

        if (files.Count == 0)
        {
            ShowStatus("No audio files were found in what was dropped.", isError: true);
            return;
        }

        IsImporting = true;
        ImportProgressText = $"Importing {files.Count} file{(files.Count == 1 ? "" : "s")}…";
        var progress = new Progress<ImportProgress>(p =>
        {
            if (p.Done < p.Total)
                ImportProgressText = $"Importing {p.Done + 1}/{p.Total}: {p.CurrentFile}";
        });

        IReadOnlyList<ImportItemResult> results;
        try
        {
            int colorStart = board.Slots.Count;
            var importer = _importer;
            results = await Task.Run(() => importer.ImportFiles(files, colorStart, progress));
        }
        catch (Exception ex)
        {
            IsImporting = false;
            ImportProgressText = null;
            _ui?.ShowError("Import failed", ex.Message);
            return;
        }

        IsImporting = false;
        ImportProgressText = null;
        if (_disposed) return;

        int ok = results.Count(r => r.Success);

        // The target board may have been deleted while the files were decoding. Adding the slots to it
        // would leave them (and their encrypted sounds) on a board that is no longer in the library.
        bool boardDeleted = !_library.Boards.Any(b => b.Id == board.Model.Id);
        if (boardDeleted)
        {
            foreach (var r in results)
            {
                if (!r.Success || r.Slot is null) continue;
                try
                {
                    _vault.DeleteSound(r.Slot.SoundId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Could not remove a sound imported into a deleted board: " + ex.Message);
                }
            }
        }
        else
        {
            SlotViewModel? last = null;
            foreach (var r in results)
            {
                if (r.Success && r.Slot is not null)
                    last = board.AddSlot(r.Slot);
            }

            if (ok > 0)
            {
                SaveNow(); // reference the new .enc files right away
                SelectedBoard = board;
                SelectedSlot = last;
            }
        }

        var lines = results
            .Select(r => new ReportLine(
                r.Success && !boardDeleted,
                r.FileName,
                !r.Success
                    ? r.Error ?? "Failed."
                    : boardDeleted
                        ? "Not added — the board was deleted during the import."
                        : $"Imported — {r.Slot!.DurationSeconds.ToString("0.000", System.Globalization.CultureInfo.CurrentCulture)} s"))
            .ToList();

        string summary = boardDeleted
            ? $"The board \"{board.Name}\" was deleted during the import, so nothing was imported. " +
              $"{ok} decoded sound{(ok == 1 ? " was" : "s were")} discarded."
            : ok == results.Count
                ? $"Imported {ok} of {results.Count} file{(results.Count == 1 ? "" : "s")} into \"{board.Name}\"."
                : $"Imported {ok} of {results.Count} files into \"{board.Name}\". {results.Count - ok} failed — nothing was trimmed or partially imported.";
        _ui?.ShowReport("Import summary", summary, lines);
    }

    // ------------------------------------------------------------------ hotkeys

    internal void OnStopAllHotkeyChanged()
    {
        MarkDirty();
        RegisterAllHotkeys(report: true);
    }

    /// <summary>
    /// (Re)registers every global hotkey: stop-all first, then every slot on every board in order.
    /// The first owner of a gesture wins; later ones are marked "(conflict)". New conflicts are reported
    /// once, in one dialog listing the losers.
    /// </summary>
    public void RegisterAllHotkeys(bool report)
    {
        var hk = _hotkeys;
        if (hk is null || _disposed) return;

        hk.UnregisterAll();
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var losers = new List<ReportLine>();

        var stopAll = _library.Settings.StopAllHotkey;
        StopAllHotkeyConflict = null;
        if (!string.IsNullOrWhiteSpace(stopAll))
        {
            var norm = HotkeyManager.Normalize(stopAll);
            if (norm is null)
            {
                StopAllHotkeyConflict = $"\"{stopAll}\" is not a valid hotkey.";
            }
            else if (!hk.TryRegister(stopAll, StopAllOwnerId, StopAll, out var error))
            {
                StopAllHotkeyConflict = error;
            }
            else
            {
                used[norm] = "Stop all";
            }

            if (StopAllHotkeyConflict is not null)
                losers.Add(new ReportLine(false, $"Stop all — {stopAll}", StopAllHotkeyConflict));
        }

        foreach (var board in Boards)
        {
            foreach (var slot in board.Slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Hotkey))
                {
                    slot.HotkeyConflict = null;
                    continue;
                }

                string? conflict;
                var norm = HotkeyManager.Normalize(slot.Hotkey);
                if (norm is null)
                {
                    conflict = $"\"{slot.Hotkey}\" is not a valid hotkey.";
                }
                else if (used.TryGetValue(norm, out var owner))
                {
                    conflict = $"Already used by \"{owner}\".";
                }
                else
                {
                    var s = slot;
                    bool ok = s.PlayMode == PlayMode.HoldToPlay
                        ? hk.TryRegister(norm, s.Id, () => BeginHold(s), () => EndHold(s), out conflict)
                        : hk.TryRegister(norm, s.Id, () => ActivateSlot(s), out conflict);
                    if (ok)
                    {
                        used[norm] = s.DisplayName;
                        conflict = null;
                    }
                }

                slot.HotkeyConflict = conflict;
                if (conflict is not null)
                    losers.Add(new ReportLine(false, $"{slot.DisplayName} ({board.Name}) — {slot.Hotkey}", conflict));
            }
        }

        if (report) ReportHotkeyConflicts(losers);
    }

    /// <summary>
    /// Hotkeys that could not be re-registered after a capture (another program took them meanwhile):
    /// mark their owners "(conflict)" and report them like any other registration conflict.
    /// </summary>
    private void OnHotkeysLost(IReadOnlyList<(Guid OwnerId, string Error)> lost)
    {
        if (_disposed) return;

        var losers = new List<ReportLine>();
        foreach (var (ownerId, error) in lost)
        {
            if (ownerId == StopAllOwnerId)
            {
                StopAllHotkeyConflict = error;
                losers.Add(new ReportLine(false, $"Stop all — {_library.Settings.StopAllHotkey}", error));
                continue;
            }

            foreach (var board in Boards)
            {
                var slot = board.Slots.FirstOrDefault(s => s.Id == ownerId);
                if (slot is null) continue;

                slot.HotkeyConflict = error;
                losers.Add(new ReportLine(false, $"{slot.DisplayName} ({board.Name}) — {slot.Hotkey}", error));
                break;
            }
        }

        ReportHotkeyConflicts(losers);
    }

    private void ReportHotkeyConflicts(List<ReportLine> losers)
    {
        if (_ui is null) return;

        var fresh = losers.Where(l => _reportedConflicts.Add(l.Title + "|" + l.Detail)).ToList();
        if (fresh.Count == 0) return;

        // Defer so the dialog never opens in the middle of a key press / registration pass.
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_disposed) return;
            _ui.ShowReport("Hotkey conflicts",
                fresh.Count == 1
                    ? "This hotkey could not be registered. It keeps its setting, marked \"(conflict)\"; change it or free it in the other program."
                    : $"These {fresh.Count} hotkeys could not be registered. They keep their setting, marked \"(conflict)\"; change them or free them in the other program.",
                fresh);
        });
    }

    // ------------------------------------------------------------------ devices

    /// <summary>Thread pool (device manager). Marshals to the UI thread and debounces into one refresh.</summary>
    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed) return;
            _devicesChangedTimer.Stop();
            _devicesChangedTimer.Start();
        });
    }

    private sealed record DeviceScan(
        IReadOnlyList<AudioDeviceInfo> Devices, bool CableInstalled, string? CableInputId, EqualizerApoState Apo, string? Error);

    public async Task RefreshDevicesAsync()
    {
        if (_disposed) return;
        int version = Interlocked.Increment(ref _refreshVersion);

        var devices = _devices;
        var detect = _detectEqualizerApo;
        string? savedOutput = _library.Settings.OutputDeviceId;
        var scan = await Task.Run(() =>
        {
            // Read-only registry check; never throws (NotInstalled on any failure).
            EqualizerApoState apo;
            try
            {
                apo = detect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Equalizer APO detection failed: " + ex.Message);
                apo = EqualizerApoState.NotInstalled;
            }

            try
            {
                var list = devices.GetDevices();
                bool cable = VirtualCableDetector.IsInstalled(new SnapshotDeviceManager(list), savedOutput, out var cableIn, out _);
                return new DeviceScan(list, cable, cableIn, apo, null);
            }
            catch (Exception ex)
            {
                return new DeviceScan(Array.Empty<AudioDeviceInfo>(), false, null, apo, ex.Message);
            }
        });

        if (_disposed || version != Volatile.Read(ref _refreshVersion)) return;

        UpdateEqualizerApo(scan.Apo);
        _deviceSnapshot = scan.Devices;
        _windowsDefaultOutputId = scan.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.IsDefault)?.Id;
        if (scan.Error is null) _routing.ObserveDevices(scan.Devices); // notices a default Windows picked by itself
        DeviceError = scan.Error is null ? null : "Could not list audio devices: " + scan.Error;
        _cableInputId = scan.CableInputId;
        _cableChecked = true;
        CableMissing = !scan.CableInstalled;
        OnPropertyChanged(nameof(ShowCableBanner));

        // One-time upgrade of pre-1.4 settings: the headset comes from the old System EQ output / monitor choice.
        if (scan.Error is null && RoutingPlanner.MigrateSettings(_library.Settings, scan.Devices, _windowsDefaultOutputId))
        {
            OnPropertyChanged(nameof(SelectedHeadsetId));
            MarkDirty();
        }

        // Re-pairing a Bluetooth device gives it new endpoint ids: follow the mic / headset by name.
        if (scan.Error is null && RoutingPlanner.ReadoptRenamedIds(_library.Settings, scan.Devices))
        {
            OnPropertyChanged(nameof(SelectedMicId));
            OnPropertyChanged(nameof(SelectedHeadsetId));
            MarkDirty();
        }

        // Devices changed: the routing pass below retries "Also play on" outputs that failed (a device came back).
        if (scan.Error is null) _mirrorRefreshPending = true;

        ApplyDeviceSnapshot();
        await ApplyRoutingAsync(DefaultOutputMode.Passive);
    }

    /// <summary>
    /// Runs a routing pass after the current input event (a ComboBox / CheckBox binding is still committing when
    /// its setter calls this, so the pass must not rebuild lists underneath it).
    /// </summary>
    private void QueueRouting(DefaultOutputMode mode)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ApplyRoutingAsync(mode));
    }

    /// <summary>
    /// The coordinator pass: computes the <see cref="RoutingPlan"/> from the settings and the latest device scan,
    /// writes its choices back into the settings, and applies it — mic engine and System EQ router (independently),
    /// the soundboard monitor, and the Windows default output. Passes run one at a time; each uses the newest
    /// snapshot. UI thread.
    /// </summary>
    /// <remarks>
    /// Order matters for the default output: when routing, the router starts before Windows is pointed at the
    /// inlet (and never if it failed to start); when not routing, Windows is moved back to the headset before the
    /// router stops, so there is no gap of silence either way.
    /// </remarks>
    public async Task ApplyRoutingAsync(DefaultOutputMode mode)
    {
        if (_disposed) return;

        await _planGate.WaitAsync();
        try
        {
            if (_disposed) return;

            var plan = MakePlan();
            _plan = plan;
            WriteBackPlan(plan);
            RefreshSetupState();

            ApplyMonitor();
            ApplyEqualizerApo();

            // The virtual mic, System EQ and the "Also play on" mirror are independent: none waits for another to
            // (re)start.
            var engine = EnsureEngineAsync();
            bool retryMirror = _mirrorRefreshPending;
            _mirrorRefreshPending = false;
            var mirror = EnsureMirrorAsync(retryMirror);
            if (plan.RoutingActive)
            {
                await EnsureSystemEqAsync();
                await ApplyWindowsDefaultAsync(plan, mode);
            }
            else
            {
                await ApplyWindowsDefaultAsync(plan, mode);
                await EnsureSystemEqAsync();
            }

            await engine;
            await mirror;
            RefreshSetupState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Routing pass failed: " + ex);
            ShowStatus("Could not apply the audio routing: " + ex.Message, isError: true);
        }
        finally
        {
            _planGate.Release();
        }
    }

    /// <summary>
    /// The plan for the current settings and the latest scan. Whether the Windows default is "automatic" (put there by
    /// this app, or picked by Windows after the previous default went away) decides if an inlet default may be moved
    /// with routing off; a default the user chose is never fought.
    /// </summary>
    private RoutingPlan MakePlan() =>
        RoutingPlanner.Plan(_library.Settings, _deviceSnapshot, _windowsDefaultOutputId, _apo,
            _routing.IsAutomaticDefault(_windowsDefaultOutputId));

    /// <summary>
    /// Keeps the older settings in step with the plan (so earlier code paths and older versions see the same
    /// routing). Only while the headset is connected: a temporary fallback is never saved as a choice.
    /// </summary>
    private void WriteBackPlan(RoutingPlan plan)
    {
        var s = _library.Settings;
        bool changed = false;

        void Set(Func<string?> get, Action<string?> set, string? value)
        {
            if (value is null || SameId(get(), value)) return;
            set(value);
            changed = true;
        }

        if (plan.HeadsetId is not null)
        {
            Set(() => s.MonitorDeviceId, v => s.MonitorDeviceId = v, plan.MonitorOutputId);
            Set(() => s.SystemEqSourceDeviceId, v => s.SystemEqSourceDeviceId = v, plan.InletId);
            if (plan.RouterOutputId is not null)
                Set(() => s.SystemEqOutputDeviceId, v => s.SystemEqOutputDeviceId = v, plan.RouterOutputId);
            else if (string.IsNullOrEmpty(s.SystemEqOutputDeviceId))
                Set(() => s.SystemEqOutputDeviceId, v => s.SystemEqOutputDeviceId = v, plan.HeadsetId);
        }

        // "EQ everything I hear" never chosen: the plan's default (on only for a pre-1.4 System EQ user; Equalizer APO
        // alone waits for the tick) becomes the saved choice as soon as it can work, so it never changes under the user.
        if (s.EqAllAudio is null && plan.EqAllMechanism != EqAllMechanism.None)
        {
            s.EqAllAudio = plan.EqAllRequested;
            changed = true;
        }

        // Older versions read SystemEqEnabled to start the router: true exactly while the router path is chosen.
        bool routerChosen = plan.EqAllRequested && plan.EqAllMechanism == EqAllMechanism.Router;
        if (s.EqAllAudio is not null && s.SystemEqEnabled != routerChosen)
        {
            s.SystemEqEnabled = routerChosen;
            changed = true;
        }

        if (changed) MarkDirty();
    }

    /// <summary>Moves Windows' default output as the plan says (see <see cref="RoutingCoordinator.ChooseChange"/>).</summary>
    private async Task ApplyWindowsDefaultAsync(RoutingPlan plan, DefaultOutputMode mode)
    {
        bool wasTripped = _routing.LoopGuardTripped;
        string? change = _routing.ChooseChange(plan, _windowsDefaultOutputId, mode);

        // Never point Windows at the inlet when the router could not start: that would be silence.
        if (plan.RoutingActive && !_systemEq.IsRunning)
        {
            change = SameId(_windowsDefaultOutputId, plan.InletId) ? plan.HeadsetId : null;
        }

        if (!wasTripped && _routing.LoopGuardTripped)
        {
            ShowStatus("Another program keeps changing the Windows default output back, so Audio Controller stopped switching it " +
                       "for now. It tries again when your devices change.", isError: true);
        }

        if (change is null || _disposed) return;

        var s = _library.Settings;
        bool comms = s.SetDefaultCommunications
                     || SameId(_deviceSnapshot.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.IsDefaultCommunications)?.Id, plan.InletId);
        string target = change;
        string? error = await Task.Run(() => _routing.TrySwitch(target, comms));
        if (_disposed) return;

        if (error is not null)
        {
            ShowStatus("Could not change the Windows default output: " + error, isError: true);
            return;
        }

        // The scan that the change notification triggers confirms it; until then show the new state (banner,
        // "through the app") without waiting. Display only: nothing else is re-applied from this plan.
        _windowsDefaultOutputId = target;
        _plan = MakePlan();
        string name = DeviceName(target);
        if (SameId(target, plan.InletId))
            ShowStatus($"PC audio now goes through Audio Controller to \"{DeviceName(plan.RouterOutputId)}\".");
        else if (SameId(target, plan.FallbackOutputId) && plan.HeadsetMissing)
            ShowStatus($"Your headset isn't connected — PC audio now plays on \"{name}\".", isError: true);
        else
            ShowStatus($"PC audio now plays straight on \"{name}\".");
        RefreshSetupState();
    }

    /// <summary>Rebuilds the Setup pick lists (only when they changed) and raises every Setup text.</summary>
    private void RefreshSetupState()
    {
        var s = _library.Settings;
        var mics = BuildChoices(RoutingPlanner.MicrophoneChoices(_deviceSnapshot), s.InputDeviceId);
        var headsets = BuildChoices(RoutingPlanner.HeadsetChoices(_deviceSnapshot), s.HeadsetDeviceId);

        bool micsChanged = !mics.SequenceEqual(_micChoices);
        bool headsetsChanged = !headsets.SequenceEqual(_headsetChoices);
        if (micsChanged || headsetsChanged)
        {
            // A ComboBox whose list is replaced may push "" back through its binding; ignore that and re-sync.
            _rebuildingChoices = true;
            try
            {
                if (micsChanged) MicChoices = mics;
                if (headsetsChanged) HeadsetChoices = headsets;
            }
            finally
            {
                _rebuildingChoices = false;
            }

            OnPropertyChanged(nameof(SelectedMicId));
            OnPropertyChanged(nameof(SelectedHeadsetId));
        }

        OnPropertyChanged(nameof(EqAllAudio));
        OnPropertyChanged(nameof(CanEqAllAudio));
        OnPropertyChanged(nameof(EqAllStatusText));
        OnPropertyChanged(nameof(ShowApoDeviceSelectorHint));
        OnPropertyChanged(nameof(ApoInfoText));
        OnPropertyChanged(nameof(ApoEqRunning));
        OnPropertyChanged(nameof(ApoEqBadgeText));
        OnPropertyChanged(nameof(VirtualMicName));
        OnPropertyChanged(nameof(VirtualMicHint));
        OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(SetupNeeded));
        OnPropertyChanged(nameof(ShowCallModeWarning));
        OnPropertyChanged(nameof(SetupWarningText));
        OnPropertyChanged(nameof(SetupMicStatus));
        OnPropertyChanged(nameof(SetupHearStatus));
        OnPropertyChanged(nameof(ShowMisrouteBanner));
        OnPropertyChanged(nameof(InletName));
        OnPropertyChanged(nameof(SystemEqStatusText));
        OnPropertyChanged(nameof(EffectsStatusText));
        OnPropertyChanged(nameof(SpectrumDeviceId));
        RefreshMirrorState();
    }

    private IReadOnlyList<DeviceOption> BuildChoices(IReadOnlyList<AudioDeviceInfo> candidates, string? currentId)
    {
        var options = candidates.Select(d => new DeviceOption(d.Id, d.FriendlyName)).ToList();
        if (!string.IsNullOrEmpty(currentId) && !options.Any(o => SameId(o.Id, currentId)))
        {
            // The saved pick is not offered (unplugged, or a virtual device chosen before): keep it visible.
            var known = _deviceSnapshot.FirstOrDefault(d => SameId(d.Id, currentId));
            options.Add(new DeviceOption(currentId,
                known is null ? "Saved device (not connected)"
                : known.State == DeviceActivity.Active ? known.FriendlyName
                : $"{known.FriendlyName} (not connected)"));
        }

        return options;
    }

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task RecheckCableAsync()
    {
        _cableBannerDismissed = false;
        await RefreshDevicesAsync();
        ShowStatus(CableMissing
            ? "VB-Audio Virtual Cable still not found. After installing it, restart Windows if the installer asked you to."
            : "Virtual cable found.", isError: CableMissing);
    }

    private void ApplyDeviceSnapshot()
    {
        // The virtual-mic cable's extra playback endpoint (its 16-channel "CABLE In 16ch", renamed or not) is never
        // listed: it is the same cable as the feed and only invites misrouting.
        string? feed = CableFeedId;
        var list = _deviceSnapshot.Where(d => !VirtualDeviceDetector.IsHiddenCableEndpoint(d, feed)).ToList();
        bool all = _showInactiveDevices;

        Sync(Outputs, list.Where(d => d.Kind == DeviceKind.Playback && (all || d.State == DeviceActivity.Active)), headphones: false);
        Sync(Inputs, list.Where(d => d.Kind == DeviceKind.Capture && (all || d.State == DeviceActivity.Active)), headphones: false);
        Sync(Headphones, list.Where(d => d.Kind == DeviceKind.Playback && d.IsBluetooth && (all || d.State == DeviceActivity.Active)), headphones: true);

        HasHeadphones = Headphones.Count > 0;
        InactiveDeviceCount = list.Count(d => d.State != DeviceActivity.Active);
        OnPropertyChanged(nameof(MicStatusText));
        RefreshBatteries();
    }

    private void Sync(ObservableCollection<DeviceViewModel> target, IEnumerable<AudioDeviceInfo> source, bool headphones)
    {
        var wanted = source.ToList();
        var ids = wanted.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(target[i].Id)) target.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            var info = wanted[i];
            int existing = -1;
            for (int j = 0; j < target.Count; j++)
            {
                if (string.Equals(target[j].Id, info.Id, StringComparison.OrdinalIgnoreCase))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                target.Insert(Math.Min(i, target.Count), new DeviceViewModel(this, info, headphones));
            }
            else
            {
                target[existing].Update(info);
                if (existing != i && i < target.Count) target.Move(existing, i);
            }
        }
    }

    private void RefreshBatteries()
    {
        if (_disposed) return;
        foreach (var row in Headphones.ToList())
        {
            if (!row.IsActive)
            {
                row.BatteryText = "—";
                continue;
            }

            var name = row.Name;
            Task.Run(() => BluetoothBatteryReader.TryGetBatteryPercent(name))
                .ContinueWith(t =>
                {
                    int? pct = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    row.BatteryText = pct is int p ? $"{p}%" : "—";
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>Background thread (DeviceViewModel's volume worker).</summary>
    internal void ApplyVolume(DeviceViewModel device, float scalar)
    {
        try
        {
            _devices.SetVolume(device.Id, scalar);
        }
        catch (Exception ex)
        {
            _dispatcher.BeginInvoke(() => ShowStatus($"Could not set the volume of \"{device.Name}\": {ex.Message}", isError: true));
        }
    }

    internal void SetMute(DeviceViewModel device, bool muted)
    {
        var id = device.Id;
        Task.Run(() => _devices.SetMute(id, muted)).ContinueWith(t =>
        {
            if (t.Exception is { } ex)
            {
                device.RevertMuted(!muted);
                ShowStatus($"Could not {(muted ? "mute" : "unmute")} \"{device.Name}\": {ex.GetBaseException().Message}", isError: true);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    internal void SetEnhancements(DeviceViewModel device, bool enabled)
    {
        var id = device.Id;
        Task.Run(() => _devices.TrySetEnhancements(id, enabled)).ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
            if (!ok)
            {
                device.RevertEnhancements(!enabled);
                ShowStatus($"Windows did not allow changing Audio Enhancements for \"{device.Name}\" (some drivers lock it, or it needs administrator rights).", isError: true);
            }
            else
            {
                ShowStatus($"Audio Enhancements {(enabled ? "enabled" : "disabled")} for \"{device.Name}\".");
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    internal void MakeDefault(DeviceViewModel device)
    {
        var id = device.Id;
        bool comms = _library.Settings.SetDefaultCommunications;
        Task.Run(() =>
        {
            _devices.SetDefaultDevice(id, DeviceRole.Default);
            if (comms) _devices.SetDefaultDevice(id, DeviceRole.Communications);
        }).ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                ShowStatus(ex.GetBaseException().Message, isError: true);
            else
                ShowStatus($"\"{device.Name}\" is now the default {(device.Kind == DeviceKind.Playback ? "playback" : "recording")} device{(comms ? " (and communications device)" : "")}.");

            _ = RefreshDevicesAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private AudioDeviceInfo? FindActive(string? id, DeviceKind kind) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _deviceSnapshot.FirstOrDefault(d => d.Kind == kind && d.State == DeviceActivity.Active
                                                  && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    private string DeviceName(string? id) => SnapshotName(id) ?? "(unknown device)";

    /// <summary>The device's current friendly name, or null when it is not in the last device scan.</summary>
    private string? SnapshotName(string? id) =>
        _deviceSnapshot.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))?.FriendlyName;

    /// <summary>The virtual-mic cable's feed by its plain label ("Virtual mic feed"); any other device by name.</summary>
    private string OutputLabel(string? id)
    {
        var device = _deviceSnapshot.FirstOrDefault(d => SameId(d.Id, id));
        return device is not null && device.Kind == DeviceKind.Playback && VirtualDeviceDetector.IsVirtualMicInput(device.FriendlyName)
            ? "Virtual mic feed"
            : DeviceName(id);
    }

    // ------------------------------------------------------------------ engine

    /// <summary>Settings → Advanced changed the virtual-mic output: re-plan (restarts the engine on the new device).</summary>
    internal void OnRoutingDevicesChanged()
    {
        MarkDirty();
        QueueRouting(DefaultOutputMode.Passive);
    }

    /// <summary>Settings → Advanced changed the monitor override: re-plan (the monitor follows the plan).</summary>
    internal void OnMonitorSettingsChanged()
    {
        MarkDirty();
        OnPropertyChanged(nameof(MonitorEnabled));
        QueueRouting(DefaultOutputMode.Passive);
    }

    /// <summary>
    /// Test seam: feeds <paramref name="apo"/> into <see cref="UpdateEqualizerApo"/> (so the writer factory runs)
    /// and sets the current <paramref name="plan"/> so <see cref="ApplyEqualizerApo"/> can route through the writer
    /// without a device-scan round-trip. Internal; used by AudioController.Tests.
    /// </summary>
    internal void SeedEqualizerApoForTests(EqualizerApoState apo, RoutingPlan plan)
    {
        UpdateEqualizerApo(apo);
        _plan = plan;
    }

    /// <summary>
    /// The one set of EQ sliders drives the soundboard bus, System EQ and — while "EQ everything I hear" uses it —
    /// Equalizer APO (debounced ~150 ms while a slider moves).
    /// </summary>
    internal void ApplyEq()
    {
        var s = _library.Settings;

        // An older code path that wrote the 1.4 array directly still works: its gains are adopted into the bands.
        if (FxSettings.SyncLegacyGains(s) && Effects is { } effects)
        {
            effects.Refresh();
        }

        var fx = FxState.From(s);
        SafeEngine(() => _engine.SetFx(fx));
        SafeEngine(() => _systemEq.SetFx(fx));
        ApplyEqualizerApo();
        OnPropertyChanged(nameof(ApoEqRunning));
        OnPropertyChanged(nameof(SetupHearStatus));
        OnPropertyChanged(nameof(EffectsStatusText));
        MarkDirty();
    }

    /// <summary>The Effects panel would change the sound (power on, and the EQ on or an effect above 0 %).</summary>
    private bool FxAudible
    {
        get
        {
            var s = _library.Settings;
            return s.FxEnabled && (s.EqEnabled || s.Effects is { IsZero: false });
        }
    }

    /// <summary>
    /// The Effects panel's status line: how the effects reach PC audio ("EQ everything I hear via Equalizer APO — …",
    /// the same text as the Setup card) or why they apply to soundboard sounds only.
    /// </summary>
    public string EffectsStatusText
    {
        get
        {
            var s = _library.Settings;
            if (!s.FxEnabled) return "Effects are off (power button) — nothing is applied to PC audio or soundboard sounds.";
            var plan = _plan;
            if (plan is null) return "EQ everything I hear: checking devices…";
            if (plan.ApoActive)
            {
                return plan.HeadsetMissing
                    ? "EQ everything I hear via Equalizer APO — your headset isn't connected right now."
                    : "EQ everything I hear " + RoutingPlanner.ApoViaText + $" (\"{DeviceName(plan.HeadsetId)}\").";
            }

            if (plan.EqAllActive && plan.EqAllMechanism == EqAllMechanism.Router)
                return "EQ everything I hear via the System EQ router — " + SystemEqStatusText;

            return "Soundboard sounds only — EQ everything I hear: " + plan.EqAllStatusText;
        }
    }

    /// <summary>The endpoint the spectrum analyzer listens to (post-Equalizer APO): the connected headset, or null.</summary>
    public string? SpectrumDeviceId => _plan is { HeadsetMissing: false, HeadsetId: { } headset } ? headset : null;

    /// <summary>Asks the user (Yes/No) through the window; true when there is no window (tests).</summary>
    internal bool ConfirmWithUser(string title, string message) => _ui?.Confirm(title, message) ?? true;

    // ------------------------------------------------------------------ Equalizer APO

    /// <summary>UI thread: a new Equalizer APO state from a scan. (Re)creates the writer when the config folder changed.</summary>
    private void UpdateEqualizerApo(EqualizerApoState state)
    {
        bool folderChanged = !string.Equals(state.ConfigPath, _apo.ConfigPath, StringComparison.OrdinalIgnoreCase)
                             || state.Installed != _apo.Installed;
        _apo = state;
        if (folderChanged)
        {
            try { _apoWriter?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            _apoWriter = null;
            if (state.Installed && state.ConfigPath is { } dir)
            {
                var writer = _apoWriterFactory(dir);
                writer.WriteFailed += (_, message) => _dispatcher.BeginInvoke(() => ShowStatus(message, isError: true));
                _apoWriter = writer;
            }

            RelayCommand.Refresh();
        }
    }

    /// <summary>
    /// Keeps the app's Equalizer APO file in step with the plan and the sliders (never written when Equalizer APO is
    /// not installed). "EQ everything I hear" via Equalizer APO → the EQ, scoped to the headset only, and the include
    /// line in config.txt; otherwise a flat (neutral) file so stale filters never linger on a device the app is no
    /// longer EQ'ing. While the headset is disconnected the file is left alone (so briefly losing it does not blip the
    /// filters). Nothing here can reach a microphone or the virtual cable: every filter sits behind
    /// "Device: &lt;headset GUID&gt;".
    /// </summary>
    private void ApplyEqualizerApo()
    {
        var writer = _apoWriter;
        var plan = _plan;
        if (writer is null || plan is null || _disposed) return;

        if (plan.HeadsetMissing) return;

        var s = _library.Settings;
        var (rate, channels) = HeadsetFormat(plan.HeadsetId);
        writer.WriteFxState(FxState.From(s), plan.HeadsetId, plan.ApoActive, s.EqAutoPreamp, channels, rate);
    }

    /// <summary>
    /// The headset's mix format for the Equalizer APO effects (Surround's Copy is stereo-only; the Ambience impulse
    /// response must match its rate). Read once per headset off the UI thread; until it is known (or when it can't be
    /// read) 48 kHz stereo is assumed, and the file is rewritten as soon as the real format arrives.
    /// </summary>
    private (int SampleRate, int Channels) HeadsetFormat(string? headsetId)
    {
        const int defaultRate = AmbienceImpulse.DefaultSampleRate;
        if (headsetId is null) return (defaultRate, 2);
        if (SameId(_headsetFormatId, headsetId)) return _headsetFormat ?? (defaultRate, 2);

        _headsetFormatId = headsetId;
        _headsetFormat = null;
        var devices = _devices;
        Task.Run(() => devices.GetMixFormat(headsetId)).ContinueWith(t =>
        {
            var format = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (format is null || _disposed) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !SameId(_headsetFormatId, headsetId)) return;
                _headsetFormat = format;
                if (format.Value != (defaultRate, 2)) ApplyEqualizerApo();
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return (defaultRate, 2);
    }

    /// <summary>Starts Equalizer APO's Device Selector from its own folder (a Qt app: it needs that working directory).</summary>
    private void OpenDeviceSelector()
    {
        var apo = _apo;
        var path = EqualizerApoDetector.DeviceSelectorPath(apo);
        if (path is null || apo.InstallPath is null)
        {
            ShowStatus("Equalizer APO's Device Selector was not found. Reinstall Equalizer APO, then try again.", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = apo.InstallPath,
                UseShellExecute = true, // it asks for administrator rights itself
            });
            ShowStatus("Device Selector opened: tick your headset under Playback devices, click OK, then restart the PC.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowStatus("Device Selector was not opened (administrator permission was declined).", isError: true);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open Equalizer APO's Device Selector: " + ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Applies the mute toggle off the UI thread (unmuting opens the microphone). Requests run strictly in
    /// order; once the latest one is done the toggle is re-synced with the engine, which stays muted when
    /// the microphone could not be reopened (its EngineError explains why).
    /// </summary>
    private void ApplyMicMuted(bool muted)
    {
        int version = ++_micMuteVersion;
        var engine = _engine;
        var apply = _micMuteApply.ContinueWith(_ => engine.MicMuted = muted,
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _micMuteApply = apply;
        apply.ContinueWith(t =>
        {
            if (_disposed || version != _micMuteVersion) return;
            if (t.Exception is { } ex)
                ShowStatus("Mic mute: " + ex.GetBaseException().Message, isError: true);

            bool actual = _engine.MicMuted;
            if (actual != _library.Settings.MicMuted)
            {
                _library.Settings.MicMuted = actual;
                OnPropertyChanged(nameof(MicMuted));
                MarkDirty();
            }

            SetEngineMicMuted(actual);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetEngineMicMuted(bool muted)
    {
        if (_engineMicMuted == muted) return;
        _engineMicMuted = muted;
        OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(MicStatusDetail));
        OnPropertyChanged(nameof(SetupMicStatus));
    }

    /// <summary>
    /// "Hear soundboard locally" plays on the plan's monitor device (the headset unless overridden). While Equalizer APO
    /// equalizes that same headset the monitor bus skips the in-app EQ (no double EQ); the virtual-mic bus keeps it.
    /// </summary>
    private void ApplyMonitor()
    {
        var s = _library.Settings;
        string? id = _plan is { } plan ? plan.MonitorOutputId : s.MonitorDeviceId;
        bool on = s.MonitorEnabled;
        bool bypass = _plan?.MonitorEqBypass == true;
        Task.Run(() =>
        {
            _engine.SetMonitorEqBypass(bypass);
            _engine.SetMonitor(id, on);
        }).ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                ShowStatus("Local monitoring: " + ex.GetBaseException().Message, isError: true);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Starts routing only when the microphone and the virtual output are set and present; otherwise
    /// stops it and shows why. Preselects (and saves) "CABLE Input" when the cable exists and no
    /// output is configured.
    /// </summary>
    public async Task EnsureEngineAsync(bool forceRestart = false)
    {
        if (_disposed) return;

        await _engineGate.WaitAsync();
        try
        {
            if (_disposed) return;
            var s = _library.Settings;

            if (string.IsNullOrWhiteSpace(s.OutputDeviceId) && _cableInputId is not null)
            {
                s.OutputDeviceId = _cableInputId;
                MarkDirty();
            }

            var input = FindActive(s.InputDeviceId, DeviceKind.Capture);

            // The plan's virtual-mic output: a valid Advanced override, else CABLE Input found automatically.
            string? outputId = _plan is { } plan ? plan.VirtualMicOutputId : s.OutputDeviceId;
            var output = FindActive(outputId, DeviceKind.Playback);

            string? reason = null;
            if (output is null && string.IsNullOrWhiteSpace(outputId))
                reason = _cableMissing ? "VB-Audio Virtual Cable is not installed" : "the virtual cable (CABLE Input) was not found";
            else if (string.IsNullOrWhiteSpace(s.InputDeviceId))
                reason = "pick your microphone in Setup";
            else if (input is null)
                reason = "the selected microphone is not connected";
            else if (VirtualDeviceDetector.IsVirtualMicOutput(input.FriendlyName))
                reason = "the microphone is set to the virtual mic itself; pick your real microphone in Setup";
            else if (output is null)
                reason = "the virtual mic feed (CABLE Input) is not available";

            if (reason is not null)
            {
                if (_engine.IsRunning) await Task.Run(_engine.Stop);
                _runningInputId = _runningOutputId = null;
                SetStopReason(reason);
                EngineRunning = false;
                return;
            }

            if (!forceRestart && _engine.IsRunning
                && string.Equals(_runningInputId, input!.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_runningOutputId, output!.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string inId = input!.Id;
            string outId = output!.Id;
            var fx = FxState.From(s);
            string? monitorId = _plan is { } p ? p.MonitorOutputId : s.MonitorDeviceId;
            bool monitorOn = s.MonitorEnabled;

            SetStopReason("starting…");
            try
            {
                await Task.Run(() =>
                {
                    _engine.SetFx(fx);
                    _engine.SetMonitor(monitorId, monitorOn);
                    _engine.Start(inId, outId);
                });
                _runningInputId = inId;
                _runningOutputId = outId;
                EngineRunning = _engine.IsRunning;
                OnPropertyChanged(nameof(MicStatusText));
            }
            catch (Exception ex)
            {
                _runningInputId = _runningOutputId = null;
                EngineRunning = false;
                SetStopReason(ex.Message);
                ShowStatus(ex.Message, isError: true);
            }
        }
        finally
        {
            _engineGate.Release();
        }
    }

    private void SetStopReason(string reason)
    {
        _stopReason = reason;
        OnPropertyChanged(nameof(MicStatusDetail));
        OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(SetupMicStatus));
    }

    private void OnEngineStatus(object? sender, EngineStatus status)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pendingStatus, status);
        if (Interlocked.Exchange(ref _statusScheduled, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPendingStatus);
    }

    private void ApplyPendingStatus()
    {
        Volatile.Write(ref _statusScheduled, 0);
        var status = Interlocked.Exchange(ref _pendingStatus, null);
        if (status is null || _disposed) return;

        if (!status.Running && _runningInputId is not null && !_engine.IsRunning)
        {
            // The engine stopped itself (device lost); EngineError carries the reason.
            _runningInputId = _runningOutputId = null;
        }

        EngineRunning = status.Running;
        SetEngineMicMuted(status.MicMuted);
        SoundPlaying = status.Running && status.AnySoundPlaying;

        double target = status.Running ? ToMeter(status.PeakLevel) : 0;
        _displayPeak = Math.Max(target, _displayPeak - 0.05);
        PeakLevel = _displayPeak;
        PeakText = status.Running && status.PeakLevel > 0.00001f
            ? $"{20 * Math.Log10(status.PeakLevel):0.0} dB"
            : "−∞ dB";

        foreach (var slot in AllSlots())
            slot.IsPlaying = status.Running && _engine.IsSlotPlaying(slot.Id);
    }

    private static double ToMeter(float peak)
    {
        if (peak <= 0.001f) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    private void OnEngineError(object? sender, string message)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (!_engine.IsRunning)
            {
                _runningInputId = _runningOutputId = null;
                SetStopReason(message);
                EngineRunning = false;
            }

            ShowStatus(message, isError: true);
        });
    }

    private void SafeEngine(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
    }

    // ------------------------------------------------------------------ system EQ

    /// <summary>
    /// Settings → Advanced changed the System EQ source (inlet) or output: re-plan. A new inlet while routing
    /// also moves the Windows default to it.
    /// </summary>
    internal void OnSystemEqDevicesChanged()
    {
        MarkDirty();
        QueueRouting(DefaultOutputMode.Explicit);
    }

    /// <summary>
    /// Runs the System EQ router (inlet → EQ → headset) only while the plan routes PC audio and both devices are
    /// present and valid; otherwise stops it and records why. Starting happens off the UI thread. Never affects the
    /// virtual microphone.
    /// </summary>
    public async Task EnsureSystemEqAsync(bool forceRestart = false)
    {
        if (_disposed) return;

        await _systemEqGate.WaitAsync();
        try
        {
            if (_disposed) return;
            var s = _library.Settings;
            var plan = _plan;

            AudioDeviceInfo? source = null;
            AudioDeviceInfo? output = null;
            string? reason;
            if (plan is null)
            {
                reason = "checking devices…";
            }
            else if (!plan.EqAllRequested || plan.EqAllMechanism == EqAllMechanism.EqualizerApo)
            {
                reason = "off"; // not wanted, or Equalizer APO equalizes the headset instead
            }
            else if (!plan.RoutingActive)
            {
                reason = plan.InletId is null ? "no spare virtual output device (see Setup)"
                    : string.IsNullOrEmpty(s.HeadsetDeviceId) ? "pick your headset in Setup"
                    : plan.HeadsetMissing ? "your headset isn't connected"
                    : "not available";
            }
            else
            {
                source = FindActive(plan.InletId, DeviceKind.Playback);
                output = FindActive(plan.RouterOutputId, DeviceKind.Playback);
                if (source is null)
                    reason = "the inlet device is not connected";
                else if (output is null)
                    reason = "the headset is not connected";
                else if (SameId(source.Id, plan.VirtualMicOutputId) || SameId(output.Id, plan.VirtualMicOutputId))
                    reason = "that is the virtual-mic output, so Discord would hear your system audio";
                else
                    reason = SystemEqRouter.CheckDevices(source.Id, output.Id, source, output);
            }

            if (reason is not null)
            {
                if (_systemEq.IsRunning) await Task.Run(_systemEq.Stop);
                SetSystemEqReason(reason);
                SystemEqRunning = false;
                return;
            }

            if (!forceRestart && _systemEq.IsRunning
                && string.Equals(_systemEq.SourceDeviceId, source!.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_systemEq.OutputDeviceId, output!.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string sourceId = source!.Id;
            string outputId = output!.Id;
            var fx = FxState.From(s);

            SetSystemEqReason("starting…");
            try
            {
                await Task.Run(() =>
                {
                    _systemEq.SetFx(fx);
                    _systemEq.Start(sourceId, outputId);
                });
                SystemEqRunning = _systemEq.IsRunning;
                OnPropertyChanged(nameof(SystemEqStatusText));
            }
            catch (Exception ex)
            {
                SystemEqRunning = false;
                SetSystemEqReason(ex.Message);
                ShowStatus(ex.Message, isError: true);
            }
        }
        finally
        {
            _systemEqGate.Release();
        }
    }

    private void SetSystemEqReason(string reason)
    {
        _systemEqReason = reason;
        OnPropertyChanged(nameof(SystemEqStatusText));
    }

    private void OnSystemEqStatus(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed) return;
            SystemEqRunning = _systemEq.IsRunning;
            OnPropertyChanged(nameof(SystemEqStatusText));
        });
    }

    private void OnSystemEqError(object? sender, string message)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (!_systemEq.IsRunning)
            {
                SetSystemEqReason(message);
                SystemEqRunning = false;

                // Re-plan from a fresh scan: restart the router, or (headset gone / restart fails) move the
                // Windows default off the inlet so PC audio is not left playing into silence.
                _devicesChangedTimer.Stop();
                _devicesChangedTimer.Start();
            }

            ShowStatus(message, isError: true);
        });
    }

    // ------------------------------------------------------------------ "Also play on" (mirror)

    /// <summary>
    /// "Also play on" master switch (persisted as <see cref="AppSettings.MirrorEnabled"/>): what plays on the headset —
    /// with the EQ and effects, since it is copied after Equalizer APO — also plays on the ticked outputs while the app
    /// is open.
    /// </summary>
    public bool MirrorEnabled
    {
        get => _library.Settings.MirrorEnabled;
        set
        {
            var s = _library.Settings;
            if (value == s.MirrorEnabled) return;
            s.MirrorEnabled = value;
            OnPropertyChanged();
            MarkDirty();
            QueueMirrorRefresh();
            ScheduleMirrorApply();
        }
    }

    /// <summary>The "Also play on" list: connected real outputs other than the headset, plus ticked ones that are gone.</summary>
    public ObservableCollection<MirrorTargetViewModel> MirrorDevices { get; } = new();

    /// <summary>"Mirroring to Digital Output (Realtek USB2.0 Audio)", "Off", or why it is not playing.</summary>
    public string MirrorStatusText
    {
        get
        {
            var s = _library.Settings;
            if (!s.MirrorEnabled) return "Off — tick to also hear your PC audio on another output (e.g. optical / Toslink).";
            var plan = _mirrorPlan;
            if (plan is null) return "Also play on: checking devices…";
            if (!plan.Active) return plan.InactiveReason;
            if (_mirrorError is { } error) return error;
            if (_mirrorStatus.SourceError is { } sourceError) return sourceError;
            var running = _mirrorStatus.RunningTargets.Select(t => t.DeviceName).ToList();
            if (running.Count == 0)
            {
                var failed = _mirrorStatus.Targets.FirstOrDefault(t => t.Error is not null && t.Error != "starting…");
                return failed?.Error ?? "Starting…";
            }

            return "Mirroring to " + JoinNames(running);
        }
    }

    /// <summary>The mirror is asked for but something is wrong (status shown in the warning colour).</summary>
    public bool MirrorStatusIsError
    {
        get
        {
            var plan = _mirrorPlan;
            if (!_library.Settings.MirrorEnabled || plan is null) return false;
            if (!plan.Active) return plan.Skipped.Count > 0 || plan.InactiveReason == MirrorPlanner.HeadsetMissingText;
            return _mirrorError is not null || _mirrorStatus.SourceError is not null
                   || _mirrorStatus.Targets.Any(t => !t.Running && t.Error is not null && t.Error != "starting…");
        }
    }

    /// <summary>
    /// Notes under the list: Equalizer APO attached to an extra output (it stays neutral there), and PC audio playing on
    /// another device than the headset (only what plays on the headset is copied). Null when there is nothing to say.
    /// </summary>
    public string? MirrorNoteText
    {
        get
        {
            var plan = _mirrorPlan;
            if (!_library.Settings.MirrorEnabled || plan is null) return null;
            var notes = new List<string>(plan.Notes);
            if (plan.Active && _windowsDefaultOutputId is { } def && plan.SourceId is { } src
                && !SameId(def, src) && !SameId(def, _plan?.InletId))
            {
                notes.Add($"PC audio is set to play on \"{DeviceName(def)}\", not on your headset — only what plays on the headset is copied.");
            }

            return notes.Count == 0 ? null : string.Join("\n", notes);
        }
    }

    /// <summary>Only the headset is connected: the list is empty.</summary>
    public bool MirrorNoDevices => MirrorDevices.Count == 0;

    /// <summary>
    /// Test seam: feeds a device list and a routing plan as if a scan had produced them (no hardware, no routing pass),
    /// then rebuilds the "Also play on" rows. Internal; used by AudioController.Tests.
    /// </summary>
    internal void SeedDevicesForTests(IReadOnlyList<AudioDeviceInfo> devices, RoutingPlan plan, string? windowsDefaultOutputId = null)
    {
        _deviceSnapshot = devices;
        _plan = plan;
        _windowsDefaultOutputId = windowsDefaultOutputId;
        RefreshMirrorState();
    }

    /// <summary>A row's checkbox / slider changed: saved (debounced) and applied ~150 ms later.</summary>
    internal void OnMirrorTargetsChanged()
    {
        MarkDirty();
        QueueMirrorRefresh();
        ScheduleMirrorApply();
    }

    /// <summary>
    /// Rebuilds the rows after the current input event (a CheckBox / Slider binding is still committing when its setter
    /// calls this, so the rows must not be refreshed underneath it).
    /// </summary>
    private void QueueMirrorRefresh()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshMirrorState);
    }

    private void ScheduleMirrorApply()
    {
        if (_disposed) return;
        _mirrorApplyTimer.Stop();
        _mirrorApplyTimer.Start();
    }

    /// <summary>
    /// Runs the "Also play on" mirror as <see cref="MirrorPlanner.Plan"/> says: copies the connected headset to the ticked,
    /// connected outputs (delay and volume change live), or stops it. Starting happens off the UI thread. Never touches
    /// the microphone, the virtual cable or Equalizer APO's files.
    /// </summary>
    /// <param name="retryFailed">Retry outputs that failed right away (after a device scan) instead of after their back-off.</param>
    public async Task EnsureMirrorAsync(bool retryFailed = false)
    {
        if (_disposed) return;

        await _mirrorGate.WaitAsync();
        try
        {
            if (_disposed) return;
            var plan = MirrorPlanner.Plan(_library.Settings, _deviceSnapshot, _plan, _apo);
            _mirrorPlan = plan;
            var mirror = _mirror;
            if (!plan.Active)
            {
                _mirrorError = null;
                if (mirror.IsRunning || mirror.GetStatus().SourceId is not null) await Task.Run(mirror.Stop);
                _mirrorStatus = mirror.GetStatus();
            }
            else
            {
                string source = plan.SourceId!;
                var routes = plan.Routes;
                try
                {
                    _mirrorStatus = await Task.Run(() => mirror.Apply(source, routes, retryFailed));
                    _mirrorError = null;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _mirrorError = "Also play on: " + ex.Message;
                    ShowStatus(_mirrorError, isError: true);
                }
            }

            RefreshMirrorState();
        }
        finally
        {
            _mirrorGate.Release();
        }
    }

    /// <summary>Rebuilds the "Also play on" rows (keeping existing ones, so a slider being dragged is never replaced) and the texts.</summary>
    private void RefreshMirrorState()
    {
        if (_disposed) return;
        var s = _library.Settings;
        string? headsetId = _plan?.HeadsetId ?? s.HeadsetDeviceId;
        var wanted = MirrorPlanner.TargetChoices(_deviceSnapshot, headsetId, _plan?.VirtualMicOutputId)
            .Select(d => (Id: d.Id, Name: d.FriendlyName, Connected: true))
            .ToList();

        // A ticked output that is gone stays listed (greyed) so its tick, volume and delay are visible.
        foreach (var t in s.MirrorTargets)
        {
            if (!t.Enabled || wanted.Any(w => SameId(w.Id, t.DeviceId))) continue;
            var known = _deviceSnapshot.FirstOrDefault(d => SameId(d.Id, t.DeviceId));
            if (known is not null && MirrorPlanner.RejectReason(known, headsetId, _plan?.VirtualMicOutputId) is { } reason
                && reason != "not connected")
            {
                continue; // the headset itself, the cable, a virtual device: never offered
            }

            wanted.Add((t.DeviceId, (known?.FriendlyName ?? t.DeviceName ?? "Saved output") + " (not connected)", false));
        }

        for (int i = MirrorDevices.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => SameId(w.Id, MirrorDevices[i].DeviceId))) MirrorDevices.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            var (id, name, connected) = wanted[i];
            int at = -1;
            for (int j = 0; j < MirrorDevices.Count; j++)
            {
                if (SameId(MirrorDevices[j].DeviceId, id)) { at = j; break; }
            }

            MirrorTargetViewModel row;
            if (at < 0)
            {
                row = new MirrorTargetViewModel(this, id, name, connected);
                MirrorDevices.Insert(Math.Min(i, MirrorDevices.Count), row);
            }
            else
            {
                row = MirrorDevices[at];
                if (at != i && i < MirrorDevices.Count) MirrorDevices.Move(at, i);
                row.Name = name;
                row.IsConnected = connected;
                row.Refresh();
            }

            var status = _mirrorStatus.Targets.FirstOrDefault(t => SameId(t.DeviceId, id));
            var skip = _mirrorPlan?.Skipped.FirstOrDefault(k => SameId(k.DeviceId, id));
            if (!row.IsChecked || !s.MirrorEnabled)
            {
                row.StatusText = null;
                row.StatusIsError = false;
            }
            else if (skip is not null)
            {
                row.StatusText = skip.Reason;
                row.StatusIsError = true;
            }
            else if (status is { Running: true })
            {
                row.StatusText = "playing";
                row.StatusIsError = false;
            }
            else
            {
                row.StatusText = status?.Error;
                row.StatusIsError = status?.Error is not null && status.Error != "starting…";
            }
        }

        OnPropertyChanged(nameof(MirrorNoDevices));
        OnPropertyChanged(nameof(MirrorStatusText));
        OnPropertyChanged(nameof(MirrorStatusIsError));
        OnPropertyChanged(nameof(MirrorNoteText));
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private void OnMirrorStatus(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed) return;
            _mirrorStatus = _mirror.GetStatus();
            RefreshMirrorState();
        });
    }

    private void OnMirrorError(object? sender, string message)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _mirrorStatus = _mirror.GetStatus();
            RefreshMirrorState();
            ShowStatus(message, isError: true);
        });
    }

    // ------------------------------------------------------------------ settings window

    public SettingsViewModel CreateSettingsViewModel() => new(this, _library.Settings, _deviceSnapshot);

    // ------------------------------------------------------------------ status line

    public void ShowStatus(string message, bool isError = false)
    {
        if (_disposed) return;
        StatusMessage = message;
        StatusIsError = isError;
        _statusClearTimer.Stop();
        _statusClearTimer.Interval = TimeSpan.FromSeconds(isError ? 12 : 6);
        _statusClearTimer.Start();
    }

    /// <summary>Read-only device list for <see cref="VirtualCableDetector"/> over an existing snapshot.</summary>
    private sealed class SnapshotDeviceManager(IReadOnlyList<AudioDeviceInfo> devices) : IDeviceManager
    {
        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => devices;

        public void SetDefaultDevice(string deviceId, DeviceRole role) => throw new NotSupportedException();

        public void SetVolume(string deviceId, float volumeScalar) => throw new NotSupportedException();

        public void SetMute(string deviceId, bool muted) => throw new NotSupportedException();

        public bool TrySetEnhancements(string deviceId, bool enabled) => false;

        public void Dispose()
        {
        }
    }
}
