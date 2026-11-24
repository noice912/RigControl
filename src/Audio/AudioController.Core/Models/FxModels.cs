namespace AudioController.Core.Models;

/// <summary>
/// One band of the graphic EQ: its centre frequency and gain. Stored in the encrypted library (the 10 adjustable
/// bands in <see cref="AppSettings.EqBands"/>, and every band of a <see cref="FxPreset"/>).
/// </summary>
public sealed class EqBand
{
    /// <summary>Creates a 0 dB band at 0 Hz (for the JSON serializer).</summary>
    public EqBand()
    {
    }

    /// <summary>Creates a band.</summary>
    public EqBand(double frequencyHz, float gainDb)
    {
        FrequencyHz = frequencyHz;
        GainDb = gainDb;
    }

    /// <summary>Centre frequency in Hz (20–20 000).</summary>
    public double FrequencyHz { get; set; }

    /// <summary>Gain in dB (−24..+24).</summary>
    public float GainDb { get; set; }

    /// <summary>A copy of this band.</summary>
    public EqBand Clone() => new(FrequencyHz, GainDb);
}

/// <summary>Which graphic EQ the Effects panel shows and applies.</summary>
public enum EqMode
{
    /// <summary>FXSound's 10 bands (32 Hz … 16 kHz, Q 1), each centre frequency editable (the default).</summary>
    TenBandAdjustable,

    /// <summary>31 ISO third-octave bands, 20 Hz … 20 kHz, Q 4.32, fixed frequencies.</summary>
    ThirtyOneBandThirdOctave,
}

/// <summary>
/// The five FXSound-style effect sliders, each 0–100 % (0 = off). Applied through Equalizer APO on the headset and
/// on the soundboard bus — never on the microphone.
/// </summary>
public sealed class EffectLevels
{
    /// <summary>Highest slider value.</summary>
    public const int MaxPercent = 100;

    /// <summary>Bass Boost: a low shelf at 100 Hz, up to +12 dB.</summary>
    public int Bass { get; set; }

    /// <summary>Fidelity: a high shelf at 6 kHz (up to +8 dB) plus a 12 kHz peak (up to +4 dB).</summary>
    public int Fidelity { get; set; }

    /// <summary>Ambience: a short, light reverb (wet up to 0.35).</summary>
    public int Ambience { get; set; }

    /// <summary>Surround Sound: stereo widening (side gain up to ×2).</summary>
    public int Surround { get; set; }

    /// <summary>Dynamic Boost: a loudness contour (low shelf up to +6 dB, high shelf up to +4 dB) — not a compressor.</summary>
    public int DynamicBoost { get; set; }

    /// <summary>True when every slider is at 0 %.</summary>
    public bool IsZero => Bass == 0 && Fidelity == 0 && Ambience == 0 && Surround == 0 && DynamicBoost == 0;

    /// <summary>A copy of these levels.</summary>
    public EffectLevels Clone() => new()
    {
        Bass = Bass,
        Fidelity = Fidelity,
        Ambience = Ambience,
        Surround = Surround,
        DynamicBoost = DynamicBoost,
    };

    /// <summary>Clamps every slider to 0–100 (in place) and returns this instance.</summary>
    public EffectLevels Clamp()
    {
        Bass = ClampPercent(Bass);
        Fidelity = ClampPercent(Fidelity);
        Ambience = ClampPercent(Ambience);
        Surround = ClampPercent(Surround);
        DynamicBoost = ClampPercent(DynamicBoost);
        return this;
    }

    /// <summary>True when both hold the same five values.</summary>
    public bool SameAs(EffectLevels? other) =>
        other is not null && Bass == other.Bass && Fidelity == other.Fidelity && Ambience == other.Ambience
        && Surround == other.Surround && DynamicBoost == other.DynamicBoost;

    /// <summary>0–100.</summary>
    public static int ClampPercent(int value) => Math.Clamp(value, 0, MaxPercent);
}

/// <summary>
/// A saved Effects panel state: EQ mode and bands plus the five effect levels. The built-in presets are data in
/// <see cref="Equalizer.FxPresets"/>; the user's own are stored in <see cref="Library.UserPresets"/>.
/// </summary>
public sealed class FxPreset
{
    /// <summary>The name shown in the preset list (unique, case-insensitive).</summary>
    public string Name { get; set; } = "";

    /// <summary>Which EQ the bands belong to.</summary>
    public EqMode EqMode { get; set; } = EqMode.TenBandAdjustable;

    /// <summary>10 adjustable bands, or the 31 third-octave bands.</summary>
    public List<EqBand> Bands { get; set; } = new();

    /// <summary>The five effect levels.</summary>
    public EffectLevels Effects { get; set; } = new();

    /// <summary>A deep copy.</summary>
    public FxPreset Clone() => new()
    {
        Name = Name,
        EqMode = EqMode,
        Bands = Bands.Select(b => b.Clone()).ToList(),
        Effects = Effects.Clone(),
    };
}
