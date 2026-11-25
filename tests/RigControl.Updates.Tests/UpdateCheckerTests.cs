using System.Net;
using System.Security.Cryptography;

namespace RigControl.Updates.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private const string Dl = GitHubHosts.ReleaseDownloadPrefix;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-check-test-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _exe = RandomNumberGenerator.GetBytes(20000);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private string Pub => EcSignature.PublicKeyBase64(_key);

    /// <summary>A fake latest release v<paramref name="version"/>, signed with the test key unless <paramref name="signer"/> is given.</summary>
    private FakeGitHub Release(string version, ECDsa? signer = null, string? tag = null, byte[]? served = null, string? exeUrl = null)
    {
        var v = AppVersion.Parse(version);
        var manifest = new UpdateManifest(v, v.SetupFileName, Convert.ToHexString(SHA256.HashData(_exe)).ToLowerInvariant(), _exe.Length, DateTimeOffset.UtcNow);
        var json = manifest.ToJsonBytes();
        var sig = System.Text.Encoding.ASCII.GetBytes(EcSignature.Sign(json, signer ?? _key));
        var t = tag ?? v.Tag;
        var exeLink = exeUrl ?? Dl + t + "/" + v.SetupFileName;
        var release = $$"""
            {"tag_name":"{{t}}","html_url":"https://github.com/noice912/RigControl/releases/tag/{{t}}","body":"notes","assets":[
              {"name":"update.json","browser_download_url":"{{Dl}}{{t}}/update.json","size":{{json.Length}}},
              {"name":"update.json.sig","browser_download_url":"{{Dl}}{{t}}/update.json.sig","size":{{sig.Length}}},
              {"name":"{{v.SetupFileName}}","browser_download_url":"{{exeLink}}","size":{{_exe.Length}}}]}
            """;
        return new FakeGitHub()
            .On(HttpMethod.Get, GitHubHosts.RepoApi + "/releases/latest", _ => FakeGitHub.Json(release))
            // Like GitHub: the download address sends the request on to its file host.
            .On(HttpMethod.Get, Dl + t + "/update.json", _ => FakeGitHub.Redirect("https://release-assets.githubusercontent.com/x/update.json"))
            .On(HttpMethod.Get, "https://release-assets.githubusercontent.com/x/update.json", _ => FakeGitHub.Bytes(json))
            .On(HttpMethod.Get, Dl + t + "/update.json.sig", _ => FakeGitHub.Bytes(sig))
            .On(HttpMethod.Get, Dl + t + "/" + v.SetupFileName, _ => FakeGitHub.Redirect("https://objects.githubusercontent.com/x/setup"))
            .On(HttpMethod.Get, "https://objects.githubusercontent.com/x/setup", _ => FakeGitHub.Bytes(served ?? _exe));
    }

    private UpdateChecker Checker(FakeGitHub fake) => new(new GitHubApi(fake.Client()), Pub);

    [Fact]
    public async Task A_newer_signed_release_is_offered_and_downloads_checked()
    {
        var fake = Release("1.2.0");
        var checker = Checker(fake);
        var result = await checker.CheckAsync(AppVersion.Parse("1.1.2"), default);

        Assert.NotNull(result.Offer);
        Assert.Equal("1.2.0", result.LatestVersion);
        var path = await checker.DownloadAsync(result.Offer!, _dir, null, default);
        Assert.Equal(Path.Combine(_dir, "RigControl-Setup-v1.2.0.exe"), path);
        Assert.Equal(_exe, File.ReadAllBytes(path));
        Assert.True(UpdateChecker.StillMatches(path, result.Offer!.Manifest));
        // No sign-in is ever sent for updates.
        Assert.All(fake.Requests, r => Assert.Null(r.Auth));
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_not_offered()
    {
        Assert.Null((await Checker(Release("1.1.2")).CheckAsync(AppVersion.Parse("1.1.2"), default)).Offer);
        Assert.Null((await Checker(Release("1.0.0")).CheckAsync(AppVersion.Parse("1.1.2"), default)).Offer);
    }

    [Fact]
    public async Task A_release_signed_with_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<UpdateException>(() => Checker(Release("9.0.0", signer: other)).CheckAsync(AppVersion.Parse("1.1.2"), default));
    }

    [Fact]
    public async Task A_tag_that_does_not_match_the_signed_version_is_refused()
    {
        await Assert.ThrowsAsync<UpdateException>(() => Checker(Release("1.2.0", tag: "v9.9.9")).CheckAsync(AppVersion.Parse("1.1.2"), default));
    }

    [Fact]
    public async Task A_setup_file_outside_RigControls_releases_is_refused()
    {
        var fake = Release("1.2.0", exeUrl: "https://github.com/someone/else/releases/download/v1.2.0/RigControl-Setup-v1.2.0.exe");
        await Assert.ThrowsAsync<UpdateException>(() => Checker(fake).CheckAsync(AppVersion.Parse("1.1.2"), default));
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_signed_hash_is_deleted()
    {
        var wrong = (byte[])_exe.Clone();
        wrong[100] ^= 0xFF;
        var checker = Checker(Release("1.2.0", served: wrong));
        var offer = (await checker.CheckAsync(AppVersion.Parse("1.1.2"), default)).Offer!;

        await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(offer, _dir, null, default));
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task A_release_without_signed_information_is_not_offered()
    {
        var fake = new FakeGitHub().On(HttpMethod.Get, GitHubHosts.RepoApi + "/releases/latest",
            _ => FakeGitHub.Json("{\"tag_name\":\"v5.0.0\",\"assets\":[]}"));
        var result = await Checker(fake).CheckAsync(AppVersion.Parse("1.1.2"), default);
        Assert.Null(result.Offer);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public async Task Nothing_but_GitHub_is_ever_contacted()
    {
        var fake = new FakeGitHub()
            .On(HttpMethod.Get, GitHubHosts.RepoApi + "/releases/latest", _ => FakeGitHub.Redirect("https://example.com/steal"));
        var ex = await Assert.ThrowsAsync<GitHubException>(() => new GitHubApi(fake.Client()).GetLatestReleaseAsync(default));
        Assert.Contains("example.com", ex.Message);
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Host == "example.com");

        using var client = fake.Client();
        await Assert.ThrowsAsync<GitHubException>(() => client.GetAsync("http://api.github.com/"));
        await Assert.ThrowsAsync<GitHubException>(() => client.GetAsync("https://api.github.com:8443/"));
        await Assert.ThrowsAsync<GitHubException>(() => client.GetAsync("https://github.com.evil.test/"));
    }

    [Fact]
    public async Task The_sign_in_is_not_carried_to_another_host_on_a_redirect()
    {
        var fake = new FakeGitHub()
            .On(HttpMethod.Get, "https://api.github.com/a", _ => FakeGitHub.Redirect("https://objects.githubusercontent.com/b"))
            .On(HttpMethod.Get, "https://objects.githubusercontent.com/b", _ => FakeGitHub.Json("{}"));
        using var client = fake.Client();
        using var request = GitHubHttp.Api(HttpMethod.Get, "https://api.github.com/a", "secret-token");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer secret-token", fake.Requests[0].Auth);
        Assert.Null(fake.Requests[1].Auth);
    }

    [Fact]
    public async Task GitHubs_own_error_text_is_shown()
    {
        var fake = new FakeGitHub().On(HttpMethod.Post, GitHubHosts.RepoApi + "/issues",
            _ => FakeGitHub.Json("{\"message\":\"Validation Failed\",\"errors\":[{\"field\":\"title\",\"code\":\"missing\"}]}", HttpStatusCode.UnprocessableEntity));
        var ex = await Assert.ThrowsAsync<GitHubException>(() => new GitHubApi(fake.Client()).CreateIssueAsync("t", "x", "y", new[] { "review" }, default));
        Assert.Contains("Validation Failed", ex.Message);
        Assert.Contains("title", ex.Message);
        Assert.Contains("422", ex.Message);
        Assert.DoesNotContain("Bearer", ex.Message);
    }
}
