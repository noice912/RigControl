using RigControl.Core.Logging;

namespace RigControl.Desktop;

/// <summary>
/// Makes sure the page connects after the window opened it. The page counts as connected when its first message
/// arrives. When that does not happen in time, the next way of handing over the page is tried
/// (see <see cref="UiMode"/>). The host is told which way worked, and told when none did.
/// The window library does not say whether a page loaded, so two signs are used: the clock, and which of the page's
/// files the window asked for (<see cref="Served"/>). A page whose index.html and script the window has already
/// taken is loading, only slowly, so it gets more time (up to <c>slowLimit</c> in all) before the next way is
/// tried. A page whose files were never asked for is treated as refused after the first wait.
/// </summary>
internal sealed class PageLoader : IDisposable
{
    /// <summary>The order when nothing was asked for and nothing is remembered.</summary>
    public static readonly IReadOnlyList<UiMode> DefaultOrder = new[] { UiMode.Scheme, UiMode.Classic };

    private readonly object _gate = new();
    private readonly IReadOnlyList<UiMode> _order;
    private readonly TimeSpan _wait;
    private readonly TimeSpan _slowLimit;
    private readonly Action<UiMode> _load;
    private readonly Action<UiMode>? _connected;
    private readonly Action? _gaveUp;
    private readonly ILog _log;
    private Timer? _timer;
    private int _index = -1;
    private bool _done;
    private DateTime _wayStarted;
    private bool _pageServed;   // the current way's index.html was handed to the window
    private bool _scriptServed; // and its script
    private bool _slowNoted;

    /// <param name="order">The ways to try, first to last.</param>
    /// <param name="wait">How long the page gets to connect before the next way is tried.</param>
    /// <param name="load">Opens the page that way. Called from a timer thread, never for the first way.</param>
    /// <param name="log">Where each try is written.</param>
    /// <param name="connected">Told once, with the way that worked.</param>
    /// <param name="gaveUp">Told once, from a timer thread, when the last way did not connect either.</param>
    /// <param name="slowLimit">
    /// How long in all one way may take when the window has already taken its page and script.
    /// Left out: the same as <paramref name="wait"/> (no extra time).
    /// </param>
    public PageLoader(IReadOnlyList<UiMode> order, TimeSpan wait, Action<UiMode> load, ILog log,
        Action<UiMode>? connected = null, Action? gaveUp = null, TimeSpan? slowLimit = null)
    {
        if (order.Count == 0) throw new ArgumentException("At least one way to load the page is needed.", nameof(order));
        _order = order;
        _wait = wait;
        _slowLimit = slowLimit is { } s && s > wait ? s : wait;
        _load = load;
        _connected = connected;
        _gaveUp = gaveUp;
        _log = log;
    }

    /// <summary>
    /// The order for this start: only what was asked for on the command line; otherwise the remembered way first,
    /// then the rest in the usual order.
    /// </summary>
    public static IReadOnlyList<UiMode> Order(UiMode? asked, UiMode? remembered)
    {
        if (asked is { } only) return new[] { only };
        if (remembered is not { } first) return DefaultOrder;
        return new[] { first }.Concat(DefaultOrder.Where(m => m != first)).ToArray();
    }

    /// <summary>The way the host gives the window as its start page.</summary>
    public UiMode First => _order[0];

    /// <summary>The way in use now, or null before <see cref="Begin"/>.</summary>
    public UiMode? Current
    {
        get { lock (_gate) return _index >= 0 && _index < _order.Count ? _order[_index] : null; }
    }

    public bool Connected { get; private set; }

    /// <summary>
    /// Starts the clock for the first way. The host has already given the window that page as its start page,
    /// so nothing is loaded here.
    /// </summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (_index >= 0 || _done) return;
            _index = 0;
            StartWay();
            _timer = new Timer(_ => OnTimeout(), null, _wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Under _gate: a new way starts now, with nothing of it handed over yet.</summary>
    private void StartWay()
    {
        _wayStarted = DateTime.UtcNow;
        _pageServed = _scriptServed = _slowNoted = false;
    }

    /// <summary>
    /// The window took one of the page's files, asked for the given way. Files for a way that is no longer the
    /// current one (a late request from the page being replaced) do not count.
    /// </summary>
    public void Served(UiMode mode, UiFileKind kind)
    {
        lock (_gate)
        {
            if (_done || _index < 0 || _order[_index] != mode) return;
            if (kind == UiFileKind.Page) _pageServed = true;
            else if (kind == UiFileKind.Script) _scriptServed = true;
        }
    }

    /// <summary>The page sent a message. The first one settles which way works.</summary>
    public void PageSpoke()
    {
        UiMode mode;
        lock (_gate)
        {
            if (_done) return;
            _done = true;
            Connected = true;
            _timer?.Dispose();
            _timer = null;
            mode = _order[Math.Clamp(_index, 0, _order.Count - 1)];
        }
        _log.Info($"The page connected ({StartOptions.Name(mode)}).");
        try { _connected?.Invoke(mode); }
        catch (Exception ex) { _log.Warn("Remembering how the page was loaded failed.", ex); }
    }

    private void OnTimeout()
    {
        UiMode failed, next = default;
        var last = false;
        var slow = false;
        TimeSpan more = default;
        lock (_gate)
        {
            if (_done || _index < 0) return;
            failed = _order[_index];
            var used = DateTime.UtcNow - _wayStarted;
            if (_pageServed && _scriptServed && used < _slowLimit)
            {
                // Loading, only slowly: the window has the page and its script. Moving on now would throw that away.
                more = _slowLimit - used < _wait ? _slowLimit - used : _wait;
                slow = !_slowNoted;
                _slowNoted = true;
                _timer?.Change(more, Timeout.InfiniteTimeSpan);
            }
            else if (_index + 1 >= _order.Count)
            {
                last = true;
                _done = true;
                _timer?.Dispose();
                _timer = null;
            }
            else
            {
                _index++;
                next = _order[_index];
                StartWay();
                _timer?.Change(_wait, Timeout.InfiniteTimeSpan);
            }
        }
        if (more != default)
        {
            if (slow)
                _log.Info($"The page is loading slowly ({StartOptions.Name(failed)}): the window has its files. " +
                          $"Waiting up to {_slowLimit.TotalSeconds:0.#} s in all before anything else is tried.");
            return;
        }
        if (last)
        {
            _log.Error($"RigControl's window did not connect ({StartOptions.Name(failed)} was the last way tried). " +
                       "The log above says what was tried.");
            try { _gaveUp?.Invoke(); }
            catch (Exception ex) { _log.Warn("Closing after the page did not connect failed.", ex); }
            return;
        }
        _log.Warn($"The page did not connect ({StartOptions.Name(failed)}). Trying {StartOptions.Name(next)}.");
        try { _load(next); }
        catch (Exception ex) { _log.Error($"Opening the page ({StartOptions.Name(next)}) failed.", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _done = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
