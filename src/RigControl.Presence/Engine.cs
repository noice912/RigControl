using System.Net.Http;
using System.Text.Json.Nodes;
using RigControl.Core.Logging;

namespace RigControl.Presence;

/// <summary>A running process, as far as the engine needs one.</summary>
public sealed record RunningProcess(string Name, DateTime? StartUtc);

/// <summary>Everything the engine reads from the PC. Abstracted so tests can drive it.</summary>
public interface IPresenceInputs
{
    IReadOnlyList<RunningProcess> Processes();
    Task<MusicNow?> MusicAsync(bool appleMusic, bool spotify);
    ForegroundApp? Foreground(IReadOnlySet<string> gameExes);
    Task<IReadOnlyList<MediaSession>> SessionsAsync();
    IReadOnlyList<string> BrowserTitles();
}

#if WINDOWS
public sealed class PcInputs : IPresenceInputs
{
    /// <summary>Only this Windows session's processes: another signed-in user's game must never show on this user's card.</summary>
    public IReadOnlyList<RunningProcess> Processes() => WinSession.Processes();

    public Task<MusicNow?> MusicAsync(bool appleMusic, bool spotify) => MusicSource.GetAsync(appleMusic, spotify);
    public ForegroundApp? Foreground(IReadOnlySet<string> gameExes) => ForegroundSource.Get(gameExes);
    public Task<IReadOnlyList<MediaSession>> SessionsAsync() => Watching.SessionsAsync();
    public IReadOnlyList<string> BrowserTitles() => Watching.BrowserTitles();
}
#endif

/// <summary>What the engine is showing right now (for the Games page and, later, the overlay).</summary>
public sealed record PresenceNow(string Status, string? Playing, IReadOnlyList<string> RunningIds, MusicNow? Music, string? LyricLine, string? App,
    string? Watching = null, int CustomShown = 0, IReadOnlyList<string>? CustomSkipped = null,
    IReadOnlyDictionary<string, string>? CustomErrors = null);

/// <summary>
/// The Discord status engine: a game card per running game, a music card and the current-app card, each on its own
/// Discord application. Ported from RichPresence's engine loop (same rules; updates go out as fast as Discord allows).
///
/// Rules:
///  - The application ids come from <see cref="AppIds"/>, never from this file. A card whose id this build doesn't
///    have is simply not shown: nothing is ever opened or sent with a missing id.
///  - Every card's updates go through its own <see cref="ActivityLimiter"/> (Discord takes 5 per 20 s per connection).
///  - An id is never logged.
/// </summary>
public sealed class PresenceEngine
{
    /// <summary>What the status says when this build has no ids for its own cards and nothing else is showing.</summary>
    public const string NotSetUp = "Discord status isn't set up in this build";
    private const string FallbackImage = "https://upload.wikimedia.org/wikipedia/commons/thumb/5/5f/Apple_Music_icon.svg/240px-Apple_Music_icon.svg.png";
    private const string GenericAppIcon = "https://upload.wikimedia.org/wikipedia/commons/thumb/8/87/Windows_logo_-_2021.svg/240px-Windows_logo_-_2021.svg.png";

    private readonly IDiscordClient _discord;
    private readonly IPresenceInputs _inputs;
    private readonly ILog _log;
    private readonly MusicExtras _extras;
    private readonly GenshinSource _genshin;
    private readonly WatchExtras _watchExtras;
    private readonly Func<string, ExtReport?> _ext;
    private readonly Func<DateTime> _now;
    private readonly Func<AppIds> _ids;
    private int _reopen;

    private sealed class Card
    {
        public required IDiscordConnection Conn;
        public required ActivityLimiter Out;
        public required string Name;
        /// <summary>What the engine last handed to <see cref="Out"/> (sent, or waiting for a free place).</summary>
        public string? Id;
        public DateTime SentUtc = DateTime.MinValue;
        public string? LastError;
        /// <summary>For a custom status: its id, so Discord's answer can be shown next to it.</summary>
        public string? Key;
    }

