using System.Globalization;
using System.IO;
using System.Windows.Input;
using AudioController.Core.Models;

namespace AudioController.App.ViewModels;

/// <summary>Play-mode choice for the editor's ComboBox.</summary>
public sealed record PlayModeOption(PlayMode Value, string Label, string Description)
{
    public static IReadOnlyList<PlayModeOption> All { get; } = new[]
    {
        new PlayModeOption(PlayMode.OneShot, "One-shot", "Each press plays the sound from the start; presses can overlap."),
        new PlayModeOption(PlayMode.Toggle, "Toggle", "Press to start, press again to stop."),
        new PlayModeOption(PlayMode.HoldToPlay, "Hold to play",
            "Plays while the button is held down. With a global hotkey, Windows reports no key release, so the sound " +
            "stops about 0.5 s after you let go (the app watches keyboard auto-repeat)."),
    };
}

/// <summary>One sound slot button; wraps the persisted <see cref="SoundSlot"/> model directly.</summary>
public sealed class SlotViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private string? _hotkeyConflict;
    private bool _isPlaying;
    private bool _isSelected;

    public SlotViewModel(MainViewModel owner, BoardViewModel board, SoundSlot model)
    {
        _owner = owner;
        Board = board;
        Model = model;

        PlayCommand = new RelayCommand(() => _owner.ActivateSlot(this));
        PreviewCommand = new RelayCommand(() => _owner.PreviewSlot(this));
        DeleteCommand = new RelayCommand(() => _owner.DeleteSlot(this));
        SetColorCommand = new RelayCommand(p =>
        {
            if (p is string hex) ColorHex = hex;
        });
        ClearHotkeyCommand = new RelayCommand(() => Hotkey = null, () => Hotkey is not null);
    }

    public SoundSlot Model { get; }

    public BoardViewModel Board { get; internal set; }

    public Guid Id => Model.Id;

    public ICommand PlayCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand SetColorCommand { get; }

    public ICommand ClearHotkeyCommand { get; }

    /// <summary>Set while a HoldToPlay press (mouse or hotkey) is down.</summary>
    internal bool HoldActive { get; set; }

    /// <summary>Set while the sound is being loaded from the vault for a start request.</summary>
    internal bool IsStarting { get; set; }

    public string Name
    {
        get => Model.Name;
        set
        {
            value ??= "";
            if (Model.Name == value) return;
            Model.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            _owner.OnSlotEdited(this);
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Model.Name) ? "(unnamed)" : Model.Name;

    public string ColorHex
    {
        get => Model.ColorHex;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Model.ColorHex, value, StringComparison.OrdinalIgnoreCase)) return;
            Model.ColorHex = value;
            OnPropertyChanged();
            _owner.OnSlotEdited(this);
        }
    }

    /// <summary>Gesture text ("Ctrl+Alt+D1") or null. Changing it re-registers global hotkeys.</summary>
    public string? Hotkey
    {
        get => Model.Hotkey;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Model.Hotkey == v) return;
            Model.Hotkey = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyDisplay));
            OnPropertyChanged(nameof(Caption));
            _owner.OnSlotHotkeyChanged(this);
        }
    }

    /// <summary>Why the hotkey could not be registered (null when it works or there is none).</summary>
    public string? HotkeyConflict
    {
        get => _hotkeyConflict;
        set
        {
            if (!SetProperty(ref _hotkeyConflict, value)) return;
            OnPropertyChanged(nameof(HotkeyDisplay));
            OnPropertyChanged(nameof(Caption));
            OnPropertyChanged(nameof(HasHotkeyConflict));
        }
    }

    public bool HasHotkeyConflict => _hotkeyConflict is not null;

    /// <summary>The gesture, marked "(conflict)" when it could not be registered.</summary>
    public string HotkeyDisplay => Model.Hotkey is null ? "" : _hotkeyConflict is null ? Model.Hotkey : Model.Hotkey + " (conflict)";

    /// <summary>0–200 (%), stored as 0.0–2.0.</summary>
    public double VolumePercent
    {
        get => Math.Round(Model.Volume * 100.0);
        set
        {
            if (double.IsNaN(value)) return;
            float v = (float)(Math.Clamp(Math.Round(value), 0, 200) / 100.0);
            if (Math.Abs(Model.Volume - v) < 0.0001f) return;
            Model.Volume = v;
            OnPropertyChanged();
            _owner.OnSlotEdited(this);
        }
    }

    public PlayMode PlayMode
    {
        get => Model.PlayMode;
        set
        {
            if (Model.PlayMode == value) return;
            Model.PlayMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayModeLabel));
            _owner.OnSlotPlayModeChanged(this);
        }
    }

    public string PlayModeLabel => PlayModeOption.All.FirstOrDefault(o => o.Value == Model.PlayMode)?.Label ?? "";

    public string OriginalFileName => string.IsNullOrWhiteSpace(Model.OriginalFileName) ? "—" : Model.OriginalFileName;

    public string DurationText => Model.DurationSeconds.ToString("0.000", CultureInfo.CurrentCulture) + " s";

    public string SizeText => FormatBytes(Model.OriginalSizeBytes);

    /// <summary>Second line on the tile: short duration plus the hotkey.</summary>
    public string Caption
    {
        get
        {
            var duration = Model.DurationSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s";
            return Model.Hotkey is null ? duration : duration + "  ·  " + HotkeyDisplay;
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Replaces an empty name (left behind by editing) with the file name.</summary>
    internal void NormalizeName()
    {
        if (!string.IsNullOrWhiteSpace(Model.Name))
        {
            var trimmed = Model.Name.Trim();
            if (trimmed != Model.Name) Name = trimmed;
            return;
        }

        var fallback = Path.GetFileNameWithoutExtension(Model.OriginalFileName);
        Name = string.IsNullOrWhiteSpace(fallback) ? "Sound" : fallback;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : size.ToString(size >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
