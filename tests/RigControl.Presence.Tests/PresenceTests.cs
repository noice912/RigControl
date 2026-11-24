using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Settings;
using RigControl.Presence;

namespace RigControl.Presence.Tests;

internal sealed class FakeConn : IDiscordConnection
{
    private readonly FakeDiscord _owner;
    public FakeConn(FakeDiscord owner, string id) { _owner = owner; ClientId = id; }
    public string ClientId { get; }
    public bool Connected { get; private set; }
    public DiscordUser? User { get; private set; }
    /// <summary>The account this connection reached (cards sent on it land there).</summary>
    public string? Account => User?.Id;
    public bool Connect()
    {
        Connected = _owner.Running;
        User = Connected ? _owner.User : null;
        return Connected;
    }
    /// <summary>What Discord does when the signed-in account changes: the link is cut.</summary>
    public void Cut() => Connected = false;
    public string? SetActivity(JsonObject? activity)
    {
        if (!Connected) throw new IOException("Discord is not connected.");
        _owner.Sent.Add((ClientId, activity?.ToJsonString(), _owner.Clock?.Invoke() ?? default));
        _owner.Cards[ClientId] = activity?.ToJsonString();
        _shows = activity is not null;
        if (activity is null)
        {
            _owner.Cards.Remove(ClientId);
            _owner.Cleared.Add((ClientId, Account));
        }
        return _owner.Answer?.Invoke(ClientId);
    }
    private bool _shows;
    public void Dispose()
    {
        // Discord takes a card down by itself when its connection closes.
        if (Connected && _shows) _owner.Cards.Remove(ClientId);
        _shows = false;
        Connected = false;
        _owner.Opened.Remove(this);
    }
}

internal sealed class FakeDiscord : IDiscordClient
{
    public bool Running { get; set; } = true;
    /// <summary>Set to pretend something else is seen (another Windows user's Discord, or one that doesn't answer).</summary>
    public DiscordLook? Seen { get; set; }
    /// <summary>The account signed in to the pretend Discord.</summary>
    public DiscordUser? User { get; set; }
    /// <summary>Cards taken down: which app id, on which account's connection.</summary>
    public List<(string ClientId, string? Account)> Cleared { get; } = new();
    public DiscordLook Look() => Seen ?? (Running ? DiscordLook.Running : DiscordLook.NotRunning);
    public Dictionary<string, string?> Cards { get; } = new();
    /// <summary>Every update that reached Discord, in order (null = card taken down), with the test clock's time.</summary>
    public List<(string ClientId, string? Activity, DateTime At)> Sent { get; } = new();
    public Func<DateTime>? Clock { get; set; }
    /// <summary>Discord's error for an update, by app id (null = accepted).</summary>
    public Func<string, string?>? Answer { get; set; }
    public List<FakeConn> Opened { get; } = new();
    public int OpenCount { get; private set; }
    public IDiscordConnection Open(string clientId)
    {
        OpenCount++;
        var c = new FakeConn(this, clientId);
        Opened.Add(c);
        return c;
    }
}

internal sealed class FakeInputs : IPresenceInputs
{
    public List<RunningProcess> Procs { get; } = new();
    public MusicNow? Music { get; set; }
    public ForegroundApp? App { get; set; }
    public IReadOnlyList<RunningProcess> Processes() => Procs;
    public Task<MusicNow?> MusicAsync(bool a, bool s) => Task.FromResult(Music);
    public ForegroundApp? Foreground(IReadOnlySet<string> gameExes) => App is not null && !gameExes.Contains(App.Proc) ? App : null;
    public List<MediaSession> Sessions { get; } = new();
    public List<string> Titles { get; } = new();
    public Task<IReadOnlyList<MediaSession>> SessionsAsync() => Task.FromResult<IReadOnlyList<MediaSession>>(Sessions);
    public IReadOnlyList<string> BrowserTitles() => Titles;
}

/// <summary>No network in tests: every web lookup answers 404.</summary>
internal sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}

public class EngineTests
{
    private readonly FakeDiscord _discord = new();
    private readonly FakeInputs _inputs = new();
    private DateTime _now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly PresenceSettings _s = new() { Enabled = true, ShowCurrentApp = false, ShowLyrics = false, ShowAlbumArt = false };

    private PresenceEngine Engine() => new(_discord, _inputs, NullLog.Instance, new HttpClient(new NoNetwork()), () => _now);

