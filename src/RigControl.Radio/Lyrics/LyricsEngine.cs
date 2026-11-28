using RigControl.Core.Logging;
using RigControl.Radio.Catalogue;
using RigControl.Radio.Fingerprint;
using RigControl.Radio.Previews;
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

    /// <summary>A catalogue song recognised from its sound (its preview), and where in the preview frame the sound ends.</summary>
    (CatalogueSong Song, double PositionSeconds)? MatchSound(IReadOnlyList<Landmark> query, double heardSeconds);
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
    private PreviewLibrary? _previews;
    private int _lyricsSongs;
    private string? _previewProgress;
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

            // The processor model (60 MB): the helper runs on the processor (see below).
            var model = new SpeechModel(http, radioFolder, SpeechModelSpec.BaseQ5);
            var mb = model.Spec.Size / (1024 * 1024);
            if (!model.IsDownloaded)
            {
                Set("preparing", $"Downloading the speech model ({mb} MB)...");
                await model.DownloadAsync(new Progress<double>(p => Set("preparing", $"Downloading the speech model ({p:P0} of {mb} MB)...")), ct).ConfigureAwait(false);
            }
            try { model.LoadVerified(); }
            catch (InvalidDataException)
            {
                Set("preparing", $"Downloading the speech model again ({mb} MB)...");
                await model.DownloadAsync(new Progress<double>(p => Set("preparing", $"Downloading the speech model ({p:P0} of {mb} MB)...")), ct, force: true).ConfigureAwait(false);
            }
            // The speech engine runs in its own helper process without administrator rights (it loads and checks the
            // model itself): a crash there can't take RigControl down, and its native code never runs as admin.
            Set("preparing", "Starting speech recognition...");
            // The processor: the graphics-card (Vulkan) engine crashed within seconds on the owner's PC; the processor
            // handles 8 s of sound in about a second, which is plenty for listening every 2 s.
            var speech = new SpeechHelperClient(startHelper, log, Path.Combine(radioFolder, "speech-on-processor"), preferGpu: false);
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
            // What every song sounds like (also songs without words): learned in the background from free previews.
            // Its own client that never follows redirects by itself (PreviewLibrary checks each one first).
            var previews = new PreviewLibrary(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) },
                radioFolder, _catalogue, speech.DecodeAsync, log);
            lock (_gate)
            {
                _previews = previews;
                _lyricsSongs = have;
            }
            Set("ready", ReadyText());
            _ = Task.Run(() => LearnPreviewsAsync(previews, ct), CancellationToken.None);
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

    private string ReadyText()
    {
        lock (_gate)
        {
            var sound = _previews?.Count ?? 0;
            var text = $"Recognises {sound} of {_catalogue.Count} songs by their sound and {_lyricsSongs} by their lyrics.";
            return _previewProgress is { } p ? text + " " + p : text;
        }
    }

    private async Task LearnPreviewsAsync(PreviewLibrary previews, CancellationToken ct)
    {
        try
        {
            var have = await previews.FetchAsync(_catalogue, new Progress<(int Done, int Total)>(p =>
            {
                lock (_gate) _previewProgress = $"Learning what the songs sound like ({p.Done} of {p.Total})...";
                if (State == "ready") Set("ready", ReadyText());
            }), ct).ConfigureAwait(false);
            log.Info($"In-game radio: knows the sound of {have} of {_catalogue.Count} songs.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            log.Info("In-game radio: learning the songs' sound stopped for now: " + ex.Message);
        }
        lock (_gate) _previewProgress = null;
        if (State == "ready") Set("ready", ReadyText());
    }

    public (CatalogueSong Song, double PositionSeconds)? MatchSound(IReadOnlyList<Landmark> query, double heardSeconds)
    {
        PreviewLibrary? p;
        lock (_gate) p = _previews;
        return p?.Match(query, heardSeconds);
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
                log.Info($"In-game radio: closest song \"{title}\": {c.Hits} of {c.HeardRuns} words lined up, score {c.Score:F1}, lead {c.Lead:F1} (not enough).");
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
