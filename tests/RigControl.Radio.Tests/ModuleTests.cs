using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Capture.Listen;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Radio.Tests;

/// <summary>
/// A capture card that plays whatever the test feeds it (16 kHz mono). Like NAudio's WasapiCapture, closing it fires
/// the "stopped" callback on its own thread and waits for that thread to finish.
/// </summary>
internal sealed class FakeInput : IRadioInput
{
    public List<AudioEndpoint> Endpoints { get; } = new();
    public int Opened { get; private set; }
    public bool IsOpen => _sink is not null;
    private Action<float[], int, int>? _sink;
    private Action<Exception?>? _stopped;

    public IReadOnlyList<AudioEndpoint> Inputs() => Endpoints.ToList();

    public IDisposable Open(AudioEndpoint input, Action<float[], int, int> onAudio, Action<Exception?>? onStopped = null)
    {
        Opened++;
        _sink = onAudio;
        _stopped = onStopped;
        return new Closer(() =>
        {
            var stopped = _stopped;
            _sink = null;
            _stopped = null;
            var thread = new Thread(() => stopped?.Invoke(null));
            thread.Start();
            thread.Join(); // what WasapiCapture.Dispose does
        });
    }

    public void Feed(float[] audio16k) => _sink?.Invoke(audio16k, 16000, 1);

    /// <summary>The card is pulled out: Windows stops the capture from its own thread.</summary>
    public void Unplug(AudioEndpoint card)
    {
        Endpoints.Remove(card);
        var stopped = _stopped;
        _sink = null;
        _stopped = null;
        var thread = new Thread(() => stopped?.Invoke(new IOException("Device removed")));
        thread.Start();
        thread.Join();
    }

    private sealed class Closer(Action a) : IDisposable { public void Dispose() => a(); }
}

/// <summary>A lyrics engine that "hears" whatever the test says.</summary>
internal sealed class FakeLyrics : RigControl.Radio.Lyrics.ILyricsEngine
{
    public bool Prepared { get; private set; }
    public int Calls;
    public int InFlight, MaxInFlight;
    public Func<float[], RigControl.Radio.Lyrics.LyricsHit?> Answer { get; set; } = _ => null;
    public ManualResetEventSlim? Hold { get; set; }
    public bool Fail { get; set; }
    public string State => Prepared ? "ready" : "off";
    public string? Detail => null;
    public string? LastHeard { get; private set; }
    public bool Ready => Prepared;
    public Task PrepareAsync(CancellationToken ct) { Prepared = true; return Task.CompletedTask; }
    public async Task<RigControl.Radio.Lyrics.LyricsHit?> HearAsync(float[] audio16k, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var now = Interlocked.Increment(ref InFlight);
        lock (this) MaxInFlight = Math.Max(MaxInFlight, now);
        try
        {
            if (Hold is { } h) await Task.Run(() => h.Wait(5000), ct);
            if (Fail) throw new System.Runtime.InteropServices.SEHException("External component has thrown an exception.");
            var hit = Answer(audio16k);
            LastHeard = hit?.Heard ?? "nothing";
            return hit;
        }
        finally { Interlocked.Decrement(ref InFlight); }
    }
    public Func<IReadOnlyList<RigControl.Radio.Fingerprint.Landmark>, (RigControl.Radio.Catalogue.CatalogueSong, double)?> Sound { get; set; } = _ => null;
    public (RigControl.Radio.Catalogue.CatalogueSong Song, double PositionSeconds)? MatchSound(IReadOnlyList<RigControl.Radio.Fingerprint.Landmark> query, double heardSeconds) => Sound(query);
    public void Dispose() => Prepared = false;
}

public class ModuleTests : IDisposable
{
    private static readonly AudioEndpoint Card = new("card-1", "Digital Audio Interface (USB Digital Audio)", "USB\\VID_534D&PID_2109");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-radio-mod-" + Guid.NewGuid().ToString("N"));
    private readonly FakeInput _input = new();
    private readonly List<(string Method, JsonObject Args)> _presence = new();
    private readonly RadioModule _m;
    private readonly FakeLyrics _lyrics = new();

