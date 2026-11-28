using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RigControl.Core.Logging;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Fingerprint;
using RigControl.Radio.Lyrics;
using RigControl.Radio.Previews;
using RigControl.Radio.Speech;

namespace RigControl.Radio.Tests;

/// <summary>A pretend song search and preview server: answers by host, counts requests.</summary>
internal sealed class FakeSearch : HttpMessageHandler
{
    public Func<Uri, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public List<Uri> Asked { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Asked) Asked.Add(request.RequestUri!);
        var res = Answer(request.RequestUri!);
        res.RequestMessage = request;
        return Task.FromResult(res);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class PreviewPickTests
{
    private static readonly CatalogueSong Plain = new("Station", "Isaka (6am)", "Ciza, Jazzwrld");
    private const string Apple = "https://audio-ssl.itunes.apple.com/a.m4a";

    [Fact]
    public void The_exact_title_wins_and_other_takes_are_refused()
    {
        var song = new CatalogueSong("S", "Glow", "Band ft. Guest");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow (Live)", "https://audio-ssl.itunes.apple.com/live.m4a"), ("Band & Guest", "Glow", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Band", "Glow (Acoustic)", Apple), ("Band", "Glow - Remix", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Other Singer", "Glow", Apple)]));
    }

    [Fact]
    public void A_listed_remix_must_be_that_remix()
    {
        var remix = new CatalogueSong("S", "Do It (Night Remix)", "Band");
        Assert.Null(PreviewLibrary.Pick(remix, [("Band", "Do It", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(remix, [("Band", "Do It", "https://audio-ssl.itunes.apple.com/o.m4a"), ("Band", "Do It (Night Remix)", Apple)]));
    }

    [Fact]
    public void A_name_note_may_be_missing_and_a_remaster_counts()
    {
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(Plain, [("Ciza", "Isaka", Apple)]));
        var song = new CatalogueSong("S", "Glow", "Band");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow (2011 Remaster)", Apple)]));
    }

    [Theory]
    [InlineData("http://audio-ssl.itunes.apple.com/a.m4a")]
    [InlineData("https://evil.example/a.m4a")]
    [InlineData("https://apple.com.evil.example/a.m4a")]
    [InlineData("https://audio-ssl.itunes.apple.com:8443/a.m4a")]
    public void Previews_only_come_from_apple_or_deezer_over_https(string url)
    {
        Assert.Null(PreviewLibrary.Pick(Plain, [("Ciza", "Isaka (6am)", url)]));
    }

    [Theory]
    [InlineData("Do I (Remix)", "Do I")]
    [InlineData("Giza ft. Seyi Vibez", "Giza")]
    [InlineData("Song - 2011 Remaster", "Song")]
    [InlineData("Blow My Mind (A & B Version)", "Blow My Mind")]
    [InlineData("(Intro)", "(Intro)")]
    public void Titles_are_cleaned(string title, string clean) => Assert.Equal(clean, LyricsLibrary.CleanTitle(title));
}

public class PreviewLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-previews-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSearch _server = new();
    private static readonly CatalogueSong Song = new("Station", "Glow", "Band");
    private static readonly float[] Sound = TestAudio.Song(21, 30);

    public PreviewLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private PreviewLibrary Library(Func<byte[], float[]>? decode = null) =>
        new(new HttpClient(_server), _dir, [Song], (b, _) => Task.FromResult((decode ?? (_ => Sound))(b)), NullLog.Instance,
            TimeSpan.Zero, TimeSpan.Zero, (_, _) => Task.CompletedTask);

    private void AppleHas(string previewUrl) => _server.Answer = u => u.Host switch
    {
        "itunes.apple.com" => FakeSearch.Json($$"""{"results":[{"artistName":"Band","trackName":"Glow","previewUrl":"{{previewUrl}}"}]}"""),
        "audio-ssl.itunes.apple.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) },
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task A_song_is_recognised_from_its_preview_and_kept_for_next_time()
    {
        AppleHas("https://audio-ssl.itunes.apple.com/glow.m4a");
        Assert.Equal(1, await Library().FetchAsync([Song], null, default));

        var again = Library();
        Assert.Equal(1, again.Count);
        var hit = again.Match(Fingerprinter.Compute(TestAudio.Slice(Sound, 10, 20)), 10);
        Assert.NotNull(hit);
        Assert.Equal(Song.Key, hit!.Value.Song.Key);
        Assert.InRange(hit.Value.PositionSeconds, PreviewLibrary.Base + 18, PreviewLibrary.Base + 22);

        // Known: nothing is asked again.
        _server.Asked.Clear();
        await again.FetchAsync([Song], null, default);
        Assert.Empty(_server.Asked);
    }

    [Fact]
    public async Task A_preview_elsewhere_is_never_downloaded()
    {
        AppleHas("https://evil.example/glow.m4a");
        Assert.Equal(0, await Library().FetchAsync([Song], null, default));
        Assert.DoesNotContain(_server.Asked, u => u.Host == "evil.example");
        Assert.Contains(_server.Asked, u => u.Host == "api.deezer.com"); // tried the other search
    }

    [Fact]
    public async Task A_redirect_elsewhere_is_never_followed()
    {
        _server.Answer = u => u.Host switch
        {
            "itunes.apple.com" => FakeSearch.Json("""{"results":[{"artistName":"Band","trackName":"Glow","previewUrl":"https://audio-ssl.itunes.apple.com/glow.m4a"}]}"""),
            "audio-ssl.itunes.apple.com" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/x") } },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        await Library().FetchAsync([Song], null, default);
        Assert.DoesNotContain(_server.Asked, u => u.Host == "evil.example");
    }

    [Fact]
    public async Task An_unreadable_preview_teaches_nothing_and_isnt_asked_again_for_a_week()
    {
        AppleHas("https://audio-ssl.itunes.apple.com/glow.m4a");
        Assert.Equal(0, await Library(_ => []).FetchAsync([Song], null, default));
        _server.Asked.Clear();
        await Library().FetchAsync([Song], null, default);
        Assert.Empty(_server.Asked);
    }

    [Fact]
    public async Task Offline_marks_nothing_missing()
    {
        _server.Answer = _ => throw new HttpRequestException("offline");
        var many = Enumerable.Range(0, 6).Select(i => new CatalogueSong("S", "Song " + i, "Band")).ToList();
        await Assert.ThrowsAsync<HttpRequestException>(() => Library().FetchAsync(many, null, default));
        Assert.False(File.Exists(Path.Combine(_dir, "previews.json")));
    }
}

public class PreviewDecodeWireTests
{
    [Fact]
    public async Task The_helper_decodes_previews_and_keeps_listening()
    {
        var starts = 0;
        using var client = new SpeechHelperClient((pipe, cpu) =>
        {
            Interlocked.Increment(ref starts);
            _ = Task.Run(() => SpeechHelperHost.RunAsync(pipe, cpu, c => new FakeSpeech("cpu", a => $"heard {a.Length}"), NullLog.Instance, default,
                bytes => bytes.Length == 3 ? throw new InvalidDataException("broken") : new float[bytes.Length]));
            return true;
        }, NullLog.Instance, preferGpu: false);
        Assert.Equal(500, (await client.DecodeAsync(new byte[500], default)).Length);
        Assert.Empty(await client.DecodeAsync(new byte[3], default)); // unreadable: empty, the helper stays
        Assert.Equal("heard 1600", await client.TranscribeAsync(new float[1600], default));
        Assert.Equal(1, starts);
    }
}
