using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using AudioController.App.Hosting;
using RigControl.Capture;
using RigControl.Cooling;
using RigControl.Drivers;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Lighting;
using RigControl.Nvidia;
using RigControl.Sensors;

namespace RigControl.App;

public partial class App : System.Windows.Application
{
    /// <summary>Command-line switch: wait for this process id to exit before starting (used by restart-as-administrator).</summary>
    public const string AfterPidSwitch = "--after-pid";

    private SingleInstance? _single;
    private FileLog? _log;
    private AppServices? _services;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private Timer? _heartbeat;
    private OverlayWindow? _overlay;
    private GamePanel? _panel;
    private CoolingModule? _cooling;
    private Updates.GitHubServices? _github;
    private Updates.UpdatesModule? _updates;

    /// <summary>Shows temperatures in °F (true) or °C everywhere outside the web pages: the cooler screen and the overlay.</summary>
    public void ApplyTemperatureUnit(string? unit)
    {
        var fahrenheit = !string.Equals(unit, "C", StringComparison.OrdinalIgnoreCase);
        _cooling?.SetFahrenheit(fahrenheit);
        Dispatcher.BeginInvoke(() => _overlay?.SetFahrenheit(fahrenheit));
    }
    private string? _ffmpegExe;
    private AudioHost? _audio;
    private string? _audioResourcesError;
    private readonly CancellationTokenSource _lifetime = new();
    private int _exitStarted;

    /// <summary>The merged Audio Controller, or null if it could not be created at all.</summary>
    public AudioHost? Audio => _audio;

