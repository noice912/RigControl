using AudioController.Audio;
using Xunit;

namespace AudioController.Tests;

/// <summary>SPEC 4.1: the 10-band software EQ for the soundboard bus (pure DSP, no devices needed).</summary>
public sealed class TenBandEqTests
{
    private const int Rate = 48_000;
    private const int Channels = 2;

    /// <summary>Interleaved stereo sine, identical on both channels.</summary>
    private static float[] StereoSine(double frequencyHz, double seconds, float amplitude = 0.1f)
    {
        int frames = (int)(Rate * seconds);
        var buffer = new float[frames * Channels];
        for (int i = 0; i < frames; i++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * frequencyHz * i / Rate);
            buffer[2 * i] = v;
            buffer[2 * i + 1] = v;
        }

        return buffer;
    }

    /// <summary>RMS of the interleaved buffer from <paramref name="startSample"/> to the end.</summary>
    private static double Rms(float[] buffer, int startSample = 0)
    {
        double sum = 0;
        for (int i = startSample; i < buffer.Length; i++)
        {
            sum += (double)buffer[i] * buffer[i];
        }

        return Math.Sqrt(sum / (buffer.Length - startSample));
    }

    /// <summary>Processes in realistic 10 ms blocks, like the audio thread does.</summary>
    private static void ProcessInBlocks(TenBandEq eq, float[] buffer, int blockSamples = 480 * Channels)
    {
        for (int offset = 0; offset < buffer.Length; offset += blockSamples)
        {
            eq.Process(buffer, offset, Math.Min(blockSamples, buffer.Length - offset));
        }
    }

    private static float[] Gains(int band, float db)
    {
        var gains = new float[TenBandEq.BandCount];
        gains[band] = db;
        return gains;
    }

    private const int Band1k = 5;

    [Fact]
    public void BandFrequencies_AreTheTenSpecifiedBands()
    {
        Assert.Equal(10, TenBandEq.BandCount);
        Assert.Equal(new[] { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f }, TenBandEq.BandFrequenciesHz);
    }

    [Fact]
    public void NewInstance_IsDisabledAndFlat()
    {
        var eq = new TenBandEq(Rate, Channels);

        Assert.False(eq.Enabled);
        Assert.Equal(new float[10], eq.GetGains());
        Assert.Equal(Rate, eq.SampleRate);
        Assert.Equal(Channels, eq.Channels);
    }

    [Fact]
    public void Process_Disabled_LeavesBufferBitExact_EvenWithStrongGains()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(new[] { 12f, -12f, 12f, -12f, 12f, 12f, -12f, 12f, -12f, 12f });
        eq.Enabled = false;
        var buffer = StereoSine(1000, 0.25);
        var expected = (float[])buffer.Clone();

        ProcessInBlocks(eq, buffer);

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Process_EnabledWithFlatGains_LeavesSignalUnchanged()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(new float[10]);
        eq.Enabled = true;
        var buffer = StereoSine(440, 0.25, amplitude: 0.5f);
        var expected = (float[])buffer.Clone();

        ProcessInBlocks(eq, buffer);

        for (int i = 0; i < buffer.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - buffer[i]) <= 1e-6f, $"Sample {i}: {expected[i]} -> {buffer[i]}");
        }
    }

    [Fact]
    public void Process_Plus12dBAt1kHz_On1kHzSine_RaisesRmsByAbout12dB()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(Gains(Band1k, 12f));
        eq.Enabled = true;
        var input = StereoSine(1000, 0.5);
        var buffer = (float[])input.Clone();

        ProcessInBlocks(eq, buffer);

        int settle = Rate / 10 * Channels; // ignore the first 100 ms of filter transient
        double ratio = Rms(buffer, settle) / Rms(input, settle);
        Assert.True(ratio > 2.0, $"RMS ratio {ratio:0.000} should rise well above 1 for +12 dB.");
        Assert.InRange(ratio, 3.5, 4.5); // +12 dB ≈ ×3.98 at the band centre
    }

    [Fact]
    public void Process_Minus12dBAt1kHz_On1kHzSine_LowersRms()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(Gains(Band1k, -12f));
        eq.Enabled = true;
        var input = StereoSine(1000, 0.5);
        var buffer = (float[])input.Clone();

        ProcessInBlocks(eq, buffer);

        int settle = Rate / 10 * Channels;
        double ratio = Rms(buffer, settle) / Rms(input, settle);
        Assert.InRange(ratio, 0.2, 0.3); // −12 dB ≈ ×0.251
    }

    [Fact]
    public void Process_Plus12dBAt16kHz_BarelyAffects1kHzSine()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(Gains(9, 12f));
        eq.Enabled = true;
        var input = StereoSine(1000, 0.5);
        var buffer = (float[])input.Clone();

        ProcessInBlocks(eq, buffer);

        int settle = Rate / 10 * Channels;
        double ratio = Rms(buffer, settle) / Rms(input, settle);
        Assert.InRange(ratio, 0.9, 1.15);
    }

    [Fact]
    public void Process_TogglingEnabledOff_StopsProcessingImmediately()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(Gains(Band1k, 12f));
        eq.Enabled = true;
        ProcessInBlocks(eq, StereoSine(1000, 0.1));

        eq.Enabled = false;
        var buffer = StereoSine(1000, 0.1);
        var expected = (float[])buffer.Clone();
        ProcessInBlocks(eq, buffer);

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Process_DigitalSilence_StaysExactlyZero()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(new[] { 12f, 12f, 12f, 12f, 12f, 12f, 12f, 12f, 12f, 12f });
        eq.Enabled = true;
        var buffer = new float[Rate * Channels];

        ProcessInBlocks(eq, buffer);

        Assert.All(buffer, s => Assert.Equal(0, BitConverter.SingleToInt32Bits(s)));
    }

    [Fact]
    public void Process_OnlyTouchesTheRequestedRange()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(Gains(Band1k, 12f));
        eq.Enabled = true;
        var buffer = StereoSine(1000, 0.1);
        var original = (float[])buffer.Clone();
        int offset = 960, count = 1920;

        eq.Process(buffer, offset, count);

        Assert.Equal(original[..offset], buffer[..offset]);
        Assert.Equal(original[(offset + count)..], buffer[(offset + count)..]);
        Assert.NotEqual(original[offset..(offset + count)], buffer[offset..(offset + count)]);
    }

    [Fact]
    public void SetGains_ClampsToPlusMinus24_TreatsNaNAsZero_AndPadsMissingBands()
    {
        var eq = new TenBandEq(Rate, Channels);

        eq.SetGains(new[] { 40f, -40f, float.NaN, 3.5f });

        Assert.Equal(new[] { 24f, -24f, 0f, 3.5f, 0f, 0f, 0f, 0f, 0f, 0f }, eq.GetGains());
    }

    [Fact]
    public void SetGains_ExtraValuesBeyondTenBands_AreIgnored()
    {
        var eq = new TenBandEq(Rate, Channels);

        eq.SetGains(Enumerable.Range(1, 12).Select(i => (float)i).ToArray());

        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f }, eq.GetGains());
    }

    [Fact]
    public void GetGains_ReturnsACopy()
    {
        var eq = new TenBandEq(Rate, Channels);
        eq.SetGains(Gains(0, 6f));

        eq.GetGains()[0] = -99f;

        Assert.Equal(6f, eq.GetGains()[0]);
    }

    [Fact]
    public void ArgumentValidation_RejectsBadInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenBandEq(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenBandEq(48_000, 0));
        var eq = new TenBandEq(Rate, Channels);
        Assert.Throws<ArgumentNullException>(() => eq.SetGains(null!));
        Assert.Throws<ArgumentNullException>(() => eq.Process(null!, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.Process(new float[10], 5, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.Process(new float[10], 11, 0));
    }
}
