using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AudioController.Core.Abstractions;
using AudioController.Core.Validation;
using NAudio.Utils;
using NAudio.Wave;

namespace AudioController.Decode;

/// <summary>
/// Decodes any audio file FFmpeg understands into the canonical internal format
/// (RIFF WAV, IEEE float 32-bit, 48 000 Hz, stereo) and enforces the 30.000-second maximum.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg runs as a separate process and streams raw float PCM over its standard output.
/// Decoded audio is held in memory only; nothing is ever written to disk, including temporary files.
/// </para>
/// <para>
/// Reading stops once the output passes 31 seconds of audio, so an arbitrarily long or endless
/// input cannot exhaust memory. Over-long files are rejected, never trimmed. Every FFmpeg run is
/// limited to 60 seconds; a process that hangs is killed and the decode fails cleanly.
/// </para>
/// <para>Instances are stateless apart from the tool paths and are safe to use from several threads.</para>
/// </remarks>
public sealed partial class FfmpegDecoder : IAudioDecoder
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);

    /// <summary>Read cap: 30 s plus one second of headroom, in samples per channel (31 s).</summary>
    private const long CapSamplesPerChannel = 30L * SampleRate + SampleRate;

    /// <summary>Read cap in bytes: 31 * 48 000 * 2 floats * 4 bytes = 11 904 000.</summary>
    private const long CapBytes = CapSamplesPerChannel * BytesPerFrame;

    private const double CapSeconds = (double)CapSamplesPerChannel / SampleRate;
    private const int InitialPcmCapacity = 1 << 20;
    private const int ReadChunkBytes = 64 * 1024;
    private const int MaxCapturedStderrChars = 16 * 1024;
    private const int MaxDetailChars = 300;

    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Lazy<string?> _ffprobePath;

    /// <summary>Creates a decoder that runs the given FFmpeg executable.</summary>
    /// <param name="ffmpegExePath">
    /// Path to <c>ffmpeg.exe</c>, usually from <see cref="FfmpegLocator.Find"/>. FFprobe (used only to
    /// report the real length of over-long files) is looked for next to it, then via
    /// <see cref="FfmpegLocator.FindProbe"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="ffmpegExePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="ffmpegExePath"/> is empty, whitespace, or not a valid path.</exception>
    public FfmpegDecoder(string ffmpegExePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExePath);

        FfmpegPath = Path.GetFullPath(ffmpegExePath);
        _ffprobePath = new Lazy<string?>(ResolveProbePath, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Full path of the FFmpeg executable this decoder runs.</summary>
    public string FfmpegPath { get; }

    /// <summary>
    /// Decodes <paramref name="inputFilePath"/> to canonical WAV bytes in memory and validates its length.
    /// </summary>
    /// <param name="inputFilePath">The audio (or audio-bearing video) file to import.</param>
    /// <returns>
    /// On success: <see cref="DecodeResult.WavData"/> (IEEE float, 48 kHz, stereo),
    /// <see cref="DecodeResult.DurationSeconds"/> and <see cref="DecodeResult.SamplesPerChannel"/>.
    /// On failure: a user-readable <see cref="DecodeResult.Error"/>. This method does not throw for bad
    /// input, unsupported formats, over-long files, or FFmpeg failures.
    /// </returns>
    public DecodeResult Decode(string inputFilePath)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
            return DecodeResult.Fail("No input file was specified.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(inputFilePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return DecodeResult.Fail($"\"{inputFilePath}\" is not a valid file path.");
        }

        string name = Path.GetFileName(fullPath);
        if (name.Length == 0)
            name = fullPath;

        if (!File.Exists(fullPath))
            return DecodeResult.Fail($"Could not decode \"{name}\": the file was not found.");

        if (!File.Exists(FfmpegPath))
            return DecodeResult.Fail($"Could not decode \"{name}\": FFmpeg was not found at \"{FfmpegPath}\".");

        FfmpegRun run;
        try
        {
            run = RunFfmpeg(fullPath);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return DecodeResult.Fail($"Could not decode \"{name}\": FFmpeg could not be started ({ex.Message}).");
        }

        if (run.Overflowed)
            return DecodeResult.Fail($"Could not import \"{name}\": " + BuildTooLongMessage(fullPath));

        if (run.TimedOut)
        {
            return DecodeResult.Fail(
                $"Could not decode \"{name}\": FFmpeg did not finish within {DecodeTimeout.TotalSeconds:0} s and was stopped. " +
                "The file may be damaged or not a supported audio file.");
        }

        string? detail = PickUsefulStderrLine(run.Stderr, fullPath, name);

        if (run.PcmLength == 0)
            return DecodeResult.Fail(BuildNoPcmMessage(name, run, detail));

        if (run.ExitCode != 0)
        {
            // FFmpeg itself reported failure after producing some audio (for example most frames failed
            // to decode). Importing that would silently trim or garble the sound, so reject it.
            return DecodeResult.Fail(
                $"Could not decode \"{name}\": FFmpeg stopped with an error part-way through, so the sound would be incomplete" +
                FormatDetail(detail) + ".");
        }

        long samplesPerChannel = run.PcmLength / BytesPerFrame;
        LengthValidation validation = SoundLengthValidator.Validate(samplesPerChannel, SampleRate);
        if (!validation.Accepted)
            return DecodeResult.Fail($"Could not import \"{name}\": " + (validation.Error ?? "its length is not allowed."));

        byte[] wav = BuildWav(run.Pcm, checked((int)(samplesPerChannel * BytesPerFrame)));

        return new DecodeResult
        {
            Success = true,
            WavData = wav,
            DurationSeconds = validation.ActualSeconds,
            SamplesPerChannel = samplesPerChannel,
        };
    }

    private FfmpegRun RunFfmpeg(string fullPath)
    {
        ProcessStartInfo startInfo = CreateStartInfo(FfmpegPath);
        // Contract command line: ffmpeg -v error -i <input> -map a:0 -f f32le -ar 48000 -ac 2 pipe:1
        // plus two hardening options: -nostdin (never read keyboard commands from an inherited console)
        // and -protocol_whitelist file (playlist-style inputs can never make FFmpeg open network URLs).
        foreach (string arg in new[]
                 {
                     "-nostdin",
                     "-v", "error",
                     "-protocol_whitelist", "file",
                     "-i", fullPath,
                     "-map", "a:0",
                     "-f", "f32le",
                     "-ar", "48000",
                     "-ac", "2",
                     "pipe:1",
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stopwatch = Stopwatch.StartNew();
        Task<string> stderrTask = DrainAsync(process.StandardError);

        bool killedByTimeout = false;
        bool overflowed = false;
        var pcm = new MemoryStream(InitialPcmCapacity);

        using (var timeoutCts = new CancellationTokenSource(DecodeTimeout))
        {
            using (timeoutCts.Token.Register(() =>
                   {
                       if (TryKill(process))
                           killedByTimeout = true;
                   }))
            {
                overflowed = ReadPcm(process.StandardOutput.BaseStream, pcm);

                if (overflowed)
                {
                    // More than 31 s of audio: stop now instead of buffering an unbounded stream.
                    TryKill(process);
                    process.WaitForExit(ShutdownGrace);
                }
                else
                {
                    TimeSpan remaining = DecodeTimeout - stopwatch.Elapsed;
                    if (remaining < TimeSpan.Zero)
                        remaining = TimeSpan.Zero;

                    if (!process.WaitForExit(remaining + ShutdownGrace))
                    {
                        TryKill(process);
                        process.WaitForExit(ShutdownGrace);
                        killedByTimeout = true;
                    }
                }
            }
            // Disposing the registration waits for a running timeout callback, so the flag is final here.
        }

        string stderr = stderrTask.Wait(ShutdownGrace) ? stderrTask.Result : string.Empty;
        int? exitCode = process.HasExited ? process.ExitCode : null;

        return new FfmpegRun(pcm.GetBuffer(), pcm.Length, overflowed, killedByTimeout && !overflowed, exitCode, stderr);
    }

    /// <summary>Copies stdout into <paramref name="pcm"/> until EOF or the 31 s cap is exceeded.</summary>
    /// <returns><see langword="true"/> when the output exceeded the cap.</returns>
    private static bool ReadPcm(Stream stdout, MemoryStream pcm)
    {
        var chunk = new byte[ReadChunkBytes];
        try
        {
            int read;
            while ((read = stdout.Read(chunk, 0, chunk.Length)) > 0)
            {
                long room = CapBytes - pcm.Length;
                if (read > room)
                    return true;

                pcm.Write(chunk, 0, read);
            }
        }
        catch (IOException)
        {
            // The pipe broke (process killed or crashed). Whatever arrived is evaluated by the caller.
        }
        catch (ObjectDisposedException)
        {
            // Same as above: the stream was torn down while reading.
        }

        return false;
    }

    private string BuildTooLongMessage(string fullPath)
    {
        double? probed = TryProbeDurationSeconds(fullPath);

        // Only trust the probed length when it agrees that the sound exceeds the read cap; container
        // metadata can be wrong, and the decoded stream has already proven it is longer than 31 s.
        if (probed is double seconds && seconds > CapSeconds && seconds < 1e9)
        {
            long samples = (long)Math.Ceiling(seconds * SampleRate);
            LengthValidation validation = SoundLengthValidator.Validate(samples, SampleRate);
            if (!validation.Accepted && validation.Error is not null)
                return validation.Error;
        }

        double max = SoundLengthValidator.MaxSeconds;
        return $"Sound is longer than {max:0.000} s; the maximum is {max:0.000} s. The file was not imported (files are never trimmed).";
    }

    private double? TryProbeDurationSeconds(string fullPath)
    {
        string? probePath = _ffprobePath.Value;
        if (probePath is null)
            return null;

        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(probePath);
            startInfo.StandardOutputEncoding = Utf8NoBom;
            foreach (string arg in new[]
                     {
                         "-v", "error",
                         "-protocol_whitelist", "file",
                         "-show_entries", "format=duration",
                         "-of", "default=nw=1:nk=1",
                         fullPath,
                     })
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = DrainAsync(process.StandardOutput);
            Task<string> stderrTask = DrainAsync(process.StandardError);

            if (!process.WaitForExit(ProbeTimeout))
            {
                TryKill(process);
                process.WaitForExit(ShutdownGrace);
                return null;
            }

            if (!stdoutTask.Wait(ShutdownGrace))
                return null;

            stderrTask.Wait(ShutdownGrace);

            if (process.ExitCode != 0)
                return null;

            string firstLine = stdoutTask.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;

            if (double.TryParse(firstLine, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && double.IsFinite(seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
                                       or AggregateException)
        {
            // FFprobe is optional; the caller falls back to a generic "longer than 30.000 s" message.
        }

        return null;
    }

    private string? ResolveProbePath()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FfmpegPath);
            if (!string.IsNullOrEmpty(directory))
            {
                string sibling = Path.Combine(directory, FfmpegLocator.FfprobeExeName);
                if (File.Exists(sibling))
                    return sibling;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // Fall through to the regular search.
        }

        return FfmpegLocator.FindProbe();
    }

    private static string BuildNoPcmMessage(string name, FfmpegRun run, string? detail)
    {
        if (MentionsMissingAudioStream(run.Stderr))
            return $"Could not decode \"{name}\": the file contains no audio stream.";

        if (run.ExitCode != 0)
        {
            return $"Could not decode \"{name}\": the format is unsupported or the file is corrupt" +
                   FormatDetail(detail) + ".";
        }

        return $"Could not decode \"{name}\": FFmpeg produced no audio samples; the file may be empty, unsupported, or corrupt" +
               FormatDetail(detail) + ".";
    }

    private static string FormatDetail(string? detail) =>
        string.IsNullOrEmpty(detail) ? string.Empty : $" (FFmpeg: {detail})";

    private static bool MentionsMissingAudioStream(string stderr) =>
        stderr.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the single most informative line from FFmpeg's <c>-v error</c> output: the reason given on an
    /// "Error opening input:" line when present, otherwise the first line that is not boilerplate.
    /// </summary>
    private static string? PickUsefulStderrLine(string stderr, string fullPath, string name)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        string? firstUseful = null;
        foreach (string rawLine in stderr.Split('\n'))
        {
            string line = LogContextPrefix().Replace(rawLine.Trim(), string.Empty).Trim();
            if (line.Length == 0)
                continue;

            const string openingInput = "Error opening input:";
            if (line.StartsWith(openingInput, StringComparison.OrdinalIgnoreCase))
            {
                string reason = line[openingInput.Length..].Trim();
                if (reason.Length > 0)
                    return Shorten(reason);
            }

            if (firstUseful is null && !IsBoilerplate(line))
                firstUseful = line.Replace(fullPath, name, StringComparison.OrdinalIgnoreCase);
        }

        return firstUseful is null ? null : Shorten(firstUseful);
    }

    private static bool IsBoilerplate(string line) =>
        line.StartsWith("Error opening input file", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Error opening output", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Error parsing options", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("To ignore this", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Conversion failed", StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text)
    {
        string trimmed = text.Trim().TrimEnd('.');
        return trimmed.Length <= MaxDetailChars ? trimmed : string.Concat(trimmed.AsSpan(0, MaxDetailChars), "...");
    }

    /// <summary>Wraps whole stereo float frames in a RIFF WAV container entirely in memory.</summary>
    private static byte[] BuildWav(byte[] pcm, int byteCount)
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        using var output = new MemoryStream(byteCount + 128);

        // WaveFileWriter disposes its target stream; IgnoreDisposeStream keeps the MemoryStream readable.
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(output), format))
        {
            writer.Write(pcm, 0, byteCount);
        }

        return output.ToArray();
    }

    private static ProcessStartInfo CreateStartInfo(string exePath) => new()
    {
        FileName = exePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardInput = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardErrorEncoding = Utf8NoBom,
        WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
    };

    /// <summary>
    /// Reads a redirected text stream to the end on a pool thread (so the child can never block on a full
    /// pipe), keeping only the first <see cref="MaxCapturedStderrChars"/> characters.
    /// </summary>
    private static Task<string> DrainAsync(StreamReader reader) => Task.Run(async () =>
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                int room = MaxCapturedStderrChars - captured.Length;
                if (room > 0)
                    captured.Append(buffer, 0, Math.Min(room, read));
            }
        }
        catch (IOException)
        {
            // Pipe closed abruptly; keep what was captured.
        }
        catch (ObjectDisposedException)
        {
            // Process torn down while draining; keep what was captured.
        }

        return captured.ToString();
    });

    /// <returns><see langword="true"/> when a still-running process was told to terminate.</returns>
    private static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
                return false;

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException
                                       or AggregateException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(\[[^\]]*\]\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LogContextPrefix();

    private sealed record FfmpegRun(
        byte[] Pcm,
        long PcmLength,
        bool Overflowed,
        bool TimedOut,
        int? ExitCode,
        string Stderr);
}
