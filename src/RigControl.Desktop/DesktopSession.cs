using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Hosting;

namespace RigControl.Desktop;

/// <summary>The few things the session asks of the real window. Each may be called from any thread.</summary>
internal interface IAppWindow
{
    /// <summary>Hands one message to the page. May block until the window's own thread has taken it.</summary>
    void Send(string json);

    /// <summary>Opens another address in the window (only RigControl's own page is ever passed).</summary>
    void Navigate(Uri address);

    /// <summary>Asks the window to close. Returns at once. Before the window exists this does nothing.</summary>
    void Close();
}

/// <summary>What the Linux/macOS app remembers for itself, in settings.json under "desktop".</summary>
internal sealed class DesktopSettings
{
    public const string Section = "desktop";

    /// <summary>
    /// "classic" when the page only connected as a plain script last time, so that way is tried first; otherwise
    /// not set. Any other value (such as "loopback" from an early test build, when a small local server still
    /// existed) is ignored and taken out.
    /// </summary>
    public string? UiMode { get; set; }
}

/// <summary>
/// Everything of the Linux/macOS app except the window itself: the modules, the "app" module, the message bridge,
/// the page files, and the checks that the page really connected. The window calls in
/// (<see cref="WindowCreated"/>, <see cref="FromPage"/>, <see cref="WindowClosing"/>) and is called through
/// <see cref="IAppWindow"/>, so all of this runs in tests without a window.
/// </summary>
internal sealed class DesktopSession : IHostShell, IAsyncDisposable
{
    /// <summary>How long the page gets to connect before the next way of loading it is tried.</summary>
    public static readonly TimeSpan PageWait = TimeSpan.FromSeconds(7);

    /// <summary>
    /// How long one way may take in all when the window has already taken the page and its script: it is loading,
    /// only slowly (a cold start at sign-in, a busy disk), so it is not swapped for the next way at once.
    /// </summary>
    public static readonly TimeSpan PageSlowLimit = TimeSpan.FromSeconds(25);

    /// <summary>
    /// RigControl's own page says "still here" (with this run's key) every <see cref="PageAliveEvery"/>. When nothing
    /// keyed has arrived for this long, the page in the window may no longer be RigControl's (the window can be made to
    /// load another page, and cannot tell), so nothing more is sent to it until RigControl's page speaks again.
    /// </summary>
    public static readonly TimeSpan PageQuietLimit = TimeSpan.FromSeconds(6);

    /// <summary>How often RigControl's own page says it is still there (bridge.ts uses the same 2 seconds).</summary>
    public static readonly TimeSpan PageAliveEvery = TimeSpan.FromSeconds(2);

    /// <summary>The note RigControl's own page sends to say it is still there. It goes no further than this class.</summary>
    internal const string AliveNote = "{\"note\":\"alive\"}";

    /// <summary>How long the smoke check waits in all: long enough for a slow first way and then the second.</summary>
    public static readonly TimeSpan SmokeLimit = TimeSpan.FromSeconds(60);

    private readonly HostRuntime _runtime;
    private readonly StartOptions _options;
    private readonly IAppWindow _window;
    private readonly ILog _log;
    private readonly MessagePump _pump;
    private readonly UiBridge _bridge;
    private readonly PageLoader _loader;
    private readonly PageKey _key = new();
    private readonly object _exitGate = new();
    private bool _windowCreated; // under _exitGate
    private bool _exitAsked;     // under _exitGate
    private int _strangerWarned;
    private readonly Func<long> _nowMs;
    private readonly long _quietLimitMs;
    private long _lastKeyedMs = long.MinValue / 2;
    private int _quietNoted;
    private int _disposed;

