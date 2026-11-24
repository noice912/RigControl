using AudioController.Core.AudioMath;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// SPEC 4.3 / 7 "Mixer". <see cref="MixerMath.MixInto"/> is the only place soundboard audio meets the mic.
/// </summary>
/// <remarks>
/// When no sound is playing the engine (RoutingWaveProvider, internal) does not call MixInto at all, so the
/// mic buffer is untouched by construction. These tests pin down the pure math: MixInto is plain additive
/// summation with a clamp to ±1.0 applied only to the samples it mixes, and nothing else.
/// </remarks>
public sealed class MixerTests
{
    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    private static void AssertBitwiseEqual(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Bits(expected[i]) == Bits(actual[i]),
                $"Sample {i}: expected {expected[i]:R} (0x{Bits(expected[i]):X8}) but got {actual[i]:R} (0x{Bits(actual[i]):X8}).");
        }
    }

    /// <summary>Deterministic "microphone" samples in (-1, 1), never negative zero.</summary>
    private static float[] MicSamples(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = (float)(rng.NextDouble() * 2 - 1);
            samples[i] = v == 0f ? 0.5f : v;
        }

        return samples;
    }

    [Fact]
    public void MixInto_SilenceIntoSilence_IsSilence()
    {
        var mic = new float[960];
        var board = new float[960];

        MixerMath.MixInto(mic, 0, mic.Length, board, 0);

        AssertBitwiseEqual(new float[960], mic);
    }

    [Fact]
    public void NoMixCall_WhenNoSoundPlays_MicSamplesAreBitExact()
    {
        // The engine's contract: zero sounds playing => MixInto is skipped. A buffer that is never handed
        // to the mixer is, trivially but importantly, untouched; this documents that the mic path has
        // no other processing step in Core.
        var mic = MicSamples(480);
        var copy = (float[])mic.Clone();

        AssertBitwiseEqual(copy, mic);
    }

    [Fact]
    public void MixInto_SilentBoard_LeavesMicSamplesBitExact()
    {
        // Even if the engine did sum a silent bus, in-range mic samples would come out bit-identical.
        var mic = MicSamples(4800);
        mic[0] = 1f;
        mic[1] = -1f;
        mic[2] = float.Epsilon;           // smallest denormal must survive
        mic[3] = -float.Epsilon;
        mic[4] = 0.999999940f;            // largest float below 1
        var expected = (float[])mic.Clone();

        MixerMath.MixInto(mic, 0, mic.Length, new float[mic.Length], 0);

        AssertBitwiseEqual(expected, mic);
    }

    [Fact]
    public void MixInto_RandomInRangeSignals_IsExactlyClampedFloatSum()
    {
        var mic = MicSamples(10_000, seed: 1);
        var board = MicSamples(10_000, seed: 2);
        var micCopy = (float[])mic.Clone();

        MixerMath.MixInto(mic, 0, mic.Length, board, 0);

        for (int i = 0; i < mic.Length; i++)
        {
            float expected = Math.Clamp(micCopy[i] + board[i], -1f, 1f);
            Assert.True(Bits(expected) == Bits(mic[i]), $"Sample {i}: {micCopy[i]:R} + {board[i]:R} => {mic[i]:R}, expected {expected:R}.");
        }
    }

    [Fact]
    public void MixInto_DoesNotModifyBoardBuffer()
    {
        var mic = MicSamples(256, seed: 3);
        var board = MicSamples(256, seed: 4);
        var boardCopy = (float[])board.Clone();

        MixerMath.MixInto(mic, 0, mic.Length, board, 0);

        AssertBitwiseEqual(boardCopy, board);
    }

    [Theory]
    [InlineData(0.8f, 0.5f, 1.0f)]
    [InlineData(-0.8f, -0.5f, -1.0f)]
    [InlineData(1.0f, 1.0f, 1.0f)]
    [InlineData(-1.0f, -1.0f, -1.0f)]
    [InlineData(1.0f, 0.0001f, 1.0f)]
    [InlineData(0.9f, 100f, 1.0f)]
    [InlineData(-0.9f, -100f, -1.0f)]
    public void MixInto_SumOutsideRange_ClampsToPlusMinusOne(float mic, float board, float expected)
    {
        var micBuffer = new[] { mic };

        MixerMath.MixInto(micBuffer, 0, 1, new[] { board }, 0);

        Assert.Equal(Bits(expected), Bits(micBuffer[0]));
    }

    [Theory]
    [InlineData(0.25f, 0.25f, 0.5f)]
    [InlineData(0.5f, 0.5f, 1.0f)]       // exactly at the limit: not "clipped", just 1.0
    [InlineData(-0.5f, -0.5f, -1.0f)]
    [InlineData(0.75f, -0.25f, 0.5f)]
    [InlineData(-0.125f, 0.0625f, -0.0625f)]
    [InlineData(0.8f, -0.8f, 0.0f)]
    public void MixInto_SumInsideRange_IsExact(float mic, float board, float expected)
    {
        var micBuffer = new[] { mic };

        MixerMath.MixInto(micBuffer, 0, 1, new[] { board }, 0);

        Assert.Equal(Bits(expected), Bits(micBuffer[0]));
    }

    [Fact]
    public void MixInto_LoudBoardOnSilentMic_OutputsBoardClampedToRange()
    {
        var mic = new float[4];
        var board = new[] { 1.5f, -2.0f, 0.3f, -0.3f };

        MixerMath.MixInto(mic, 0, 4, board, 0);

        AssertBitwiseEqual(new[] { 1.0f, -1.0f, 0.3f, -0.3f }, mic);
    }

    [Fact]
    public void MixInto_SubRange_OnlyTouchesRequestedSamples_AndHonoursBoardOffset()
    {
        // Samples outside [offset, offset+count) — including out-of-range values — are never touched or clamped.
        var mic = new[] { 5.0f, 0.1f, 0.2f, 0.3f, -7.0f };
        var board = new[] { 9f, 9f, 0.5f, 0.5f, 0.5f, 9f };

        MixerMath.MixInto(mic, offset: 1, count: 3, board, boardOffset: 2);

        AssertBitwiseEqual(new[] { 5.0f, 0.1f + 0.5f, 0.2f + 0.5f, 0.3f + 0.5f, -7.0f }, mic);
    }

    [Fact]
    public void MixInto_ZeroCount_ChangesNothing()
    {
        var mic = new[] { 3f, -3f };
        MixerMath.MixInto(mic, 0, 0, new[] { 1f, 1f }, 0);

        AssertBitwiseEqual(new[] { 3f, -3f }, mic);
    }

    [Fact]
    public void MixInto_NullBuffers_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MixerMath.MixInto(null!, 0, 0, new float[1], 0));
        Assert.Throws<ArgumentNullException>(() => MixerMath.MixInto(new float[1], 0, 0, null!, 0));
    }
}
