using System.Windows.Threading;
using AudioController.App.ViewModels;
using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The main window's Effects panel view model: every control goes through <see cref="MainViewModel.ApplyEq"/> to the
/// soundboard engine, the System EQ router and the Equalizer APO writer (the same <see cref="FxState"/> everywhere);
/// presets, "(modified)", Save as / Delete, Reset to Flat, the 31-band mode, the Hz boxes and the power button.
/// </summary>
public sealed class EffectsViewModelTests : IDisposable
{
    private const string HeadsetId = "{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}";

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private (MainViewModel Vm, Library Library, RecordingWriter Writer, RecordingEngine Engine) Build(Library? library = null)
    {
        // Most tests here drive the 10 adjustable bands; the 31-band default of a fresh library has its own test.
        library ??= new Library { Settings = new AppSettings { EqMode = EqMode.TenBandAdjustable } };
        var writer = new RecordingWriter(_dir.Path);
        var engine = new RecordingEngine();
        var vm = new MainViewModel(new MemoryVault(library), library, decoder: null, new ListDeviceManager(Array.Empty<AudioDeviceInfo>()),
            engine, new SystemEqRouter(), Dispatcher.CurrentDispatcher,
            detectEqualizerApo: () => EqualizerApoState.NotInstalled, apoWriterFactory: _ => writer);
        vm.SeedEqualizerApoForTests(
            new EqualizerApoState { Installed = true, ConfigPath = _dir.Path, AttachedEndpointIds = new[] { HeadsetId } },
            new RoutingPlan { HeadsetId = HeadsetId, ApoActive = true });
        return (vm, library, writer, engine);
    }

    private static EffectSliderViewModel Slider(EffectsViewModel fx, string key) => fx.EffectSliders.Single(s => s.Key == key);

    [Fact]
    public void Panel_HasPower_Presets_FiveEffects_TenBandsWithHz_And31ThirdOctaveBands()
    {
        var (vm, _, _, _) = Build();
        using var _ = vm;
        var fx = vm.Effects;

        Assert.True(fx.FxEnabled);
        Assert.Equal(new[] { "Fidelity", "Ambience", "Surround Sound", "Dynamic Boost", "Bass Boost" }, fx.EffectSliders.Select(s => s.Label));
        Assert.Contains("not a compressor", Slider(fx, "DynamicBoost").Tooltip);
        Assert.Equal(10, fx.TenBands.Count);
        Assert.Equal(new[] { "32", "64", "125", "250", "500", "1000", "2000", "4000", "8000", "16000" }, fx.TenBands.Select(b => b.FrequencyText));
        Assert.All(fx.TenBands, b => Assert.True(b.IsAdjustable));
        Assert.Equal(31, fx.ThirdOctaveBands.Count);
        Assert.All(fx.ThirdOctaveBands, b => Assert.False(b.IsAdjustable));
        Assert.Equal("31.5", fx.ThirdOctaveBands[2].Label);
        Assert.Equal("1.25k", fx.ThirdOctaveBands[18].Label);
        Assert.Equal(FxPresets.BuiltIn.Count, fx.Presets.Count); // no user presets → no separator
        Assert.Equal("Custom", fx.PresetDisplayName);
        Assert.Equal(EffectsViewModel.Caption, fx.CaptionText);
        Assert.Equal(
            "Applies to all PC audio on your headset through Equalizer APO, and to soundboard sounds. Your microphone is never equalized.",
            EffectsViewModel.Caption);
    }

    [Fact]
    public void EffectSlider_ReachesEngineRouterAndWriter_ThroughApplyEq()
    {
        var (vm, lib, writer, engine) = Build();
        using var _ = vm;

        Slider(vm.Effects, "Bass").Value = 60;

        Assert.Equal(60, lib.Settings.Effects.Bass);
        var expected = new FxFilter(FxFilterType.LowShelf, 100, 7.2, FxEffects.ShelfQ);
        Assert.Equal(expected, Assert.Single(writer.LastFx!.EffectFilters));
        Assert.Equal(expected, Assert.Single(engine.LastFx!.EffectFilters));
        Assert.True(writer.LastApoActive);
        Assert.Equal(HeadsetId, writer.LastHeadsetId);
        Assert.Equal(2, writer.LastChannels);
        Assert.Equal(48000, writer.LastSampleRate);
        Assert.Equal(1, writer.FxCalls);
        Assert.Equal(0, writer.LegacyCalls); // the Effects entry point, not the 10-gain one
    }

