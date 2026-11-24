using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RigControl.Capture;

/// <summary>What one capture has to do. Either a normal recording to <see cref="Output"/>, or a rolling replay buffer in <see cref="SegmentDirectory"/>.</summary>
public sealed record CaptureJob(
    int Display, int Fps, int BitrateMbps, string Codec, bool SystemAudio,
    string? Output, string? SegmentDirectory, int SegmentSeconds, int SegmentCount);

public interface ICaptureSession
{
    bool Running { get; }
    /// <summary>The last thing the capture program said, for error messages.</summary>
    string? LastError { get; }
    /// <summary>Stops and finishes the file. Safe to call more than once.</summary>
    void Stop();
}

/// <summary>Does the actual screen capture. Abstracted so tests never record the screen.</summary>
public interface ICaptureEngine
{
    /// <summary>Null when capture can work; otherwise a plain reason why it cannot.</summary>
    string? Problem { get; }
    ICaptureSession Start(CaptureJob job);
    /// <summary>Joins pieces (in order) into one video without re-encoding. Returns false if that failed.</summary>
    bool Concat(IReadOnlyList<string> parts, string output);
    /// <summary>Ends recorder processes left over from an earlier RigControl that did not close properly. Returns how many.</summary>
    int EndLeftovers() => 0;
}

/// <summary>Builds the command lines. Pure, so they can be checked in tests.</summary>
public static class CaptureArgs
{
    public sealed record AudioInput(string PipePath, string SampleFormat, int SampleRate, int Channels);

    public static IReadOnlyList<string> Build(CaptureJob job, AudioInput? audio)
    {
        if (job.Fps is < 15 or > 240) throw new ArgumentOutOfRangeException(nameof(job), "Frames per second must be between 15 and 240.");
        if (job.BitrateMbps is < 2 or > 150) throw new ArgumentOutOfRangeException(nameof(job), "Quality must be between 2 and 150 Mbps.");
        if (job.Display is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(job), "Unknown screen.");
        var encoder = job.Codec switch
        {
            "hevc" => "hevc_nvenc",
            "h264" => "h264_nvenc",
            _ => throw new ArgumentOutOfRangeException(nameof(job), "Unknown video format."),
        };
        var inv = CultureInfo.InvariantCulture;
        var replay = job.SegmentDirectory is not null;
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "lavfi", "-i", string.Create(inv, $"ddagrab=output_idx={job.Display}:framerate={job.Fps}:draw_mouse=1"),
        };
        if (audio is not null)
        {
            a.AddRange(new[]
            {
                "-f", audio.SampleFormat, "-ar", audio.SampleRate.ToString(inv), "-ac", audio.Channels.ToString(inv),
                "-thread_queue_size", "4096", "-i", audio.PipePath,
            });
        }
        a.AddRange(new[]
        {
            "-c:v", encoder, "-preset", "p5", "-rc", "vbr",
            "-b:v", job.BitrateMbps.ToString(inv) + "M",
            "-maxrate", (job.BitrateMbps * 3 / 2).ToString(inv) + "M",
            "-bufsize", (job.BitrateMbps * 2).ToString(inv) + "M",
            // The replay buffer cuts its pieces at key frames, so it needs one every second.
            "-g", (replay ? job.Fps : job.Fps * 2).ToString(inv),
        });
        if (audio is not null) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        if (replay)
        {
            if (job.SegmentSeconds < 1 || job.SegmentCount < 2) throw new ArgumentOutOfRangeException(nameof(job));
            a.AddRange(new[]
            {
                "-f", "segment", "-segment_time", job.SegmentSeconds.ToString(inv), "-segment_wrap", job.SegmentCount.ToString(inv),
                "-reset_timestamps", "1", "-segment_format", "mpegts", Path.Combine(job.SegmentDirectory!, "part%03d.ts"),
            });
        }
        else
        {
            a.Add(job.Output ?? throw new ArgumentException("A recording needs an output file.", nameof(job)));
        }
        return a;
    }

    public static IReadOnlyList<string> BuildConcat(string listFile, string output) => new[]
    {
        "-hide_banner", "-loglevel", "warning", "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", output,
    };
}

/// <summary>
/// The real engine: the converter bundled with RigControl records the screen straight on the graphics card
/// (Desktop Duplication into NVENC), and RigControl feeds it what the PC is playing through a pipe.
/// </summary>
public sealed class FfmpegCaptureEngine : ICaptureEngine, IDisposable
{
    private readonly Func<string?> _ffmpegPath;
    private readonly LoopbackHub _audio = new();

