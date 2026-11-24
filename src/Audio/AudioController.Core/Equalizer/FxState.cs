using AudioController.Core.Models;

namespace AudioController.Core.Equalizer;

/// <summary>A biquad's shape: Equalizer APO's <c>PK</c>, <c>LS</c> and <c>HS</c> (the RBJ cookbook filters).</summary>
public enum FxFilterType
{
    /// <summary>Peaking EQ (<c>PK</c>; NAudio <c>BiQuadFilter.PeakingEQ</c>).</summary>
    Peaking,

    /// <summary>Low shelf (<c>LS</c>; NAudio <c>BiQuadFilter.LowShelf</c>), centre (half-gain) frequency.</summary>
    LowShelf,

    /// <summary>High shelf (<c>HS</c>; NAudio <c>BiQuadFilter.HighShelf</c>), centre (half-gain) frequency.</summary>
    HighShelf,
}

/// <summary>
/// One filter of the Effects chain, exactly as it is written to Equalizer APO and built in the app's own soundboard
/// bus, so both paths use the same parameters.
/// </summary>
public readonly record struct FxFilter(FxFilterType Type, double FrequencyHz, double GainDb, double Q);

/// <summary>
/// The five FXSound-style effects as filters and parameters (pure). Each slider runs 0–100 %; gains are rounded to
/// 0.001 dB and widths to 0.0001 so the Equalizer APO file and the in-app chain use identical numbers.
/// </summary>
public static class FxEffects
{
    /// <summary>
    /// Q of every shelf: 1/√2 to four places — the RBJ shelf with slope S = 1 (NAudio's <c>LowShelf/HighShelf(…, 1, …)</c>),
    /// the steepest shelf without overshoot. Written as <c>Q 0.7071</c> (Equalizer APO accepts Q on LS/HS since 1.2.1).
    /// </summary>
    public const double ShelfQ = 0.7071;

    /// <summary>Bass Boost: low shelf centre in Hz.</summary>
    public const double BassShelfHz = 100;

    /// <summary>Bass Boost at 100 %: +12 dB.</summary>
    public const double BassMaxDb = 12;

    /// <summary>Fidelity: high shelf centre in Hz.</summary>
    public const double FidelityShelfHz = 6000;

    /// <summary>Fidelity at 100 %: high shelf +8 dB.</summary>
    public const double FidelityShelfMaxDb = 8;

    /// <summary>Fidelity: "air" peak centre in Hz.</summary>
    public const double FidelityPeakHz = 12000;

    /// <summary>Fidelity: Q of the air peak.</summary>
    public const double FidelityPeakQ = 1;

    /// <summary>Fidelity at 100 %: air peak +4 dB.</summary>
    public const double FidelityPeakMaxDb = 4;

    /// <summary>Dynamic Boost: loudness-contour low shelf centre in Hz.</summary>
    public const double DynamicLowHz = 100;

    /// <summary>Dynamic Boost at 100 %: low shelf +6 dB.</summary>
    public const double DynamicLowMaxDb = 6;

    /// <summary>Dynamic Boost: loudness-contour high shelf centre in Hz.</summary>
    public const double DynamicHighHz = 8000;

    /// <summary>Dynamic Boost at 100 %: high shelf +4 dB.</summary>
    public const double DynamicHighMaxDb = 4;

    /// <summary>Surround at 100 %: stereo width k = 0.5 (L' = 1.5·L − 0.5·R; the side signal ×2).</summary>
    public const double SurroundMaxWidth = 0.5;

    /// <summary>Ambience at 100 %: the reverb tail's level (energy relative to the dry signal = wet²).</summary>
    public const double AmbienceMaxWet = 0.35;

    /// <summary>Bass Boost gain in dB for a slider value.</summary>
    public static double BassDb(int percent) => Scale(percent, BassMaxDb);

    /// <summary>Fidelity high-shelf gain in dB.</summary>
    public static double FidelityShelfDb(int percent) => Scale(percent, FidelityShelfMaxDb);

    /// <summary>Fidelity air-peak gain in dB.</summary>
    public static double FidelityPeakDb(int percent) => Scale(percent, FidelityPeakMaxDb);

    /// <summary>Dynamic Boost low-shelf gain in dB.</summary>
    public static double DynamicLowDb(int percent) => Scale(percent, DynamicLowMaxDb);

    /// <summary>Dynamic Boost high-shelf gain in dB.</summary>
    public static double DynamicHighDb(int percent) => Scale(percent, DynamicHighMaxDb);

    /// <summary>Surround width k = pct/100 × 0.5 (0 = off).</summary>
    public static double SurroundWidth(int percent) =>
        Math.Round(EffectLevels.ClampPercent(percent) / 100.0 * SurroundMaxWidth, 4, MidpointRounding.AwayFromZero);

