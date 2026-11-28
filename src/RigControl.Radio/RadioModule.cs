using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Capture.Listen;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Radio.Audio;
using RigControl.Radio.Fingerprint;
using RigControl.Radio.Lyrics;
using RigControl.Radio.Store;

namespace RigControl.Radio;

/// <summary>
/// In-game radio: listens to the capture card's sound (only while switched on), recognises songs this PC has learned
/// from their audio fingerprints, and hands the current one to Discord status (<c>presence.setRadioMusic</c>).
/// Songs are learned by naming them once; while a song plays, more of it is learned. Nothing leaves the PC.
/// </summary>
public sealed class RadioModule : IAppModule
{
    /// <summary>The detector checks the newest sound every second (fingerprints take a few milliseconds).</summary>
    public const int TickMs = 1000;
    public const float SilenceRms = 0.003f;
    private const int BufferSeconds = 15, MatchSeconds = 10, NameSeconds = 15, LearnEverySeconds = 15;
    /// <summary>A song is taken off after this long without sound (a pause menu, a loading screen, the radio off).</summary>
    private const double QuietSecondsToClear = 9;
    private const double AgreeSeconds = 3, LostAfterSeconds = 60, SaveEverySeconds = 60, UnknownLengthCap = 600;

    private readonly IRadioInput _input;
    private readonly Func<string, JsonObject, Task> _presence;
    private readonly string? _storePathOverride;
    private readonly bool _runLoop;
    private readonly object _gate = new();
    private readonly RingBuffer _buffer = new(RadioAudio.SampleRate * BufferSeconds);
    private readonly SemaphoreSlim _ticking = new(1, 1);

    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private RadioSettings _settings = new();
    private LearnedSongs? _store;
    private Timer? _timer;
    private ModuleStatus _status = ModuleStatus.NotStarted;

    // Listening state (under _gate).
    private IDisposable? _session;
    private AudioEndpoint? _opened;
    private MonoResampler? _resampler;
    private (int Rate, int Channels) _format;
    private string? _problem;
    private long _samples;                 // 16 kHz samples heard since start (the detector's clock)
    private bool _sound;
    private long? _quietSince;             // _samples when the sound stopped (null while there is sound)
    private (int SongId, double Position, long At)? _pending;
    private (int SongId, double Position, long At)? _current;
    private long _lastMatchAt, _lastLearnAt, _lastSaveAt;
    private long _openedAt;                // _samples when the input was opened (sound before it doesn't count)
    private long _since;                   // _samples when the current song was first recognised
    private int _generation;               // bumped whenever a session is closed: its late callbacks are ignored
    private bool _owesClear;               // a song ended on the capture thread; the next tick tells Discord
    private bool _dirty;
    private RadioStatusDto? _published;

    /// <param name="presence">Calls a Discord status method (the app passes the RPC dispatcher).</param>
    /// <param name="lyrics">Makes the lyrics engine (the app passes the real one; tests a fake). Null: the real one.</param>
    public RadioModule(IRadioInput input, Func<string, JsonObject, Task> presence, string? storePath = null, bool runLoop = true,
        Func<ModuleContext, ILyricsEngine>? lyrics = null)
    {
        _input = input;
        _presence = presence;
        _storePathOverride = storePath;
        _runLoop = runLoop;
        _lyricsFactory = lyrics;
    }

    private readonly Func<ModuleContext, ILyricsEngine>? _lyricsFactory;
    private ILyricsEngine? _lyrics;
    private CancellationTokenSource? _lyricsCts;
    private Task _hearing = Task.CompletedTask;
    private int _hearFailures;
    private string? _lyricsProblem;            // listening keeps failing: shown, and not tried again until restarted
    private long _lastHearAt = long.MinValue / 2;
    /// <summary>Listen for lyrics every 2 s (the graphics card needs about 0.2 s for 8 s of sound).</summary>
    private const double HearEverySeconds = 2, HearSeconds = 8;
    /// <summary>The confirming lyrics hit must come from sound at least this much newer than the first one's.</summary>
    private const double ConfirmGapSeconds = 6;
    /// <summary>An answer for sound older than this is dropped (the station may have changed meanwhile).</summary>
    private const double MaxHitAgeSeconds = 2;
    /// <summary>The confirming hit: at least one listen later (no shared sound), and within this long.</summary>
    private const double ConfirmWithinSeconds = 40, PlaceSlackSeconds = 5;
    /// <summary>A song heard once by its lyrics, waiting for a second, independent hit. Never saved until then.</summary>
    private (Catalogue.CatalogueSong Song, double? Position, double? Duration, long EndAt)? _lyricCandidate;