    public ModuleTests()
    {
        Directory.CreateDirectory(_dir);
        _m = new RadioModule(_input, (method, args) => { lock (_presence) _presence.Add((method, args)); return Task.CompletedTask; },
            storePath: Path.Combine(_dir, "learned.rcfp"), runLoop: false, lyrics: _ => _lyrics);
        var ctx = new ModuleContext(new EventBus(), NullLog.Instance, new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe"));
        _m.StartAsync(ctx, default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _m.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private Task<object?> Call(string method, string? json = null) => _m.InvokeAsync(method, json is null ? null : J(json), default);

    private async Task On()
    {
        _input.Endpoints.Add(Card);
        await Call("setSettings", """{"enabled":true}""");
        await _m.TickAsync();
    }

    /// <summary>Plays <paramref name="seconds"/> of a song starting at <paramref name="from"/>, ticking every 3 s.</summary>
    private async Task Play(float[] song, double from, double seconds)
    {
        for (var t = from; t < from + seconds; t += 3)
        {
            _input.Feed(TestAudio.Slice(song, t, Math.Min(t + 3, song.Length / 16000.0)));
            await _m.TickAsync();
            await _m.HearingDone();
        }
    }

    private RadioStatusDto Status => (RadioStatusDto)Call("getStatus").Result!;

    [Fact]
    public async Task Off_by_default_opens_nothing()
    {
        _input.Endpoints.Add(Card);
        await _m.TickAsync();
        Assert.Equal(0, _input.Opened);
        Assert.Equal("off", Status.State);
        Assert.False(Status.Enabled);
    }

    [Fact]
    public async Task Missing_input_waits_and_retries()
    {
        await Call("setSettings", """{"enabled":true}""");
        await _m.TickAsync();
        Assert.Equal("waiting", Status.State);
        Assert.Equal("No capture card sound input found.", Status.Problem);
        _input.Endpoints.Add(Card);
        await _m.TickAsync();
        Assert.Equal("listening", Status.State);
        Assert.True(_input.IsOpen);
    }

    [Fact]
    public async Task Named_song_is_recognised_next_time()
    {
        await On();
        var song = TestAudio.Song(7, 120);
        await Play(song, 0, 15);
        await Call("nameSong", """{"title":"Seven","artist":"Band"}""");
        Assert.Equal("Seven", Status.Now!.Title);
        Assert.Null(Status.Now.PositionSeconds); // named by hand: where in the song isn't known

        // Silence, then the same part of the song again later: recognised by itself.
        await Play(new float[16000 * 15], 0, 15);
        Assert.Null(Status.Now);
        await Play(song, 0, 15);
        Assert.Equal("Seven", Status.Now!.Title);
        var last = _presence.Last();
        Assert.Equal("presence.setRadioMusic", last.Method);
        Assert.Equal("Seven", last.Args["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_agreeing_matches_make_a_song_current()
    {
        await On();
        var song = TestAudio.Song(8, 120);
        await Play(song, 0, 15);
        await Call("nameSong", """{"title":"Eight","artist":"Band"}""");
        await Play(new float[16000 * 15], 0, 15);
        _input.Feed(TestAudio.Slice(song, 0, 12));
        await _m.TickAsync(); // first match: not shown yet
        Assert.Null(Status.Now);
        _input.Feed(TestAudio.Slice(song, 12, 15));
        await _m.TickAsync(); // second, agreeing: shown
        Assert.Equal("Eight", Status.Now!.Title);
    }

    [Fact]
    public async Task A_station_song_is_recognised_by_its_preview_sound_without_naming()
    {
        // The "preview": 30 s from the middle of the song, at the preview frame like PreviewLibrary keeps it.
        var song = TestAudio.Song(31, 180);
        var station = new RigControl.Radio.Catalogue.CatalogueSong("Station", "Thirty One", "Band");
        var previews = RigControl.Radio.Store.LearnedSongs.Load(Path.Combine(_dir, "previews.rcfp"), NullLog.Instance);
        previews.Learn(station.Title, station.Artist, "", null, RigControl.Radio.Fingerprint.Fingerprinter.Compute(TestAudio.Slice(song, 60, 90)),
            RigControl.Radio.Previews.PreviewLibrary.Base);
        _lyrics.Sound = q => previews.Match(q, 10) is { } m ? (station, m.PositionSeconds) : null;
        await On();
        await Call("setSettings", """{"lyrics":true}""");

        await Play(song, 0, 30);         // a part the preview doesn't have: nothing
        Assert.Null(Status.Now);
        await Play(song, 63, 15);        // the preview's part: recognised, no hand naming
        Assert.Equal("Thirty One", Status.Now!.Title);
        Assert.Null(Status.Now.PositionSeconds); // where in the song isn't known
        Assert.Equal("Thirty One", _presence.Last().Args["title"]!.GetValue<string>());
        Assert.Single((IEnumerable<LearnedSongDto>)Call("listSongs").Result!);

        // It keeps learning as it plays, so later parts are recognised next time too.
        await Play(song, 78, 60);
        await Play(new float[16000 * 15], 0, 15);
        Assert.Null(Status.Now);
        _lyrics.Sound = _ => null;
        await Play(song, 110, 15);
        Assert.Equal("Thirty One", Status.Now!.Title);
    }

    [Fact]
    public async Task Silence_clears_the_song()
    {
        await On();
        var song = TestAudio.Song(9, 60);
        await Play(song, 0, 15);
        await Call("nameSong", """{"title":"Nine","artist":"Band"}""");
        await Play(new float[16000 * 9], 0, 9);
        Assert.Null(Status.Now);
        Assert.Empty(_presence.Last().Args); // {} = nothing playing
    }

    [Fact]
    public async Task Naming_needs_sound()
    {
        await On();
        await Play(new float[16000 * 12], 0, 12);
        var e = await Assert.ThrowsAsync<RpcException>(() => Call("nameSong", """{"title":"X","artist":"Y"}"""));
        Assert.Contains("sound", e.Message);
    }

    [Theory]
    [InlineData("""{"title":"","artist":"Y"}""")]
    [InlineData("""{"title":"X","artist":""}""")]
    [InlineData("""{"title":"X\u0007","artist":"Y"}""")]
    [InlineData("""{"title":"X‮","artist":"Y"}""")]
    public async Task Naming_refuses_bad_text(string args)
    {
        await On();
        await Play(TestAudio.Song(10, 15), 0, 15);
        await Assert.ThrowsAsync<RpcException>(() => Call("nameSong", args));
        var tooLong = "{\"title\":\"" + new string('a', 257) + "\",\"artist\":\"Y\"}";
        await Assert.ThrowsAsync<RpcException>(() => Call("nameSong", tooLong));
    }

    [Fact]
    public async Task Forget_and_rename_change_the_list()
    {
        await On();
        await Play(TestAudio.Song(11, 15), 0, 15);
        await Call("nameSong", """{"title":"Elevn","artist":"Band"}""");
        var id = ((IReadOnlyList<LearnedSongDto>)(await Call("listSongs"))!).Single().Id;
        await Call("renameSong", $$"""{"id":{{id}},"title":"Eleven","artist":"Band","album":"A"}""");
        Assert.Equal("Eleven", ((IReadOnlyList<LearnedSongDto>)(await Call("listSongs"))!).Single().Title);
        await Call("forgetSong", $$"""{"id":{{id}}}""");
        Assert.Empty((IReadOnlyList<LearnedSongDto>)(await Call("listSongs"))!);
    }

    [Fact]
    public async Task Learned_songs_are_kept_across_restarts()
    {
        await On();
        await Play(TestAudio.Song(12, 15), 0, 15);
        await Call("nameSong", """{"title":"Twelve","artist":"Band"}""");
        await _m.DisposeAsync(); // saves
        Assert.True(File.Exists(Path.Combine(_dir, "learned.rcfp")));
    }

    [Fact]
    public async Task Switching_off_never_hangs()
    {
        await On();
        await Play(TestAudio.Song(13, 15), 0, 15);
        var off = Call("setSettings", """{"enabled":false}""");
        Assert.Same(off, await Task.WhenAny(off, Task.Delay(5000)));
        Assert.Equal("off", Status.State);
        var dispose = _m.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(5000)));
    }

    [Fact]
    public async Task Recognising_never_holds_up_the_sound_or_the_status()
    {
        await On();
        await Play(TestAudio.Song(14, 15), 0, 15);
        using var inMatch = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _m.BeforeMatch = () => { inMatch.Set(); release.Wait(5000); };
        var tick = Task.Run(() => _m.TickAsync());
        Assert.True(inMatch.Wait(5000));
        var feed = Task.Run(() => _input.Feed(new float[16000]));
        var status = Task.Run(() => _m.GetStatus());
        Assert.Same(feed, await Task.WhenAny(feed, Task.Delay(1000)));
        Assert.Same(status, await Task.WhenAny(status, Task.Delay(1000)));
        release.Set();
        await tick;
    }

    [Fact]
    public async Task Naming_needs_15_seconds_of_fresh_sound()
    {
        await On();
        await Play(TestAudio.Song(15, 15), 0, 6);
        var e = await Assert.ThrowsAsync<RpcException>(() => Call("nameSong", """{"title":"X","artist":"Y"}"""));
        Assert.Contains("15 seconds", e.Message);
    }

    [Fact]
    public async Task Reopening_forgets_the_old_sound()
    {
        await On();
        await Play(TestAudio.Song(16, 15), 0, 15);
        await Call("setSettings", """{"enabled":false}""");
        await Call("setSettings", """{"enabled":true}""");
        await _m.TickAsync();
        Assert.False(Status.Sound);
        await Assert.ThrowsAsync<RpcException>(() => Call("nameSong", """{"title":"X","artist":"Y"}"""));
    }

    [Fact]
    public async Task Unplugging_clears_the_song()
    {
        await On();
        await Play(TestAudio.Song(17, 15), 0, 15);
        await Call("nameSong", """{"title":"Seventeen","artist":"Band"}""");
        _input.Unplug(Card);
        await _m.TickAsync();
        Assert.Equal("waiting", Status.State);
        Assert.Null(Status.Now);
        Assert.Empty(_presence.Last().Args);
    }

    [Fact]
    public async Task A_hand_named_song_keeps_its_timer_on_Discord()
    {
        await On();
        var song = TestAudio.Song(18, 60);
        await Play(song, 0, 15);
        await Call("nameSong", """{"title":"Eighteen","artist":"Band"}""");
        await Play(song, 15, 9);
        var positions = _presence.Where(p => p.Args.ContainsKey("position")).Select(p => p.Args["position"]!.GetValue<double>()).ToList();
        Assert.True(positions.Count >= 3);
        Assert.True(positions.Zip(positions.Skip(1)).All(p => p.Second > p.First), string.Join(", ", positions));
        Assert.DoesNotContain(_presence, p => p.Args.ContainsKey("duration")); // the length isn't known
    }

    private static readonly RigControl.Radio.Catalogue.CatalogueSong Feel = new("", "Electric Feel", "MGMT");

    [Fact]
    public async Task Lyrics_are_off_until_switched_on()
    {
        await On();
        Assert.False(_lyrics.Prepared);
        Assert.Equal("off", Status.Lyrics.State);
        await Call("setSettings", """{"lyrics":true}""");
        Assert.True(_lyrics.Prepared);
        Assert.True(Status.Lyrics.Enabled);
    }

    [Fact]
    public async Task A_song_heard_by_its_lyrics_shows_and_is_learned_for_next_time()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        // The test feeds 3 s per tick and listens every tick: each call's place in the song is 3 s further on.
        _lyrics.Answer = audio => audio.Length >= 16000 * 6 ? new(Feel, 100 + 3 * _lyrics.Calls, 230, "made-up words for the test") : null;
        var song = TestAudio.Song(21, 120);
        await Play(song, 0, 24);
        Assert.Equal("Electric Feel", Status.Now?.Title);
        Assert.Equal("MGMT", Status.Now!.Artist);
        Assert.NotNull(Status.Now.DurationSeconds);
        Assert.Contains(_presence, p => p.Args.ContainsKey("title") && p.Args["title"]!.GetValue<string>() == "Electric Feel");

        // Quiet, then the same part again: the fingerprints learned from it are enough.
        await Play(new float[16000 * 12], 0, 12);
        Assert.Null(Status.Now);
        _lyrics.Answer = _ => null;
        await Play(song, 0, 30);
        Assert.Equal("Electric Feel", Status.Now?.Title);
    }

    [Fact]
    public async Task Talking_that_isnt_a_song_shows_nothing()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        await Play(TestAudio.Song(22, 30), 0, 24);
        Assert.Null(Status.Now);
        Assert.True(_lyrics.Calls > 0);
        Assert.Equal("nothing", Status.Lyrics.LastHeard);
    }

    [Fact]
    public async Task Only_one_transcription_runs_at_a_time()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        using var hold = new ManualResetEventSlim();
        _lyrics.Hold = hold;
        var song = TestAudio.Song(23, 60);
        for (var t = 0; t < 24; t += 3)
        {
            _input.Feed(TestAudio.Slice(song, t, t + 3));
            await _m.TickAsync();
        }
        Assert.Equal(1, _lyrics.Calls);
        hold.Set();
        await _m.HearingDone();
        Assert.Equal(1, _lyrics.MaxInFlight);
    }

    [Fact]
    public async Task One_lyrics_hit_alone_shows_nothing_and_learns_nothing()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        var given = false;
        _lyrics.Answer = audio =>
        {
            if (given || audio.Length < 16000 * 6) return null;
            given = true; // one hit, ever
            return new(Feel, 120, 230, "made-up words for the test");
        };
        await Play(TestAudio.Song(24, 60), 0, 30);
        Assert.Null(Status.Now);
        Assert.Empty((IReadOnlyList<LearnedSongDto>)(await Call("listSongs"))!);
    }

