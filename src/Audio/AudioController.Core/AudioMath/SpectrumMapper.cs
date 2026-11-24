namespace AudioController.Core.AudioMath;

/// <summary>
/// Pure FFT-bin → bar mapping for the Effects panel's spectrum analyzer: <see cref="BarCount"/> bars log-spaced
/// from <see cref="MinHz"/> to <see cref="MaxHz"/>, each the loudest bin inside it, in dBFS.
/// </summary>
/// <remarks>
/// Magnitudes are expected from a forward FFT scaled by 1/N (NAudio's <c>FastFourierTransform.FFT</c>) of a
/// Hann-windowed frame: a full-scale sine then reads 1/4 in its bin (½ for the two-sided spectrum × ½ Hann coherent
/// gain), so <see cref="AmplitudeScale"/> = 4 maps it to 0 dBFS. A bar too narrow to hold a bin (the lowest bars at
/// 2048 points) uses the bin nearest its centre.
/// </remarks>
public sealed class SpectrumMapper
{
    /// <summary>Multiplier from a Hann-windowed, 1/N-scaled bin magnitude to the sine amplitude.</summary>
    public const float AmplitudeScale = 4f;

    private readonly int[] _firstBin;
    private readonly int[] _lastBin;

    /// <param name="sampleRate">Sample rate of the analyzed audio.</param>
    /// <param name="fftSize">FFT length (a power of two).</param>
    /// <param name="barCount">Number of bars.</param>
    /// <param name="minHz">Lower edge of the first bar.</param>
    /// <param name="maxHz">Upper edge of the last bar (capped at Nyquist).</param>
    /// <param name="floorDb">Silence level: no bar goes below it.</param>
    public SpectrumMapper(int sampleRate, int fftSize, int barCount = 32, double minHz = 20, double maxHz = 20000, float floorDb = -90f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (fftSize < 16 || (fftSize & (fftSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fftSize), "The FFT size must be a power of two of at least 16.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(barCount);
        if (!(minHz > 0) || !(maxHz > minHz))
        {
            throw new ArgumentOutOfRangeException(nameof(maxHz), "Need 0 < minHz < maxHz.");
        }

        SampleRate = sampleRate;
        FftSize = fftSize;
        BarCount = barCount;
        MinHz = minHz;
        MaxHz = Math.Min(maxHz, sampleRate / 2.0);
        FloorDb = floorDb;
        BinHz = (double)sampleRate / fftSize;

        var edges = new double[barCount + 1];
        double ratio = Math.Log(MaxHz / MinHz);
        for (int i = 0; i <= barCount; i++)
        {
            edges[i] = MinHz * Math.Exp(ratio * i / barCount);
        }

        BarEdgesHz = Array.AsReadOnly(edges);
        _firstBin = new int[barCount];
        _lastBin = new int[barCount];
        int maxBin = fftSize / 2;
        for (int b = 0; b < barCount; b++)
        {
            int first = (int)Math.Ceiling(edges[b] / BinHz);
            int last = (int)Math.Ceiling(edges[b + 1] / BinHz) - 1; // bins with lo ≤ f < hi
            first = Math.Clamp(first, 1, maxBin);
            last = Math.Clamp(last, 0, maxBin);
            if (last < first)
            {
                int nearest = Math.Clamp((int)Math.Round(BarCenterHz(b) / BinHz), 1, maxBin);
                first = last = nearest;
            }

            _firstBin[b] = first;
            _lastBin[b] = last;
        }
    }

    /// <summary>Sample rate of the analyzed audio.</summary>
    public int SampleRate { get; }

    /// <summary>FFT length.</summary>
    public int FftSize { get; }

    /// <summary>Number of bars.</summary>
    public int BarCount { get; }

    /// <summary>Lower edge of the first bar in Hz.</summary>
    public double MinHz { get; }

    /// <summary>Upper edge of the last bar in Hz.</summary>
    public double MaxHz { get; }

    /// <summary>The level of an empty bar in dBFS.</summary>
    public float FloorDb { get; }

    /// <summary>Width of one FFT bin in Hz.</summary>
    public double BinHz { get; }

    /// <summary>The <see cref="BarCount"/> + 1 bar edges in Hz, log-spaced, increasing.</summary>
    public IReadOnlyList<double> BarEdgesHz { get; }

    /// <summary>Geometric centre of bar <paramref name="bar"/> in Hz.</summary>
    public double BarCenterHz(int bar) => Math.Sqrt(BarEdgesHz[bar] * BarEdgesHz[bar + 1]);

    /// <summary>The FFT bins (inclusive) feeding bar <paramref name="bar"/>.</summary>
    public (int First, int Last) BinsOf(int bar) => (_firstBin[bar], _lastBin[bar]);

    /// <summary>
    /// Maps one spectrum (<paramref name="magnitudes"/> = |X[k]| for k = 0 … N/2) to <paramref name="barsDb"/>
    /// (<see cref="BarCount"/> values in dBFS, never below <see cref="FloorDb"/>; NaN/∞ bins count as silence).
    /// </summary>
    public void Map(ReadOnlySpan<float> magnitudes, Span<float> barsDb)
    {
        if (magnitudes.Length < FftSize / 2 + 1)
        {
            throw new ArgumentException($"Need {FftSize / 2 + 1} magnitudes.", nameof(magnitudes));
        }

        if (barsDb.Length < BarCount)
        {
            throw new ArgumentException($"Need room for {BarCount} bars.", nameof(barsDb));
        }

        for (int b = 0; b < BarCount; b++)
        {
            float peak = 0f;
            for (int k = _firstBin[b]; k <= _lastBin[b]; k++)
            {
                float m = magnitudes[k];
                if (float.IsFinite(m) && m > peak)
                {
                    peak = m;
                }
            }

            barsDb[b] = ToDb(peak, FloorDb);
        }
    }

    /// <summary>A bin magnitude (Hann, 1/N) in dBFS: 20·log10(<see cref="AmplitudeScale"/>·m), never below the floor.</summary>
    public static float ToDb(float magnitude, float floorDb)
    {
        double amplitude = magnitude * (double)AmplitudeScale;
        if (!(amplitude > 0) || !double.IsFinite(amplitude))
        {
            return floorDb;
        }

        return (float)Math.Max(floorDb, 20 * Math.Log10(amplitude));
    }

    /// <summary>A Hann window of <paramref name="length"/> points (periodic form, for FFT analysis).</summary>
    public static float[] HannWindow(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var w = new float[length];
        for (int i = 0; i < length; i++)
        {
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / length));
        }

        return w;
    }
}

