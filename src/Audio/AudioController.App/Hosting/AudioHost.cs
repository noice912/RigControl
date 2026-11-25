using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioController.App.ViewModels;
using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Decode;
using AudioController.Ffmpeg;
using AudioController.Storage;

namespace AudioController.App.Hosting;

/// <summary>What <see cref="AudioHost.Start(Dispatcher)"/> ended up with.</summary>
public enum AudioHostState
{
    /// <summary>The audio engine runs and <see cref="AudioHost.View"/> is the Audio Controller panel.</summary>
    Running,

    /// <summary>The standalone Audio Controller (or another RigControl) holds the single-instance lock.</summary>
    AlreadyRunningElsewhere,

    /// <summary>The encrypted library is tampered/undecryptable; nothing was loaded and nothing was written.</summary>
    VaultUnreadable,

    /// <summary>Anything else that stopped the start (see <see cref="AudioHost.Message"/>).</summary>
    Failed,
}

/// <summary>
/// RigControl's only entry point into Audio Controller: does what the standalone <c>App</c> class did (single-instance
/// lock, encrypted vault, FFmpeg, devices, engine, routers, view model) but hands back a view instead of showing a window.
/// </summary>
/// <remarks>
/// Host contract: merge <see cref="ResourcesUri"/> into <c>Application.Resources</c> before any view is created (Start
/// also merges it when it is missing); call <see cref="Start(Dispatcher)"/> on the UI thread; put <see cref="View"/> in
/// the window; call <see cref="AttachToWindow"/> once that window has a handle (SourceInitialized); forward
/// <see cref="OnSessionEnding"/> and <see cref="EmergencyRestore"/>; <see cref="Dispose"/> on exit.
/// </remarks>
public sealed class AudioHost : IDisposable
{
    /// <summary>
    /// The folder holding the bundled ffmpeg.exe (unpacked on first use), or null if it could not be unpacked.
    /// Used by RigControl's screen recorder, which shares this copy.
    /// </summary>
    public static string? ExtractFfmpegDirectory() => ExtractFfmpegDirectory(FfmpegRoot);

    /// <summary>The same, into another folder (the installer unpacks it into the install folder).</summary>
    public static string? ExtractFfmpegDirectory(string root) => FfmpegExtractor.TryExtract(AudioHostOptions.EmbeddedFfmpegFiles(), root);

    /// <summary>
    /// Where FFmpeg is unpacked. %LOCALAPPDATA%\RigControl\ffmpeg unless the host says otherwise: an installed
    /// RigControl uses its own folder under Program Files, which only administrators can change. Set before Start.
    /// </summary>
    public static string FfmpegRoot { get; set; } = FfmpegExtractor.DefaultRoot;

    /// <summary>The same name as the standalone app, so the two never run the audio engine at the same time.</summary>
    internal const string SingleInstanceMutexName = @"Local\AudioController.SingleInstance.v1";

    private const string AppTitle = "Audio Controller";

    private readonly Dispatcher? _dispatcher;
    private readonly TimeSpan _uiShutdownWait;
    private Mutex? _singleInstance;
    private IVault? _vault;
    private MainViewModel? _viewModel;
    private IDeviceManager? _devices;
    private IAudioEngine? _engine;
    private SystemEqRouter? _systemEq;
    private MirrorRouter? _mirror;
    private FrameworkElement? _view;
    private bool _attached;
    private int _disposed;

    private AudioHost(Dispatcher? dispatcher, TimeSpan uiShutdownWait)
    {
        _dispatcher = dispatcher;
        _uiShutdownWait = uiShutdownWait;
    }

    /// <summary><c>pack://application:,,,/AudioController;component/Themes/AudioResources.xaml</c>: the dark theme and the
    /// converters every audio view and dialog looks up (what the standalone App.xaml provided).</summary>
    public static Uri ResourcesUri { get; } = CreateResourcesUri();

    public AudioHostState State { get; private set; } = AudioHostState.Failed;

    /// <summary>User-facing explanation when <see cref="State"/> is not <see cref="AudioHostState.Running"/>; otherwise null.</summary>
    public string? Message { get; private set; }

