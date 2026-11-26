using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RigControl.Updates.Tests;

/// <summary>SemVer test-build versions, the public and test channels, and switching back to the public version.</summary>
public sealed class ChannelTests : IDisposable
{
    private const string Dl = GitHubHosts.ReleaseDownloadPrefix;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-channel-test-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _exe = RandomNumberGenerator.GetBytes(5000);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private string Pub => EcSignature.PublicKeyBase64(_key);

    // ---- versions ----

    [Theory]
    [InlineData("1.1.3-rc.1", true)]
    [InlineData("v1.1.3-rc.1", true)]
    [InlineData("1.1.3-beta", true)]
    [InlineData("1.1.3-alpha.0.x-y", true)]
    [InlineData("1.1.3-", false)]
    [InlineData("1.1.3-rc..1", false)]
    [InlineData("1.1.3-rc.01", false)] // numeric identifiers have no leading zeros
    [InlineData("1.1.3-rc_1", false)]
    [InlineData("1.1.3-rc.1+abc", false)] // build metadata is not a version here
    [InlineData("1.1-rc.1", false)]
    public void Pre_release_versions_parse_strictly(string text, bool ok) => Assert.Equal(ok, AppVersion.TryParse(text, out _));

    [Fact]
    public void A_pre_release_part_longer_than_40_characters_is_refused()
    {
        Assert.True(AppVersion.TryParse("1.0.0-" + new string('a', 40), out _));
        Assert.False(AppVersion.TryParse("1.0.0-" + new string('a', 41), out _));
    }