    private readonly Dictionary<string, Card> _games = new();
    private Card? _music;
    private Card? _app;
    private Card? _watch;
    private readonly Dictionary<string, Card> _custom = new();
    private readonly Dictionary<string, long> _customStart = new();
    /// <summary>Discord's reason for refusing a custom status, by status id (shown next to it).</summary>
    private readonly Dictionary<string, string> _customErrors = new();
    private string _customSkipLog = "";
    private string? _watchKey;
    private long _watchStart;
    private readonly Dictionary<string, (DateTime Until, bool Logged)> _badIds = new();
    private readonly Dictionary<string, long> _firstSeen = new();
    private string? _lastLyric;
    private long _appStartMs;
    private string? _appPendProc;
    private DateTime _appPendSince = DateTime.MinValue;
    private DateTime? _lastValidApp;
    /// <summary>The Discord account the cards are on now (null while nothing is attached).</summary>
    private DiscordUser? _attached;
    /// <summary>The last account ever attached to, kept while Discord is closed so a different one is noticed.</summary>
    private string? _lastUserId;
    private string? _switchNote;
    private DateTime _switchedAt = DateTime.MinValue;

    public PresenceEngine(IDiscordClient discord, IPresenceInputs inputs, ILog log, HttpClient http, Func<DateTime>? now = null,
        Func<string, ExtReport?>? extensionReport = null, Func<AppIds>? ids = null)
    {
        _ids = ids ?? (() => AppIds.BuiltIn);
        _watchExtras = new WatchExtras(http);
        _ext = extensionReport ?? (_ => null);
        _discord = discord;
        _inputs = inputs;
        _log = log;
        _extras = new MusicExtras(http);
        _genshin = new GenshinSource(http);
        _now = now ?? (() => DateTime.UtcNow);
    }

    public PresenceNow Now { get; private set; } = new("Off", null, Array.Empty<string>(), null, null, null);

    private long NowMs => new DateTimeOffset(_now()).ToUnixTimeMilliseconds();

    /// <summary>The Discord account RigControl is attached to, or null.</summary>
    public DiscordUser? Attached => _attached;

    /// <summary>For a few minutes after Discord switched account: a sentence saying so.</summary>
    public string? AttachNote => _switchNote is not null && (_now() - _switchedAt).TotalMinutes < 5 ? _switchNote : null;

    /// <summary>True while at least one card is connected to Discord.</summary>
    public bool AnyConnected =>
        _music?.Conn.Connected == true || _app?.Conn.Connected == true || _watch?.Conn.Connected == true
        || _games.Values.Any(c => c.Conn.Connected) || _custom.Values.Any(c => c.Conn.Connected);

    /// <summary>
    /// Asks Discord who is signed in without showing anything: one handshake, no card. Used while no card is up
    /// (status off, or nothing to show), so the Games page can say which account it would use.
    /// </summary>
    public void Probe()
    {
        if (AnyConnected) return;
        var ids = _ids();
        var id = ids.App ?? ids.Music ?? ids.Game ?? ids.Pool.FirstOrDefault();
        if (id is null) return; // no ids in this build: nothing to ask with
        using var c = _discord.Open(id);
        try
        {
            if (c.Connect()) Seen(c.User);
        }
        catch { /* Discord went away in between */ }
    }

    /// <summary>Discord is gone (closed, or only another Windows user's is left): nothing is attached any more.</summary>
    public void Detach() => _attached = null;

