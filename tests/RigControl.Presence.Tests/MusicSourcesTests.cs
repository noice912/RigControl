using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Settings;
using RigControl.Presence;

namespace RigControl.Presence.Tests;

/// <summary>Pretend Spotify: answers by a script per request and records what was asked.</summary>
internal sealed class FakeSpotify : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Auth, string? Body)> Calls { get; } = new();
    public Func<HttpRequestMessage, string?, HttpResponseMessage>? Token { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage>? Playing { get; set; }
    public int Refreshes => Calls.Count(c => c.Uri == SpotifyWebSource.TokenUri);
    public int Polls => Calls.Count(c => c.Uri == SpotifyWebSource.PlayingUri);

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage AccessToken(string access = "access-token-0000001", string? refresh = null) =>
        Json(HttpStatusCode.OK, refresh is null
            ? $$"""{"access_token":"{{access}}","token_type":"Bearer","expires_in":3600}"""
            : $$"""{"access_token":"{{access}}","token_type":"Bearer","expires_in":3600,"refresh_token":"{{refresh}}"}""");

    public const string Song = """
        {"is_playing":true,"progress_ms":30000,"currently_playing_type":"track",
         "item":{"type":"track","name":"Song","duration_ms":200000,"artists":[{"name":"Band"},{"name":"Guest"}],
                 "album":{"name":"Album","images":[{"url":"https://i.scdn.co/image/abc","height":640}]}}}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        if (request.RequestUri == SpotifyWebSource.TokenUri) return (Token ?? ((_, _) => AccessToken()))(request, body);
        if (request.RequestUri == SpotifyWebSource.PlayingUri) return (Playing ?? (_ => Json(HttpStatusCode.OK, Song)))(request);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public class SpotifyWebTests
{
    private const string Refresh = "refresh-token-AAAAAAAAAAAAAAAA";
    private readonly FakeSpotify _fake = new();
    private readonly ListLog _log = new();
    private readonly List<string?> _saved = new();
    private DateTime _now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private SpotifyWebSource Make()
    {
        var s = new SpotifyWebSource(new HttpClient(_fake), _log, t => _saved.Add(t), () => _now);
        s.Connect(Refresh);
        return s;
    }

    [Fact]
    public async Task Refreshes_with_the_public_client_id_and_no_secret_then_reads_the_song()
    {
        var s = Make();
        await s.TickAsync(default);
        var refresh = _fake.Calls[0];
        Assert.Equal(HttpMethod.Post, refresh.Method);
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("client_id=" + SpotifyWebSource.ClientId, refresh.Body);
        Assert.Contains("refresh_token=" + Refresh, refresh.Body);
        Assert.DoesNotContain("client_secret", refresh.Body);
        Assert.Equal("Bearer access-token-0000001", _fake.Calls[1].Auth);

        var now = s.Get()!;
        Assert.Equal(new MusicNow("Spotify", "Song", "Band, Guest", "Album", true, 30, 200), now.Music);
        Assert.Equal("https://i.scdn.co/image/abc", now.ArtUrl);

        // The position moves on between polls.
        _now = _now.AddSeconds(4);
        Assert.Equal(34, s.Get()!.Music.PositionSec);
    }

    [Fact]
    public async Task Polls_at_most_every_ten_seconds_and_reuses_the_access_token()
    {
        var s = Make();
        await s.TickAsync(default);
        await s.TickAsync(default);
        Assert.Equal(1, _fake.Polls);
        _now = _now.AddSeconds(10);
        await s.TickAsync(default);
        Assert.Equal(2, _fake.Polls);
        Assert.Equal(1, _fake.Refreshes);
    }

    [Fact]
    public async Task No_content_means_nothing_playing()
    {
        var s = Make();
        await s.TickAsync(default);
        Assert.NotNull(s.Get());
        _fake.Playing = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        _now = _now.AddSeconds(10);
        await s.TickAsync(default);
        Assert.Null(s.Get());
    }

    [Fact]
    public async Task Unauthorized_refreshes_once_and_asks_again()
    {
        var s = Make();
        var first = true;
        _fake.Playing = _ =>
        {
            if (!first) return FakeSpotify.Json(HttpStatusCode.OK, FakeSpotify.Song);
            first = false;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };
        await s.TickAsync(default);
        Assert.Equal(2, _fake.Refreshes);
        Assert.Equal(2, _fake.Polls);
        Assert.NotNull(s.Get());

        // Unauthorized again and again: only one retry per poll, never a loop.
        _fake.Playing = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        _now = _now.AddSeconds(10);
        await s.TickAsync(default);
        Assert.Equal(4, _fake.Polls);
        Assert.Equal(3, _fake.Refreshes);
    }

    [Fact]
    public async Task Too_many_requests_waits_as_long_as_Spotify_says()
    {
        var s = Make();
        _fake.Playing = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "60");
            return r;
        };
        await s.TickAsync(default);
        Assert.Equal(1, _fake.Polls);
        _now = _now.AddSeconds(30);
        await s.TickAsync(default);
        Assert.Equal(1, _fake.Polls); // still waiting
        _now = _now.AddSeconds(31);
        _fake.Playing = null;
        await s.TickAsync(default);
        Assert.Equal(2, _fake.Polls);
        Assert.NotNull(s.Get());
    }

    [Fact]
    public async Task Invalid_grant_forgets_the_sign_in_and_says_connect_again()
    {
        var s = Make();
        _fake.Token = (_, _) => FakeSpotify.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token revoked"}""");
        await s.TickAsync(default);
        Assert.False(s.Connected);
        Assert.Equal(SpotifyWebSource.ExpiredMessage, s.Problem);
        Assert.Equal(new string?[] { null }, _saved);
        Assert.Equal(0, _fake.Polls);
        _now = _now.AddMinutes(1);
        await s.TickAsync(default);
        Assert.Equal(1, _fake.Refreshes); // nothing more is tried
    }

    [Fact]
    public async Task A_new_refresh_token_from_Spotify_is_kept()
    {
        var s = Make();
        _fake.Token = (_, _) => FakeSpotify.AccessToken(refresh: "rotated-token-BBBBBBBBBBBBBBBB");
        await s.TickAsync(default);
        Assert.Equal(new string?[] { "rotated-token-BBBBBBBBBBBBBBBB" }, _saved);
    }

    [Fact]
    public async Task An_account_Spotify_refuses_says_so_and_waits_hours()
    {
        var s = Make();
        _fake.Playing = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await s.TickAsync(default);
        Assert.Equal(SpotifyWebSource.NotAllowedMessage, s.Problem);
        Assert.True(s.Connected);
        var polls = _fake.Polls;
        _now = _now.AddHours(5);
        await s.TickAsync(default);
        Assert.Equal(polls, _fake.Polls); // not asked again within hours
        _fake.Playing = null;
        _now = _now.AddHours(2);
        await s.TickAsync(default);
        Assert.Null(s.Problem); // let in later: fine again
    }

    [Fact]
    public async Task Only_the_sign_in_in_use_counts_as_current()
    {
        var s = Make();
        Assert.True(s.IsCurrent(Refresh));
        _fake.Token = (_, _) => FakeSpotify.AccessToken(refresh: "rotated-token-BBBBBBBBBBBBBBBB");
        await s.TickAsync(default);
        Assert.False(s.IsCurrent(Refresh));
        Assert.True(s.IsCurrent("rotated-token-BBBBBBBBBBBBBBBB"));
        s.Connect("newer-token-CCCCCCCCCCCCCCCCCC");
        Assert.False(s.IsCurrent("rotated-token-BBBBBBBBBBBBBBBB")); // a late save of the old one is skipped
    }

    [Fact]
    public async Task Neither_token_is_ever_logged()
    {
        var s = Make();
        _fake.Token = (_, _) => FakeSpotify.Json(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");
        await s.TickAsync(default);
        _fake.Token = null;
        _now = _now.AddMinutes(6);
        _fake.Playing = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await s.TickAsync(default);
        s.Connect(Refresh);
        _fake.Token = (_, _) => FakeSpotify.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        await s.TickAsync(default);
        Assert.NotEmpty(_log.Lines);
        Assert.All(_log.Lines, l => Assert.DoesNotContain("refresh-token", l));
        Assert.All(_log.Lines, l => Assert.DoesNotContain("access-token", l));
    }

    [Fact]
    public async Task A_reading_goes_stale_when_polls_keep_failing()
    {
        var s = Make();
        await s.TickAsync(default);
        _fake.Playing = _ => throw new HttpRequestException("offline");
        for (var i = 0; i < 4; i++)
        {
            _now = _now.AddSeconds(10);
            await s.TickAsync(default);
        }
        Assert.Null(s.Get());
    }

    [Fact]
    public async Task Disconnect_drops_the_reading()
    {
        var s = Make();
        await s.TickAsync(default);
        s.Disconnect();
        Assert.Null(s.Get());
        Assert.False(s.Connected);
    }

    [Theory]
    [InlineData("""{"is_playing":true,"progress_ms":1000,"currently_playing_type":"ad","item":null}""")]
    [InlineData("""{"is_playing":true,"progress_ms":1000,"currently_playing_type":"episode","item":null}""")]
    [InlineData("""{"is_playing":true,"progress_ms":1000,"currently_playing_type":"unknown"}""")]
    [InlineData("""{"is_playing":true,"item":{"type":"episode","name":"Podcast"}}""")]
    [InlineData("""{"is_playing":true,"item":{"type":"track","name":""}}""")]
    public void Ads_podcasts_and_empty_items_give_no_song(string json)
    {
        Assert.Null(SpotifyWebSource.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Parsing_is_strict_and_bounded()
    {
        Assert.ThrowsAny<JsonException>(() => SpotifyWebSource.Parse(Encoding.UTF8.GetBytes("""{"item":{},}""")));
        Assert.Throws<FormatException>(() => SpotifyWebSource.Parse(Encoding.UTF8.GetBytes("[]")));
        var big = new string('x', 1000);
        var r = SpotifyWebSource.Parse(Encoding.UTF8.GetBytes($$$"""{"is_playing":false,"progress_ms":-5,"item":{"name":"{{{big}}}\u0007","duration_ms":"x"}}"""))!;
        Assert.Equal(256, r.Music.Title.Length);
        Assert.Equal(0, r.Music.PositionSec);
        Assert.Equal(0, r.Music.DurationSec);
        Assert.False(r.Music.Playing);
        Assert.Equal("", r.Music.Artist);
        Assert.Throws<FormatException>(() => SpotifyWebSource.ParseToken(Encoding.UTF8.GetBytes("""{"token_type":"Bearer"}""")));
    }

    [Theory]
    [InlineData("https://i.scdn.co/image/abc", true)]
    [InlineData("http://i.scdn.co/image/abc", false)]
    [InlineData("https://evil.example/image.png", false)]
    [InlineData("https://i.scdn.co.evil.example/x", false)]
    [InlineData("https://user@i.scdn.co/x", false)]
    [InlineData("https://i.scdn.co:8443/x", false)]
    public void Album_art_only_from_Spotifys_image_host_over_https(string url, bool ok)
    {
        Assert.Equal(ok, SpotifyWebSource.ArtUrl(url) is not null);
    }

    [Theory]
    [InlineData("https://accounts.spotify.com/api/token", true)]
    [InlineData("https://api.spotify.com/v1/me/player/currently-playing", true)]
    [InlineData("http://api.spotify.com/v1/me", false)]
    [InlineData("https://api.spotify.com.evil.example/v1", false)]
    [InlineData("https://example.com/", false)]
    public void Requests_go_only_to_Spotify_over_https(string url, bool ok)
    {
        Assert.Equal(ok, SpotifyWebSource.Allowed(new Uri(url)));
    }

    [Fact]
    public async Task An_oversized_answer_is_refused()
    {
        var s = Make();
        _fake.Playing = _ => FakeSpotify.Json(HttpStatusCode.OK, "{\"x\":\"" + new string('a', 300 * 1024) + "\"}");
        await s.TickAsync(default);
        Assert.Null(s.Get());
        Assert.Contains(_log.Lines, l => l.Contains("couldn't be read"));
    }
}

public class PhoneMusicTests
{
    private DateTime _now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_report_counts_for_45_seconds_and_its_position_moves_on()
    {
        var p = new PhoneMusic(() => _now);
        p.Set(new MusicNow("Apple Music", "Song", "Band", "", true, 10, 200));
        _now = _now.AddSeconds(20);
        Assert.Equal(30, p.Get()!.PositionSec);
        _now = _now.AddSeconds(26);
        Assert.Null(p.Get());
    }

    [Fact]
    public void An_empty_report_clears_it()
    {
        var p = new PhoneMusic(() => _now);
        p.Set(new MusicNow("Apple Music", "Song", "Band", "", true, 10, 200));
        p.Set(null);
        Assert.Null(p.Get());
    }

    private static readonly MusicNow Pc = new("Spotify", "Pc song", "A", "", true, 1, 100);
    private static readonly SpotifyNow Web = new(new MusicNow("Spotify", "Web song", "B", "", true, 1, 100), "https://i.scdn.co/image/x");
    private static readonly MusicNow Phone = new("Apple Music", "Phone song", "C", "", true, 1, 100);

    [Fact]
    public void The_PC_comes_first_then_Spotify_then_the_phone()
    {
        Assert.Equal("pc", PresenceEngine.PickMusic(Pc, Web, Phone).Source);
        Assert.Equal("spotify", PresenceEngine.PickMusic(Pc with { Playing = false }, Web, Phone).Source);
        Assert.Equal("https://i.scdn.co/image/x", PresenceEngine.PickMusic(null, Web, Phone).Art);
        Assert.Equal("phone", PresenceEngine.PickMusic(Pc with { Playing = false }, Web with { Music = Web.Music with { Playing = false } }, Phone).Source);
        Assert.Equal("phone", PresenceEngine.PickMusic(null, null, Phone).Source);
        var paused = PresenceEngine.PickMusic(Pc with { Playing = false }, null, Phone with { Playing = false });
        Assert.Equal(("pc", false), (paused.Source, paused.Music!.Playing));
        Assert.Null(PresenceEngine.PickMusic(null, null, null).Music);
    }
}

public class MusicCardSourceTests
{
    private readonly FakeDiscord _discord = new();
    private readonly FakeInputs _inputs = new();
    private DateTime _now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly PresenceSettings _s = new() { Enabled = true, ShowCurrentApp = false, ShowLyrics = false, ShowWatching = false };

    [Fact]
    public async Task The_phones_song_shows_with_a_hint_until_it_goes_stale_and_the_PC_wins()
    {
        var phone = new PhoneMusic(() => _now);
        var e = new PresenceEngine(_discord, _inputs, NullLog.Instance, new HttpClient(new NoNetwork()), () => _now, phoneMusic: phone.Get);
        phone.Set(new MusicNow("Apple Music", "Phone song", "Band", "Album", true, 10, 200));
        await e.TickAsync(_s, Array.Empty<Game>(), default);
        var card = JsonNode.Parse(_discord.Cards[TestIds.Music]!)!;
        Assert.Equal("Phone song", card["details"]!.GetValue<string>());
        Assert.EndsWith("(on iPhone)", card["assets"]!["large_text"]!.GetValue<string>());
        Assert.Equal("phone", e.Now.MusicSource);

        // The PC's own player playing: it wins.
        _inputs.Music = new MusicNow("Spotify", "Pc song", "Other", "", true, 1, 100);
        await e.TickAsync(_s, Array.Empty<Game>(), default);
        Assert.Contains("Pc song", _discord.Cards[TestIds.Music]);
        Assert.Equal("pc", e.Now.MusicSource);

        // The PC stops; the phone's report is older than 45 s: no card.
        _inputs.Music = null;
        _now = _now.AddSeconds(50);
        await e.TickAsync(_s, Array.Empty<Game>(), default);
        Assert.False(_discord.Cards.ContainsKey(TestIds.Music));
        Assert.Null(e.Now.MusicSource);
    }

    [Fact]
    public async Task Spotifys_song_uses_its_own_album_art()
    {
        var web = new SpotifyNow(new MusicNow("Spotify", "Web song", "Band", "Album", true, 10, 200), "https://i.scdn.co/image/abc");
        var e = new PresenceEngine(_discord, _inputs, NullLog.Instance, new HttpClient(new NoNetwork()), () => _now, spotify: () => web);
        await e.TickAsync(_s, Array.Empty<Game>(), default);
        var card = JsonNode.Parse(_discord.Cards[TestIds.Music]!)!;
        Assert.Equal("https://i.scdn.co/image/abc", card["assets"]!["large_image"]!.GetValue<string>());
        Assert.Equal("spotify", e.Now.MusicSource);

        // Spotify switched off on the Games page: the Web API song is not used.
        _s.ShowSpotify = false;
        await e.TickAsync(_s, Array.Empty<Game>(), default);
        Assert.False(_discord.Cards.ContainsKey(TestIds.Music));
    }
}

/// <summary>Reversible stand-in for DPAPI: marks what it protected, so the test can see nothing is kept in plain text.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public string Description => "test";
    public List<string> Purposes { get; } = new();
    public byte[] Protect(byte[] data, byte[] purpose)
    {
        Purposes.Add(Encoding.UTF8.GetString(purpose));
        return Encoding.UTF8.GetBytes("P:" + Convert.ToBase64String(Enumerable.Reverse(data).ToArray()));
    }
    public byte[] Unprotect(byte[] protectedData, byte[] purpose)
    {
        var s = Encoding.UTF8.GetString(protectedData);
        if (!s.StartsWith("P:")) throw new System.Security.Cryptography.CryptographicException("not ours");
        return Enumerable.Reverse(Convert.FromBase64String(s[2..])).ToArray();
    }
}

public class SpotifyModuleTests : IDisposable
{
    private const string Token = "phone-refresh-token-CCCCCCCCCCCCCCCC";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-spotify-tests-" + Guid.NewGuid().ToString("N"));
    public SpotifyModuleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<(PresenceModule M, SettingsStore Store, ListLog Log, FakeProtector Secrets)> Start(FakeSpotify spotify, PresenceSettings? seed = null)
    {
        var log = new ListLog();
        var secrets = new FakeProtector();
        var m = new PresenceModule(new FakeDiscord(), new FakeInputs(), new HttpClient(new NoNetwork()), () => false)
        {
            TickInterval = TimeSpan.FromMilliseconds(20), StartBridge = false, SpotifyHttp = new HttpClient(spotify),
        };
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance);
        store.Set(PresenceSettings.Section, seed ?? new PresenceSettings { Enabled = true, ImportedFromRichPresence = DateTimeOffset.Now, ShowLyrics = false, ShowAlbumArt = false });
        Directory.CreateDirectory(Path.Combine(_dir, "presence"));
        File.WriteAllText(Path.Combine(_dir, "presence", "library.json"), "[]");
        var ctx = new ModuleContext(new EventBus(), log, store, new AppEnvironment(_dir, _dir, true, "1", "x.exe"))
        {
            Platform = PlatformServices.Create(_dir, false) with { Secrets = secrets },
        };
        await m.StartAsync(ctx, default);
        return (m, store, log, secrets);
    }

    private static JsonElement Status(object? o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task Connect_keeps_the_token_protected_reads_it_back_and_never_logs_it()
    {
        var fake = new FakeSpotify();
        var (m, store, log, secrets) = await Start(fake);
        var st = Status(await m.InvokeAsync("connectSpotify", J($$"""{"refreshToken":"{{Token}}"}"""), default));
        Assert.True(st.GetProperty("spotifyConnected").GetBoolean());
        Assert.Contains("RigControl.Presence.SpotifySignIn.v1", secrets.Purposes);
        var file = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain(Token, file);
        Assert.NotEqual("", store.Get<PresenceSettings>(PresenceSettings.Section).SpotifySignIn);

        // The loop reads Spotify, and the music line says where it came from.
        for (var i = 0; i < 100 && Status(await m.InvokeAsync("getStatus", null, default)).GetProperty("musicSource").ValueKind == JsonValueKind.Null; i++)
            await Task.Delay(20);
        var now = Status(await m.InvokeAsync("getStatus", null, default));
        Assert.Equal("spotify", now.GetProperty("musicSource").GetString());
        Assert.Contains(fake.Calls, c => c.Body?.Contains(Token) == true);
        Assert.DoesNotContain(Token, now.GetRawText());
        await m.DisposeAsync();

        // A fresh start reads the kept sign-in back.
        var fake2 = new FakeSpotify();
        var (m2, store2, _, _) = await Start(fake2, store.Get<PresenceSettings>(PresenceSettings.Section));
        Assert.True(Status(await m2.InvokeAsync("getStatus", null, default)).GetProperty("spotifyConnected").GetBoolean());
        for (var i = 0; i < 100 && fake2.Refreshes == 0; i++) await Task.Delay(20);
        Assert.Contains(fake2.Calls, c => c.Body?.Contains("refresh_token=" + Token) == true);

        var off = Status(await m2.InvokeAsync("disconnectSpotify", null, default));
        Assert.False(off.GetProperty("spotifyConnected").GetBoolean());
        Assert.Equal("", store2.Get<PresenceSettings>(PresenceSettings.Section).SpotifySignIn);
        await m2.DisposeAsync();
        Assert.All(log.Lines, l => Assert.DoesNotContain(Token, l));
    }

    [Theory]
    [InlineData("""{"refreshToken":"short"}""")]
    [InlineData("""{"refreshToken":"has spaces in it which is not ok"}""")]
    [InlineData("""{"refreshToken":"aaaaaaaaaaaaaaaaaaaa","x":1}""")]
    [InlineData("""{"refreshToken":12345678901234567890}""")]
    [InlineData("""{}""")]
    public async Task Connect_refuses_anything_that_isnt_a_token(string args)
    {
        var (m, _, _, _) = await Start(new FakeSpotify());
        var ex = await Assert.ThrowsAsync<RigControl.Core.Rpc.RpcException>(() => m.InvokeAsync("connectSpotify", J(args), default));
        Assert.DoesNotContain("aaaa", ex.Message);
        await m.DisposeAsync();
    }

    [Fact]
    public async Task A_kept_sign_in_that_cant_be_read_is_dropped()
    {
        var seed = new PresenceSettings { ImportedFromRichPresence = DateTimeOffset.Now, SpotifySignIn = Convert.ToBase64String(Encoding.UTF8.GetBytes("someone else's")) };
        var (m, store, log, _) = await Start(new FakeSpotify(), seed);
        Assert.False(Status(await m.InvokeAsync("getStatus", null, default)).GetProperty("spotifyConnected").GetBoolean());
        Assert.Equal("", store.Get<PresenceSettings>(PresenceSettings.Section).SpotifySignIn);
        Assert.Contains(log.Lines, l => l.Contains("connect Spotify again"));
        await m.DisposeAsync();
    }

    [Fact]
    public async Task Phone_music_is_checked_and_shows_as_the_phone_source()
    {
        var (m, _, _, _) = await Start(new FakeSpotify());
        await Assert.ThrowsAsync<RigControl.Core.Rpc.RpcException>(() => m.InvokeAsync("setPhoneMusic", J("""{"title":"","playing":true}"""), default));
        await Assert.ThrowsAsync<RigControl.Core.Rpc.RpcException>(() => m.InvokeAsync("setPhoneMusic", J("""{"title":"a","playing":true,"link":"x"}"""), default));
        await Assert.ThrowsAsync<RigControl.Core.Rpc.RpcException>(() => m.InvokeAsync("setPhoneMusic", J("""{"title":"a","playing":true,"position":-1}"""), default));
        await m.InvokeAsync("setPhoneMusic", J("""{"title":"Phone song","artist":"Band","album":"","playing":true,"position":5,"duration":200}"""), default);
        JsonElement st = default;
        for (var i = 0; i < 100; i++)
        {
            st = Status(await m.InvokeAsync("getStatus", null, default));
            if (st.GetProperty("musicSource").ValueKind == JsonValueKind.String) break;
            await Task.Delay(20);
        }
        Assert.Equal("phone", st.GetProperty("musicSource").GetString());
        Assert.Equal("Apple Music", st.GetProperty("music").GetProperty("player").GetString());

        await m.InvokeAsync("setPhoneMusic", J("{}"), default);
        for (var i = 0; i < 100; i++)
        {
            st = Status(await m.InvokeAsync("getStatus", null, default));
            if (st.GetProperty("musicSource").ValueKind == JsonValueKind.Null) break;
            await Task.Delay(20);
        }
        Assert.Equal(JsonValueKind.Null, st.GetProperty("musicSource").ValueKind);
        await m.DisposeAsync();
    }
}
