using System.Text;
using System.Windows.Threading;
using AudioController.App.ViewModels;
using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using AudioController.Storage;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// "When I raise the bass everything else gets muffled": the Equalizer APO auto pre-amp lowered the whole level by the
/// biggest boost. Since 1.6 it is OFF by default (a slider only changes its own sound), and the EQ opens with 31 bands —
/// for new installs and for libraries that never stored those fields. A value the user stored is never flipped.
/// </summary>
public sealed class EqDefaultsTests : IDisposable
{
    private const string HeadsetGuid = "{07abda82-1125-4339-aca5-dca468fc985a}";
    private const string HeadsetId = "{0.0.0.00000000}." + HeadsetGuid;
    private const string DigitalGuid = "{c8c00f88-f4fc-4ece-84a9-a04737e700df}";

    private readonly TempDirectory _temp = new();
    private readonly byte[] _key = TestBytes.RandomKey();

    public void Dispose() => _temp.Dispose();

    private string VaultRoot => _temp.Combine("vault");

    /// <summary>Writes <paramref name="json"/> as library.dat exactly as the vault would (encrypted), then loads it.</summary>
    private Library LoadFromJson(string json)
    {
        Directory.CreateDirectory(VaultRoot);
        AtomicFile.Write(Path.Combine(VaultRoot, "library.dat"), AesGcmCrypto.Encrypt(_key, Encoding.UTF8.GetBytes(json)));
        return new Vault(VaultRoot, _key).LoadLibrary();
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void FreshSettings_AutoPreampOff_And31Bands()
    {
        var s = new AppSettings();

        Assert.False(s.EqAutoPreamp);
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, s.EqMode);
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, AppSettings.DefaultEqMode);
        FxSettings.Normalize(s);
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, s.EqMode); // normalizing a fresh library keeps the default
        Assert.False(s.EqAutoPreamp);
    }

    [Fact]
    public void NewInstall_EmptyVault_LoadsWithAutoPreampOff_And31Bands()
    {
        var s = new Vault(VaultRoot, _key).LoadLibrary().Settings;

        Assert.False(s.EqAutoPreamp);
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, s.EqMode);
        Assert.False(s.MirrorEnabled);
        Assert.Empty(s.MirrorTargets);
    }

    [Fact]
    public void LibraryJson_WithoutThoseFields_LoadsWithTheNewDefaults()
    {
        var s = LoadFromJson("""{"SchemaVersion":1,"Boards":[],"Settings":{"EqEnabled":true,"MonitorEnabled":true}}""").Settings;

        Assert.False(s.EqAutoPreamp);
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, s.EqMode);
        Assert.False(s.MirrorEnabled);
        Assert.NotNull(s.MirrorTargets);
        Assert.Empty(s.MirrorTargets);
        Assert.True(s.EqEnabled);
    }

    [Theory]
    [InlineData(true, "TenBandAdjustable")]
    [InlineData(false, "TenBandAdjustable")]
    [InlineData(true, "ThirtyOneBandThirdOctave")]
    public void LibraryJson_WithStoredValues_KeepsThem(bool autoPreamp, string mode)
    {
        string json = "{\"Settings\":{\"EqAutoPreamp\":" + (autoPreamp ? "true" : "false")
                      + ",\"EqMode\":\"" + mode + "\",\"EqBands\":[{\"FrequencyHz\":32,\"GainDb\":3}]}}";

        var s = LoadFromJson(json).Settings;

        Assert.Equal(autoPreamp, s.EqAutoPreamp);
        Assert.Equal(Enum.Parse<EqMode>(mode), s.EqMode);
    }

    [Fact]
    public void StoredValues_SurviveSaveAndLoad_Unchanged()
    {
        var library = new Library { Settings = new AppSettings { EqAutoPreamp = true, EqMode = EqMode.TenBandAdjustable } };

        new Vault(VaultRoot, _key).SaveLibrary(library);
        var s = new Vault(VaultRoot, _key).LoadLibrary().Settings;

        Assert.True(s.EqAutoPreamp);
        Assert.Equal(EqMode.TenBandAdjustable, s.EqMode);
    }

    [Fact]
    public void PreEffectsLibrary_WithAnAudible10SliderEq_KeepsThe10Bands_SoItsEqStillSoundsTheSame()
    {
        // A 1.4 library: only EqGainsDb (no EqBands, no EqMode). Its gains live in the 10 bands, so it stays on them.
        var s = LoadFromJson("""{"Settings":{"EqEnabled":true,"EqGainsDb":[0,0,0,0,0,6,0,0,0,0]}}""").Settings;

        Assert.Equal(EqMode.TenBandAdjustable, s.EqMode);
        Assert.Equal(6f, s.EqBands![5].GainDb);
        Assert.Equal(new FxFilter(FxFilterType.Peaking, 1000, 6, 1), Assert.Single(FxState.From(s).EqFilters));

        // Flat 1.4 EQ: nothing to keep → the new 31-band default.
        var flat = LoadFromJson("""{"Settings":{"EqEnabled":true,"EqGainsDb":[0,0,0,0,0,0,0,0,0,0]}}""").Settings;
        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, flat.EqMode);
    }

    // ---------------------------------------------------------------- the generator

    [Fact]
    public void Generator_AutoPreampOff_NeverWritesANonZeroPreamp()
    {
        var random = new Random(1234);
        for (int run = 0; run < 300; run++)
        {
            var s = new AppSettings
            {
                EqEnabled = random.Next(4) != 0,
                FxEnabled = random.Next(6) != 0,
                EqMode = random.Next(2) == 0 ? EqMode.TenBandAdjustable : EqMode.ThirtyOneBandThirdOctave,
            };
            FxSettings.Normalize(s);
            for (int i = 0; i < 10; i++) FxSettings.SetTenBandGain(s, i, (float)(random.NextDouble() * 48 - 24));
            for (int i = 0; i < 31; i++) FxSettings.SetThirdOctaveGain(s, i, (float)(random.NextDouble() * 48 - 24));
            s.Effects = new EffectLevels
            {
                Bass = random.Next(101), Fidelity = random.Next(101), Ambience = random.Next(101),
                Surround = random.Next(101), DynamicBoost = random.Next(101),
            };

            string text = EqualizerApoConfig.Generate(FxState.From(s), HeadsetGuid, autoPreamp: false);

            var preamps = text.Split("\r\n").Where(l => l.StartsWith("Preamp:", StringComparison.Ordinal)).ToList();
            Assert.Equal("Preamp: 0 dB", Assert.Single(preamps));
        }
    }

    [Fact]
    public void Generator_BassBoost100_TheReportedCase_LowersNothingWhenOff_AndStillProtectsWhenOn()
    {
        // The live log showed "Preamp: -12 dB" for Bass Boost 100 % (and -37.2 dB with stacked boosts).
        var s = new AppSettings { FxEnabled = true, EqEnabled = true };
        FxSettings.Normalize(s);
        s.Effects.Bass = 100;
        FxSettings.SetThirdOctaveGain(s, 4, 24f);
        FxSettings.SetThirdOctaveGain(s, 5, 1.2f);
        var fx = FxState.From(s);

        string off = EqualizerApoConfig.Generate(fx, HeadsetGuid, autoPreamp: false);
        string on = EqualizerApoConfig.Generate(fx, HeadsetGuid, autoPreamp: true);

        Assert.Contains("Preamp: 0 dB\r\n", off);
        Assert.Contains("Filter 1: ON PK Fc 50 Hz Gain 24 dB Q 4.32\r\n", off);
        Assert.Contains("ON LS Fc 100 Hz Gain 12 dB", off);
        Assert.Contains("Preamp: -36 dB\r\n", on); // the opt-in clip protection is unchanged
    }

    [Fact]
    public void Generator_StaysScopedToTheHeadset_SoAnExtraOutputWithEqualizerApoStaysNeutral()
    {
        var s = new AppSettings { FxEnabled = true, EqEnabled = true };
        FxSettings.Normalize(s);
        s.Effects.Bass = 100;

        string text = EqualizerApoConfig.Generate(FxState.From(s), EqualizerApoConfig.DeviceSelectorFor(HeadsetId)!, autoPreamp: false);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Device: " + HeadsetGuid, lines[1]);
        Assert.Equal("Device: all", lines[^1]);
        Assert.DoesNotContain(DigitalGuid, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, lines.Count(l => l.StartsWith("Device:", StringComparison.Ordinal))); // nothing applies outside the headset
    }

    // ---------------------------------------------------------------- the app passes the user's choice through

    [Theory]
    [InlineData(null, false)]  // a library that never stored it
    [InlineData(false, false)]
    [InlineData(true, true)]   // the user's explicit opt-in
    public void MainViewModel_PassesThePreampChoice_ToTheEqualizerApoWriter(bool? stored, bool expected)
    {
        var library = new Library();
        if (stored is { } value) library.Settings.EqAutoPreamp = value;
        library.Settings.EqEnabled = true;
        library.Settings.EqAllAudio = true;
        var fake = new MainViewModelEqualizerApoTests.FakeApoWriter(_temp.Path);
        using var vm = new MainViewModel(new NullVault(library), library, decoder: null, new ListDeviceManager(Array.Empty<AudioDeviceInfo>()),
            new NullEngine(), new SystemEqRouter(), Dispatcher.CurrentDispatcher,
            detectEqualizerApo: () => EqualizerApoState.NotInstalled, apoWriterFactory: _ => fake);
        vm.SeedEqualizerApoForTests(
            new EqualizerApoState { Installed = true, ConfigPath = _temp.Path, AttachedEndpointIds = new[] { HeadsetId } },
            new RoutingPlan { HeadsetId = HeadsetId, ApoActive = true });

        vm.ApplyEq();

        Assert.Equal(expected, fake.LastAutoPreamp);
        Assert.Equal(stored ?? false, library.Settings.EqAutoPreamp); // never flipped by the app
        Assert.True(vm.Effects.IsThirdOctave);                        // the 31-band layout is the default view
    }

    // ---------------------------------------------------------------- the in-app soundboard path: no hidden gain compensation

    [Fact]
    public void SoundboardChain_HasNoHiddenGainCompensation_ABassBoostLeaves1kHzAlone()
    {
        const int rate = 48_000;
        var s = new AppSettings { FxEnabled = true, EqEnabled = true, EqAutoPreamp = false };
        FxSettings.Normalize(s);
        s.Effects.Bass = 100;
        FxSettings.SetThirdOctaveGain(s, 5, 12f); // +12 dB at 63 Hz on top
        var chain = new FxChain(rate, 2);
        chain.SetState(FxState.From(s));

        var tone = new float[rate * 2];
        for (int i = 0; i < rate; i++)
        {
            tone[2 * i] = tone[2 * i + 1] = 0.1f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
        }

        var processed = (float[])tone.Clone();
        for (int o = 0; o < processed.Length; o += 960)
        {
            chain.Process(processed, o, Math.Min(960, processed.Length - o));
        }

        double Rms(float[] b) => Math.Sqrt(b.Skip(rate).Select(v => (double)v * v).Average());
        double changeDb = 20 * Math.Log10(Rms(processed) / Rms(tone));
        Assert.InRange(changeDb, -0.2, 0.2); // the level of everything else stays put (no pre-amp in the app's chain)

        // The in-app chain is the same whether or not the Equalizer APO pre-amp is on.
        s.EqAutoPreamp = true;
        var withPreamp = FxState.From(s);
        Assert.Equal(FxState.From(s).EqFilters, withPreamp.EqFilters);
        var again = new FxChain(rate, 2);
        again.SetState(withPreamp);
        var processed2 = (float[])tone.Clone();
        for (int o = 0; o < processed2.Length; o += 960)
        {
            again.Process(processed2, o, Math.Min(960, processed2.Length - o));
        }

        Assert.Equal(processed, processed2);
    }

    private sealed class NullVault(Library library) : IVault
    {
        public Library LoadLibrary() => library;

        public void SaveLibrary(Library l) { }

        public Guid AddSound(byte[] wavBytes) => Guid.NewGuid();

        public byte[] GetSound(Guid soundId) => Array.Empty<byte>();

        public void DeleteSound(Guid soundId) { }

        public bool SoundExists(Guid soundId) => false;
    }

    private sealed class NullEngine : IAudioEngine
    {
        public bool IsRunning => false;

        public bool AnySoundPlaying => false;

        public bool MicMuted { get; set; }

        public event EventHandler<EngineStatus>? StatusChanged { add { } remove { } }

        public event EventHandler<string>? EngineError { add { } remove { } }

        public void Start(string inputDeviceId, string outputDeviceId) { }

        public void Stop() { }

        public void PlaySound(SoundSlot slot, byte[] wavData) { }

        public void StopSound(Guid slotId) { }

        public bool IsSlotPlaying(Guid slotId) => false;

        public void StopAll() { }

        public void SetMonitor(string? deviceId, bool enabled) { }

        public void SetEq(float[] gainsDb, bool enabled) { }

        public void SetMonitorEqBypass(bool bypass) { }

        public void Dispose() { }
    }
}
