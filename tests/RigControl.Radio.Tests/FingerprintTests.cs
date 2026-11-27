using System.Diagnostics;
using RigControl.Radio.Fingerprint;

namespace RigControl.Radio.Tests;

public class FingerprintTests
{
    /// <summary>A tiny in-memory "store": hash -> (song, frame).</summary>
    private sealed class Index
    {
        private readonly Dictionary<uint, List<(int, int)>> _map = new();
        public void Add(int song, IReadOnlyList<Landmark> landmarks, int frameOffset = 0)
        {
            foreach (var l in landmarks)
            {
                if (!_map.TryGetValue(l.Hash, out var list)) _map[l.Hash] = list = new();
                list.Add((song, l.Frame + frameOffset));
            }
        }
        public IEnumerable<(int SongId, int Frame)> Lookup(uint h) => _map.TryGetValue(h, out var l) ? l : [];
    }

    [Fact]
    public void Same_audio_matches_at_the_right_position()
    {
        var song = TestAudio.Song(1, 60);
        var index = new Index();
        index.Add(1, Fingerprinter.Compute(song));
        var m = Matcher.Best(Fingerprinter.Compute(TestAudio.Slice(song, 30, 40)), index.Lookup, 10);
        Assert.NotNull(m);
        Assert.Equal(1, m!.SongId);
        Assert.InRange(m.PositionSeconds, 39.5, 40.5);
    }

    [Fact]
    public void Matches_with_noise_at_0_dB()
    {
        var song = TestAudio.Song(2, 60);
        var index = new Index();
        index.Add(2, Fingerprinter.Compute(song));
        for (var s = 10; s <= 40; s++) index.Add(s, Fingerprinter.Compute(TestAudio.Song(s * 13, 30)));
        var noisy = TestAudio.WithNoise(TestAudio.Slice(song, 20, 30), 5);
        var m = Matcher.Best(Fingerprinter.Compute(noisy), index.Lookup, 10);
        Assert.NotNull(m);
        Assert.Equal(2, m!.SongId);
        Assert.InRange(m.PositionSeconds, 29.5, 30.5);
    }

    [Fact]
    public void Different_song_never_matches()
    {
        var index = new Index();
        for (var s = 1; s <= 50; s++) index.Add(s, Fingerprinter.Compute(TestAudio.Song(s, 30)));
        Assert.Null(Matcher.Best(Fingerprinter.Compute(TestAudio.Song(99, 10)), index.Lookup, 10));
        Assert.Null(Matcher.Best(Fingerprinter.Compute(TestAudio.WithNoise(TestAudio.Song(98, 10), 3)), index.Lookup, 10));
    }

    [Fact]
    public void Silence_gives_no_landmarks()
    {
        Assert.Empty(Fingerprinter.Compute(new float[16000 * 10]));
        Assert.Empty(Fingerprinter.Compute(new float[100]));
    }

    [Fact]
    public void Fifteen_seconds_are_fingerprinted_quickly()
    {
        var audio = TestAudio.Song(3, 15);
        Fingerprinter.Compute(audio); // warm up
        var sw = Stopwatch.StartNew();
        Fingerprinter.Compute(audio);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 200);
    }

    [Fact]
    public void Position_is_measured_from_the_end_of_the_heard_sound()
    {
        var song = TestAudio.Song(4, 60);
        var index = new Index();
        index.Add(4, Fingerprinter.Compute(song));
        // 8 s of the song (30-38 s) then 2 s of quiet: the end of what was heard is 40 s into the song.
        var heard = TestAudio.Slice(song, 30, 38).Concat(new float[16000 * 2]).ToArray();
        var m = Matcher.Best(Fingerprinter.Compute(heard), index.Lookup, heard.Length / 16000.0);
        Assert.InRange(m!.PositionSeconds, 39.8, 40.2);
    }

    [Fact]
    public void A_hash_posted_everywhere_is_ignored_and_costs_little()
    {
        var query = Fingerprinter.Compute(TestAudio.Song(5, 10));
        var flood = Enumerable.Range(0, 50_000).Select(i => (i % 7, i)).ToArray();
        var sw = Stopwatch.StartNew();
        var m = Matcher.Best(query, _ => flood, 10);
        Assert.Null(m);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 1000);
    }
}
