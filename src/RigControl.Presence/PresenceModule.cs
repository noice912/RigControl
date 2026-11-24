using System.Net.Http;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Presence;

public sealed record PresenceGameDto(string Id, string Name, string Store, bool Matched, bool Enabled, bool Running, bool HasArt, bool CanPlay);

public sealed record PresenceMusicDto(string Player, string Title, string Artist, string Album, bool Playing);

public sealed record PresenceSettingsDto(bool ShowMusic, bool ShowSpotify, bool ShowLyrics, bool ShowAlbumArt, bool ShowCurrentApp,
    bool OfficialToDiscord, double OfficialDelayMinutes, string GenshinUid, IReadOnlyList<string> ExtraFolders,
    bool ShowWatching, bool ShowWatchTitle, bool ShowWatchPoster, bool ShowWatchCasual, IReadOnlyList<CustomStatus> CustomStatuses, string CustomMode);

/// <summary>The Discord account the status shows on: Discord's user id and the name it shows.</summary>
public sealed record PresenceAttachedDto(string Id, string Name);

/// <param name="DiscordRunning">Discord desktop is running for this Windows user (another user's never counts).</param>
/// <param name="AttachedUser">The Discord account RigControl is attached to, or null.</param>
/// <param name="DiscordState">"attached", "found", "notRunning", "otherUser" or "noAnswer".</param>
/// <param name="DiscordReason">Why nothing is attached, in plain words; null when attached.</param>
/// <param name="AttachNote">Set for a few minutes after Discord switched to another account.</param>
/// <param name="ExtensionPort">The port the browser extension link listens on (47610-47619), or null.</param>
/// <param name="SetUp">False when this build has no Discord application id for any of its main cards (game, music, app).</param>
/// <param name="SetupNote">What is missing and what to do about it, in plain words; null when every main card has an id.</param>
/// <param name="OwnAppIds">The card names the user gave their own Discord application id for. Never the ids themselves.</param>
public sealed record PresenceStatusDto(bool Enabled, bool DiscordRunning, string? Blocked, string Status, string? Playing,
    PresenceMusicDto? Music, string? LyricLine, string? App, bool Scanning, string? ScannedAt, IReadOnlyList<PresenceGameDto> Games,
    PresenceSettingsDto Settings, string? ImportedFromRichPresence,
    string? Watching = null, int CustomShown = 0, IReadOnlyList<string>? CustomSkipped = null,
    IReadOnlyDictionary<string, string>? CustomErrors = null, bool ExtensionLinked = false,
    PresenceAttachedDto? AttachedUser = null, string DiscordState = "notRunning", string? DiscordReason = null,
    string? AttachNote = null, int? ExtensionPort = null,
    bool SetUp = true, string? SetupNote = null, IReadOnlyList<string>? OwnAppIds = null);

/// <summary>
/// Discord status (what used to be the standalone RichPresence app, rebuilt here): the game library and the game,
/// music and current-app cards. The original behaviour is RichPresence's (MIT licence).
///
/// Rules:
///  - Off until the user turns it on, and paused while the standalone RichPresence.exe is running, so the two never
///    post cards at the same time.
///  - Discord application ids are not in the source: an official build carries them encrypted (see
///    <see cref="AppIds"/>); a build without them says "isn't set up in this build" and uses the ids the user puts
///    in settings.json (presence > appIds), checked before use. An id is never logged and never returned over RPC.
///  - It attaches to this Windows user's own running Discord and nothing else: no linking, no sign-in, and
///    Discord's stored login is never read. Another signed-in user's Discord, games or browser never count.
///  - Games are started through Windows' desktop shell, so they don't inherit RigControl's administrator rights.
/// </summary>
public sealed class PresenceModule : IAppModule
{
    private readonly IDiscordClient _discord;
    private readonly IPresenceInputs _inputs;
    private readonly HttpClient _http;
    private readonly Func<bool> _richPresenceRunning;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private PresenceSettings _settings = new();
    private List<Game> _library = new();
    private DateTimeOffset? _scannedAt;
    private bool _scanning;
    private PresenceEngine? _engine;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _blocked;
    private bool _discordRunning;
    private DiscordLook _look = DiscordLook.NotRunning;
    private DiscordLook? _lookLogged;
    private DateTime _probedAt = DateTime.MinValue;
    private string? _lastPublished;
    private ExtensionBridge? _bridge;
    /// <summary>The ids in use: the built-in ones with the user's own laid over them.</summary>
    private volatile AppIds _ids = AppIds.Empty;
    private IReadOnlyList<string> _ownIds = Array.Empty<string>();

