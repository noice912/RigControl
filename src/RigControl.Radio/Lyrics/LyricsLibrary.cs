using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using RigControl.Core.Logging;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Store;

namespace RigControl.Radio.Lyrics;

/// <summary>
/// The catalogue songs' lyrics, fetched once from lrclib.net on this PC and kept in <c>radio\lyrics.json</c>. A song
/// LRCLIB doesn't have is asked again after a day. Only https://lrclib.net is contacted; answers are size-capped.
/// </summary>
public sealed class LyricsLibrary(HttpClient http, string cachePath, ILog log, Func<DateTimeOffset>? now = null, TimeSpan? pause = null)
{
    public const string Host = "https://lrclib.net";
    public const int MaxAnswerBytes = 256 * 1024;
    public const long MaxCacheBytes = 64L * 1024 * 1024;
    private const int MaxFailuresInARow = 5;
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _pause = pause ?? TimeSpan.FromMilliseconds(350); // about 3 requests a second at most

    public sealed record Entry(string Key, string Title, string Artist, string? Synced, string? Plain, double? Duration, bool Missing, DateTimeOffset FetchedAt);

    /// <summary>Fetches every song not kept yet (or missing for over a day). Returns how many songs have lyrics now.</summary>
    public async Task<int> FetchAsync(IReadOnlyList<CatalogueSong> songs, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var kept = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in Load()) kept[e.Key] = e; // a damaged file may repeat a song: the last one counts
        var todo = songs.Where(s => !kept.TryGetValue(s.Key, out var e) || (e.Missing && _now() - e.FetchedAt > RetryMissingAfter)).ToList();
        var done = 0;
        var failedInARow = 0;
        var changed = false;
        foreach (var song in todo)
        {
            ct.ThrowIfCancellationRequested();
            Entry entry;
            try
            {
                entry = await FetchOneAsync(song, ct).ConfigureAwait(false);
                failedInARow = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Not reached (offline, LRCLIB down, too slow): nothing is marked missing; stop after a few in a row.
                log.Info($"In-game radio: lyrics for \"{song.Title}\" couldn't be fetched ({ex.GetType().Name}).");
                if (++failedInARow >= MaxFailuresInARow)
                {
                    if (changed) Save(kept.Values);
                    throw new HttpRequestException("lrclib.net couldn't be reached. RigControl tries again later.", ex);
                }
                continue;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // An answer that can't be used: count the song as missing for now.
                log.Info($"In-game radio: LRCLIB's answer for \"{song.Title}\" couldn't be used ({ex.GetType().Name}).");
                entry = new Entry(song.Key, song.Title, song.Artist, null, null, null, true, _now());
            }
            kept[song.Key] = entry;
            changed = true;
            progress?.Report((++done, todo.Count));
            if (done % 25 == 0) Save(kept.Values);
            if (_pause > TimeSpan.Zero) await Task.Delay(_pause, ct).ConfigureAwait(false);
        }
        if (changed) Save(kept.Values);
        return kept.Values.Count(e => !e.Missing && songs.Any(s => s.Key == e.Key));
    }

    /// <summary>Adds every kept song's lyrics to <paramref name="index"/>. Returns how many were added.</summary>
    public int FillIndex(LyricsIndex index, IReadOnlyList<CatalogueSong> songs)
    {
        var wanted = songs.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var e in Load())
        {
            if (e.Missing || !wanted.Contains(e.Key)) continue;
            var lines = LyricsText.ParseSynced(e.Synced);
            if (lines.Count == 0 && !string.IsNullOrWhiteSpace(e.Plain))
                lines = e.Plain.Split('\n').Where(l => l.Trim().Length > 0).Select(l => new LyricLine(-1, l)).ToList();
            if (lines.Count == 0) continue;
            var before = index.Count;
            index.Add(e.Key, lines, e.Duration);
            if (index.Count > before) added++;
        }
        return added;
    }

    public IReadOnlyList<Entry> Load()
    {
        try
        {
            if (!File.Exists(cachePath)) return [];
            if (SafeFolder.ReachedThroughLink(cachePath) || new FileInfo(cachePath).Length > MaxCacheBytes)
            {
                log.Warn("In-game radio: the lyrics file is too big or reached through a link, so it wasn't read.");
                return [];
            }
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(cachePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Warn("In-game radio: the lyrics file couldn't be read (" + ex.GetType().Name + "); it will be fetched again.");
            return [];
        }
    }

    private void Save(IEnumerable<Entry> entries)
    {
        try { SafeFolder.WriteFile(cachePath, JsonSerializer.SerializeToUtf8Bytes(entries.ToList(), Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("In-game radio: the lyrics couldn't be saved: " + ex.Message);
        }
    }

    private async Task<Entry?> FetchOneAsync(CatalogueSong song, CancellationToken ct)
    {
        var artist = FirstArtist(song.Artist);
        var title = song.Title;
        var got = await GetAsync($"/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", ct).ConfigureAwait(false);
        if (got is { ValueKind: JsonValueKind.Object } o) return FromJson(song, o);
        // Not under that exact name: the first search result whose title starts the same way.
        var found = await GetAsync($"/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", ct).ConfigureAwait(false);
        if (found is { ValueKind: JsonValueKind.Array } list)
        {
            // Same start of the title AND the same (first) artist: never another singer's song of the same name.
            var want = LyricsText.Words(title).Take(2).ToList();
            var wantArtist = string.Join(' ', LyricsText.Words(artist));
            foreach (var item in list.EnumerateArray().Take(10))
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("trackName", out var tn) || tn.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("artistName", out var an) || an.ValueKind != JsonValueKind.String) continue;
                var theirArtist = " " + string.Join(' ', LyricsText.Words(an.GetString())) + " ";
                if (LyricsText.Words(tn.GetString()).Take(2).SequenceEqual(want) && wantArtist.Length > 0
                    && theirArtist.Contains(" " + wantArtist + " ", StringComparison.Ordinal))
                    return FromJson(song, item);
            }
        }
        return new Entry(song.Key, song.Title, song.Artist, null, null, null, true, _now());
    }

    private Entry FromJson(CatalogueSong song, JsonElement o)
    {
        string? Str(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? duration = o.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var x) && x is > 0 and < 86400 ? x : null;
        var instrumental = o.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        var synced = instrumental ? null : Str("syncedLyrics");
        var plain = instrumental ? null : Str("plainLyrics");
        var has = !string.IsNullOrWhiteSpace(synced) || !string.IsNullOrWhiteSpace(plain);
        return new Entry(song.Key, song.Title, song.Artist, synced, plain, duration, !has, _now());
    }

    /// <summary>One request to lrclib.net; null for "not found". Throws on errors and oversized answers.</summary>
    private async Task<JsonElement?> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Host + pathAndQuery);
        req.Headers.UserAgent.ParseAdd("RigControl (https://github.com/noice912/RigControl)");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new HttpRequestException("LRCLIB answered " + (int)res.StatusCode + ".");
        if (res.Content.Headers.ContentLength is > MaxAnswerBytes) throw new InvalidDataException("Answer too big.");
        await using var s = await res.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxAnswerBytes) throw new InvalidDataException("Answer too big.");
            ms.Write(buf, 0, n);
        }
        using var doc = JsonDocument.Parse(ms.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        return doc.RootElement.Clone();
    }

    /// <summary>"Usher, Pitbull" -> "Usher"; "Prospa ft. Halsey" -> "Prospa"; "Rossi. & Demi" -> "Rossi.".</summary>
    internal static string FirstArtist(string artists)
    {
        var cut = artists.Length;
        foreach (var sep in new[] { ",", " & ", " ft. ", " feat. ", " featuring ", " w/ ", " x " })
        {
            var i = artists.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && i < cut) cut = i;
        }
        return artists[..cut].Trim();
    }
}