    /// <summary>Ambience wet level = pct/100 × 0.35 (0 = off).</summary>
    public static double AmbienceWet(int percent) =>
        Math.Round(EffectLevels.ClampPercent(percent) / 100.0 * AmbienceMaxWet, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The filters of Bass Boost, Fidelity and Dynamic Boost in chain order (Bass LS; Fidelity HS + PK; Dynamic LS + HS),
    /// leaving out every filter whose gain is 0.
    /// </summary>
    public static IReadOnlyList<FxFilter> Filters(EffectLevels? effects)
    {
        var list = new List<FxFilter>(5);
        if (effects is null)
        {
            return list;
        }

        Add(list, FxFilterType.LowShelf, BassShelfHz, BassDb(effects.Bass), ShelfQ);
        Add(list, FxFilterType.HighShelf, FidelityShelfHz, FidelityShelfDb(effects.Fidelity), ShelfQ);
        Add(list, FxFilterType.Peaking, FidelityPeakHz, FidelityPeakDb(effects.Fidelity), FidelityPeakQ);
        Add(list, FxFilterType.LowShelf, DynamicLowHz, DynamicLowDb(effects.DynamicBoost), ShelfQ);
        Add(list, FxFilterType.HighShelf, DynamicHighHz, DynamicHighDb(effects.DynamicBoost), ShelfQ);
        return list;
    }

    /// <summary>
    /// The largest boost the effects can add anywhere, in dB (for the auto pre-amp): the larger of the stacked low
    /// shelves (Bass + Dynamic low), the stacked top end (Fidelity shelf + air peak + Dynamic high) and Surround's
    /// gain on a hard-panned sound, 20·log10(1 + k). 0 when every effect is off.
    /// </summary>
    public static double BoostDb(EffectLevels? effects)
    {
        if (effects is null)
        {
            return 0;
        }

        double low = BassDb(effects.Bass) + DynamicLowDb(effects.DynamicBoost);
        double high = FidelityShelfDb(effects.Fidelity) + FidelityPeakDb(effects.Fidelity) + DynamicHighDb(effects.DynamicBoost);
        double k = SurroundWidth(effects.Surround);
        double surround = k > 0 ? 20 * Math.Log10(1 + k) : 0;
        return Math.Round(Math.Max(Math.Max(low, high), surround), 3, MidpointRounding.AwayFromZero);
    }

    private static double Scale(int percent, double maxDb) =>
        Math.Round(EffectLevels.ClampPercent(percent) / 100.0 * maxDb, 3, MidpointRounding.AwayFromZero);

    private static void Add(List<FxFilter> list, FxFilterType type, double hz, double gainDb, double q)
    {
        if (gainDb != 0)
        {
            list.Add(new FxFilter(type, hz, gainDb, q));
        }
    }
}

/// <summary>
/// Everything the Effects panel applies, frozen into one immutable snapshot that both outputs consume: the
/// Equalizer APO generator (<see cref="EqualizerApoConfig.Generate(FxState, string, bool, int)"/>) and the app's own
/// soundboard-bus chain (<c>AudioController.Audio.FxChain</c>). Chain order: graphic EQ bands → Bass Boost → Fidelity →
/// Dynamic Boost → Surround → Ambience.
/// </summary>
public sealed class FxState
{
    /// <summary>Bands within this many dB of 0 are left out (nothing to do).</summary>
    public const float IdentityThresholdDb = 0.01f;

    private readonly float[] _legacyGains;

    private FxState(IReadOnlyList<FxFilter> eqFilters, IReadOnlyList<FxFilter> effectFilters, int surroundPercent,
        int ambiencePercent, float[] legacyGains, bool legacyEqEnabled)
    {
        EqFilters = eqFilters;
        EffectFilters = effectFilters;
        SurroundPercent = surroundPercent;
        AmbiencePercent = ambiencePercent;
        SurroundWidth = FxEffects.SurroundWidth(surroundPercent);
        AmbienceWet = FxEffects.AmbienceWet(ambiencePercent);
        _legacyGains = legacyGains;
        LegacyEqEnabled = legacyEqEnabled;

        double maxBand = 0;
        foreach (var f in eqFilters)
        {
            maxBand = Math.Max(maxBand, f.GainDb);
        }

        MaxBandBoostDb = maxBand;
        EffectBoostDb = BoostFromFilters(effectFilters, SurroundWidth);
    }

    /// <summary>Nothing at all: no bands, no effects.</summary>
    public static FxState Neutral { get; } = new(Array.Empty<FxFilter>(), Array.Empty<FxFilter>(), 0, 0, new float[FxSettings.TenBandCount], false);

    /// <summary>The graphic EQ's peaking filters (non-flat bands only, gains clamped to ±24 dB), in band order.</summary>
    public IReadOnlyList<FxFilter> EqFilters { get; }

    /// <summary>Bass Boost, Fidelity and Dynamic Boost filters (see <see cref="FxEffects.Filters"/>).</summary>
    public IReadOnlyList<FxFilter> EffectFilters { get; }

    /// <summary>Surround slider (0–100).</summary>
    public int SurroundPercent { get; }

    /// <summary>Surround width k (0 = off, 0.5 at 100 %).</summary>
    public double SurroundWidth { get; }

    /// <summary>Ambience slider (0–100).</summary>
    public int AmbiencePercent { get; }

