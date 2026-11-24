using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Capture.Tests;

public class CaptureArgsTests
{
    private static CaptureJob Job(string? output = @"C:\v\a.mp4", string? segments = null, string codec = "h264")
        => new(0, 60, 30, codec, true, output, segments, 5, 14);

    [Fact]
    public void A_recording_uses_the_graphics_card_and_the_chosen_quality()
    {
        var a = CaptureArgs.Build(Job(), null);
        var line = string.Join(" ", a);
        Assert.Contains("-f lavfi -i ddagrab=output_idx=0:framerate=60:draw_mouse=1", line);
        Assert.Contains("-c:v h264_nvenc", line);
        Assert.Contains("-b:v 30M -maxrate 45M -bufsize 60M -g 120", line);
        Assert.DoesNotContain("-c:a", line);
        Assert.Equal(@"C:\v\a.mp4", a[^1]);
    }

    [Fact]
    public void Sound_comes_in_through_the_pipe()
    {
        var line = string.Join(" ", CaptureArgs.Build(Job(codec: "hevc"), new CaptureArgs.AudioInput(@"\\.\pipe\x", "f32le", 48000, 2)));
        Assert.Contains(@"-f f32le -ar 48000 -ac 2 -thread_queue_size 4096 -i \\.\pipe\x", line);
        Assert.Contains("-c:v hevc_nvenc", line);
        Assert.Contains("-c:a aac -b:a 192k", line);
    }

    [Fact]
    public void The_replay_buffer_is_a_ring_of_short_pieces()
    {
        var a = CaptureArgs.Build(Job(output: null, segments: @"C:\buf"), null);
        var line = string.Join(" ", a);
        Assert.Contains("-g 60", line);
        Assert.Contains("-f segment -segment_time 5 -segment_wrap 14 -reset_timestamps 1 -segment_format mpegts", line);
        Assert.Equal(@"C:\buf\part%03d.ts", a[^1]);
    }

    [Fact]
    public void Nonsense_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureArgs.Build(Job() with { Fps = 0 }, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureArgs.Build(Job() with { BitrateMbps = 1000 }, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureArgs.Build(Job(codec: "; rm"), null));
        Assert.Throws<ArgumentException>(() => CaptureArgs.Build(Job(output: null), null));
    }

    [Theory]
    [InlineData("Alt+F9", 0x1u, 0x78u)]
    [InlineData("ctrl + shift + r", 0x6u, (uint)'R')]
    [InlineData("Win+Alt+F24", 0x9u, 0x87u)]
    public void Shortcuts_parse(string text, uint mods, uint key)
        => Assert.Equal(new HotkeyGesture(mods, key), HotkeyGesture.Parse(text));

    [Theory]
    [InlineData("F9")]          // no modifier: would steal the key from every program
    [InlineData("Alt+")]
    [InlineData("Alt+F9+F10")]
    [InlineData("Alt+Banana")]
    [InlineData("")]
    public void Bad_shortcuts_are_rejected(string text) => Assert.Null(HotkeyGesture.Parse(text));
}

internal sealed class FakeSession : ICaptureSession
{
    public CaptureJob Job { get; }
    public bool Running { get; set; } = true;
    public string? LastError { get; set; }
    public bool Stopped { get; private set; }

    public FakeSession(CaptureJob job)
    {
        Job = job;
        // Behave like the recorder: a recording leaves a file, a replay buffer leaves pieces.
        if (job.Output is not null) File.WriteAllText(job.Output, "video");
        if (job.SegmentDirectory is not null)
            for (var i = 0; i < 3; i++)
            {
                var p = Path.Combine(job.SegmentDirectory, $"part{i:000}.ts");
                File.WriteAllText(p, "piece" + i);
                File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(i - 10));
            }
    }

    public void Stop()
    {
        Stopped = true;
        Running = false;
    }
}

internal sealed class FakeEngine : ICaptureEngine
{
    public string? Problem { get; set; }
    public List<FakeSession> Sessions { get; } = new();
    public List<(List<string> Parts, string Output)> Concats { get; } = new();
    public bool ConcatWorks { get; set; } = true;

    public ICaptureSession Start(CaptureJob job)
    {
        var s = new FakeSession(job);
        Sessions.Add(s);
        return s;
    }

    public bool Concat(IReadOnlyList<string> parts, string output)
    {
        Concats.Add((parts.Select(p => File.ReadAllText(p)).ToList(), output));
        if (ConcatWorks) File.WriteAllText(output, "joined");
        return ConcatWorks;
    }
}

internal sealed class FakeHotkeys : IHotkeys
{
    public IReadOnlyDictionary<string, string> Shortcuts { get; private set; } = new Dictionary<string, string>();
    public Action<string>? Pressed { get; private set; }
    public List<string> Refuse { get; } = new();

