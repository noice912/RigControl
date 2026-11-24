using System.Globalization;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace AudioController.Tests;

/// <summary>
/// The Effects panel's Equalizer APO file (pure): the exact text of every effect at 0 / 50 / 100 %, line order, the auto
/// pre-amp (band boost + effect boost), Copy only on stereo and never at 0, Convolution iff Ambience &gt; 0, the 31-band
/// layout, frequency clamping, and invariant numbers under any culture. Syntax per the Equalizer APO Configuration
/// reference: "Filter n: ON PK|LS|HS Fc … Hz Gain … dB Q …", "Copy: L=a*L+-k*R …", "Convolution: file".
/// </summary>
public sealed class FxApoGeneratorTests
{
    private const string Selector = "{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string Head =
        "# Written by Audio Controller from its EQ sliders - edits here are overwritten. Applies to your headset only.\r\n" +
        "Device: " + Selector + "\r\n" +
        "Channel: all\r\n";
    private const string Tail = "Device: all\r\n";

    private readonly ITestOutputHelper _output;

    public FxApoGeneratorTests(ITestOutputHelper output) => _output = output;

    /// <summary>Power on, EQ on, flat 10 bands, the given effects.</summary>
    private static AppSettings Settings(EffectLevels? effects = null, float[]? gains = null)
    {
        var s = new AppSettings { EqEnabled = true, FxEnabled = true, EqGainsDb = gains ?? new float[10] };
        FxSettings.Normalize(s);
        if (effects is not null) s.Effects = effects;
        return s;
    }

    private static string Gen(AppSettings s, bool autoPreamp = true, int channels = 2) =>
        EqualizerApoConfig.Generate(FxState.From(s), Selector, autoPreamp, channels);

    // ---------------------------------------------------------------- neutral

    [Fact]
    public void EverythingFlatAndZero_IsTheNeutralScopedFile()
    {
        Assert.Equal(Head + "Preamp: 0 dB\r\n" + Tail, Gen(Settings()));
    }

    [Fact]
    public void PowerOff_IsTheNeutralScopedFile_WhateverTheSettings()
    {
        var s = Settings(new EffectLevels { Bass = 100, Fidelity = 100, Ambience = 100, Surround = 100, DynamicBoost = 100 },
            new[] { 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f });
        s.FxEnabled = false;

        Assert.Equal(Head + "Preamp: 0 dB\r\n" + Tail, Gen(s));
        Assert.True(FxState.From(s).IsNeutral);
    }

