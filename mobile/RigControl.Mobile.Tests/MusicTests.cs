using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RigControl.Link.Tests;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Core.Music;

namespace RigControl.Mobile.Tests;

public class PkceTests
{
    [Fact]
    public void Verifier_is_64_unreserved_characters_and_random()
    {
        var a = Pkce.CreateVerifier();
        var b = Pkce.CreateVerifier();
        Assert.Equal(64, a.Length);
        Assert.All(a, c => Assert.Contains(c, Pkce.Alphabet));
        Assert.NotEqual(a, b);
        Assert.Equal(32, Pkce.CreateState().Length);
    }

    [Fact]
    public void Challenge_is_S256_base64url_without_padding()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

        var v = Pkce.CreateVerifier();
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(v))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, Pkce.Challenge(v));
        Assert.DoesNotContain('=', Pkce.Challenge(v));
    }

    [Fact]
    public void Authorize_url_is_exactly_the_agreed_one()
    {
        var url = SpotifyApp.AuthorizeUrl("CHAL-lenge_1", "st4te");
        Assert.Equal(
            "https://accounts.spotify.com/authorize?client_id=891243e2ce084409945d7f5e49b95fc8&response_type=code"
            + "&redirect_uri=rigcontrol%3A%2F%2Fcallback&code_challenge_method=S256&code_challenge=CHAL-lenge_1&state=st4te"
            + "&scope=user-read-currently-playing%20user-read-playback-state",
            url.AbsoluteUri);
    }

    [Fact]
    public void A_started_sign_in_links_its_challenge_and_state()
    {
        var signIn = new SpotifySignIn(new HttpClient(new FakeSpotify()));
        var request = signIn.Start();
        var q = Query(request.Url);
        Assert.Equal(Pkce.Challenge(request.Verifier), q["code_challenge"]);
        Assert.Equal(request.State, q["state"]);
        Assert.True(signIn.IsWaiting);
        Assert.DoesNotContain("state", request.ToString());
    }

    internal static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
}

public class SpotifySignInTests
{
    private const string Token = "AQDn-refresh_token.value~123";

    [Fact]
    public async Task Exchanges_the_code_without_a_secret_and_keeps_only_the_refresh_token()
    {
        var spotify = new FakeSpotify();
        var signIn = new SpotifySignIn(new HttpClient(spotify));
        var request = signIn.Start();

        var result = await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "the-code", ["state"] = request.State });

        Assert.True(result.Ok);
        Assert.Equal(Token, result.RefreshToken);
        Assert.DoesNotContain(Token, result.ToString());
        Assert.Equal(SpotifyApp.TokenEndpoint, spotify.Uri);
        Assert.Equal(HttpMethod.Post, spotify.Method);
        var form = spotify.Form!;
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("rigcontrol://callback", form["redirect_uri"]);
        Assert.Equal(SpotifyApp.ClientId, form["client_id"]);
        Assert.Equal(request.Verifier, form["code_verifier"]);
        Assert.False(form.ContainsKey("client_secret"));
        Assert.Null(spotify.Authorization);
        Assert.False(signIn.IsWaiting);
    }

    [Fact]
    public async Task A_wrong_or_missing_state_is_refused_before_Spotify_is_asked()
    {
        var spotify = new FakeSpotify();
        var signIn = new SpotifySignIn(new HttpClient(spotify));
        signIn.Start();

        Assert.False((await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "c", ["state"] = "other" })).Ok);
        Assert.False((await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "c" })).Ok);
        Assert.Null(spotify.Uri);
        Assert.True(signIn.IsWaiting); // the real callback may still come
    }

    [Fact]
    public async Task A_callback_without_a_started_sign_in_or_used_twice_is_refused()
    {
        var spotify = new FakeSpotify();
        var signIn = new SpotifySignIn(new HttpClient(spotify));
        Assert.False((await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "c", ["state"] = "s" })).Ok);

        var request = signIn.Start();
        var q = new Dictionary<string, string> { ["code"] = "c", ["state"] = request.State };
        Assert.True((await signIn.CompleteAsync(q)).Ok);
        Assert.False((await signIn.CompleteAsync(q)).Ok);
        Assert.Equal(1, spotify.Calls);
    }

    [Fact]
    public async Task An_older_sign_in_is_replaced_by_a_new_one()
    {
        var signIn = new SpotifySignIn(new HttpClient(new FakeSpotify()));
        var old = signIn.Start();
        signIn.Start();
        Assert.False((await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "c", ["state"] = old.State })).Ok);
    }

    [Fact]
    public async Task A_cancelled_sign_in_says_so()
    {
        var spotify = new FakeSpotify();
        var signIn = new SpotifySignIn(new HttpClient(spotify));
        var request = signIn.Start();
        var r = await signIn.CompleteAsync(new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = request.State });
        Assert.False(r.Ok);
        Assert.Contains("cancelled", r.Error);
        Assert.Null(spotify.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Invalid authorization code"}""", "expired")]
    [InlineData(HttpStatusCode.InternalServerError, "oops", "500")]
    [InlineData(HttpStatusCode.OK, """{"access_token":"a","token_type":"Bearer"}""", "no sign-in")]
    [InlineData(HttpStatusCode.OK, """{"refresh_token":"bad token with spaces"}""", "no sign-in")]
    [InlineData(HttpStatusCode.OK, "not json", "couldn't be read")]
    public async Task Spotify_errors_become_sentences(HttpStatusCode status, string body, string expected)
    {
        var signIn = new SpotifySignIn(new HttpClient(new FakeSpotify { Status = status, Body = body }));
        var request = signIn.Start();
        var r = await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "c", ["state"] = request.State });
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Error);
        Assert.DoesNotContain("Invalid authorization code", r.Error);
    }

    [Fact]
    public async Task No_network_is_a_sentence()
    {
        var signIn = new SpotifySignIn(new HttpClient(new FakeSpotify { Throw = true }));
        var request = signIn.Start();
        var r = await signIn.CompleteAsync(new Dictionary<string, string> { ["code"] = "c", ["state"] = request.State });
        Assert.False(r.Ok);
        Assert.Contains("Couldn't reach Spotify", r.Error);
    }
}

