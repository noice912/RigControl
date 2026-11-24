using AudioController.Core.AudioMath;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The "Also play on" signal path pieces that need no hardware: the delay line (exact, bit-exact at 0 ms), the volume
/// stage (bit-exact at 100 %, exactly half at 50 %, never a boost), the drift-correction rule and controller, and the
/// per-output buffer (priming, silence padding, click-free gaps, bounded latency over hours of clock drift).
/// </summary>
public sealed class MirrorDspTests
{
    private const int Rate = 48_000;

    private static float[] Noise(int samples, int seed = 7)
    {
        var random = new Random(seed);
        var data = new float[samples];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(random.NextDouble() * 1.6 - 0.8);
        }

        return data;
    }

    private static void AssertBitExact(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
            {
                Assert.Fail($"Sample {i}: expected {expected[i]:R}, got {actual[i]:R}.");
            }
        }
    }

    // ---------------------------------------------------------------- delay line

    [Fact]
    public void DelayLine_ZeroDelay_IsBitExactPassThrough()
    {
        var input = Noise(2 * 5000);
        input[0] = -0f;
        input[1] = float.Epsilon;
        var line = new DelayLine(2, DelayLine.FramesFor(500, Rate));
        var buffer = (float[])input.Clone();

        for (int offset = 0; offset < buffer.Length; offset += 2 * 333)
        {
            line.Process(buffer.AsSpan(offset, Math.Min(2 * 333, buffer.Length - offset)));
        }

        AssertBitExact(input, buffer);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 7)]
    [InlineData(9600, 480)]  // 200 ms at 48 kHz, 10 ms blocks
    [InlineData(24000, 441)] // the maximum (500 ms), odd block size
    public void DelayLine_DelaysByExactlyThatManyFrames_AcrossBlocks(int delayFrames, int blockFrames)
    {
        const int channels = 2;
        int frames = delayFrames + 3000;
        var input = Noise(frames * channels, seed: delayFrames);
        var line = new DelayLine(channels, 24000, crossfadeFrames: 0, delayFrames: delayFrames);
        var output = (float[])input.Clone();

        for (int f = 0; f < frames; f += blockFrames)
        {
            int n = Math.Min(blockFrames, frames - f);
            line.Process(output.AsSpan(f * channels, n * channels));
        }

        Assert.All(output.AsSpan(0, delayFrames * channels).ToArray(), s => Assert.Equal(0f, s)); // silence first
        AssertBitExact(input.AsSpan(0, (frames - delayFrames) * channels), output.AsSpan(delayFrames * channels));
    }

    [Fact]
    public void DelayLine_FramesFor_RoundsAndRejectsNonsense()
    {
        Assert.Equal(9600, DelayLine.FramesFor(200, 48000));
        Assert.Equal(8820, DelayLine.FramesFor(200, 44100));
        Assert.Equal(0, DelayLine.FramesFor(0, 48000));
        Assert.Equal(0, DelayLine.FramesFor(-5, 48000));
        Assert.Equal(0, DelayLine.FramesFor(double.NaN, 48000));
        Assert.Equal(1, DelayLine.FramesFor(0.0105, 48000)); // 0.504 frames → 1
    }

    [Fact]
    public void DelayLine_SetDelay_IsClamped_AndCrossfadesWithoutAJump()
    {
        const int channels = 1;
        var line = new DelayLine(channels, maxDelayFrames: 100, crossfadeFrames: 10);
        line.SetDelay(1000);
        Assert.Equal(100, line.DelayFrames);
        line.SetDelay(-3);
        Assert.Equal(0, line.DelayFrames);

        // A slow ramp: switching the tap from 0 to 50 frames back must move gradually, never jump by the full step.
        var ramp = Enumerable.Range(0, 400).Select(i => i / 400f).ToArray();
        var first = ramp[..200];
        line.Process(first);
        AssertBitExact(ramp[..200], first); // delay 0: untouched

        line.SetDelay(50);
        var second = ramp[200..];
        line.Process(second);
        float previous = first[^1];
        for (int i = 0; i < 10; i++)
        {
            Assert.True(Math.Abs(second[i] - previous) < 0.02f, $"jump at {i}: {previous} → {second[i]}");
            previous = second[i];
        }

        // After the fade the output is exactly the input 50 frames back.
        for (int i = 10; i < second.Length; i++)
        {
            Assert.Equal(ramp[200 + i - 50], second[i]);
        }
    }

    [Fact]
    public void DelayLine_LeavesATrailingPartialFrameAlone_AndRejectsBadSizes()
    {
        var line = new DelayLine(2, 10, delayFrames: 3);
        var data = new float[] { 1, 2, 3, 4, 5 };
        line.Process(data);
        Assert.Equal(5f, data[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayLine(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayLine(2, -1));
    }

    // ---------------------------------------------------------------- volume

    [Fact]
    public void Gain_100Percent_IsBitExact()
    {
        var input = Noise(4096);
        var stage = new GainStage(2, GainStage.FromPercent(100));
        var buffer = (float[])input.Clone();

        stage.Process(buffer);

        Assert.Equal(1f, GainStage.FromPercent(100));
        AssertBitExact(input, buffer);
    }

    [Fact]
    public void Gain_50Percent_HalvesExactly()
    {
        var input = Noise(4096);
        var stage = new GainStage(2, GainStage.FromPercent(50));
        var buffer = (float[])input.Clone();

        stage.Process(buffer);

        Assert.Equal(0.5f, GainStage.FromPercent(50));
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i] / 2, buffer[i]);
        }
    }

    [Fact]
    public void Gain_IsNeverABoost_AndZeroIsSilence()
    {
        Assert.Equal(1f, GainStage.FromPercent(250));
        Assert.Equal(0f, GainStage.FromPercent(-10));
        var stage = new GainStage(2, 3f);
        Assert.Equal(1f, stage.Gain);
        stage.Gain = float.NaN;
        Assert.Equal(0f, stage.Gain);

        var silent = new GainStage(2, 0f);
        var buffer = Noise(100);
        silent.Process(buffer);
        Assert.All(buffer, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Gain_Change_IsRampedAcrossOneBlock_ThenSteady()
    {
        var stage = new GainStage(1, 1f);
        stage.Gain = 0.5f;
        var block = Enumerable.Repeat(1f, 100).ToArray();

        stage.Process(block);

        for (int i = 1; i < block.Length; i++)
        {
            Assert.True(block[i] <= block[i - 1] + 1e-6f, "the ramp only goes down");
        }

        Assert.True(block[0] > 0.99f);
        Assert.Equal(0.5f, block[^1]);

        var next = Enumerable.Repeat(1f, 10).ToArray();
        stage.Process(next);
        Assert.All(next, s => Assert.Equal(0.5f, s));
    }

    // ---------------------------------------------------------------- drift correction (pure rule)

    [Theory]
    [InlineData(1920, 1920, DriftAction.None)]   // at the setpoint
    [InlineData(2300, 2300, DriftAction.None)]   // inside the tolerance (480)
    [InlineData(1500, 1500, DriftAction.None)]
    [InlineData(2401, 2401, DriftAction.DropFrame)]      // above: the source clock is faster
    [InlineData(1439, 1439, DriftAction.DuplicateFrame)] // below: the source clock is slower
    public void Drift_Decide_AboveDrops_BelowDuplicates_InsideDoesNothing(double smoothed, int fill, DriftAction expected)
    {
        var d = DriftCorrection.Decide(smoothed, fill, setpointFrames: 1920, toleranceFrames: 480, hardLimitFrames: 7200, DriftAction.None);

        Assert.Equal(expected, d.Action);
        Assert.Equal(expected == DriftAction.None ? 0 : 1, d.Frames); // never more than one frame per block
    }

    [Fact]
    public void Drift_Decide_KeepsCorrecting_UntilTheAverageCrossesTheSetpoint()
    {
        // Hysteresis: a run of drops continues inside the tolerance and stops once the average is back at the setpoint.
        Assert.Equal(DriftAction.DropFrame, DriftCorrection.Decide(2000, 2000, 1920, 480, 7200, DriftAction.DropFrame).Action);
        Assert.Equal(DriftAction.None, DriftCorrection.Decide(1920, 1920, 1920, 480, 7200, DriftAction.DropFrame).Action);
        Assert.Equal(DriftAction.DuplicateFrame, DriftCorrection.Decide(1800, 1800, 1920, 480, 7200, DriftAction.DuplicateFrame).Action);
        Assert.Equal(DriftAction.None, DriftCorrection.Decide(1921, 1921, 1920, 480, 7200, DriftAction.DuplicateFrame).Action);
    }

    [Fact]
    public void Drift_Decide_HardLimit_TrimsStraightBackToTheSetpoint()
    {
        // Bounded: however the fill got there, latency is cut back to the setpoint in one step.
        var d = DriftCorrection.Decide(2000, 1920 + 7201, 1920, 480, 7200, DriftAction.None);

        Assert.Equal(DriftAction.Trim, d.Action);
        Assert.Equal(7201, d.Frames);
        Assert.Equal(DriftAction.None, DriftCorrection.Decide(1920, 1920 + 7200, 1920, 480, 7200, DriftAction.None).Action); // at the limit: not yet
    }

    [Fact]
    public void DriftController_IsDeterministic_AndIgnoresAPacketSizedSawtooth()
    {
        var a = new DriftController(1920, 480, 7200);
        var b = new DriftController(1920, 480, 7200);
        var actionsA = new List<DriftAction>();
        var actionsB = new List<DriftAction>();
        for (int i = 0; i < 10_000; i++)
        {
            int fill = 1920 + (i % 2 == 0 ? 480 : -480) + (i / 50); // ±10 ms sawtooth plus a slow climb
            actionsA.Add(a.Update(fill).Action);
            actionsB.Add(b.Update(fill).Action);
        }

        Assert.Equal(actionsA, actionsB);

        // The sawtooth alone (first blocks, average at the setpoint) triggers nothing.
        var c = new DriftController(1920, 480, 7200);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(DriftAction.None, c.Update(1920 + (i % 2 == 0 ? 470 : -470)).Action);
        }
    }

    [Fact]
    public void DriftController_RejectsBadParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DriftController(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DriftController(1, 0, 0, smoothing: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DriftController(1, 0, 0, smoothing: 1.5));
    }

    // ---------------------------------------------------------------- per-output buffer

    private static MirrorBuffer Buffer48k(int channels = 2) =>
        new(channels, Rate, MirrorBufferOptions.ForMilliseconds(Rate, setpointMs: 40, toleranceMs: 10, hardLimitMs: 150));

    [Fact]
    public void Buffer_Options_InFrames()
    {
        var o = MirrorBufferOptions.ForMilliseconds(Rate, 40, 10, 150, 1000, 2);

        Assert.Equal(1920, o.SetpointFrames);
        Assert.Equal(480, o.ToleranceFrames);
        Assert.Equal(7200, o.HardLimitFrames);
        Assert.Equal(48000, o.CapacityFrames);
        Assert.Equal(96, o.FadeFrames);
    }

    [Fact]
    public void Buffer_PrimesWithSilence_ThenPlaysTheInputExactly_AfterAShortFadeIn()
    {
        const int channels = 2;
        const int block = 480;
        var buffer = Buffer48k();
        var input = Noise(block * channels * 40, seed: 3);
        var output = new float[input.Length + block * channels * 8];
        int written = 0, read = 0;

        // Write 3 blocks: still below the 40 ms cushion → silence.
        for (int i = 0; i < 3; i++)
        {
            buffer.Write(input.AsSpan(written, block * channels));
            written += block * channels;
        }

        var probe = new float[block * channels];
        buffer.Read(probe);
        Assert.All(probe, s => Assert.Equal(0f, s));
        Assert.True(buffer.IsPriming);

        while (written < input.Length)
        {
            buffer.Write(input.AsSpan(written, block * channels));
            written += block * channels;
            buffer.Read(output.AsSpan(read, block * channels));
            read += block * channels;
        }

        Assert.False(buffer.IsPriming);
        int fade = buffer.Options.FadeFrames * channels;
        Assert.True(Math.Abs(output[0]) <= Math.Abs(input[0]) / 10 + 1e-6f, "the first frame is faded in");
        AssertBitExact(input.AsSpan(fade, read - fade), output.AsSpan(fade, read - fade));
        Assert.Equal(default, buffer.Stats);
    }

    [Fact]
    public void Buffer_Underrun_FadesOut_PadsSilence_AndPrimesAgain()
    {
        const int channels = 2;
        var buffer = Buffer48k();
        var tone = Enumerable.Repeat(0.5f, 2400 * channels).ToArray();
        buffer.Write(tone);

        var first = new float[2000 * channels];
        buffer.Read(first);
        var second = new float[1000 * channels]; // only 400 frames left: underrun inside this block
        buffer.Read(second);

        Assert.Equal(0.5f, second[0]);
        Assert.True(second[399 * channels] < 0.01f, "the last real frame is faded out, so the gap does not click");
        for (int i = 1; i < 400; i++)
        {
            Assert.True(second[i * channels] <= second[(i - 1) * channels] + 1e-6f);
        }

        Assert.All(second.AsSpan(400 * channels).ToArray(), s => Assert.Equal(0f, s));
        Assert.True(buffer.IsPriming);
        Assert.Equal(1, buffer.Stats.Underruns);

        // Nothing arrives (Windows sends no loopback packets while nothing plays): silence, no error.
        var idle = new float[480 * channels];
        for (int i = 0; i < 100; i++)
        {
            buffer.Read(idle);
            Assert.All(idle, s => Assert.Equal(0f, s));
        }

        Assert.Equal(1, buffer.Stats.Underruns); // idle silence is not counted again
    }

    [Fact]
    public void Buffer_Overflow_KeepsTheNewestAudio_LatencyNeverExceedsTheCapacity()
    {
        var buffer = new MirrorBuffer(1, Rate, new MirrorBufferOptions(SetpointFrames: 10, ToleranceFrames: 2, HardLimitFrames: 50, CapacityFrames: 100, FadeFrames: 0));
        buffer.Write(Enumerable.Range(0, 250).Select(i => (float)i).ToArray());

        Assert.Equal(100, buffer.BufferedFrames);
        Assert.Equal(150, buffer.Stats.OverflowFrames);

        var first = new float[20];
        buffer.Read(first); // primes and plays the newest 100 frames from their start
        Assert.Equal(150f, first[0]);

        // 80 frames left = 70 over the setpoint, past the 50-frame hard limit: cut straight back to the setpoint.
        var second = new float[5];
        buffer.Read(second);
        Assert.Equal(70, buffer.Stats.TrimmedFrames);
        Assert.Equal(240f, second[0]);
        Assert.Equal(5, buffer.BufferedFrames);
    }

    [Theory]
    [InlineData(+300)] // the headset's clock runs 300 ppm fast
    [InlineData(-300)] // … or slow
    [InlineData(+2000)]
    [InlineData(-1500)]
    public void Buffer_ClockDrift_ForThreeHours_StaysNearTheSetpoint_WithoutUnderruns(int ppm)
    {
        const int channels = 1;
        const int block = 480;
        var buffer = new MirrorBuffer(channels, Rate, MirrorBufferOptions.ForMilliseconds(Rate, setpointMs: 40, toleranceMs: 10, hardLimitMs: 150));
        var packet = new float[block * 2];
        var output = new float[block];
        double produced = 0;
        int minFill = int.MaxValue, maxFill = 0;
        long blocks = 3L * 3600 * Rate / block; // three hours of 10 ms render blocks

        for (long i = 0; i < blocks; i++)
        {
            // The capture delivers ~10 ms packets on its own clock.
            produced += block * (1 + ppm / 1_000_000.0);
            int whole = (int)produced;
            produced -= whole;
            Array.Fill(packet, 0.25f, 0, whole);
            buffer.Write(packet.AsSpan(0, whole));

            buffer.Read(output);
            if (i > 100)
            {
                int fill = buffer.BufferedFrames;
                minFill = Math.Min(minFill, fill);
                maxFill = Math.Max(maxFill, fill);
            }
        }

        var stats = buffer.Stats;
        Assert.Equal(0, stats.Underruns);
        Assert.Equal(0, stats.TrimmedFrames);
        Assert.Equal(0, stats.OverflowFrames);
        Assert.InRange(maxFill, 0, 1920 + 480 + 2 * block); // latency never grows
        Assert.True(minFill > 0, "never runs dry");

        long expectedCorrections = (long)Math.Abs(ppm / 1_000_000.0 * blocks * block);
        long corrections = ppm > 0 ? stats.DroppedFrames : stats.DuplicatedFrames;
        Assert.InRange(corrections, expectedCorrections - 1000, expectedCorrections + 1000);
        Assert.Equal(0, ppm > 0 ? stats.DuplicatedFrames : stats.DroppedFrames);
    }

    [Fact]
    public void Buffer_Clear_PrimesAgain_AndTrailingPartialFramesAreZeroed()
    {
        var buffer = Buffer48k();
        buffer.Write(Enumerable.Repeat(0.3f, 4000 * 2).ToArray());
        var output = new float[481 * 2 + 1];
        buffer.Read(output);
        Assert.Equal(0f, output[^1]);
        Assert.False(buffer.IsPriming);

        buffer.Clear();
        Assert.True(buffer.IsPriming);
        Assert.Equal(0, buffer.BufferedFrames);
    }
}
