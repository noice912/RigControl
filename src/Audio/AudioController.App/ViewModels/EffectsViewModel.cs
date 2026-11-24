using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;

namespace AudioController.App.ViewModels;

/// <summary>An entry of the preset drop-down: a built-in preset, a user preset, or the separator between them.</summary>
public sealed record PresetItem(string Name, bool IsUser, bool IsSeparator = false)
{
    /// <summary>The line between the built-in and the user presets (not selectable).</summary>
    public static PresetItem Separator { get; } = new("", false, true);

    public override string ToString() => Name;
}

/// <summary>One FXSound-style effect slider (0–100 %).</summary>
public sealed class EffectSliderViewModel : ObservableObject
{
    private readonly Action<EffectSliderViewModel> _changed;
    private int _value;

    public EffectSliderViewModel(string key, string label, string tooltip, Action<EffectSliderViewModel> changed)
    {
        Key = key;
        Label = label;
        Tooltip = tooltip;
        _changed = changed;
    }

    /// <summary>Which effect (<c>Fidelity</c>, <c>Ambience</c>, <c>Surround</c>, <c>DynamicBoost</c>, <c>Bass</c>).</summary>
    public string Key { get; }

    public string Label { get; }

    public string Tooltip { get; }

    public double Value
    {
        get => _value;
        set
        {
            int v = EffectLevels.ClampPercent((int)Math.Round(double.IsFinite(value) ? value : 0));
            if (v == _value) return;
            _value = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PercentText));
            _changed(this);
        }
    }

    public string PercentText => _value.ToString(CultureInfo.CurrentCulture) + " %";

    internal void SetSilently(int value)
    {
        if (value == _value) return;
        _value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(PercentText));
    }
}

/// <summary>One vertical EQ slider (−24..+24 dB, 0.5 dB steps); the 10-band ones also have an editable centre frequency.</summary>
public sealed class FxBandViewModel : ObservableObject
{
    private readonly Action<FxBandViewModel> _gainChanged;
    private readonly Func<FxBandViewModel, double, double>? _frequencyChanged;
    private double _gain;
    private double _frequencyHz;
    private string _frequencyText = "";
    private string? _frequencyError;

    public FxBandViewModel(int index, double frequencyHz, double gain, Action<FxBandViewModel> gainChanged,
        Func<FxBandViewModel, double, double>? frequencyChanged)
    {
        Index = index;
        _frequencyHz = frequencyHz;
        _frequencyText = FormatHz(frequencyHz);
        _gain = gain;
        _gainChanged = gainChanged;
        _frequencyChanged = frequencyChanged;
    }

    public int Index { get; }

    /// <summary>True for the 10 adjustable bands (the Hz box can be edited).</summary>
    public bool IsAdjustable => _frequencyChanged is not null;

    public double FrequencyHz => _frequencyHz;

    /// <summary>Short label: 31.5, 125, 1k, 2.5k, 16k.</summary>
    public string Label => ShortHz(_frequencyHz);

    public double Gain
    {
        get => _gain;
        set
        {
            value = double.IsFinite(value) ? value : 0;
            value = Math.Clamp(Math.Round(value * 2) / 2, FxSettings.MinGainDb, FxSettings.MaxGainDb);
            if (!SetProperty(ref _gain, value)) return;
            OnPropertyChanged(nameof(GainText));
            OnPropertyChanged(nameof(Tooltip));
            _gainChanged(this);
        }
    }

    public string GainText => (_gain > 0 ? "+" : "") + _gain.ToString("0.0", CultureInfo.CurrentCulture);

    public string Tooltip => $"{ShortHz(_frequencyHz)}Hz: {GainText} dB";

    /// <summary>
    /// The Hz box (committed on Enter / leaving the box). Accepts "1000", "1000 Hz", "1k", "1.5k" (either decimal
    /// separator); the band is clamped to 20–20 000 Hz and between its neighbours, and the box then shows the applied
    /// value. Text that is not a frequency is reverted (see <see cref="FrequencyError"/>).
    /// </summary>
    public string FrequencyText
    {
        get => _frequencyText;
        set
        {
            if (_frequencyChanged is null) return;
            if (!TryParseHz(value, out double hz))
            {
                FrequencyError = $"\"{value}\" is not a frequency — type a number of hertz such as 1000 or 1.5k.";
                _frequencyText = FormatHz(_frequencyHz);
                OnPropertyChanged();
                return;
            }

            double applied = _frequencyChanged(this, hz);
            FrequencyError = Math.Abs(applied - hz) > 0.5
                ? $"Kept between its neighbours and 20–20 000 Hz: {FormatHz(applied)} Hz."
                : null;
            SetFrequencySilently(applied);
            OnPropertyChanged(); // the box may hold different text than the applied value
        }
    }