    private static readonly Game Official = new() { Id = "steam:1", Name = "Official Game", Exes = { "official" }, DiscordId = "111111111111111111" };
    private static readonly Game Unknown = new() { Id = "folder:x", Name = "Indie Game", Exes = { "indie" } };

    [Fact]
    public async Task A_running_game_without_its_own_Discord_app_gets_the_shared_game_card()
    {
        var e = Engine();
        _inputs.Procs.Add(new RunningProcess("indie", _now.AddMinutes(-5)));
        await e.TickAsync(_s, new[] { Unknown }, CancellationToken.None);
        Assert.Contains("Indie Game", _discord.Cards[TestIds.Game]);
        Assert.Equal(new[] { "folder:x" }, e.Now.RunningIds);

        _inputs.Procs.Clear();
        await e.TickAsync(_s, new[] { Unknown }, CancellationToken.None);
        Assert.False(_discord.Cards.ContainsKey(TestIds.Game)); // cleared, not left stale
        Assert.Empty(_discord.Opened);                                   // and the pipe closed
    }

    [Fact]
    public async Task Discord_gets_a_head_start_on_games_it_knows()
    {
        var e = Engine();
        _inputs.Procs.Add(new RunningProcess("official", _now.AddMinutes(-1)));
        await e.TickAsync(_s, new[] { Official }, CancellationToken.None);
        Assert.Empty(_discord.Cards);

        _now = _now.AddMinutes(1.5); // now 2.5 minutes in
        await e.TickAsync(_s, new[] { Official }, CancellationToken.None);
        Assert.Contains("Official Game", _discord.Cards[Official.DiscordId!]);
    }

    [Fact]
    public async Task A_switched_off_game_counts_as_running_but_gets_no_card()
    {
        var e = Engine();
        _s.DisabledGames.Add(Unknown.Id);
        _inputs.Procs.Add(new RunningProcess("indie", _now));
        await e.TickAsync(_s, new[] { Unknown }, CancellationToken.None);
        Assert.Empty(_discord.Cards);
        Assert.Equal(new[] { Unknown.Id }, e.Now.RunningIds);
    }

    [Fact]
    public async Task Music_shows_the_artist_and_goes_away_when_paused()
    {
        var e = Engine();
        _inputs.Music = new MusicNow("Spotify", "Song", "Band", "Album", true, 30, 200);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        var card = JsonNode.Parse(_discord.Cards[TestIds.Music]!)!;
        Assert.Equal("Song", card["details"]!.GetValue<string>());
        Assert.Equal("by Band", card["state"]!.GetValue<string>());
        Assert.Equal(2, card["type"]!.GetValue<int>());

        _inputs.Music = _inputs.Music with { Playing = false };
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        Assert.False(_discord.Cards.ContainsKey(TestIds.Music));
    }

    [Fact]
    public async Task Nothing_connects_while_Discord_is_closed()
    {
        _discord.Running = false;
        var e = Engine();
        _inputs.Procs.Add(new RunningProcess("indie", _now));
        await e.TickAsync(_s, new[] { Unknown }, CancellationToken.None);
        Assert.Equal(0, _discord.OpenCount);
        Assert.Equal("Waiting for Discord", e.Now.Status);
    }

    [Fact]
    public void Synced_lyrics_give_the_line_at_the_current_position()
    {
        var l = MusicExtras.ParseLrc("[00:01.50] first\r\n[00:10.00] second\n[00:20.00]\n");
        Assert.Equal("first", MusicExtras.LineAt(l, 5));
        Assert.Equal("second", MusicExtras.LineAt(l, 9.8)); // 0.3 s early, like RichPresence
        Assert.Null(MusicExtras.LineAt(l, 25));               // an empty line clears it
        Assert.Null(MusicExtras.LineAt(l, 0.5));
    }
}

