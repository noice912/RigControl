using System.Diagnostics;
using System.Net;
using System.Text;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Lyrics;

namespace RigControl.Radio.Tests;

/// <summary>Made-up songs from a small word list, so many runs of words turn up in more than one song, like real lyrics.</summary>
internal static class FakeCorpus
{
    public static readonly string[] Common =
    [
        "i", "you", "me", "we", "the", "a", "and", "to", "know", "love", "baby", "oh", "yeah", "let", "go", "tell", "what",
        "mean", "right", "now", "get", "got", "on", "in", "it", "my", "your", "all", "night", "day", "time", "back", "up",
        "down", "make", "feel", "see", "say", "want", "need", "come", "with", "for", "this", "that", "just", "like", "so",
        "do", "dont", "cant", "gonna", "wanna", "real", "money", "girl", "boy", "heart", "way", "something", "nothing",
    ];

    public static LyricsIndex Build(int songs, int seed)
    {
        var rng = new Random(seed);
        var index = new LyricsIndex();
        for (var s = 0; s < songs; s++)
        {
            var lines = new List<LyricLine>();
            for (var l = 0; l < 40; l++)
                lines.Add(new LyricLine(5 + l * 4, string.Join(' ', Enumerable.Range(0, 7).Select(_ => Common[rng.Next(Common.Length)]))));
            index.Add("song" + s, lines, 200);
        }
        return index;
    }
}

public class LyricsSafetyTests
{
    [Fact]
    public void Long_everyday_talk_never_matches_a_song()
    {
        var index = FakeCorpus.Build(800, 1);
        var rng = new Random(99);
        for (var i = 0; i < 300; i++)
        {
            // 8 s of dialogue is about 20 words, from the same everyday words songs use.
            var talk = string.Join(' ', Enumerable.Range(0, 20).Select(_ => FakeCorpus.Common[rng.Next(FakeCorpus.Common.Length)]));
            Assert.Null(index.Match(talk));
        }
    }

    [Fact]
    public void A_few_words_found_in_only_one_song_are_not_enough()
    {
        var index = FakeCorpus.Build(200, 2);
        index.Add("unique", [new(10, "orange bicycle whispers under the frozen lighthouse tonight")], 200);
        // Five rare words in a row among a lot of other talk.
        Assert.Null(index.Match("well you know what i mean the bicycle whispers under the frozen thing anyway so like i said yeah"));
    }

    [Fact]
    public void Very_common_runs_stay_quick()
    {
        var index = new LyricsIndex();
        for (var s = 0; s < 800; s++)
            index.Add("s" + s, Enumerable.Range(0, 30).Select(l => new LyricLine(l * 4, "oh oh oh oh yeah yeah yeah yeah")).ToList(), 200);
        index.Match("oh oh oh oh yeah yeah yeah"); // warm up
        var sw = Stopwatch.StartNew();
        index.Match("oh oh oh oh yeah yeah yeah yeah oh oh oh oh yeah yeah yeah");
        Assert.InRange(sw.ElapsedMilliseconds, 0, 300);
    }
}

public class LyricsLibrarySafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-lyrics-safety-" + Guid.NewGuid().ToString("N"));
    public LyricsLibrarySafetyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private sealed class Down : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("No route to host");
        }
    }

    [Fact]
    public async Task When_lrclib_cant_be_reached_it_stops_soon_and_marks_nothing_missing()
    {
        var down = new Down();
        var lib = new LyricsLibrary(new HttpClient(down), Path.Combine(_dir, "radio", "lyrics.json"), new ListLog(), pause: TimeSpan.Zero);
        var songs = Enumerable.Range(0, 50).Select(i => new CatalogueSong("", "Song " + i, "Band")).ToList();
        await Assert.ThrowsAsync<HttpRequestException>(() => lib.FetchAsync(songs, null, default));
        Assert.InRange(down.Calls, 1, 6);
        Assert.Empty(lib.Load());
    }

    [Fact]
    public async Task A_search_result_by_another_artist_is_not_taken()
    {
        var fake = new FakeLrclib();
        fake.Override = r => r.RequestUri!.AbsolutePath == "/api/search"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[" + FakeLrclib.Json("Midnight Train", "Somebody Else", "[00:10.00] the copper kettle hums along the quiet harbour") + "]",
                    Encoding.UTF8, "application/json"),
            }
            : null;
        var lib = new LyricsLibrary(new HttpClient(fake), Path.Combine(_dir, "radio", "lyrics.json"), new ListLog(), pause: TimeSpan.Zero);
        var got = await lib.FetchAsync([new CatalogueSong("", "Midnight Train", "Real Artist")], null, default);
        Assert.Equal(0, got);
    }

    [Fact]
    public async Task A_damaged_lyrics_file_with_repeats_is_still_usable()
    {
        var path = Path.Combine(_dir, "radio", "lyrics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var e = """{"key":"a|b","title":"A","artist":"B","missing":true,"fetchedAt":"2026-11-27T00:00:00+00:00"}""";
        File.WriteAllText(path, "[" + e + "," + e + "]");
        var fake = new FakeLrclib();
        var lib = new LyricsLibrary(new HttpClient(fake), path, new ListLog(), pause: TimeSpan.Zero);
        await lib.FetchAsync([new CatalogueSong("", "Other", "Band")], null, default); // must not throw
    }

    [Fact]
    public async Task When_lrclib_says_slow_down_it_waits_and_carries_on()
    {
        var fake = new FakeLrclib();
        var limited = 0;
        fake.Override = r =>
        {
            if (Interlocked.Increment(ref limited) > 5) return null; // after a while it answers normally
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return res;
        };
        fake.Songs["Song 3|Band"] = FakeLrclib.Json("Song 3", "Band", "[00:10.00] the copper kettle hums along the quiet harbour");
        var waits = new List<TimeSpan>();
        var lib = new LyricsLibrary(new HttpClient(fake), Path.Combine(_dir, "radio", "lyrics.json"), new ListLog(), pause: TimeSpan.Zero,
            wait: (d, _) => { waits.Add(d); return Task.CompletedTask; });
        var songs = Enumerable.Range(0, 6).Select(i => new CatalogueSong("", "Song " + i, "Band")).ToList();
        var got = await lib.FetchAsync(songs, null, default); // no "couldn't be reached"
        Assert.Equal(1, got);
        Assert.Contains(waits, w => w >= TimeSpan.FromSeconds(20));
        Assert.Equal(6, lib.Load().Count); // every song asked, none skipped because of the slow-downs
    }
}