    /// <summary>Why the last Hz entry was changed or refused, or null.</summary>
    public string? FrequencyError
    {
        get => _frequencyError;
        private set
        {
            if (!SetProperty(ref _frequencyError, value)) return;
            OnPropertyChanged(nameof(HasFrequencyError));
            OnPropertyChanged(nameof(FrequencyTooltip));
        }
    }

    /// <summary>The Hz box shows a warning outline (see <see cref="FrequencyError"/>).</summary>
    public bool HasFrequencyError => _frequencyError is not null;

    /// <summary>The Hz box's tooltip.</summary>
    public string FrequencyTooltip => _frequencyError
        ?? "Centre frequency in Hz (20–20 000, between its neighbours). Type a value such as 1000 or 1.5k, then Enter.";

    internal void SetGainSilently(double value)
    {
        _gain = value;
        OnPropertyChanged(nameof(Gain));
        OnPropertyChanged(nameof(GainText));
        OnPropertyChanged(nameof(Tooltip));
    }

    internal void SetFrequencySilently(double hz)
    {
        _frequencyHz = hz;
        _frequencyText = FormatHz(hz);
        OnPropertyChanged(nameof(FrequencyHz));
        OnPropertyChanged(nameof(FrequencyText));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>"1000", "1000 Hz", "1k", "1,5k", "1.5 kHz" → hertz. False for anything else.</summary>
    public static bool TryParseHz(string? text, out double hz)
    {
        hz = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (t.EndsWith("hz", StringComparison.Ordinal)) t = t[..^2];
        double multiplier = 1;
        if (t.EndsWith('k'))
        {
            multiplier = 1000;
            t = t[..^1];
        }

        t = t.Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v <= 0)
            return false;

        hz = v * multiplier;
        return true;
    }

    /// <summary>Whole hertz (one decimal below 100 Hz when needed): 32, 31.5, 1000, 16000.</summary>
    public static string FormatHz(double hz) =>
        hz.ToString(hz < 100 && hz % 1 != 0 ? "0.#" : "0", CultureInfo.InvariantCulture);