public class PhoneMusicPayloadTests
{
    [Fact]
    public void Nothing_playing_is_an_empty_object()
    {
        Assert.Empty(PhoneMusicPayload.Build(null));
        Assert.Empty(PhoneMusicPayload.Build(new PhoneMusicTrack("   ", "Artist", "Album", true, 3, 100)));
        Assert.Empty(PhoneMusicPayload.Build(new PhoneMusicTrack(null, null, null, false, 0, 0)));
    }

    [Fact]
    public void Trims_drops_control_and_format_characters_and_leaves_out_empty_fields()
    {
        var o = PhoneMusicPayload.Build(new PhoneMusicTrack("  Song​\nTwo ", " ", null, true, 12.34, 200));
        Assert.Equal("Song Two", (string?)o["title"]);
        Assert.False(o.ContainsKey("artist"));
        Assert.False(o.ContainsKey("album"));
        Assert.True((bool)o["playing"]!);
        Assert.Equal(12.3, (double)o["position"]!);
        Assert.Equal(200, (double)o["duration"]!);
    }

    [Fact]
    public void Caps_text_at_256_without_splitting_an_emoji()
    {
        var o = PhoneMusicPayload.Build(new PhoneMusicTrack(new string('a', 300), new string('b', 255) + "\U0001F3B5x", "c", false, 0, 0));
        Assert.Equal(256, ((string)o["title"]!).Length);
        Assert.Equal(new string('b', 255), (string)o["artist"]!);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(90000, 86400)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Clamps_times(double given, double sent)
    {
        var o = PhoneMusicPayload.Build(new PhoneMusicTrack("t", null, null, true, given, given));
        Assert.Equal(sent, (double)o["position"]!);
        Assert.Equal(sent, (double)o["duration"]!);
    }

    [Fact]
    public void The_PC_accepts_what_the_phone_builds()
    {
        var o = PhoneMusicPayload.Build(new PhoneMusicTrack(new string('é', 400), "A\tB", "Alb‮um", true, 1e9, -1));
        var checkedAction = global::RigControl.Link.LinkActions.Check(PcAction.PresencePhoneMusic, PcAction.PhoneMusic(o).ToJsonArgs(), out var error);
        Assert.Null(error);
        Assert.NotNull(checkedAction);
        Assert.NotNull(global::RigControl.Link.LinkActions.Check(PcAction.PresencePhoneMusic, PcAction.PhoneMusic(new JsonObject()).ToJsonArgs(), out _));
        Assert.NotNull(global::RigControl.Link.LinkActions.Check(PcAction.PresenceSpotifyConnect, PcAction.SpotifyConnect("AQDn-refresh_token.value~123").ToJsonArgs(), out _));
        Assert.NotNull(global::RigControl.Link.LinkActions.Check(PcAction.PresenceSpotifyDisconnect, PcAction.SpotifyDisconnect().ToJsonArgs(), out _));
    }

    [Fact]
    public void The_sign_in_stays_out_of_printed_actions()
    {
        var a = PcAction.SpotifyConnect("AQDn-refresh_token.value~123");
        Assert.DoesNotContain("AQDn", a.ToString());
        Assert.Empty(a.Args);
        Assert.Equal("AQDn-refresh_token.value~123", (string?)a.ToJsonArgs()["refreshToken"]);
    }
}

public class MusicSendingTests
{
    private static readonly PhoneMusicTrack Song = new("Song", "Artist", "Album", true, 10, 200);

    [Fact]
    public async Task Spotify_connect_and_disconnect_go_to_every_linked_pc_and_connections_are_given_back()
    {
        var link = new RecordingLink("pc-a", "pc-b");
        var results = await LinkedPcs.SendToAllAsync(link, PcAction.SpotifyConnect("AQDn-refresh_token.value~123"));
        Assert.All(results, r => Assert.True(r.Result.Ok));
        Assert.Equal(["pc-a", "pc-b"], link.Sent.Select(s => s.Pc).Order());
        Assert.All(link.Sent, s => Assert.Equal("presence.spotifyConnect", s.Action.Name));
        Assert.Equal(0, link.OpenConnections);

        await LinkedPcs.SendToAllAsync(link, PcAction.SpotifyDisconnect());
        Assert.Equal(2, link.Sent.Count(s => s.Action.Name == "presence.spotifyDisconnect"));
        Assert.Equal(0, link.OpenConnections);
    }

