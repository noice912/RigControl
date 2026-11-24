using System.Windows;
using System.Windows.Threading;
using AudioController.App.Hosting;
using AudioController.App.ViewModels;
using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;

namespace AudioController.Tests;

/// <summary>
/// RigControl hosting tests: one STA thread running a WPF dispatcher with one <see cref="Application"/> whose resources
/// are exactly what RigControl merges (<see cref="AudioHost.ResourcesUri"/>). WPF allows one Application per process,
/// so every WPF test runs its body on this thread. Nothing is ever shown on screen.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> s_dispatcher = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Dispatcher Dispatcher => s_dispatcher.Value;

    public static int ThreadId { get; private set; }

    public static void Run(Action action) => Dispatcher.Invoke(action);

    public static T Run<T>(Func<T> func) => Dispatcher.Invoke(func);

    private static Dispatcher Create()
    {
        Dispatcher? dispatcher = null;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ThreadId = Environment.CurrentManagedThreadId;
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AudioHost.ResourcesUri });
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (error is not null) throw new InvalidOperationException("The WPF test host could not start: " + error.Message, error);
        return dispatcher!;
    }
}

/// <summary>In-memory vault that logs what happens to it (and on which thread it was saved).</summary>
internal sealed class RecordingVault(Library library, List<string> log, Exception? loadError = null) : IVault, IDisposable
{
    public int? SaveThreadId { get; private set; }

    public Library LoadLibrary()
    {
        lock (log) log.Add("vault-load");
        if (loadError is not null) throw loadError;
        return library;
    }

    public void SaveLibrary(Library l)
    {
        SaveThreadId = Environment.CurrentManagedThreadId;
        lock (log) log.Add("vault-save");
    }

    public Guid AddSound(byte[] wavBytes) => Guid.NewGuid();

    public byte[] GetSound(Guid soundId) => Array.Empty<byte>();

    public void DeleteSound(Guid soundId) { }

    public bool SoundExists(Guid soundId) => false;

    public void Dispose()
    {
        lock (log) log.Add("vault-dispose");
    }
}

/// <summary>An engine that never touches a device; logs Dispose.</summary>
internal sealed class RecordingEngine(List<string> log) : IAudioEngine
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

    public void Dispose()
    {
        lock (log) log.Add("engine-dispose");
    }
}

internal static class HostingFakes
{
    public static string UniqueMutexName() => $@"Local\AudioController.Tests.{Guid.NewGuid():N}";

    /// <summary>A library that registers no global hotkey (the stop-all default would be a real, system-wide one).</summary>
    public static Library NewLibrary()
    {
        var library = new Library();
        library.Settings.StopAllHotkey = "";
        return library;
    }

    /// <summary>Every service fake: no vault on disk, no device, no Equalizer APO, no FFmpeg.</summary>
    public static AudioHostOptions Options(string mutexName, List<string> log, Func<IVault>? openVault = null) => new()
    {
        MutexName = mutexName,
        OpenVault = openVault ?? (() => new RecordingVault(NewLibrary(), log)),
        CreateDecoder = () => null,
        CreateDevices = () =>
        {
            lock (log) log.Add("devices");
            return new ListDeviceManager(Array.Empty<AudioDeviceInfo>());
        },
        CreateEngine = () => new RecordingEngine(log),
        CreateSystemEq = () => new SystemEqRouter(),
        CreateMirror = () => new MirrorRouter(new FakeMirrorEndpoints(), _ => TimeSpan.FromMinutes(1)),
        DetectEqualizerApo = () => EqualizerApoState.NotInstalled,
    };

    /// <summary>A view model over fakes, built the way the MainViewModel tests build theirs (call on the WPF thread).</summary>
    public static MainViewModel NewViewModel(Library library, List<string>? log = null) =>
        new(new RecordingVault(library, log ?? new List<string>()), library, decoder: null,
            new ListDeviceManager(Array.Empty<AudioDeviceInfo>()), new RecordingEngine(log ?? new List<string>()),
            new SystemEqRouter(), Dispatcher.CurrentDispatcher, detectEqualizerApo: () => EqualizerApoState.NotInstalled,
            mirror: new MirrorRouter(new FakeMirrorEndpoints(), _ => TimeSpan.FromMinutes(1)));

    /// <summary>True when nobody holds <paramref name="name"/> (creating it succeeds as a new object).</summary>
    public static bool MutexIsFree(string name)
    {
        using var probe = new Mutex(false, name, out bool createdNew);
        return createdNew;
    }
}