    /// <summary>Compact label: 20, 31.5, 125, 1k, 1.25k, 16k.</summary>
    public static string ShortHz(double hz) => hz >= 1000
        ? (hz / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "k"
        : hz.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>
/// The main window's Effects panel (FXSound-style): power, presets, five effect sliders and the graphic EQ (10
/// adjustable bands or 31 third-octave bands). Every change goes through <see cref="MainViewModel.ApplyEq"/>, which
/// drives the soundboard bus, the System EQ router and — debounced — Equalizer APO on the headset. The microphone is
/// never equalized.
/// </summary>
public sealed class EffectsViewModel : ObservableObject
{
    /// <summary>The caption under the panel.</summary>
    public const string Caption =
        "Applies to all PC audio on your headset through Equalizer APO, and to soundboard sounds. Your microphone is never equalized.";

    /// <summary>Tooltip of Dynamic Boost (what it really is).</summary>
    public const string DynamicBoostTooltip =
        "Dynamic Boost: a loudness contour — lifts the lows (up to +6 dB at 100 Hz) and highs (up to +4 dB from 8 kHz) " +
        "the way your ears lose them at low volume. It is not a compressor (Equalizer APO has none).";

    private readonly MainViewModel _main;
    private readonly Library _library;
    private PresetItem? _selectedPreset;
    private bool _refreshing;
    private bool _isSavingPreset;
    private string _newPresetName = "";
    private string? _presetError;

    public EffectsViewModel(MainViewModel main, Library library)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        FxSettings.Normalize(S);

        EffectSliders = new ObservableCollection<EffectSliderViewModel>
        {
            new("Fidelity", "Fidelity",
                "Fidelity: restores clarity and detail — a high shelf from 6 kHz (up to +8 dB) plus an air peak at 12 kHz (up to +4 dB).",
                OnEffectChanged),
            new("Ambience", "Ambience",
                "Ambience: a light room reverb (0.35 s). On your headset it is Equalizer APO convolving with ambience.wav; on soundboard sounds a small built-in reverb.",
                OnEffectChanged),
            new("Surround", "Surround Sound",
                "Surround Sound: widens the stereo image (the left/right difference up to ×2). Stereo only; mono sounds are unchanged.",
                OnEffectChanged),
            new("DynamicBoost", "Dynamic Boost", DynamicBoostTooltip, OnEffectChanged),
            new("Bass", "Bass Boost",
                "Bass Boost: a low shelf at 100 Hz, up to +12 dB.",
                OnEffectChanged),
        };

        TenBands = new ObservableCollection<FxBandViewModel>(
            S.EqBands!.Select((b, i) => new FxBandViewModel(i, b.FrequencyHz, b.GainDb, OnTenBandGainChanged, OnTenBandFrequencyChanged)));
        ThirdOctaveBands = new ObservableCollection<FxBandViewModel>(
            FxSettings.ThirdOctaveFrequenciesHz.Select((f, i) => new FxBandViewModel(i, f, S.EqGains31Db![i], OnThirdOctaveGainChanged, null)));
        Presets = new ObservableCollection<PresetItem>();

        SaveAsCommand = new RelayCommand(BeginSaveAs);
        ConfirmSaveCommand = new RelayCommand(ConfirmSave);
        CancelSaveCommand = new RelayCommand(() =>
        {
            IsSavingPreset = false;
            PresetError = null;
        });
        DeletePresetCommand = new RelayCommand(DeleteSelectedPreset, () => SelectedPreset is { IsUser: true });
        ResetFlatCommand = new RelayCommand(ResetToFlat);

        RebuildPresets();
        Refresh();
    }

    private AppSettings S => _library.Settings;

    public MainViewModel Main => _main;

    public string CaptionText => Caption;

    public ObservableCollection<EffectSliderViewModel> EffectSliders { get; }

    public ObservableCollection<FxBandViewModel> TenBands { get; }

    public ObservableCollection<FxBandViewModel> ThirdOctaveBands { get; }

    public ObservableCollection<PresetItem> Presets { get; }

    public ICommand SaveAsCommand { get; }

    public ICommand ConfirmSaveCommand { get; }

    public ICommand CancelSaveCommand { get; }

    public ICommand DeletePresetCommand { get; }

    public ICommand ResetFlatCommand { get; }

    /// <summary>The power button: off = nothing applied anywhere (EQ and effects neutral).</summary>
    public bool FxEnabled
    {
        get => S.FxEnabled;
        set
        {
            if (value == S.FxEnabled) return;
            S.FxEnabled = value;
            OnPropertyChanged();
            _main.ApplyEq();
        }
    }

    /// <summary>The graphic EQ's own switch (the effects follow the power button only).</summary>
    public bool EqEnabled
    {
        get => S.EqEnabled;
        set
        {
            if (value == S.EqEnabled) return;
            S.EqEnabled = value;
            OnPropertyChanged();
            _main.ApplyEq();
        }
    }

    /// <summary>31 fixed third-octave bands instead of the 10 adjustable ones.</summary>
    public bool IsThirdOctave
    {
        get => S.EqMode == EqMode.ThirtyOneBandThirdOctave;
        set
        {
            var mode = value ? EqMode.ThirtyOneBandThirdOctave : EqMode.TenBandAdjustable;
            if (mode == S.EqMode) return;
            S.EqMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTenBand));
            Changed();
        }
    }

    /// <summary>The 10 adjustable bands are shown (the inverse of <see cref="IsThirdOctave"/>).</summary>
    public bool IsTenBand
    {
        get => !IsThirdOctave;
        set => IsThirdOctave = !value;
    }

    /// <summary>The selected preset (null = "Custom"). Selecting one loads it.</summary>
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is { IsSeparator: true }) return;
            if (Equals(value, _selectedPreset)) return;
            _selectedPreset = value;
            OnPropertyChanged();
            RelayCommand.Refresh();
            if (_refreshing || value is null) return;
            LoadPreset(value.Name);
        }
    }

    /// <summary>"Rock", "Rock (modified)" or "Custom".</summary>
    public string PresetDisplayName => S.FxPresetName is null
        ? "Custom"
        : S.FxPresetName + (S.FxPresetModified ? " (modified)" : "");

    /// <summary>A control changed since the preset was loaded.</summary>
    public bool IsPresetModified => S.FxPresetName is not null && S.FxPresetModified;

    /// <summary>No preset is current.</summary>
    public bool IsCustom => S.FxPresetName is null;

    /// <summary>The inline "Save as…" row is open.</summary>
    public bool IsSavingPreset
    {
        get => _isSavingPreset;
        set => SetProperty(ref _isSavingPreset, value);
    }

    public string NewPresetName
    {
        get => _newPresetName;
        set
        {
            if (SetProperty(ref _newPresetName, value ?? "")) PresetError = null;
        }
    }

    /// <summary>Why the preset could not be saved, or null.</summary>
    public string? PresetError
    {
        get => _presetError;
        private set => SetProperty(ref _presetError, value);
    }

    /// <summary>Loads a preset by name (built-in or the user's) and applies it.</summary>
    public void LoadPreset(string name)
    {
        var preset = FxPresets.Find(name, _library.UserPresets);
        if (preset is null) return;
        FxPresets.Apply(S, preset);
        Refresh();
        _main.ApplyEq();
    }

    /// <summary>"Reset to Flat": every band 0 dB and every effect 0 %.</summary>
    public void ResetToFlat()
    {
        FxSettings.ResetToFlat(S);
        Refresh();
        _main.ApplyEq();
    }

    /// <summary>Re-reads every control from the settings (after a preset load, a reset or an external change).</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            FxSettings.EnsureNormalized(S);
            var e = S.Effects;
            foreach (var slider in EffectSliders)
            {
                slider.SetSilently(slider.Key switch
                {
                    "Fidelity" => e.Fidelity,
                    "Ambience" => e.Ambience,
                    "Surround" => e.Surround,
                    "DynamicBoost" => e.DynamicBoost,
                    _ => e.Bass,
                });
            }

            for (int i = 0; i < TenBands.Count; i++)
            {
                TenBands[i].SetGainSilently(S.EqBands![i].GainDb);
                TenBands[i].SetFrequencySilently(S.EqBands![i].FrequencyHz);
            }

            for (int i = 0; i < ThirdOctaveBands.Count; i++)
            {
                ThirdOctaveBands[i].SetGainSilently(S.EqGains31Db![i]);
            }

            SelectedPreset = S.FxPresetName is { } name
                ? Presets.FirstOrDefault(p => !p.IsSeparator && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        finally
        {
            _refreshing = false;
        }

        OnPropertyChanged(nameof(FxEnabled));
        OnPropertyChanged(nameof(EqEnabled));
        OnPropertyChanged(nameof(IsThirdOctave));
        OnPropertyChanged(nameof(IsTenBand));
        NotifyPresetState();
    }

    private void BeginSaveAs()
    {
        NewPresetName = SelectedPreset is { IsUser: true } p ? p.Name : "";
        PresetError = null;
        IsSavingPreset = true;
    }

    private void ConfirmSave()
    {
        if (FxPresets.SaveUserPreset(_library, NewPresetName) is { } error)
        {
            PresetError = error;
            return;
        }

        IsSavingPreset = false;
        PresetError = null;
        RebuildPresets();
        Refresh();
        _main.MarkDirty();
        _main.ShowStatus($"Preset \"{S.FxPresetName}\" saved.");
    }

    private void DeleteSelectedPreset()
    {
        if (SelectedPreset is not { IsUser: true } p) return;
        if (!_main.ConfirmWithUser("Delete preset", $"Delete your preset \"{p.Name}\"? Your current sound stays as it is.")) return;
        if (!FxPresets.DeleteUserPreset(_library, p.Name)) return;
        RebuildPresets();
        Refresh();
        _main.MarkDirty();
    }

    private void RebuildPresets()
    {
        _refreshing = true;
        try
        {
            Presets.Clear();
            foreach (var p in FxPresets.BuiltIn)
            {
                Presets.Add(new PresetItem(p.Name, false));
            }

            var user = _library.UserPresets ?? new List<FxPreset>();
            if (user.Count > 0)
            {
                Presets.Add(PresetItem.Separator);
                foreach (var p in user.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Presets.Add(new PresetItem(p.Name, true));
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnEffectChanged(EffectSliderViewModel slider)
    {
        if (_refreshing) return;
        var e = S.Effects;
        int v = (int)slider.Value;
        switch (slider.Key)
        {
            case "Fidelity": e.Fidelity = v; break;
            case "Ambience": e.Ambience = v; break;
            case "Surround": e.Surround = v; break;
            case "DynamicBoost": e.DynamicBoost = v; break;
            default: e.Bass = v; break;
        }

        Changed();
    }

    private void OnTenBandGainChanged(FxBandViewModel band)
    {
        if (_refreshing) return;
        FxSettings.SetTenBandGain(S, band.Index, (float)band.Gain);
        Changed();
    }

    private double OnTenBandFrequencyChanged(FxBandViewModel band, double hz)
    {
        double before = S.EqBands![band.Index].FrequencyHz;
        double applied = FxSettings.SetTenBandFrequency(S, band.Index, hz);
        if (applied != before && !_refreshing) Changed();
        return applied;
    }

    private void OnThirdOctaveGainChanged(FxBandViewModel band)
    {
        if (_refreshing) return;
        FxSettings.SetThirdOctaveGain(S, band.Index, (float)band.Gain);
        Changed();
    }

    /// <summary>A preset-relevant control changed: mark "(modified)" and apply.</summary>
    private void Changed()
    {
        FxSettings.MarkModified(S);
        NotifyPresetState();
        _main.ApplyEq();
    }

    private void NotifyPresetState()
    {
        OnPropertyChanged(nameof(PresetDisplayName));
        OnPropertyChanged(nameof(IsPresetModified));
        OnPropertyChanged(nameof(IsCustom));
        RelayCommand.Refresh();
    }
}
