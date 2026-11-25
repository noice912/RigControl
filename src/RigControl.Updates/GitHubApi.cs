using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RigControl.Updates;

/// <summary>One file on a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>A release as far as updates need it.</summary>
public sealed record ReleaseInfo(string Tag, string HtmlUrl, string? Body, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A release just made as a draft: where to upload its files.</summary>
public sealed record DraftRelease(long Id, string UploadUrl, string HtmlUrl);

/// <summary>An issue as the review list needs it.</summary>
public sealed record IssueInfo(long Number, string HtmlUrl, string Title, string? Body, string? Author, DateTimeOffset? CreatedAt, string State);

/// <summary>The few GitHub calls RigControl makes, all on noice912/RigControl. Tokens go only into the Authorization header.</summary>
public sealed class GitHubApi
{
    private readonly HttpClient _http;

    public GitHubApi(HttpClient http)
    {
        _http = http;
    }

    public HttpClient Http => _http;

    /// <summary>The signed-in account's name.</summary>
    public async Task<string> GetLoginAsync(string token, CancellationToken ct)
    {
        using var doc = await GitHubHttp.SendJsonAsync(_http, GitHubHttp.Api(HttpMethod.Get, GitHubHosts.ApiBase + "/user", token), ct).ConfigureAwait(false);
        return Str(doc.RootElement, "login") ?? throw new GitHubException("GitHub did not say which account signed in.");
    }

    /// <summary>True when the signed-in account may push to noice912/RigControl (GitHub's "permissions.push").</summary>
    public async Task<bool> CanPushAsync(string token, CancellationToken ct)
    {
        using var doc = await GitHubHttp.SendJsonAsync(_http, GitHubHttp.Api(HttpMethod.Get, GitHubHosts.RepoApi, token), ct).ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Object
               && p.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.True;
    }

    /// <summary>The newest published release (no sign-in needed).</summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GitHubHttp.SendJsonAsync(_http, GitHubHttp.Api(HttpMethod.Get, GitHubHosts.RepoApi + "/releases/latest"), ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    if (Str(a, "name") is { } name && Str(a, "browser_download_url") is { } url && a.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                        assets.Add(new ReleaseAsset(name, url, bytes));
                }
            }
            return new ReleaseInfo(Str(root, "tag_name") ?? "", Str(root, "html_url") ?? "", Str(root, "body"), assets);
        }
        catch (GitHubException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no release yet
        }
    }

    /// <summary>Opens an issue. GitHub drops the labels for people who may not set them; the issue is still made.</summary>
    public async Task<IssueInfo> CreateIssueAsync(string token, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var request = GitHubHttp.Api(HttpMethod.Post, GitHubHosts.RepoApi + "/issues", token);
        request.Content = JsonContent.Create(new { title, body, labels });
        using var doc = await GitHubHttp.SendJsonAsync(_http, request, ct).ConfigureAwait(false);
        return ReadIssue(doc.RootElement);
    }

    /// <summary>Issues of the repository whose text holds <paramref name="phrase"/>, newest first (GitHub's search).</summary>
    public async Task<IReadOnlyList<IssueInfo>> SearchIssuesAsync(string? token, string phrase, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"repo:{GitHubHosts.Owner}/{GitHubHosts.Repo} is:issue in:body \"{phrase}\"");
        var url = GitHubHosts.ApiBase + "/search/issues?q=" + q + "&sort=created&order=desc&per_page=100";
        using var doc = await GitHubHttp.SendJsonAsync(_http, GitHubHttp.Api(HttpMethod.Get, url, token), ct).ConfigureAwait(false);
        var result = new List<IssueInfo>();
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("pull_request", out _)) continue;
                result.Add(ReadIssue(item));
            }
        }
        return result;
    }

    /// <summary>Makes a draft release (not visible to the public until <see cref="PublishReleaseAsync"/>).</summary>
    public async Task<DraftRelease> CreateDraftReleaseAsync(string token, string tag, string name, string body, CancellationToken ct)
    {
        var request = GitHubHttp.Api(HttpMethod.Post, GitHubHosts.RepoApi + "/releases", token);
        request.Content = JsonContent.Create(new { tag_name = tag, name, body, draft = true, prerelease = false });
        using var doc = await GitHubHttp.SendJsonAsync(_http, request, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || Str(root, "upload_url") is not { } upload)
            throw new GitHubException("GitHub's answer to making the release was not what RigControl expected.");
        // "https://uploads.github.com/repos/o/r/releases/1/assets{?name,label}": the part in braces is a template.
        var brace = upload.IndexOf('{');
        return new DraftRelease(id, brace >= 0 ? upload[..brace] : upload, Str(root, "html_url") ?? "");
    }

    /// <summary>Uploads one file to a release.</summary>
    public async Task UploadAssetAsync(string token, DraftRelease release, string filePath, string contentType, CancellationToken ct)
    {
        var name = Path.GetFileName(filePath);
        var url = release.UploadUrl + "?name=" + Uri.EscapeDataString(name);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !GitHubHosts.IsAllowed(uri))
            throw new GitHubException("GitHub gave an upload address RigControl does not use.");
        await using var file = File.OpenRead(filePath);
        var request = GitHubHttp.Api(HttpMethod.Post, url, token);
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = file.Length;
        request.Content = content;
        using var limit = GitHubHttp.Limit(ct, TimeSpan.FromMinutes(30));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException("Uploading " + name + " to GitHub failed: " + ex.Message, null, ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await GitHubHttp.ReadLimitedAsync(response, 64 * 1024, ct).ConfigureAwait(false);
                var e = GitHubHttp.Describe(response, body);
                throw new GitHubException("Uploading " + name + ": " + e.Message, e.Status);
            }
        }
    }

    /// <summary>Makes a draft public and the newest release.</summary>
    public async Task<string> PublishReleaseAsync(string token, long id, CancellationToken ct)
    {
        var request = GitHubHttp.Api(HttpMethod.Patch, GitHubHosts.RepoApi + "/releases/" + id, token);
        request.Content = JsonContent.Create(new { draft = false, make_latest = "true" });
        using var doc = await GitHubHttp.SendJsonAsync(_http, request, ct).ConfigureAwait(false);
        return Str(doc.RootElement, "html_url") ?? "";
    }

    /// <summary>Deletes a release (used only to clear away a draft this app made when publishing failed half-way).</summary>
    public async Task DeleteReleaseAsync(string token, long id, CancellationToken ct)
    {
        using var doc = await GitHubHttp.SendJsonAsync(_http, GitHubHttp.Api(HttpMethod.Delete, GitHubHosts.RepoApi + "/releases/" + id, token), ct).ConfigureAwait(false);
    }

    private static IssueInfo ReadIssue(JsonElement e)
    {
        var number = e.TryGetProperty("number", out var n) && n.TryGetInt64(out var num) ? num : 0;
        string? author = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "login") : null;
        DateTimeOffset? created = Str(e, "created_at") is { } c && DateTimeOffset.TryParse(c, out var d) ? d : null;
        return new IssueInfo(number, Str(e, "html_url") ?? "", Str(e, "title") ?? "", Str(e, "body"), author, created, Str(e, "state") ?? "");
    }

    private static string? Str(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
