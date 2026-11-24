using AudioController.Core.Models;
using AudioController.Core.Routing;

namespace AudioController.App.ViewModels;

/// <summary>
/// One row of the Setup card's "Also play on" list: a playback device that can receive a copy of the headset's sound,
/// with its checkbox, volume (0–100 %) and delay (0–500 ms). Every change is stored in
/// <see cref="AppSettings.MirrorTargets"/> (saved with the debounced library save) and applied live through
/// <see cref="MainViewModel.OnMirrorTargetsChanged"/>.
/// </summary>
public sealed class MirrorTargetViewModel : ObservableObject
{
    /// <summary>Tooltip of the delay slider.</summary>
    public const string DelayTooltip =
        "Match Bluetooth delay: Bluetooth headphones play ~150–250 ms late, so this output plays first. " +
        "Raise the delay until both sound together (start around 150–200 ms).";

    /// <summary>Tooltip of the volume slider.</summary>
    public const string VolumeTooltip =
        "Volume of the copy on this output (100 % = the same level as your headset; it is never boosted).";

    private readonly MainViewModel _owner;
    private string _name;
    private bool _isConnected;
    private string? _statusText;
    private bool _statusIsError;

    /// <summary>Creates a row.</summary>
    public MirrorTargetViewModel(MainViewModel owner, string deviceId, string name, bool isConnected)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        DeviceId = deviceId;
        _name = name;
        _isConnected = isConnected;
    }

    /// <summary>The playback endpoint id.</summary>
    public string DeviceId { get; }

    /// <summary>The device name as shown (with "(not connected)" while it is gone).</summary>
    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }

    /// <summary>The device is connected (only then can it be ticked).</summary>
    public bool IsConnected
    {
        get => _isConnected;
        internal set => SetProperty(ref _isConnected, value);
    }

    /// <summary>The copy plays on this device ("Also play on" ticked for it).</summary>
    public bool IsChecked
    {
        get => Stored?.Enabled == true;
        set
        {
            if (value == IsChecked)
            {
                return;
            }

            var target = Edit();
            target.Enabled = value;
            OnPropertyChanged();
            _owner.OnMirrorTargetsChanged();
        }
    }

    /// <summary>Volume of the copy, 0–100 %.</summary>
    public double Volume
    {
        get => Stored?.VolumePercent ?? MirrorTarget.MaxVolumePercent;
        set
        {
            int v = double.IsFinite(value) ? MirrorPlanner.ClampVolume((int)Math.Round(value, MidpointRounding.AwayFromZero)) : MirrorTarget.MaxVolumePercent;
            var target = Edit();
            if (target.VolumePercent == v)
            {
                return;
            }

            target.VolumePercent = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            _owner.OnMirrorTargetsChanged();
        }
    }

    /// <summary>"80 %".</summary>
    public string VolumeText => $"{Volume:0} %";

    /// <summary>Extra delay of the copy, 0–500 ms.</summary>
    public double DelayMs
    {
        get => Stored?.DelayMs ?? 0;
        set
        {
            int v = double.IsFinite(value) ? MirrorPlanner.ClampDelay((int)Math.Round(value, MidpointRounding.AwayFromZero)) : 0;
            var target = Edit();
            if (target.DelayMs == v)
            {
                return;
            }

            target.DelayMs = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayText));
            _owner.OnMirrorTargetsChanged();
        }
    }

    /// <summary>"180 ms".</summary>
    public string DelayText => $"{DelayMs:0} ms";

    /// <summary>Why the copy is not playing here (or null while all is well).</summary>
    public string? StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value);
    }

    /// <summary><see cref="StatusText"/> is a problem (shown in the warning colour).</summary>
    public bool StatusIsError
    {
        get => _statusIsError;
        internal set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>See <see cref="DelayTooltip"/>.</summary>
    public string DelayHint => DelayTooltip;

    /// <summary>See <see cref="VolumeTooltip"/>.</summary>
    public string VolumeHint => VolumeTooltip;

    private MirrorTarget? Stored => MirrorPlanner.Find(_owner.Settings, DeviceId);

    /// <summary>Re-reads the stored values (after a re-adoption or an outside change).</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(DelayMs));
        OnPropertyChanged(nameof(DelayText));
    }

    /// <summary>The stored entry, created (unticked) on first use; a connected device's name is kept current.</summary>
    private MirrorTarget Edit()
    {
        if (Stored is { } existing)
        {
            if (_isConnected)
            {
                existing.DeviceName = _name;
            }

            return existing;
        }

        var added = MirrorPlanner.GetOrAdd(_owner.Settings, DeviceId, _isConnected ? _name : null);
        added.Enabled = false;
        return added;
    }
}