    public PresenceModule() : this(new DiscordClient(), new PcInputs(), DefaultHttp(), IsRichPresenceRunning) { }

    public PresenceModule(IDiscordClient discord, IPresenceInputs inputs, HttpClient http, Func<bool> richPresenceRunning)
    {
        _discord = discord;
        _inputs = inputs;
        _http = http;
        _richPresenceRunning = richPresenceRunning;
    }

    public string Name => "presence";
    public string DisplayName => "Discord status";
    public ModuleStatus Status => _status;

    /// <summary>Listen for the browser extension on 127.0.0.1:47610-47619 (off in tests).</summary>
    public bool StartBridge { get; init; } = true;

    /// <summary>How often the engine runs (short in tests).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The ids this build carries. Tests pass their own; left alone it is what the build embedded.</summary>
    public AppIds? BuiltInIds { get; init; }

    /// <summary>What the Games page says when the status can't show anything of its own.</summary>
    public const string NotSetUpNote = PresenceEngine.NotSetUp + ". If you built RigControl yourself, add your own Discord application ids in settings.json (presence > appIds) and start RigControl again.";

    private static HttpClient DefaultHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RigControl (personal use)");
        return h;
    }

    /// <summary>True when the standalone RichPresence runs for this Windows user (another user's copy doesn't count).</summary>
    public static bool IsRichPresenceRunning() => WinSession.PidsByName(new[] { "RichPresence" }).Count > 0;

    private string DataDir => Path.Combine(_ctx!.Environment.DataDirectory, "presence");
    private string LibraryPath => Path.Combine(DataDir, "library.json");

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        lock (_gate)
        {
            _settings = context.Settings.Get<PresenceSettings>(PresenceSettings.Section);
            if (_settings.ImportedFromRichPresence is null)
            {
                try
                {
                    if (_settings.ImportFromRichPresence(Path.Combine(PresenceSettings.RichPresenceDataDir, "settings.json")))
                    {
                        _log.Info("Discord status: settings copied from RichPresence.");
                        Save();
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Couldn't copy RichPresence's settings: " + ex.Message);
                }
            }
            try
            {
                if (File.Exists(LibraryPath))
                {
                    _library = JsonSerializer.Deserialize<List<Game>>(File.ReadAllText(LibraryPath)) ?? new();
                    _scannedAt = File.GetLastWriteTimeUtc(LibraryPath);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("The saved game library is unreadable; scanning again. " + ex.Message);
            }
            _bridge = StartBridge ? new ExtensionBridge(_log, context.Environment.Version, linkToken: LinkToken) : null;
            _bridge?.Start();
            var bridge = _bridge;
            var own = AppIds.From(_settings.AppIds, out var refused);
            // Names only: an id is never written to the log.
            foreach (var (name, why) in refused)
                _log.Warn($"Discord status: the \"{name}\" entry under presence > appIds in settings.json was left out: {why}.");
            UseIds(own);
            if (!_ids.SetUp) _log.Info("Discord status: " + NotSetUpNote);
            _engine = new PresenceEngine(_discord, _inputs, _log, _http, extensionReport: bridge is null ? null : bridge.Get, ids: () => _ids);
            _cts = new CancellationTokenSource();
        }
        _status = new ModuleStatus(ModuleState.Ready);
        var token = _cts.Token;
        if (_library.Count == 0 || _scannedAt is null || DateTimeOffset.UtcNow - _scannedAt > TimeSpan.FromDays(1)) StartScan();
        _loop = Task.Run(() => LoopAsync(token), token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Looks for this user's Discord and says in the log, once per change, what was found. While no card is up it
    /// also asks Discord once a minute who is signed in (a handshake only, nothing is shown), so the Games page can
    /// name the account before the status is switched on.
    /// </summary>
    private void WatchDiscord()
    {
        var look = _discord.Look();
        var mine = look is DiscordLook.Running or DiscordLook.NoAnswer;
        if (!mine) _engine!.Detach();
        else if (_blocked is null && !_engine!.AnyConnected && (DateTime.UtcNow - _probedAt).TotalSeconds >= 60)
        {
            _probedAt = DateTime.UtcNow;
            _engine.Probe();
            look = _discord.Look();
        }
        if (look != DiscordLook.Running && look != DiscordLook.NoAnswer) _probedAt = DateTime.MinValue;
        _look = look;
        _discordRunning = mine;
        if (_lookLogged == look) return;
        _lookLogged = look;
        _log.Info("Discord status: " + (look == DiscordLook.Running ? "Discord is running." : Reason(look)));
    }

    /// <summary>Why nothing is attached, for the Games page and the log.</summary>
    private static string? Reason(DiscordLook look) => look switch
    {
        DiscordLook.NotRunning => "Discord isn't running. Open the Discord desktop app and your status can show.",
        DiscordLook.OtherUser => "Discord is running under another Windows user. Open Discord in your own Windows account.",
        DiscordLook.NoAnswer => "Discord didn't answer. Restart Discord; RigControl keeps trying.",
        _ => null,
    };

    private void Save() => _ctx?.Settings.Set(PresenceSettings.Section, _settings);

    /// <summary>Lays the user's own ids over the built-in ones.</summary>
    private void UseIds(AppIds own)
    {
        _ownIds = own.Have;
        _ids = (BuiltInIds ?? AppIds.BuiltIn).With(own);
    }

    private static string? SetupNoteFor(AppIds ids)
    {
        if (!ids.SetUp) return NotSetUpNote;
        var missing = ids.MissingMain;
        if (missing.Count == 0) return null;
        return $"This build has no Discord application id for the {string.Join(" and ", missing)} card, so it stays off.";
    }

    private void StartScan()
    {
        lock (_gate)
        {
            if (_scanning || _cts is null) return;
            _scanning = true;
        }
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                List<string> folders;
                List<CustomGame> custom;
                lock (_gate)
                {
                    folders = _settings.ExtraFolders.ToList();
                    custom = _settings.CustomGames.ToList();
                }
                var scanner = new LibraryScanner(DataDir, _log, _http);
                var games = await scanner.ScanAsync(folders, custom, token).ConfigureAwait(false);
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(LibraryPath, JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
                lock (_gate)
                {
                    _library = games;
                    _scannedAt = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) { /* closing */ }
            catch (Exception ex)
            {
                _log.Warn("Game scan failed: " + ex.Message);
            }
            finally
            {
                lock (_gate) _scanning = false;
                Publish();
            }
        }, token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var wasRunning = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PresenceSettings s;
                List<Game> lib;
                lock (_gate)
                {
                    s = _settings;
                    lib = _library;
                }
                _blocked = _richPresenceRunning() ? "RichPresence is running. Close it to let RigControl show your Discord status." : null;
                if (s.Enabled && _blocked is null)
                {
                    if (!wasRunning) _log.Info("Discord status: started.");
                    wasRunning = true;
                    await _engine!.TickAsync(s, lib, ct).ConfigureAwait(false);
                }
                else if (wasRunning)
                {
                    _engine!.CloseAll();
                    wasRunning = false;
                    _log.Info("Discord status: stopped.");
                }
                WatchDiscord();
                Publish();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn("Discord status error: " + AppIds.Scrub(ex.Message));
                try { _engine?.CloseAll(clear: false); } catch { /* ignore */ }
            }
            try { await WaitAsync(wasRunning, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        try { _engine?.CloseAll(); } catch { /* closing */ }
    }

    /// <summary>
    /// The pause between two passes. A card update that had to wait for Discord's limit (5 per 20 s per card) is sent
    /// the moment it is allowed, not at the next pass.
    /// </summary>
    private async Task WaitAsync(bool sending, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TickInterval;
        while (true)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return;
            var due = sending ? _engine?.NextSendIn : null;
            if (due is not { } d || d >= left)
            {
                await Task.Delay(left, ct).ConfigureAwait(false);
                return;
            }
            if (d > TimeSpan.Zero) await Task.Delay(d, ct).ConfigureAwait(false);
            try
            {
                _engine!.Flush();
            }
            catch (Exception ex)
            {
                _log.Warn("Discord status error: " + AppIds.Scrub(ex.Message));
                return;
            }
            // Never spin: at least a short breath before looking again.
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
        }
    }

    private void Publish()
    {
        var s = GetStatus();
        var key = JsonSerializer.Serialize(s);
        if (key == _lastPublished) return;
        _lastPublished = key;
        _ctx?.Events.Publish("presence.status", s);
    }

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getStatus" => GetStatus(),
            "setSettings" => SetSettings(RpcArgs.Require<SettingsArgs>(args)),
            "setGameEnabled" => SetGameEnabled(RpcArgs.Require<GameArgs>(args)),
            "setCustomStatuses" => SetCustomStatuses(RpcArgs.Require<CustomArgs>(args)),
            "setAppIds" => SetAppIds(RpcArgs.Require<AppIdsArgs>(args)),
            "rescan" => Rescan(),
            "play" => Play(RpcArgs.Require<GameArgs>(args)),
            "getArt" => GetArt(RpcArgs.Require<GameArgs>(args)),
            "getExtension" => GetExtension(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult(result);
    }

    // ---------------------------------------------------------------- status

    private PresenceStatusDto GetStatus()
    {
        lock (_gate)
        {
            var now = _engine?.Now;
            var disabled = new HashSet<string>(_settings.DisabledGames);
            var running = new HashSet<string>(now?.RunningIds ?? Array.Empty<string>());
            var games = _library.Select(g => new PresenceGameDto(g.Id, g.Name, g.Store, g.Matched, !disabled.Contains(g.Id), running.Contains(g.Id),
                g.Art is not null && File.Exists(g.Art), !string.IsNullOrEmpty(g.Launch))).ToList();
            var on = _settings.Enabled && _blocked is null;
            var m = on ? now?.Music : null;
            var s = _settings;
            var user = _discordRunning ? _engine?.Attached : null;
            var look = _look;
            var ids = _ids;
            return new PresenceStatusDto(_settings.Enabled, _discordRunning, _blocked,
                !_settings.Enabled ? "Off" : _blocked is not null ? "Paused" : now?.Status ?? "Starting",
                now?.Playing, m is null ? null : new PresenceMusicDto(m.Player, m.Title, m.Artist, m.Album, m.Playing),
                on ? now?.LyricLine : null, on ? now?.App : null, _scanning, _scannedAt?.ToString("o"), games,
                new PresenceSettingsDto(s.ShowMusic, s.ShowSpotify, s.ShowLyrics, s.ShowAlbumArt, s.ShowCurrentApp, s.OfficialToDiscord,
                    s.OfficialDelayMinutes, s.GenshinUid, s.ExtraFolders, s.ShowWatching, s.ShowWatchTitle, s.ShowWatchPoster,
                    s.ShowWatchCasual, s.CustomStatuses, s.CustomMode),
                s.ImportedFromRichPresence?.ToString("o"),
                on ? now?.Watching : null, on ? now?.CustomShown ?? 0 : 0, on ? now?.CustomSkipped : null,
                on ? now?.CustomErrors : null, _bridge?.Listening ?? false,
                user is null ? null : new PresenceAttachedDto(user.Id, user.Name),
                user is not null ? "attached" : look switch
                {
                    DiscordLook.NotRunning => "notRunning",
                    DiscordLook.OtherUser => "otherUser",
                    DiscordLook.NoAnswer => "noAnswer",
                    _ => "found",
                },
                user is not null ? null : Reason(look),
                user is not null ? _engine?.AttachNote : null, _bridge?.Port,
                ids.SetUp, SetupNoteFor(ids), _ownIds);
        }
    }

    private PresenceStatusDto SetSettings(SettingsArgs a)
    {
        lock (_gate)
        {
            var s = _settings;
            if (a.Enabled is { } e) s.Enabled = e;
            if (a.ShowMusic is { } sm) s.ShowMusic = sm;
            if (a.ShowSpotify is { } ss) s.ShowSpotify = ss;
            if (a.ShowLyrics is { } sl) s.ShowLyrics = sl;
            if (a.ShowAlbumArt is { } sa) s.ShowAlbumArt = sa;
            if (a.ShowCurrentApp is { } sc) s.ShowCurrentApp = sc;
            if (a.OfficialToDiscord is { } od) s.OfficialToDiscord = od;
            if (a.ShowWatching is { } sw) s.ShowWatching = sw;
            if (a.ShowWatchTitle is { } swt) s.ShowWatchTitle = swt;
            if (a.ShowWatchPoster is { } swp) s.ShowWatchPoster = swp;
            if (a.ShowWatchCasual is { } swc) s.ShowWatchCasual = swc;
            if (a.OfficialDelayMinutes is { } dm)
            {
                if (double.IsNaN(dm) || dm < 0 || dm > 60) throw RpcException.InvalidArgs("The delay must be between 0 and 60 minutes.");
                s.OfficialDelayMinutes = dm;
            }
            if (a.GenshinUid is { } uid)
            {
                uid = uid.Trim();
                if (uid != "" && !System.Text.RegularExpressions.Regex.IsMatch(uid, @"^\d{9,10}$")) throw RpcException.InvalidArgs("A Genshin UID is 9 or 10 digits.");
                s.GenshinUid = uid;
            }
            Save();
        }
        Publish();
        return GetStatus();
    }

    private PresenceStatusDto SetGameEnabled(GameArgs a)
    {
        lock (_gate)
        {
            if (_library.All(g => g.Id != a.Id)) throw RpcException.InvalidArgs("Unknown game.");
            _settings.DisabledGames.Remove(a.Id!);
            if (a.Enabled == false) _settings.DisabledGames.Add(a.Id!);
            Save();
        }
        Publish();
        return GetStatus();
    }

    private PresenceStatusDto SetCustomStatuses(CustomArgs a)
    {
        var items = a.Items ?? throw RpcException.InvalidArgs("The list of custom statuses is missing.");
        if (items.Count > 20) throw RpcException.InvalidArgs("Up to 20 custom statuses can be kept.");
        foreach (var it in items)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(it.Id ?? "", @"^[\w-]{1,64}$")) it.Id = Guid.NewGuid().ToString("N");
            if (it.AppId.Trim() != "" && !CustomCards.IsAppId(it.AppId)) throw RpcException.InvalidArgs($"\"{CustomCards.Label(it)}\": a Discord app id is 17 to 20 digits.");
            it.AppId = it.AppId.Trim();
            if (CustomCards.IsBuiltIn(it.AppId, _ids))
                throw RpcException.InvalidArgs($"\"{CustomCards.Label(it)}\": that app id is one RigControl already uses for its own cards. Use your own Discord app id, or leave it empty.");
        }
        if (items.Select(i => i.Id).Distinct().Count() != items.Count) throw RpcException.InvalidArgs("Two custom statuses have the same id.");
        lock (_gate)
        {
            _settings.CustomStatuses = items;
            if (a.Mode is "merge" or "separate") _settings.CustomMode = a.Mode;
            Save();
        }
        Publish();
        return GetStatus();
    }

    /// <summary>
    /// The user's own Discord application ids, by card name. The whole set is replaced; an empty value removes one.
    /// Everything is checked first and nothing is saved when one entry is wrong. The ids are never logged.
    /// </summary>
    private PresenceStatusDto SetAppIds(AppIdsArgs a)
    {
        var given = a.Ids ?? throw RpcException.InvalidArgs("The list of Discord application ids is missing.");
        var clean = new Dictionary<string, string>();
        foreach (var (rawName, rawId) in given)
        {
            var id = (rawId ?? "").Trim();
            if (id == "") continue;
            var name = AppIds.NameOf(rawName)
                ?? throw RpcException.InvalidArgs($"There is no card called \"{AppIds.Scrub(rawName.Length > 32 ? rawName[..32] : rawName)}\". The names are: {string.Join(", ", AppIds.Names)}.");
            if (!AppIds.IsId(id)) throw RpcException.InvalidArgs($"\"{name}\": a Discord application id is 17 to 20 digits.");
            if (clean.ContainsKey(name)) throw RpcException.InvalidArgs($"\"{name}\" is there twice.");
            if (clean.ContainsValue(id)) throw RpcException.InvalidArgs($"\"{name}\" has the same Discord application id as another card. Each card needs its own.");
            clean[name] = id;
        }
        lock (_gate)
        {
            _settings.AppIds = clean;
            UseIds(AppIds.From(clean, out _));
            Save();
        }
        // Cards opened with the old ids are closed; the next pass opens them with the new ones.
        _engine?.Reopen();
        _log.Info(clean.Count == 0
            ? "Discord status: your own Discord application ids were removed."
            : $"Discord status: your own Discord application ids were saved for: {string.Join(", ", clean.Keys)}.");
        Publish();
        return GetStatus();
    }

    private PresenceStatusDto Rescan()
    {
        StartScan();
        return GetStatus();
    }

    private object Play(GameArgs a)
    {
        Game g;
        lock (_gate) g = _library.FirstOrDefault(x => x.Id == a.Id) ?? throw RpcException.InvalidArgs("Unknown game.");
        if (string.IsNullOrEmpty(g.Launch)) throw RpcException.Unavailable($"RigControl doesn't know how to start {g.Name}.");
        try
        {
            Launcher.Start(g.Launch);
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable($"Couldn't start {g.Name}: {ex.Message}");
        }
        _log.Info("Launching " + g.Name);
        return new { started = true, name = g.Name };
    }

    private object? GetArt(GameArgs a)
    {
        Game? g;
        lock (_gate) g = _library.FirstOrDefault(x => x.Id == a.Id);
        if (g?.Art is null || !File.Exists(g.Art)) return null;
        var bytes = File.ReadAllBytes(g.Art);
        if (bytes.Length > 4 << 20) return null;
        var mime = g.Art.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        return new { dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" };
    }

    // ---------------------------------------------------------------- the browser extension

    private const string ExtensionPrefix = "RigControl.Presence.Extension.";

    private string? _linkToken;

    /// <summary>This Windows user's secret for the handed-out browser extension (see <see cref="ExtensionLink"/>).</summary>
    private string? LinkToken()
    {
        if (_linkToken is not null || _ctx is null) return _linkToken;
        return _linkToken = ExtensionLink.LoadOrCreateToken(Path.Combine(_ctx.Environment.DataDirectory, "presence", "extension-link.key"));
    }

    /// <summary>
    /// Puts a copy of the browser extension in a new, randomly named folder (so no other program can have prepared
    /// that path) under RigControl's data folder, opens it in Explorer and returns where it is. Files are created new,
    /// never written over something already there.
    /// </summary>
    private object GetExtension()
    {
        var asm = typeof(PresenceModule).Assembly;
        var files = asm.GetManifestResourceNames().Where(n => n.StartsWith(ExtensionPrefix, StringComparison.Ordinal)).ToList();
        if (files.Count == 0) throw RpcException.Unavailable("This copy of RigControl doesn't include the browser extension.");
        var dir = Path.Combine(_ctx!.Environment.DataDirectory, "browser-extension-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        if (Directory.Exists(dir) || File.Exists(dir)) throw RpcException.Unavailable("Try again.");
        var info = Directory.CreateDirectory(dir);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw RpcException.Unsafe("The new folder turned out to be a link; nothing was written.");
        foreach (var name in files)
        {
            var file = name[ExtensionPrefix.Length..];
            if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) continue;
            if (file.Equals(ExtensionLink.TokenFile, StringComparison.OrdinalIgnoreCase)) continue; // written below, with this user's secret
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = new FileStream(Path.Combine(dir, file), FileMode.CreateNew, FileAccess.Write);
            src.CopyTo(dst);
        }
        // This copy carries this user's secret, so it only reports to this user's RigControl. The secret is not logged.
        var token = LinkToken();
        File.WriteAllText(Path.Combine(dir, ExtensionLink.TokenFile), ExtensionLink.TokenScript(token));
        if (token is null) _log.Warn("The browser extension copy could not be tied to your RigControl, so it reports to whichever RigControl answers first on this PC.");
        _log.Info("Browser extension copied to " + dir);
        try { Launcher.StartFolder(dir); }
        catch (Exception ex) { _log.Warn("Couldn't open the extension folder: " + ex.Message); }
        return new { path = dir };
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _bridge?.Dispose();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* closing */ }
        }
        _cts?.Dispose();
        _status = new ModuleStatus(ModuleState.Stopped);
    }

    private sealed class SettingsArgs
    {
        public bool? Enabled { get; set; }
        public bool? ShowMusic { get; set; }
        public bool? ShowSpotify { get; set; }
        public bool? ShowLyrics { get; set; }
        public bool? ShowAlbumArt { get; set; }
        public bool? ShowCurrentApp { get; set; }
        public bool? OfficialToDiscord { get; set; }
        public double? OfficialDelayMinutes { get; set; }
        public string? GenshinUid { get; set; }
        public bool? ShowWatching { get; set; }
        public bool? ShowWatchTitle { get; set; }
        public bool? ShowWatchPoster { get; set; }
        public bool? ShowWatchCasual { get; set; }
    }

    private sealed class AppIdsArgs
    {
        public Dictionary<string, string>? Ids { get; set; }
    }

    private sealed class CustomArgs
    {
        public List<CustomStatus>? Items { get; set; }
        public string? Mode { get; set; }
    }

    private sealed class GameArgs
    {
        public string? Id { get; set; }
        public bool? Enabled { get; set; }
    }
}
