using AudioController.Core.Models;

namespace AudioController.Core.Equalizer;

/// <summary>
/// Pure rules for the Effects panel's state in <see cref="AppSettings"/>: band layouts, clamping, the migration from
/// the 1.4 ten-slider EQ (<see cref="AppSettings.EqGainsDb"/>) and the legacy mirror older versions read. No I/O.
/// </summary>
/// <remarks>
/// <para>10 adjustable bands (default 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 Hz, Q 1, ±24 dB): each centre
/// frequency can be edited (20–20 000 Hz, whole hertz) and the bands are always strictly increasing — an edit is
/// clamped between its neighbours (at least <see cref="MinBandSpacingHz"/> apart).</para>
/// <para>31 fixed bands: the ISO third-octave series 20 Hz … 20 kHz, Q 4.32 (a third of an octave).</para>
/// <para>Migration never discards user data: a library written before the Effects panel has no
/// <see cref="AppSettings.EqBands"/>, so they are filled from <see cref="AppSettings.EqGainsDb"/> on the default
/// frequencies. Afterwards <see cref="AppSettings.EqGainsDb"/> is kept equal to the 10 band gains so an older version
/// reading the same library keeps the user's EQ; when it disagrees with the bands (only an older code path writing
/// the array directly can cause that) the array wins and is copied into the bands.</para>
/// </remarks>
public static class FxSettings
{
    /// <summary>Number of adjustable bands.</summary>
    public const int TenBandCount = 10;

    /// <summary>Number of third-octave bands.</summary>
    public const int ThirdOctaveBandCount = 31;

    /// <summary>Quality factor of the 10 adjustable bands.</summary>
    public const double TenBandQ = 1.0;

    /// <summary>Quality factor of the third-octave bands: √(2^⅓) / (2^⅓ − 1) ≈ 4.32.</summary>
    public const double ThirdOctaveQ = 4.32;

    /// <summary>Lowest band gain in dB.</summary>
    public const float MinGainDb = -24f;

    /// <summary>Highest band gain in dB.</summary>
    public const float MaxGainDb = 24f;

    /// <summary>Lowest band centre frequency in Hz.</summary>
    public const double MinFrequencyHz = 20;

    /// <summary>Highest band centre frequency in Hz.</summary>
    public const double MaxFrequencyHz = 20000;

    /// <summary>Two adjustable bands are always at least this far apart (they stay strictly increasing).</summary>
    public const double MinBandSpacingHz = 1;

    private static readonly double[] TenBandDefaults = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private static readonly double[] ThirdOctave =
    [
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
        1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000,
    ];

    /// <summary>Default centre frequencies of the 10 adjustable bands (FXSound's layout).</summary>
    public static IReadOnlyList<double> DefaultTenBandFrequenciesHz { get; } = Array.AsReadOnly(TenBandDefaults);

    /// <summary>The 31 ISO third-octave centre frequencies, 20 Hz … 20 kHz.</summary>
    public static IReadOnlyList<double> ThirdOctaveFrequenciesHz { get; } = Array.AsReadOnly(ThirdOctave);

    /// <summary>The 10 adjustable bands on the default frequencies, with <paramref name="gainsDb"/> (missing = 0 dB).</summary>
    public static List<EqBand> DefaultTenBands(float[]? gainsDb = null)
    {
        var bands = new List<EqBand>(TenBandCount);
        for (int i = 0; i < TenBandCount; i++)
        {
            float g = gainsDb is not null && i < gainsDb.Length ? ClampGain(gainsDb[i]) : 0f;
            bands.Add(new EqBand(TenBandDefaults[i], g));
        }

        return bands;
    }

    /// <summary>−24..+24 dB; NaN = 0.</summary>
    public static float ClampGain(float gainDb) => float.IsNaN(gainDb) ? 0f : Math.Clamp(gainDb, MinGainDb, MaxGainDb);

    /// <summary>
    /// Where band <paramref name="index"/> of <paramref name="bands"/> may sit: rounded to whole hertz, within
    /// 20–20 000 Hz and strictly between its neighbours (at least <see cref="MinBandSpacingHz"/> away). NaN or infinity
    /// keeps the band's current frequency.
    /// </summary>
    public static double ClampFrequency(IReadOnlyList<EqBand> bands, int index, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, bands.Count);

