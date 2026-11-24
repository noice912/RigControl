using AudioController.Core.Equalizer;
using NAudio.Wave;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The Ambience impulse response behind "Convolution: ambience.wav": length, the untouched dry impulse at t = 0, a
/// decaying decorrelated tail whose energy is wet² (growing with the slider), peak ≤ 1, no NaN, deterministic bytes, and
/// a WAV file NAudio (like libsndfile) reads as 32-bit float stereo.
/// </summary>
public sealed class AmbienceImpulseTests
{
    private static double TailEnergy(float[] channel) => channel.Skip(1).Sum(v => (double)v * v);

    [Theory]
    [InlineData(48000, 16800)]
    [InlineData(44100, 15435)]
    [InlineData(96000, 33600)]
    public void Length_Is035Seconds_AtTheDeviceRate(int rate, int frames)
    {
        var ir = AmbienceImpulse.Generate(50, rate);

        Assert.Equal(2, ir.Length);
        Assert.All(ir, ch => Assert.Equal(frames, ch.Length));
        Assert.Equal(frames, AmbienceImpulse.FrameCount(rate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void SampleZero_IsTheDryImpulse_OnBothChannels(int percent)
    {
        var ir = AmbienceImpulse.Generate(percent);

        Assert.Equal(1f, ir[0][0]);
        Assert.Equal(1f, ir[1][0]);
    }

    [Fact]
    public void AtZero_ThereIsNoTail()
    {
        var ir = AmbienceImpulse.Generate(0);

        Assert.All(ir, ch => Assert.All(ch.Skip(1), v => Assert.Equal(0f, v)));
    }

    [Fact]
    public void TailEnergy_IsWetSquared_AndGrowsWithThePercentage()
    {
        double previous = 0;
        foreach (int pct in new[] { 10, 25, 50, 75, 100 })
        {
            var ir = AmbienceImpulse.Generate(pct);
            double wet = FxEffects.AmbienceWet(pct);
            foreach (var ch in ir)
            {
                double e = TailEnergy(ch);
                Assert.Equal(wet * wet, e, 4);
            }

            double energy = TailEnergy(ir[0]);
            Assert.True(energy > previous, $"tail energy at {pct} % ({energy}) should exceed the previous step ({previous})");
            previous = energy;
        }

        Assert.Equal(0.35, FxEffects.AmbienceWet(100));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public void Peak_IsAtMostOne_AndNothingIsNaN(int percent)
    {
        var ir = AmbienceImpulse.Generate(percent);

        Assert.All(ir, ch => Assert.All(ch, v =>
        {
            Assert.True(float.IsFinite(v));
            Assert.InRange(MathF.Abs(v), 0f, 1f);
        }));
        Assert.Equal(1f, ir.Max(ch => ch.Max(MathF.Abs)));
    }

    [Fact]
    public void Tail_Decays_ByAbout60dB()
    {
        var ch = AmbienceImpulse.Generate(100)[0];
        int tenth = ch.Length / 10;
        double first = ch.Skip(1).Take(tenth).Sum(v => (double)v * v);
        double last = ch.Skip(ch.Length - tenth).Sum(v => (double)v * v);

        double dropDb = 10 * Math.Log10(first / last);
        Assert.InRange(dropDb, 45, 60); // the envelope falls 60 dB over the file; first vs last tenth ≈ 54 dB
    }

    [Fact]
    public void LeftAndRight_AreDecorrelated()
    {
        var ir = AmbienceImpulse.Generate(100);
        var l = ir[0].Skip(1).Select(v => (double)v).ToArray();
        var r = ir[1].Skip(1).Select(v => (double)v).ToArray();

        Assert.NotEqual(l, r);
        double dot = l.Zip(r, (a, b) => a * b).Sum();
        double corr = dot / Math.Sqrt(l.Sum(a => a * a) * r.Sum(b => b * b));
        Assert.InRange(corr, -0.1, 0.1);
    }

    [Fact]
    public void Bytes_AreDeterministic_AndChangeWithTheLevel()
    {
        Assert.Equal(AmbienceImpulse.CreateWav(70), AmbienceImpulse.CreateWav(70));
        Assert.NotEqual(AmbienceImpulse.CreateWav(70), AmbienceImpulse.CreateWav(71));
        Assert.NotEqual(AmbienceImpulse.CreateWav(70, 48000), AmbienceImpulse.CreateWav(70, 44100));
    }

    [Fact]
    public void Wav_IsStereo32BitFloat_AtTheRate_AndReadsBackExactly()
    {
        byte[] bytes = AmbienceImpulse.CreateWav(60, 48000);
        var expected = AmbienceImpulse.Generate(60, 48000);

        using var reader = new WaveFileReader(new MemoryStream(bytes));
        Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
        Assert.Equal(48000, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(32, reader.WaveFormat.BitsPerSample);
        Assert.Equal(expected[0].Length, reader.SampleCount);

        var samples = reader.ToSampleProvider();
        var buffer = new float[expected[0].Length * 2];
        Assert.Equal(buffer.Length, samples.Read(buffer, 0, buffer.Length));
        for (int f = 0; f < expected[0].Length; f++)
        {
            Assert.Equal(expected[0][f], buffer[2 * f]);
            Assert.Equal(expected[1][f], buffer[2 * f + 1]);
        }

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
    }

    [Fact]
    public void BadArguments_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmbienceImpulse.Generate(50, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmbienceImpulse.Generate(50, 10_000_000));
        Assert.Throws<ArgumentException>(() => AmbienceImpulse.ToWav(new[] { new float[3], new float[4] }, 48000));
        Assert.Equal(AmbienceImpulse.Generate(100)[0], AmbienceImpulse.Generate(150)[0]); // clamped to 100 %
    }
}
