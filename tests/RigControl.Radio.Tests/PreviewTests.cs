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

    [Fact]
    public void A_radio_edit_counts_as_the_song_but_a_named_edit_does_not()
    {
        var song = new CatalogueSong("S", "Glow", "Band");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow (Radio Edit)", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow - Edit", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Glow (Radio Edit)", "Band"), [("Band", "Glow", Apple)]));
        // An edit by another producer is a different mix.
        Assert.Null(PreviewLibrary.Pick(new CatalogueSong("S", "Glow (Night Owl Edit)", "Band"), [("Band", "Glow", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Band", "Glow (Extended Edit)", Apple)]));
    }

    [Fact]
    public void A_remix_hidden_in_a_credit_never_takes_the_original()
    {
        var song = new CatalogueSong("S", "Glow (feat. Guest Remix)", "Band");
        Assert.Null(PreviewLibrary.Pick(song, [("Band", "Glow", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow (Guest Remix)", Apple)]));
    }

    [Theory]
    [InlineData("Glow (Part 2)")]
    [InlineData("Glow Interlude")]
    [InlineData("Glow Pt. 2")]
    [InlineData("Glow Than Me")]
    [InlineData("Glow (Piano)")]
    public void A_longer_title_is_only_taken_for_a_release_note(string theirs)
    {
        var song = new CatalogueSong("S", "Glow", "Band");
        Assert.Null(PreviewLibrary.Pick(song, [("Band", theirs, Apple)]));
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
    [InlineData("Harbour Lights – Reprise", "Harbour Lights")]
    public void Titles_are_cleaned(string title, string clean) => Assert.Equal(clean, LyricsLibrary.CleanTitle(title));

    [Fact]
    public void Guest_credits_and_ampersands_dont_count()
    {
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Night Drive ft. Guest", "Band"), [("Band", "Night Drive (feat. Guest)", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Night Drive", "Band"), [("Band", "Night Drive [with Guest]", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Salt & Sea", "Band"), [("Band", "Salt and Sea", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Glow", "Rita And The Tides"), [("Rita & The Tides", "Glow", Apple)]));
        // Still another take.
        Assert.Null(PreviewLibrary.Pick(new CatalogueSong("S", "Night Drive", "Band"), [("Band", "Night Drive (feat. Guest) [Live]", Apple)]));
    }

    [Fact]
    public void A_leading_the_and_a_name_without_letters_still_match()
    {
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Glow", "The Glimmers"), [("Glimmers", "Glow", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Glow", "Glimmers"), [("The Glimmers", "Glow", Apple)]));
        Assert.Null(PreviewLibrary.Pick(new CatalogueSong("S", "Glow", "The Glimmers"), [("Shimmers", "Glow", Apple), ("The Others", "Glow", Apple)]));
        var noLetters = new CatalogueSong("S", "Glow", "???");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(noLetters, [("Other Band", "Glow", "https://audio-ssl.itunes.apple.com/o.m4a"), ("???", "Glow", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Glow", "??? ft. Guest"), [("??? & Guest", "Glow", Apple)]));
        Assert.Null(PreviewLibrary.Pick(noLetters, [("Other Band", "Glow", Apple), ("?", "Glow", Apple)]));
    }

    [Fact]
    public void Another_credited_artist_counts_only_with_the_same_title_and_after_the_first()
    {
        var song = new CatalogueSong("S", "Glow", "Band, Guest Singer");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Guest Singer", "Glow", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Guest Singer", "Glow (Live)", Apple), ("Guest Singer", "Glow (2011 Remaster)", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Someone Else", "Glow", Apple)]));
        // The first artist's (renamed) original wins over another artist's.
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Guest Singer", "Glow", "https://audio-ssl.itunes.apple.com/g.m4a"), ("Band", "Glow (2011 Remaster)", Apple)]));
        // "Jr." is not an artist of its own.
        Assert.Null(PreviewLibrary.Pick(new CatalogueSong("S", "Glow", "Hank Band, Jr."), [("Jr.", "Glow", Apple)]));
    }

    [Fact]
    public void A_single_or_album_version_is_the_song_itself_and_spacing_may_differ()
    {
        var song = new CatalogueSong("S", "Glow", "Band");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow (Live Version)", "https://audio-ssl.itunes.apple.com/l.m4a"), ("Band", "Glow (Single Version)", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Glow (Album Version)", "Band"), [("Band", "Glow", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Band", "Glow (Extended Version)", Apple), ("Band", "Glow (Club Version)", Apple)]));
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Mi Pa Ka Dans", "Band"), [("Band", "Mi Paka Dans", Apple)]));
        Assert.Null(PreviewLibrary.Pick(new CatalogueSong("S", "Mi Pa Ka Dans", "Band"), [("Band", "Mi Paka Dans Ankò", Apple)]));
    }

    [Fact]
    public void A_note_may_differ_only_when_it_is_about_the_release()
    {
        var song = new CatalogueSong("S", "Glow (Summer Trip)", "Band");
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(song, [("Band", "Glow (From The Film)", Apple)]));
        Assert.Null(PreviewLibrary.Pick(song, [("Band", "Glow (Part 2)", Apple), ("Band", "Glow (Live)", Apple), ("Band", "Glow Interlude", Apple)]));
        // Ours with an extra producer note: theirs without it.
        Assert.Equal(new Uri(Apple), PreviewLibrary.Pick(new CatalogueSong("S", "Run Fast (Night) (Prod by Someone)", "Band"), [("Band", "Run Fast (Night)", Apple)]));
        // A listed remix never takes a release note of the plain song.
        Assert.Null(PreviewLibrary.Pick(new CatalogueSong("S", "Glow (Night Remix)", "Band"), [("Band", "Glow (2011 Remaster)", Apple)]));
    }
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
    public async Task Deezer_is_asked_plainly_when_the_exact_search_finds_nothing()
    {
        _server.Answer = u => u.Host switch
        {
            "itunes.apple.com" => FakeSearch.Json("""{"results":[]}"""),
            "api.deezer.com" when Uri.UnescapeDataString(u.Query).Contains("artist:") => FakeSearch.Json("""{"data":[]}"""),
            "api.deezer.com" => FakeSearch.Json("""{"data":[{"title":"Glow","artist":{"name":"The Band"},"preview":"https://cdnt-preview.dzcdn.net/glow.mp3"}]}"""),
            "cdnt-preview.dzcdn.net" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        Assert.Equal(1, await Library().FetchAsync([Song], null, default));
        Assert.Equal(2, _server.Asked.Count(u => u.Host == "api.deezer.com"));
    }

    [Fact]
    public async Task Songs_not_found_under_older_rules_are_looked_up_again_at_once()
    {
        File.WriteAllText(Path.Combine(_dir, "previews.json"),
            "[{\"key\":\"" + Song.Key + "\",\"found\":false,\"checkedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}]");
        AppleHas("https://audio-ssl.itunes.apple.com/glow.m4a");
        Assert.Equal(1, await Library().FetchAsync([Song], null, default));
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
