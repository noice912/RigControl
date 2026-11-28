using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RigControl.Core.Logging;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Fingerprint;
using RigControl.Radio.Lyrics;
using RigControl.Radio.Store;

namespace RigControl.Radio.Previews;

/// <summary>
/// What every catalogue song sounds like, so songs are recognised from their sound alone (also songs without words).
/// For each song RigControl asks Apple's public song search (then Deezer's) for its free 30-second preview, has the
/// speech helper (no administrator rights) decode it, keeps only its fingerprints in <c>radio\previews.rcfp</c> and
/// throws the sound away. A song without a preview is asked again after a week (<c>radio\previews.json</c>).
/// Where in the song the preview is isn't known, so its fingerprints sit at <see cref="Base"/> seconds: positions
/// from them only count relative to each other.
/// </summary>
public sealed class PreviewLibrary
{
    public const string AppleSearch = "https://itunes.apple.com/search";
    public const string DeezerSearch = "https://api.deezer.com/search";
    public const int MaxAnswerBytes = 512 * 1024;
    public const int MaxPreviewBytes = 2 * 1024 * 1024;
    public const long MaxStateBytes = 4L * 1024 * 1024;

    /// <summary>Where (in seconds) a preview's fingerprints are put: far from 0, so sound before it fits too.</summary>
    public const double Base = 600;