    [Fact]
    public void EqSwitchOff_DropsTheBands_ButKeepsTheEffects()
    {
        var s = Settings(new EffectLevels { Bass = 50 }, new[] { 6f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        s.EqEnabled = false;

        Assert.Equal(Head + "Preamp: -6 dB\r\n" + "Filter 1: ON LS Fc 100 Hz Gain 6 dB Q 0.7071\r\n" + Tail, Gen(s));
    }

    // ---------------------------------------------------------------- each effect at 0 / 50 / 100 %

    [Theory]
    [InlineData(0, "Preamp: 0 dB\r\n")]
    [InlineData(50, "Preamp: -6 dB\r\nFilter 1: ON LS Fc 100 Hz Gain 6 dB Q 0.7071\r\n")]
    [InlineData(100, "Preamp: -12 dB\r\nFilter 1: ON LS Fc 100 Hz Gain 12 dB Q 0.7071\r\n")]
    public void BassBoost_IsALowShelfAt100Hz_UpTo12dB(int percent, string body)
    {
        Assert.Equal(Head + body + Tail, Gen(Settings(new EffectLevels { Bass = percent })));
    }

    [Theory]
    [InlineData(0, "Preamp: 0 dB\r\n")]
    [InlineData(50, "Preamp: -6 dB\r\nFilter 1: ON HS Fc 6000 Hz Gain 4 dB Q 0.7071\r\nFilter 2: ON PK Fc 12000 Hz Gain 2 dB Q 1\r\n")]
    [InlineData(100, "Preamp: -12 dB\r\nFilter 1: ON HS Fc 6000 Hz Gain 8 dB Q 0.7071\r\nFilter 2: ON PK Fc 12000 Hz Gain 4 dB Q 1\r\n")]
    public void Fidelity_IsAHighShelfAt6k_PlusAnAirPeakAt12k(int percent, string body)
    {
        Assert.Equal(Head + body + Tail, Gen(Settings(new EffectLevels { Fidelity = percent })));
    }

    [Theory]
    [InlineData(0, "Preamp: 0 dB\r\n")]
    [InlineData(50, "Preamp: -3 dB\r\nFilter 1: ON LS Fc 100 Hz Gain 3 dB Q 0.7071\r\nFilter 2: ON HS Fc 8000 Hz Gain 2 dB Q 0.7071\r\n")]
    [InlineData(100, "Preamp: -6 dB\r\nFilter 1: ON LS Fc 100 Hz Gain 6 dB Q 0.7071\r\nFilter 2: ON HS Fc 8000 Hz Gain 4 dB Q 0.7071\r\n")]
    public void DynamicBoost_IsALoudnessContour_LowAndHighShelves(int percent, string body)
    {
        Assert.Equal(Head + body + Tail, Gen(Settings(new EffectLevels { DynamicBoost = percent })));
    }

    [Theory]
    [InlineData(0, "Preamp: 0 dB\r\n")]
    [InlineData(50, "Preamp: -1.938 dB\r\nCopy: L=1.25*L+-0.25*R R=1.25*R+-0.25*L\r\n")]
    [InlineData(100, "Preamp: -3.522 dB\r\nCopy: L=1.5*L+-0.5*R R=1.5*R+-0.5*L\r\n")]
    public void Surround_IsAStereoWideningCopy(int percent, string body)
    {
        Assert.Equal(Head + body + Tail, Gen(Settings(new EffectLevels { Surround = percent })));
    }

    [Theory]
    [InlineData(0, "Preamp: 0 dB\r\n")]
    [InlineData(50, "Preamp: 0 dB\r\nConvolution: ambience.wav\r\n")]
    [InlineData(100, "Preamp: 0 dB\r\nConvolution: ambience.wav\r\n")]
    public void Ambience_IsAConvolutionWithTheGeneratedImpulse(int percent, string body)
    {
        Assert.Equal(Head + body + Tail, Gen(Settings(new EffectLevels { Ambience = percent })));
    }

    // ---------------------------------------------------------------- order, pre-amp, scoping

    [Fact]
    public void AllFiveAt50_WithAnEq_TheExampleFile_InChainOrder()
    {
        var s = Settings(new EffectLevels { Bass = 50, Fidelity = 50, Ambience = 50, Surround = 50, DynamicBoost = 50 },
            new[] { 4f, 3f, 1f, -1f, -2f, 0f, 2f, 3f, 4f, 3f });

        string text = Gen(s);
        _output.WriteLine(text);

        Assert.Equal(
            Head +
            "Preamp: -13 dB\r\n" + // −(largest band boost 4 + largest effect boost: Bass 6 + Dynamic low 3)
            "Filter 1: ON PK Fc 32 Hz Gain 4 dB Q 1\r\n" +
            "Filter 2: ON PK Fc 64 Hz Gain 3 dB Q 1\r\n" +
            "Filter 3: ON PK Fc 125 Hz Gain 1 dB Q 1\r\n" +
            "Filter 4: ON PK Fc 250 Hz Gain -1 dB Q 1\r\n" +
            "Filter 5: ON PK Fc 500 Hz Gain -2 dB Q 1\r\n" +
            "Filter 6: ON PK Fc 2000 Hz Gain 2 dB Q 1\r\n" +
            "Filter 7: ON PK Fc 4000 Hz Gain 3 dB Q 1\r\n" +
            "Filter 8: ON PK Fc 8000 Hz Gain 4 dB Q 1\r\n" +
            "Filter 9: ON PK Fc 16000 Hz Gain 3 dB Q 1\r\n" +
            "Filter 10: ON LS Fc 100 Hz Gain 6 dB Q 0.7071\r\n" +
            "Filter 11: ON HS Fc 6000 Hz Gain 4 dB Q 0.7071\r\n" +
            "Filter 12: ON PK Fc 12000 Hz Gain 2 dB Q 1\r\n" +
            "Filter 13: ON LS Fc 100 Hz Gain 3 dB Q 0.7071\r\n" +
            "Filter 14: ON HS Fc 8000 Hz Gain 2 dB Q 0.7071\r\n" +
            "Copy: L=1.25*L+-0.25*R R=1.25*R+-0.25*L\r\n" +
            "Convolution: ambience.wav\r\n" +
            Tail,
            text);
    }

    [Fact]
    public void LineOrder_IsScope_Preamp_Bands_Effects_Copy_Convolution_Reset()
    {
        var s = Settings(new EffectLevels { Bass = 80, Fidelity = 30, Ambience = 10, Surround = 70, DynamicBoost = 60 },
            new[] { 0f, 0f, 0f, 0f, 0f, -6f, 0f, 0f, 0f, 0f });
        var commands = Gen(s).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToList();

        Assert.Equal("Device: " + Selector, commands[0]);
        Assert.Equal("Channel: all", commands[1]);
        Assert.StartsWith("Preamp: ", commands[2]);
        Assert.StartsWith("Filter 1: ON PK Fc 1000 Hz", commands[3]); // the EQ band first
        Assert.StartsWith("Filter 2: ON LS Fc 100 Hz", commands[4]);  // Bass Boost
        Assert.StartsWith("Filter 3: ON HS Fc 6000 Hz", commands[5]); // Fidelity shelf
        Assert.StartsWith("Filter 4: ON PK Fc 12000 Hz", commands[6]); // Fidelity air
        Assert.StartsWith("Filter 5: ON LS Fc 100 Hz", commands[7]);  // Dynamic low
        Assert.StartsWith("Filter 6: ON HS Fc 8000 Hz", commands[8]); // Dynamic high
        Assert.StartsWith("Copy: ", commands[9]);
        Assert.Equal("Convolution: ambience.wav", commands[10]);
        Assert.Equal("Device: all", commands[11]);
        Assert.Equal(12, commands.Count);
    }

    [Fact]
    public void AutoPreamp_AddsTheEffectBoostToTheLargestBandBoost()
    {
        // Band +5 dB; Bass 100 % (+12 dB) + Dynamic 50 % (+3 dB low) → −(5 + 15) = −20 dB.
        var s = Settings(new EffectLevels { Bass = 100, DynamicBoost = 50 }, new[] { 0f, 5f, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Contains("Preamp: -20 dB\r\n", Gen(s));

        // The top end can be the bigger stack: Fidelity 100 % (8 + 4) + Dynamic 100 % high (+4) = 16 > low 6.
        var top = Settings(new EffectLevels { Fidelity = 100, DynamicBoost = 100 });
        Assert.Contains("Preamp: -16 dB\r\n", Gen(top));
        Assert.Equal(16, FxEffects.BoostDb(top.Effects));

        // Cuts never raise the pre-amp; off → 0 dB.
        var cuts = Settings(new EffectLevels(), new[] { -6f, 0, 0, 0, 0, 0, 0, 0, 0, -3f });
        Assert.Contains("Preamp: 0 dB\r\n", Gen(cuts));
        Assert.Contains("Preamp: 0 dB\r\n", Gen(s, autoPreamp: false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(8)]
    public void Copy_IsWrittenOnlyForStereoDevices(int channels)
    {
        var s = Settings(new EffectLevels { Surround = 100, Bass = 50 });

        string text = Gen(s, channels: channels);

        Assert.DoesNotContain("Copy:", text);
        Assert.Contains("Filter 1: ON LS Fc 100 Hz Gain 6 dB Q 0.7071", text); // the rest is still there
        Assert.Contains("Copy: L=1.5*L+-0.5*R R=1.5*R+-0.5*L", Gen(s, channels: 2));
    }

    [Fact]
    public void Copy_IsOmittedAtZero_AndConvolution_OnlyWhenAmbienceIsOn()
    {
        for (int pct = 0; pct <= 100; pct += 10)
        {
            string text = Gen(Settings(new EffectLevels { Surround = pct, Ambience = pct }));
            Assert.Equal(pct > 0, text.Contains("Copy:", StringComparison.Ordinal));
            Assert.Equal(pct > 0, text.Contains("Convolution: ambience.wav", StringComparison.Ordinal));
        }

        Assert.Equal("Convolution: " + AmbienceImpulse.FileName, EqualizerApoConfig.ConvolutionLine);
    }

    [Fact]
    public void EveryCommand_StaysInsideTheHeadsetScope()
    {
        var s = Settings(new EffectLevels { Bass = 100, Fidelity = 100, Ambience = 100, Surround = 100, DynamicBoost = 100 },
            new[] { 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f });
        var commands = Gen(s).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToList();

        Assert.Equal("Device: " + Selector, commands[0]);
        Assert.Equal("Device: all", commands[^1]);
        Assert.Equal(2, commands.Count(c => c.StartsWith("Device:", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, c => c.StartsWith("Stage:", StringComparison.Ordinal) || c.StartsWith("Include:", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 31 bands

    [Fact]
    public void ThirdOctave_Emits31PeakingLines_AtTheIsoFrequencies_WithQ432()
    {
        var s = Settings();
        s.EqMode = EqMode.ThirtyOneBandThirdOctave;
        for (int i = 0; i < 31; i++) s.EqGains31Db![i] = (i % 2 == 0 ? 1 : -1) * (1 + i % 5);

        var lines = Gen(s, autoPreamp: false).Split("\r\n").Where(l => l.StartsWith("Filter ", StringComparison.Ordinal)).ToList();

        Assert.Equal(31, lines.Count);
        string[] iso =
        [
            "20", "25", "31.5", "40", "50", "63", "80", "100", "125", "160", "200", "250", "315", "400", "500", "630", "800",
            "1000", "1250", "1600", "2000", "2500", "3150", "4000", "5000", "6300", "8000", "10000", "12500", "16000", "20000",
        ];
        for (int i = 0; i < 31; i++)
        {
            string g = s.EqGains31Db![i].ToString("0.###", CultureInfo.InvariantCulture);
            Assert.Equal($"Filter {i + 1}: ON PK Fc {iso[i]} Hz Gain {g} dB Q 4.32", lines[i]);
        }

        Assert.Equal(31, FxSettings.ThirdOctaveFrequenciesHz.Count);
    }

    [Fact]
    public void ThirdOctave_IgnoresTheTenBands_AndViceVersa()
    {
        var s = Settings(gains: new[] { 6f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        s.EqGains31Db![17] = -3f; // 1 kHz

        Assert.Contains("Fc 32 Hz Gain 6 dB Q 1", Gen(s));
        Assert.DoesNotContain("Q 4.32", Gen(s));

        s.EqMode = EqMode.ThirtyOneBandThirdOctave;
        Assert.Contains("Filter 1: ON PK Fc 1000 Hz Gain -3 dB Q 4.32", Gen(s));
        Assert.DoesNotContain("Fc 32 Hz", Gen(s));
    }

    // ---------------------------------------------------------------- adjustable frequencies

    [Fact]
    public void EditedFrequencies_AreWritten_ClampedTo20To20000_AndStrictlyIncreasing()
    {
        var s = Settings(gains: new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f });

        Assert.Equal(20, FxSettings.SetTenBandFrequency(s, 0, 5));          // below 20 Hz
        Assert.Equal(20000, FxSettings.SetTenBandFrequency(s, 9, 96000));   // above 20 kHz
        Assert.Equal(1999, FxSettings.SetTenBandFrequency(s, 5, 2500));     // not past its neighbour (2000)
        Assert.Equal(501, FxSettings.SetTenBandFrequency(s, 5, 100));       // nor below the previous one (500)
        Assert.Equal(1234, FxSettings.SetTenBandFrequency(s, 5, 1234.4));   // whole hertz
        Assert.Equal(1234, FxSettings.SetTenBandFrequency(s, 5, double.NaN)); // NaN keeps it

        var rng = new Random(7);
        for (int n = 0; n < 500; n++)
        {
            FxSettings.SetTenBandFrequency(s, rng.Next(10), rng.NextDouble() * 30000 - 1000);
            var f = s.EqBands!.Select(b => b.FrequencyHz).ToList();
            Assert.All(f, hz => Assert.InRange(hz, 20, 20000));
            for (int i = 1; i < f.Count; i++) Assert.True(f[i] > f[i - 1], $"bands not increasing: {string.Join(", ", f)}");
        }

        var filters = Gen(s, autoPreamp: false).Split("\r\n").Where(l => l.StartsWith("Filter ", StringComparison.Ordinal)).ToList();
        Assert.Equal(10, filters.Count);
        for (int i = 0; i < 10; i++)
        {
            string hz = s.EqBands![i].FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
            Assert.StartsWith($"Filter {i + 1}: ON PK Fc {hz} Hz Gain 1 dB Q 1", filters[i]);
        }
    }

    // ---------------------------------------------------------------- culture

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void UsesInvariantNumbers_UnderAnyCulture(string culture)
    {
        var s = Settings(new EffectLevels { Bass = 33, Fidelity = 33, Surround = 33, DynamicBoost = 33, Ambience = 33 },
            new[] { -2.5f, 0, 0, 0, 0, 1.25f, 0, 0, 0, 0 });
        string expected;
        using (CultureScope.Invariant())
        {
            expected = Gen(s);
        }

        using (new CultureScope(new CultureInfo(culture)))
        {
            string text = Gen(s);
            Assert.Equal(expected, text);
            Assert.Contains("Filter 2: ON PK Fc 1000 Hz Gain 1.25 dB Q 1", text);
            Assert.Contains("Filter 3: ON LS Fc 100 Hz Gain 3.96 dB Q 0.7071", text);     // 33 % of 12 dB
            Assert.Contains("Copy: L=1.165*L+-0.165*R R=1.165*R+-0.165*L", text);       // k = 0.165
            Assert.DoesNotContain(",", text.Replace(Head, "", StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------- same numbers on both paths

    [Fact]
    public void EffectFilters_AreTheSameObjectsTheInAppChainBuilds()
    {
        var fx = FxState.From(Settings(new EffectLevels { Bass = 70, Fidelity = 40, DynamicBoost = 20 }));

        Assert.Equal(
            new[]
            {
                new FxFilter(FxFilterType.LowShelf, 100, 8.4, 0.7071),
                new FxFilter(FxFilterType.HighShelf, 6000, 3.2, 0.7071),
                new FxFilter(FxFilterType.Peaking, 12000, 1.6, 1),
                new FxFilter(FxFilterType.LowShelf, 100, 1.2, 0.7071),
                new FxFilter(FxFilterType.HighShelf, 8000, 0.8, 0.7071),
            },
            fx.EffectFilters);
    }
}
