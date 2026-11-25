using System.Net;
using System.Security.Cryptography;

namespace RigControl.Updates.Tests;

public sealed class PublisherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-publish-test-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public PublisherTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Exe, RandomNumberGenerator.GetBytes(3000));
    }

    private string Exe => Path.Combine(_dir, "RigControl.exe");

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private static FakeGitHub GitHub(bool failUpload = false)
    {
        var fake = new FakeGitHub()
            .On(HttpMethod.Post, GitHubHosts.RepoApi + "/releases", _ => FakeGitHub.Json(
                "{\"id\":42,\"upload_url\":\"https://uploads.github.com/repos/noice912/RigControl/releases/42/assets{?name,label}\",\"html_url\":\"https://github.com/noice912/RigControl/releases/tag/untagged\"}",
                HttpStatusCode.Created))
            .OnPrefix(HttpMethod.Post, "https://uploads.github.com/repos/noice912/RigControl/releases/42/assets?name=", r =>
                failUpload && r.RequestUri!.Query.Contains("update.json.sig")
                    ? FakeGitHub.Json("{\"message\":\"Server Error\"}", HttpStatusCode.InternalServerError)
                    : FakeGitHub.Json("{}", HttpStatusCode.Created))
            .On(HttpMethod.Patch, GitHubHosts.RepoApi + "/releases/42", _ => FakeGitHub.Json("{\"html_url\":\"https://github.com/noice912/RigControl/releases/tag/v1.2.0\"}"))
            .On(HttpMethod.Delete, GitHubHosts.RepoApi + "/releases/42", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        return fake;
    }

    [Fact]
    public async Task Publishing_makes_a_draft_uploads_four_files_then_publishes()
    {
        var fake = GitHub();
        var publisher = new ReleasePublisher(new GitHubApi(fake.Client()), EcSignature.PublicKeyBase64(_key));
        var result = await publisher.PublishAsync("tok", Exe, AppVersion.Parse("1.2.0"), "Fixes", _key.ExportPkcs8PrivateKeyPem(), null, default);

        Assert.Equal("https://github.com/noice912/RigControl/releases/tag/v1.2.0", result.ReleaseUrl);
        var create = fake.Requests[0];
        Assert.Contains("\"tag_name\":\"v1.2.0\"", create.Body);
        Assert.Contains("\"draft\":true", create.Body);
        var uploads = fake.Requests.Where(r => r.Uri.Host == "uploads.github.com").Select(r => Uri.UnescapeDataString(r.Uri.Query)).ToList();
        Assert.Equal(new[] { "?name=RigControl-Setup-v1.2.0.exe", "?name=update.json", "?name=update.json.sig", "?name=SHA256SUMS.txt" }, uploads);
        Assert.Contains("\"draft\":false", fake.Requests.Single(r => r.Method == HttpMethod.Patch).Body);
        Assert.All(fake.Requests, r => Assert.Equal("Bearer tok", r.Auth));
    }

    [Fact]
    public async Task A_key_the_app_does_not_trust_is_refused_before_GitHub_is_asked()
    {
        var fake = GitHub();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = new ReleasePublisher(new GitHubApi(fake.Client()), EcSignature.PublicKeyBase64(_key));
        await Assert.ThrowsAsync<UpdateException>(() =>
            publisher.PublishAsync("tok", Exe, AppVersion.Parse("1.2.0"), null, other.ExportPkcs8PrivateKeyPem(), null, default));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task A_failed_upload_deletes_the_draft_and_says_why()
    {
        var fake = GitHub(failUpload: true);
        var publisher = new ReleasePublisher(new GitHubApi(fake.Client()), EcSignature.PublicKeyBase64(_key));
        var ex = await Assert.ThrowsAsync<GitHubException>(() =>
            publisher.PublishAsync("tok", Exe, AppVersion.Parse("1.2.0"), null, _key.ExportPkcs8PrivateKeyPem(), null, default));
        Assert.Contains("Server Error", ex.Message);
        Assert.Contains("deleted", ex.Message);
        Assert.Contains(fake.Requests, r => r.Method == HttpMethod.Delete);
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Patch);
    }
}