    [Fact]
    public void TenBandGain_AndHzBox_Apply_WithClampingAndErrors()
    {
        var (vm, lib, writer, _) = Build();
        using var _ = vm;
        var fx = vm.Effects;
        fx.EqEnabled = true;

        fx.TenBands[5].Gain = 6.3; // snaps to 0.5 dB
        Assert.Equal(6.5, fx.TenBands[5].Gain);
        Assert.Equal(new FxFilter(FxFilterType.Peaking, 1000, 6.5, 1), Assert.Single(writer.LastFx!.EqFilters));
        Assert.Equal(6.5f, lib.Settings.EqGainsDb[5]); // legacy mirror

        fx.TenBands[5].FrequencyText = "1.2k";
        Assert.Equal("1200", fx.TenBands[5].FrequencyText);
        Assert.Null(fx.TenBands[5].FrequencyError);
        Assert.Equal(1200, Assert.Single(writer.LastFx!.EqFilters).FrequencyHz);

        fx.TenBands[0].FrequencyText = "5 Hz";
        Assert.Equal("20", fx.TenBands[0].FrequencyText);
        Assert.NotNull(fx.TenBands[0].FrequencyError);

        fx.TenBands[5].FrequencyText = "3000"; // past its neighbour (2000)
        Assert.Equal("1999", fx.TenBands[5].FrequencyText);
        Assert.True(fx.TenBands[5].HasFrequencyError);

        fx.TenBands[3].FrequencyText = "loud";
        Assert.Equal("250", fx.TenBands[3].FrequencyText);
        Assert.Contains("not a frequency", fx.TenBands[3].FrequencyError);
        Assert.Equal(250, lib.Settings.EqBands![3].FrequencyHz);
    }

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData(" 1000 Hz ", 1000)]
    [InlineData("1k", 1000)]
    [InlineData("1,5k", 1500)]
    [InlineData("2.5 kHz", 2500)]
    [InlineData("31.5", 31.5)]
    public void HzBox_Parses(string text, double hz)
    {
        Assert.True(FxBandViewModel.TryParseHz(text, out double parsed));
        Assert.Equal(hz, parsed, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-50")]
    [InlineData("0")]
    [InlineData("NaN")]
    public void HzBox_RejectsNonsense(string text) => Assert.False(FxBandViewModel.TryParseHz(text, out _));

    [Fact]
    public void ThirdOctaveMode_Applies31FixedBands()
    {
        var (vm, lib, writer, _) = Build();
        using var _ = vm;
        var fx = vm.Effects;
        fx.EqEnabled = true;
        fx.TenBands[0].Gain = 3;

        fx.IsThirdOctave = true;
        fx.ThirdOctaveBands[17].Gain = -6;

        Assert.Equal(EqMode.ThirtyOneBandThirdOctave, lib.Settings.EqMode);
        Assert.False(fx.IsTenBand);
        Assert.Equal(new FxFilter(FxFilterType.Peaking, 1000, -6, 4.32), Assert.Single(writer.LastFx!.EqFilters));
        Assert.Equal(3f, lib.Settings.EqBands![0].GainDb); // the 10 bands are kept for later

        fx.IsTenBand = true;
        Assert.Equal(32, Assert.Single(writer.LastFx!.EqFilters).FrequencyHz);
    }

    [Fact]
    public void ResetToFlat_ZeroesEveryControl_AndTheAppliedState()
    {
        var (vm, lib, writer, _) = Build();
        using var _ = vm;
        var fx = vm.Effects;
        fx.EqEnabled = true;
        foreach (var s in fx.EffectSliders) s.Value = 40;
        foreach (var b in fx.TenBands) b.Gain = 5;
        foreach (var b in fx.ThirdOctaveBands) b.Gain = -3;

        fx.ResetFlatCommand.Execute(null);

        Assert.All(fx.EffectSliders, s => Assert.Equal(0, s.Value));
        Assert.All(fx.TenBands, b => Assert.Equal(0, b.Gain));
        Assert.All(fx.ThirdOctaveBands, b => Assert.Equal(0, b.Gain));
        Assert.True(lib.Settings.Effects.IsZero);
        Assert.All(lib.Settings.EqGainsDb, g => Assert.Equal(0f, g));
        Assert.True(writer.LastFx!.IsNeutral);
        Assert.Equal("Flat", fx.PresetDisplayName);
        Assert.Equal("Flat", fx.SelectedPreset?.Name);
        Assert.True(fx.EqEnabled); // switches are kept
        Assert.True(fx.FxEnabled);
    }

    [Fact]
    public void PresetSelection_LoadsIt_AndEditsMarkItModified()
    {
        var (vm, lib, writer, _) = Build();
        using var _ = vm;
        var fx = vm.Effects;

        fx.SelectedPreset = fx.Presets.Single(p => p.Name == "Rock");

        Assert.Equal(30, lib.Settings.Effects.Bass);
        Assert.Equal(30, Slider(fx, "Bass").Value);
        Assert.Equal(4, fx.TenBands[0].Gain);
        Assert.True(fx.EqEnabled);
        Assert.Equal("Rock", fx.PresetDisplayName);
        Assert.False(fx.IsPresetModified);
        Assert.False(writer.LastFx!.IsNeutral);

        Slider(fx, "Ambience").Value = 55;

        Assert.True(fx.IsPresetModified);
        Assert.Equal("Rock (modified)", fx.PresetDisplayName);
        Assert.True(lib.Settings.FxPresetModified);

        fx.SelectedPreset = fx.Presets.Single(p => p.Name == "Flat");
        Assert.False(fx.IsPresetModified);
        Assert.True(writer.LastFx!.IsNeutral);
    }

    [Fact]
    public void SaveAs_ThenDelete_AUserPreset()
    {
        var (vm, lib, _, _) = Build();
        using var _ = vm;
        var fx = vm.Effects;
        Slider(fx, "Surround").Value = 35;

        fx.SaveAsCommand.Execute(null);
        Assert.True(fx.IsSavingPreset);
        fx.NewPresetName = "Rock";
        fx.ConfirmSaveCommand.Execute(null);
        Assert.NotNull(fx.PresetError); // built-in name refused
        Assert.True(fx.IsSavingPreset);

        fx.NewPresetName = "Late night";
        fx.ConfirmSaveCommand.Execute(null);

        Assert.False(fx.IsSavingPreset);
        Assert.Null(fx.PresetError);
        Assert.Contains(fx.Presets, p => p.IsSeparator);
        Assert.Equal(new PresetItem("Late night", true), fx.Presets[^1]);
        Assert.Equal("Late night", fx.SelectedPreset?.Name);
        Assert.Equal(35, Assert.Single(lib.UserPresets).Effects.Surround);
        Assert.True(fx.DeletePresetCommand.CanExecute(null));

        fx.DeletePresetCommand.Execute(null);

        Assert.Empty(lib.UserPresets);
        Assert.DoesNotContain(fx.Presets, p => p.IsSeparator || p.IsUser);
        Assert.Equal("Custom", fx.PresetDisplayName);
        Assert.Equal(35, lib.Settings.Effects.Surround); // the sound itself stays

        fx.SelectedPreset = fx.Presets.Single(p => p.Name == "Music");
        Assert.False(fx.DeletePresetCommand.CanExecute(null)); // built-ins can't be deleted
    }

    [Fact]
    public void PowerOff_AppliesNothing_AnywhereElse()
    {
        var (vm, _, writer, engine) = Build();
        using var _ = vm;
        var fx = vm.Effects;
        fx.SelectedPreset = fx.Presets.Single(p => p.Name == "Hip-Hop");
        Assert.False(engine.LastFx!.IsNeutral);

        fx.FxEnabled = false;

        Assert.True(writer.LastFx!.IsNeutral);
        Assert.True(engine.LastFx!.IsNeutral);
        Assert.Contains("off", vm.EffectsStatusText, StringComparison.OrdinalIgnoreCase);

        fx.FxEnabled = true;
        Assert.False(writer.LastFx!.IsNeutral);
    }

    [Fact]
    public void StatusLine_ReusesTheEqEverythingText()
    {
        var (vm, _, _, _) = Build();
        using var _ = vm;

        Assert.StartsWith("EQ everything I hear via Equalizer APO", vm.EffectsStatusText);
        Assert.Contains(RoutingPlanner.ApoViaText, vm.EffectsStatusText);
    }

    [Fact]
    public void RealWriter_WritesAmbienceWavBeforeTheFile_AndOnlyRegeneratesOnChange()
    {
        var library = new Library();
        var writer = new EqualizerApoWriter(_dir.Path);
        using var vm = new MainViewModel(new MemoryVault(library), library, decoder: null, new ListDeviceManager(Array.Empty<AudioDeviceInfo>()),
            new RecordingEngine(), new SystemEqRouter(), Dispatcher.CurrentDispatcher,
            detectEqualizerApo: () => EqualizerApoState.NotInstalled, apoWriterFactory: _ => writer);
        vm.SeedEqualizerApoForTests(
            new EqualizerApoState { Installed = true, ConfigPath = _dir.Path, AttachedEndpointIds = new[] { HeadsetId } },
            new RoutingPlan { HeadsetId = HeadsetId, ApoActive = true });

        Slider(vm.Effects, "Ambience").Value = 50;
        writer.Flush();

        string wav = Path.Combine(_dir.Path, AmbienceImpulse.FileName);
        Assert.Equal(AmbienceImpulse.CreateWav(50), File.ReadAllBytes(wav));
        Assert.Contains("Convolution: ambience.wav\r\n", File.ReadAllText(writer.FilePath));
        Assert.Contains("Include: AudioController.txt", File.ReadAllText(Path.Combine(_dir.Path, "config.txt")));

        var stamp = File.GetLastWriteTimeUtc(wav);
        Slider(vm.Effects, "Bass").Value = 20; // Ambience unchanged → the impulse is not rewritten
        writer.Flush();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(wav));
        Assert.Contains("ON LS Fc 100 Hz Gain 2.4 dB", File.ReadAllText(writer.FilePath));

        Slider(vm.Effects, "Ambience").Value = 0;
        writer.Flush();
        Assert.DoesNotContain("Convolution", File.ReadAllText(writer.FilePath));
    }

    // ---------------------------------------------------------------- fakes

    /// <summary>Records the Effects writes (and any 1.4-style ones).</summary>
    private sealed class RecordingWriter(string dir) : IEqualizerApoWriter
    {
        public event EventHandler<string>? WriteFailed { add { } remove { } }
        public event EventHandler? Written { add { } remove { } }

        public string ConfigDirectory { get; } = dir;
        public string FilePath => Path.Combine(ConfigDirectory, EqualizerApoConfig.FileName);
        public bool FileExists => false;

        public int FxCalls { get; private set; }
        public int LegacyCalls { get; private set; }
        public FxState? LastFx { get; private set; }
        public string? LastHeadsetId { get; private set; }
        public bool LastApoActive { get; private set; }
        public int LastChannels { get; private set; }
        public int LastSampleRate { get; private set; }

        public void WriteApoState(float[] gainsDb, bool eqEnabled, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp) =>
            LegacyCalls++;

        public void WriteFxState(FxState fx, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp, int channels = 2, int sampleRate = 48000)
        {
            FxCalls++;
            LastFx = fx;
            LastHeadsetId = headsetDeviceId;
            LastApoActive = apoAttachedToHeadset;
            LastChannels = channels;
            LastSampleRate = sampleRate;
        }

        public string? WriteNow(string content, bool ensureInclude) => null;
        public void Schedule(string content, bool ensureInclude) { }
        public void Flush() { }
        public void Dispose() { }
    }

    /// <summary>No devices; records the Effects state the soundboard bus would get.</summary>
    private sealed class RecordingEngine : IAudioEngine
    {
        public FxState? LastFx { get; private set; }
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
        public void SetFx(FxState fx) => LastFx = fx;
        public void SetMonitorEqBypass(bool bypass) { }
        public void Dispose() { }
    }

    private sealed class MemoryVault(Library library) : IVault
    {
        public Library LoadLibrary() => library;
        public void SaveLibrary(Library l) { }
        public Guid AddSound(byte[] wavBytes) => Guid.NewGuid();
        public byte[] GetSound(Guid soundId) => Array.Empty<byte>();
        public void DeleteSound(Guid soundId) { }
        public bool SoundExists(Guid soundId) => false;
    }
}
