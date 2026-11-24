using System.Diagnostics;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Capture;

// ---------------------------------------------------------------- contract types (docs/API.md, "capture")

public sealed class CaptureSettings
{
    public const string Section = "capture";
    public string? Folder { get; set; }
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 30;
    public string Codec { get; set; } = "h264";       // "h264" | "hevc"
    public int Display { get; set; }                   // 0 = first screen
    public bool SystemAudio { get; set; } = true;
    public bool ReplayEnabled { get; set; }
    public int ReplaySeconds { get; set; } = 60;
    public bool OverlayStats { get; set; }
    public string RecordHotkey { get; set; } = "Alt+F9";
    public string ReplayHotkey { get; set; } = "Alt+F10";
    public string OverlayHotkey { get; set; } = "Alt+R";
}

public sealed record CaptureSettingsDto(
    string Folder, int Fps, int BitrateMbps, string Codec, int Display, bool SystemAudio, bool ReplayEnabled, int ReplaySeconds,
    bool OverlayStats, string RecordHotkey, string ReplayHotkey, string OverlayHotkey);

public sealed record CaptureStatusDto(
    bool Available, string? Problem, bool Recording, string? RecordingSince, string? RecordingFile, bool ReplayRunning,
    string? LastSaved, IReadOnlyList<string> HotkeyProblems, CaptureSettingsDto Settings);

/// <summary>Shown by the on-screen overlay. Kind: "recordingStarted", "recordingSaved", "replaySaved", "error".</summary>
public sealed record CaptureNoticeDto(string Kind, string Text);

/// <summary>
/// Screen recording and instant replay (what the NVIDIA App's overlay did), using the graphics card's encoder.
///
/// Rules enforced here:
///  - Nothing is recorded until the user starts a recording or switches instant replay on.
///  - Instant replay keeps only the last few minutes, in RigControl's own data folder, and deletes it when switched off or on exit.
///  - Videos are only ever written to the folder the user chose.
/// </summary>
public sealed class CaptureModule : IAppModule
{
    private const int SegmentSeconds = 5;
    private static readonly int[] AllowedReplaySeconds = { 30, 60, 120, 300, 600 };

    private readonly ICaptureEngine _engine;
    private readonly IHotkeys _hotkeys;
    private readonly bool _runLoop;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private Timer? _timer;
    private int _disposed;

    // Guarded by _gate.
    private CaptureSettings _settings = new();
    private ICaptureSession? _record;
    private string? _recordFile;
    private DateTimeOffset? _recordSince;
    private ICaptureSession? _replay;
    private int _replayRestarts;
    private DateTime? _replayRetryAt;
    private DateTime _replayStartedAt;
    /// <summary>Waits between attempts to get instant replay going again. Right after Windows starts the screen may not be capturable yet.</summary>
    private static readonly int[] ReplayRetrySeconds = { 3, 5, 10, 20, 30, 60, 60, 60, 60, 60 };
    /// <summary>The clock, replaceable in tests.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    private string? _lastSaved;
    private string? _problem;
    private IReadOnlyList<string> _hotkeyProblems = Array.Empty<string>();