    public MainWindow? Window => _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        var dataDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "RigControl");
        var logDir = Path.Combine(dataDir, "logs");
        try { Directory.CreateDirectory(dataDir); } catch { /* FileLog and SettingsStore cope with a missing folder */ }
        _log = new FileLog(logDir);

        // Started as a short-lived elevated helper to add/remove the "start with Windows as administrator" task?
        // Then do only that: no window, no hardware, no single-instance check.
        var helperExit = Autostart.HandleCommandLine(e.Args, System.Environment.ProcessPath ?? string.Empty,
            message => _log.For("autostart").Info(message));
        if (helperExit is { } exitCode)
        {
            Interlocked.Exchange(ref _exitStarted, 1);
            Shutdown(exitCode);
            return;
        }

        // Setup, "--update", "--uninstall", or RigControl itself? (Install\InstallEntry.cs; the release is one exe.)
        if (Install.InstallEntry.Handle(e.Args, _log.For("install")) is { } installExit)
        {
            Interlocked.Exchange(ref _exitStarted, 1);
            Shutdown(installExit);
            return;
        }

        WaitForPreviousInstance(e.Args);

        // "RigControl.exe --exit": ask the running copy to close properly (finishing recordings, handing fans back).
        if (e.Args.Any(x => string.Equals(x, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            using var probe = new SingleInstance("RigControl");
            var asked = probe.SignalExit();
            _log?.Info(asked ? "Asked the running copy to exit (--exit)." : "--exit: no running copy to ask.");
            Shutdown(0);
            return;
        }

        _single = new SingleInstance("RigControl");
        if (!_single.TryAcquire() && !TakeOverFromCopyWithoutAdminRights(_single))
        {
            _single.SignalExisting();
            _single.Dispose();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Unhandled UI exception.", args.Exception);
            args.Handled = true; // keep the app (and the fan control it may be doing) alive
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _log.Error("Unhandled exception (fatal=" + args.IsTerminating + ").", args.ExceptionObject as Exception);
            // Best effort: never leave Windows' default output on the virtual cable.
            try { _audio?.EmergencyRestore(); } catch { /* nothing more we can do */ }
        };
        // Leaves a trace in the log whenever the process ends through .NET (not when Windows kills it outright),
        // and a line every few minutes so a silent end can at least be dated.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _log?.Info("Process ending.");
        _heartbeat = new Timer(_ => _log?.Info("Still running."), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        // The full version, with a development build's "-rc.N" (the numeric assembly version drops it).
        var version = Updates.RunningVersion.Text;
        var env = new AppEnvironment(dataDir, logDir, AppEnvironment.DetectElevated(), version,
            System.Environment.ProcessPath ?? string.Empty);
        _log.Info($"RigControl {version} starting. Elevated={env.IsElevated}. Exe={env.ExecutablePath}");

        // The Audio section's XAML resolves its styles from application resources, so merge them before any view exists.
        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AudioHost.ResourcesUri });
        }
        catch (Exception ex)
        {
            _audioResourcesError = "The Audio section's styles could not be loaded: " + ex.Message;
            _log.Error("Audio resources failed to load.", ex);
        }

        // A settings file that is already there means RigControl was in use on this PC before: no first-time setup then.
        var settingsPath = Path.Combine(dataDir, "settings.json");
        var settingsExisted = FirstRunState.SettingsExist(settingsPath);
        var settings = new SettingsStore(settingsPath, _log.For("settings"));
        _log.For("settings").Info($"Settings read from {settingsPath}: {string.Join(", ", settings.SectionNames)}.");
        FirstRunState.Initialise(settings, settingsExisted, _log.For("setup"));
        var bus = new EventBus();
        var dispatcher = new RpcDispatcher(_log.For("rpc"));
        var host = new ModuleHost(_log.For("modules"));

        var cooling = new CoolingModule();
        _cooling = cooling;
        // The cooler screen's "web page" mode: a hidden browser whose picture is sent to the screen.
        var screenPage = new WebLcdHost(Dispatcher, dataDir, _log.For("screen"));
        cooling.SetWebFrameSource(screenPage.Latest);
        // The cooler's screen infographic shows readings that come from the sensors section.
        bus.Published += (topic, payload) =>
        {
            if (topic == "sensors.snapshot" && payload is SensorSnapshotDto snap)
                cooling.SetMetrics(new LcdMetrics(snap.Cpu?.PackageTempC ?? snap.Cpu?.CoreMaxTempC, snap.Gpu?.TempC, null,
                    snap.Cpu?.LoadPercent, snap.Gpu?.LoadPercent));
        };
        // Recording uses the converter that is bundled for the Audio section; it is unpacked once, on first use.
        var capture = new CaptureModule(new FfmpegCaptureEngine(() =>
        {
            if (_ffmpegExe is not null && File.Exists(_ffmpegExe)) return _ffmpegExe;
            var dir = AudioHost.ExtractFfmpegDirectory();
            return _ffmpegExe = dir is null ? null : Path.Combine(dir, "ffmpeg.exe");
        }), new GlobalHotkeys());
        _overlay = new OverlayWindow();
        bus.Published += (topic, payload) =>
        {
            // Events arrive on background threads; the overlay belongs to the UI thread.
            switch (topic, payload)
            {
                case ("capture.status", CaptureStatusDto status):
                    Dispatcher.BeginInvoke(() => _overlay?.SetStatus(status));
                    break;
                case ("capture.notice", CaptureNoticeDto notice):
                    Dispatcher.BeginInvoke(() => _overlay?.ShowNotice(notice));
                    break;
                case ("sensors.snapshot", SensorSnapshotDto snap):
                    Dispatcher.BeginInvoke(() => _overlay?.SetSnapshot(snap));
                    break;
            }
        };
        ApplyTemperatureUnit(settings.Get<AppSettings>(AppSettings.Section).TemperatureUnit);
        var lighting = new LightingModule();
        // The cooler screen's letters can follow the lights' colour.
        lighting.SyncColorChanged += c => cooling.SetLightsColor(c);
        var programFolderOpen = new Lazy<bool>(() =>
            !string.IsNullOrEmpty(env.ExecutablePath) && RigControl.Bundled.FolderGuard.OthersCanChange(env.ExecutablePath));
        var modules = new List<IAppModule>
        {
            new SensorsModule(),
            new NvidiaModule(),
            cooling,
            lighting,
            capture,
            new DriversModule(),
            new RigControl.Bios.BiosModule(),
            // The processor page for any PC: what the processor is, and Windows' own power-plan settings.
            new RigControl.Cpu.CpuModule(),
            new RigControl.Presence.PresenceModule(),
            new RigControl.Health.HealthModule
            {
                // Told on the Health page: RigControl starts as administrator from a folder any program can change.
                StartsAsAdminFromOpenFolder = () => settings.Get<AppSettings>(AppSettings.Section).AutostartRegistered && programFolderOpen.Value,
            },
        };
        HostModules.Register(modules, host, dispatcher);

        _services = new AppServices(env, settings, bus, dispatcher, host, _log);
        dispatcher.Register(new AppModule(_services, this));
        // Updates, GitHub sign-in and reviews, and dev mode (Updates\). They only ever connect to GitHub.
        _github = new Updates.GitHubServices(_services);
        _updates = new Updates.UpdatesModule(_services, this, _github);
        dispatcher.Register(_updates);
        var githubModule = new Updates.GitHubModule(_services, _github);
        var devModule = new Updates.DeveloperModule(_services, _github, new Updates.WindowsHelloKey());
        githubModule.DevModeClosed += devModule.Close;
        dispatcher.Register(githubModule);
        dispatcher.Register(devModule);
        _updates.Start();
        githubModule.Start();
        var panelModule = new GamePanelModule(_services, () => _panel);
        dispatcher.Register(panelModule);
        try
        {
            _panel = new GamePanel(_services);
            panelModule.SetHotkeyProblem(_panel.ApplySettings());
        }
        catch (Exception ex)
        {
            _log.Error("The in-game panel could not be set up.", ex);
        }

        _window = new MainWindow(_services, this);
        _window.SourceInitialized += (_, _) => AttachAudio();
        _tray = new TrayIcon(ShowMainWindow, () => RequestExit());
        _single.ShowRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
        _single.ExitRequested += () =>
        {
            _log?.Info("Exit requested by another copy (--exit).");
            RequestExit();
        };

        StartAudio();

        var appSettings = settings.Get<AppSettings>(AppSettings.Section);
        RepairAutostart(env, appSettings);
        if (startMinimized || appSettings.StartMinimized)
        {
            // Hidden in the tray: still create the window handle so soundboard hotkeys work.
            new WindowInteropHelper(_window).EnsureHandle();
        }
        else
        {
            _window.Show();
        }

        // The programs RigControl carries (OpenRGB, PawnIO, Equalizer APO). They are only set up after the user's one yes for all;
        // what is already on the PC stays as it is. Needs administrator rights. The yes that counts is kept where
        // only administrators can write: the settings file is open to every program the user runs, so a "yes" in
        // there never installs anything.
        var bundledLog = _log.For("bundled");
        var adminConsent = new AdminConsentStore();
        var consent = BundledConsentSource.Load(env.IsElevated, adminConsent, settings);
        Bundled = new RigControl.Bundled.BundledApps(s => bundledLog.Info(s), s => bundledLog.Warn(s), consent: consent)
        {
            ConsentChanged = c =>
            {
                try { BundledConsentSource.Save(env.IsElevated, adminConsent, settings, c); }
                catch (Exception ex) { bundledLog.Warn("Your answer could not be saved, so RigControl will ask again next time: " + ex.Message); }
            },
        };
        // RigControl's own OpenRGB is only started when every file still matches what this build carries. A build
        // that carries no OpenRGB has nothing to compare with: a copy an earlier build left is then still held to
        // the "only administrators can change its folder" rule, and started as before.
        OpenRgbServer.OwnCopyVerifier = Bundled.OwnCopyVerifier;

        _ = StartBundledThenModulesAsync(Bundled, host, env, settings, bus);
    }

    /// <summary>
    /// Sets up the carried programs first (so the lights find OpenRGB at the first try), then starts the modules.
    /// A set-up that takes long (an installer after a yes) does not hold the modules back for more than half a minute.
    /// </summary>
    private async Task StartBundledThenModulesAsync(RigControl.Bundled.BundledApps bundled, ModuleHost host, AppEnvironment env,
        SettingsStore settings, EventBus bus)
    {
        if (env.IsElevated)
        {
            try
            {
                var setUp = Task.Run(bundled.EnsureAll);
                if (await Task.WhenAny(setUp, Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token)) == setUp) await setUp;
                else if (_lifetime.IsCancellationRequested) return; // RigControl is closing
                else _log?.For("bundled").Info("Setting up the carried programs is taking a while; the rest of RigControl starts now.");
            }
            catch (Exception ex)
            {
                _log?.Error("Setting up the carried programs failed.", ex);
            }
        }
        await StartModulesAsync(host, env, settings, bus);
    }

    /// <summary>The programs RigControl carries inside its exe.</summary>
    public RigControl.Bundled.BundledApps? Bundled { get; private set; }

    /// <summary>

    /// <summary>
    /// At sign-in a copy without administrator rights (started from the Start menu, say) can be running already when the
    /// start-up task's copy, which has them, arrives. Giving way would leave RigControl running with half its features,
    /// so a copy with administrator rights asks one without them to close (properly, as "--exit" does) and takes its
    /// place. A copy that has administrator rights itself is never asked to close.
    /// </summary>
    private bool TakeOverFromCopyWithoutAdminRights(SingleInstance single)
    {
        if (!AppEnvironment.DetectElevated()) return false;
        var others = Install.RunningCopy.OthersElevated();
        if (others is not false) return false; // one has administrator rights too, or it can't be told: leave it
        _log?.Info("A copy of RigControl without administrator rights is running; asking it to close so this one (with them) takes over.");
        using (var probe = new SingleInstance("RigControl"))
        {
            if (!probe.SignalExit()) return false;
        }
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(250);
            if (single.TryAcquire())
            {
                _log?.Info("Took over from the copy without administrator rights.");
                return true;
            }
        }
        _log?.Warn("The copy without administrator rights did not close in time; it keeps running.");
        return false;
    }

    /// <summary>
    /// After "restart as administrator" the new copy starts while the old one is still shutting down (saving the audio
    /// library, handing fans back to the BIOS). Wait for it so the two never control the same hardware at once.
    /// </summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, AfterPidSwitch, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(20000);
        }
        catch
        {
            // Already gone (or not ours to inspect): carry on.
        }
    }

    /// <summary>
    /// If the startup task points at a RigControl.exe that no longer exists (the exe was moved or replaced in a new
    /// folder), point it at this one. Only possible when already elevated; never prompts. Only this Windows user's
    /// own task is looked at and changed (<see cref="Autostart.Query()"/>), never another user's.
    /// </summary>
    private void RepairAutostart(AppEnvironment env, AppSettings appSettings)
    {
        if (!appSettings.AutostartRegistered || !env.IsElevated || string.IsNullOrEmpty(env.ExecutablePath)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var state = Autostart.Query();
                if (!state.Known || !state.Enabled || state.Command is null) return;
                // An install that is already there is never refused or removed for this; it is only said.
                if (RigControl.Bundled.FolderGuard.OthersCanChange(env.ExecutablePath)) _log?.For("autostart").Warn(Autostart.OpenFolderWarning);
                if (string.Equals(state.Command, env.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
                if (File.Exists(state.Command)) return; // still valid: leave the user's choice alone
                var error = Autostart.Install(env.ExecutablePath, Autostart.CurrentUserSid());
                _log?.For("autostart").Info(error is null
                    ? "Startup task repointed to " + env.ExecutablePath
                    : "Startup task could not be repointed: " + error);
            }
            catch (Exception ex)
            {
                _log?.Error("Checking the startup task failed.", ex);
            }
        });
    }

    private void StartAudio()
    {
        if (_audioResourcesError is not null) return;
        // Testing aid: RIGCONTROL_NO_AUDIO=1 runs everything except the Audio section (no sound library, no audio devices).
        if (System.Environment.GetEnvironmentVariable("RIGCONTROL_NO_AUDIO") == "1")
        {
            _audioResourcesError = "The Audio section is switched off for this run (RIGCONTROL_NO_AUDIO=1).";
            _log?.Info("Audio section skipped: RIGCONTROL_NO_AUDIO=1.");
            return;
        }
        try
        {
            _audio = AudioHost.Start(Dispatcher);
            _log?.Info($"Audio section: {_audio.State} {_audio.Message}");
        }
        catch (Exception ex)
        {
            // AudioHost.Start is documented never to throw; this is a last line of defence.
            _audioResourcesError = "The Audio section could not start: " + ex.Message;
            _log?.Error("Audio section failed to start.", ex);
        }
    }

    private void AttachAudio()
    {
        if (_audio is null || _window is null) return;
        try
        {
            _audio.AttachToWindow(_window);
        }
        catch (Exception ex)
        {
            _log?.Error("Attaching the Audio section to the window failed.", ex);
        }
    }

    /// <summary>The view to show for the Audio section (the audio app itself, or a panel explaining why it is unavailable).</summary>
    public FrameworkElement AudioView()
    {
        if (_audio is not null) return _audio.View;
        return new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x1C, 0x23)),
            Padding = new Thickness(40),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = _audioResourcesError ?? "The Audio section is starting.",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xE9, 0xEE)),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                MaxWidth = 640,
            },
        };
    }

    public (string State, string? Message) AudioStatus()
    {
        if (_audio is null) return (_audioResourcesError is null ? "starting" : "failed", _audioResourcesError);
        var state = _audio.State switch
        {
            AudioHostState.Running => "running",
            AudioHostState.AlreadyRunningElsewhere => "alreadyRunningElsewhere",
            AudioHostState.VaultUnreadable => "vaultUnreadable",
            _ => "failed",
        };
        return (state, _audio.Message);
    }

    private Task StartModulesAsync(ModuleHost host, AppEnvironment env, SettingsStore settings, EventBus bus)
        => HostModules.StartAllAsync(host, env, settings, bus, _log!, _lifetime.Token);

    public void ShowMainWindow()
    {
        // A "show" request can still arrive while RigControl is closing (another copy starting up asks for it): the
        // window is already closed then and can't be shown again.
        if (_window is null || Volatile.Read(ref _exitStarted) != 0 || _window.IsClosed) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ShowTrayNotice(string title, string text) => _tray?.ShowNotice(title, text);

    /// <summary>Releases the single-instance lock early so a replacement process (e.g. the elevated one) can start.</summary>
    public void ReleaseSingleInstance() => _single?.Dispose();

    public bool ReacquireSingleInstance()
    {
        _single?.Dispose();
        _single = new SingleInstance("RigControl");
        var ok = _single.TryAcquire();
        if (ok)
        {
            _single.ShowRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
            _single.ExitRequested += () => RequestExit();
        }
        return ok;
    }

    /// <summary>
    /// Shuts everything down in order: hardware modules first (so fans go back to safe control), then the audio
    /// section (saves the library and puts the Windows default output back on the headset), then the UI.
    /// </summary>
    public void RequestExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RequestExit());
            return;
        }
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;

        _log?.Info("Exit requested.");
        try { _lifetime.Cancel(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _updates?.Dispose(); } catch { /* ignore */ }
        try { _github?.Dispose(); } catch { /* ignore */ }

        StopModules();
        StopAudio();

        try { _panel?.Close(); } catch { /* ignore */ }
        try { _window?.ForceClose(); } catch { /* ignore */ }
        try { _single?.Dispose(); } catch { /* ignore */ }
        Shutdown(0);
    }

    private void StopModules()
    {
        var host = _services?.Host;
        if (host is null) return;
        try
        {
            // Run off the UI thread so module code that marshals back to it cannot deadlock.
            var stop = Task.Run(async () => await host.DisposeAsync());
            if (!stop.Wait(TimeSpan.FromSeconds(12)))
                _log?.Warn("Modules did not stop within 12s; exiting anyway.");
        }
        catch (Exception ex)
        {
            _log?.Error("Stopping modules failed.", ex);
        }
    }

    private void StopAudio()
    {
        // On the UI thread: the audio view model is bound to it.
        try { _audio?.Dispose(); }
        catch (Exception ex) { _log?.Error("Stopping the Audio section failed.", ex); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off / shutting down. Not final: another program can still cancel it, so only do the
        // non-destructive part here (save the audio library, put the default output back). If the session really
        // ends, WPF shuts the application down next and OnExit does the rest.
        _log?.Info("Windows session is ending (" + e.ReasonSessionEnding + ").");

        // Windows waits only a few seconds for each program before it shows "this app is preventing shutdown".
        // Whatever happens below, RigControl is gone within that time: this timer ends the process outright.
        var deadline = new Thread(() =>
        {
            Thread.Sleep(4000);
            _log?.Warn("Closing took too long while Windows is shutting down; ending now.");
            Environment.Exit(0);
        }) { IsBackground = true, Name = "RigControl shutdown deadline" };
        deadline.Start();

        try
        {
            _audio?.OnSessionEnding();
            _log?.Info("Audio library saved for shutdown.");
        }
        catch (Exception ex) { _log?.Error("Audio session-ending save failed.", ex); }

        // Finish a running recording and hand fans and the cooler screen back, as far as there is time. Windows
        // resets the hardware on a real shutdown anyway, so this is a courtesy, not a requirement.
        if (Interlocked.Exchange(ref _exitStarted, 1) == 0)
        {
            var host = _services?.Host;
            if (host is not null)
            {
                try
                {
                    var stop = Task.Run(async () => await host.DisposeAsync());
                    _log?.Info(stop.Wait(TimeSpan.FromSeconds(2.5)) ? "Hardware handed back for shutdown." : "Hardware hand-back was still running when time ran out.");
                }
                catch (Exception ex) { _log?.Error("Stopping modules for shutdown failed.", ex); }
            }
            _log?.Info("Application exiting (Windows shutdown).");
            // If another program cancels the shutdown, RigControl is simply closed; that is better than a stuck shutdown.
            Environment.Exit(0);
        }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normal exits go through RequestExit. This catches the others (Windows ending the session).
        if (Interlocked.Exchange(ref _exitStarted, 1) == 0)
        {
            _log?.Info("Application exiting.");
            StopModules();
            StopAudio();
            try { _tray?.Dispose(); } catch { /* ignore */ }
            try { _single?.Dispose(); } catch { /* ignore */ }
        }
        base.OnExit(e);
    }
}