    private const int MaxFailuresInARow = 5, MaxSlowDowns = 3;
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] OtherTake = ["live", "remix", "mix", "acoustic", "instrumental", "karaoke", "version", "edit", "demo", "cover", "sped", "slowed", "reprise", "dub", "vip", "rework"];

    /// <summary>A guest credit in a title: "(feat. X)", "[with X]", or a trailing "ft. X".</summary>
    private static readonly Regex Credit = new(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s+(?:feat\.?|ft\.?|featuring)\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Where one artist ends and the next starts in a credit ("A, B &amp; C feat. D").</summary>
    private static readonly Regex ArtistSplit = new(@",|&|\s(?:and|x|ft\.?|feat\.?|featuring|w/)\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private readonly HttpClient _http;
    private readonly string _statePath;
    private readonly Func<byte[], CancellationToken, Task<float[]>> _decode;
    private readonly ILog _log;
    private readonly TimeSpan _applePace, _deezerPace;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<DateTimeOffset> _now;
    private readonly LearnedSongs _store;
    private readonly Dictionary<string, CatalogueSong> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Rules">Which song-matching rules looked: a song not found under older rules is looked up again at once.</param>
    public sealed record State(string Key, bool Found, DateTimeOffset CheckedAt, int? Rules = null);

    /// <summary>Raised whenever the matching rules change, so songs not found before are looked up again.</summary>
    public const int RulesVersion = 2;

    /// <param name="decode">Turns a downloaded preview into 16 kHz mono sound (the speech helper); empty: unreadable.</param>
    public PreviewLibrary(HttpClient http, string radioFolder, IReadOnlyList<CatalogueSong> catalogue,
        Func<byte[], CancellationToken, Task<float[]>> decode, ILog log, TimeSpan? applePace = null, TimeSpan? deezerPace = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _statePath = Path.Combine(radioFolder, "previews.json");
        _decode = decode;
        _log = log;
        _applePace = applePace ?? TimeSpan.FromSeconds(3.2); // Apple allows about 20 searches a minute
        _deezerPace = deezerPace ?? TimeSpan.FromMilliseconds(300);
        _wait = wait ?? ((d, c) => Task.Delay(d, c));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _store = LearnedSongs.Load(Path.Combine(radioFolder, "previews.rcfp"), log);
        foreach (var s in catalogue) _byName.TryAdd(s.Title + "\n" + s.Artist, s);
    }

    /// <summary>How many songs' sound is known.</summary>
    public int Count => _store.Songs.Count;

    /// <summary>The catalogue song this sound is from, and where in the preview frame the heard sound ends.</summary>
    public (CatalogueSong Song, double PositionSeconds)? Match(IReadOnlyList<Landmark> query, double heardSeconds)
    {
        if (_store.Match(query, heardSeconds) is not { } m || _store.Get(m.SongId) is not { } s) return null;
        return _byName.TryGetValue(s.Title + "\n" + s.Artist, out var song) ? (song, m.PositionSeconds) : null;
    }

    /// <summary>Gets the preview of every song not done yet. Returns how many songs' sound is known now.</summary>
    public async Task<int> FetchAsync(IReadOnlyList<CatalogueSong> songs, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var states = new Dictionary<string, State>(StringComparer.Ordinal);
        foreach (var st in LoadStates()) states[st.Key] = st;
        var known = _store.Songs.Select(s => s.Title + "\n" + s.Artist).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var todo = songs.Where(s => !known.Contains(s.Title + "\n" + s.Artist)
                                    && !(states.TryGetValue(s.Key, out var st) && !st.Found && (st.Rules ?? 1) >= RulesVersion && _now() - st.CheckedAt < RetryMissingAfter))
            .DistinctBy(s => (s.Title.ToLowerInvariant(), s.Artist.ToLowerInvariant())).ToList();
        var done = 0;
        var failedInARow = 0;
        var dirty = false;
        try
        {
        foreach (var song in todo)
        {
            ct.ThrowIfCancellationRequested();
            bool found;
            try
            {
                found = await LearnOneAsync(song, ct).ConfigureAwait(false);
                failedInARow = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidDataException
                                       or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException)
            {
                _log.Info($"In-game radio: the preview of \"{song.Title}\" couldn't be fetched ({ex.Message}).");
                if (++failedInARow >= MaxFailuresInARow)
                {
                    throw new HttpRequestException("The song previews couldn't be fetched. RigControl tries again later.", ex);
                }
                continue;
            }
            states[song.Key] = new State(song.Key, found, _now(), RulesVersion);
            dirty = true;
            progress?.Report((++done, todo.Count));
            if (done % 10 == 0) Save(states.Values, dirty);
        }
        }
        finally
        {
            // Also when switched off or closing: what was learned so far is kept.
            Save(states.Values, dirty);
        }
        return Count;
    }

    private async Task<bool> LearnOneAsync(CatalogueSong song, CancellationToken ct)
    {
        var url = await FindAppleAsync(song, ct).ConfigureAwait(false) ?? await FindDeezerAsync(song, ct).ConfigureAwait(false);
        if (url is null) return false;
        var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
        var sound = await _decode(bytes, ct).ConfigureAwait(false);
        if (sound.Length < Audio.RadioAudio.SampleRate * 5) return false;
        var landmarks = Fingerprinter.Compute(sound);
        if (landmarks.Count < Matcher.MinVotes) return false;
        try { _store.Learn(song.Title, song.Artist, "", null, landmarks, Base); }
        catch (InvalidOperationException) { return false; } // full
        return true;
    }

    private async Task<Uri?> FindAppleAsync(CatalogueSong song, CancellationToken ct)
    {
        var term = LyricsLibrary.FirstArtist(song.Artist) + " " + LyricsLibrary.CleanTitle(song.Title);
        var answer = await GetJsonAsync(new Uri($"{AppleSearch}?media=music&entity=song&limit=15&term={Uri.EscapeDataString(term)}"), ct).ConfigureAwait(false);
        if (_applePace > TimeSpan.Zero) await _wait(_applePace, ct).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("results", out var r) || r.ValueKind != JsonValueKind.Array) return null;
        return Pick(song, r.EnumerateArray().Take(15).Select(x => (Str(x, "artistName"), Str(x, "trackName"), Str(x, "previewUrl"))));
    }

    private async Task<Uri?> FindDeezerAsync(CatalogueSong song, CancellationToken ct)
    {
        var artist = LyricsLibrary.FirstArtist(song.Artist);
        var title = LyricsLibrary.CleanTitle(song.Title);
        // The exact search first; it misses names written a little differently ("The", "&", accents, "!!!"), so
        // then a plain one. Pick decides either way.
        return await AskDeezerAsync(song, $"artist:\"{artist}\" track:\"{title}\"", ct).ConfigureAwait(false)
               ?? await AskDeezerAsync(song, artist + " " + title, ct).ConfigureAwait(false);
    }

    private async Task<Uri?> AskDeezerAsync(CatalogueSong song, string q, CancellationToken ct)
    {
        var answer = await GetJsonAsync(new Uri($"{DeezerSearch}?limit=15&q={Uri.EscapeDataString(q)}"), ct).ConfigureAwait(false);
        if (_deezerPace > TimeSpan.Zero) await _wait(_deezerPace, ct).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Array) return null;
        return Pick(song, d.EnumerateArray().Take(15).Select(x =>
            (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("artist", out var a) ? Str(a, "name") : null, Str(x, "title"), Str(x, "preview"))));
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The result that is this song: the same (first) artist, and the same title. A plain title also takes the
    /// original under a longer name ("Song (2011 Remaster)"), never another take of it (live, remix, acoustic...);
    /// a remix or version in the catalogue must be that one. Only https previews from Apple's or Deezer's servers.
    /// Names are compared loosely only where that can't change the song: guest credits ("feat. X"), "&amp;" vs
    /// "and", a leading "The" in the artist. A song filed under another of its credited artists counts only with
    /// exactly the same title, and only when nothing under the first artist does.
    /// </summary>
    internal static Uri? Pick(CatalogueSong song, IEnumerable<(string? Artist, string? Title, string? Preview)> results)
    {
        var first = LyricsLibrary.FirstArtist(song.Artist);
        var wantArtist = ArtistKey(first);
        var others = Artists(song.Artist).Skip(1).Select(ArtistKey)
            .Where(a => a.Length >= 3 && a is not ("jr" or "sr") && a != wantArtist).Distinct().ToList();
        var full = TitleWords(song.Title);
        var clean = TitleWords(LyricsLibrary.CleanTitle(song.Title));
        // Another take in ours, also when it sits in a credit-like part ("(with X Remix)").
        var ourWords = DropSameTake(LyricsText.Words(song.Title).ToList());
        var oursIsOtherTake = ourWords.Except(LyricsText.Words(LyricsLibrary.CleanTitle(song.Title))).Any(IsOtherTake);
        Uri? near = null, other = null;
        foreach (var (artist, title, preview) in results)
        {
            if (artist is null || title is null || preview is null || full.Count == 0 || first.Length == 0) continue;
            if (!Uri.TryCreate(preview, UriKind.Absolute, out var url) || !AllowedPreviewHost(url)) continue;
            var theirArtist = " " + ArtistKey(artist) + " ";
            var byFirst = wantArtist.Length > 0
                ? theirArtist.Contains(" " + wantArtist + " ", StringComparison.Ordinal)
                : Artists(artist).Any(a => string.Equals(a, first, StringComparison.OrdinalIgnoreCase)); // "!!!" has no words
            var byOther = !byFirst && others.Any(a => theirArtist.Contains(" " + a + " ", StringComparison.Ordinal));
            if (!byFirst && !byOther) continue;
            var theirs = TitleWords(title);
            // The same words, also split differently ("Ou Paka" = "Ou Pa Ka").
            if (theirs.SequenceEqual(full) || string.Concat(theirs) == string.Concat(full))
            {
                // Ours is a remix (also inside a credit: "(feat. X Remix)"): theirs must be another take too.
                if (oursIsOtherTake && !DropSameTake(LyricsText.Words(title).ToList()).Any(IsOtherTake)) continue;
                if (byFirst) return url;
                other ??= url;
                continue;
            }
            if (byFirst && near is null && SameSongOtherName(theirs, full, clean, oursIsOtherTake, ourWords)) near = url;
        }
        return near ?? other;
    }

    /// <summary>
    /// "Isaka (6am)" listed as "Isaka", "1901" as "1901 (2009 Remaster)", "Song (A) (Prod by B)" as "Song (A)",
    /// "Song (Girls Trip)" as "Song (From The Film)". Never a live, remix or other take, and never the plain song
    /// when ours is a remix or version.
    /// </summary>
    private static bool SameSongOtherName(List<string> theirs, List<string> full, List<string> clean, bool oursIsOtherTake, List<string> ourWords)
    {
        // Longer than ours: only a note about the release ("(2011 Remaster)", "(From The Film)"), never "Part 2",
        // "Interlude" or another song that starts the same way.
        if (theirs.Count > full.Count && theirs.Take(full.Count).SequenceEqual(full))
            return oursIsOtherTake
                // Ours is a remix credited to someone: theirs may name it without the "feat." ("Song (X Remix)").
                ? theirs.Skip(full.Count).All(ourWords.Contains) && theirs.Skip(full.Count).Any(IsOtherTake)
                : theirs.Skip(full.Count).All(w => ReleaseNote.Contains(w) || w.All(char.IsAsciiDigit));
        if (oursIsOtherTake || clean.Count == 0 || theirs.Count < clean.Count || !theirs.Take(clean.Count).SequenceEqual(clean)) return false;
        var theirNote = theirs.Skip(clean.Count).ToList();
        if (theirNote.Count == 0) return true; // the plain name
        // Ours cut short ("Song (A) (Prod by B)" -> "Song (A)").
        if (theirs.Count < full.Count && full.Take(theirs.Count).SequenceEqual(theirs)) return true;
        // The plain name with a note that only says where or how it was released ("(From The Film)", "(2011 Remaster)").
        return theirNote.All(w => ReleaseNote.Contains(w) || w.All(char.IsAsciiDigit));
    }

    private static readonly string[] ReleaseNote = ["edit", "radio", "remaster", "remastered", "mono", "stereo", "original", "single", "explicit", "deluxe", "bonus", "track", "album", "from", "the", "film", "movie", "motion", "picture", "soundtrack", "ost", "series", "official", "audio"];

    private static bool IsOtherTake(string word) => OtherTake.Contains(word);

    /// <summary>A title's words without guest credits and without "and" (so "&amp;" and "and" compare the same).</summary>
    internal static List<string> TitleWords(string title)
    {
        var w = LyricsText.Words(Credit.Replace(title, " ")).Where(x => x != "and").ToList();
        if (w.Count == 0) w = LyricsText.Words(title).ToList(); // a title that is all brackets: "(With You)"
        // "(Single Version)", "(Album Version)", "(Radio Edit)": the release itself, not another take of it.
        return DropSameTake(w);
    }

    /// <summary>Removes "single version", "album version" and the like (the release itself, not another take).</summary>
    private static List<string> DropSameTake(List<string> w)
    {
        for (var i = w.Count - 2; i >= 0; i--)
            if (i + 1 < w.Count && (w[i + 1] == "version" && SameTake.Contains(w[i]) || w[i] == "radio" && w[i + 1] is "edit" or "version"))
                w.RemoveRange(i, 2);
        return w;
    }

    private static readonly string[] SameTake = ["single", "album", "original", "mono", "stereo", "lp", "remastered", "remaster"];

    /// <summary>An artist's words for comparing: no "and", no leading "the" ("The Ghostbusters" = "Ghostbusters").</summary>
    internal static string ArtistKey(string artist)
    {
        var w = LyricsText.Words(artist).Where(x => x != "and").ToList();
        if (w.Count > 1 && w[0] == "the") w.RemoveAt(0);
        return string.Join(' ', w);
    }

    private static IEnumerable<string> Artists(string credit) =>
        ArtistSplit.Split(credit).Select(a => a.Trim()).Where(a => a.Length > 0);

    internal static bool AllowedPreviewHost(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort) return false;
        var h = url.IdnHost.ToLowerInvariant();
        return h.EndsWith(".apple.com", StringComparison.Ordinal) || h.EndsWith(".mzstatic.com", StringComparison.Ordinal)
               || h.EndsWith(".dzcdn.net", StringComparison.Ordinal);
    }

    /// <summary>
    /// Downloads a preview. Redirects are followed by hand (the client given here must not follow them itself), at
    /// most 3, each checked against the allowed servers BEFORE it is asked.
    /// </summary>
    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var hop = 0; ; hop++)
        {
            if (!AllowedPreviewHost(url)) throw new InvalidDataException("The preview moved elsewhere.");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)res.StatusCode is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } next && hop < 3)
            {
                url = next.IsAbsoluteUri ? next : new Uri(url, next);
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("The preview server answered " + (int)res.StatusCode + ".");
            return await ReadCappedAsync(res, MaxPreviewBytes, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>A search request; null when nothing came back. Waits when asked to slow down (403/429/503).</summary>
    private async Task<JsonElement?> GetJsonAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("RigControl (https://github.com/noice912/RigControl)");
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            if ((int)res.StatusCode is 403 or 429 or 503)
            {
                if (attempt >= MaxSlowDowns) throw new HttpRequestException("The song search kept asking to wait.");
                var after = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                var wait = TimeSpan.FromSeconds(Math.Clamp(after.TotalSeconds, 10, 180));
                _log.Info($"In-game radio: the song search asked to slow down; waiting {(int)wait.TotalSeconds} s.");
                await _wait(wait, ct).ConfigureAwait(false);
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("The song search answered " + (int)res.StatusCode + ".");
            var bytes = await ReadCappedAsync(res, MaxAnswerBytes, timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return doc.RootElement.Clone();
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage res, int cap, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > cap) throw new InvalidDataException("Answer too big.");
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > cap) throw new InvalidDataException("Answer too big.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private IReadOnlyList<State> LoadStates()
    {
        try
        {
            if (!File.Exists(_statePath)) return [];
            if (SafeFolder.ReachedThroughLink(_statePath) || new FileInfo(_statePath).Length > MaxStateBytes) return [];
            return JsonSerializer.Deserialize<List<State>>(File.ReadAllBytes(_statePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(IEnumerable<State> states, bool dirty)
    {
        if (!dirty) return;
        try
        {
            _store.Save();
            SafeFolder.WriteFile(_statePath, JsonSerializer.SerializeToUtf8Bytes(states.ToList(), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("In-game radio: the song previews couldn't be saved: " + ex.Message);
        }
    }
}