    /// <param name="runLoop">False in tests, which call <see cref="Tick"/> themselves.</param>
    public CaptureModule(ICaptureEngine engine, IHotkeys hotkeys, bool runLoop = true)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _runLoop = runLoop;
    }

    public string Name => "capture";
    public string DisplayName => "Recording and instant replay";
    public ModuleStatus Status => _status;

    private string ReplayDirectory => Path.Combine(_ctx?.Environment.DataDirectory ?? Path.GetTempPath(), "replay-buffer");

    public static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RigControl");

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        lock (_gate)
        {
            _settings = Sanitise(context.Settings.Get<CaptureSettings>(CaptureSettings.Section));
            _problem = _engine.Problem;
            ApplyHotkeys();
            try
            {
                var ended = _engine.EndLeftovers();
                if (ended > 0) _log.Info($"Ended {ended} recorder process(es) left over from a RigControl that did not close properly.");
            }
            catch (Exception ex)
            {
                _log.Warn("Checking for leftover recorder processes failed.", ex);
            }
            CleanReplayDirectory();
            if (_problem is null && _settings.ReplayEnabled) StartReplay();
            UpdateModuleStatus();
        }
        if (_runLoop) _timer = new Timer(_ => SafeTick(), null, 2000, 2000);
        return Task.CompletedTask;
    }

    private static CaptureSettings Sanitise(CaptureSettings s)
    {
        s.Fps = Math.Clamp(s.Fps, 15, 240);
        s.BitrateMbps = Math.Clamp(s.BitrateMbps, 2, 150);
        if (s.Codec is not ("h264" or "hevc")) s.Codec = "h264";
        s.Display = Math.Clamp(s.Display, 0, 15);
        if (!AllowedReplaySeconds.Contains(s.ReplaySeconds)) s.ReplaySeconds = 60;
        if (string.IsNullOrWhiteSpace(s.Folder)) s.Folder = null;
        return s;
    }

    private string Folder => _settings.Folder ?? DefaultFolder;

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex) { _log.Error("Capture check failed.", ex); }
    }

    /// <summary>Notices captures that ended by themselves and keeps the replay buffer running. Public for tests.</summary>
    public void Tick()
    {
        var notices = new List<CaptureNoticeDto>();
        var changed = false;
        lock (_gate)
        {
            if (_record is { Running: false })
            {
                var error = _record.LastError;
                _record.Stop();
                _log.Warn("Recording stopped by itself. " + error);
                notices.Add(new CaptureNoticeDto("error", "Recording stopped unexpectedly." + (error is null ? "" : " " + error)));
                _record = null;
                _recordSince = null;
                changed = true;
            }
            var now = Now();
            if (_replay is { Running: false })
            {
                var error = _replay.LastError;
                _replay.Stop();
                _replay = null;
                changed = true;
                // A buffer that ran for a while and then stopped starts counting again from the beginning.
                if (now - _replayStartedAt > TimeSpan.FromSeconds(60)) _replayRestarts = 0;
                if (_settings.ReplayEnabled && _replayRestarts < ReplayRetrySeconds.Length)
                {
                    var wait = ReplayRetrySeconds[_replayRestarts++];
                    _replayRetryAt = now.AddSeconds(wait);
                    _log.Warn($"Instant replay stopped by itself; trying again in {wait} s (attempt {_replayRestarts}). " + error);
                }
                else if (_settings.ReplayEnabled)
                {
                    // Give up for now, but leave it switched on: the next start of RigControl tries again.
                    _replayRetryAt = null;
                    _log.Error("Instant replay keeps stopping; not trying again until RigControl is restarted or it is switched off and on. " + error);
                    notices.Add(new CaptureNoticeDto("error", "Instant replay keeps stopping." + (error is null ? "" : " " + error)));
                }
            }
            else if (_replay is null && _settings.ReplayEnabled && _replayRetryAt is { } at && now >= at)
            {
                _replayRetryAt = null;
                try
                {
                    StartReplay();
                    changed = true;
                }
                catch (RpcException ex)
                {
                    _settings.ReplayEnabled = true; // a failed retry must not switch the user's setting off
                    if (_replayRestarts < ReplayRetrySeconds.Length) _replayRetryAt = now.AddSeconds(ReplayRetrySeconds[_replayRestarts++]);
                    _log.Warn("Instant replay could not start; will try again. " + ex.Message);
                }
            }
            if (changed) UpdateModuleStatus();
        }
        foreach (var n in notices) _ctx?.Events.Publish("capture.notice", n);
        if (changed) PublishStatus();
    }

    private void UpdateModuleStatus()
    {
        _status = _problem is not null
            ? new ModuleStatus(ModuleState.Degraded, _problem)
            : new ModuleStatus(ModuleState.Ready);
    }

    private CaptureStatusDto BuildStatus()
    {
        var s = _settings;
        return new CaptureStatusDto(_problem is null, _problem, _record is not null, _recordSince?.ToString("o"), _record is null ? null : _recordFile,
            _replay is not null, _lastSaved, _hotkeyProblems,
            new CaptureSettingsDto(Folder, s.Fps, s.BitrateMbps, s.Codec, s.Display, s.SystemAudio, s.ReplayEnabled, s.ReplaySeconds,
                s.OverlayStats, s.RecordHotkey, s.ReplayHotkey, s.OverlayHotkey));
    }

    private void PublishStatus()
    {
        CaptureStatusDto dto;
        lock (_gate) dto = BuildStatus();
        _ctx?.Events.Publish("capture.status", dto);
    }

    private void Notify(string kind, string text) => _ctx?.Events.Publish("capture.notice", new CaptureNoticeDto(kind, text));

    private void Save() => _ctx?.Settings.Set(CaptureSettings.Section, _settings);

    // ---------------------------------------------------------------- hotkeys

    private void ApplyHotkeys()
    {
        var map = new Dictionary<string, string>
        {
            ["record"] = _settings.RecordHotkey,
            ["replay"] = _settings.ReplayHotkey,
            ["overlay"] = _settings.OverlayHotkey,
        };
        try
        {
            var failed = _hotkeys.Set(map, OnHotkey);
            _hotkeyProblems = failed.Select(f => f switch
            {
                "record" => $"The recording shortcut ({_settings.RecordHotkey}) is not valid or is used by another program.",
                "replay" => $"The instant replay shortcut ({_settings.ReplayHotkey}) is not valid or is used by another program.",
                _ => $"The overlay shortcut ({_settings.OverlayHotkey}) is not valid or is used by another program.",
            }).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn("Registering shortcuts failed.", ex);
            _hotkeyProblems = new[] { "Keyboard shortcuts could not be set up." };
        }
    }

    private void OnHotkey(string name)
    {
        // Runs on the shortcut thread: never let an error escape, and tell the user through the overlay.
        try
        {
            switch (name)
            {
                case "record":
                    bool recording;
                    lock (_gate) recording = _record is not null;
                    if (recording) StopRecording();
                    else StartRecording();
                    break;
                case "replay":
                    SaveReplay();
                    break;
                case "overlay":
                    lock (_gate)
                    {
                        _settings.OverlayStats = !_settings.OverlayStats;
                        Save();
                    }
                    PublishStatus();
                    break;
            }
        }
        catch (RpcException ex)
        {
            Notify("error", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("Shortcut action failed.", ex);
            Notify("error", "That did not work: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- RPC

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getStatus" => GetStatus(),
            "setSettings" => SetSettings(RpcArgs.Require<SettingsArgs>(args)),
            "startRecording" => StartRecording(),
            "stopRecording" => StopRecording(),
            "saveReplay" => SaveReplay(),
            "openFolder" => OpenFolder(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult<object?>(result);
    }

    private CaptureStatusDto GetStatus()
    {
        lock (_gate) return BuildStatus();
    }

    private void RequireAvailable()
    {
        _problem = _engine.Problem;
        UpdateModuleStatus();
        if (_problem is not null) throw RpcException.Unavailable(_problem);
    }

    private CaptureJob Job(string? output, string? segmentDir)
    {
        var s = _settings;
        var count = s.ReplaySeconds / SegmentSeconds + 2;
        return new CaptureJob(s.Display, s.Fps, s.BitrateMbps, s.Codec, s.SystemAudio, output, segmentDir, SegmentSeconds, count);
    }

    private string EnsureFolder()
    {
        var folder = Folder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable($"The video folder ({folder}) cannot be used: {ex.Message}");
        }
        return folder;
    }

    private string NewFile(string folder, string kind)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var path = Path.Combine(folder, $"{kind} {stamp}.mp4");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{kind} {stamp} ({i}).mp4");
        return path;
    }

    private CaptureStatusDto StartRecording()
    {
        string file;
        lock (_gate)
        {
            if (_record is not null) return BuildStatus();
            RequireAvailable();
            file = NewFile(EnsureFolder(), "Recording");
            try
            {
                _record = _engine.Start(Job(file, null));
            }
            catch (Exception ex)
            {
                _log.Error("Recording could not start.", ex);
                throw RpcException.Unavailable("Recording could not start: " + ex.Message);
            }
            _recordFile = file;
            _recordSince = DateTimeOffset.Now;
            _log.Info("Recording started: " + file);
        }
        Notify("recordingStarted", "Recording started");
        PublishStatus();
        return GetStatus();
    }

    private CaptureStatusDto StopRecording()
    {
        string? file;
        lock (_gate)
        {
            if (_record is null) return BuildStatus();
            _record.Stop();
            _record = null;
            _recordSince = null;
            file = _recordFile;
            if (file is not null && File.Exists(file) && new FileInfo(file).Length > 0)
            {
                _lastSaved = file;
                _log.Info("Recording saved: " + file);
            }
            else
            {
                file = null;
                _log.Warn("Recording stopped but no video file was produced.");
            }
        }
        if (file is not null) Notify("recordingSaved", "Recording saved");
        else Notify("error", "The recording produced no video.");
        PublishStatus();
        return GetStatus();
    }

    // ---------------------------------------------------------------- instant replay

    private void CleanReplayDirectory()
    {
        try
        {
            if (Directory.Exists(ReplayDirectory))
                foreach (var f in Directory.GetFiles(ReplayDirectory, "*.ts")) File.Delete(f);
        }
        catch (Exception ex)
        {
            _log.Warn("Old replay pieces could not be removed.", ex);
        }
    }

    private void StartReplay()
    {
        try
        {
            Directory.CreateDirectory(ReplayDirectory);
            CleanReplayDirectory();
            _replay = _engine.Start(Job(null, ReplayDirectory));
            _replayStartedAt = Now();
            _log.Info($"Instant replay running (last {_settings.ReplaySeconds} s).");
        }
        catch (Exception ex)
        {
            _replay = null;
            _settings.ReplayEnabled = false;
            _log.Error("Instant replay could not start.", ex);
            throw RpcException.Unavailable("Instant replay could not start: " + ex.Message);
        }
    }

    private void StopReplay()
    {
        _replay?.Stop();
        _replay = null;
        CleanReplayDirectory();
    }

    /// <summary>Saves the last minutes from the replay buffer as one video in the user's folder.</summary>
    private CaptureStatusDto SaveReplay()
    {
        string output;
        lock (_gate)
        {
            if (_replay is null) throw RpcException.Unavailable("Instant replay is off. Switch it on first; it can only save what happened while it was on.");
            var folder = EnsureFolder();
            var wanted = _settings.ReplaySeconds / SegmentSeconds + 1;
            List<string> parts;
            try
            {
                parts = new DirectoryInfo(ReplayDirectory).GetFiles("part*.ts")
                    .Where(f => f.Length > 0)
                    .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name)
                    .Select(f => f.FullName).ToList();
            }
            catch (Exception ex)
            {
                throw RpcException.Unavailable("The replay buffer could not be read: " + ex.Message);
            }
            if (parts.Count == 0) throw RpcException.Unavailable("Instant replay has only just started; there is nothing to save yet.");
            parts = parts.Skip(Math.Max(0, parts.Count - wanted)).ToList();

            // Copy the pieces first: the recorder keeps overwriting them, and the newest one is still being written.
            var temp = Path.Combine(ReplayDirectory, "save-" + Guid.NewGuid().ToString("N"));
            output = NewFile(folder, "Replay");
            try
            {
                Directory.CreateDirectory(temp);
                var copies = new List<string>();
                foreach (var part in parts)
                {
                    var copy = Path.Combine(temp, Path.GetFileName(part));
                    using (var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var target = File.Create(copy))
                        source.CopyTo(target);
                    copies.Add(copy);
                }
                if (!_engine.Concat(copies, output))
                {
                    try { File.Delete(output); } catch { /* ignore */ }
                    throw RpcException.Unavailable("The replay could not be put together into a video.");
                }
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                throw RpcException.Unavailable("Saving the replay failed: " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { /* ignore */ }
            }
            _lastSaved = output;
            _log.Info("Replay saved: " + output);
        }
        Notify("replaySaved", "Replay saved");
        PublishStatus();
        return GetStatus();
    }

    // ---------------------------------------------------------------- settings

    private CaptureStatusDto SetSettings(SettingsArgs a)
    {
        lock (_gate)
        {
            var s = _settings;
            if (a.Fps is { } fps && fps is < 15 or > 240) throw RpcException.InvalidArgs("Frames per second must be between 15 and 240.");
            if (a.BitrateMbps is { } b && b is < 2 or > 150) throw RpcException.InvalidArgs("Quality must be between 2 and 150 Mbps.");
            if (a.Codec is not (null or "h264" or "hevc")) throw RpcException.InvalidArgs("Unknown video format.");
            if (a.Display is { } d && d is < 0 or > 15) throw RpcException.InvalidArgs("Unknown screen.");
            if (a.ReplaySeconds is { } r && !AllowedReplaySeconds.Contains(r)) throw RpcException.InvalidArgs("Unknown replay length.");
            foreach (var hk in new[] { a.RecordHotkey, a.ReplayHotkey, a.OverlayHotkey })
                if (!string.IsNullOrWhiteSpace(hk) && HotkeyGesture.Parse(hk) is null)
                    throw RpcException.InvalidArgs($"\"{hk}\" is not a shortcut RigControl understands. Use something like Alt+F9 or Ctrl+Shift+R.");
            if (a.Folder is { Length: > 0 } folder)
            {
                if (!Path.IsPathFullyQualified(folder)) throw RpcException.InvalidArgs("Choose a full folder path.");
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { throw RpcException.InvalidArgs("That folder cannot be used: " + ex.Message); }
            }
            if (_record is not null && (a.Fps ?? a.BitrateMbps ?? a.Display) is not null)
                throw RpcException.Unavailable("Stop the recording before changing video settings.");

            var videoChanged = (a.Fps is { } f1 && f1 != s.Fps) || (a.BitrateMbps is { } b1 && b1 != s.BitrateMbps) || (a.Codec is { } c1 && c1 != s.Codec)
                || (a.Display is { } d1 && d1 != s.Display) || (a.SystemAudio is { } au && au != s.SystemAudio) || (a.ReplaySeconds is { } r1 && r1 != s.ReplaySeconds);
            var hotkeysChanged = (a.RecordHotkey is { } h1 && h1 != s.RecordHotkey) || (a.ReplayHotkey is { } h2 && h2 != s.ReplayHotkey) || (a.OverlayHotkey is { } h3 && h3 != s.OverlayHotkey);

            if (a.Folder is not null) s.Folder = a.Folder.Length == 0 ? null : a.Folder;
            s.Fps = a.Fps ?? s.Fps;
            s.BitrateMbps = a.BitrateMbps ?? s.BitrateMbps;
            s.Codec = a.Codec ?? s.Codec;
            s.Display = a.Display ?? s.Display;
            s.SystemAudio = a.SystemAudio ?? s.SystemAudio;
            s.ReplaySeconds = a.ReplaySeconds ?? s.ReplaySeconds;
            s.OverlayStats = a.OverlayStats ?? s.OverlayStats;
            s.RecordHotkey = a.RecordHotkey ?? s.RecordHotkey;
            s.ReplayHotkey = a.ReplayHotkey ?? s.ReplayHotkey;
            s.OverlayHotkey = a.OverlayHotkey ?? s.OverlayHotkey;
            if (hotkeysChanged) ApplyHotkeys();

            var wantReplay = a.ReplayEnabled ?? s.ReplayEnabled;
            try
            {
                if (wantReplay && (_replay is null || videoChanged))
                {
                    RequireAvailable();
                    StopReplay();
                    s.ReplayEnabled = true;
                    _replayRestarts = 0;
                    _replayRetryAt = null;
                    StartReplay();
                }
                else if (!wantReplay && (_replay is not null || s.ReplayEnabled))
                {
                    StopReplay();
                    s.ReplayEnabled = false;
                    _replayRetryAt = null;
                    _log.Info("Instant replay switched off; its buffer was deleted.");
                }
            }
            finally
            {
                Save();
            }
        }
        PublishStatus();
        return GetStatus();
    }

    private CaptureStatusDto OpenFolder()
    {
        string folder;
        lock (_gate) folder = EnsureFolder();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { folder } });
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable("The folder could not be opened: " + ex.Message);
        }
        return GetStatus();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _timer?.Dispose();
        try { _hotkeys.Dispose(); } catch { /* ignore */ }
        lock (_gate)
        {
            // Finish a running recording properly so the file is playable, and throw the replay buffer away.
            try { _record?.Stop(); } catch { /* ignore */ }
            _record = null;
            try { StopReplay(); } catch { /* ignore */ }
        }
        (_engine as IDisposable)?.Dispose();
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    private sealed class SettingsArgs
    {
        public string? Folder { get; set; }
        public int? Fps { get; set; }
        public int? BitrateMbps { get; set; }
        public string? Codec { get; set; }
        public int? Display { get; set; }
        public bool? SystemAudio { get; set; }
        public bool? ReplayEnabled { get; set; }
        public int? ReplaySeconds { get; set; }
        public bool? OverlayStats { get; set; }
        public string? RecordHotkey { get; set; }
        public string? ReplayHotkey { get; set; }
        public string? OverlayHotkey { get; set; }
    }
}