    /// <summary>
    /// The Audio Controller panel (<see cref="AudioView"/>) when running; otherwise a simple themed panel showing
    /// <see cref="Message"/>. Created on the UI thread; read it there.
    /// </summary>
    public FrameworkElement View => _view ??= CreateMessageView(State, Message);

    /// <summary>The view model while running (tests and diagnostics).</summary>
    internal MainViewModel? ViewModel => _viewModel;

    /// <summary>
    /// Starts Audio Controller inside the host. Must be called on the UI thread that owns <paramref name="uiDispatcher"/>.
    /// Never throws: every problem ends up in <see cref="State"/> and <see cref="Message"/>.
    /// </summary>
    public static AudioHost Start(Dispatcher uiDispatcher) => Start(uiDispatcher, AudioHostOptions.Default);

    internal static AudioHost Start(Dispatcher? uiDispatcher, AudioHostOptions options)
    {
        var host = new AudioHost(uiDispatcher, options.UiShutdownWait);
        try
        {
            host.StartCore(uiDispatcher, options);
        }
        catch (Exception ex)
        {
            host.Fail(AudioHostState.Failed, "Audio Controller could not start.\n\n" + ex.Message);
        }

        return host;
    }

    private void StartCore(Dispatcher? uiDispatcher, AudioHostOptions options)
    {
        if (uiDispatcher is null || !uiDispatcher.CheckAccess())
        {
            Fail(AudioHostState.Failed,
                "Audio Controller could not start.\n\nIt must be started on the window's UI thread (AudioHost.Start).");
            return;
        }

        EnsureResources(uiDispatcher);

        // One instance only: two copies would fight over the same hotkeys and the same library.dat. The standalone app
        // checks whether this named mutex already exists, so holding an (unowned) handle is enough; it has no thread
        // affinity, which lets Dispose release it from any thread.
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, options.MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, but belongs to a process we may not open (for example an elevated standalone copy).
            Fail(AudioHostState.AlreadyRunningElsewhere, AlreadyRunningMessage);
            return;
        }

        if (!createdNew)
        {
            mutex.Dispose();
            Fail(AudioHostState.AlreadyRunningElsewhere, AlreadyRunningMessage);
            return;
        }

        _singleInstance = mutex;

