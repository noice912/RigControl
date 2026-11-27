namespace RigControl.Radio.Lyrics;

/// <summary>A song found from heard words: where in it the last matched words are, and how clearly it won.</summary>
public sealed record LyricsMatch(string SongKey, double? PositionSeconds, double Score, int Hits, double Lead);

/// <summary>
/// The lyrics of the catalogue's songs as runs of three words. Heard words vote for every song that has the same three
/// words in a row; votes only count when they line up in the same order, a run shared by many songs counts for little
/// (and runs in very many songs, like "oh oh oh", not at all), a good share of what was heard must belong to the song,
/// and the best song must clearly beat the next one. Thread-safe.
/// </summary>
public sealed class LyricsIndex
{
    public const int MinHits = 5;
    public const double MinScore = 8.0;
    public const double MinLead = 1.6;
    /// <summary>Share of the heard word runs that must line up with the song (talk mostly doesn't).</summary>
    public const double MinShare = 0.4;
    private const int AlignSlack = 3;
    private const int MaxPostings = 5000;

    private readonly object _gate = new();
    private readonly List<(string Key, double? Duration)> _songs = new();
    private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<(int Song, int Word, double Time)>> _postings = new();
    private readonly Dictionary<long, int> _songsWith = new();

    public int Count
    {
        get { lock (_gate) return _songs.Count; }
    }

    /// <summary>Adds a song's lines (a key added twice is ignored). Lines may be untimed (Seconds &lt; 0).</summary>
    public void Add(string key, IReadOnlyList<LyricLine> lines, double? durationSeconds)
    {
        var words = new List<(string Word, double Time)>();
        foreach (var line in lines)
            foreach (var w in LyricsText.Words(line.Text))
                words.Add((w, line.Seconds));
        lock (_gate)
        {
            if (_byKey.ContainsKey(key) || words.Count < 3) return;
            var song = _songs.Count;
            _songs.Add((key, durationSeconds));
            _byKey[key] = song;
            var seen = new HashSet<long>();
            for (var i = 0; i + 2 < words.Count; i++)
            {
                var h = Hash(words[i].Word, words[i + 1].Word, words[i + 2].Word);
                if (!_postings.TryGetValue(h, out var list)) _postings[h] = list = new();
                list.Add((song, i, words[i + 2].Time));
                if (seen.Add(h)) _songsWith[h] = _songsWith.GetValueOrDefault(h) + 1;
            }
        }
    }

    public LyricsMatch? Match(string? transcript)
    {
        var heard = LyricsText.Words(transcript);
        if (heard.Count < MinHits + 2) return null;
        var heardRuns = heard.Count - 2;
        lock (_gate)
        {
            var n = Math.Max(1, _songs.Count);
            var tooCommon = Math.Max(20, n / 10);
            var weights = new Dictionary<long, double>();
            // song -> alignment (song word - heard word) -> distinct runs matched there, and the latest line time
            var votes = new Dictionary<(int Song, int Offset), (HashSet<long> Runs, double Time)>();
            for (var j = 0; j + 2 < heard.Count; j++)
            {
                var h = Hash(heard[j], heard[j + 1], heard[j + 2]);
                if (!_postings.TryGetValue(h, out var list) || list.Count > MaxPostings) continue;
                var songsWithIt = _songsWith[h];
                if (songsWithIt > tooCommon) continue; // says nothing about which song this is
                weights[h] = Math.Log(1 + n / (double)songsWithIt);
                foreach (var (song, word, time) in list)
                {
                    var at = (song, word - j);
                    var v = votes.TryGetValue(at, out var x) ? x : (new HashSet<long>(), double.MinValue);
                    v.Item1.Add(h);
                    votes[at] = (v.Item1, Math.Max(v.Item2, time));
                }
            }
            if (votes.Count == 0) return null;
            // Runs a few words apart in alignment are the same place (a word heard twice, or missed).
            var best = new Dictionary<int, (double Score, int Hits, double Time)>();
            foreach (var ((song, offset), _) in votes)
            {
                var runs = new HashSet<long>();
                var time = double.MinValue;
                for (var d = -AlignSlack; d <= AlignSlack; d++)
                {
                    if (!votes.TryGetValue((song, offset + d), out var v)) continue;
                    runs.UnionWith(v.Runs);
                    time = Math.Max(time, v.Time);
                }
                var score = runs.Sum(r => weights[r]);
                if (!best.TryGetValue(song, out var b) || score > b.Score) best[song] = (score, runs.Count, time);
            }
            var ranked = best.OrderByDescending(kv => kv.Value.Score).ToList();
            var top = ranked[0].Key;
            var (topScore, hits, lastTime) = ranked[0].Value;
            var second = ranked.Count > 1 ? ranked[1].Value.Score : 0;
            // No runner-up is not a huge lead: it is measured against the least score that counts at all.
            var lead = topScore / Math.Max(second, MinScore);
            if (hits < MinHits || topScore < MinScore || lead < MinLead || hits < MinShare * heardRuns) return null;
            var (key, duration) = _songs[top];
            double? position = lastTime >= 0 ? lastTime + 2 : null;
            if (position is { } p && duration is { } d2 && p > d2) position = d2;
            return new LyricsMatch(key, position, topScore, hits, lead);
        }
    }

    private static long Hash(string a, string b, string c) =>
        (long)(uint)StringComparer.Ordinal.GetHashCode(a + " " + b + " " + c) << 32 | (uint)StringComparer.Ordinal.GetHashCode(c + "|" + a + "|" + b);
}
