using System.Net;
using System.Text;

namespace RigControl.Updates.Tests;

/// <summary>Stands in for the network: answers by URL, records every request. Nothing leaves the test.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = new();

    public List<(HttpMethod Method, Uri Uri, string? Auth, string? Body)> Requests { get; } = new();

    public FakeGitHub On(HttpMethod method, string url, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes.Add((r => r.Method == method && r.RequestUri!.AbsoluteUri == url, answer));
        return this;
    }

    public FakeGitHub OnPrefix(HttpMethod method, string prefix, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes.Add((r => r.Method == method && r.RequestUri!.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal), answer));
        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = null;
        if (request.Content is not null && request.Content is not StreamContent) body = await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        foreach (var (match, answer) in _routes)
            if (match(request)) return answer(request);
        return Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound);
    }

    public HttpClient Client() => GitHubHttp.CreateClient(this);
}