    [Fact]
    public async Task Music_is_sent_on_change_every_15_seconds_and_cleared_once()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var link = new RecordingLink("pc-a");
        var feeder = new PhoneMusicFeeder(link, time);

        Assert.True(await feeder.ReportAsync(Song));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await feeder.ReportAsync(Song with { Position = 13 })); // same song, on time
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await feeder.ReportAsync(Song with { Position = 16, Playing = false })); // paused
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await feeder.ReportAsync(Song with { Position = 120, Playing = false })); // seek
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await feeder.ReportAsync(new PhoneMusicTrack("Next", "Artist", "Album", true, 0, 180)));
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await feeder.ReportAsync(new PhoneMusicTrack("Next", "Artist", "Album", true, 15, 180))); // refresh

        Assert.True(await feeder.ReportAsync(null));
        Assert.False(await feeder.ReportAsync(null));
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(await feeder.ReportAsync(null));

        Assert.All(link.Sent, s => Assert.Equal("presence.phoneMusic", s.Action.Name));
        Assert.Equal(6, link.Sent.Count);
        Assert.Equal("Next", (string?)link.Sent[4].Action.ToJsonArgs()["title"]);
        Assert.Empty(link.Sent[^1].Action.ToJsonArgs());
        Assert.Equal(1, link.OpenConnections); // held while sharing is on
    }

    [Fact]
    public async Task Stopping_clears_the_card_and_gives_back_the_connections()
    {
        var link = new RecordingLink("pc-a", "pc-b");
        var feeder = new PhoneMusicFeeder(link, new ManualTime(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero)));
        await feeder.ReportAsync(Song);
        Assert.Equal(2, link.OpenConnections);

        await feeder.StopAsync();
        Assert.Equal(0, link.OpenConnections);
        Assert.Equal(2, link.Sent.Count(s => s.Action.ToJsonArgs().Count == 0));

        // Stopping again sends nothing more.
        await feeder.StopAsync();
        Assert.Equal(4, link.Sent.Count);
    }

    [Fact]
    public async Task Works_with_the_sample_link()
    {
        var link = new FakeLinkService(delay: TimeSpan.Zero);
        var results = await LinkedPcs.SendToAllAsync(link, PcAction.SpotifyConnect("AQDn-refresh_token.value~123"));
        Assert.Contains(results, r => r.Result.Ok);      // the online sample PC
        Assert.Contains(results, r => !r.Result.Ok);     // the offline one
    }
}

/// <summary>A Spotify token endpoint on the phone's side of the wire.</summary>
internal sealed class FakeSpotify : HttpMessageHandler
{
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    public string Body { get; init; } =
        """{"access_token":"access","token_type":"Bearer","scope":"user-read-currently-playing user-read-playback-state","expires_in":3600,"refresh_token":"AQDn-refresh_token.value~123"}""";
    public bool Throw { get; init; }

    public Uri? Uri { get; private set; }
    public HttpMethod? Method { get; private set; }
    public Dictionary<string, string>? Form { get; private set; }
    public string? Authorization { get; private set; }
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Uri = request.RequestUri;
        Method = request.Method;
        Authorization = request.Headers.Authorization?.ToString();
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Form = text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        if (Throw)
            throw new HttpRequestException("no network");
        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>A link that records what is sent and counts open connections.</summary>
internal sealed class RecordingLink(params string[] pcIds) : ILinkService
{
    private readonly object _gate = new();
    private int _open;
    public List<(string Pc, PcAction Action)> Sent { get; } = [];
    public int OpenConnections { get { lock (_gate) return _open; } }

    public event EventHandler<LinkEvent>? EventReceived { add { } remove { } }

    public Task<IReadOnlyList<LinkedPc>> GetPcsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LinkedPc>>(pcIds.Select(id => new LinkedPc(id, "PC " + id, "relay.test", true, null)).ToArray());

    public Task ConnectAsync(string pcId, CancellationToken cancellationToken = default)
    {
        lock (_gate) _open++;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string pcId)
    {
        lock (_gate) _open--;
        return Task.CompletedTask;
    }

    public Task<ActionResult> DoAsync(string pcId, PcAction action, CancellationToken cancellationToken = default)
    {
        lock (_gate) Sent.Add((pcId, action));
        return Task.FromResult(ActionResult.Success);
    }

    public Task<PairResult> PairAsync(PairingRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PairResult> ConfirmReplaceAsync(string replaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void DiscardReplace(string replaceId) { }
    public Task WatchAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<PcSnapshot> GetSnapshotAsync(string pcId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ForgetAsync(string pcId) => Task.CompletedTask;
}