    /// <summary>Notes the account behind a new connection. True when it is a different account than before.</summary>
    private bool Seen(DiscordUser? u)
    {
        if (u is null) return false;
        if (_attached?.Id == u.Id)
        {
            _attached = u;
            return false;
        }
        var switched = _lastUserId is not null && _lastUserId != u.Id;
        _attached = u;
        _lastUserId = u.Id;
        if (switched)
        {
            _switchedAt = _now();
            _switchNote = $"Discord switched to another account. Your status now shows on {u.Name}'s Discord.";
            _log.Info("Discord switched to another account. Cards sent to the old one were cleared.");
        }
        _log.Info($"Attached to {u.Name}'s Discord.");
        return switched;
    }

    /// <summary>After an account switch: every other card was sent to the old account, so it is cleared and reopened.</summary>
    private void DropOthers(Card keep)
    {
        if (!ReferenceEquals(_music, keep)) Drop(ref _music);
        if (ReferenceEquals(_watch, _app))
        {
            if (!ReferenceEquals(_app, keep)) { _watch = null; Drop(ref _app); }
        }
        else
        {
            if (!ReferenceEquals(_app, keep)) Drop(ref _app);
            if (!ReferenceEquals(_watch, keep)) Drop(ref _watch);
        }
        foreach (var k in _games.Keys.ToList())
        {
            var c = _games[k];
            if (ReferenceEquals(c, keep)) continue;
            Close(c);
            _games.Remove(k);
        }
        foreach (var k in _custom.Keys.ToList())
        {
            var c = _custom[k];
            if (ReferenceEquals(c, keep)) continue;
            Close(c);
            _custom.Remove(k);
        }
    }

    /// <summary>The ids changed (the user saved their own): every card is closed and opened again on the next pass.</summary>
    public void Reopen() => Interlocked.Exchange(ref _reopen, 1);

