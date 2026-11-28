using RigControl.Core.Logging;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Speech;

namespace RigControl.Radio.Lyrics;

/// <summary>A catalogue song recognised from its sung words, and where in it the heard sound ends (null: unknown).</summary>
public sealed record LyricsHit(CatalogueSong Song, double? PositionSeconds, double? DurationSeconds, string Heard);

/// <summary>Recognising songs by their lyrics: getting ready (lyrics + speech model) and hearing.</summary>
public interface ILyricsEngine : IDisposable
{
    /// <summary>"off", "preparing", "ready" or "problem".</summary>
    string State { get; }
    string? Detail { get; }
    string? LastHeard { get; }
    bool Ready { get; }

    /// <summary>Downloads what's missing and loads the speech engine. Safe to call again after a problem.</summary>
    Task PrepareAsync(CancellationToken ct);

    /// <summary>Transcribes <paramref name="audio16k"/> and looks the words up. Null: no song recognised.</summary>
    Task<LyricsHit?> HearAsync(float[] audio16k, CancellationToken ct);
}

/// <summary>The real engine: the station catalogue, lyrics from lrclib.net, and Whisper.</summary>
/// <param name="startHelper">Starts the speech helper process (pipe name, use the processor); see SpeechHelperClient.</param>
public sealed class LyricsEngine(HttpClient http, string radioFolder, Func<string, bool, bool> startHelper, ILog log) : ILyricsEngine
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<CatalogueSong> _catalogue = RadioCatalogue.Load();
    private readonly LyricsIndex _index = new();
    private readonly Dictionary<string, (CatalogueSong Song, double? Duration)> _byKey = new(StringComparer.Ordinal);
    private ISpeech? _speech;
    private string _state = "off";
    private string? _detail;
    private string? _lastHeard;
    private bool _disposed;
    private DateTime _lastClosestLog = DateTime.MinValue;

    public string State { get { lock (_gate) return _state; } }
    public string? Detail { get { lock (_gate) return _detail; } }
    public string? LastHeard { get { lock (_gate) return _lastHeard; } }
    public bool Ready { get { lock (_gate) return _speech is not null && _index.Count > 0; } }

    private void Set(string state, string? detail)
    {
        lock (_gate)
        {
            _state = state;
            _detail = detail;
        }
    }

    public async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            var library = new LyricsLibrary(http, Path.Combine(radioFolder, "lyrics.json"), log);
            Set("preparing", "Getting the stations' lyrics...");
            var have = await library.FetchAsync(_catalogue,
                new Progress<(int Done, int Total)>(p => Set("preparing", $"Getting the stations' lyrics ({p.Done} of {p.Total})...")), ct).ConfigureAwait(false);
            library.FillIndex(_index, _catalogue);
            lock (_gate)
                foreach (var e in library.Load().Where(e => !e.Missing))
                    if (_catalogue.FirstOrDefault(s => s.Key == e.Key) is { } song) _byKey[e.Key] = (song, e.Duration);
            log.Info($"In-game radio: lyrics ready for {have} of {_catalogue.Count} songs.");
            if (_index.Count == 0)
            {
                Set("problem", "No lyrics could be fetched yet (is the PC online?). RigControl tries again when you switch this on again.");
                return;
            }

            var model = new SpeechModel(http, radioFolder, SpeechModelSpec.SmallQ5);
            if (!model.IsDownloaded)
            {
                Set("preparing", "Downloading the speech model (190 MB)...");
                await model.DownloadAsync(new Progress<double>(p => Set("preparing", $"Downloading the speech model ({p:P0} of 190 MB)...")), ct).ConfigureAwait(false);
            }
            try { model.LoadVerified(); }
            catch (InvalidDataException)
            {
                Set("preparing", "Downloading the speech model again (190 MB)...");
                await model.DownloadAsync(new Progress<double>(p => Set("preparing", $"Downloading the speech model ({p:P0} of 190 MB)...")), ct, force: true).ConfigureAwait(false);
            }
            // The speech engine runs in its own helper process without administrator rights (it loads and checks the
            // model itself): a crash there can't take RigControl down, and its native code never runs as admin.
            Set("preparing", "Starting speech recognition...");
            var speech = new SpeechHelperClient(startHelper, log);
            try { await speech.StartAsync(ct).ConfigureAwait(false); }
            catch
            {
                speech.Dispose();
                throw;
            }
            lock (_gate)
            {
                if (_disposed || ct.IsCancellationRequested)
                {
                    // Switched off while it was loading: don't keep the model (190 MB) around.
                    speech.Dispose();
                    return;
                }
                _speech?.Dispose();
                _speech = speech;
            }
            Set("ready", $"Recognises {have} songs from the stations by their lyrics, on the {(speech.Device == "gpu" ? "graphics card" : "processor")}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Set("off", null);
        }
        catch (Exception ex)
        {
            log.Warn("In-game radio: lyrics recognition couldn't start: " + ex.Message);
            Set("problem", "Lyrics recognition couldn't start: " + ex.Message);
        }
    }

    public async Task<LyricsHit?> HearAsync(float[] audio16k, CancellationToken ct)
    {
        ISpeech? speech;
        lock (_gate) speech = _speech;
        if (speech is null) return null;
        var text = await speech.TranscribeAsync(audio16k, ct).ConfigureAwait(false);
        lock (_gate) _lastHeard = text.Length > 200 ? text[..200] : text;
        var match = _index.Match(text);
        if (match is null)
        {
            // How close it came, for the log (song name and counts only, never words), at most every 10 s.
            if (_index.Closest(text) is { Hits: >= 2 } c && DateTime.UtcNow - _lastClosestLog > TimeSpan.FromSeconds(10))
            {
                _lastClosestLog = DateTime.UtcNow;
                var title = _byKey.TryGetValue(c.SongKey, out var cs) ? cs.Song.Title : "?";
                log.Info($"In-game radio: closest song \"{title}\": {c.Hits} of {c.HeardRuns} word runs, score {c.Score:F1}, lead {c.Lead:F1} (not enough).");
            }
            return null;
        }
        lock (_gate)
            return _byKey.TryGetValue(match.SongKey, out var s) ? new LyricsHit(s.Song, match.PositionSeconds, s.Duration, text) : null;
    }

    public void Dispose()
    {
        ISpeech? speech;
        lock (_gate)
        {
            _disposed = true;
            speech = _speech;
            _speech = null;
            _state = "off";
        }
        speech?.Dispose();
    }
}
