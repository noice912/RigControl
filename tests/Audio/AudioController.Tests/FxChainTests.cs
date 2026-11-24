using AudioController.Audio;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The in-app Effects chain on the soundboard bus (pure DSP): GraphicEq with any (frequency, gain, Q) bands, Bass Boost,
/// Surround's mid/side width, the Schroeder Ambience, bypass, and bit-exact identity cases.
/// </summary>
public sealed class FxChainTests
{
    private const int Rate = 48_000;

    private static float[] StereoSine(double hz, double seconds, float amplitude = 0.1f, bool leftOnly = false)
    {
        int frames = (int)(Rate * seconds);
        var buffer = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
            buffer[2 * i] = v;
            buffer[2 * i + 1] = leftOnly ? 0f : v;
        }

        return buffer;
    }

    private static float[] Noise(int frames, int seed = 3)
    {
        var rng = new Random(seed);
        var b = new float[frames * 2];
        for (int i = 0; i < b.Length; i++) b[i] = (float)(rng.NextDouble() * 0.5 - 0.25);
        return b;
    }

    private static double RmsDb(float[] buffer, int startFrame = 0, int channel = -1)
    {
        double sum = 0;
        int n = 0;
        for (int i = startFrame * 2; i < buffer.Length; i++)
        {
            if (channel >= 0 && i % 2 != channel) continue;
            sum += (double)buffer[i] * buffer[i];
            n++;
        }

        return 10 * Math.Log10(sum / n);
    }

    private static void ProcessInBlocks(FxChain fx, float[] buffer, int block = 960)
    {
        for (int o = 0; o < buffer.Length; o += block) fx.Process(buffer, o, Math.Min(block, buffer.Length - o));
    }

    private static FxState State(EffectLevels effects, float[]? gains = null, bool eq = true)
    {
        var s = new AppSettings { EqEnabled = eq, FxEnabled = true, EqGainsDb = gains ?? new float[10] };
        FxSettings.Normalize(s);
        s.Effects = effects;
        return FxState.From(s);
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public void Neutral_IsBitExact()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels()));
        var input = Noise(Rate / 2);
        var output = (float[])input.Clone();

        ProcessInBlocks(fx, output);

        Assert.Equal(input, output);
        Assert.False(fx.IsActive);
    }

    [Fact]
    public void Bypassed_IsBitExact_EvenWithEverythingAtFull()
    {
        var fx = new FxChain(Rate, 2) { Bypassed = true };
        fx.SetState(State(new EffectLevels { Bass = 100, Fidelity = 100, Ambience = 100, Surround = 100, DynamicBoost = 100 },
            new[] { 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f }));
        var input = Noise(Rate / 4);
        var output = (float[])input.Clone();

        ProcessInBlocks(fx, output);

        Assert.Equal(input, output);
    }

    [Fact]
    public void AmbienceAtZero_AddsNothing_TheEqAloneIsBitExactWithGraphicEq()
    {
        var state = State(new EffectLevels { Ambience = 0 }, new[] { 0f, 0f, 0f, 0f, 0f, 9f, 0f, 0f, 0f, 0f });
        var fx = new FxChain(Rate, 2);
        fx.SetState(state);
        var eq = new GraphicEq(Rate, 2) { Enabled = true };
        eq.SetBands(state.EqFilters);

        var input = Noise(Rate / 4);
        var a = (float[])input.Clone();
        var b = (float[])input.Clone();
        ProcessInBlocks(fx, a);
        for (int o = 0; o < b.Length; o += 960) eq.Process(b, o, Math.Min(960, b.Length - o));

        Assert.Equal(b, a);
        Assert.NotEqual(input, a);
    }

    [Fact]
    public void SurroundAtZero_LeavesLeftAndRightUnchanged()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels { Surround = 0 }));
        var input = StereoSine(1000, 0.2, leftOnly: true);
        var output = (float[])input.Clone();

        ProcessInBlocks(fx, output);

        Assert.Equal(input, output);
    }

    // ---------------------------------------------------------------- effects

    [Fact]
    public void BassBoost_Raises50Hz_AndLeaves5kHzAlone()
    {
        var fx50 = new FxChain(Rate, 2);
        fx50.SetState(State(new EffectLevels { Bass = 100 }));
        var low = StereoSine(50, 1.0);
        double before = RmsDb(low, Rate / 2);
        ProcessInBlocks(fx50, low);
        double after = RmsDb(low, Rate / 2);

        var fx5k = new FxChain(Rate, 2);
        fx5k.SetState(State(new EffectLevels { Bass = 100 }));
        var high = StereoSine(5000, 0.5);
        double beforeHigh = RmsDb(high, Rate / 4);
        ProcessInBlocks(fx5k, high);
        double afterHigh = RmsDb(high, Rate / 4);

        Assert.InRange(after - before, 9.5, 12.01);   // RBJ shelf centred at 100 Hz: ~+10.9 dB an octave below
        Assert.InRange(afterHigh - beforeHigh, -0.1, 0.1);
    }

    [Fact]
    public void Surround_AtFull_DoublesTheSide_AndKeepsTheMid()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels { Surround = 100 }));
        var input = StereoSine(1000, 0.2, leftOnly: true); // L = s, R = 0
        var output = (float[])input.Clone();

        ProcessInBlocks(fx, output);

        double sideIn = 0, sideOut = 0, midIn = 0, midOut = 0;
        for (int i = 0; i < input.Length; i += 2)
        {
            sideIn += Math.Pow(input[i] - input[i + 1], 2);
            sideOut += Math.Pow(output[i] - output[i + 1], 2);
            midIn += Math.Pow(input[i] + input[i + 1], 2);
            midOut += Math.Pow(output[i] + output[i + 1], 2);
        }

        Assert.Equal(4, sideOut / sideIn, 3);          // |L−R| ×2 → energy ×4
        Assert.Equal(1, midOut / midIn, 3);            // L+R unchanged
        Assert.Equal(1.5, output[2 * 100] / input[2 * 100], 3);          // L' = 1.5 L − 0.5 R (= the APO Copy line)
        Assert.Equal(-0.5, output[2 * 100 + 1] / input[2 * 100], 3);     // R' = 1.5 R − 0.5 L
    }

    [Fact]
    public void Surround_OnMonoContent_ChangesNothing()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels { Surround = 100 }));
        var input = StereoSine(440, 0.1); // L = R
        var output = (float[])input.Clone();

        ProcessInBlocks(fx, output);

        for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i], 6);
    }

    [Fact]
    public void Ambience_AddsATail_WithEnergyNearWetSquared_AndKeepsTheDrySample()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels { Ambience = 100 }));
        var buffer = new float[Rate * 2]; // 1 s
        buffer[0] = 1f;
        buffer[1] = 1f;

        ProcessInBlocks(fx, buffer);

        Assert.Equal(1f, buffer[0]); // the comb delays are > 20 ms: frame 0 is dry
        double tail = 0;
        for (int i = 2; i < buffer.Length; i += 2) tail += (double)buffer[i] * buffer[i];
        double wet = FxEffects.AmbienceWet(100);
        Assert.InRange(tail / (wet * wet), 0.5, 2.0);

        double lateTail = 0;
        for (int i = (int)(0.6 * Rate) * 2; i < buffer.Length; i += 2) lateTail += (double)buffer[i] * buffer[i];
        Assert.True(lateTail < tail * 1e-5, "RT60 0.35 s: almost nothing is left after 0.6 s");
    }

    [Fact]
    public void Ambience_TailsDiffer_LeftToRight()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels { Ambience = 100 }));
        var buffer = new float[Rate / 2 * 2];
        buffer[0] = 1f;
        buffer[1] = 1f;

        ProcessInBlocks(fx, buffer);

        double l = 0, r = 0, diff = 0;
        for (int i = 2; i < buffer.Length; i += 2)
        {
            l += (double)buffer[i] * buffer[i];
            r += (double)buffer[i + 1] * buffer[i + 1];
            diff += Math.Pow(buffer[i] - buffer[i + 1], 2);
        }

        Assert.True(l > 0 && r > 0);
        Assert.True(diff / (l + r) > 0.5, $"tails should be largely decorrelated (diff/(l+r) = {diff / (l + r):0.00})");
    }

    [Fact]
    public void DigitalSilence_StaysExactlyZero_AfterTheTailHasRungOut()
    {
        var fx = new FxChain(Rate, 2);
        fx.SetState(State(new EffectLevels { Bass = 100, Ambience = 100, Surround = 100 }));
        var tone = StereoSine(200, 0.2);
        ProcessInBlocks(fx, tone);

        var silence = new float[Rate * 2]; // 1 s
        ProcessInBlocks(fx, silence);
        var after = new float[Rate / 10 * 2];
        ProcessInBlocks(fx, after);

        Assert.All(after, v => Assert.Equal(0f, v));
    }

    // ---------------------------------------------------------------- GraphicEq

    [Fact]
    public void GraphicEq_31Bands_AllBuilt_And1kBandBoosts1kTone()
    {
        var s = new AppSettings { EqEnabled = true, EqMode = EqMode.ThirtyOneBandThirdOctave };
        FxSettings.Normalize(s);
        for (int i = 0; i < 31; i++) s.EqGains31Db![i] = 0.5f;
        s.EqGains31Db![17] = 12f; // 1 kHz
        var state = FxState.From(s);

        var eq = new GraphicEq(Rate, 2) { Enabled = true };
        eq.SetBands(state.EqFilters);
        Assert.Equal(31, state.EqFilters.Count);
        Assert.All(state.EqFilters, f => Assert.Equal(4.32, f.Q));
        Assert.Equal(31, eq.ActiveFilterCount); // 20 kHz < 0.45 × 48 kHz

        var tone = StereoSine(1000, 0.5);
        double before = RmsDb(tone, Rate / 4);
        for (int o = 0; o < tone.Length; o += 960) eq.Process(tone, o, Math.Min(960, tone.Length - o));
        Assert.InRange(RmsDb(tone, Rate / 4) - before, 12, 14); // +12 plus a little from the 0.5 dB neighbours
    }

    [Fact]
    public void GraphicEq_AdjustableBand_FollowsItsFrequency()
    {
        var eq = new GraphicEq(Rate, 2) { Enabled = true };
        eq.SetBands(new[] { new FxFilter(FxFilterType.Peaking, 3000, 12, 1) });

        var at3k = StereoSine(3000, 0.5);
        double before = RmsDb(at3k, Rate / 4);
        for (int o = 0; o < at3k.Length; o += 960) eq.Process(at3k, o, Math.Min(960, at3k.Length - o));

        Assert.Equal(12, RmsDb(at3k, Rate / 4) - before, 0);
    }

    [Fact]
    public void GraphicEq_SkipsFlatAndNearNyquistBands_AndClampsGains()
    {
        var eq = new GraphicEq(44_100, 2) { Enabled = true };
        eq.SetBands(new[]
        {
            new FxFilter(FxFilterType.Peaking, 1000, 0.005, 1),  // flat
            new FxFilter(FxFilterType.Peaking, 20000, 6, 4.32), // ≥ 0.45 × 44.1 kHz
            new FxFilter(FxFilterType.Peaking, 500, 99, 1),     // clamped to +24
            new FxFilter(FxFilterType.Peaking, double.NaN, 6, 1), // dropped
        });

        Assert.Equal(1, eq.ActiveFilterCount);
        Assert.Contains(eq.GetBands(), b => b.FrequencyHz == 500 && b.GainDb == 24);
    }

    [Fact]
    public void ShelfSlope_ForQ0707_IsOne_SoTheShelfMatchesBiQuadLowShelfS1()
    {
        Assert.Equal(1, GraphicEq.ShelfSlope(1 / Math.Sqrt(2), 12), 9);
        Assert.Equal(1, GraphicEq.ShelfSlope(FxEffects.ShelfQ, -6), 4);
        Assert.Equal(1, GraphicEq.ShelfSlope(FxEffects.ShelfQ, 12), 4);
    }

    [Fact]
    public void Engine_SetFx_NeverTouchesTheMicPath_ByContract()
    {
        // The engine applies an FxChain only to bus mixers: SetEq/SetFx record the state while stopped and never throw.
        using var engine = new AudioEngine();
        engine.SetFx(State(new EffectLevels { Bass = 100, Ambience = 100 }));
        engine.SetEq(new float[] { 6 }, true);
        Assert.Single(engine.Fx.EqFilters);
        Assert.Empty(engine.Fx.EffectFilters);
        Assert.Throws<ArgumentNullException>(() => engine.SetFx(null!));
    }
}