    public FfmpegCaptureEngine(Func<string?> ffmpegPath) => _ffmpegPath = ffmpegPath;

    public string? Problem => Path() is null ? "The video converter inside RigControl could not be unpacked, so recording is unavailable." : null;

    private string? Path()
    {
        try
        {
            var p = _ffmpegPath();
            return p is not null && File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    public ICaptureSession Start(CaptureJob job)
    {
        var exe = Path() ?? throw new InvalidOperationException(Problem);
        return new Session(exe, job, job.SystemAudio ? _audio : null);
    }

    public bool Concat(IReadOnlyList<string> parts, string output)
    {
        var exe = Path();
        if (exe is null || parts.Count == 0) return false;
        var list = output + ".parts.txt";
        try
        {
            File.WriteAllLines(list, parts.Select(p => "file '" + p.Replace("'", "'\\''") + "'"));
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in CaptureArgs.BuildConcat(list, output)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            if (!p.WaitForExit(120000))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return false;
            }
            return p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(list); } catch { /* ignore */ }
        }
    }

    public void Dispose() => _audio.Dispose();

    /// <summary>
    /// Ends recorder processes left behind by a RigControl that was ended abruptly (they would keep recording and
    /// hold the replay buffer's files). Only copies of the bundled converter are touched.
    /// </summary>
    public int EndLeftovers()
    {
        var exe = Path();
        if (exe is null) return 0;
        var ended = 0;
        foreach (var p in Process.GetProcessesByName("ffmpeg"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(true);
                    p.WaitForExit(3000);
                    ended++;
                }
            }
            catch
            {
                // not ours to end, or already gone
            }
            finally
            {
                p.Dispose();
            }
        }
        return ended;
    }

    private sealed class Session : ICaptureSession
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream? _pipe;
        private readonly CancellationTokenSource _cts = new();
        private IDisposable? _subscription;
        private readonly Queue<string> _lastLines = new();
        private int _stopped;

        public Session(string exe, CaptureJob job, LoopbackHub? audio)
        {
            CaptureArgs.AudioInput? input = null;
            WaveFormat? format = null;
            if (audio is not null)
            {
                try
                {
                    format = audio.Format();
                    var name = "rigcontrol-capture-" + Guid.NewGuid().ToString("N");
                    _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4 << 20);
                    input = new CaptureArgs.AudioInput(@"\\.\pipe\" + name, SampleFormat(format), format.SampleRate, format.Channels);
                }
                catch
                {
                    // No playback device or an unusual format: record without sound rather than not at all.
                    _pipe?.Dispose();
                    _pipe = null;
                    input = null;
                }
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            foreach (var a in CaptureArgs.Build(job, input)) psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("The capture program did not start.");
            RigControl.Core.Util.ChildProcesses.Attach(_process);
            _process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_lastLines)
                {
                    _lastLines.Enqueue(e.Data.Trim());
                    while (_lastLines.Count > 8) _lastLines.Dequeue();
                }
            };
            _process.BeginErrorReadLine();

            if (_pipe is not null && audio is not null && format is not null)
            {
                var pipe = _pipe;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                        _subscription = audio.Subscribe(pipe, format);
                    }
                    catch
                    {
                        // capture stopped before the pipe was opened
                    }
                });
            }
        }

        private static string SampleFormat(WaveFormat f)
        {
            var std = f is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : f;
            return (std.Encoding, std.BitsPerSample) switch
            {
                (WaveFormatEncoding.IeeeFloat, 32) => "f32le",
                (WaveFormatEncoding.Pcm, 16) => "s16le",
                (WaveFormatEncoding.Pcm, 24) => "s24le",
                (WaveFormatEncoding.Pcm, 32) => "s32le",
                _ => throw new NotSupportedException("Unusual sound format."),
            };
        }

        public bool Running
        {
            get
            {
                try { return !_process.HasExited; }
                catch { return false; }
            }
        }

        public string? LastError
        {
            get
            {
                lock (_lastLines) return _lastLines.Count == 0 ? null : string.Join(" ", _lastLines.TakeLast(3));
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try
            {
                if (!_process.HasExited)
                {
                    // "q" asks the program to finish the file properly; only if it ignores that is it ended.
                    try
                    {
                        _process.StandardInput.Write('q');
                        _process.StandardInput.Flush();
                    }
                    catch
                    {
                        // already closing
                    }
                    // The sound pipe must close too, or the program waits for more sound.
                    _cts.Cancel();
                    _subscription?.Dispose();
                    try { _pipe?.Dispose(); } catch { /* ignore */ }
                    if (!_process.WaitForExit(8000))
                    {
                        try { _process.Kill(true); } catch { /* ignore */ }
                        _process.WaitForExit(3000);
                    }
                }
            }
            catch
            {
                // nothing more can be done
            }
            finally
            {
                _cts.Cancel();
                _subscription?.Dispose();
                try { _pipe?.Dispose(); } catch { /* ignore */ }
                _process.Dispose();
            }
        }
    }
}

