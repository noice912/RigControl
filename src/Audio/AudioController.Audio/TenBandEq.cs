using AudioController.Core.Equalizer;

namespace AudioController.Audio;

/// <summary>
/// The 1.4 ten-band equalizer API (31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz; Q 1; ±24 dB) — now a thin wrapper
/// over <see cref="GraphicEq"/>, kept for callers of the fixed-band API. The app itself drives the Effects chain
/// (<see cref="FxChain"/>), whose 10 bands are adjustable. Never applied to the microphone path.
/// </summary>
/// <remarks>
/// <see cref="SetGains"/> and <see cref="Enabled"/> may be called from any thread while audio runs (the filter bank is
/// swapped atomically); <see cref="Process"/> must be called by one thread at a time.
/// </remarks>
public sealed class TenBandEq
{
    /// <summary>Number of bands.</summary>
    public const int BandCount = 10;

    /// <summary>Lowest accepted band gain in dB.</summary>
    public const float MinGainDb = -24f;

    /// <summary>Highest accepted band gain in dB.</summary>
    public const float MaxGainDb = 24f;

    /// <summary>Quality factor of every band.</summary>
    public const float BandQ = 1.0f;

    private static readonly float[] Frequencies = [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    private readonly object _gainLock = new();
    private readonly float[] _gainsDb = new float[BandCount];
    private readonly GraphicEq _eq;

    /// <summary>Creates a flat (0 dB), disabled equalizer for interleaved float audio.</summary>
    /// <param name="sampleRate">Sample rate of the processed audio in Hz.</param>
    /// <param name="channels">Interleaved channel count of the processed audio.</param>
    public TenBandEq(int sampleRate, int channels)
    {
        _eq = new GraphicEq(sampleRate, channels);
    }

    /// <summary>Band centre frequencies in Hz: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k.</summary>
    public static IReadOnlyList<float> BandFrequenciesHz { get; } = Array.AsReadOnly(Frequencies);

    /// <summary>Sample rate this instance processes.</summary>
    public int SampleRate => _eq.SampleRate;

    /// <summary>Interleaved channel count this instance processes.</summary>
    public int Channels => _eq.Channels;

    /// <summary>When false, <see cref="Process"/> leaves the buffer untouched.</summary>
    public bool Enabled
    {
        get => _eq.Enabled;
        set => _eq.Enabled = value;
    }

    /// <summary>Returns a copy of the current band gains in dB.</summary>
    public float[] GetGains()
    {
        lock (_gainLock)
        {
            return (float[])_gainsDb.Clone();
        }
    }

    /// <summary>
    /// Sets the band gains in dB (clamped to −24..+24; NaN counts as 0) and rebuilds the filters.
    /// Values beyond the tenth are ignored; missing bands are 0 dB.
    /// </summary>
    public void SetGains(float[] gainsDb)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        lock (_gainLock)
        {
            var bands = new FxFilter[BandCount];
            for (int i = 0; i < BandCount; i++)
            {
                float g = i < gainsDb.Length ? gainsDb[i] : 0f;
                _gainsDb[i] = float.IsNaN(g) ? 0f : Math.Clamp(g, MinGainDb, MaxGainDb);
                bands[i] = new FxFilter(FxFilterType.Peaking, Frequencies[i], _gainsDb[i], BandQ);
            }

            _eq.SetBands(bands);
        }
    }

    /// <summary>Equalizes interleaved float samples in place. No-op when disabled or flat.</summary>
    public void Process(float[] buffer, int offset, int count) => _eq.Process(buffer, offset, count);
}