    /// <param name="runtime">Log, settings, dispatcher and module host, already made.</param>
    /// <param name="options">What the command line asked for.</param>
    /// <param name="window">The window (or a stand-in in tests).</param>
    /// <param name="makers">The modules this app really has. Left out in a smoke check, so no device and no Discord is touched.</param>
    /// <param name="pageWait">For tests: a shorter wait before the next way of loading the page is tried.</param>
    /// <param name="pageSlowLimit">For tests: a shorter limit for a page that is loading slowly.</param>
    public DesktopSession(HostRuntime runtime, StartOptions options, IAppWindow window,
        IReadOnlyList<KeyValuePair<string, Func<IAppModule>>> makers, TimeSpan? pageWait = null, TimeSpan? pageSlowLimit = null,
        Func<long>? clockMs = null, TimeSpan? pageQuietLimit = null)
    {
        _nowMs = clockMs ?? (() => Environment.TickCount64);
        _quietLimitMs = (long)(pageQuietLimit ?? PageQuietLimit).TotalMilliseconds;
        _runtime = runtime;
        _options = options;
        _window = window;
        var services = runtime.Services;
        _log = services.Log.For("window");
        Site = new UiSite(pageKey: _key.Value);
        Probe = new SmokeProbe();

        var features = new List<string>();
        if (SafeAutostartSupported()) features.Add(HostCapabilities.Autostart);
        Features = features;

        var modules = ModuleCatalog.Build(services.Platform.Platform,
            options.Smoke ? Array.Empty<KeyValuePair<string, Func<IAppModule>>>() : makers);
        runtime.AddModules(modules);
        services.Dispatcher.Register(new HostAppModule(services, this));

        _pump = new MessagePump(ToWindow, _log, Probe.ToPage);
        _bridge = new UiBridge(services.Dispatcher, services.Events, services.Log.For("bridge"), _pump.Post);

        UiMode? remembered = options.Smoke ? null
            : StartOptions.ParseMode(services.Settings.Get<DesktopSettings>(DesktopSettings.Section).UiMode);
        _loader = new PageLoader(PageLoader.Order(options.Ui, remembered), pageWait ?? PageWait, Load, _log, Remember, GaveUp,
            pageSlowLimit ?? PageSlowLimit);
    }

    public UiSite Site { get; }
    public SmokeProbe Probe { get; }

    /// <summary>What RigControl's own page puts in front of every message (see <see cref="PageKey"/>). Never logged.</summary>
    internal string PageKeyValue => _key.Value;

    /// <summary>0 unless a smoke check failed or the page never connected.</summary>
    public int ExitCode { get; private set; }

    /// <summary>True when every way of loading the page was tried and none connected, so the window was closed.</summary>
    public bool PageNeverConnected { get; private set; }

    // ----- IHostShell -----

    public IReadOnlyCollection<string> Features { get; }

    public void RequestExit()
    {
        bool created;
        lock (_exitGate)
        {
            if (_exitAsked) return;
            _exitAsked = true;
            created = _windowCreated;
        }
        // Asked before the window exists (Ctrl+C during start-up): there is nothing to close yet, so
        // WindowCreated closes it the moment it is there. The request is never lost.
        _log.Info(created ? "Closing RigControl." : "Closing RigControl as soon as its window is there.");
        CloseWindowSoon();
    }

    /// <summary>Off this thread: the caller may be the page's own "quit" call, whose answer is still on its way.</summary>
    private void CloseWindowSoon() => _ = Task.Run(() =>
    {
        try { _window.Close(); }
        catch (Exception ex) { _log.Warn("The window could not be closed.", ex); }
    });

    private bool SafeAutostartSupported()
    {
        try { return _runtime.Services.Platform.Autostart.Supported; }
        catch { return false; }
    }

    // ----- the page -----

    /// <summary>The address the window opens first.</summary>
    public Uri StartAddress => AddressFor(_loader.First);

    private static Uri AddressFor(UiMode mode) => UiSite.SchemeAddress(classic: mode == UiMode.Classic);

    private void Load(UiMode mode) => _window.Navigate(AddressFor(mode));

    /// <summary>
    /// Answers the window's request for one of the page's files (app://rigcontrol/...), from memory. Which files
    /// were asked for tells a page that is loading slowly from one the web view refused (see <see cref="PageLoader"/>).
    /// </summary>
    public UiResponse? ServeFile(string? url)
    {
        var file = Site.ForSchemeUrl(url);
        if (file is not null && file.Kind != UiFileKind.Other)
            _loader.Served(file.Classic ? UiMode.Classic : UiMode.Scheme, file.Kind);
        return file;
    }

