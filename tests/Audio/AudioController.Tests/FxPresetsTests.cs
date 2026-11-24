using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Storage;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// Effects presets and the settings model: the built-in presets, user presets (save / replace / delete / vault
/// round-trip), the "(modified)" flag, "Reset to Flat", and the migration from the 1.4 <c>EqGainsDb</c> array that never
/// discards user data.
/// </summary>
public sealed class FxPresetsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static AppSettings Fresh()
    {
        var s = new AppSettings();
        FxSettings.Normalize(s);
        return s;
    }

    // ---------------------------------------------------------------- built-ins

    [Fact]
    public void BuiltIns_AreTheElevenNamedPresets_InMenuOrder()
    {
        Assert.Equal(
            new[] { "Flat", "Music", "Rock", "Pop", "Hip-Hop", "Electronic", "Gaming", "Movies", "Voice & Podcast", "Treble Clarity", "Crusher Sensory" },
            FxPresets.BuiltIn.Select(p => p.Name));
    }

    [Fact]
    public void EveryBuiltIn_Loads_AsTenAdjustableBands_InRange()
    {
        foreach (var preset in FxPresets.BuiltIn)
        {
            var s = Fresh();
            s.EqEnabled = false;
            s.EqMode = EqMode.ThirtyOneBandThirdOctave;

            FxPresets.Apply(s, preset);

            Assert.Equal(EqMode.TenBandAdjustable, s.EqMode);
            Assert.Equal(preset.Name, s.FxPresetName);
            Assert.False(s.FxPresetModified);
            Assert.True(s.EqEnabled);
            Assert.Equal(FxSettings.DefaultTenBandFrequenciesHz, s.EqBands!.Select(b => b.FrequencyHz));
            Assert.Equal(preset.Bands.Select(b => b.GainDb), s.EqBands!.Select(b => b.GainDb));
            Assert.Equal(s.EqBands!.Select(b => b.GainDb), s.EqGainsDb); // legacy mirror
            Assert.True(preset.Effects.SameAs(s.Effects));
            Assert.All(s.EqBands!, b => Assert.InRange(b.GainDb, -24f, 24f));
            Assert.False(FxPresets.NormalizePreset(preset.Clone())); // already normalized data
            Assert.NotSame(preset.Effects, s.Effects);                // a copy: editing never changes the built-in
        }
    }

    [Fact]
    public void Flat_IsNeutral_AndOthersAreNot()
    {
        var s = Fresh();
        FxPresets.Apply(s, FxPresets.Find("flat", null)!);
        Assert.True(FxState.From(s).IsNeutral);

        FxPresets.Apply(s, FxPresets.Find("Rock", null)!);
        Assert.False(FxState.From(s).IsNeutral);
        Assert.Equal(30, s.Effects.Bass);
    }

    [Fact]
    public void Apply_LeavesThePowerSwitchAlone()
    {
        var s = Fresh();
        s.FxEnabled = false;

        FxPresets.Apply(s, FxPresets.Find("Gaming", null)!);

        Assert.False(s.FxEnabled);
        Assert.True(FxState.From(s).IsNeutral);
    }

    // ---------------------------------------------------------------- user presets

    [Fact]
    public void SaveUserPreset_CapturesTheCurrentState_AndMakesItCurrent()
    {
        var lib = new Library();
        lib.Settings.EqMode = EqMode.TenBandAdjustable;
        FxSettings.Normalize(lib.Settings);
        FxSettings.SetTenBandGain(lib.Settings, 3, 5.5f);
        FxSettings.SetTenBandFrequency(lib.Settings, 3, 300);
        lib.Settings.Effects.Surround = 42;

        Assert.Null(FxPresets.SaveUserPreset(lib, "  My Mix  "));

        var p = Assert.Single(lib.UserPresets);
        Assert.Equal("My Mix", p.Name);
        Assert.Equal(300, p.Bands[3].FrequencyHz);
        Assert.Equal(5.5f, p.Bands[3].GainDb);
        Assert.Equal(42, p.Effects.Surround);
        Assert.Equal("My Mix", lib.Settings.FxPresetName);
        Assert.False(lib.Settings.FxPresetModified);

        // Editing the settings afterwards never changes the stored preset.
        lib.Settings.EqBands![3].GainDb = -9f;
        Assert.Equal(5.5f, lib.UserPresets[0].Bands[3].GainDb);
    }

    [Fact]
    public void SaveUserPreset_SameName_Replaces_AnyLetterCase()
    {
        var lib = new Library();
        FxSettings.Normalize(lib.Settings);
        FxPresets.SaveUserPreset(lib, "Night");
        lib.Settings.Effects.Bass = 77;

        Assert.Null(FxPresets.SaveUserPreset(lib, "NIGHT"));

        var p = Assert.Single(lib.UserPresets);
        Assert.Equal(77, p.Effects.Bass);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rock")]
    [InlineData("Voice & Podcast")]
    [InlineData("line\nbreak")]
    [InlineData("01234567890123456789012345678901234567890")] // 41 characters
    public void SaveUserPreset_RejectsBadNames_AndChangesNothing(string name)
    {
        var lib = new Library();
        FxSettings.Normalize(lib.Settings);

        Assert.NotNull(FxPresets.SaveUserPreset(lib, name));
        Assert.Empty(lib.UserPresets);
        Assert.Null(lib.Settings.FxPresetName);
    }

    [Fact]
    public void ThirdOctavePreset_RoundTrips_ItsMode_AndGains()
    {
        var lib = new Library();
        FxSettings.Normalize(lib.Settings);
        lib.Settings.EqMode = EqMode.ThirtyOneBandThirdOctave;
        lib.Settings.EqGains31Db![30] = -7f;
        FxPresets.SaveUserPreset(lib, "Thirds");

        var s = Fresh();
        FxPresets.Apply(s, lib.UserPresets[0]);

        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, s.EqMode);
        Assert.Equal(-7f, s.EqGains31Db![30]);
        Assert.Equal(31, lib.UserPresets[0].Bands.Count);
        Assert.Equal(20000, lib.UserPresets[0].Bands[30].FrequencyHz);
    }

    [Fact]
    public void DeleteUserPreset_RemovesIt_TheSoundStays_AndBuiltInsCannotBeDeleted()
    {
        var lib = new Library();
        FxSettings.Normalize(lib.Settings);
        lib.Settings.Effects.Fidelity = 60;
        FxPresets.SaveUserPreset(lib, "Mine");

        Assert.False(FxPresets.DeleteUserPreset(lib, "Rock"));
        Assert.True(FxPresets.DeleteUserPreset(lib, "mine"));

        Assert.Empty(lib.UserPresets);
        Assert.Null(lib.Settings.FxPresetName); // now "Custom"
        Assert.Equal(60, lib.Settings.Effects.Fidelity);
        Assert.False(FxPresets.DeleteUserPreset(lib, "Mine"));
    }

    [Fact]
    public void UserPresets_SurviveAVaultRoundTrip()
    {
        var key = TestBytes.RandomKey();
        var lib = new Library();
        lib.Settings.EqMode = EqMode.TenBandAdjustable;
        FxSettings.Normalize(lib.Settings);
        FxSettings.SetTenBandFrequency(lib.Settings, 9, 14500);
        FxSettings.SetTenBandGain(lib.Settings, 9, -4.5f);
        lib.Settings.Effects = new EffectLevels { Bass = 1, Fidelity = 2, Ambience = 3, Surround = 4, DynamicBoost = 5 };
        FxPresets.SaveUserPreset(lib, "Desk speakers");
        lib.Settings.EqMode = EqMode.ThirtyOneBandThirdOctave;
        lib.Settings.EqGains31Db![0] = 3f;
        FxPresets.SaveUserPreset(lib, "Thirds");

        new Vault(_temp.Path, key).SaveLibrary(lib);
        var loaded = new Vault(_temp.Path, key).LoadLibrary();

        Assert.Equal(2, loaded.UserPresets.Count);
        var desk = loaded.UserPresets.Single(p => p.Name == "Desk speakers");
        Assert.Equal(EqMode.TenBandAdjustable, desk.EqMode);
        Assert.Equal(14500, desk.Bands[9].FrequencyHz);
        Assert.Equal(-4.5f, desk.Bands[9].GainDb);
        Assert.True(new EffectLevels { Bass = 1, Fidelity = 2, Ambience = 3, Surround = 4, DynamicBoost = 5 }.SameAs(desk.Effects));
        var thirds = loaded.UserPresets.Single(p => p.Name == "Thirds");
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, thirds.EqMode);
        Assert.Equal(3f, thirds.Bands[0].GainDb);
        Assert.Equal("Thirds", loaded.Settings.FxPresetName);
    }

    [Fact]
    public void NormalizeUserPresets_RepairsWithoutDroppingAnything()
    {
        var list = new List<FxPreset>
        {
            null!,
            new() { Name = "Rock", Bands = new List<EqBand>(), Effects = new EffectLevels { Bass = 400 } },
            new() { Name = "  ", Bands = null!, Effects = null! },
            new() { Name = "Mine" },
            new() { Name = "mine" },
        };

        Assert.True(FxPresets.NormalizeUserPresets(list));

        Assert.Equal(new[] { "Rock (2)", "My preset", "Mine", "mine (2)" }, list.Select(p => p.Name));
        Assert.All(list, p => Assert.Equal(10, p.Bands.Count));
        Assert.Equal(100, list[0].Effects.Bass);
        Assert.NotNull(list[1].Effects);
        Assert.False(FxPresets.NormalizeUserPresets(list)); // idempotent
    }

    // ---------------------------------------------------------------- modified flag + reset

    [Fact]
    public void ModifiedFlag_IsSetOnlyWhileAPresetIsCurrent_AndClearedByLoadOrSave()
    {
        var lib = new Library();
        FxSettings.Normalize(lib.Settings);
        var s = lib.Settings;

        FxSettings.MarkModified(s);
        Assert.False(s.FxPresetModified); // "Custom": nothing to be modified from

        FxPresets.Apply(s, FxPresets.Find("Pop", null)!);
        FxSettings.MarkModified(s);
        Assert.True(s.FxPresetModified);

        FxPresets.Apply(s, FxPresets.Find("Pop", null)!);
        Assert.False(s.FxPresetModified);

        FxSettings.MarkModified(s);
        FxPresets.SaveUserPreset(lib, "Pop tweak");
        Assert.False(s.FxPresetModified);
        Assert.Equal("Pop tweak", s.FxPresetName);
    }

    [Fact]
    public void ResetToFlat_ZeroesEveryBandAndEffect_KeepsModeFrequenciesAndSwitches()
    {
        var s = Fresh();
        s.FxEnabled = true;
        s.EqEnabled = true;
        s.EqMode = EqMode.ThirtyOneBandThirdOctave;
        FxPresets.Apply(s, FxPresets.Find("Electronic", null)!);
        s.EqMode = EqMode.ThirtyOneBandThirdOctave;
        FxSettings.SetTenBandFrequency(s, 4, 700);
        for (int i = 0; i < 31; i++) s.EqGains31Db![i] = 2f;

        FxSettings.ResetToFlat(s);

        Assert.All(s.EqBands!, b => Assert.Equal(0f, b.GainDb));
        Assert.All(s.EqGains31Db!, g => Assert.Equal(0f, g));
        Assert.All(s.EqGainsDb, g => Assert.Equal(0f, g));
        Assert.True(s.Effects.IsZero);
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, s.EqMode);
        Assert.Equal(700, s.EqBands![4].FrequencyHz);
        Assert.True(s.FxEnabled);
        Assert.True(s.EqEnabled);
        Assert.Equal("Flat", s.FxPresetName);
        Assert.False(s.FxPresetModified);
        Assert.True(FxState.From(s).IsNeutral);
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public void Migration_FillsTheBandsFromEqGainsDb_OnTheDefaultFrequencies()
    {
        var legacy = new[] { -12f, -6.5f, -1.25f, 0f, 0.1f, 3f, 6f, 9.75f, 11.5f, 12f };
        var s = new AppSettings { EqEnabled = true, EqGainsDb = (float[])legacy.Clone() }; // as 1.4 wrote it: no EqBands

        Assert.True(FxSettings.Normalize(s));

        Assert.Equal(FxSettings.DefaultTenBandFrequenciesHz, s.EqBands!.Select(b => b.FrequencyHz));
        Assert.Equal(legacy, s.EqBands!.Select(b => b.GainDb));
        Assert.Equal(legacy, s.EqGainsDb); // unchanged for older versions
        Assert.Equal(EqMode.TenBandAdjustable, s.EqMode);
        Assert.True(s.FxEnabled);           // an EQ that was on stays on
        Assert.True(s.Effects.IsZero);
        Assert.Equal(31, s.EqGains31Db!.Length);
        Assert.False(FxSettings.Normalize(s)); // idempotent

        var fx = FxState.From(s);
        Assert.Equal(9, fx.EqFilters.Count); // the 0 dB band is skipped
        Assert.Equal(new FxFilter(FxFilterType.Peaking, 32, -12, 1), fx.EqFilters[0]);
    }

    [Fact]
    public void Migration_NeverResetsBandsWhenOnlyTheLegacyArrayIsMissing()
    {
        var bands = FxSettings.DefaultTenBands(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        bands[2].FrequencyHz = 150;
        var s = new AppSettings { EqBands = bands, EqGainsDb = null! };

        FxSettings.Normalize(s);

        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f }, s.EqBands!.Select(b => b.GainDb));
        Assert.Equal(150, s.EqBands![2].FrequencyHz);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f }, s.EqGainsDb);
    }

    [Fact]
    public void LegacyArrayEdits_AreAdopted_IntoTheBands()
    {
        var s = Fresh();
        FxSettings.SetTenBandFrequency(s, 5, 1100);
        s.EqGainsDb[5] = 7f; // an older code path writing the array directly

        Assert.True(FxSettings.SyncLegacyGains(s));

        Assert.Equal(7f, s.EqBands![5].GainDb);
        Assert.Equal(1100, s.EqBands![5].FrequencyHz);
        Assert.False(FxSettings.SyncLegacyGains(s));
    }

    [Fact]
    public void Normalize_RepairsOutOfRangeData()
    {
        var s = new AppSettings
        {
            EqGainsDb = new float[10],
            EqBands = new List<EqBand>
            {
                new(1000, 1), new(500, 2), new(double.NaN, 3), new(99999, 4), new(-5, float.NaN),
                new(250, 99), new(250, -99), new(4000, 8), new(8000, 9), new(16000, 10), new(17000, 11),
            },
            EqGains31Db = new float[] { 50, float.NaN },
            Effects = new EffectLevels { Bass = -5, Fidelity = 101, Ambience = 50 },
            EqMode = (EqMode)42,
            FxPresetName = "   ",
            FxPresetModified = true,
        };
        s.EqGainsDb = s.EqBands.Take(10).Select(b => b.GainDb).ToArray();

        Assert.True(FxSettings.Normalize(s));

        Assert.Equal(10, s.EqBands!.Count);
        var f = s.EqBands.Select(b => b.FrequencyHz).ToList();
        Assert.All(f, hz => Assert.InRange(hz, 20, 20000));
        for (int i = 1; i < f.Count; i++) Assert.True(f[i] > f[i - 1]);
        Assert.All(s.EqBands, b => Assert.InRange(b.GainDb, -24f, 24f));
        Assert.Equal(31, s.EqGains31Db!.Length);
        Assert.Equal(24f, s.EqGains31Db[0]);
        Assert.Equal(0f, s.EqGains31Db[1]);
        Assert.Equal(0, s.Effects.Bass);
        Assert.Equal(100, s.Effects.Fidelity);
        Assert.Equal(50, s.Effects.Ambience);
        Assert.Equal(AppSettings.DefaultEqMode, s.EqMode); // an unknown stored value becomes the default layout
        Assert.Null(s.FxPresetName);
        Assert.False(s.FxPresetModified);
        Assert.Equal(s.EqBands.Select(b => b.GainDb), s.EqGainsDb);
    }
}