public class ModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-presence-tests-" + Guid.NewGuid().ToString("N"));
    public ModuleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void RichPresence_settings_are_imported_without_any_app_ids()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "\uFEFF" + """
            {"genshin_uid":"123456789","show_current_app":false,"show_lyrics":true,"official_delay_minutes":3,
             "disabled_games":["steam:1"],"extra_folders":["D:\\Games2"],"custom_games":[{"name":"X","exe":"C:\\x.exe"}],
             "game_client_id":"999999999999999999"}
            """);
        var s = new PresenceSettings();
        Assert.True(s.ImportFromRichPresence(path));
        Assert.Equal("123456789", s.GenshinUid);
        Assert.False(s.ShowCurrentApp);
        Assert.Equal(3, s.OfficialDelayMinutes);
        Assert.Equal(new[] { "steam:1" }, s.DisabledGames);
        Assert.Equal("C:\\x.exe", s.CustomGames.Single().Exe);
        Assert.False(s.Enabled); // never switched on by an import
    }

    [Fact]
    public async Task Nothing_is_posted_while_the_standalone_RichPresence_runs()
    {
        var discord = new FakeDiscord();
        var inputs = new FakeInputs();
        inputs.Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100);
        var richPresence = true;
        var m = new PresenceModule(discord, inputs, new HttpClient(new NoNetwork()), () => richPresence) { TickInterval = TimeSpan.FromMilliseconds(20), StartBridge = false };
        var store = new SettingsStore(Path.Combine(_dir, "s.json"), NullLog.Instance);
        store.Set(PresenceSettings.Section, new PresenceSettings { Enabled = true, ImportedFromRichPresence = DateTimeOffset.Now, ShowLyrics = false, ShowAlbumArt = false, ShowCurrentApp = false });
        Directory.CreateDirectory(Path.Combine(_dir, "presence"));
        File.WriteAllText(Path.Combine(_dir, "presence", "library.json"), "[]");
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance, store, new AppEnvironment(_dir, _dir, true, "1", "x.exe")), CancellationToken.None);
        await Task.Delay(200);
        Assert.Empty(discord.Cards);
        var st = JsonSerializer.SerializeToElement(await m.InvokeAsync("getStatus", null, CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("RichPresence is running", st.GetProperty("blocked").GetString());

        richPresence = false;
        for (var i = 0; i < 50 && !discord.Cards.ContainsKey(TestIds.Music); i++) await Task.Delay(20);
        Assert.True(discord.Cards.ContainsKey(TestIds.Music));
        await m.DisposeAsync();
        Assert.Empty(discord.Cards); // closing removes the card
    }
}

public class BridgeTests
{
    private DateTime _now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private ExtensionBridge Bridge() => new(NullLog.Instance, "1.0.0", () => _now);

    private static (int, string?) Post(ExtensionBridge b, string json, bool fromExtension = true) =>
        b.HandleRequest("POST", "/watching", fromExtension, () => json);

