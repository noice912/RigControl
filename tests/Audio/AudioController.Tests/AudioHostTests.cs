using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using AudioController.App;
using AudioController.App.Hosting;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// RigControl: <see cref="AudioHost"/>, the composition root that replaced the standalone App class. Every test uses
/// fakes and a unique lock name, so the real vault (%LOCALAPPDATA%\AudioController), devices and Equalizer APO are
/// never touched.
/// </summary>
public sealed class AudioHostTests
{
    [Fact]
    public void ResourcesUri_IsThePackUriOfAudioResources()
    {
        Assert.Equal("pack://application:,,,/AudioController;component/Themes/AudioResources.xaml", AudioHost.ResourcesUri.OriginalString);
    }

    [Fact]
    public void Lock_IsTheStandaloneAppsMutexName()
    {
        Assert.Equal(@"Local\AudioController.SingleInstance.v1", AudioHost.SingleInstanceMutexName);
        Assert.Equal(AudioHost.SingleInstanceMutexName, AudioHostOptions.Default.MutexName);
    }

    [Fact]
    public void Start_WhileTheStandaloneAppHoldsTheLock_IsAlreadyRunningElsewhere_AndOpensNothing()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();

        // Exactly what the standalone App.OnStartup does: an owned mutex on its UI thread.
        using var standalone = new Mutex(initiallyOwned: true, name, out bool createdNew);
        Assert.True(createdNew);
        try
        {
            var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log)));

            Assert.Equal(AudioHostState.AlreadyRunningElsewhere, host.State);
            Assert.Contains("Close the standalone Audio Controller", host.Message);
            Assert.Empty(log); // no vault, no devices, no engine
            WpfTestHost.Run(() => Assert.IsNotType<AudioView>(host.View));

            host.Dispose();
            host.Dispose();
            Assert.False(HostingFakes.MutexIsFree(name)); // the standalone app still holds it
        }
        finally
        {
            standalone.ReleaseMutex();
        }
    }

    [Fact]
    public void SecondStart_WhileTheFirstRuns_IsAlreadyRunningElsewhere_UntilTheFirstIsDisposed()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();

        var first = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log)));
        Assert.Equal(AudioHostState.Running, first.State);
        Assert.Null(first.Message);

        var second = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, new List<string>())));
        Assert.Equal(AudioHostState.AlreadyRunningElsewhere, second.State);
        second.Dispose();
        Assert.False(HostingFakes.MutexIsFree(name)); // disposing the refused host does not release the first one's lock

        WpfTestHost.Run(first.Dispose);
        Assert.True(HostingFakes.MutexIsFree(name));

        var third = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, new List<string>())));
        Assert.Equal(AudioHostState.Running, third.State);
        WpfTestHost.Run(third.Dispose);
    }

    [Fact]
    public void Start_UndecryptableVault_IsVaultUnreadable_WritesNothing_AndReleasesTheLock()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();
        var options = HostingFakes.Options(name, log,
            openVault: () => new RecordingVault(HostingFakes.NewLibrary(), log, new CryptographicException("The computed authentication tag did not match.")));

        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, options));

        Assert.Equal(AudioHostState.VaultUnreadable, host.State);
        Assert.StartsWith("Your saved Audio Controller data is tampered/undecryptable, so it was not loaded.", host.Message);
        Assert.Contains("Details: The computed authentication tag did not match.", host.Message);
        Assert.Equal(new[] { "vault-load", "vault-dispose" }, log); // nothing saved, no audio objects created
        Assert.True(HostingFakes.MutexIsFree(name)); // the standalone app may be used instead
        WpfTestHost.Run(() => Assert.IsNotType<AudioView>(host.View));

        host.Dispose();
        host.Dispose();
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Start_WhenAnAudioServiceFails_IsFailed_AndReleasesWhatItTook()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();
        var options = HostingFakes.Options(name, log);
        options = new AudioHostOptions
        {
            MutexName = options.MutexName,
            OpenVault = options.OpenVault,
            CreateDecoder = options.CreateDecoder,
            CreateDevices = options.CreateDevices,
            CreateEngine = () => throw new InvalidOperationException("No audio engine today."),
            CreateSystemEq = options.CreateSystemEq,
            CreateMirror = options.CreateMirror,
            DetectEqualizerApo = options.DetectEqualizerApo,
        };

        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, options));

        Assert.Equal(AudioHostState.Failed, host.State);
        Assert.Equal("Audio Controller could not start.\n\nNo audio engine today.", host.Message);
        Assert.Contains("vault-dispose", log);
        Assert.DoesNotContain("vault-save", log);
        Assert.True(HostingFakes.MutexIsFree(name));
        host.Dispose();
    }

    [Fact]
    public void Start_OffTheUiThread_IsFailed_AndNeverThrows()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();

        var host = AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log)); // the xunit thread
        var nullDispatcher = AudioHost.Start(null, HostingFakes.Options(name, log));

        Assert.Equal(AudioHostState.Failed, host.State);
        Assert.Contains("UI thread", host.Message);
        Assert.Equal(AudioHostState.Failed, nullDispatcher.State);
        Assert.Empty(log);
        Assert.True(HostingFakes.MutexIsFree(name));
        host.Dispose();
        nullDispatcher.Dispose();
    }

    [Fact]
    public void Running_Dispose_SavesAndShutsDown_ThenDisposesTheVault_ThenReleasesTheLock_Once()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();

        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log)));
        Assert.Equal(AudioHostState.Running, host.State);
        WpfTestHost.Run(() => Assert.IsType<AudioView>(host.View));
        Assert.NotNull(host.ViewModel);
        Assert.False(HostingFakes.MutexIsFree(name));

        WpfTestHost.Run(host.Dispose);

        string[] entries;
        lock (log) entries = log.ToArray();
        int lastSave = Array.LastIndexOf(entries, "vault-save");
        int engine = Array.IndexOf(entries, "engine-dispose");
        int vault = Array.IndexOf(entries, "vault-dispose");
        Assert.True(lastSave >= 0, string.Join(", ", entries));
        Assert.True(lastSave < engine && engine < vault, string.Join(", ", entries)); // vm.Shutdown (save, engine), then the vault
        Assert.True(HostingFakes.MutexIsFree(name)); // and then the lock

        WpfTestHost.Run(host.Dispose);
        host.Dispose();
        lock (log) Assert.Equal(entries, log);
    }

    [Fact]
    public void Dispose_FromAnotherThread_ShutsDownOnTheUiThread()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();
        RecordingVault? vault = null;
        var options = HostingFakes.Options(name, log, openVault: () => vault = new RecordingVault(HostingFakes.NewLibrary(), log));

        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, options));
        Assert.Equal(AudioHostState.Running, host.State);

        host.Dispose(); // the xunit thread, not the UI thread

        Assert.Equal(WpfTestHost.ThreadId, vault!.SaveThreadId);
        Assert.True(HostingFakes.MutexIsFree(name));
    }

    [Fact]
    public void Dispose_FromAnotherThread_WhileTheUiThreadIsStuck_StillReleasesTheLock_AndNeverThrows()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();
        var baseOptions = HostingFakes.Options(name, log);
        var options = new AudioHostOptions
        {
            MutexName = baseOptions.MutexName,
            OpenVault = baseOptions.OpenVault,
            CreateDecoder = baseOptions.CreateDecoder,
            CreateDevices = baseOptions.CreateDevices,
            CreateEngine = baseOptions.CreateEngine,
            CreateSystemEq = baseOptions.CreateSystemEq,
            CreateMirror = baseOptions.CreateMirror,
            DetectEqualizerApo = baseOptions.DetectEqualizerApo,
            UiShutdownWait = TimeSpan.FromMilliseconds(150),
        };

        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, options));
        Assert.Equal(AudioHostState.Running, host.State);

        // The UI thread is busy and will not run the shutdown: the lock (taken on the UI thread) and the vault must
        // still be released by this thread. The original's owned mutex threw ApplicationException here.
        using var uiBusy = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        WpfTestHost.Dispatcher.BeginInvoke(() =>
        {
            uiBusy.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        Assert.True(uiBusy.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            host.Dispose();
            host.Dispose();

            Assert.True(HostingFakes.MutexIsFree(name));
            lock (log)
            {
                Assert.Contains("vault-save", log);
                Assert.Equal("vault-dispose", log[^1]);
            }
        }
        finally
        {
            release.Set();
        }

        WpfTestHost.Run(host.Dispose); // and the UI thread, once it is back, has nothing left to do
        lock (log) Assert.Single(log, e => e == "vault-dispose");
    }

    [Fact]
    public void Running_SessionEndingAndEmergencyRestore_SaveAndNeverThrow_AlsoAfterDispose()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();
        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log)));

        WpfTestHost.Run(host.OnSessionEnding);
        lock (log) Assert.Contains("vault-save", log);
        host.EmergencyRestore(); // any thread, no UI

        WpfTestHost.Run(host.Dispose);
        WpfTestHost.Run(host.OnSessionEnding);
        host.EmergencyRestore();
    }

    [Fact]
    public void AttachToWindow_WiresTheHostWindow_OnlyOnce_AndNotAfterDispose()
    {
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();

        WpfTestHost.Run(() =>
        {
            var host = AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log));
            Assert.Equal(AudioHostState.Running, host.State);

            var window = new Window(); // never shown; a handle is all the hotkeys need
            new WindowInteropHelper(window).EnsureHandle();
            host.AttachToWindow(window);
            host.AttachToWindow(window);

            Assert.DoesNotContain("hotkeys are unavailable", host.ViewModel!.StatusMessage ?? "");

            host.Dispose();
            host.AttachToWindow(new Window());
            window.Close();
        });
    }

    [Fact]
    public void NotRunning_AttachToWindow_IsIgnored()
    {
        string name = HostingFakes.UniqueMutexName();
        using var held = new Mutex(false, name);

        WpfTestHost.Run(() =>
        {
            var host = AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, new List<string>()));
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            host.AttachToWindow(window);
            host.OnSessionEnding();
            host.EmergencyRestore();
            host.Dispose();
            window.Close();
        });
    }
}
