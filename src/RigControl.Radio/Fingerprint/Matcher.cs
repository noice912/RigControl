namespace RigControl.Radio.Fingerprint;

/// <summary>A recognised song: where in it the end of the heard sound is, and how strongly it matched.</summary>
public sealed record MatchResult(int SongId, double PositionSeconds, int Votes, double Confidence);

public static class Matcher
{
    public const int MinVotes = 12;
    public const double MinLead = 2.5;

    /// <summary>A hash found in more places than this says nothing (silence-like or very common sound): it is skipped.</summary>
    public const int MaxPostings = 500;

    /// <summary>
    /// Every stored (song, frame) sharing a hash with the heard sound votes for "the heard sound started at this frame
    /// of that song". A real match piles its votes on one offset; chance hits spread out. The best song wins only with
    /// <see cref="MinVotes"/> votes and <see cref="MinLead"/> times the votes of the best other song. Null otherwise.
    /// <paramref name="heardSeconds"/> is the length of the heard sound, so the position is where its end is in the song.
    /// </summary>
    public static MatchResult? Best(IReadOnlyList<Landmark> query, Func<uint, IEnumerable<(int SongId, int Frame)>> lookup, double heardSeconds)
    {
        if (query.Count == 0) return null;
        var votes = new Dictionary<(int Song, int Offset), int>();
        foreach (var q in query)
        {
            var postings = lookup(q.Hash);
            var count = postings is IReadOnlyCollection<(int, int)> c ? c.Count : postings.Take(MaxPostings + 1).Count();
            if (count > MaxPostings) continue;
            foreach (var (song, frame) in postings)
            {
                var key = (song, frame - q.Frame);
                votes[key] = votes.GetValueOrDefault(key) + 1;
            }
        }
        if (votes.Count == 0) return null;
        // Offsets one frame apart are the same alignment (frames never line up exactly).
        var bestPerSong = new Dictionary<int, (int Votes, int Offset)>();
        foreach (var ((song, offset), n) in votes)
        {
            var total = n + votes.GetValueOrDefault((song, offset - 1)) + votes.GetValueOrDefault((song, offset + 1));
            if (!bestPerSong.TryGetValue(song, out var b) || total > b.Votes) bestPerSong[song] = (total, offset);
        }
        var ranked = bestPerSong.OrderByDescending(kv => kv.Value.Votes).ToList();
        var bestSong = ranked[0].Key;
        var (bestVotes, bestOffset) = ranked[0].Value;
        var second = ranked.Count > 1 ? ranked[1].Value.Votes : 0;
        if (bestVotes < MinVotes || bestVotes < MinLead * second) return null;
        var position = bestOffset * Fingerprinter.SecondsPerFrame + heardSeconds;
        return new MatchResult(bestSong, position, bestVotes, (double)bestVotes / query.Count);
    }
}
