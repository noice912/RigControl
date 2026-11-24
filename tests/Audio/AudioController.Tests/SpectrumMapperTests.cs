using AudioController.Core.AudioMath;
using NAudio.Dsp;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The spectrum analyzer's pure parts: 32 log-spaced bars 20 Hz – 20 kHz from a 2048-point Hann FFT (NAudio's FFT, scaled
/// 1/N), dBFS calibration, empty low bars, silence, and the display ballistics (fall + peak hold).
/// </summary>
public sealed class SpectrumMapperTests
{
    private const int Rate = 48_000;
    private const int N = 2048;

    /// <summary>Magnitudes of a Hann-windowed sine, exactly as the analyzer computes them.</summary>
    private static float[] SineMagnitudes(double hz, float amplitude)
    {
        var window = SpectrumMapper.HannWindow(N);
        var data = new Complex[N];
        for (int i = 0; i < N; i++)
        {
            data[i].X = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate) * window[i];
        }

        FastFourierTransform.FFT(true, 11, data);
        var mags = new float[N / 2 + 1];
        for (int k = 0; k <= N / 2; k++) mags[k] = MathF.Sqrt(data[k].X * data[k].X + data[k].Y * data[k].Y);
        return mags;
    }

    private static int BarOf(SpectrumMapper m, double hz)
    {
        for (int b = 0; b < m.BarCount; b++)
        {
            if (hz >= m.BarEdgesHz[b] && hz < m.BarEdgesHz[b + 1]) return b;
        }

        return -1;
    }

    [Fact]
    public void Bars_Are32_LogSpaced_From20HzTo20kHz()
    {
        var m = new SpectrumMapper(Rate, N);

        Assert.Equal(32, m.BarCount);
        Assert.Equal(33, m.BarEdgesHz.Count);
        Assert.Equal(20, m.BarEdgesHz[0], 9);
        Assert.Equal(20000, m.BarEdgesHz[^1], 6);
        double ratio = m.BarEdgesHz[1] / m.BarEdgesHz[0];
        for (int i = 1; i < 33; i++) Assert.Equal(ratio, m.BarEdgesHz[i] / m.BarEdgesHz[i - 1], 9);
        Assert.Equal(Math.Pow(1000, 1.0 / 32), ratio, 9);
        Assert.Equal(Rate / (double)N, m.BinHz);
    }

    [Fact]
    public void EveryBar_HasValidBins_AndNarrowLowBars_UseTheNearestBin()
    {
        var m = new SpectrumMapper(Rate, N);
        for (int b = 0; b < m.BarCount; b++)
        {
            var (first, last) = m.BinsOf(b);
            Assert.InRange(first, 1, N / 2);
            Assert.InRange(last, first, N / 2);
        }

        // The first bar (20–24.9 Hz) is narrower than one bin (23.4 Hz): it reads the bin nearest its centre.
        var (f0, l0) = m.BinsOf(0);
        Assert.Equal(f0, l0);
        Assert.Equal(1, f0);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(300)]
    [InlineData(8000)]
    public void AFullScaleSine_ReadsAbout0dBFS_InItsBar_AndMuchLessFarAway(double hz)
    {
        var m = new SpectrumMapper(Rate, N);
        var bars = new float[32];

        m.Map(SineMagnitudes(hz, 1f), bars);

        int bar = BarOf(m, hz);
        Assert.InRange(bars[bar], -2f, 0.5f); // Hann scalloping is at most −1.4 dB
        for (int b = 0; b < 32; b++)
        {
            if (Math.Abs(b - bar) >= 4) Assert.True(bars[b] < bars[bar] - 40, $"bar {b} ({bars[b]:0.0} dB) leaks from {hz} Hz");
        }
    }

    [Fact]
    public void Level_TracksAmplitude()
    {
        var m = new SpectrumMapper(Rate, N);
        var loud = new float[32];
        var quiet = new float[32];

        m.Map(SineMagnitudes(1000, 0.5f), loud);
        m.Map(SineMagnitudes(1000, 0.05f), quiet);

        int bar = BarOf(m, 1000);
        Assert.Equal(20, loud[bar] - quiet[bar], 1);
    }

    [Fact]
    public void Silence_AndBadNumbers_ReadTheFloor()
    {
        var m = new SpectrumMapper(Rate, N, floorDb: -90f);
        var bars = new float[32];

        m.Map(new float[N / 2 + 1], bars);
        Assert.All(bars, v => Assert.Equal(-90f, v));

        var bad = Enumerable.Repeat(float.NaN, N / 2 + 1).ToArray();
        bad[100] = float.PositiveInfinity;
        m.Map(bad, bars);
        Assert.All(bars, v => Assert.Equal(-90f, v));

        Assert.Equal(-90f, SpectrumMapper.ToDb(0f, -90f));
        Assert.Equal(-90f, SpectrumMapper.ToDb(1e-12f, -90f));
        Assert.Equal(0f, SpectrumMapper.ToDb(0.25f, -90f), 4);
    }

    [Fact]
    public void Arguments_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumMapper(0, N));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumMapper(Rate, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumMapper(Rate, N, barCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumMapper(Rate, N, minHz: 500, maxHz: 100));
        var m = new SpectrumMapper(Rate, N);
        Assert.Throws<ArgumentException>(() => m.Map(new float[10], new float[32]));
        Assert.Throws<ArgumentException>(() => m.Map(new float[N / 2 + 1], new float[3]));

        // At 32 kHz the top bar stops at Nyquist.
        Assert.Equal(16000, new SpectrumMapper(32_000, N).MaxHz);
    }

    [Fact]
    public void HannWindow_IsZeroAtTheEdge_AndOneInTheMiddle()
    {
        var w = SpectrumMapper.HannWindow(N);

        Assert.Equal(0f, w[0], 6);
        Assert.Equal(1f, w[N / 2], 6);
        Assert.Equal(0.5, w.Average(), 4);
    }

    // ---------------------------------------------------------------- ballistics

    [Fact]
    public void Ballistics_RiseInstantly_FallAt60dBPerSecond_PeaksHoldThenFall()
    {
        var b = new SpectrumBallistics(2, floorDb: -90f);

        b.Update(new[] { -10f, -50f }, 0.033);
        Assert.Equal(-10f, b.Levels[0]);
        Assert.Equal(-10f, b.Peaks[0]);

        b.Update(new[] { -90f, -90f }, 0.5);         // signal gone for half a second
        Assert.Equal(-40f, b.Levels[0], 3);          // 60 dB/s × 0.5 s
        Assert.Equal(-10f, b.Peaks[0]);              // still held (0.6 s)

        b.Update(new[] { -90f, -90f }, 0.5);         // 1.0 s: hold over after 0.1 s of this step
        Assert.Equal(-70f, b.Levels[0], 3);
        Assert.True(b.Peaks[0] < -10f && b.Peaks[0] >= b.Levels[0]);

        for (int i = 0; i < 100; i++) b.Update(new[] { -90f, -90f }, 0.1);
        Assert.All(b.Levels, v => Assert.Equal(-90f, v));
        Assert.All(b.Peaks, v => Assert.Equal(-90f, v));

        b.Update(new[] { float.NaN, -20f }, 0.03);
        Assert.Equal(-90f, b.Levels[0]);
        b.Reset();
        Assert.All(b.Peaks, v => Assert.Equal(-90f, v));
    }
}