    [Fact]
    public void The_handed_out_extension_can_check_that_the_app_is_this_users_own_RigControl()
    {
        var token = new string('a', 64);
        var mine = new ExtensionBridge(NullLog.Instance, "1.0.0", () => _now, linkToken: () => token);
        const string nonce = "00112233445566778899aabbccddeeff";

        var (code, json) = mine.HandleRequest("GET", "/status?nonce=" + nonce, true, () => null);
        Assert.Equal(200, code);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("RigControl", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal(ExtensionLink.Proof(token, nonce), doc.RootElement.GetProperty("proof").GetString());
        Assert.DoesNotContain(token, json); // the secret itself is never sent

        // Somebody else's RigControl (another secret) or a pretend one (none) cannot give the same answer.
        var theirs = new ExtensionBridge(NullLog.Instance, "1.0.0", () => _now, linkToken: () => new string('b', 64));
        using var other = JsonDocument.Parse(theirs.HandleRequest("GET", "/status?nonce=" + nonce, true, () => null).Json!);
        Assert.NotEqual(ExtensionLink.Proof(token, nonce), other.RootElement.GetProperty("proof").GetString());
        using var none = JsonDocument.Parse(Bridge().HandleRequest("GET", "/status?nonce=" + nonce, true, () => null).Json!);
        Assert.False(none.RootElement.TryGetProperty("proof", out _));

        // No number, or something that is not one: the plain answer, as older copies of the extension expect.
        foreach (var path in new[] { "/status", "/status?nonce=", "/status?nonce=xyz", "/status?other=1" })
        {
            using var plain = JsonDocument.Parse(mine.HandleRequest("GET", path, true, () => null).Json!);
            Assert.False(plain.RootElement.TryGetProperty("proof", out _));
        }
        Assert.Equal(403, mine.HandleRequest("GET", "/status?nonce=" + nonce, false, () => null).Code);
    }

    [Fact]
    public void Each_Windows_user_gets_one_secret_for_the_extension_and_keeps_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigcontrol-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "presence", "extension-link.key");
            var first = ExtensionLink.LoadOrCreateToken(file)!;
            Assert.Matches("^[0-9a-f]{64}$", first);
            Assert.Equal(first, ExtensionLink.LoadOrCreateToken(file));
            Assert.Contains($"const RP_TOKEN = '{first}';", ExtensionLink.TokenScript(first));
            // Anything that is not a secret never ends up in the script.
            Assert.Contains("const RP_TOKEN = '';", ExtensionLink.TokenScript("'; alert(1); '"));
            Assert.Contains("const RP_TOKEN = '';", ExtensionLink.TokenScript(null));
            File.WriteAllText(file, "not a secret");
            Assert.NotEqual("not a secret", ExtensionLink.LoadOrCreateToken(file));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp folder */ }
        }
    }

    [Fact]
    public void Only_browser_extension_requests_are_accepted()
    {
        var b = Bridge();
        Assert.Equal(403, Post(b, """{"service":"Netflix","title":"The Bear","playing":true}""", fromExtension: false).Item1);
        Assert.Null(b.Get("Netflix"));
        Assert.Equal(403, b.HandleRequest("GET", "/status", false, () => null).Item1);
        Assert.Equal(204, b.HandleRequest("OPTIONS", "/watching", false, () => null).Item1);
        Assert.Equal(400, Post(b, """{"service":"EvilTube","title":"x","playing":true}""").Item1);
    }

    [Fact]
    public void A_report_is_kept_per_tab_and_the_playing_one_wins()
    {
        var b = Bridge();
        Assert.Equal(204, Post(b, """{"service":"Netflix","tab":"aaaa1111bbbb2222","title":"Paused Show","playing":false}""").Item1);
        _now = _now.AddSeconds(1);
        Post(b, """{"service":"Netflix","tab":"cccc3333dddd4444","title":"The Bear","episode":"S2:E1\nPilot","playing":true,"position":"12.5","duration":1800}""");
        _now = _now.AddSeconds(1);
        Post(b, """{"service":"Netflix","tab":"aaaa1111bbbb2222","title":"Paused Show","playing":false}""");
        var r = b.Get("Netflix")!;
        Assert.Equal("The Bear", r.Title);
        Assert.Equal("S2:E1 Pilot", r.Episode); // newlines stripped
        Assert.Equal(12.5, r.Pos);              // culture-invariant
        Assert.Equal(1800, r.Dur);

        _now = _now.AddSeconds(20); // reports go stale after 15 s
        Assert.Null(b.Get("Netflix"));
    }

    [Fact]
    public void Thumbnails_only_come_from_YouTube_and_Twitch_image_servers_and_odd_tab_ids_share_one_slot()
    {
        var b = Bridge();
        Post(b, """{"service":"YouTube","tab":"../../x","title":"Video","playing":true,"image":"https://evil.example/a.jpg"}""");
        Assert.Equal("", b.Get("YouTube")!.Image);
        Post(b, """{"service":"YouTube","title":"Video","playing":true,"image":"https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg"}""");
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", b.Get("YouTube")!.Image);
    }
}

public class WatchingTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Watch The Bear | Hulu - Google Chrome", "Hulu", "The Bear")]
    [InlineData("Netflix", "Netflix", "")]
    [InlineData("Stranger Things - Netflix and 3 more pages - Personal - Microsoft​ Edge", "Netflix", "Stranger Things")]
    public void The_service_and_show_come_out_of_a_tab_title(string title, string service, string show)
    {
        var r = Watching.FindService(title, true);
        Assert.NotNull(r);
        Assert.Equal(service, r!.Value.Svc.Name);
        Assert.Equal(show, r.Value.Clean);
    }

    [Theory]
    [InlineData("Mad Max - Wikipedia - Google Chrome")]
    [InlineData("Netflixing around - Google Chrome")]
    public void A_service_name_inside_other_words_is_not_a_match(string title) => Assert.Null(Watching.FindService(title, true));

    [Fact]
    public void The_extension_beats_media_controls_and_a_streaming_service_beats_YouTube()
    {
        var sessions = new[] { new MediaSession("msedge", true, "Some video - YouTube", "www.youtube.com", 300, 10) };
        var ext = new ExtReport("The Bear", "S1:E1", "", true, 100, 1800, Now.AddSeconds(-2));
        var w = Watching.Find(sessions, () => Array.Empty<string>(), svc => svc == "Hulu" ? ext : null, true, Now)!;
        Assert.Equal("Hulu", w.Svc.Name);
        Assert.Equal(102, w.Pos, 1); // moved on by the report's age

        w = Watching.Find(sessions, () => Array.Empty<string>(), _ => null, true, Now)!;
        Assert.Equal("YouTube", w.Svc.Name);
        Assert.Null(Watching.Find(sessions, () => Array.Empty<string>(), _ => null, casual: false, Now));
    }

    [Fact]
    public void Watch_alone_is_never_a_title_and_a_paused_extension_report_wins()
    {
        var sessions = new[] { new MediaSession("chrome", true, "Watch", "www.hulu.com", 0, 0) };
        var w = Watching.Find(sessions, () => Array.Empty<string>(), _ => null, true, Now)!;
        Assert.Equal("Hulu", w.Svc.Name);
        Assert.Equal("", w.Show);
        var paused = new ExtReport("X", "", "", false, 0, 0, Now);
        Assert.Null(Watching.Find(sessions, () => Array.Empty<string>(), s => s == "Hulu" ? paused : null, true, Now));
    }

    [Fact]
    public void Posters_must_match_the_title()
    {
        Assert.True(WatchExtras.SameTitle("The Bear Season 2", "The Bear"));
        Assert.False(WatchExtras.SameTitle("Watch", "Watch the Skies"));
    }
}

