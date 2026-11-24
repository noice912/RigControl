using AudioController.Audio;
using AudioController.Core.Devices;
using AudioController.Core.Models;

namespace AudioController.App.ViewModels;

/// <summary>Device choice in a settings ComboBox. Id "" = none.</summary>
public sealed record DeviceOption(string Id, string Name);

/// <summary>
/// Backs the modal settings window. Every change applies live and is persisted. The two Setup picks and the
/// PC-audio switch are the main view model's (bound through <see cref="Main"/>); this class adds the hotkey,
/// the Equalizer APO pre-amp and the Settings → Advanced overrides. The EQ and effects themselves live in the main
/// window's Effects panel (one source of truth); Settings → EQ only points there.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>The note in Settings → EQ about the headset's own EQ.</summary>
    public const string HeadsetEqNote =
        "Your headset's own EQ (e.g. Skull-iQ) is applied inside the headphones after this one — they add up. " +
        "Switch the Effects panel's power off to hear only the headset's EQ.";

    /// <summary>Settings → EQ: where the EQ went.</summary>
    public const string EffectsMovedNote =
        "The EQ and the effects (Fidelity, Ambience, Surround Sound, Dynamic Boost, Bass Boost) are in the Effects panel " +
        "of the main window, with presets and a live spectrum.";

    private readonly MainViewModel _main;
    private readonly AppSettings _s;
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;
    private readonly List<AudioDeviceInfo> _activePlayback;
    private readonly string? _cableFeedId;
    private readonly string? _cableMicId;
    private IReadOnlyList<DeviceOption> _systemEqSourceOptions;
    private IReadOnlyList<DeviceOption> _systemEqOutputOptions;
    private bool _rebuildingSystemEqOptions;

    public SettingsViewModel(MainViewModel main, AppSettings settings, IReadOnlyList<AudioDeviceInfo> devices)
    {
        _main = main;
        _s = settings;
        _devices = devices;

        // The cable's extra playback endpoint (its 16-channel one, renamed or not) is never offered anywhere.
        string? feedId = main.CableFeedId;
        _cableFeedId = feedId;
        _cableMicId = main.CableMicId;
        var active = devices.Where(d => d.State == DeviceActivity.Active && !VirtualDeviceDetector.IsHiddenCableEndpoint(d, feedId)).ToList();

        OutputOptions = BuildOptions(
            active.Where(d => d.Kind == DeviceKind.Playback)
                  .OrderByDescending(d => SameId(d.Id, feedId)),
            _s.OutputDeviceId, devices, "(automatic: " + VirtualDeviceDetector.FeedLabel + ")", feedId);

        MonitorOptions = BuildOptions(
            active.Where(d => d.Kind == DeviceKind.Playback
                              && !SystemEqRouter.IsVirtualMicInput(d.FriendlyName)),
            _s.MonitorDeviceId, devices, "(your headset)");

        // System EQ (Advanced): the routing plan fills source = inlet and output = headset; these lists override them.
        _activePlayback = active.Where(d => d.Kind == DeviceKind.Playback
                                            && !SystemEqRouter.IsVirtualMicInput(d.FriendlyName)).ToList();

        _systemEqSourceOptions = BuildSystemEqSourceOptions();
        _systemEqOutputOptions = BuildSystemEqOutputOptions();

        _main.PropertyChanged += OnMainPropertyChanged;
    }

    /// <summary>Unsubscribes from the main view model (call when the window closes).</summary>
    public void Detach() => _main.PropertyChanged -= OnMainPropertyChanged;

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StopAllHotkeyConflict))
        {
            OnPropertyChanged(nameof(StopAllConflict));
            OnPropertyChanged(nameof(HasStopAllConflict));
        }
        else if (e.PropertyName is nameof(MainViewModel.SelectedHeadsetId) or nameof(MainViewModel.InletName))
        {
            // The plan rewrote the System EQ source/output (new headset or inlet): show them.
            RebuildSystemEqOptions(source: true, output: true);
            OnPropertyChanged(nameof(SelectedMonitorId));
        }
    }

    public MainViewModel Main => _main;

    public string EqNote => HeadsetEqNote;

    public string EffectsNote => EffectsMovedNote;

    /// <summary>What the effects apply to (the Effects panel's caption).</summary>
    public string EffectsCaption => EffectsViewModel.Caption;

    /// <summary>Advanced: the cable's playback side, by its plain label and its current Windows name.</summary>
    public string VirtualMicFeedText => _cableFeedId is { } id
        ? $"{VirtualDeviceDetector.FeedLabel}: \"{NameOf(id)}\""
        : "Virtual mic feed: not found — install VB-Audio Virtual Cable.";

    /// <summary>Advanced: the cable's recording side, by its plain label and its current Windows name.</summary>
    public string VirtualMicText => _cableMicId is { } id
        ? $"{VirtualDeviceDetector.MicLabel}: \"{NameOf(id)}\""
        : "Virtual mic: not found — install VB-Audio Virtual Cable.";

    /// <summary>What the auto pre-amp checkbox does, in plain words (shown under it and as its tooltip).</summary>
    public const string EqAutoPreampHelpText =
        "On: the whole level is lowered by your biggest boost (EQ plus effects), so nothing can clip — but raising one " +
        "slider makes everything else quieter too. Off (default): raising a slider changes only that slider's sound and " +
        "the overall level stays the same; very big boosts can distort on loud songs.";

    /// <summary>See <see cref="EqAutoPreampHelpText"/>.</summary>
    public string EqAutoPreampHelp => EqAutoPreampHelpText;

    /// <summary>
    /// Equalizer APO auto pre-amp (off by default): on lowers the whole level by the biggest boost so it can't clip; off
    /// keeps the level, so a slider only changes its own sound.
    /// </summary>
    public bool EqAutoPreamp
    {
        get => _s.EqAutoPreamp;
        set
        {
            if (value == _s.EqAutoPreamp) return;
            _s.EqAutoPreamp = value;
            OnPropertyChanged();
            _main.ApplyEq();
        }
    }

    private string NameOf(string id) =>
        _devices.FirstOrDefault(d => SameId(d.Id, id))?.FriendlyName ?? "(unknown device)";

    public IReadOnlyList<DeviceOption> OutputOptions { get; }

    public IReadOnlyList<DeviceOption> MonitorOptions { get; }

    /// <summary>Active playback devices except "CABLE Input" and the System EQ output (virtual devices first).</summary>
    public IReadOnlyList<DeviceOption> SystemEqSourceOptions
    {
        get => _systemEqSourceOptions;
        private set => SetProperty(ref _systemEqSourceOptions, value);
    }

    /// <summary>Active playback devices except "CABLE Input" and the System EQ source (real devices first).</summary>
    public IReadOnlyList<DeviceOption> SystemEqOutputOptions
    {
        get => _systemEqOutputOptions;
        private set => SetProperty(ref _systemEqOutputOptions, value);
    }

    public string SelectedSystemEqSourceId
    {
        get => _s.SystemEqSourceDeviceId ?? "";
        set
        {
            if (_rebuildingSystemEqOptions) return; // a ComboBox clearing itself while its list is replaced
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v == _s.SystemEqSourceDeviceId) return;
            _s.SystemEqSourceDeviceId = v;
            OnPropertyChanged();
            RebuildSystemEqOptions(source: false, output: true); // only the other list, never the one being changed
            RelayCommand.Refresh();
            _main.OnSystemEqDevicesChanged();
        }
    }

    public string SelectedSystemEqOutputId
    {
        get => _s.SystemEqOutputDeviceId ?? "";
        set
        {
            if (_rebuildingSystemEqOptions) return;
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v == _s.SystemEqOutputDeviceId) return;
            _s.SystemEqOutputDeviceId = v;
            OnPropertyChanged();
            RebuildSystemEqOptions(source: true, output: false);
            RelayCommand.Refresh();
            _main.OnSystemEqDevicesChanged();
        }
    }

    public string SelectedOutputId
    {
        get => _s.OutputDeviceId ?? "";
        set
        {
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v == _s.OutputDeviceId) return;
            _s.OutputDeviceId = v;
            OnPropertyChanged();
            _main.OnRoutingDevicesChanged();
        }
    }

    public string SelectedMonitorId
    {
        get => _s.MonitorDeviceId ?? "";
        set
        {
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v == _s.MonitorDeviceId) return;
            _s.MonitorDeviceId = v;
            OnPropertyChanged();
            _main.OnMonitorSettingsChanged();
        }
    }

    public bool MonitorEnabled
    {
        get => _main.MonitorEnabled;
        set
        {
            if (value == _main.MonitorEnabled) return;
            _main.MonitorEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMonitorId));
        }
    }

    public string? StopAllHotkey
    {
        get => string.IsNullOrWhiteSpace(_s.StopAllHotkey) ? null : _s.StopAllHotkey;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
            if (v == (_s.StopAllHotkey ?? "")) return;
            _s.StopAllHotkey = v;
            OnPropertyChanged();
            _main.OnStopAllHotkeyChanged();
            OnPropertyChanged(nameof(StopAllConflict));
            OnPropertyChanged(nameof(HasStopAllConflict));
        }
    }

    public string? StopAllConflict => _main.StopAllHotkeyConflict;

    public bool HasStopAllConflict => _main.StopAllHotkeyConflict is not null;

    public bool SetDefaultCommunications
    {
        get => _s.SetDefaultCommunications;
        set
        {
            if (value == _s.SetDefaultCommunications) return;
            _s.SetDefaultCommunications = value;
            OnPropertyChanged();
            _main.MarkDirty();
        }
    }

    /// <summary>
    /// Replaces the System EQ device lists (each excludes the other's choice). A ComboBox whose selected item is
    /// not in its new list clears itself and pushes "" back through its binding; that push is ignored and the
    /// saved choice re-applied afterwards.
    /// </summary>
    private void RebuildSystemEqOptions(bool source, bool output)
    {
        _rebuildingSystemEqOptions = true;
        try
        {
            // Only replace a list whose content changed, so an open drop-down is not closed by a routine re-plan.
            if (source && BuildSystemEqSourceOptions() is var s && !s.SequenceEqual(SystemEqSourceOptions)) SystemEqSourceOptions = s;
            if (output && BuildSystemEqOutputOptions() is var o && !o.SequenceEqual(SystemEqOutputOptions)) SystemEqOutputOptions = o;
        }
        finally
        {
            _rebuildingSystemEqOptions = false;
        }

        if (source) OnPropertyChanged(nameof(SelectedSystemEqSourceId));
        if (output) OnPropertyChanged(nameof(SelectedSystemEqOutputId));
    }

    private IReadOnlyList<DeviceOption> BuildSystemEqSourceOptions() => BuildOptions(
        _activePlayback.Where(d => !SameId(d.Id, _s.SystemEqOutputDeviceId) && !SameId(d.Id, _s.OutputDeviceId))
                       .OrderByDescending(d => d.IsVirtual),
        _s.SystemEqSourceDeviceId, _devices, "(automatic)");

    private IReadOnlyList<DeviceOption> BuildSystemEqOutputOptions() => BuildOptions(
        _activePlayback.Where(d => !SameId(d.Id, _s.SystemEqSourceDeviceId) && !SameId(d.Id, _s.OutputDeviceId))
                       .OrderBy(d => d.IsVirtual),
        _s.SystemEqOutputDeviceId, _devices, "(your headset)");

    private static bool SameId(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DeviceOption> BuildOptions(
        IEnumerable<AudioDeviceInfo> candidates, string? currentId, IReadOnlyList<AudioDeviceInfo> all, string noneLabel,
        string? cableFeedId = null)
    {
        var options = new List<DeviceOption> { new("", noneLabel) };
        options.AddRange(candidates.Select(d => new DeviceOption(d.Id,
            cableFeedId is not null && SameId(d.Id, cableFeedId) ? VirtualDeviceDetector.FeedLabel : d.FriendlyName)));

        if (!string.IsNullOrEmpty(currentId) && options.All(o => o.Id != currentId))
        {
            var known = all.FirstOrDefault(d => d.Id == currentId);
            options.Add(new DeviceOption(currentId,
                known is null ? "Saved device (not connected)" : $"{known.FriendlyName} (not available)"));
        }

        return options;
    }
}
