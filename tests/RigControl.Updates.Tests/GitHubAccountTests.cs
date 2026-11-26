using System.Net;
using System.Text;
using RigControl.Core.Logging;
using RigControl.Core.Platform;
using RigControl.Core.Settings;

namespace RigControl.Updates.Tests;

/// <summary>Reversible stand-in for DPAPI, so the tests run anywhere; it still proves nothing is stored in plain text.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public string Description => "test";
    public byte[] Protect(byte[] data, byte[] purpose) => data.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)1).ToArray();
    public byte[] Unprotect(byte[] protectedData, byte[] purpose)
        => protectedData.Length > 0 && protectedData[0] == 1
            ? protectedData.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray()
            : throw new System.Security.Cryptography.CryptographicException("not ours");
}

public sealed class GitHubAccountTests : IDisposable
{
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string CodeUrl = "https://github.com/login/device/code";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-account-test-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private SettingsStore Settings() => new(Path.Combine(_dir, "settings.json"), NullLog.Instance);

    private GitHubSignIn SignIn(FakeGitHub fake, List<TimeSpan>? waits = null, string? clientId = "test-client")
        => new(fake.Client(), clientId, (t, _) => { waits?.Add(t); _now += t; return Task.CompletedTask; }, () => _now);

    private static FakeGitHub Sequence(FakeGitHub fake, string url, params string[] answers)
    {
        var i = 0;
        return fake.On(HttpMethod.Post, url, _ => FakeGitHub.Json(answers[Math.Min(i++, answers.Length - 1)]));
    }

    private const string Device = "{\"device_code\":\"dc\",\"user_code\":\"ABCD-1234\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900,\"interval\":5}";
    private const string Tokens = "{\"access_token\":\"ghu_access\",\"expires_in\":28800,\"refresh_token\":\"ghr_refresh\",\"refresh_token_expires_in\":15811200,\"token_type\":\"bearer\"}";