public class WatchingCardTests
{
    private readonly FakeDiscord _discord = new();
    private readonly FakeInputs _inputs = new();
    private readonly PresenceSettings _s = new() { Enabled = true, ShowCurrentApp = true, ShowLyrics = false, ShowAlbumArt = false, ShowWatchPoster = false };

    [Fact]
    public async Task A_service_with_its_own_app_gets_its_own_card_and_the_app_card_steps_aside()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var e = new PresenceEngine(_discord, _inputs, NullLog.Instance, new HttpClient(new NoNetwork()), () => now);
        _inputs.App = new ForegroundApp("msedge", "Microsoft Edge", "Netflix", null);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        now = now.AddSeconds(5);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        Assert.True(_discord.Cards.ContainsKey(TestIds.App));

        _inputs.Sessions.Add(new MediaSession("msedge", true, "Stranger Things", "www.netflix.com", 3000, 60));
        now = now.AddSeconds(5);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        var netflix = TestIds.Netflix;
        var card = JsonNode.Parse(_discord.Cards[netflix]!)!;
        Assert.Equal(3, card["type"]!.GetValue<int>());
        Assert.Equal("Stranger Things", card["details"]!.GetValue<string>());
        Assert.False(_discord.Cards.ContainsKey(TestIds.App)); // no two cards stacked

        _inputs.Sessions.Clear();
        now = now.AddSeconds(5);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        Assert.False(_discord.Cards.ContainsKey(netflix));
    }

    [Fact]
    public async Task A_service_without_its_own_app_uses_the_app_card()
    {
        var e = new PresenceEngine(_discord, _inputs, NullLog.Instance, new HttpClient(new NoNetwork()));
        _inputs.Sessions.Add(new MediaSession("chrome", true, "Lo-fi beats - YouTube", "www.youtube.com", 0, 0));
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        var card = JsonNode.Parse(_discord.Cards[TestIds.App]!)!;
        Assert.Equal("YouTube", card["name"]!.GetValue<string>());
        Assert.Equal(3, card["type"]!.GetValue<int>());
    }
}

public class CustomStatusTests
{
    private static CustomStatus St(string name, string appId = "", bool on = true) => new() { Name = name, Enabled = on, AppId = appId };

    [Fact]
    public void Separate_mode_has_two_built_in_cards_and_own_app_ids_get_their_own()
    {
        var (cards, skipped) = CustomCards.Build(new[] { St("A"), St("B"), St("C"), St("D", "123456789012345678"), St("E", on: false) }, "separate", TestIds.Ids);
        Assert.Equal(3, cards.Count);
        Assert.Equal("123456789012345678", cards[0].AppId); // own id claims first
        Assert.Equal(TestIds.Pool, cards.Skip(1).Select(c => c.AppId));
        Assert.Equal(new[] { "C" }, skipped);
    }

