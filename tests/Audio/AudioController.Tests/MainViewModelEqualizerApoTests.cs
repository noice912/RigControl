using System.Windows.Threading;
using AudioController.App.ViewModels;
using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace AudioController.Tests;

/// <summary>
/// Verifies the fix for the "sliders don't move the APO file" bug: <see cref="MainViewModel.ApplyEq"/> routes
/// every slider move through the <see cref="IEqualizerApoWriter"/>, so the writer sees the latest gains with the
/// current enable/APO-attached state (not just the content Generate'd once at startup).
/// </summary>
public sealed class MainViewModelEqualizerApoTests
{
    private const string HeadsetGuid = "9a828b4a-e626-46d3-b084-908d7994216b";
    private const string HeadsetId = "{0.0.0.00000000}.{" + HeadsetGuid + "}";

    private readonly ITestOutputHelper _output;

    public MainViewModelEqualizerApoTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ApplyEq_WithPlanAndApoAttached_SendsCurrentGainsToTheWriter()
    {
        using var dir = new TempDirectory();
        var fake = new FakeApoWriter(dir.Path);
        using var vm = BuildViewModel(_ => fake, out var library);

        library.Settings.EqGainsDb[5] = 24f;
        library.Settings.EqEnabled = true;
        library.Settings.EqAutoPreamp = true;

        vm.SeedEqualizerApoForTests(
            new EqualizerApoState { Installed = true, ConfigPath = dir.Path, AttachedEndpointIds = new[] { HeadsetId } },
            new RoutingPlan { HeadsetId = HeadsetId, ApoActive = true });

        vm.ApplyEq();

        Assert.Equal(1, fake.CallCount);
        Assert.Equal(24f, fake.LastGainsDb[5]);
        Assert.True(fake.LastEqEnabled);
        Assert.True(fake.LastApoAttachedToHeadset);
        Assert.Equal(HeadsetId, fake.LastHeadsetDeviceId);
    }

    [Fact]
    public void BootAndSliderMove_WritesFilterOnStartup_ThenDropsItWhenGainReturnsToZero()
    {
        // Matches the task's scratch-host smoke: a vault whose slider is already +24 dB at 1 kHz boots the view
        // model → the real writer puts "Filter … Gain 24 … Preamp: -24 dB" into AudioController.txt within one
        // debounce. Dropping that band back to 0 dB and reapplying removes the filter line.
        using var dir = new TempDirectory();
        var writer = new EqualizerApoWriter(dir.Path);
        using var vm = BuildViewModel(_ => writer, out var library);

        library.Settings.EqGainsDb[5] = 24f;
        library.Settings.EqEnabled = true;
        library.Settings.EqAutoPreamp = true;
        library.Settings.EqAllAudio = true;

        vm.SeedEqualizerApoForTests(
            new EqualizerApoState { Installed = true, ConfigPath = dir.Path, AttachedEndpointIds = new[] { HeadsetId } },
            new RoutingPlan { HeadsetId = HeadsetId, ApoActive = true });

        vm.ApplyEq();
        writer.Flush();

        string path = System.IO.Path.Combine(dir.Path, EqualizerApoConfig.FileName);
        string boot = File.ReadAllText(path);
        _output.WriteLine("--- after boot (+24 dB at 1 kHz, EqEnabled, EqAllAudio, APO attached) ---");
        _output.WriteLine(boot);
        Assert.Contains("Filter 1: ON PK Fc 1000 Hz Gain 24 dB Q 1", boot);
        Assert.Contains("Preamp: -24 dB", boot);

        library.Settings.EqGainsDb[5] = 0f;
        vm.ApplyEq();
        writer.Flush();

        string after = File.ReadAllText(path);
        _output.WriteLine("--- after 1 kHz band back to 0 dB ---");
        _output.WriteLine(after);
        Assert.DoesNotContain("Filter", after);
        Assert.Contains("Preamp: 0 dB", after);
    }