    /// <summary>For tests: waits until the lyrics listening started by the last tick has finished.</summary>
    internal Task HearingDone()
    {
        lock (_gate) return _hearing;
    }

    public string Name => "radio";
    public string DisplayName => "In-game radio";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        var path = _storePathOverride ?? Path.Combine(context.Environment.DataDirectory, "radio", "learned.rcfp");
        _store = LearnedSongs.Load(path, _log);
        lock (_gate) _settings = context.Settings.Get<RadioSettings>(RadioSettings.Section);
        _status = new ModuleStatus(ModuleState.Ready);
        if (_settings.Lyrics) StartLyrics();
        if (_runLoop) _timer = new Timer(_ => _ = TickAsync(), null, 1000, TickMs);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- the loop

    /// <summary>Test hook: runs just before fingerprinting, outside every lock.</summary>
    internal Action? BeforeMatch { get; set; }

    /// <summary>One check: opens or closes the input, then recognises. A tick still running makes the next one skip.</summary>
    public async Task TickAsync()
    {
        if (!await _ticking.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            IDisposable? stale;
            JsonObject? clear;
            lock (_gate) stale = Reconcile(out clear);
            // Closing waits for the capture thread, whose callbacks take _gate: never under the lock.
            DisposeQuietly(stale);
            if (clear is not null) await Notify(clear).ConfigureAwait(false);
            var send = Recognise();
            if (send is not null) await Notify(send).ConfigureAwait(false);
            SaveIfDue(force: false);
            Publish();
        }
        catch (Exception ex)
        {
            _log.Error("In-game radio check failed.", ex);
        }
        finally
        {
            _ticking.Release();
        }
    }

    /// <summary>
    /// Opens the capture card input when switched on and it's there; otherwise detaches it. Returns the session to
    /// close (by the caller, outside the lock) and, in <paramref name="clear"/>, "no song" for Discord when one ended.
    /// </summary>
    private IDisposable? Reconcile(out JsonObject? clear)
    {
        clear = null;
        if (_owesClear)
        {
            _owesClear = false;
            clear = new JsonObject();
        }
        if (!_settings.Enabled)
        {
            _problem = null;
            return Detach(ref clear, "switched off");
        }
        IReadOnlyList<AudioEndpoint> inputs;
        try { inputs = _input.Inputs(); }
        catch (Exception ex)
        {
            _problem = "Windows' sound inputs couldn't be listed: " + ex.Message;
            return Detach(ref clear, "the capture card is gone");
        }
        var want = (_settings.InputId is { Length: > 0 } id ? inputs.FirstOrDefault(d => d.Id == id) : null) ?? CaptureCardPicker.Pick(inputs);
        if (_session is not null && _opened is not null && want?.Id == _opened.Id && want.Enabled) return null;
        var stale = Detach(ref clear, "the capture card is gone");
        if (want is null)
        {
            _problem = "No capture card sound input found.";
            return stale;
        }
        if (!want.Enabled)
        {
            _problem = "The capture card's sound input is switched off in Windows (turn it on under Recording > Capture card sound).";
            return stale;
        }
        // Fresh start: nothing heard before this moment counts.
        _buffer.Clear();
        _openedAt = Interlocked.Read(ref _samples);
        var gen = ++_generation;
        try
        {
            _session = _input.Open(want, (s, r, c) => OnAudio(gen, s, r, c), ex => OnStopped(gen, ex));
            _opened = want;
            _problem = null;
            _log.Info("In-game radio: listening to " + want.Name + ".");
        }
        catch (Exception ex)
        {
            _session = null;
            _problem = "The capture card's sound couldn't be opened: " + ex.Message;
        }
        return stale;
    }