    [Fact]
    public void Merge_mode_makes_one_card()
    {
        var a = St("Coding"); a.Details = "RigControl"; a.LargeImage = "https://x.example/a.png";
        var b = St("Music"); b.Details = "lo-fi"; b.LargeImage = "https://x.example/b.png"; b.Button1Label = "Site"; b.Button1Url = "https://example.com";
        var (cards, skipped) = CustomCards.Build(new[] { a, b }, "merge", TestIds.Ids);
        Assert.Empty(skipped);
        var act = Assert.Single(cards).Activity;
        Assert.Equal("Coding", act["name"]!.GetValue<string>());
        Assert.Equal("RigControl · lo-fi", act["details"]!.GetValue<string>());
        Assert.Equal("https://x.example/a.png", act["assets"]!["large_image"]!.GetValue<string>());
        Assert.Equal("https://x.example/b.png", act["assets"]!["small_image"]!.GetValue<string>());
        Assert.Equal("Site", act["buttons"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void Discords_limits_are_applied_before_sending()
    {
        var s = St(new string('n', 200));
        s.Type = 4; // not allowed: becomes Playing
        s.Details = "x"; // one character: left out
        s.LargeImage = "http://not-https.example/a.png";
        s.Button1Label = new string('b', 40); s.Button1Url = "javascript:alert(1)";
        s.Button2Label = "Ok"; s.Button2Url = "https://example.com";
        var a = CustomCards.Activity(s);
        Assert.Equal(0, a["type"]!.GetValue<int>());
        Assert.Equal(128, a["name"]!.GetValue<string>().Length);
        Assert.Null(a["details"]);
        Assert.Null(a["assets"]);
        Assert.Single(a["buttons"]!.AsArray());
    }

    [Fact]
    public async Task Custom_cards_go_up_and_come_down()
    {
        var discord = new FakeDiscord();
        var e = new PresenceEngine(discord, new FakeInputs(), NullLog.Instance, new HttpClient(new NoNetwork()));
        var s = new PresenceSettings { ShowMusic = false, ShowSpotify = false, ShowCurrentApp = false, ShowWatching = false };
        s.CustomStatuses.Add(St("Studying"));
        await e.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
        Assert.Contains("Studying", discord.Cards[TestIds.Pool[0]]);
        Assert.Equal(1, e.Now.CustomShown);
        s.CustomStatuses[0].Enabled = false;
        await e.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
        Assert.Empty(discord.Cards);
    }
}

public class ExtensionTests
{
    [Fact]
    public void The_browser_extension_is_bundled_and_talks_to_this_app()
    {
        var asm = typeof(PresenceModule).Assembly;
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("RigControl.Presence.Extension.", StringComparison.Ordinal)).ToList();
        foreach (var f in new[] { "manifest.json", "background.js", "link.js", "link-token.js", "common.js", "sites.js", "frame.js", "popup.html", "popup.js", "icon48.png", "icon128.png", "LICENSE-RichPresence.txt" })
            Assert.Contains("RigControl.Presence.Extension." + f, names);
        using var s = asm.GetManifestResourceStream("RigControl.Presence.Extension.manifest.json")!;
        using var doc = JsonDocument.Parse(s);
        Assert.Contains("RigControl", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("manifest_version").GetInt32());
        // No new permission: only the 127.0.0.1 one it always had.
        Assert.False(doc.RootElement.TryGetProperty("permissions", out _));
        Assert.Equal("http://127.0.0.1/*", doc.RootElement.GetProperty("host_permissions").EnumerateArray().Single().GetString());
        Assert.Equal("1.5.0", doc.RootElement.GetProperty("version").GetString());
        // Same ports and header as the bridge: the extension tries each of them and keeps the one that answers.
        var link = Text(asm, "link.js");
        Assert.Contains("RP_FIRST_PORT = " + ExtensionBridge.FirstPort, link);
        Assert.Contains("RP_PORT_COUNT = " + ExtensionBridge.PortCount, link);
        Assert.Contains("X-RichPresence", link);
        Assert.Contains("'RigControl'", link);
        Assert.Contains("importScripts('link-token.js', 'link.js')", Text(asm, "background.js"));
        // The copy in the source folder carries no secret; the handed-out one gets this user's.
        Assert.Contains("const RP_TOKEN = '';", Text(asm, "link-token.js"));
        Assert.Contains("link-token.js", Text(asm, "popup.html"));
        // With a secret, an app that can't prove it knows it is never used.
        Assert.Contains("s.proof !== await rpProof(nonce)", link);
        Assert.Contains("rpTrusted()", Text(asm, "background.js"));
        Assert.Contains("X-RichPresence", Text(asm, "background.js"));
        Assert.Contains("link.js", Text(asm, "popup.html"));
        // Nothing is left that only knows the first port.
        foreach (var f in new[] { "background.js", "popup.js" }) Assert.DoesNotContain("127.0.0.1:47610", Text(asm, f));
    }

    private static string Text(System.Reflection.Assembly asm, string file)
    {
        using var r = new StreamReader(asm.GetManifestResourceStream("RigControl.Presence.Extension." + file)!);
        return r.ReadToEnd();
    }
}
