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
/// The Setup card's "Also play on" section (<see cref="MainViewModel"/> + <see cref="MirrorTargetViewModel"/>) over a
/// <see cref="MirrorRouter"/> on fake endpoints: the list offers real outputs only, ticking a row stores it and copies
/// the headset there, the sliders change the copy live and are stored, and the status line says what happens.
/// </summary>
public sealed class MainViewModelMirrorTests
{
    private const string Headset = "{0.0.0.00000000}.{07abda82-1125-4339-aca5-dca468fc985a}";
    private const string Digital = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}";
    private const string Speakers = "{0.0.0.00000000}.{33333333-0000-0000-0000-000000000003}";
    private const string CableIn = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000004}";
    private const string CableA = "{0.0.0.00000000}.{77777777-0000-0000-0000-000000000007}";
    private const string Old = "{0.0.0.00000000}.{88888888-0000-0000-0000-000000000008}";
    private const string DigitalName = "Digital Output (Realtek USB2.0 Audio)";

    private static AudioDeviceInfo Dev(string id, string name, DeviceActivity state = DeviceActivity.Active, bool isVirtual = false) =>
        new() { Id = id, FriendlyName = name, Kind = DeviceKind.Playback, State = state, IsVirtual = isVirtual };

    private static List<AudioDeviceInfo> Devices() => new()
    {
        Dev(Headset, "Headphones (Crusher ANC 2)"),
        Dev(Digital, DigitalName),
        Dev(Speakers, "Speakers (Realtek(R) Audio)"),
        Dev(CableIn, "CABLE Input (VB-Audio Virtual Cable)", isVirtual: true),
        Dev(CableA, "CABLE-A Input (VB-Audio Cable A)", isVirtual: true),
        Dev(Old, "Old Speaker", DeviceActivity.Unplugged),
    };

    private static (MainViewModel Vm, Library Library, MirrorRouter Mirror, FakeMirrorEndpoints Endpoints) Build(Action<AppSettings>? setup = null)
    {
        var library = new Library();
        library.Settings.HeadsetDeviceId = Headset;
        setup?.Invoke(library.Settings);
        var endpoints = new FakeMirrorEndpoints();
        foreach (var d in Devices()) endpoints.Add(d.Id, d.FriendlyName, d.State);
        var mirror = new MirrorRouter(endpoints, _ => TimeSpan.FromMinutes(1));
        var vm = new MainViewModel(new NullVault(library), library, decoder: null, new ListDeviceManager(Devices()),
            new NullEngine(), new SystemEqRouter(), Dispatcher.CurrentDispatcher,
            detectEqualizerApo: () => EqualizerApoState.NotInstalled, mirror: mirror);
        vm.SeedDevicesForTests(Devices(), new RoutingPlan { HeadsetId = Headset }, windowsDefaultOutputId: Headset);
        return (vm, library, mirror, endpoints);
    }

    /// <summary>Runs the coordinator's mirror step off the test thread (as the debounce timer would) and waits for it.</summary>
    private static void Apply(MainViewModel vm) => Assert.True(Task.Run(() => vm.EnsureMirrorAsync()).Wait(TimeSpan.FromSeconds(10)));

    [Fact]
    public void List_OffersRealConnectedOutputs_NeverTheHeadsetTheCableOrVirtualDevices()
    {
        var (vm, _, mirror, _) = Build();
        using var _1 = vm;
        using var _2 = mirror;

        Assert.Equal(new[] { Digital, Speakers }, vm.MirrorDevices.Select(r => r.DeviceId));
        Assert.All(vm.MirrorDevices, r => Assert.True(r.IsConnected));
        Assert.All(vm.MirrorDevices, r => Assert.False(r.IsChecked));
        Assert.False(vm.MirrorEnabled);
        Assert.StartsWith("Off", vm.MirrorStatusText);
        Assert.False(vm.MirrorNoDevices);
    }

    [Fact]
    public void Ticking_StoresTheTarget_AndCopiesTheHeadsetThere_SlidersApplyLive()
    {
        var (vm, library, mirror, endpoints) = Build();
        using var _1 = vm;
        using var _2 = mirror;

        vm.MirrorEnabled = true;
        Apply(vm);
        Assert.Equal(MirrorPlanner.NothingTickedText, vm.MirrorStatusText);
        Assert.False(mirror.IsRunning);

        var row = vm.MirrorDevices.Single(r => r.DeviceId == Digital);
        row.IsChecked = true;
        row.Volume = 80.4;
        row.DelayMs = 183;
        Apply(vm);

        var stored = Assert.Single(library.Settings.MirrorTargets);
        Assert.Equal((Digital, DigitalName, true, 183, 80), (stored.DeviceId, stored.DeviceName, stored.Enabled, stored.DelayMs, stored.VolumePercent));
        Assert.True(library.Settings.MirrorEnabled);
        Assert.Equal(new[] { Digital }, mirror.ActiveTargetIds);
        Assert.Equal(Headset, Assert.Single(endpoints.Captures).DeviceId);
        Assert.Equal("Mirroring to " + DigitalName, vm.MirrorStatusText);
        Assert.False(vm.MirrorStatusIsError);
        Assert.Equal("playing", row.StatusText);
        Assert.Equal("80 %", row.VolumeText);
        Assert.Equal("183 ms", row.DelayText);

        row.Volume = 50;
        row.DelayMs = 999; // clamped to 500
        Apply(vm);
        Assert.Single(endpoints.Renders); // changed live, not reopened
        var t = Assert.Single(mirror.GetStatus().Targets);
        Assert.Equal((500, 50), (t.DelayMs, t.VolumePercent));
        Assert.Equal(500, stored.DelayMs);

        vm.MirrorDevices.Single(r => r.DeviceId == Speakers).IsChecked = true;
        Apply(vm);
        Assert.Equal(new[] { Digital, Speakers }, mirror.ActiveTargetIds);
        Assert.Equal($"Mirroring to {DigitalName} and Speakers (Realtek(R) Audio)", vm.MirrorStatusText);

        row.IsChecked = false;
        Apply(vm);
        Assert.Equal(new[] { Speakers }, mirror.ActiveTargetIds);
        Assert.False(stored.Enabled);
        Assert.Equal(500, stored.DelayMs); // an unticked output keeps its settings

        vm.MirrorEnabled = false;
        Apply(vm);
        Assert.False(mirror.IsRunning);
        Assert.All(endpoints.Renders, r => Assert.True(r.Disposed));
        Assert.StartsWith("Off", vm.MirrorStatusText);
    }

    [Fact]
    public void SavedTargets_RestoreOnStartup_AndAGoneOutputStaysListedAsNotConnected()
    {
        var (vm, _, mirror, _) = Build(s =>
        {
            s.MirrorEnabled = true;
            s.MirrorTargets.Add(new MirrorTarget { DeviceId = Digital, DeviceName = DigitalName, DelayMs = 150, VolumePercent = 90 });
            s.MirrorTargets.Add(new MirrorTarget { DeviceId = Old, DeviceName = "Old Speaker" });
        });
        using var _1 = vm;
        using var _2 = mirror;

        Apply(vm);

        Assert.Equal(new[] { Digital }, mirror.ActiveTargetIds);
        var t = Assert.Single(mirror.GetStatus().Targets);
        Assert.Equal((150, 90), (t.DelayMs, t.VolumePercent));
        var gone = vm.MirrorDevices.Single(r => r.DeviceId == Old);
        Assert.False(gone.IsConnected);
        Assert.True(gone.IsChecked);
        Assert.Contains("not connected", gone.Name);
        Assert.Equal("not connected", gone.StatusText);
        Assert.True(gone.StatusIsError);
    }

    [Fact]
    public void HeadsetDisconnected_StopsTheMirror_WithAPlainReason()
    {
        var (vm, _, mirror, _) = Build(s =>
        {
            s.MirrorEnabled = true;
            s.MirrorTargets.Add(new MirrorTarget { DeviceId = Digital, DeviceName = DigitalName });
        });
        using var _1 = vm;
        using var _2 = mirror;
        Apply(vm);
        Assert.True(mirror.IsRunning);

        vm.SeedDevicesForTests(Devices(), new RoutingPlan { HeadsetId = null, HeadsetMissing = true });
        Apply(vm);

        Assert.False(mirror.IsRunning);
        Assert.Equal(MirrorPlanner.HeadsetMissingText, vm.MirrorStatusText);
        Assert.True(vm.MirrorStatusIsError);
    }

    [Fact]
    public void PcAudioOnAnotherDevice_IsExplained()
    {
        var (vm, _, mirror, _) = Build(s =>
        {
            s.MirrorEnabled = true;
            s.MirrorTargets.Add(new MirrorTarget { DeviceId = Digital, DeviceName = DigitalName });
        });
        using var _1 = vm;
        using var _2 = mirror;

        vm.SeedDevicesForTests(Devices(), new RoutingPlan { HeadsetId = Headset }, windowsDefaultOutputId: Speakers);
        Apply(vm);

        Assert.Contains("Speakers (Realtek(R) Audio)", vm.MirrorNoteText);
        Assert.Contains("only what plays on the headset is copied", vm.MirrorNoteText);
    }

    [Fact]
    public void Shutdown_DisposesTheMirror()
    {
        var (vm, _, mirror, endpoints) = Build(s =>
        {
            s.MirrorEnabled = true;
            s.MirrorTargets.Add(new MirrorTarget { DeviceId = Digital, DeviceName = DigitalName });
        });
        Apply(vm);
        Assert.True(mirror.IsRunning);

        vm.Dispose();

        Assert.False(mirror.IsRunning);
        Assert.All(endpoints.Captures, c => Assert.True(c.Disposed));
        Assert.All(endpoints.Renders, r => Assert.True(r.Disposed));
        Assert.Throws<ObjectDisposedException>(() => mirror.Apply(Headset, Array.Empty<MirrorRoute>()));
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