    /// <summary>Ambience wet level (0 = off, 0.35 at 100 %).</summary>
    public double AmbienceWet { get; }

    /// <summary>The largest positive band gain in dB (0 when nothing is boosted).</summary>
    public double MaxBandBoostDb { get; }

    /// <summary>The largest boost the effects add, in dB (see <see cref="FxEffects.BoostDb"/>).</summary>
    public double EffectBoostDb { get; }

    /// <summary>
    /// The auto pre-amp in dB: −(largest positive band gain + largest effect boost), so the loudest boosted frequency
    /// peaks at 0 dB; 0 when nothing is boosted.
    /// </summary>
    public double AutoPreampDb => MaxBandBoostDb + EffectBoostDb > 0 ? -Math.Round(MaxBandBoostDb + EffectBoostDb, 3, MidpointRounding.AwayFromZero) : 0;

    /// <summary>True when nothing is applied (power off, or every band flat and every effect at 0).</summary>
    public bool IsNeutral => EqFilters.Count == 0 && EffectFilters.Count == 0 && SurroundWidth == 0 && AmbienceWet == 0;

    /// <summary>The 10 adjustable band gains (for the 1.4 single-array APIs).</summary>
    public float[] LegacyGainsDb => (float[])_legacyGains.Clone();

    /// <summary>The EQ is applied (power on and the EQ switch on) — the 1.4 "Enable EQ" meaning.</summary>
    public bool LegacyEqEnabled { get; }

    /// <summary>The snapshot of <paramref name="settings"/> (normalized first when fields are missing).</summary>
    public static FxState From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FxSettings.EnsureNormalized(settings);
        var legacy = (float[])settings.EqGainsDb.Clone();
        if (!settings.FxEnabled)
        {
            return new FxState(Array.Empty<FxFilter>(), Array.Empty<FxFilter>(), 0, 0, legacy, false);
        }

        IReadOnlyList<FxFilter> eq = Array.Empty<FxFilter>();
        if (settings.EqEnabled)
        {
            eq = settings.EqMode == EqMode.ThirtyOneBandThirdOctave
                ? Peaking(FxSettings.ThirdOctaveFrequenciesHz, settings.EqGains31Db!, FxSettings.ThirdOctaveQ)
                : Peaking(settings.EqBands!.Select(b => b.FrequencyHz).ToList(), settings.EqBands!.Select(b => b.GainDb).ToArray(), FxSettings.TenBandQ);
        }

        var effects = settings.Effects ?? new EffectLevels();
        return new FxState(eq, FxEffects.Filters(effects), EffectLevels.ClampPercent(effects.Surround),
            EffectLevels.ClampPercent(effects.Ambience), legacy, settings.EqEnabled);
    }

    /// <summary>
    /// The 1.4 form: one peaking band per frequency with these gains (clamped ±24 dB, NaN = 0, missing = 0, extra
    /// ignored), no effects; nothing when <paramref name="eqEnabled"/> is false.
    /// </summary>
    public static FxState FromGains(IReadOnlyList<double> frequenciesHz, float[] gainsDb, bool eqEnabled, double q = FxSettings.TenBandQ)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(gainsDb);
        var gains = new float[frequenciesHz.Count];
        for (int i = 0; i < gains.Length; i++)
        {
            gains[i] = i < gainsDb.Length ? FxSettings.ClampGain(gainsDb[i]) : 0f;
        }

        var legacy = new float[FxSettings.TenBandCount];
        Array.Copy(gains, legacy, Math.Min(legacy.Length, gains.Length));
        var eq = eqEnabled ? Peaking(frequenciesHz, gains, q) : Array.Empty<FxFilter>();
        return new FxState(eq, Array.Empty<FxFilter>(), 0, 0, legacy, eqEnabled);
    }

    private static IReadOnlyList<FxFilter> Peaking(IReadOnlyList<double> frequencies, float[] gains, double q)
    {
        var list = new List<FxFilter>(frequencies.Count);
        for (int i = 0; i < frequencies.Count; i++)
        {
            float g = i < gains.Length ? FxSettings.ClampGain(gains[i]) : 0f;
            if (MathF.Abs(g) >= IdentityThresholdDb)
            {
                list.Add(new FxFilter(FxFilterType.Peaking, frequencies[i], g, q));
            }
        }

        return list;
    }

    private static double BoostFromFilters(IReadOnlyList<FxFilter> effectFilters, double surroundWidth)
    {
        // Same rule as FxEffects.BoostDb, from the filters themselves: stacked low shelves, stacked top end, surround.
        double low = 0;
        double high = 0;
        foreach (var f in effectFilters)
        {
            if (f.Type == FxFilterType.LowShelf)
            {
                low += Math.Max(0, f.GainDb);
            }
            else
            {
                high += Math.Max(0, f.GainDb);
            }
        }

        double surround = surroundWidth > 0 ? 20 * Math.Log10(1 + surroundWidth) : 0;
        return Math.Round(Math.Max(Math.Max(low, high), surround), 3, MidpointRounding.AwayFromZero);
    }
}