    [Fact]
    public async Task Device_flow_waits_slows_down_and_finishes()
    {
        var waits = new List<TimeSpan>();
        var fake = Sequence(new FakeGitHub(), CodeUrl, Device);
        Sequence(fake, TokenUrl,
            "{\"error\":\"authorization_pending\"}",
            "{\"error\":\"slow_down\",\"interval\":10}",
            "{\"error\":\"authorization_pending\"}",
            Tokens);
        var signIn = SignIn(fake, waits);

        var device = await signIn.StartAsync(default);
        Assert.Equal("ABCD-1234", device.UserCode);
        var tokens = await signIn.WaitAsync(device, default);

        Assert.Equal("ghu_access", tokens.AccessToken);
        Assert.Equal("ghr_refresh", tokens.RefreshToken);
        Assert.Equal(new[] { 5, 5, 10, 10 }, waits.Select(w => (int)w.TotalSeconds));
        Assert.DoesNotContain("ghu_access", tokens.ToString());
        // The token request is a device-code grant for this app's id, with no secret.
        var body = fake.Requests.Last().Body!;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", body);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Device_flow_stops_when_the_code_runs_out_or_is_declined()
    {
        var fake = Sequence(new FakeGitHub(), TokenUrl, "{\"error\":\"expired_token\"}");
        var device = new DeviceCode("dc", "X", "https://github.com/login/device", _now.AddMinutes(15), TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<GitHubException>(() => SignIn(fake).WaitAsync(device, default));

        var declined = Sequence(new FakeGitHub(), TokenUrl, "{\"error\":\"access_denied\"}");
        var ex = await Assert.ThrowsAsync<GitHubException>(() => SignIn(declined).WaitAsync(device, default));
        Assert.Contains("declined", ex.Message);

        // Pending for ever: stops at the code's own end time.
        var pending = Sequence(new FakeGitHub(), TokenUrl, "{\"error\":\"authorization_pending\"}");
        var shortDevice = device with { ExpiresAt = _now.AddSeconds(30) };
        await Assert.ThrowsAsync<GitHubException>(() => SignIn(pending).WaitAsync(shortDevice, default));
    }

    [Fact]
    public async Task An_empty_client_id_says_not_set_up()
    {
        var signIn = SignIn(new FakeGitHub(), clientId: "");
        Assert.False(signIn.Configured);
        var ex = await Assert.ThrowsAsync<GitHubException>(() => signIn.StartAsync(default));
        Assert.Equal(GitHubSignIn.NotSetUp, ex.Message);
    }

    [Fact]
    public async Task Tokens_are_stored_protected_renewed_when_expired_and_deleted_on_sign_out()
    {
        var fake = Sequence(new FakeGitHub(), TokenUrl, Tokens,
            "{\"access_token\":\"ghu_second\",\"expires_in\":28800,\"refresh_token\":\"ghr_second\",\"refresh_token_expires_in\":15811200}");
        fake.On(HttpMethod.Get, "https://api.github.com/user", _ => FakeGitHub.Json("{\"login\":\"octo\"}"));
        var settings = Settings();
        var signIn = SignIn(fake);
        var account = new GitHubAccount(settings, new XorProtector(), signIn, new GitHubApi(fake.Client()), NullLog.Instance, () => _now);

        var login = await account.CompleteSignInAsync(new DeviceCode("dc", "X", "https://github.com/login/device", _now.AddMinutes(15), TimeSpan.FromSeconds(1)), default);
        Assert.Equal("octo", login);
        Assert.Equal("octo", account.Login);

        var file = File.ReadAllText(settings.FilePath);
        Assert.DoesNotContain("ghu_access", file);
        Assert.DoesNotContain("ghr_refresh", file);

        Assert.Equal("ghu_access", await account.GetAccessTokenAsync(default));

        _now += TimeSpan.FromHours(9);
        Assert.Equal("ghu_second", await account.GetAccessTokenAsync(default));
        var refresh = fake.Requests.Last(r => r.Uri.AbsoluteUri == TokenUrl).Body!;
        Assert.Contains("grant_type=refresh_token", refresh);
        Assert.Contains("refresh_token=ghr_refresh", refresh);
        Assert.Contains("client_id=test-client", refresh);

        account.SignOut();
        Assert.Null(account.Login);
        Assert.DoesNotContain("octo", File.ReadAllText(settings.FilePath));
        await Assert.ThrowsAsync<GitHubException>(() => account.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task A_refused_refresh_forgets_the_sign_in()
    {
        var fake = Sequence(new FakeGitHub(), TokenUrl, Tokens, "{\"error\":\"bad_refresh_token\"}");
        fake.On(HttpMethod.Get, "https://api.github.com/user", _ => FakeGitHub.Json("{\"login\":\"octo\"}"));
        var account = new GitHubAccount(Settings(), new XorProtector(), SignIn(fake), new GitHubApi(fake.Client()), NullLog.Instance, () => _now);
        await account.CompleteSignInAsync(new DeviceCode("dc", "X", "https://github.com/login/device", _now.AddMinutes(15), TimeSpan.FromSeconds(1)), default);

        _now += TimeSpan.FromHours(9);
        await Assert.ThrowsAsync<GitHubException>(() => account.GetAccessTokenAsync(default));
        Assert.Null(account.Login);
    }

    [Fact]
    public async Task A_401_from_the_token_address_forgets_the_sign_in()
    {
        var i = 0;
        var fake = new FakeGitHub().On(HttpMethod.Post, TokenUrl, _ => i++ == 0
            ? FakeGitHub.Json(Tokens)
            : FakeGitHub.Json("{\"message\":\"Bad credentials\"}", HttpStatusCode.Unauthorized));
        fake.On(HttpMethod.Get, "https://api.github.com/user", _ => FakeGitHub.Json("{\"login\":\"octo\"}"));
        var account = new GitHubAccount(Settings(), new XorProtector(), SignIn(fake), new GitHubApi(fake.Client()), NullLog.Instance, () => _now);
        await account.CompleteSignInAsync(new DeviceCode("dc", "X", "https://github.com/login/device", _now.AddMinutes(15), TimeSpan.FromSeconds(1)), default);

        _now += TimeSpan.FromHours(9);
        await Assert.ThrowsAsync<GitHubException>(() => account.GetAccessTokenAsync(default));
        Assert.Null(account.Login);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("503")]
    [InlineData("500")]
    public async Task A_refresh_that_fails_for_network_reasons_keeps_the_sign_in(string failure)
    {
        var i = 0;
        var fake = new FakeGitHub().On(HttpMethod.Post, TokenUrl, _ =>
        {
            if (i++ == 0) return FakeGitHub.Json(Tokens);
            return failure switch
            {
                "network" => throw new HttpRequestException("No such host is known."),
                "503" => FakeGitHub.Json("{}", HttpStatusCode.ServiceUnavailable),
                _ => FakeGitHub.Json("{}", HttpStatusCode.InternalServerError),
            };
        });
        fake.On(HttpMethod.Get, "https://api.github.com/user", _ => FakeGitHub.Json("{\"login\":\"octo\"}"));
        var account = new GitHubAccount(Settings(), new XorProtector(), SignIn(fake), new GitHubApi(fake.Client()), NullLog.Instance, () => _now);
        await account.CompleteSignInAsync(new DeviceCode("dc", "X", "https://github.com/login/device", _now.AddMinutes(15), TimeSpan.FromSeconds(1)), default);

        _now += TimeSpan.FromHours(9);
        await Assert.ThrowsAsync<GitHubException>(() => account.GetAccessTokenAsync(default));
        Assert.Equal("octo", account.Login);
    }

    [Fact]
    public void Only_GitHubs_own_refusal_counts_as_a_refusal()
    {
        Assert.True(GitHubAccount.RefusedByGitHub(new GitHubException("x", HttpStatusCode.BadRequest)));
        Assert.True(GitHubAccount.RefusedByGitHub(new GitHubException("x", HttpStatusCode.Unauthorized)));
        Assert.True(GitHubAccount.RefusedByGitHub(new GitHubException("x") { OAuthError = "bad_refresh_token" }));
        Assert.True(GitHubAccount.RefusedByGitHub(new GitHubException("x") { OAuthError = "invalid_grant" }));
        Assert.False(GitHubAccount.RefusedByGitHub(new GitHubException("GitHub could not be reached.")));
        Assert.False(GitHubAccount.RefusedByGitHub(new GitHubException("x", HttpStatusCode.ServiceUnavailable)));
        Assert.False(GitHubAccount.RefusedByGitHub(new GitHubException("x", HttpStatusCode.Forbidden)));
        Assert.False(GitHubAccount.RefusedByGitHub(new GitHubException("x") { OAuthError = "slow_down" }));
    }

    [Fact]
    public void Reviews_carry_the_marker_and_nothing_else()
    {
        var (title, body) = Reviews.Compose(4, "  Works great on my rig. Fans are quiet now and the lights match.  ", AppVersion.Parse("1.1.2"));
        Assert.StartsWith("Review: ★★★★☆ Works great", title);
        Assert.EndsWith("<!-- rigcontrol-review v1 stars=4 version=1.1.2 -->", body);
        Assert.Contains("RigControl 1.1.2", body);

        var review = Reviews.Read(new IssueInfo(7, "https://github.com/x", title, body, "octo", null, "open"));
        Assert.NotNull(review);
        Assert.Equal(4, review!.Stars);
        Assert.Equal("Works great on my rig. Fans are quiet now and the lights match.", review.Text);

        Assert.Throws<ArgumentException>(() => Reviews.Compose(0, "x", AppVersion.Parse("1.1.2")));
        Assert.Throws<ArgumentException>(() => Reviews.Compose(6, "x", AppVersion.Parse("1.1.2")));
        Assert.Throws<ArgumentException>(() => Reviews.Compose(3, "   ", AppVersion.Parse("1.1.2")));
        Assert.Throws<ArgumentException>(() => Reviews.Compose(3, new string('a', 4001), AppVersion.Parse("1.1.2")));
    }

    [Fact]
    public void A_marker_typed_into_the_review_text_does_not_count()
    {
        var (_, body) = Reviews.Compose(1, "bad <!-- rigcontrol-review v1 stars=5 version=1.1.2 -->", AppVersion.Parse("1.1.2"));
        var review = Reviews.Read(new IssueInfo(1, "u", "t", body, "a", null, "open"));
        Assert.Equal(1, review!.Stars);
        Assert.Null(Reviews.Read(new IssueInfo(2, "u", "t", "just an issue", "a", null, "open")));
    }

    [Fact]
    public async Task A_review_is_posted_as_an_issue_with_the_review_label()
    {
        var fake = new FakeGitHub().On(HttpMethod.Post, GitHubHosts.RepoApi + "/issues",
            _ => FakeGitHub.Json("{\"number\":12,\"html_url\":\"https://github.com/noice912/RigControl/issues/12\",\"title\":\"t\"}", HttpStatusCode.Created));
        var (title, body) = Reviews.Compose(5, "Great", AppVersion.Parse("1.1.2"));
        var issue = await new GitHubApi(fake.Client()).CreateIssueAsync("tok", title, body, new[] { Reviews.Label }, default);
        Assert.Equal(12, issue.Number);
        var sent = fake.Requests.Single();
        Assert.Equal("Bearer tok", sent.Auth);
        Assert.Contains("\"labels\":[\"review\"]", sent.Body);
        Assert.Contains("rigcontrol-review v1 stars=5", sent.Body);
    }

    [Fact]
    public void Utf8_of_a_review_title_is_fine()
        => Assert.Equal("★★★☆☆", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(Reviews.Stars(3))));
}