        // ---- encrypted vault (fatal when undecryptable: never load partial data)
        IVault vault;
        Library library;
        try
        {
            vault = options.OpenVault();
            _vault = vault;
            library = vault.LoadLibrary();
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException)
        {
            Fail(AudioHostState.VaultUnreadable,
                "Your saved Audio Controller data is tampered/undecryptable, so it was not loaded.\n\n" +
                "Either a file in the encrypted library was modified, or the encryption key (protected by your " +
                "Windows account) cannot unlock it. No partial data was loaded and nothing was changed on disk.\n\n" +
                $"Location: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioController")}\n\n" +
                $"Details: {ex.Message}");
            return;
        }

        // ---- decoder (missing FFmpeg is not fatal: import is disabled, playback still works). Same place in the order
        // as the standalone app. The first run extracts the embedded FFmpeg (~200 MB) and later runs re-verify its
        // SHA-256 (see FfmpegExtractor), so this step takes a moment longer than the standalone app's file lookup.
        IAudioDecoder? decoder = null;
        try
        {
            decoder = options.CreateDecoder();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("FFmpeg unusable: " + ex.Message);
        }

        // ---- audio
        _devices = options.CreateDevices();
        _engine = options.CreateEngine();
        _systemEq = options.CreateSystemEq();
        _mirror = options.CreateMirror();

        _viewModel = new MainViewModel(vault, library, decoder, _devices, _engine, _systemEq, uiDispatcher,
            detectEqualizerApo: options.DetectEqualizerApo, mirror: _mirror);
        _view = options.CreateView(_viewModel);
        State = AudioHostState.Running;
        Message = null;
        _viewModel.Start();
    }

    private const string AlreadyRunningMessage =
        "Audio Controller is already running on its own, so the Audio section is turned off here. Two copies would " +
        "fight over the same hotkeys and the same library.\n\n" +
        "Close the standalone Audio Controller (look for its window or taskbar button), then restart RigControl.";

    /// <summary>
    /// Wires the host window once it has a handle (call from SourceInitialized): the global hotkeys are registered on
    /// its HWND, its Activated event refreshes the device list (as the standalone window did), and it owns the audio
    /// dialogs when the Audio view is not in the visual tree. Only the first call counts; does nothing unless running.
    /// </summary>
    public void AttachToWindow(Window host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => AttachToWindow(host));
            return;
        }

        if (_attached || Volatile.Read(ref _disposed) != 0) return;
        _attached = true;
        (_view as AudioView)?.AttachToWindow(host);
    }

    /// <summary>
    /// Windows is logging off / shutting down: persist now (the host may not get to Dispose), and put the default output
    /// back on the headset so the next session does not start playing into the inlet. Not final: another program can
    /// still cancel the logoff, and then the app must keep switching normally (Dispose does the final one).
    /// </summary>
    public void OnSessionEnding()
    {
        try
        {
            _viewModel?.SaveNow();
            _viewModel?.RestoreWindowsDefaultForExit(final: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Best effort before the process dies (AppDomain.UnhandledException): never leave Windows playing into the inlet.
    /// Any thread; no UI; never throws.
    /// </summary>
    public void EmergencyRestore()
    {
        try
        {
            _viewModel?.RestoreWindowsDefaultForExit();
        }
        catch (Exception)
        {
            // Nothing more we can do.
        }
    }

    /// <summary>
    /// What the standalone app did on exit, in the same order: the view model's Shutdown (final library save, Windows
    /// default output back to the headset, hotkeys/engine/routers/devices released), then the vault (wipes its
    /// in-memory key), then the single-instance lock. Runs on the UI thread when that is still alive (the hotkeys belong
    /// to it), otherwise inline. Idempotent; never throws.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_dispatcher is not null && !_dispatcher.CheckAccess() && !_dispatcher.HasShutdownStarted)
        {
            try
            {
                // Bounded: if the UI thread never gets to it (it is blocked, or its message loop already ended without
                // a dispatcher shutdown) the wait is given up and the clean-up below runs here instead of hanging exit.
                _dispatcher.Invoke(CleanUpAll, DispatcherPriority.Send, CancellationToken.None, _uiShutdownWait);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Audio shutdown on the UI thread failed: " + ex.Message);
            }
        }

        // Inline when already on the UI thread, when it is gone, or to finish whatever the UI thread could not.
        CleanUpAll();
    }

    /// <summary>Every step runs at most once, so this can be repeated (failure path, Dispose, a retry after a failed Invoke).</summary>
    private void CleanUpAll()
    {
        try
        {
            (_view as AudioView)?.Detach();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        CleanUp();

        // Only after that last save: wipe the vault's in-memory master key.
        try
        {
            (Interlocked.Exchange(ref _vault, null) as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        // Unowned handle (see StartCore): closing it releases the lock from any thread.
        try
        {
            Interlocked.Exchange(ref _singleInstance, null)?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void CleanUp()
    {
        try
        {
            if (_viewModel is not null) _viewModel.Shutdown(); // idempotent
            else
            {
                Interlocked.Exchange(ref _engine, null)?.Dispose();
                Interlocked.Exchange(ref _systemEq, null)?.Dispose();
                Interlocked.Exchange(ref _mirror, null)?.Dispose();
                Interlocked.Exchange(ref _devices, null)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Cleanup failed: " + ex);
        }
    }

    /// <summary>Records why the start stopped and releases everything acquired so far, as the standalone app's exit did.</summary>
    private void Fail(AudioHostState state, string message)
    {
        State = state;
        Message = message;
        _view = null; // the message panel is built on first access to View
        CleanUpAll();
    }

    // ------------------------------------------------------------------ resources

    private static Uri CreateResourcesUri()
    {
        // Touching PackUriHelper registers the pack:// scheme, so the Uri parses even before any WPF type was used.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        return new Uri("pack://application:,,,/AudioController;component/Themes/AudioResources.xaml", UriKind.Absolute);
    }

    /// <summary>Safety net: merges <see cref="ResourcesUri"/> into the application's resources when the host did not.</summary>
    private static void EnsureResources(Dispatcher uiDispatcher)
    {
        if (Application.Current is not { } app || app.Dispatcher != uiDispatcher) return;
        if (ContainsAudioResources(app.Resources)) return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ResourcesUri });
    }

    private static bool ContainsAudioResources(ResourceDictionary dictionary)
    {
        if (dictionary.Source is { } source && IsAudioResources(source)) return true;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (ContainsAudioResources(merged)) return true;
        }

        return false;
    }

    private static bool IsAudioResources(Uri source) =>
        source.OriginalString.EndsWith("AudioController;component/Themes/AudioResources.xaml", StringComparison.OrdinalIgnoreCase);

    private static FrameworkElement CreateMessageView(AudioHostState state, string? message)
    {
        var title = new TextBlock
        {
            Text = state switch
            {
                AudioHostState.AlreadyRunningElsewhere => "Audio Controller is running on its own",
                AudioHostState.VaultUnreadable => "Your saved audio data could not be opened",
                AudioHostState.Running => "Audio Controller",
                _ => "Audio Controller could not start",
            },
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var body = new TextBlock
        {
            Text = message ?? "Audio Controller has stopped.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var panel = new StackPanel
        {
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(title);
        panel.Children.Add(body);

        var root = new Border
        {
            Padding = new Thickness(40),
            Child = panel,
            Background = Brushes.Transparent,
        };
        root.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        System.Windows.Documents.TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
        System.Windows.Automation.AutomationProperties.SetName(root, AppTitle);
        return root;
    }
}

/// <summary>The services <see cref="AudioHost"/> builds; the defaults are the standalone app's. Tests swap in fakes.</summary>
internal sealed class AudioHostOptions
{
    public static AudioHostOptions Default => new();

    public string MutexName { get; init; } = AudioHost.SingleInstanceMutexName;

    public Func<IVault> OpenVault { get; init; } = Vault.OpenDefault;

    public Func<IAudioDecoder?> CreateDecoder { get; init; } = CreateDefaultDecoder;

    public Func<IDeviceManager> CreateDevices { get; init; } = () => new DeviceManager();

    public Func<IAudioEngine> CreateEngine { get; init; } = () => new AudioEngine();

    public Func<SystemEqRouter> CreateSystemEq { get; init; } = () => new SystemEqRouter();

    public Func<MirrorRouter> CreateMirror { get; init; } = () => new MirrorRouter();

    /// <summary>Null: <see cref="EqualizerApoDetector.Detect"/> (the view model's default).</summary>
    public Func<EqualizerApoState>? DetectEqualizerApo { get; init; }

    public Func<MainViewModel, FrameworkElement> CreateView { get; init; } = vm => new AudioView(vm);

    /// <summary>How long a Dispose from another thread waits for the UI thread to start the shutdown before doing it itself.</summary>
    public TimeSpan UiShutdownWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The embedded FFmpeg (extracted to <see cref="AudioHost.FfmpegRoot"/> on first use) is tried first, then every
    /// location the standalone app searched; no FFmpeg anywhere disables import exactly as before.
    /// </summary>
    private static IAudioDecoder? CreateDefaultDecoder()
    {
        FfmpegLocator.SetEmbeddedDirectoryProvider(() =>
            FfmpegExtractor.TryExtract(EmbeddedFfmpegFiles(), AudioHost.FfmpegRoot));

        string? ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null) return null;

        try
        {
            return new FfmpegDecoder(ffmpeg);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("FFmpeg unusable: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The embedded FFmpeg files: names and SHA-256s from this assembly's own metadata (FfmpegPayload.targets), bytes
    /// from AudioController.Ffmpeg. That ~200 MB assembly is only loaded when a file has to be written (first run, a
    /// new FFmpeg build, a damaged copy); on a normal start the copy on disk is verified against the hashes alone.
    /// </summary>
    internal static IReadOnlyList<EmbeddedFile> EmbeddedFfmpegFiles() =>
        FfmpegExtractor.FromManifest(typeof(AudioHost).Assembly, LoadFfmpegPayloadAssembly);

    // Its own method, never inlined: the JIT must not resolve (and so load) AudioController.Ffmpeg before it is called.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadFfmpegPayloadAssembly() => EmbeddedFfmpeg.Assembly;
}
