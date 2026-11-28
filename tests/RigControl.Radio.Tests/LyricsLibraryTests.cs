using System.Net;
using System.Text;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Lyrics;

namespace RigControl.Radio.Tests;

/// <summary>Pretend lrclib.net: /api/get answers from a table, everything else 404.</summary>
internal sealed class FakeLrclib : HttpMessageHandler
{
    public Dictionary<string, string> Songs { get; } = new(StringComparer.OrdinalIgnoreCase); // "title|artist" -> json
    public List<Uri> Calls { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Calls) Calls.Add(request.RequestUri!);
        if (Override?.Invoke(request) is { } o) return Task.FromResult(o);
        var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        if (request.RequestUri.AbsolutePath == "/api/get" && Songs.TryGetValue(q["track_name"] + "|" + q["artist_name"], out var json))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        if (request.RequestUri.AbsolutePath == "/api/search")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static string Json(string title, string artist, string synced, double duration = 200) =>
        System.Text.Json.JsonSerializer.Serialize(new { trackName = title, artistName = artist, duration, instrumental = false, plainLyrics = "", syncedLyrics = synced });
}

public class LyricsLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-lyrics-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLrclib _fake = new();
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    public LyricsLibraryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private LyricsLibrary Make() => new(new HttpClient(_fake), Path.Combine(_dir, "radio", "lyrics.json"), new ListLog(), () => _now, pause: TimeSpan.Zero);

    private static readonly CatalogueSong[] Songs =
    [
        new("", "Electric Feel", "MGMT"),
        new("", "Break Free", "Ariana Grande, Zedd"),
        new("", "Missing Song", "Nobody"),
    ];

    [Fact]
    public async Task Fetches_each_song_once_keeps_them_and_builds_the_index()
    {
        _fake.Songs["Electric Feel|MGMT"] = FakeLrclib.Json("Electric Feel", "MGMT", "[00:10.00] the copper kettle hums along the quiet harbour\n[00:14.00] seven paper lanterns drifting past the mill", 230);
        _fake.Songs["Break Free|Ariana Grande"] = FakeLrclib.Json("Break Free", "Ariana Grande", "[00:30.00] marble pigeons argue over yesterdays bread");
        var lib = Make();
        var got = await lib.FetchAsync(Songs, null, default);
        Assert.Equal(2, got);
        Assert.True(File.Exists(Path.Combine(_dir, "radio", "lyrics.json")));
        var index = FakeCorpus.Build(200, 12); // a realistic library around the two songs
        Assert.Equal(2, Make().FillIndex(index, Songs));
        Assert.Equal(Songs[0].Key, index.Match("the copper kettle hums along the quiet harbour seven paper lanterns drifting past the mill")!.SongKey);

        var calls = _fake.Calls.Count;
        await Make().FetchAsync(Songs, null, default); // cached; the missing one isn't asked again within a day
        Assert.Equal(calls, _fake.Calls.Count);
        _now = _now.AddDays(2);
        await Make().FetchAsync(Songs, null, default);
        Assert.True(_fake.Calls.Count > calls); // the missing one is tried again
    }

    [Fact]
    public async Task Errors_and_huge_answers_are_skipped_not_fatal()
    {
        _fake.Override = r => r.RequestUri!.Query.Contains("Electric")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 600_000)) }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var got = await Make().FetchAsync(Songs, null, default);
        Assert.Equal(0, got);
    }

    [Fact]
    public async Task Only_asks_lrclib_over_https()
    {
        _fake.Songs["Electric Feel|MGMT"] = FakeLrclib.Json("Electric Feel", "MGMT", "[00:10.00] the copper kettle hums along the quiet harbour");
        await Make().FetchAsync(Songs, null, default);
        Assert.All(_fake.Calls, u => Assert.Equal("https://lrclib.net", u.GetLeftPart(UriPartial.Authority)));
    }
}