    [Fact]
    public void Versions_order_like_SemVer()
    {
        var ordered = new[]
        {
            "1.1.2", "1.1.3-alpha", "1.1.3-alpha.1", "1.1.3-alpha.beta", "1.1.3-beta", "1.1.3-beta.2", "1.1.3-beta.11",
            "1.1.3-rc.1", "1.1.3-rc.2", "1.1.3-rc.10", "1.1.3", "1.1.4-rc.1", "1.2.0",
        }.Select(AppVersion.Parse).ToArray();
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(ordered[i] < ordered[i + 1], ordered[i] + " < " + ordered[i + 1]);
            Assert.True(ordered[i + 1] > ordered[i]);
        }
        var shuffled = Enumerable.Reverse(ordered).ToList();
        shuffled.Sort();
        Assert.Equal(ordered, shuffled);
    }

    [Fact]
    public void Build_metadata_is_ignored_and_names_carry_the_pre_release_part()
    {
        Assert.True(AppVersion.TryParseLoose("1.1.3-rc.1+4b970f9d", out var loose));
        Assert.Equal(AppVersion.Parse("1.1.3-rc.1"), loose!.Value);
        Assert.True(AppVersion.TryParseLoose("1.1.3+abc", out var release));
        Assert.Equal(AppVersion.Parse("1.1.3"), release!.Value);
        Assert.Equal(0, AppVersion.Parse("1.1.3-rc.1").CompareTo(loose.Value));

        var v = AppVersion.Parse("v1.1.3-rc.1");
        Assert.True(v.IsPreRelease);
        Assert.Equal("1.1.3-rc.1", v.ToString());
        Assert.Equal("v1.1.3-rc.1", v.Tag);
        Assert.Equal("RigControl-Setup-v1.1.3-rc.1.exe", v.SetupFileName);
        Assert.Equal(AppVersion.Parse("1.1.3"), v.Core);
        Assert.False(AppVersion.Parse("1.1.3").IsPreRelease);
        Assert.NotEqual(AppVersion.Parse("1.1.3"), v);
    }

    [Fact]
    public void A_signed_manifest_with_a_pre_release_version_reads_back()
    {
        var v = AppVersion.Parse("1.1.3-rc.2");
        var manifest = new UpdateManifest(v, v.SetupFileName, new string('a', 64), 10, DateTimeOffset.UtcNow);
        var json = manifest.ToJsonBytes();
        var read = UpdateManifest.ParseSigned(json, EcSignature.Sign(json, _key), Pub);
        Assert.Equal(v, read.Version);
        Assert.Equal("RigControl-Setup-v1.1.3-rc.2.exe", read.File);
    }

    [Fact]
    public void A_review_from_a_test_build_keeps_its_version()
    {
        var (_, body) = Reviews.Compose(4, "Testing the rc", AppVersion.Parse("1.1.3-rc.1"));
        var review = Reviews.Read(new IssueInfo(9, "https://github.com/noice912/RigControl/issues/9", "t", body, "octo", null, "open"));
        Assert.NotNull(review);
        Assert.Equal("1.1.3-rc.1", review!.Version);
        Assert.Equal("Testing the rc", review.Text);
    }

    // ---- channels with fake releases ----

    private sealed record Fake(string Version, bool Draft = false, bool Prerelease = false, bool Unsigned = false, ECDsa? Signer = null);

    /// <summary>JSON of one release, with its files answered on <paramref name="net"/>.</summary>
    private string AddRelease(FakeGitHub net, Fake r)
    {
        var v = AppVersion.Parse(r.Version);
        var t = v.Tag;
        var manifest = new UpdateManifest(v, v.SetupFileName, Convert.ToHexString(SHA256.HashData(_exe)).ToLowerInvariant(), _exe.Length, DateTimeOffset.UtcNow);
        var json = manifest.ToJsonBytes();
        var sig = Encoding.ASCII.GetBytes(EcSignature.Sign(json, r.Signer ?? _key));
        net.On(HttpMethod.Get, Dl + t + "/update.json", _ => FakeGitHub.Bytes(json))
           .On(HttpMethod.Get, Dl + t + "/update.json.sig", _ => FakeGitHub.Bytes(sig))
           .On(HttpMethod.Get, Dl + t + "/" + v.SetupFileName, _ => FakeGitHub.Bytes(_exe));
        var signed = r.Unsigned ? "" : $$"""
            {"name":"update.json","browser_download_url":"{{Dl}}{{t}}/update.json","size":{{json.Length}}},
            {"name":"update.json.sig","browser_download_url":"{{Dl}}{{t}}/update.json.sig","size":{{sig.Length}}},
            """;
        return $$"""
            {"tag_name":"{{t}}","html_url":"https://github.com/noice912/RigControl/releases/tag/{{t}}","body":"notes {{t}}",
             "draft":{{(r.Draft ? "true" : "false")}},"prerelease":{{(r.Prerelease ? "true" : "false")}},"assets":[
              {{signed}}
              {"name":"{{v.SetupFileName}}","browser_download_url":"{{Dl}}{{t}}/{{v.SetupFileName}}","size":{{_exe.Length}}}]}
            """;
    }

    /// <summary>A repository with these releases (listed in this order) and "latest" = <paramref name="latest"/>.</summary>
    private FakeGitHub Repo(string? latest, params Fake[] releases)
    {
        var net = new FakeGitHub();
        var items = releases.Select(r => (r, Json: AddRelease(net, r))).ToList();
        net.On(HttpMethod.Get, GitHubHosts.RepoApi + "/releases?per_page=30", _ => FakeGitHub.Json("[" + string.Join(",", items.Select(i => i.Json)) + "]"));
        var latestJson = items.FirstOrDefault(i => i.r.Version == latest).Json;
        if (latestJson is not null) net.On(HttpMethod.Get, GitHubHosts.RepoApi + "/releases/latest", _ => FakeGitHub.Json(latestJson));
        return net;
    }

    private UpdateChecker Checker(FakeGitHub net) => new(new GitHubApi(net.Client()), Pub);

    private static AppVersion V(string s) => AppVersion.Parse(s);

    [Fact]
    public async Task The_test_channel_takes_the_newest_signed_release_including_pre_releases()
    {
        var net = Repo("1.1.2",
            new Fake("1.1.2"),
            new Fake("1.1.3-rc.2", Prerelease: true),
            new Fake("1.1.3-rc.10", Prerelease: true),
            new Fake("1.1.3-rc.1", Prerelease: true));
        var result = await Checker(net).CheckAsync(V("1.1.2"), UpdateChannel.Test, switchBack: false, default);

        Assert.Equal("1.1.3-rc.10", result.Offer?.Manifest.Version.ToString());
        Assert.False(result.Offer!.IsDowngrade);
        Assert.Contains(net.Requests, r => r.Uri.AbsoluteUri == GitHubHosts.RepoApi + "/releases?per_page=30");
        Assert.All(net.Requests, r => Assert.Null(r.Auth));
        // The offer downloads and checks like any other.
        var path = await Checker(net).DownloadAsync(result.Offer, _dir, null, default);
        Assert.Equal("RigControl-Setup-v1.1.3-rc.10.exe", Path.GetFileName(path));
    }

    [Fact]
    public async Task The_test_channel_skips_drafts_unsigned_and_wrongly_signed_releases()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var net = Repo("1.1.2",
            new Fake("2.0.0", Draft: true),
            new Fake("1.9.0-rc.1", Prerelease: true, Unsigned: true),
            new Fake("1.8.0-rc.1", Prerelease: true, Signer: other),
            new Fake("1.1.3-rc.1", Prerelease: true),
            new Fake("1.1.2"));
        var result = await Checker(net).CheckAsync(V("1.1.2"), UpdateChannel.Test, switchBack: false, default);

        Assert.Equal("1.1.3-rc.1", result.Offer?.Manifest.Version.ToString());
        // The draft's files were never even asked for.
        Assert.DoesNotContain(net.Requests, r => r.Uri.AbsoluteUri.Contains("v2.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_test_channel_offers_a_public_release_newer_than_the_test_build()
    {
        var net = Repo("1.1.3", new Fake("1.1.3-rc.2", Prerelease: true), new Fake("1.1.3"));
        var result = await Checker(net).CheckAsync(V("1.1.3-rc.2"), UpdateChannel.Test, switchBack: false, default);
        Assert.Equal("1.1.3", result.Offer?.Manifest.Version.ToString());
    }

    [Fact]
    public async Task The_test_channel_never_offers_an_older_version()
    {
        var net = Repo("1.1.2", new Fake("1.1.2"), new Fake("1.1.3-rc.1", Prerelease: true));
        var result = await Checker(net).CheckAsync(V("1.1.3-rc.2"), UpdateChannel.Test, switchBack: true, default);
        Assert.Null(result.Offer);
        Assert.Equal("1.1.3-rc.1", result.LatestVersion);
    }

    [Fact]
    public async Task The_test_channel_with_nothing_signed_offers_nothing()
    {
        var net = Repo(null, new Fake("1.1.3-rc.1", Prerelease: true, Unsigned: true), new Fake("1.1.4", Draft: true));
        var result = await Checker(net).CheckAsync(V("1.1.2"), UpdateChannel.Test, switchBack: false, default);
        Assert.Null(result.Offer);
        Assert.Contains("v1.1.3-rc.1", result.Note);
    }

    [Fact]
    public async Task The_public_channel_ignores_pre_releases()
    {
        var net = Repo("1.1.2", new Fake("1.1.3-rc.1", Prerelease: true), new Fake("1.1.2"));
        var result = await Checker(net).CheckAsync(V("1.1.1"), UpdateChannel.Public, switchBack: false, default);
        Assert.Equal("1.1.2", result.Offer?.Manifest.Version.ToString());
        // Only "latest" is asked, never the list.
        Assert.DoesNotContain(net.Requests, r => r.Uri.AbsoluteUri.Contains("per_page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_public_channel_refuses_a_latest_release_marked_or_signed_as_a_pre_release()
    {
        // GitHub never returns a pre-release as "latest", but if it did, nothing is offered.
        var marked = Repo("1.1.3", new Fake("1.1.3", Prerelease: true));
        Assert.Null((await Checker(marked).CheckAsync(V("1.1.2"), default)).Offer);

        var signedPre = Repo("1.1.3-rc.1", new Fake("1.1.3-rc.1"));
        var result = await Checker(signedPre).CheckAsync(V("1.1.2"), default);
        Assert.Null(result.Offer);
        Assert.Contains("development build", result.Note);
    }

    [Fact]
    public async Task A_test_build_gets_the_public_release_with_the_same_numbers_as_a_normal_update()
    {
        var net = Repo("1.1.3", new Fake("1.1.3"));
        var result = await Checker(net).CheckAsync(V("1.1.3-rc.2"), UpdateChannel.Public, switchBack: false, default);
        Assert.Equal("1.1.3", result.Offer?.Manifest.Version.ToString());
        Assert.False(result.Offer!.IsDowngrade);
    }

    [Fact]
    public async Task An_older_public_version_is_offered_only_when_switching_back()
    {
        var net = Repo("1.1.2", new Fake("1.1.2"), new Fake("1.1.3-rc.1", Prerelease: true));
        var checker = Checker(net);

        var normal = await checker.CheckAsync(V("1.1.3-rc.1"), UpdateChannel.Public, switchBack: false, default);
        Assert.Null(normal.Offer);
        Assert.Equal("1.1.2", normal.LatestVersion);

        var back = await checker.CheckAsync(V("1.1.3-rc.1"), UpdateChannel.Public, switchBack: true, default);
        Assert.Equal("1.1.2", back.Offer?.Manifest.Version.ToString());
        Assert.True(back.Offer!.IsDowngrade);

        // Nothing to switch back to when this copy already is the public version.
        Assert.Null((await checker.CheckAsync(V("1.1.2"), UpdateChannel.Public, switchBack: true, default)).Offer);
        // The plain call never downgrades.
        Assert.Null((await checker.CheckAsync(V("1.1.3-rc.1"), default)).Offer);
    }

    [Fact]
    public async Task A_switch_back_still_checks_the_signature()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var net = Repo("1.1.2", new Fake("1.1.2", Signer: other));
        await Assert.ThrowsAsync<UpdateException>(() => Checker(net).CheckAsync(V("1.1.3-rc.1"), UpdateChannel.Public, switchBack: true, default));
    }

    [Fact]
    public async Task Listing_releases_reads_the_draft_and_pre_release_marks()
    {
        var net = Repo(null, new Fake("1.1.3-rc.1", Prerelease: true), new Fake("1.1.4", Draft: true), new Fake("1.1.2"));
        var list = await new GitHubApi(net.Client()).ListReleasesAsync(default);
        Assert.Equal(new[] { "v1.1.3-rc.1", "v1.1.4", "v1.1.2" }, list.Select(r => r.Tag));
        Assert.Equal(new[] { true, false, false }, list.Select(r => r.Prerelease));
        Assert.Equal(new[] { false, true, false }, list.Select(r => r.Draft));
    }

    // ---- publishing a test build ----

    [Theory]
    [InlineData("1.1.3-rc.1", true, true)]
    [InlineData("1.1.3", false, true)]
    [InlineData("1.1.3", true, false)]
    [InlineData("1.1.3-rc.1", false, false)]
    public void A_test_build_needs_a_pre_release_version_and_a_public_one_must_not_have_one(string version, bool testBuild, bool ok)
    {
        var problem = ReleasePublisher.ChannelProblem(V(version), testBuild);
        Assert.Equal(ok, problem is null);
        if (!ok) Assert.Contains(testBuild ? "pre-release version, like 1.1.3-rc.1" : "only be published as a development build", problem);
    }

    private FakeGitHub PublishFake()
        => new FakeGitHub()
            .On(HttpMethod.Post, GitHubHosts.RepoApi + "/releases", _ => FakeGitHub.Json(
                "{\"id\":7,\"upload_url\":\"https://uploads.github.com/repos/noice912/RigControl/releases/7/assets{?name,label}\",\"html_url\":\"https://github.com/noice912/RigControl/releases/tag/untagged\"}",
                HttpStatusCode.Created))
            .OnPrefix(HttpMethod.Post, "https://uploads.github.com/repos/noice912/RigControl/releases/7/assets?name=", _ => FakeGitHub.Json("{}", HttpStatusCode.Created))
            .On(HttpMethod.Patch, GitHubHosts.RepoApi + "/releases/7", _ => FakeGitHub.Json("{\"html_url\":\"https://github.com/noice912/RigControl/releases/tag/v1.1.3-rc.1\"}"));

    private string WriteExe()
    {
        Directory.CreateDirectory(_dir);
        var exe = Path.Combine(_dir, "RigControl.exe");
        File.WriteAllBytes(exe, _exe);
        return exe;
    }

    [Fact]
    public async Task A_test_build_is_published_as_a_pre_release_that_says_so()
    {
        var net = PublishFake();
        var publisher = new ReleasePublisher(new GitHubApi(net.Client()), Pub);
        await publisher.PublishAsync("tok", WriteExe(), V("1.1.3-rc.1"), "Try the new fan curve.", _key.ExportPkcs8PrivateKeyPem(), testBuild: true, null, default);

        var create = net.Requests[0].Body!;
        Assert.Contains("\"tag_name\":\"v1.1.3-rc.1\"", create);
        Assert.Contains("\"prerelease\":true", create);
        Assert.Contains("Development build (pre-release)", create);
        Assert.Contains("Try the new fan curve.", create);
        var uploads = net.Requests.Where(r => r.Uri.Host == "uploads.github.com").Select(r => Uri.UnescapeDataString(r.Uri.Query)).ToList();
        Assert.Contains("?name=RigControl-Setup-v1.1.3-rc.1.exe", uploads);
        // A pre-release never becomes GitHub's latest release (the public channel).
        Assert.Contains("\"make_latest\":\"false\"", net.Requests.Single(r => r.Method == HttpMethod.Patch).Body);
    }

    [Fact]
    public async Task A_public_update_is_not_a_pre_release_and_becomes_the_latest()
    {
        var net = PublishFake();
        var publisher = new ReleasePublisher(new GitHubApi(net.Client()), Pub);
        await publisher.PublishAsync("tok", WriteExe(), V("1.1.3"), null, _key.ExportPkcs8PrivateKeyPem(), testBuild: false, null, default);
        Assert.Contains("\"prerelease\":false", net.Requests[0].Body);
        Assert.DoesNotContain("Development build", net.Requests[0].Body);
        Assert.Contains("\"make_latest\":\"true\"", net.Requests.Single(r => r.Method == HttpMethod.Patch).Body);
    }

    [Fact]
    public async Task A_wrong_tick_box_and_version_pair_is_refused_before_GitHub_is_asked()
    {
        var net = PublishFake();
        var publisher = new ReleasePublisher(new GitHubApi(net.Client()), Pub);
        var pem = _key.ExportPkcs8PrivateKeyPem();
        await Assert.ThrowsAsync<UpdateException>(() => publisher.PublishAsync("tok", WriteExe(), V("1.1.3"), null, pem, testBuild: true, null, default));
        await Assert.ThrowsAsync<UpdateException>(() => publisher.PublishAsync("tok", WriteExe(), V("1.1.3-rc.1"), null, pem, testBuild: false, null, default));
        await Assert.ThrowsAsync<UpdateException>(() => publisher.PublishAsync("tok", WriteExe(), V("1.1.3-rc.1"), null, pem, null, default));
        Assert.Empty(net.Requests);
    }
}