    public IReadOnlyList<string> Set(IReadOnlyDictionary<string, string> shortcuts, Action<string> pressed)
    {
        Shortcuts = shortcuts;
        Pressed = pressed;
        return Refuse.Where(shortcuts.ContainsKey).ToList();
    }

    public void Dispose() { }
}

public class CaptureModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-capture-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEngine _engine = new();
    private readonly FakeHotkeys _hotkeys = new();
    private readonly List<(string Topic, object? Payload)> _events = new();
    private string Videos => Path.Combine(_dir, "videos");

    public CaptureModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private async Task<CaptureModule> Start()
    {
        var bus = new EventBus();
        bus.Published += (t, p) => _events.Add((t, p));
        var module = new CaptureModule(_engine, _hotkeys, runLoop: false);
        var ctx = new ModuleContext(bus, NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe"));
        await module.StartAsync(ctx, CancellationToken.None);
        await Call(module, "setSettings", new { folder = Videos });
        return module;
    }

    private static async Task<JsonElement> Call(CaptureModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        var result = await m.InvokeAsync(method, a, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
    }

    private static async Task<string> Code(CaptureModule m, string method, object? args = null)
        => (await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args))).Code;

    [Fact]
    public async Task Nothing_is_captured_until_asked()
    {
        await using var m = await Start();
        Assert.Empty(_engine.Sessions);
        var s = await Call(m, "getStatus");
        Assert.True(s.GetProperty("available").GetBoolean());
        Assert.False(s.GetProperty("recording").GetBoolean());
        Assert.False(s.GetProperty("replayRunning").GetBoolean());
        Assert.Equal("Alt+F9", s.GetProperty("settings").GetProperty("recordHotkey").GetString());
        Assert.Equal(3, _hotkeys.Shortcuts.Count);
    }

    [Fact]
    public async Task Record_then_stop_leaves_a_video_in_the_chosen_folder()
    {
        await using var m = await Start();
        var s = await Call(m, "startRecording");
        Assert.True(s.GetProperty("recording").GetBoolean());
        var file = s.GetProperty("recordingFile").GetString()!;
        Assert.StartsWith(Videos, file);
        Assert.EndsWith(".mp4", file);
        var session = Assert.Single(_engine.Sessions);
        Assert.Equal(file, session.Job.Output);
        Assert.Null(session.Job.SegmentDirectory);

        // A second start does not start a second capture.
        await Call(m, "startRecording");
        Assert.Single(_engine.Sessions);
        // Video settings are locked while recording.
        Assert.Equal("unavailable", await Code(m, "setSettings", new { fps = 30 }));

        s = await Call(m, "stopRecording");
        Assert.True(session.Stopped);
        Assert.False(s.GetProperty("recording").GetBoolean());
        Assert.Equal(file, s.GetProperty("lastSaved").GetString());
        Assert.Contains(_events, e => e.Topic == "capture.notice" && ((CaptureNoticeDto)e.Payload!).Kind == "recordingSaved");
    }

    [Fact]
    public async Task The_record_shortcut_toggles_and_the_overlay_shortcut_flips_the_stats()
    {
        await using var m = await Start();
        _hotkeys.Pressed!("record");
        Assert.True((await Call(m, "getStatus")).GetProperty("recording").GetBoolean());
        _hotkeys.Pressed!("record");
        Assert.False((await Call(m, "getStatus")).GetProperty("recording").GetBoolean());
        _hotkeys.Pressed!("overlay");
        Assert.True((await Call(m, "getStatus")).GetProperty("settings").GetProperty("overlayStats").GetBoolean());
        // Saving a replay while replay is off reports the reason instead of throwing on the shortcut thread.
        _hotkeys.Pressed!("replay");
        Assert.Contains(_events, e => e.Topic == "capture.notice" && ((CaptureNoticeDto)e.Payload!).Kind == "error");
    }

    [Fact]
    public async Task Instant_replay_saves_the_newest_pieces_in_order_and_cleans_up()
    {
        await using var m = await Start();
        Assert.Equal("unavailable", await Code(m, "saveReplay"));

        var s = await Call(m, "setSettings", new { replayEnabled = true, replaySeconds = 30 });
        Assert.True(s.GetProperty("replayRunning").GetBoolean());
        var session = Assert.Single(_engine.Sessions);
        Assert.Equal(30 / 5 + 2, session.Job.SegmentCount);
        var buffer = session.Job.SegmentDirectory!;
        Assert.StartsWith(_dir, buffer);

        s = await Call(m, "saveReplay");
        var (parts, output) = Assert.Single(_engine.Concats);
        Assert.Equal(new[] { "piece0", "piece1", "piece2" }, parts);
        Assert.StartsWith(Videos, output);
        Assert.Equal(output, s.GetProperty("lastSaved").GetString());
        Assert.Empty(Directory.GetDirectories(buffer));                 // the temporary copies are gone

        // Changing the length restarts the buffer; switching it off deletes it.
        await Call(m, "setSettings", new { replaySeconds = 120 });
        Assert.Equal(2, _engine.Sessions.Count);
        Assert.True(session.Stopped);
        s = await Call(m, "setSettings", new { replayEnabled = false });
        Assert.False(s.GetProperty("replayRunning").GetBoolean());
        Assert.Empty(Directory.GetFiles(buffer, "*.ts"));
    }

    [Fact]
    public async Task A_replay_that_cannot_be_joined_leaves_no_broken_file()
    {
        await using var m = await Start();
        await Call(m, "setSettings", new { replayEnabled = true });
        _engine.ConcatWorks = false;
        Assert.Equal("unavailable", await Code(m, "saveReplay"));
        Assert.Empty(Directory.GetFiles(Videos));
    }

    [Fact]
    public async Task Captures_that_die_are_noticed()
    {
        await using var m = await Start();
        await Call(m, "startRecording");
        await Call(m, "setSettings", new { replayEnabled = true });
        _engine.Sessions[0].Running = false;
        _engine.Sessions[0].LastError = "encoder lost";
        m.Tick();
        Assert.False((await Call(m, "getStatus")).GetProperty("recording").GetBoolean());
        Assert.Contains(_events, e => e.Topic == "capture.notice" && ((CaptureNoticeDto)e.Payload!).Text.Contains("encoder lost"));

        // The replay buffer is restarted with growing waits, not in a tight loop...
        var clock = DateTime.UtcNow;
        m.Now = () => clock;
        _engine.Sessions[^1].Running = false;
        m.Tick();
        var afterFirstFailure = _engine.Sessions.Count;
        m.Tick();                                             // too soon: nothing new is started
        Assert.Equal(afterFirstFailure, _engine.Sessions.Count);
        clock = clock.AddSeconds(4);
        m.Tick();                                             // the first wait (3 s) is over
        Assert.Equal(afterFirstFailure + 1, _engine.Sessions.Count);
        Assert.True((await Call(m, "getStatus")).GetProperty("replayRunning").GetBoolean());

        // ...and when it never comes up, RigControl stops trying but leaves the user's choice switched on.
        for (var i = 0; i < 40; i++)
        {
            _engine.Sessions[^1].Running = false;             // each new attempt dies straight away
            m.Tick();
            clock = clock.AddSeconds(61);
            m.Tick();                                         // the wait is over: the next attempt starts (if any are left)
        }
        _engine.Sessions[^1].Running = false;
        m.Tick();
        var s = await Call(m, "getStatus");
        Assert.False(s.GetProperty("replayRunning").GetBoolean());
        Assert.True(s.GetProperty("settings").GetProperty("replayEnabled").GetBoolean());
        var gaveUpAt = _engine.Sessions.Count;
        clock = clock.AddHours(1);
        m.Tick();
        Assert.Equal(gaveUpAt, _engine.Sessions.Count);
        Assert.InRange(gaveUpAt, 5, 16);
    }

    [Fact]
    public async Task Bad_settings_and_a_missing_converter_are_refused()
    {
        await using var m = await Start();
        Assert.Equal("invalidArgs", await Code(m, "setSettings", new { fps = 500 }));
        Assert.Equal("invalidArgs", await Code(m, "setSettings", new { codec = "gif" }));
        Assert.Equal("invalidArgs", await Code(m, "setSettings", new { replaySeconds = 7 }));
        Assert.Equal("invalidArgs", await Code(m, "setSettings", new { recordHotkey = "F9" }));
        Assert.Equal("invalidArgs", await Code(m, "setSettings", new { folder = "videos" }));

        _hotkeys.Refuse.Add("record");
        var s = await Call(m, "setSettings", new { recordHotkey = "Ctrl+Shift+F9" });
        Assert.Single(s.GetProperty("hotkeyProblems").EnumerateArray());

        _engine.Problem = "no converter";
        Assert.Equal("unavailable", await Code(m, "startRecording"));
        Assert.Equal("unavailable", await Code(m, "setSettings", new { replayEnabled = true }));
        Assert.Empty(_engine.Sessions);
    }

    [Fact]
    public async Task Closing_finishes_the_recording_and_deletes_the_buffer()
    {
        var m = await Start();
        await Call(m, "startRecording");
        await Call(m, "setSettings", new { replayEnabled = true });
        var buffer = _engine.Sessions[1].Job.SegmentDirectory!;
        await m.DisposeAsync();
        Assert.All(_engine.Sessions, s => Assert.True(s.Stopped));
        Assert.Empty(Directory.GetFiles(buffer, "*.ts"));
    }
}
