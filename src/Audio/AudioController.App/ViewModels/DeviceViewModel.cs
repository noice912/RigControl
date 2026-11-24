using System.Diagnostics;
using System.Windows.Input;
using AudioController.Core.Models;

namespace AudioController.App.ViewModels;

/// <summary>One endpoint row in the device panel (Outputs, Inputs or Headphones).</summary>
public sealed class DeviceViewModel : ObservableObject
{
    public const string SkullcandyNote =
        "ANC modes and Sensory Bass are set in the Skull-iQ mobile app — Skullcandy publishes no desktop API.";

    private readonly MainViewModel _owner;
    private readonly object _volumeSync = new();
    private AudioDeviceInfo _info;
    private double _volume;
    private bool _muted;
    private bool _enhancements;
    private string _batteryText = "…";
    private bool _suppress;
    private float? _pendingVolume;
    private bool _volumeWorkerRunning;
    private long _lastUserVolumeTicks;

    public DeviceViewModel(MainViewModel owner, AudioDeviceInfo info, bool isHeadphoneRow)
    {
        _owner = owner;
        _info = info;
        IsHeadphoneRow = isHeadphoneRow;
        MakeDefaultCommand = new RelayCommand(() => _owner.MakeDefault(this), () => IsActive);
        Update(info);
    }

    public ICommand MakeDefaultCommand { get; }

    public string Id => _info.Id;

    public AudioDeviceInfo Info => _info;

    public DeviceKind Kind => _info.Kind;

    public string Name => _info.FriendlyName;

    public bool IsHeadphoneRow { get; }

    public bool IsActive => _info.State == DeviceActivity.Active;

    public bool IsBluetooth => _info.IsBluetooth;

    public bool IsDefault => _info.IsDefault;

    public bool IsDefaultCommunications => _info.IsDefaultCommunications;

    public string StateText => _info.State switch
    {
        DeviceActivity.Active => IsHeadphoneRow ? "Connected" : "Active",
        DeviceActivity.Disabled => "Disabled",
        DeviceActivity.Unplugged => IsHeadphoneRow ? "Disconnected" : "Unplugged",
        DeviceActivity.NotPresent => "Not present",
        _ => _info.State.ToString(),
    };

    /// <summary>"Default", "Default · Comms", "Comms" or empty.</summary>
    public string RoleText => (_info.IsDefault, _info.IsDefaultCommunications) switch
    {
        (true, true) => "Default · Comms",
        (true, false) => "Default",
        (false, true) => "Comms",
        _ => "",
    };

    public bool IsSkullcandy =>
        _info.FriendlyName.Contains("Crusher", StringComparison.OrdinalIgnoreCase)
        || _info.FriendlyName.Contains("Skullcandy", StringComparison.OrdinalIgnoreCase);

    public bool ShowSkullcandyNote => IsHeadphoneRow && IsSkullcandy;

    public bool EnhancementsSupported => _info.EnhancementsEnabled is not null;

    /// <summary>0–100 master volume.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (!SetProperty(ref _volume, value)) return;
            OnPropertyChanged(nameof(VolumeText));
            if (_suppress) return;

            _lastUserVolumeTicks = Stopwatch.GetTimestamp();
            QueueVolume((float)(value / 100.0));
        }
    }

    public string VolumeText => $"{_volume:0}%";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value) || _suppress) return;
            _owner.SetMute(this, value);
        }
    }

    public bool EnhancementsEnabled
    {
        get => _enhancements;
        set
        {
            if (!SetProperty(ref _enhancements, value) || _suppress) return;
            _owner.SetEnhancements(this, value);
        }
    }

    /// <summary>Battery percentage text for headphone rows ("85%"), "—" when unknown.</summary>
    public string BatteryText
    {
        get => _batteryText;
        set => SetProperty(ref _batteryText, value);
    }

    public string Tooltip =>
        $"{_info.FriendlyName}\n{(_info.Kind == DeviceKind.Playback ? "Playback" : "Recording")} device · {StateText}" +
        (_info.IsBluetooth ? " · Bluetooth" : "") +
        (_info.EnhancementsEnabled is null ? "\nAudio Enhancements: status not available" : $"\nAudio Enhancements: {(_info.EnhancementsEnabled == true ? "on" : "off")}");

    /// <summary>Applies a fresh snapshot without echoing the values back to Windows.</summary>
    public void Update(AudioDeviceInfo info)
    {
        _info = info;
        _suppress = true;
        try
        {
            // Don't yank the slider while the user is dragging it (the snapshot may predate the drag).
            bool userIsDragging = Stopwatch.GetElapsedTime(_lastUserVolumeTicks) < TimeSpan.FromSeconds(1.5);
            if (!userIsDragging)
                Volume = Math.Round(info.Volume * 100.0);

            Muted = info.Muted;
            EnhancementsEnabled = info.EnhancementsEnabled == true;
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsBluetooth));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsDefaultCommunications));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(IsSkullcandy));
        OnPropertyChanged(nameof(ShowSkullcandyNote));
        OnPropertyChanged(nameof(EnhancementsSupported));
        OnPropertyChanged(nameof(Tooltip));
        RelayCommand.Refresh();
    }

    /// <summary>Restores a value Windows refused, without calling back into Windows.</summary>
    internal void RevertMuted(bool value)
    {
        _suppress = true;
        try { Muted = value; }
        finally { _suppress = false; }
    }

    internal void RevertEnhancements(bool value)
    {
        _suppress = true;
        try { EnhancementsEnabled = value; }
        finally { _suppress = false; }
    }

    // Slider drags produce many values; apply only the latest, one at a time, off the UI thread.
    private void QueueVolume(float scalar)
    {
        lock (_volumeSync)
        {
            _pendingVolume = scalar;
            if (_volumeWorkerRunning) return;
            _volumeWorkerRunning = true;
        }

        Task.Run(() =>
        {
            while (true)
            {
                float next;
                lock (_volumeSync)
                {
                    if (_pendingVolume is null)
                    {
                        _volumeWorkerRunning = false;
                        return;
                    }

                    next = _pendingVolume.Value;
                    _pendingVolume = null;
                }

                _owner.ApplyVolume(this, next);
            }
        });
    }
}