/// <summary>
/// Display ballistics for the analyzer (pure): bars rise instantly and fall at <see cref="FallDbPerSecond"/>; each bar's
/// peak marker holds for <see cref="HoldSeconds"/>, then falls at <see cref="PeakFallDbPerSecond"/>.
/// </summary>
public sealed class SpectrumBallistics
{
    private readonly float[] _levels;
    private readonly float[] _peaks;
    private readonly double[] _hold;

    /// <param name="barCount">Number of bars.</param>
    /// <param name="floorDb">Silence level.</param>
    public SpectrumBallistics(int barCount, float floorDb = -90f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(barCount);
        FloorDb = floorDb;
        _levels = Enumerable.Repeat(floorDb, barCount).ToArray();
        _peaks = Enumerable.Repeat(floorDb, barCount).ToArray();
        _hold = new double[barCount];
    }

    /// <summary>How fast a bar falls, in dB per second.</summary>
    public double FallDbPerSecond { get; init; } = 60;

    /// <summary>How long a peak marker stays put.</summary>
    public double HoldSeconds { get; init; } = 0.6;

    /// <summary>How fast a peak marker falls after its hold, in dB per second.</summary>
    public double PeakFallDbPerSecond { get; init; } = 30;

    /// <summary>The silence level.</summary>
    public float FloorDb { get; }

    /// <summary>Current bar levels in dBFS.</summary>
    public IReadOnlyList<float> Levels => _levels;

    /// <summary>Current peak markers in dBFS (always ≥ the bar).</summary>
    public IReadOnlyList<float> Peaks => _peaks;

    /// <summary>Advances by <paramref name="elapsedSeconds"/> with a new frame of bar levels.</summary>
    public void Update(ReadOnlySpan<float> barsDb, double elapsedSeconds)
    {
        double dt = double.IsFinite(elapsedSeconds) ? Math.Clamp(elapsedSeconds, 0, 1) : 0;
        for (int i = 0; i < _levels.Length; i++)
        {
            float input = i < barsDb.Length && float.IsFinite(barsDb[i]) ? Math.Max(barsDb[i], FloorDb) : FloorDb;
            float fallen = (float)Math.Max(FloorDb, _levels[i] - FallDbPerSecond * dt);
            _levels[i] = Math.Max(input, fallen);

            if (_levels[i] >= _peaks[i])
            {
                _peaks[i] = _levels[i];
                _hold[i] = HoldSeconds;
                continue;
            }

            // Hold first; whatever is left of this step after the hold ends is spent falling.
            double falling = dt;
            if (_hold[i] > 0)
            {
                double held = Math.Min(_hold[i], falling);
                _hold[i] -= held;
                falling -= held;
            }

            if (falling > 0)
            {
                _peaks[i] = (float)Math.Max(_levels[i], _peaks[i] - PeakFallDbPerSecond * falling);
            }
        }
    }

    /// <summary>Everything back to the floor (idle bars).</summary>
    public void Reset()
    {
        Array.Fill(_levels, FloorDb);
        Array.Fill(_peaks, FloorDb);
        Array.Clear(_hold);
    }
}