    private void Remember(UiMode mode)
    {
        if (_options.Smoke || _options.Ui is not null) return;
        var classic = StartOptions.Name(UiMode.Classic);
        var stored = _runtime.Services.Settings.Get<DesktopSettings>(DesktopSettings.Section).UiMode;
        // Only "classic" is worth a note: it saves the wait for the first try. The usual way needs none.
        // Anything else left in the settings (such as "loopback" from an early test build) is taken out here.
        var keep = mode == UiMode.Classic ? classic : null;
        if (stored == keep) return;
        _runtime.Services.Settings.Update<DesktopSettings>(DesktopSettings.Section, s => s.UiMode = keep);
    }

    /// <summary>
    /// No way of loading the page connected. The window is closed: whatever it shows is not RigControl's page,
    /// and an empty web view left open is a place another page could be dropped into.
    /// </summary>
    private void GaveUp()
    {
        // A smoke check ends by its own clock and says how far it got.
        if (_options.Smoke) return;
        PageNeverConnected = true;
        ExitCode = 1;
        _log.Error("RigControl is closing because its window never showed the interface.");
        RequestExit();
    }

    // ----- called by the window -----

    /// <summary>Starts the modules in the background. Call once, before the window's loop starts.</summary>
    public void StartModules() => _ = Task.Run(() => _runtime.StartModulesAsync());

    public void WindowCreated()
    {
        bool close;
        lock (_exitGate)
        {
            _windowCreated = true;
            close = _exitAsked;
        }
        if (close)
        {
            // Closing was asked for while the window was still being made. Do it now; no page is waited for.
            CloseWindowSoon();
            return;
        }
        _loader.Begin();
    }

    /// <summary>
    /// A message from whatever page the window is showing. The window cannot say which page that is, so only a
    /// message that starts with this run's <see cref="PageKey"/> is read. Anything else is dropped here, before it
    /// can count as "the page connected" and before any module sees it.
    /// </summary>
    public void FromPage(string? message)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(message)) return;
        if (!_key.TryOpen(message, out var json))
        {
            if (Interlocked.Exchange(ref _strangerWarned, 1) == 0)
                _log.Warn("A message that did not come from RigControl's own page was ignored. Later ones are ignored without a note.");
            return;
        }
        Volatile.Write(ref _lastKeyedMs, _nowMs());
        Interlocked.Exchange(ref _quietNoted, 0);
        _pump.Open();
        _loader.PageSpoke();
        if (json == AliveNote) return;
        // A note for the smoke check ("the answer arrived") is not a call and goes no further.
        if (Probe.FromPage(json)) return;
        _ = _bridge.OnMessageAsync(json);
    }

    /// <summary>The window is going away: nothing more is sent to it.</summary>
    public void WindowClosing() => _pump.Close();

    /// <summary>
    /// Hands one message to the window, but only while RigControl's own page has spoken recently (see
    /// <see cref="PageQuietLimit"/>). Messages meant for a page that has gone quiet are dropped, not kept: events are
    /// sent again as things change, and a call is only answered right after the page asked.
    /// </summary>
    private void ToWindow(string json)
    {
        if (_nowMs() - Volatile.Read(ref _lastKeyedMs) > _quietLimitMs)
        {
            if (Interlocked.Exchange(ref _quietNoted, 1) == 0)
                _log.Info("RigControl's page has gone quiet; nothing is sent to the window until it speaks again.");
            return;
        }
        _window.Send(json);
    }

    // ----- smoke check -----

    /// <summary>
    /// Waits for the page to ask for app.getInfo and to say that the answer arrived, then closes the window.
    /// Sets <see cref="ExitCode"/> to 1 and logs why when that does not happen within <paramref name="limit"/>.
    /// </summary>
    public async Task<bool> RunSmokeCheckAsync(TimeSpan limit)
    {
        var passed = await Task.WhenAny(Probe.Passed, Task.Delay(limit, _runtime.Lifetime)).ConfigureAwait(false) == Probe.Passed;
        if (passed)
        {
            var how = _loader.Current is { } m ? StartOptions.Name(m) : "unknown";
            _log.Info($"Smoke check passed: the page loaded ({how}), asked for app.getInfo and said the answer arrived.");
        }
        else
        {
            ExitCode = 1;
            bool created;
            lock (_exitGate) created = _windowCreated;
            _log.Error($"Smoke check failed after {limit.TotalSeconds:0} s. {Probe.WhereItStopped(created)}");
        }
        RequestExit();
        return passed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _loader.Dispose();
        _pump.Close();
        _bridge.Dispose();
        // Modules last and in full: this is where the lights are handed back.
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