        double current = bands[index].FrequencyHz;
        if (!double.IsFinite(frequencyHz))
        {
            frequencyHz = current;
        }

        double lo = index == 0 ? MinFrequencyHz : bands[index - 1].FrequencyHz + MinBandSpacingHz;
        double hi = index == bands.Count - 1 ? MaxFrequencyHz : bands[index + 1].FrequencyHz - MinBandSpacingHz;
        lo = Math.Max(MinFrequencyHz, Math.Ceiling(lo));
        hi = Math.Min(MaxFrequencyHz, Math.Floor(hi));
        if (lo > hi)
        {
            return current; // no room (cannot happen with normalized bands)
        }

        return Math.Clamp(Math.Round(frequencyHz, MidpointRounding.AwayFromZero), lo, hi);
    }

    /// <summary>
    /// Brings every Effects field of <paramref name="settings"/> into range and fills what is missing (in place):
    /// the migration from <see cref="AppSettings.EqGainsDb"/>, 10 strictly increasing bands, 31 third-octave gains,
    /// effect levels 0–100, a known <see cref="AppSettings.EqMode"/> (an unknown value becomes
    /// <see cref="AppSettings.DefaultEqMode"/>; a valid stored one is never changed), and the legacy mirror. Returns true
    /// when anything changed. Never resets user data that is valid.
    /// </summary>
    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool changed = false;

        // The 1.4 array: exactly 10 clamped gains. Only a complete array may overwrite the bands below.
        float[] legacy;
        bool legacyComplete = settings.EqGainsDb is { Length: TenBandCount };
        if (settings.EqGainsDb is { Length: TenBandCount } complete)
        {
            legacy = complete;
        }
        else
        {
            legacy = new float[TenBandCount];
            if (settings.EqGainsDb is { } partial)
            {
                Array.Copy(partial, legacy, Math.Min(legacy.Length, partial.Length));
            }

            changed = true;
        }

        for (int i = 0; i < legacy.Length; i++)
        {
            float g = ClampGain(legacy[i]);
            if (BitConverter.SingleToInt32Bits(g) != BitConverter.SingleToInt32Bits(legacy[i]))
            {
                legacy[i] = g;
                changed = true;
            }
        }

        settings.EqGainsDb = legacy;

        if (settings.EqBands is null)
        {
            // Migration: a library from before the Effects panel. Its 10 gains move onto the default frequencies. Such a
            // library never stored an EqMode, so it would open in the 31-band default; when its 10-slider EQ was not flat
            // it keeps the 10 bands instead, so the user's EQ still sounds the same (never silently dropped).
            settings.EqBands = DefaultTenBands(legacy);
            if (legacy.Any(static g => g != 0f) && settings.EqMode == AppSettings.DefaultEqMode)
            {
                settings.EqMode = EqMode.TenBandAdjustable;
            }

            changed = true;
        }
        else
        {
            changed |= NormalizeTenBands(settings.EqBands, out bool reordered);
            if (legacyComplete && !reordered) // after a repair-sort the array no longer lines up with the bands
            {
                changed |= AdoptLegacyGains(settings);
            }
        }

        settings.EqGains31Db = NormalizeThirdOctave(settings.EqGains31Db, ref changed);

        if (settings.Effects is null)
        {
            settings.Effects = new EffectLevels();
            changed = true;
        }
        else
        {
            var before = settings.Effects.Clone();
            if (!settings.Effects.Clamp().SameAs(before))
            {
                changed = true;
            }
        }

        if (!Enum.IsDefined(settings.EqMode))
        {
            settings.EqMode = AppSettings.DefaultEqMode;
            changed = true;
        }

        string? name = string.IsNullOrWhiteSpace(settings.FxPresetName) ? null : settings.FxPresetName.Trim();
        if (!string.Equals(name, settings.FxPresetName, StringComparison.Ordinal))
        {
            settings.FxPresetName = name;
            changed = true;
        }

        if (name is null && settings.FxPresetModified)
        {
            settings.FxPresetModified = false;
            changed = true;
        }

        changed |= MirrorLegacyGains(settings);
        return changed;
    }

    /// <summary>
    /// Copies <see cref="AppSettings.EqGainsDb"/> into the 10 band gains when they differ (something wrote the 1.4
    /// array directly). Returns true when a band changed.
    /// </summary>
    public static bool AdoptLegacyGains(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bands = settings.EqBands;
        var legacy = settings.EqGainsDb;
        if (bands is null || legacy is null)
        {
            return false;
        }

        bool changed = false;
        for (int i = 0; i < bands.Count && i < legacy.Length; i++)
        {
            float g = ClampGain(legacy[i]);
            if (BitConverter.SingleToInt32Bits(bands[i].GainDb) != BitConverter.SingleToInt32Bits(g))
            {
                bands[i].GainDb = g;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Keeps <see cref="AppSettings.EqGainsDb"/> equal to the 10 band gains (what an older version reads).
    /// Returns true when the array changed.
    /// </summary>
    public static bool MirrorLegacyGains(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bands = settings.EqBands;
        if (bands is null)
        {
            return false;
        }

        bool changed = false;
        if (settings.EqGainsDb is null || settings.EqGainsDb.Length != TenBandCount)
        {
            settings.EqGainsDb = new float[TenBandCount];
            changed = true;
        }

        for (int i = 0; i < TenBandCount; i++)
        {
            float g = i < bands.Count ? bands[i].GainDb : 0f;
            if (BitConverter.SingleToInt32Bits(settings.EqGainsDb[i]) != BitConverter.SingleToInt32Bits(g))
            {
                settings.EqGainsDb[i] = g;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Before the Effects state is applied: adopts a direct edit of the 1.4 array (see <see cref="AdoptLegacyGains"/>),
    /// then mirrors the bands back into it. Returns true when anything changed.
    /// </summary>
    public static bool SyncLegacyGains(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureNormalized(settings);
        bool changed = AdoptLegacyGains(settings);
        changed |= MirrorLegacyGains(settings);
        return changed;
    }

    /// <summary>Sets one adjustable band's gain (clamped) and its legacy mirror. Returns the applied gain.</summary>
    public static float SetTenBandGain(AppSettings settings, int index, float gainDb)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureNormalized(settings);
        float g = ClampGain(gainDb);
        settings.EqBands![index].GainDb = g;
        settings.EqGainsDb[index] = g;
        return g;
    }

    /// <summary>Moves one adjustable band (clamped between its neighbours). Returns the applied frequency.</summary>
    public static double SetTenBandFrequency(AppSettings settings, int index, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureNormalized(settings);
        double f = ClampFrequency(settings.EqBands!, index, frequencyHz);
        settings.EqBands![index].FrequencyHz = f;
        return f;
    }

    /// <summary>Sets one third-octave band's gain (clamped). Returns the applied gain.</summary>
    public static float SetThirdOctaveGain(AppSettings settings, int index, float gainDb)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureNormalized(settings);
        float g = ClampGain(gainDb);
        settings.EqGains31Db![index] = g;
        return g;
    }

    /// <summary>
    /// "Reset to Flat": every band gain (10 and 31) to 0 dB and every effect to 0 %. The EQ mode, the band frequencies
    /// and the power / EQ switches are kept. The preset becomes <see cref="FxPresets.FlatName"/>, unmodified.
    /// </summary>
    public static void ResetToFlat(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureNormalized(settings);
        foreach (var band in settings.EqBands!)
        {
            band.GainDb = 0f;
        }

        Array.Clear(settings.EqGains31Db!);
        settings.Effects = new EffectLevels();
        settings.FxPresetName = FxPresets.FlatName;
        settings.FxPresetModified = false;
        MirrorLegacyGains(settings);
    }

    /// <summary>A control changed: a loaded preset is now "(modified)".</summary>
    public static void MarkModified(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FxPresetName is not null)
        {
            settings.FxPresetModified = true;
        }
    }

    /// <summary>Normalizes when an Effects field is missing (callers may then index the bands and gains safely).</summary>
    public static void EnsureNormalized(AppSettings settings)
    {
        if (settings.EqBands is not { Count: TenBandCount } || settings.EqGains31Db is not { Length: ThirdOctaveBandCount }
            || settings.EqGainsDb is not { Length: TenBandCount } || settings.Effects is null)
        {
            Normalize(settings);
        }
    }

    /// <summary>
    /// Exactly 10 bands with finite gains in range and whole-hertz frequencies in 20–20 000 Hz, strictly increasing.
    /// Out-of-order bands (only possible in hand-edited data) are sorted by frequency, keeping each band's gain.
    /// </summary>
    internal static bool NormalizeTenBands(List<EqBand> bands) => NormalizeTenBands(bands, out _);

    /// <inheritdoc cref="NormalizeTenBands(List{EqBand})"/>
    /// <param name="bands">The bands (changed in place).</param>
    /// <param name="reordered">True when the bands had to be sorted.</param>
    internal static bool NormalizeTenBands(List<EqBand> bands, out bool reordered)
    {
        reordered = false;
        bool changed = bands.RemoveAll(static b => b is null) > 0;
        if (bands.Count > TenBandCount)
        {
            bands.RemoveRange(TenBandCount, bands.Count - TenBandCount);
            changed = true;
        }

        while (bands.Count < TenBandCount)
        {
            bands.Add(new EqBand(TenBandDefaults[bands.Count], 0f));
            changed = true;
        }

        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            float g = ClampGain(b.GainDb);
            if (BitConverter.SingleToInt32Bits(g) != BitConverter.SingleToInt32Bits(b.GainDb))
            {
                b.GainDb = g;
                changed = true;
            }

            double f = double.IsFinite(b.FrequencyHz) && b.FrequencyHz > 0 ? b.FrequencyHz : TenBandDefaults[i];
            f = Math.Clamp(Math.Round(f, MidpointRounding.AwayFromZero), MinFrequencyHz, MaxFrequencyHz);
            if (f != b.FrequencyHz)
            {
                b.FrequencyHz = f;
                changed = true;
            }
        }

        if (!IsStrictlyIncreasing(bands))
        {
            // Stable sort, then push duplicates apart: up from the bottom, then down from the top.
            var sorted = bands.Select((b, i) => (b, i)).OrderBy(t => t.b.FrequencyHz).ThenBy(t => t.i).Select(t => t.b).ToList();
            bands.Clear();
            bands.AddRange(sorted);
            for (int i = 1; i < bands.Count; i++)
            {
                bands[i].FrequencyHz = Math.Max(bands[i].FrequencyHz, bands[i - 1].FrequencyHz + MinBandSpacingHz);
            }

            bands[^1].FrequencyHz = Math.Min(bands[^1].FrequencyHz, MaxFrequencyHz);
            for (int i = bands.Count - 2; i >= 0; i--)
            {
                bands[i].FrequencyHz = Math.Min(bands[i].FrequencyHz, bands[i + 1].FrequencyHz - MinBandSpacingHz);
            }

            changed = true;
            reordered = true;
        }

        return changed;
    }

    private static bool IsStrictlyIncreasing(IReadOnlyList<EqBand> bands)
    {
        for (int i = 1; i < bands.Count; i++)
        {
            if (bands[i].FrequencyHz - bands[i - 1].FrequencyHz < MinBandSpacingHz)
            {
                return false;
            }
        }

        return true;
    }

    private static float[] NormalizeThirdOctave(float[]? gains, ref bool changed)
    {
        if (gains is null || gains.Length != ThirdOctaveBandCount)
        {
            var resized = new float[ThirdOctaveBandCount];
            if (gains is not null)
            {
                Array.Copy(gains, resized, Math.Min(resized.Length, gains.Length));
            }

            gains = resized;
            changed = true;
        }

        for (int i = 0; i < gains.Length; i++)
        {
            float g = ClampGain(gains[i]);
            if (BitConverter.SingleToInt32Bits(g) != BitConverter.SingleToInt32Bits(gains[i]))
            {
                gains[i] = g;
                changed = true;
            }
        }

        return gains;
    }
}