    /// <summary>Forgets the open session (returned, to be closed outside the lock) and any song it was hearing.</summary>
    private IDisposable? Detach(ref JsonObject? clear, string why)
    {
        var s = _session;
        _session = null;
        _opened = null;
        _resampler = null;
        _pending = null;
        _sound = false;
        _generation++;
        if (_current is not null) clear = Clear(why);
        return s;
    }

    private static void DisposeQuietly(IDisposable? d)
    {
        try { d?.Dispose(); } catch (Exception) { /* already gone */ }
    }

    private void OnAudio(int gen, float[] samples, int rate, int channels)
    {
        MonoResampler resampler;
        lock (_gate)
        {
            if (gen != _generation) return; // a session that was closed in the meantime
            if (_resampler is null || _format != (rate, channels))
            {
                _resampler = new MonoResampler(rate, channels);
                _format = (rate, channels);
            }
            resampler = _resampler;
        }
        var output = new float[(int)Math.Ceiling(samples.Length / (double)Math.Max(1, channels) * RadioAudio.SampleRate / Math.Max(1, rate)) + 16];
        int n;
        lock (resampler) n = resampler.Process(samples, output);
        _buffer.Write(output.AsSpan(0, n));
        Interlocked.Add(ref _samples, n);
    }

    private void OnStopped(int gen, Exception? ex)
    {
        IDisposable? stale;
        lock (_gate)
        {
            if (gen != _generation) return; // closed by us, not by Windows
            _log.Info("In-game radio: the capture card's sound stopped" + (ex is null ? "." : ": " + ex.Message));
            JsonObject? clear = null;
            stale = Detach(ref clear, "the capture card's sound stopped");
            if (clear is not null) _owesClear = true;
            _problem = "The capture card's sound stopped. Trying again...";
        }
        // This runs on the capture thread, and closing joins that thread: close from elsewhere.
        if (stale is not null) _ = Task.Run(() => DisposeQuietly(stale));
    }

    /// <summary>Returns what to tell Discord status this tick (null: nothing). Fingerprinting runs outside the lock.</summary>
    private JsonObject? Recognise()
    {
        int gen;
        long now;
        lock (_gate)
        {
            if (_session is null) return null;
            gen = _generation;
            now = Interlocked.Read(ref _samples);
        }
        var sound = _buffer.Rms(RadioAudio.SampleRate * 3) >= SilenceRms;
        float[] heard;
        lock (_gate)
        {
            if (gen != _generation) return null;
            _sound = sound;
            if (!sound)
            {
                _pending = null;
                // The level is measured over the last 3 s, so the quiet began at least that long ago.
                _quietSince ??= now - RadioAudio.SampleRate * 3;
                if ((now - _quietSince.Value) / (double)RadioAudio.SampleRate >= QuietSecondsToClear && _current is not null)
                    return Clear("the capture card went quiet");
                return null;
            }
            _quietSince = null;
            heard = _buffer.Last(RadioAudio.SampleRate * MatchSeconds);
        }
        BeforeMatch?.Invoke();
        var match = _store!.Match(Fingerprinter.Compute(heard), heard.Length / (double)RadioAudio.SampleRate);
        (int SongId, double Start)? learn = null;
        lock (_gate)
        {
            if (gen != _generation) return null;
            if (match is not null)
            {
                _lastMatchAt = now;
                var agrees = _pending is { } p && p.SongId == match.SongId
                             && Math.Abs(p.Position + (now - p.At) / (double)RadioAudio.SampleRate - match.PositionSeconds) <= AgreeSeconds;
                var isCurrent = _current is { } c && c.SongId == match.SongId;
                _pending = (match.SongId, match.PositionSeconds, now);
                if (agrees || isCurrent)
                {
                    if (!isCurrent)
                    {
                        _log.Info("In-game radio: recognised " + Describe(match.SongId) + ".");
                        _since = now;
                    }
                    _current = (match.SongId, match.PositionSeconds, now);
                    learn = PlanLearning(match, now);
                }
            }
            else
            {
                _pending = null;
                MaybeHear(heard, gen, now);
                if (_current is not null && (now - _lastMatchAt) / (double)RadioAudio.SampleRate > LostAfterSeconds)
                    return Clear("it hasn't been heard for a minute");
            }
        }
        if (learn is { } l) LearnMore(l.SongId, l.Start);
        lock (_gate) return _current is null ? null : SongPayload();
    }

