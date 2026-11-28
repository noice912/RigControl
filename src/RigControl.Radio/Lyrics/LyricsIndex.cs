namespace RigControl.Radio.Lyrics;

/// <summary>A song found from heard words: where in it the last matched words are, and how clearly it won.</summary>
public sealed record LyricsMatch(string SongKey, double? PositionSeconds, double Score, int Hits, double Lead);

/// <summary>The best candidate for heard words, enough or not (for the log): matched words of all heard words.</summary>
public sealed record LyricsCandidate(string SongKey, int Hits, int HeardRuns, double Score, double Lead);

/// <summary>
/// The lyrics of the catalogue's songs, searched the way sung words come out of speech recognition: misheard, missing
/// and extra words. Two steps:
/// 1. Candidates: songs sharing runs of two or three heard words (or a rare word), runs in very many songs ignored.
/// 2. Each candidate is lined up word by word with what was heard (local alignment): an exact word scores by how rare
///    it is (so "you", "love", "the" count for little), a nearly-same word ("harbor"/"harbour") half that, a wrong word
///    or a skipped one costs a little. The best song must score enough, match enough of what was heard, and clearly
///    beat the next one. Thread-safe.
/// </summary>
public sealed class LyricsIndex
{
    public const double MinScore = 10.0;
    public const int MinHits = 5;
    public const double MinShare = 0.3;
    public const double MinLead = 1.5;
    private const int Candidates = 12;
    private const double Mismatch = -0.6, Gap = -0.7, MaxWordWeight = 4, MinWordWeight = 0.2;

    private readonly object _gate = new();
    private readonly List<(string Key, double? Duration, string[] Words, double[] Times)> _songs = new();
    private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _wordSongs = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<int>> _runs = new();          // 2- and 3-word runs -> songs having them

    public int Count
    {
        get { lock (_gate) return _songs.Count; }
    }

    /// <summary>Adds a song's lines (a key added twice is ignored). Lines may be untimed (Seconds &lt; 0).</summary>
    public void Add(string key, IReadOnlyList<LyricLine> lines, double? durationSeconds)
    {
        var words = new List<string>();
        var times = new List<double>();
        foreach (var line in lines)
            foreach (var w in LyricsText.Words(line.Text))
            {
                words.Add(w);
                times.Add(line.Seconds);
            }
        lock (_gate)
        {
            if (_byKey.ContainsKey(key) || words.Count < 3) return;
            var song = _songs.Count;
            _songs.Add((key, durationSeconds, words.ToArray(), times.ToArray()));
            _byKey[key] = song;
            foreach (var w in words.Distinct(StringComparer.Ordinal)) _wordSongs[w] = _wordSongs.GetValueOrDefault(w) + 1;
            var seen = new HashSet<long>();
            for (var i = 0; i + 1 < words.Count; i++)
            {
                seen.Add(Hash(words[i], words[i + 1], null));
                if (i + 2 < words.Count) seen.Add(Hash(words[i], words[i + 1], words[i + 2]));
            }
            foreach (var h in seen)
            {
                if (!_runs.TryGetValue(h, out var list)) _runs[h] = list = new();
                list.Add(song);
            }
        }
    }

    public LyricsMatch? Match(string? transcript)
    {
        if (Evaluate(transcript) is not { } r) return null;
        if (r.Score < MinScore || r.Hits < MinHits || r.Hits < MinShare * r.Heard || r.Lead < MinLead) return null;
        return new LyricsMatch(r.Key, r.Position, r.Score, r.Hits, r.Lead);
    }

    /// <summary>The closest song even when it isn't enough to count (null: nothing heard is in any song).</summary>
    public LyricsCandidate? Closest(string? transcript) =>
        Evaluate(transcript) is { } r ? new LyricsCandidate(r.Key, r.Hits, r.Heard, r.Score, r.Lead) : null;