    /// <summary>One pass, every 3 s. Exceptions close every connection and are logged by the caller.</summary>
    public async Task TickAsync(PresenceSettings s, IReadOnlyList<Game> library, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _reopen, 0) == 1) CloseAll();
        var ids = _ids();
        // Which library games are running? Every game counts here, switched on or not; only the ones on get a card.
        var procs = new Dictionary<string, RunningProcess>();
        foreach (var p in _inputs.Processes()) procs.TryAdd(p.Name, p);
        var gameExes = new HashSet<string>(library.SelectMany(g => g.Exes));
        var running = new List<(Game G, RunningProcess P)>();
        var runIds = new List<string>();
        var disabled = new HashSet<string>(s.DisabledGames);
        foreach (var g in library)
        {
            var hit = g.Exes.FirstOrDefault(procs.ContainsKey);
            if (hit is null) continue;
            runIds.Add(g.Id);
            if (!disabled.Contains(g.Id)) running.Add((g, procs[hit]));
        }

        if (!_discord.IsRunning())
        {
            CloseAll(clear: false);
            Detach();
            Now = new PresenceNow("Waiting for Discord", running.FirstOrDefault().G?.Name, runIds, null, null, null);
            return;
        }

        await UpdateGamesAsync(s, ids, running, ct).ConfigureAwait(false);
        var (music, line) = await UpdateMusicAsync(s, ids, ct).ConfigureAwait(false);
        var watching = await UpdateWatchingAsync(s, ids, ct).ConfigureAwait(false);
        string? app = null;
        // While something is being watched the app card stays away (it would just say "Microsoft Edge").
        if (watching is null) app = UpdateApp(s, ids, gameExes);
        else if (_app is not null && !ReferenceEquals(_watch, _app)) Clear(_app);
        var (customShown, skipped) = UpdateCustom(s, ids);
        // Anything that was waiting for a free place and is still the newest state goes out now if it can.
        Flush();

        var parts = new List<string>();
        // Only what can really be on Discord is named: a card this build has no id for is not.
        if (running.Count > 0 && (ids.Game is not null || _games.Count > 0)) parts.Add("Playing " + running[0].G.Name);
        if (music is { Playing: true } && ids.Music is not null) parts.Add("Listening: " + music.Title);
        if (watching is not null) parts.Add("Watching " + watching);
        if (app is not null && running.Count == 0) parts.Add("App: " + app);
        if (customShown > 0) parts.Add(customShown > 1 ? $"{customShown} custom statuses" : "Custom status");
        Now = new PresenceNow(parts.Count > 0 ? string.Join("  |  ", parts) : ids.SetUp ? "Watching for games" : NotSetUp,
            running.FirstOrDefault().G?.Name, runIds, music, line, app, watching, customShown, skipped,
            new Dictionary<string, string>(_customErrors));
    }

    // ---------------------------------------------------------------- connections

    private bool Connect(Card c)
    {
        if (c.Conn.Connected) return true;
        if (_badIds.TryGetValue(c.Conn.ClientId, out var bad) && _now() < bad.Until) return false;
        if (c.Conn.Connect())
        {
            _badIds.Remove(c.Conn.ClientId);
            // A new connection shows nothing yet and Discord counts its updates from zero: the card is sent afresh.
            c.Out.Reset();
            c.Id = null;
            if (Seen(c.Conn.User)) DropOthers(c);
            return true;
        }
        if (c.Conn is DiscordConnection { Refused: { } why })
        {
            // A refused id: say so once and leave it alone for a minute instead of retrying every few seconds.
            if (!_badIds.ContainsKey(c.Conn.ClientId)) _log.Warn($"Discord didn't accept the {c.Name} card's application id ({AppIds.Scrub(why)}). Trying again once a minute.");
            _badIds[c.Conn.ClientId] = (_now().AddSeconds(60), true);
        }
        return false;
    }

    private Card NewCard(string clientId, string name)
    {
        var conn = _discord.Open(clientId);
        return new Card { Conn = conn, Out = new ActivityLimiter(_now, conn.SetActivity), Name = name };
    }

    /// <summary>
    /// Hands the card's newest state to its limiter: sent at once while Discord's limit allows, otherwise kept (only
    /// the newest) and sent the moment it does. A keep-alive repeats what is already shown; a change never is one.
    /// </summary>
    private void Push(Card c, JsonObject activity, string id, bool keepAlive)
    {
        var r = c.Out.Offer(activity, keepAlive);
        if (r == ActivityLimiter.Result.Sent) Answered(c);
        if (r != ActivityLimiter.Result.Same) c.SentUtc = _now();
        c.Id = id;
    }

    /// <summary>Discord answered an update: say so once if it refused, and remember it for a custom status.</summary>
    private void Answered(Card c)
    {
        var err = c.Out.LastError is { } e ? AppIds.Scrub(e) : null;
        if (err is not null && err != c.LastError) _log.Warn($"Discord didn't accept the {c.Name} card: {err}");
        c.LastError = err;
        if (c.Key is null) return;
        if (err is not null) _customErrors[c.Key] = err;
        else _customErrors.Remove(c.Key);
    }

    private IEnumerable<Card> Cards()
    {
        if (_music is not null) yield return _music;
        if (_app is not null) yield return _app;
        if (_watch is not null && !ReferenceEquals(_watch, _app)) yield return _watch;
        foreach (var c in _games.Values) yield return c;
        foreach (var c in _custom.Values) yield return c;
    }

    /// <summary>Sends every state that was waiting for a free place and can go now. Called from the loop only.</summary>
    public void Flush()
    {
        foreach (var c in Cards().ToList())
        {
            if (!c.Out.Pending || !c.Conn.Connected) continue;
            try
            {
                if (c.Out.Flush()) Answered(c);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The link went away; the card is sent afresh when it is back.
                c.Id = null;
            }
        }
    }

    /// <summary>How long until a waiting state can be sent (zero = now); null when nothing waits.</summary>
    public TimeSpan? NextSendIn
    {
        get
        {
            TimeSpan? best = null;
            var now = _now();
            foreach (var c in Cards())
            {
                if (!c.Conn.Connected || c.Out.NextSlot is not { } at) continue;
                var wait = at > now ? at - now : TimeSpan.Zero;
                if (best is null || wait < best) best = wait;
            }
            return best;
        }
    }

    /// <summary>Takes the card down but keeps its connection. Never lost: it waits for a free place like any state.</summary>
    private void Clear(Card? c)
    {
        if (c is null) return;
        if (c.Conn.Connected)
        {
            try
            {
                if (c.Out.Offer(null) == ActivityLimiter.Result.Sent) Answered(c);
            }
            catch { /* the link went away, and the card with it */ }
        }
        c.Id = null;
    }

    /// <summary>Closes the card's connection, which takes the card down on Discord's side as well.</summary>
    private static void Close(Card c, bool clear = true)
    {
        if (clear && c.Conn.Connected) c.Out.ClearBeforeClosing();
        c.Conn.Dispose();
    }

    private static void Drop(ref Card? c, bool clear = true)
    {
        if (c is null) return;
        Close(c, clear);
        c = null;
    }

    public void CloseAll(bool clear = true)
    {
        Drop(ref _music, clear);
        if (ReferenceEquals(_watch, _app)) _watch = null;
        Drop(ref _app, clear);
        Drop(ref _watch, clear);
        foreach (var c in _custom.Values) Close(c, clear);
        _custom.Clear();
        foreach (var c in _games.Values) Close(c, clear);
        _games.Clear();
    }

    // ---------------------------------------------------------------- card 1: games

    private long GameStartMs(Game g, RunningProcess p)
    {
        if (p.StartUtc is { } st) return new DateTimeOffset(DateTime.SpecifyKind(st, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        if (!_firstSeen.TryGetValue(g.Id, out var ms)) _firstSeen[g.Id] = ms = NowMs;
        return ms;
    }

    private async Task UpdateGamesAsync(PresenceSettings s, AppIds ids, List<(Game G, RunningProcess P)> running, CancellationToken ct)
    {
        var wanted = new Dictionary<string, (Game G, RunningProcess P)>();
        foreach (var r in running)
        {
            // Discord detects its official games itself: give it a head start so the session counts for Recent
            // Activity and streaks, then show our own card.
            if (s.OfficialToDiscord && r.G.DiscordId is not null && NowMs - GameStartMs(r.G, r.P) < s.OfficialDelayMinutes * 60000) continue;
            // A game Discord knows uses its own app; the others share the game card, when this build has one.
            var cid = AppIds.IsId(r.G.DiscordId) ? r.G.DiscordId! : ids.Game;
            if (cid is null) continue;
            wanted.TryAdd(cid, r);
        }
        foreach (var (cid, r) in wanted)
        {
            if (!_games.TryGetValue(cid, out var c)) _games[cid] = c = NewCard(cid, "game");
            try
            {
                if (!Connect(c)) continue;
                GenshinInfo? gi = null;
                var genshin = r.G.Exes.Contains("genshinimpact") || r.G.Exes.Contains("yuanshen");
                if (genshin) gi = await _genshin.GetAsync(s.GenshinUid.Trim(), ct).ConfigureAwait(false);
                var sig = $"{r.G.Id}|{gi?.Level}|{gi?.WorldLevel}";
                var changed = c.Id != sig;
                if (changed || (_now() - c.SentUtc).TotalSeconds >= 20)
                {
                    var a = new JsonObject { ["type"] = 0, ["name"] = r.G.Name, ["timestamps"] = new JsonObject { ["start"] = GameStartMs(r.G, r.P) } };
                    if (gi is not null)
                    {
                        a["details"] = $"{gi.Nickname} - AR {gi.Level}";
                        a["state"] = $"UID {s.GenshinUid.Trim()} - WL {gi.WorldLevel}";
                        a["assets"] = new JsonObject { ["large_image"] = "https://www.google.com/s2/favicons?sz=128&domain=genshin.hoyoverse.com", ["large_text"] = "Genshin Impact" };
                    }
                    if (changed) _log.Info("Discord status: playing " + r.G.Name);
                    Push(c, a, sig, keepAlive: !changed);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Game card error: " + AppIds.Scrub(ex.Message));
                c.Conn.Dispose();
                _games.Remove(cid);
            }
        }
        foreach (var id in _games.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            Close(_games[id]);
            _games.Remove(id);
            _log.Info("Discord status: game closed, card removed.");
        }
    }

    // ---------------------------------------------------------------- card 2: music

    private async Task<(MusicNow? Music, string? Line)> UpdateMusicAsync(PresenceSettings s, AppIds ids, CancellationToken ct)
    {
        MusicNow? m = null;
        if (s.ShowMusic || s.ShowSpotify)
        {
            try { m = await _inputs.MusicAsync(s.ShowMusic, s.ShowSpotify).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { m = null; }
        }
        var active = m is { Playing: true } && m.Title != "";
        string? line = null;
        if (!active)
        {
            Drop(ref _music);
            return (m is { Title.Length: > 0 } ? m : null, null);
        }
        if (ids.Music is not { } musicId)
        {
            Drop(ref _music); // this build has no music card
            return (m, null);
        }
        _music ??= NewCard(musicId, "music");
        if (!Connect(_music)) return (m, null);

        var lyrics = s.ShowLyrics ? await _extras.LyricsAsync(m!.Artist, m.Title, m.Album, m.DurationSec, ct).ConfigureAwait(false) : null;
        line = s.ShowLyrics ? MusicExtras.LineAt(lyrics, m!.PositionSec) : null;
        var id = $"music|{m!.Player}|{m.Artist}|{m.Title}|{m.Album}";
        var trackChanged = _music.Id != id;
        var lyricChanged = line is not null && line != _lastLyric;
        if (trackChanged || lyricChanged || (_now() - _music.SentUtc).TotalSeconds >= 15)
        {
            var art = s.ShowAlbumArt ? await _extras.ArtAsync(m.Artist, m.Title, m.Album, ct).ConfigureAwait(false) : null;
            var startMs = NowMs - (long)Math.Round(m.PositionSec * 1000);
            var ts = new JsonObject { ["start"] = startMs };
            if (m.DurationSec > 0) ts["end"] = startMs + (long)Math.Round(m.DurationSec * 1000);
            var a = new JsonObject
            {
                ["type"] = 2,
                ["details"] = m.Title,
                ["assets"] = new JsonObject { ["large_image"] = art ?? FallbackImage, ["large_text"] = m.Album != "" ? $"{m.Title} - {m.Album}" : m.Title },
                ["timestamps"] = ts,
            };
            // The lyric line, else the artist; a podcast or an ad has neither.
            if (line is not null) a["state"] = line;
            else if (m.Artist != "") a["state"] = "by " + m.Artist;
            if (m.Artist != "") a["name"] = m.Artist;
            Push(_music, a, id, keepAlive: !trackChanged && !lyricChanged);
            if (trackChanged) { _log.Info($"Discord status: music ({m.Player}) {m.Artist} - {m.Title}"); _lastLyric = null; }
            if (lyricChanged) _lastLyric = line;
        }
        return (m, line);
    }

    // ---------------------------------------------------------------- card 3: current app

    private string? UpdateApp(PresenceSettings s, AppIds ids, IReadOnlySet<string> gameExes)
    {
        if (!s.ShowCurrentApp || ids.App is not { } appId)
        {
            if (!ReferenceEquals(_watch, _app)) Drop(ref _app);
            return null;
        }
        _app ??= NewCard(appId, "app");
        if (!Connect(_app)) return null;
        var fg = _inputs.Foreground(gameExes);
        if (fg is not null)
        {
            _lastValidApp = _now();
            if (fg.Proc != _appPendProc) { _appPendProc = fg.Proc; _appPendSince = _now(); }
            var settled = (_now() - _appPendSince).TotalSeconds;
            var id = "app|" + fg.Label;
            var isNew = _app.Id != id && settled >= 3;
            var due = (_now() - _app.SentUtc).TotalSeconds >= 20;
            if (isNew || _app.Id == id || due)
            {
                if (isNew || _appStartMs == 0) _appStartMs = NowMs;
                // Browsers: just the tab's title.
                var detail = System.Text.RegularExpressions.Regex.Replace(ForegroundSource.BrowserSuffix.Replace(fg.Title, ""), @"\s+and \d+ more pages?.*$", "");
                if (detail.Length > 128) detail = detail[..125] + "...";
                var a = new JsonObject { ["type"] = 0, ["name"] = fg.Label, ["timestamps"] = new JsonObject { ["start"] = _appStartMs } };
                if (detail != "" && detail != fg.Label) a["details"] = detail;
                a["assets"] = new JsonObject { ["large_image"] = fg.Icon ?? GenericAppIcon, ["large_text"] = fg.Label };
                // The same app: a new window title goes out at once, an unchanged card only as a keep-alive.
                Push(_app, a, id, keepAlive: !isNew && _app.Id == id && due);
                if (isNew) _log.Info("Discord status: app " + fg.Label);
            }
            return fg.Label;
        }
        // An ignored window (Discord itself, or the game): keep the last app for a few minutes.
        if (_app.Id?.StartsWith("app|", StringComparison.Ordinal) == true && _lastValidApp is { } lv && (_now() - lv).TotalSeconds < 300)
            return _app.Id[4..];
        Clear(_app);
        return null;
    }

    // ---------------------------------------------------------------- card 3a: watching (takes the app card's place)

    private async Task<string?> UpdateWatchingAsync(PresenceSettings s, AppIds ids, CancellationToken ct)
    {
        WatchingNow? w = null;
        if (s.ShowWatching)
        {
            try
            {
                var sessions = await _inputs.SessionsAsync().ConfigureAwait(false);
                w = Watching.Find(sessions, _inputs.BrowserTitles, _ext, s.ShowWatchCasual, _now());
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { w = null; }
        }
        if (w is null)
        {
            _watchKey = null;
            ReleaseWatch();
            return null;
        }
        // A service with its own Discord app gets its own card; the others take over the app card.
        var svcId = ids.Get(w.Svc.Key);
        if (svcId is not null)
        {
            if (_watch is not null && (ReferenceEquals(_watch, _app) || _watch.Conn.ClientId != svcId)) ReleaseWatch();
            _watch ??= NewCard(svcId, "watching");
        }
        else if (ids.App is { } appId)
        {
            if (_watch is not null && !ReferenceEquals(_watch, _app)) ReleaseWatch();
            _app ??= NewCard(appId, "app");
            _watch = _app;
        }
        else
        {
            // This build has no card to show it on.
            _watchKey = null;
            ReleaseWatch();
            return null;
        }
        if (!Connect(_watch)) return w.Svc.Name;

        var show = s.ShowWatchTitle ? w.Show : "";
        var wkey = $"watch|{w.Svc.Name}|{w.Show}";
        if (wkey != _watchKey) { _watchKey = wkey; _watchStart = NowMs; }
        var id = $"{wkey}|{w.Sub}";
        var changed = _watch.Id != id;
        if (changed || (_now() - _watch.SentUtc).TotalSeconds >= 20)
        {
            var logo = await _watchExtras.LogoAsync(w.Svc, svcId, ct).ConfigureAwait(false);
            // The video's own thumbnail (YouTube/Twitch, from the extension), else a poster lookup.
            string? poster = s.ShowWatchPoster && w.Image is not null ? w.Image
                : s.ShowWatchPoster && show != "" && !w.Svc.Casual ? await _watchExtras.PosterAsync(show, w.Sub, ct).ConfigureAwait(false)
                : null;
            var a = new JsonObject { ["type"] = 3, ["name"] = w.Svc.Name };
            if (poster is not null)
                a["assets"] = new JsonObject { ["large_image"] = poster, ["large_text"] = show.Length >= 2 ? show : w.Svc.Name, ["small_image"] = logo, ["small_text"] = w.Svc.Name };
            else if (svcId is null)
                a["assets"] = new JsonObject { ["large_image"] = logo, ["large_text"] = w.Svc.Name };
            if (show.Length >= 2) a["details"] = show.Length > 128 ? show[..125] + "..." : show;
            if (s.ShowWatchTitle && w.Sub.Length >= 2) a["state"] = w.Sub.Length > 128 ? w.Sub[..125] + "..." : w.Sub;
            // A real progress bar when the site reports where you are, otherwise time since it started.
            if (w.Dur > 60 && w.Pos >= 0 && w.Pos <= w.Dur)
            {
                var st = NowMs - (long)(w.Pos * 1000);
                a["timestamps"] = new JsonObject { ["start"] = st, ["end"] = st + (long)(w.Dur * 1000) };
            }
            else a["timestamps"] = new JsonObject { ["start"] = _watchStart };
            Push(_watch, a, id, keepAlive: !changed);
            if (changed) _log.Info($"Discord status: watching {w.Svc.Name}{(show != "" ? " - " + show : "")}");
        }
        return w.Svc.Name + (show != "" ? " - " + show : "");
    }

    /// <summary>Ends the watching card: its own app's card is cleared and closed; a borrowed app card is only cleared.</summary>
    private void ReleaseWatch()
    {
        if (_watch is null) return;
        if (ReferenceEquals(_watch, _app)) Clear(_app);
        else Drop(ref _watch);
        _watch = null;
    }

    // ---------------------------------------------------------------- card 4+: custom statuses

    private (int Shown, List<string> Skipped) UpdateCustom(PresenceSettings s, AppIds ids)
    {
        var (cards, skipped) = CustomCards.Build(s.CustomStatuses, s.CustomMode, ids);
        var sk = string.Join(", ", skipped);
        if (sk != _customSkipLog)
        {
            _customSkipLog = sk;
            if (sk != "") _log.Warn($"No free card for: {sk}. Give it its own Discord app id, or merge your statuses into one card.");
        }
        var wanted = cards.ToDictionary(c => c.AppId);
        foreach (var (id, cd) in wanted)
        {
            if (!_custom.TryGetValue(id, out var c)) _custom[id] = c = NewCard(id, "custom");
            try
            {
                if (!Connect(c)) continue;
                var act = (JsonObject)cd.Activity.DeepClone();
                var sig = $"{cd.Key}|{cd.Elapsed}|{act.ToJsonString()}";
                var changed = c.Id != sig;
                if (changed || (_now() - c.SentUtc).TotalSeconds >= 60)
                {
                    if (!_customStart.ContainsKey(cd.Key)) _customStart[cd.Key] = NowMs;
                    if (cd.Elapsed) act["timestamps"] = new JsonObject { ["start"] = _customStart[cd.Key] };
                    c.Key = cd.Key;
                    Push(c, act, sig, keepAlive: !changed);
                    if (changed) _log.Info("Discord status: custom status " + cd.Label);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Custom status error: " + AppIds.Scrub(ex.Message));
                c.Conn.Dispose();
                _custom.Remove(id);
            }
        }
        foreach (var id in _custom.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            Close(_custom[id]);
            _custom.Remove(id);
        }
        // A status switched off starts its timer from zero next time.
        var keys = cards.Select(c => c.Key).ToHashSet();
        foreach (var k in _customStart.Keys.Where(k => !keys.Contains(k)).ToList()) _customStart.Remove(k);
        foreach (var k in _customErrors.Keys.Where(k => !keys.Contains(k)).ToList()) _customErrors.Remove(k);
        return (cards.Count, skipped);
    }
}