    [Fact]
    public void ApplyEq_WithEqAllAudioOff_StillSendsAFlatCallThroughTheWriter()
    {
        // "EQ this app only" (plan.ApoActive=false) must drop any stale filters on Equalizer APO's side.
        using var dir = new TempDirectory();
        var fake = new FakeApoWriter(dir.Path);
        using var vm = BuildViewModel(_ => fake, out var library);

        library.Settings.EqGainsDb[5] = 24f;
        library.Settings.EqEnabled = true;

        vm.SeedEqualizerApoForTests(
            new EqualizerApoState { Installed = true, ConfigPath = dir.Path, AttachedEndpointIds = new[] { HeadsetId } },
            new RoutingPlan { HeadsetId = HeadsetId, ApoActive = false });

        vm.ApplyEq();

        Assert.Equal(1, fake.CallCount);
        Assert.False(fake.LastApoAttachedToHeadset);
    }

    private static MainViewModel BuildViewModel(Func<string, IEqualizerApoWriter> apoWriterFactory, out Library library)
    {
        // These tests drive the 1.4 ten-slider array (EqGainsDb), i.e. the 10-band layout (the default is 31 bands since 1.6).
        library = new Library { Settings = new AppSettings { EqGainsDb = new float[10], EqMode = EqMode.TenBandAdjustable } };
        var vault = new FakeVault(library);
        var devices = new ListDeviceManager(Array.Empty<AudioDeviceInfo>());
        var engine = new FakeAudioEngine();
        var systemEq = new SystemEqRouter();
        return new MainViewModel(
            vault, library, decoder: null, devices, engine, systemEq, Dispatcher.CurrentDispatcher,
            detectEqualizerApo: () => EqualizerApoState.NotInstalled,
            apoWriterFactory: apoWriterFactory);
    }

    /// <summary>Records every <see cref="IEqualizerApoWriter.WriteApoState"/> call for the fake-writer tests.</summary>
    internal sealed class FakeApoWriter : IEqualizerApoWriter
    {
        public FakeApoWriter(string dir)
        {
            ConfigDirectory = dir;
            FilePath = System.IO.Path.Combine(dir, EqualizerApoConfig.FileName);
        }

        public event EventHandler<string>? WriteFailed { add { } remove { } }
        public event EventHandler? Written { add { } remove { } }

        public string ConfigDirectory { get; }

        public string FilePath { get; }

        public bool FileExists => false;

        public int CallCount { get; private set; }

        public float[] LastGainsDb { get; private set; } = Array.Empty<float>();

        public bool LastEqEnabled { get; private set; }

        public string? LastHeadsetDeviceId { get; private set; }

        public bool LastApoAttachedToHeadset { get; private set; }

        public bool LastAutoPreamp { get; private set; }

        public void WriteApoState(float[] gainsDb, bool eqEnabled, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp)
        {
            CallCount++;
            LastGainsDb = (float[])gainsDb.Clone();
            LastEqEnabled = eqEnabled;
            LastHeadsetDeviceId = headsetDeviceId;
            LastApoAttachedToHeadset = apoAttachedToHeadset;
            LastAutoPreamp = autoPreamp;
        }

        public string? WriteNow(string content, bool ensureInclude) => null;

        public void Schedule(string content, bool ensureInclude) { }

        public void Flush() { }

        public void Dispose() { }
    }

    /// <summary>Just enough IVault for the MainViewModel composition: it reads <paramref name="library"/> as-is.</summary>
    private sealed class FakeVault(Library library) : IVault
    {
        public Library LoadLibrary() => library;

        public void SaveLibrary(Library l) { }

        public Guid AddSound(byte[] wavBytes) => Guid.NewGuid();

        public byte[] GetSound(Guid soundId) => Array.Empty<byte>();

        public void DeleteSound(Guid soundId) { }

        public bool SoundExists(Guid soundId) => false;
    }

    /// <summary>No-op engine: no audio devices are touched; status is reported empty.</summary>
    private sealed class FakeAudioEngine : IAudioEngine
    {
        public bool IsRunning => false;

        public bool AnySoundPlaying => false;

        public bool MicMuted { get; set; }

        public bool MonitorEqBypassed { get; private set; }

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

        public void SetMonitorEqBypass(bool bypass) => MonitorEqBypassed = bypass;

        public void Dispose() { }
    }
}