/// <summary>
/// Listens to what the PC is playing (WASAPI loopback of the default output) and hands it to every capture that
/// wants sound. Windows delivers nothing while the PC is silent, so each listener is topped up with silence to
/// keep sound and picture in step.
/// </summary>
internal sealed class LoopbackHub : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Sink> _sinks = new();
    private WasapiLoopbackCapture? _capture;

    /// <summary>The format sound will arrive in. Throws if there is no playback device.</summary>
    public WaveFormat Format()
    {
        lock (_gate)
        {
            if (_capture is not null) return _capture.WaveFormat;
            using var probe = new WasapiLoopbackCapture();
            return probe.WaveFormat;
        }
    }

    public IDisposable Subscribe(Stream target, WaveFormat expected)
    {
        lock (_gate)
        {
            if (_capture is null)
            {
                var capture = new WasapiLoopbackCapture();
                capture.DataAvailable += OnData;
                capture.StartRecording();
                _capture = capture;
            }
            var f = _capture.WaveFormat;
            if (f.SampleRate != expected.SampleRate || f.Channels != expected.Channels || f.BitsPerSample != expected.BitsPerSample)
                throw new InvalidOperationException("The playback device changed.");
            var sink = new Sink(target, f.AverageBytesPerSecond, f.BlockAlign, this);
            _sinks.Add(sink);
            return sink;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        lock (_gate)
        {
            foreach (var s in _sinks) s.Offer(e.Buffer, e.BytesRecorded);
        }
    }

    private void Remove(Sink sink)
    {
        WasapiLoopbackCapture? stop = null;
        lock (_gate)
        {
            _sinks.Remove(sink);
            if (_sinks.Count == 0)
            {
                stop = _capture;
                _capture = null;
            }
        }
        if (stop is not null)
        {
            try
            {
                stop.DataAvailable -= OnData;
                stop.StopRecording();
                stop.Dispose();
            }
            catch
            {
                // device went away
            }
        }
    }

    public void Dispose()
    {
        List<Sink> sinks;
        lock (_gate) sinks = _sinks.ToList();
        foreach (var s in sinks) s.Dispose();
    }

    private sealed class Sink : IDisposable
    {
        private readonly Stream _target;
        private readonly int _bytesPerSecond;
        private readonly int _blockAlign;
        private readonly LoopbackHub _hub;
        private readonly BlockingCollection<byte[]> _queue = new(400);
        private readonly Thread _thread;
        private int _disposed;

        public Sink(Stream target, int bytesPerSecond, int blockAlign, LoopbackHub hub)
        {
            _target = target;
            _bytesPerSecond = bytesPerSecond;
            _blockAlign = Math.Max(1, blockAlign);
            _hub = hub;
            _thread = new Thread(Pump) { IsBackground = true, Name = "RigControl capture sound" };
            _thread.Start();
        }

        public void Offer(byte[] buffer, int count)
        {
            if (_disposed != 0) return;
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            try { _queue.TryAdd(copy); } catch (InvalidOperationException) { /* just closed */ } // if the recorder cannot keep up, drop sound rather than stall the PC's audio thread
        }

        private void Pump()
        {
            var clock = Stopwatch.StartNew();
            long written = 0;
            var silence = new byte[_bytesPerSecond / 10 / _blockAlign * _blockAlign + _blockAlign];
            try
            {
                while (_disposed == 0)
                {
                    if (_queue.TryTake(out var data, 20))
                    {
                        _target.Write(data, 0, data.Length);
                        written += data.Length;
                    }
                    // Behind the clock by more than a tenth of a second means the PC is silent: fill the gap.
                    var expected = (long)(clock.Elapsed.TotalSeconds * _bytesPerSecond);
                    var behind = expected - written;
                    if (behind > _bytesPerSecond / 10)
                    {
                        var fill = (int)Math.Min(silence.Length, behind - _bytesPerSecond / 30) / _blockAlign * _blockAlign;
                        if (fill > 0)
                        {
                            _target.Write(silence, 0, fill);
                            written += fill;
                        }
                    }
                }
            }
            catch
            {
                // the recorder closed its end
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _hub.Remove(this);
            _queue.CompleteAdding();
        }
    }
}
