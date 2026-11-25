using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RigControl.Updates;

/// <summary>GitHub said no, or could not be reached. The message is fit to show the user and never holds a token.</summary>
public sealed class GitHubException : Exception
{
    public HttpStatusCode? Status { get; }

    public GitHubException(string message, HttpStatusCode? status = null, Exception? inner = null) : base(message, inner)
    {
        Status = status;
    }
}

/// <summary>The only places RigControl's update, sign-in and review code ever connects to.</summary>
public static class GitHubHosts
{
    public const string Owner = "noice912";
    public const string Repo = "RigControl";
    public const string ApiBase = "https://api.github.com";
    public const string RepoApi = ApiBase + "/repos/" + Owner + "/" + Repo;

    /// <summary>Where a release's files are downloaded from (GitHub then sends the download on to its file hosts).</summary>
    public const string ReleaseDownloadPrefix = "https://github.com/" + Owner + "/" + Repo + "/releases/download/";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "uploads.github.com",
        // GitHub's own hosts that release downloads are sent on to.
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    /// <summary>https, a GitHub host from the list, the standard port, no user name or password in the address.</summary>
    public static bool IsAllowed(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.IsDefaultPort
           && string.IsNullOrEmpty(uri.UserInfo)
           && Allowed.Contains(uri.IdnHost);
}

/// <summary>
/// Sits in front of every connection: refuses any address that is not one of <see cref="GitHubHosts"/>, and follows
/// redirects itself (at most five) so that each new address is checked too. The sign-in is never carried to another host.
/// </summary>
public sealed class GitHubOnlyHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    public GitHubOnlyHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request;
        for (var hop = 0; ; hop++)
        {
            if (!GitHubHosts.IsAllowed(current.RequestUri))
                throw new GitHubException("RigControl only connects to GitHub, so it did not go to " + (current.RequestUri?.Host ?? "an unknown address") + ".");
            var response = await base.SendAsync(current, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null) return response;

            var next = new Uri(current.RequestUri!, response.Headers.Location);
            response.Dispose();
            if (hop >= MaxRedirects) throw new GitHubException("GitHub sent the download round in circles, so it was stopped.");
            // Only downloads are redirected; anything that sends data is never sent again somewhere else.
            if (current.Method != HttpMethod.Get && current.Method != HttpMethod.Head)
                throw new GitHubException("GitHub redirected a request that sends data, so it was stopped.");

            var follow = new HttpRequestMessage(current.Method, next);
            foreach (var h in current.Headers)
            {
                if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(next.IdnHost, current.RequestUri!.IdnHost, StringComparison.OrdinalIgnoreCase))
                    continue; // never hand the sign-in to another host
                follow.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            current = follow;
        }
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

/// <summary>Makes the one HTTP client the GitHub code uses. Tests pass their own handler; nothing then goes on the network.</summary>
public static class GitHubHttp
{
    public const string UserAgent = "RigControl-updater";
    public const string ApiVersion = "2022-11-28";

    /// <summary>
    /// A client whose every request passes <see cref="GitHubOnlyHandler"/>. <paramref name="inner"/> is the real network
    /// (a handler that does not follow redirects by itself) unless a test gives a fake.
    /// </summary>
    public static HttpClient CreateClient(HttpMessageHandler? inner = null)
    {
        inner ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(new GitHubOnlyHandler(inner))
        {
            // Downloads are long; each API call sets its own, shorter limit (Limit below).
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>A request to GitHub's API, with the headers GitHub asks for. <paramref name="token"/> is only ever put in the Authorization header.</summary>
    public static HttpRequestMessage Api(HttpMethod method, string url, string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>A token that ends <paramref name="ct"/> or after <paramref name="limit"/>, whichever comes first.</summary>
    public static CancellationTokenSource Limit(CancellationToken ct, TimeSpan limit)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        return cts;
    }

    /// <summary>Sends a request and reads a JSON answer; GitHub's own error text becomes a <see cref="GitHubException"/>.</summary>
    public static async Task<JsonDocument> SendJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct, int maxBytes = 4 * 1024 * 1024)
    {
        using var limit = Limit(ct, TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException("GitHub could not be reached: " + ex.Message, null, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GitHubException("GitHub did not answer in time.", null, ex);
        }
        using (response)
        {
            var body = await ReadLimitedAsync(response, maxBytes, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Describe(response, body);
            try
            {
                return JsonDocument.Parse(body.Length == 0 ? "{}"u8.ToArray() : body);
            }
            catch (JsonException ex)
            {
                throw new GitHubException("GitHub's answer could not be read.", response.StatusCode, ex);
            }
        }
    }

    /// <summary>Reads a body, refusing one larger than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new GitHubException("GitHub's answer was larger than expected, so it was not read.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new GitHubException("GitHub's answer was larger than expected, so it was not read.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>GitHub's own words for a failed call, plainly: its message, the details it lists, and what the code means.</summary>
    public static GitHubException Describe(HttpResponseMessage response, byte[] body)
    {
        string? message = null;
        var details = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in errs.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String) { details.Add(e.GetString()!); continue; }
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        var parts = new[] { "message", "field", "code" }
                            .Select(n => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                            .Where(s => !string.IsNullOrEmpty(s));
                        details.Add(string.Join(" ", parts));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's page, say): the status code alone has to do.
        }

        var code = (int)response.StatusCode;
        var said = message is null ? "" : " GitHub said: \"" + message + "\"" + (details.Count > 0 ? " (" + string.Join("; ", details) + ")" : "") + ".";
        var meaning = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub no longer accepts the sign-in. Sign in with GitHub again.",
            HttpStatusCode.Forbidden when RateLimited(response) => "GitHub's limit on requests was reached. Try again later.",
            HttpStatusCode.Forbidden => "GitHub says this account may not do that.",
            HttpStatusCode.NotFound => "GitHub could not find it, or this account may not see it.",
            HttpStatusCode.UnprocessableEntity => "GitHub refused what was sent.",
            _ => "GitHub answered with an error.",
        };
        return new GitHubException(meaning + said + " (HTTP " + code + ")", response.StatusCode);
    }

    private static bool RateLimited(HttpResponseMessage response)
        => response.Headers.TryGetValues("x-ratelimit-remaining", out var v) && v.FirstOrDefault() == "0";
}
