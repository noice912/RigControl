using AudioController.Core.Equalizer;
using NAudio.Dsp;

namespace AudioController.Audio;

/// <summary>
/// A graphic / parametric equalizer for the soundboard bus: one biquad per band and channel, any number of bands,
/// each with its own centre frequency, gain and Q (peaking, low shelf or high shelf — the same RBJ filters Equalizer APO
/// uses for <c>PK</c>/<c>LS</c>/<c>HS</c>). Serves both the 10 adjustable bands and the 31 fixed third-octave bands.
/// Never applied to the microphone path.
/// </summary>
/// <remarks>
/// <see cref="SetBands"/> and <see cref="Enabled"/> may be called from any thread while audio runs: the filter bank is
/// rebuilt and swapped atomically. <see cref="Process"/> must be called by one thread at a time (the audio thread that
/// owns this instance). Bands within 0.01 dB of 0 and bands at or above 0.45 × the sample rate are left out.
/// </remarks>
public sealed class GraphicEq
{
    /// <summary>Lowest accepted band gain in dB.</summary>
    public const float MinGainDb = FxSettings.MinGainDb;

    /// <summary>Highest accepted band gain in dB.</summary>
    public const float MaxGainDb = FxSettings.MaxGainDb;

    private const double IdentityThresholdDb = FxState.IdentityThresholdDb;

    private readonly object _buildLock = new();
    private readonly int _settleSamples;
    private FxFilter[] _bands = [];
    private volatile BiQuadFilter[][] _bank = [];
    private volatile bool _enabled;

    // Audio-thread state (only touched by Process).
    private int _silentSamples;
    private bool _settled;

    /// <summary>Creates a flat, disabled equalizer for interleaved float audio.</summary>
    /// <param name="sampleRate">Sample rate of the processed audio in Hz.</param>
    /// <param name="channels">Interleaved channel count of the processed audio.</param>
    public GraphicEq(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        SampleRate = sampleRate;
        Channels = channels;

        // After half a second of digital silence the filters have rung out; processing stops until signal returns,
        // which keeps silence exactly zero and avoids denormal slow paths.
        _settleSamples = (int)Math.Min(int.MaxValue, (long)sampleRate * channels / 2);
    }

    /// <summary>Sample rate this instance processes.</summary>
    public int SampleRate { get; }

    /// <summary>Interleaved channel count this instance processes.</summary>
    public int Channels { get; }

    /// <summary>When false, <see cref="Process"/> leaves the buffer untouched.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Number of filters currently built (non-flat bands below 0.45 × the sample rate).</summary>
    public int ActiveFilterCount => _bank.Length;

    /// <summary>Returns a copy of the bands last set (after clamping).</summary>
    public IReadOnlyList<FxFilter> GetBands()
    {
        lock (_buildLock)
        {
            return (FxFilter[])_bands.Clone();
        }
    }

    /// <summary>
    /// Sets the bands (gains clamped to −24..+24 dB, NaN = 0; frequencies and Q must be positive and finite, others are
    /// left out) and rebuilds the filters.
    /// </summary>
    public void SetBands(IReadOnlyList<FxFilter> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        lock (_buildLock)
        {
            var list = new List<FxFilter>(bands.Count);
            foreach (var b in bands)
            {
                if (!double.IsFinite(b.FrequencyHz) || b.FrequencyHz <= 0 || !double.IsFinite(b.Q) || b.Q <= 0)
                {
                    continue;
                }

                double g = double.IsNaN(b.GainDb) ? 0 : Math.Clamp(b.GainDb, MinGainDb, MaxGainDb);
                list.Add(b with { GainDb = g });
            }

            _bands = list.ToArray();
            _bank = BuildBank();
        }
    }

    /// <summary>Equalizes interleaved float samples in place. No-op when disabled or flat.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(count), "offset/count lie outside the buffer.");
        }

        if (!_enabled || count == 0)
        {
            return;
        }

        var bank = _bank;
        if (bank.Length == 0)
        {
            return;
        }

        if (FxChain.IsSilent(buffer, offset, count))
        {
            _silentSamples = (int)Math.Min(int.MaxValue, (long)_silentSamples + count);
            if (_silentSamples >= _settleSamples)
            {
                if (!_settled)
                {
                    _settled = true;
                    ResetFilterState();
                }

                return;
            }
        }
        else
        {
            _silentSamples = 0;
            _settled = false;
        }

        FxChain.RunBank(bank, buffer, offset, count, Channels);
    }

    /// <summary>
    /// The NAudio biquad for one band (RBJ cookbook): <c>PeakingEQ(rate, f, Q, gain)</c>, or
    /// <c>LowShelf/HighShelf(rate, f, S, gain)</c> with the shelf slope S that gives the band's Q.
    /// </summary>
    public static BiQuadFilter CreateFilter(FxFilter band, int sampleRate)
    {
        float gain = (float)band.GainDb;
        return band.Type switch
        {
            FxFilterType.LowShelf => BiQuadFilter.LowShelf(sampleRate, (float)band.FrequencyHz, (float)ShelfSlope(band.Q, band.GainDb), gain),
            FxFilterType.HighShelf => BiQuadFilter.HighShelf(sampleRate, (float)band.FrequencyHz, (float)ShelfSlope(band.Q, band.GainDb), gain),
            _ => BiQuadFilter.PeakingEQ(sampleRate, (float)band.FrequencyHz, (float)band.Q, gain),
        };
    }

    /// <summary>
    /// The RBJ shelf slope S for a shelf Q at this gain: 1/Q² = (A + 1/A)(1/S − 1) + 2, A = 10^(gain/40).
    /// Q = 1/√2 gives S = 1 at every gain.
    /// </summary>
    public static double ShelfSlope(double q, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        double denom = (1 / (q * q) - 2) / (a + 1 / a) + 1;
        return denom > 1e-6 ? 1 / denom : 1;
    }

    /// <summary>Builds per-channel filters for the active bands. Caller holds <see cref="_buildLock"/>.</summary>
    internal static BiQuadFilter[][] BuildBank(IReadOnlyList<FxFilter> bands, int sampleRate, int channels)
    {
        var result = new List<BiQuadFilter[]>(bands.Count);
        double limit = sampleRate * 0.45;
        foreach (var band in bands)
        {
            if (Math.Abs(band.GainDb) < IdentityThresholdDb || band.FrequencyHz >= limit)
            {
                continue;
            }

            var perChannel = new BiQuadFilter[channels];
            for (int c = 0; c < channels; c++)
            {
                perChannel[c] = CreateFilter(band, sampleRate);
            }

            result.Add(perChannel);
        }

        return result.ToArray();
    }

    private BiQuadFilter[][] BuildBank() => BuildBank(_bands, SampleRate, Channels);

    private void ResetFilterState()
    {
        lock (_buildLock)
        {
            _bank = BuildBank();
        }
    }
}