    // ---------------------------------------------------------------- lyrics

    private void StartLyrics()
    {
        if (_ctx is null) return;
        StopLyrics();
        _lyrics = (_lyricsFactory ?? DefaultLyrics)(_ctx);
        var cts = _lyricsCts = new CancellationTokenSource();
        var engine = _lyrics;
        var prepare = engine.PrepareAsync(cts.Token);
        _ = prepare.ContinueWith(_ => Publish(), TaskScheduler.Default);
    }

    /// <summary>Under _gate: forgets the engine; it is closed on another thread (closing waits for a running listen).</summary>
    private void StopLyrics()
    {
        _lyricsCts?.Cancel();
        _lyricsCts = null;
        var old = _lyrics;
        var hearing = _hearing;
        _lyrics = null;
        _lyricCandidate = null;
        if (old is not null)
            _ = Task.Run(async () =>
            {
                try { await hearing.ConfigureAwait(false); } catch (Exception) { /* done either way */ }
                old.Dispose();
            });
    }

    private static ILyricsEngine DefaultLyrics(ModuleContext ctx)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var folder = Path.Combine(ctx.Environment.DataDirectory, "radio");
        return new LyricsEngine(http, folder, StartSpeechHelper, ctx.Log);
    }

    /// <summary>
    /// Starts the speech helper process WITHOUT administrator rights (pipe name, use the processor). The app sets it;
    /// unset, lyrics recognition says the speech engine couldn't start.
    /// </summary>
    public static Func<string, bool, bool> StartSpeechHelper { get; set; } = (_, _) => false;

    /// <summary>Where RigControl's own native files are: its folder and the folders .NET unpacked them to.</summary>
    public static Func<IEnumerable<string>> NativeFolders { get; set; } = () =>
    {
        var dirs = new List<string> { AppContext.BaseDirectory };
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string list)
            dirs.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    };

    /// <summary>
    /// No learned song matched: let the lyrics engine listen to the newest 8 s (at most every 4 s, one at a time). Under _gate.
    /// </summary>
    private void MaybeHear(float[] heard, int gen, long now)
    {
        if (!_settings.Lyrics || _lyricsProblem is not null || _lyrics is not { Ready: true } engine || !_hearing.IsCompleted) return;
        if ((now - _lastHearAt) / (double)RadioAudio.SampleRate < HearEverySeconds) return;
        _lastHearAt = now;
        var take = Math.Min(heard.Length, (int)(HearSeconds * RadioAudio.SampleRate));
        var audio = heard[^take..];
        var ct = _lyricsCts?.Token ?? CancellationToken.None;
        _hearing = Task.Run(async () =>
        {
            try
            {
                var hit = await engine.HearAsync(audio, ct).ConfigureAwait(false);
                lock (_gate) _hearFailures = 0;
                if (hit is not null && OnHeard(gen, hit, audio, now) is { } send) await Notify(send).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* switched off */ }
            catch (Exception ex)
            {
                _log.Info("In-game radio: listening for lyrics failed: " + ex.Message);
                lock (_gate)
                {
                    if (++_hearFailures >= 3 && _lyricsProblem is null)
                    {
                        _lyricsProblem = "Speech recognition stopped working (" + ex.Message + "). Please restart RigControl.";
                        _log.Warn("In-game radio: " + _lyricsProblem);
                    }
                }
            }
            Publish();
        });
    }

    /// <summary>
    /// A song recognised from its words. One hit is only a candidate, kept in memory: it is shown (and learned) only when
    /// a second hit for the same song comes from later sound that shares nothing with the first, at a place in the song
    /// that moved on by about as much as the time between them. An answer for sound more than 2 s old is dropped.
    /// Returns what to tell Discord status (null: nothing).
    /// </summary>
    private JsonObject? OnHeard(int gen, LyricsHit hit, float[] audio, long endAt)
    {
        var seconds = audio.Length / (double)RadioAudio.SampleRate;
        lock (_gate)
        {
            if (gen != _generation) return null;
            var age = (Interlocked.Read(ref _samples) - endAt) / (double)RadioAudio.SampleRate;
            if (age > MaxHitAgeSeconds)
            {
                _log.Info($"In-game radio: a lyrics answer came {age:F0} s late and was dropped.");
                return null;
            }
            if (_lyricCandidate is { } c && c.Song.Key == hit.Song.Key)
            {
                var gap = (endAt - c.EndAt) / (double)RadioAudio.SampleRate;
                if (gap < ConfirmGapSeconds) return null; // mostly the first hit's sound: proves nothing new, keep waiting
                var placeAgrees = (c.Position, hit.PositionSeconds) switch
                {
                    (null, null) => true,
                    ({ } p1, { } p2) => Math.Abs(p2 - p1 - gap) <= PlaceSlackSeconds,
                    _ => false,
                };
                if (gap <= ConfirmWithinSeconds && placeAgrees) return Confirm(hit, audio, endAt, seconds);
            }
            _lyricCandidate = (hit.Song, hit.PositionSeconds, hit.DurationSeconds, endAt);
            _log.Info("In-game radio: maybe \"" + hit.Song.Title + "\" (lyrics); waiting for a second hit.");
            return null;
        }
    }

    /// <summary>Second, agreeing lyrics hit: learn this sound under the song and show it. Under _gate.</summary>
    private JsonObject? Confirm(LyricsHit hit, float[] audio, long endAt, double seconds)
    {
        _lyricCandidate = null;
        var landmarks = Fingerprinter.Compute(audio);
        var position = hit.PositionSeconds ?? seconds;
        LearnedSong song;
        try
        {
            song = _store!.Learn(hit.Song.Title, hit.Song.Artist, "", hit.PositionSeconds is null ? null : hit.DurationSeconds,
                landmarks, Math.Max(0, position - seconds));
        }
        catch (InvalidOperationException) { return null; } // store full
        _dirty = true;
        _current = (song.Id, position, endAt);
        _pending = (song.Id, position, endAt);
        _since = endAt;
        _lastMatchAt = endAt;
        _log.Info("In-game radio: recognised " + Describe(song.Id) + " by its lyrics.");
        return SongPayload();
    }

    /// <summary>
    /// While a song plays, its newest 15 s are learned at their place in it, every 15 s, only when that stretch isn't
    /// learned yet, the 15 s were all heard since the input opened, and it's within the song's length.
    /// </summary>
    private (int SongId, double Start)? PlanLearning(MatchResult match, long now)
    {
        if ((now - _lastLearnAt) / (double)RadioAudio.SampleRate < LearnEverySeconds) return null;
        if ((now - _openedAt) / (double)RadioAudio.SampleRate < NameSeconds) return null;
        var song = _store!.Get(match.SongId);
        if (song is null) return null;
        var start = match.PositionSeconds - NameSeconds;
        if (start < 0 || start > (song.DurationSeconds ?? UnknownLengthCap)) return null;
        if (_store.Covers(song.Id, start, NameSeconds)) return null;
        _lastLearnAt = now;
        return (song.Id, start);
    }

    private void LearnMore(int songId, double start)
    {
        var song = _store!.Get(songId);
        if (song is null) return;
        try
        {
            _store.Learn(song.Title, song.Artist, song.Album, song.DurationSeconds,
                Fingerprinter.Compute(_buffer.Last(RadioAudio.SampleRate * NameSeconds)), start);
            lock (_gate) _dirty = true;
        }
        catch (InvalidOperationException) { /* store full: keep recognising */ }
    }

    private JsonObject Clear(string why)
    {
        _log.Info("In-game radio: " + Describe(_current!.Value.SongId) + " ended (" + why + ").");
        _current = null;
        _pending = null;
        return new JsonObject();
    }

    private JsonObject? SongPayload()
    {
        var dto = CurrentDto();
        if (dto is null) return new JsonObject();
        var o = new JsonObject { ["title"] = dto.Title, ["artist"] = dto.Artist, ["album"] = dto.Album };
        // A song named by hand has no known place: Discord counts from when it was recognised, so its timer runs on.
        var pos = dto.PositionSeconds ?? (Interlocked.Read(ref _samples) - _since) / (double)RadioAudio.SampleRate;
        o["position"] = Math.Round(Math.Max(0, pos), 1);
        if (dto.DurationSeconds is { } d) o["duration"] = Math.Round(d, 1);
        return o;
    }

    private RadioSongDto? CurrentDto()
    {
        if (_current is not { } c || _store?.Get(c.SongId) is not { } s) return null;
        var known = s.DurationSeconds is not null;
        var pos = c.Position + (Interlocked.Read(ref _samples) - c.At) / (double)RadioAudio.SampleRate;
        return new RadioSongDto(s.Id, s.Title, s.Artist, s.Album, known ? Math.Min(pos, s.DurationSeconds!.Value) : null, s.DurationSeconds);
    }

    private string Describe(int id) => _store?.Get(id) is { } s ? $"\"{s.Title}\" by {s.Artist}" : "a song";

    private async Task Notify(JsonObject args)
    {
        try { await _presence("presence.setRadioMusic", args).ConfigureAwait(false); }
        catch (Exception ex) { _log.Info("In-game radio: Discord status didn't take the song: " + ex.Message); }
    }

    private void SaveIfDue(bool force)
    {
        if (_store is null) return;
        var now = Interlocked.Read(ref _samples);
        lock (_gate)
        {
            if (!_dirty || (!force && (now - _lastSaveAt) / (double)RadioAudio.SampleRate < SaveEverySeconds)) return;
            _dirty = false;
            _lastSaveAt = now;
        }
        try { _store.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("In-game radio: the learned songs couldn't be saved: " + ex.Message);
            lock (_gate) _dirty = true;
        }
    }

    // ---------------------------------------------------------------- RPC

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        switch (method)
        {
            case "getStatus": return GetStatus();
            case "setSettings": return await SetSettings(RpcArgs.Require<SettingsArgs>(args)).ConfigureAwait(false);
            case "nameSong": return await NameSong(RpcArgs.Require<SongArgs>(args)).ConfigureAwait(false);
            case "listSongs": return ListSongs();
            case "renameSong": return RenameSong(RpcArgs.Require<SongArgs>(args));
            case "forgetSong": return await ForgetSong(RpcArgs.Require<IdArgs>(args).Id).ConfigureAwait(false);
            default: throw RpcException.UnknownMethod(Name, method);
        }
    }

    public RadioStatusDto GetStatus()
    {
        lock (_gate) return BuildStatus();
    }

    private RadioStatusDto BuildStatus()
    {
        var state = !_settings.Enabled ? "off" : _session is not null ? "listening" : "waiting";
        var lyrics = _lyricsProblem is not null && _settings.Lyrics
            ? new RadioLyricsDto(true, "problem", _lyricsProblem, _lyrics?.LastHeard)
            : new RadioLyricsDto(_settings.Lyrics, _lyrics?.State ?? "off", _lyrics?.Detail, _lyrics?.LastHeard);
        return new RadioStatusDto(_settings.Enabled, state, _problem, CurrentDto(), _sound, _store?.Songs.Count ?? 0, lyrics);
    }

    private async Task<RadioStatusDto> SetSettings(SettingsArgs a)
    {
        JsonObject? clear = null;
        lock (_gate)
        {
            if (a.Enabled is { } on) _settings.Enabled = on;
            if (a.InputId is not null) _settings.InputId = a.InputId.Length == 0 ? null : a.InputId;
            if (a.Lyrics is { } lyricsOn && lyricsOn != _settings.Lyrics)
            {
                _settings.Lyrics = lyricsOn;
                if (lyricsOn) StartLyrics();
                else StopLyrics();
            }
            _ctx?.Settings.Set(RadioSettings.Section, _settings);
            if (!_settings.Enabled && _current is not null) clear = Clear("switched off");
        }
        if (clear is not null) await Notify(clear).ConfigureAwait(false);
        await TickAsync().ConfigureAwait(false);
        return GetStatus();
    }

    private async Task<RadioStatusDto> NameSong(SongArgs a)
    {
        var title = Text(a.Title, "title");
        var artist = Text(a.Artist, "artist");
        var album = a.Album is null ? "" : Text(a.Album, "album", required: false);
        JsonObject payload;
        float[] heard;
        lock (_gate)
        {
            if (_session is null || _buffer.Rms(RadioAudio.SampleRate * MatchSeconds) < SilenceRms)
                throw RpcException.InvalidArgs("Play the song first: RigControl hasn't heard any sound from the capture card in the last 10 seconds.");
            if ((Interlocked.Read(ref _samples) - _openedAt) / (double)RadioAudio.SampleRate < NameSeconds)
                throw RpcException.InvalidArgs("Let the song play for 15 seconds first, then name it.");
            heard = _buffer.Last(RadioAudio.SampleRate * NameSeconds);
        }
        var landmarks = Fingerprinter.Compute(heard);
        lock (_gate)
        {
            if (landmarks.Count < Matcher.MinVotes)
                throw RpcException.InvalidArgs("That sound is too quiet or too plain to learn. Try again while the song is playing clearly.");
            LearnedSong song;
            try { song = _store!.Learn(title, artist, album, null, landmarks, 0); }
            catch (InvalidOperationException ex) { throw RpcException.InvalidArgs(ex.Message); }
            var now = Interlocked.Read(ref _samples);
            _current = (song.Id, NameSeconds, now);
            _pending = (song.Id, NameSeconds, now);
            _since = now;
            _lastMatchAt = now;
            _lastLearnAt = now;
            _dirty = true;
            _log.Info("In-game radio: learned " + Describe(song.Id) + " (named by hand).");
            payload = SongPayload()!;
        }
        SaveIfDue(force: true);
        await Notify(payload).ConfigureAwait(false);
        Publish();
        return GetStatus();
    }

    private IReadOnlyList<LearnedSongDto> ListSongs() =>
        (_store?.Songs ?? []).Select(s => new LearnedSongDto(s.Id, s.Title, s.Artist, s.Album, s.DurationSeconds, s.LearnedAt)).ToList();

    private IReadOnlyList<LearnedSongDto> RenameSong(SongArgs a)
    {
        if (a.Id is not { } id) throw RpcException.InvalidArgs("Say which song.");
        if (!_store!.Rename(id, Text(a.Title, "title"), Text(a.Artist, "artist"), a.Album is null ? "" : Text(a.Album, "album", required: false)))
            throw RpcException.InvalidArgs("That song isn't in the list.");
        lock (_gate) _dirty = true;
        SaveIfDue(force: true);
        Publish();
        return ListSongs();
    }

    private async Task<IReadOnlyList<LearnedSongDto>> ForgetSong(int id)
    {
        JsonObject? clear = null;
        if (!_store!.Forget(id)) throw RpcException.InvalidArgs("That song isn't in the list.");
        lock (_gate)
        {
            if (_current?.SongId == id) clear = Clear("forgotten");
            if (_pending?.SongId == id) _pending = null;
            _dirty = true;
        }
        if (clear is not null) await Notify(clear).ConfigureAwait(false);
        SaveIfDue(force: true);
        Publish();
        return ListSongs();
    }

    /// <summary>Plain song text: trimmed, 1-256 characters (album may be empty), no control or format characters.</summary>
    private static string Text(string? value, string what, bool required = true)
    {
        var s = (value ?? "").Trim();
        if ((required && s.Length == 0) || s.Length > LearnedSongs.MaxText
            || s.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw RpcException.InvalidArgs($"That isn't a song's {what}" + (required ? " (1 to 256 characters of plain text)." : " (plain text, at most 256 characters)."));
        return s;
    }

    private readonly object _publishGate = new();

    /// <summary>Sends the status when it changed. Serialised, so an older status never arrives after a newer one.</summary>
    private void Publish()
    {
        lock (_publishGate)
        {
            var dto = GetStatus();
            if (dto == _published) return;
            _published = dto;
            _ctx?.Events.Publish("radio.status", dto);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
        await _ticking.WaitAsync().ConfigureAwait(false);
        try
        {
            IDisposable? stale;
            lock (_gate)
            {
                JsonObject? ignored = null;
                stale = Detach(ref ignored, "RigControl is closing");
            }
            DisposeQuietly(stale);
            lock (_gate) StopLyrics();
            SaveIfDue(force: true);
        }
        finally
        {
            _ticking.Release();
        }
    }

    public sealed class SettingsArgs
    {
        public bool? Lyrics { get; set; }
        public bool? Enabled { get; set; }
        public string? InputId { get; set; }
    }

    public sealed class SongArgs
    {
        public int? Id { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
    }

    public sealed class IdArgs
    {
        public int Id { get; set; }
    }
}