    private (string Key, double? Position, double Score, int Hits, double Lead, int Heard)? Evaluate(string? transcript)
    {
        var heard = LyricsText.Words(transcript).ToArray();
        if (heard.Length < 3) return null;
        lock (_gate)
        {
            var n = Math.Max(1, _songs.Count);
            var tooCommon = Math.Max(20, n / 10);

            // 1. Candidates by shared runs of words (and rare single words).
            var votes = new Dictionary<int, double>();
            void Vote(long h, double factor)
            {
                if (!_runs.TryGetValue(h, out var songs) || songs.Count > tooCommon) return;
                var w = factor * Math.Log(1 + n / (double)songs.Count);
                foreach (var s in songs) votes[s] = votes.GetValueOrDefault(s) + w;
            }
            var counted = new HashSet<long>();
            for (var j = 0; j + 1 < heard.Length; j++)
            {
                var h2 = Hash(heard[j], heard[j + 1], null);
                if (counted.Add(h2)) Vote(h2, 1);
                if (j + 2 < heard.Length)
                {
                    var h3 = Hash(heard[j], heard[j + 1], heard[j + 2]);
                    if (counted.Add(h3)) Vote(h3, 1.5);
                }
            }
            if (votes.Count == 0) return null;

            // 2. Line each candidate up with what was heard.
            var scored = votes.OrderByDescending(kv => kv.Value).Take(Candidates)
                .Select(kv => (Song: kv.Key, Result: Align(heard, _songs[kv.Key].Words, n)))
                .OrderByDescending(x => x.Result.Score)
                .ToList();
            var best = scored[0];
            var second = scored.Count > 1 ? scored[1].Result.Score : 0;
            var lead = best.Result.Score / Math.Max(second, MinScore);
            var song = _songs[best.Song];
            var t = best.Result.End >= 0 ? song.Times[best.Result.End] : -1;
            double? position = t >= 0 ? t + 2 : null;
            if (position is { } p && song.Duration is { } d && p > d) position = d;
            return (song.Key, position, best.Result.Score, best.Result.Matched, lead, heard.Length);
        }
    }

    /// <summary>Local alignment (Smith-Waterman) of heard words against a song's words. Under _gate.</summary>
    private (double Score, int Matched, int End) Align(string[] heard, string[] song, int n)
    {
        var cols = song.Length + 1;
        var score = new double[2, cols];
        var matched = new int[2, cols];
        double bestScore = 0;
        int bestMatched = 0, bestEnd = -1;
        for (var i = 1; i <= heard.Length; i++)
        {
            int cur = i & 1, prev = cur ^ 1;
            score[cur, 0] = 0;
            matched[cur, 0] = 0;
            var hw = heard[i - 1];
            for (var j = 1; j < cols; j++)
            {
                var sw = song[j - 1];
                double s;
                var hit = 0;
                if (hw == sw) { s = Weight(sw, n); hit = 1; }
                else if (Near(hw, sw)) { s = 0.5 * Weight(sw, n); hit = 1; }
                else s = Mismatch;
                var diag = score[prev, j - 1] + s;
                var up = score[prev, j] + Gap;
                var left = score[cur, j - 1] + Gap;
                double v;
                int m;
                if (diag >= up && diag >= left) { v = diag; m = matched[prev, j - 1] + hit; }
                else if (up >= left) { v = up; m = matched[prev, j]; }
                else { v = left; m = matched[cur, j - 1]; }
                if (v <= 0) { v = 0; m = 0; }
                score[cur, j] = v;
                matched[cur, j] = m;
                if (v > bestScore) { bestScore = v; bestMatched = m; bestEnd = j - 1; }
            }
        }
        return (bestScore, bestMatched, bestEnd);
    }

    /// <summary>How much a matching word counts: rare words a lot, words in most songs very little. Under _gate.</summary>
    private double Weight(string word, int n) =>
        Math.Clamp(Math.Log(1 + n / (double)Math.Max(1, _wordSongs.GetValueOrDefault(word))) - Math.Log(2) + MinWordWeight, MinWordWeight, MaxWordWeight);

    /// <summary>Nearly the same word: one letter off (two for long words), e.g. "harbor"/"harbour", "boats"/"bolts".</summary>
    internal static bool Near(string a, string b)
    {
        var shortest = Math.Min(a.Length, b.Length);
        if (shortest < 3 || Math.Abs(a.Length - b.Length) > 2) return false;
        var allowed = shortest >= 7 ? 2 : 1;
        // Levenshtein distance, stopping early once it is over the allowance.
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > allowed) return false;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length] <= allowed;
    }

    private static long Hash(string a, string b, string? c) =>
        (long)(uint)StringComparer.Ordinal.GetHashCode(a + " " + b + " " + c) << 32 | (uint)StringComparer.Ordinal.GetHashCode((c ?? "") + "|" + a + "|" + b);
}