    [Fact]
    public async Task Two_hits_that_disagree_on_the_place_show_nothing()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        _lyrics.Answer = audio => audio.Length >= 16000 * 6 ? new(Feel, 120, 230, "made-up words for the test") : null; // never moves on
        await Play(TestAudio.Song(25, 60), 0, 30);
        Assert.Null(Status.Now);
    }

    [Fact]
    public async Task A_hit_that_arrives_late_is_dropped()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        using var hold = new ManualResetEventSlim();
        _lyrics.Hold = hold;
        _lyrics.Answer = _ => new(Feel, 100 + 3 * _lyrics.Calls, 230, "made-up words for the test");
        var song = TestAudio.Song(26, 60);
        for (var t = 0; t < 12; t += 3)
        {
            _input.Feed(TestAudio.Slice(song, t, t + 3));
            await _m.TickAsync(); // the first listen is still running
        }
        hold.Set();
        await _m.HearingDone(); // its answer is for sound 9 s old by now
        _lyrics.Hold = null;
        await Play(new float[16000 * 12], 0, 12);
        Assert.Null(Status.Now);
        Assert.Empty((IReadOnlyList<LearnedSongDto>)(await Call("listSongs"))!);
    }

    [Fact]
    public async Task Listening_that_keeps_failing_is_shown_as_a_problem()
    {
        await On();
        await Call("setSettings", """{"lyrics":true}""");
        _lyrics.Fail = true;
        await Play(TestAudio.Song(27, 30), 0, 15);
        Assert.Equal("problem", Status.Lyrics.State);
        Assert.Contains("restart RigControl", Status.Lyrics.Detail);
        var calls = _lyrics.Calls;
        await Play(TestAudio.Song(27, 30), 15, 9);
        Assert.Equal(calls, _lyrics.Calls); // it stops trying until restarted
    }
}
