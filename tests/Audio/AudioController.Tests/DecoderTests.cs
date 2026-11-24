using System.Text;
using AudioController.Core.Abstractions;
using AudioController.Decode;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace AudioController.Tests;

/// <summary>
/// Integration tests against the REAL bundled FFmpeg (SPEC 4.2 / 7 "Length validation" and "Format detection").
/// Inputs are generated with NAudio in a per-test temp directory.
/// </summary>
/// <remarks>
/// Per CONTRACTS.md: when FFmpeg is genuinely absent (no <c>tools\ffmpeg\ffmpeg.exe</c> in the source tree and
/// none on PATH) these facts return early. When the source tree bundles FFmpeg they always run, and
/// <see cref="FfmpegLocator.Find"/> returning null is a test failure (a locator bug), not a skip.
/// </remarks>
public sealed class DecoderTests : IDisposable
{
    private const int CanonicalRate = 48_000;

    private readonly TempDirectory _temp = new();
    private readonly ITestOutputHelper _output;
    private readonly string? _ffmpeg;

    public DecoderTests(ITestOutputHelper output)
    {
        _output = output;
        _ffmpeg = FfmpegLocator.Find();
        if (_ffmpeg is null)
        {
            string? bundled = BundledFfmpegPath();
            Assert.True(
                bundled is null,
                $"FfmpegLocator.Find() returned null, but the source tree bundles FFmpeg at \"{bundled}\" " +
                $"(searched from \"{AppContext.BaseDirectory}\"). This is a locator bug.");
        }
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The source tree's bundled ffmpeg.exe, or null when it is not present.</summary>
    private static string? BundledFfmpegPath()
    {
        string? root = TestBytes.FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string path = Path.Combine(root, "tools", "ffmpeg", FfmpegLocator.FfmpegExeName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Returns a decoder, or null (treat as skip) only when FFmpeg is genuinely unavailable.</summary>
    private FfmpegDecoder? CreateDecoder()
    {
        if (_ffmpeg is null)
        {
            _output.WriteLine("FFmpeg is not available on this machine; returning early (treated as skip).");
            return null;
        }

        _output.WriteLine($"Using FFmpeg at {_ffmpeg}");
        return new FfmpegDecoder(_ffmpeg);
    }

    /// <summary>Decodes and logs the outcome, so a failing assertion shows FFmpeg's actual verdict.</summary>
    private DecodeResult Decode(FfmpegDecoder decoder, string? input)
    {
        DecodeResult result = decoder.Decode(input!);
        _output.WriteLine(
            $"Decode(\"{input}\") => Success={result.Success}, SamplesPerChannel={result.SamplesPerChannel}, " +
            $"DurationSeconds={result.DurationSeconds:R}, Error={result.Error ?? "<none>"}");
        return result;
    }

    // ---------------------------------------------------------------- locator

    [Fact]
    public void FfmpegLocatorFind_RunningFromRepo_FindsBundledFfmpeg()
    {
        string? bundled = BundledFfmpegPath();
        if (bundled is null)
        {
            return; // Not running from a source tree that bundles FFmpeg.
        }

        string? found = FfmpegLocator.Find();

        Assert.NotNull(found);
        Assert.True(File.Exists(found));
        Assert.Equal(Path.GetFullPath(bundled), found, ignoreCase: true);
    }

    [Fact]
    public void FfmpegLocatorFindProbe_RunningFromRepo_FindsBundledFfprobe()
    {
        string? root = TestBytes.FindRepoRoot();
        string? bundled = root is null ? null : Path.Combine(root, "tools", "ffmpeg", FfmpegLocator.FfprobeExeName);
        if (bundled is null || !File.Exists(bundled))
        {
            return;
        }

        string? found = FfmpegLocator.FindProbe();

        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(bundled), found, ignoreCase: true);
    }

    [Fact]
    public void FfmpegLocatorFind_ReturnsFullyQualifiedPathToFfmpegExe()
    {
        string? found = FfmpegLocator.Find();
        if (found is null)
        {
            return; // genuinely absent; the constructor already failed if the repo bundles it
        }

        Assert.True(Path.IsPathFullyQualified(found));
        Assert.Equal(FfmpegLocator.FfmpegExeName, Path.GetFileName(found), ignoreCase: true);
    }

    // ---------------------------------------------------------------- successful decode

    [Fact]
    public void Decode_TwoSecond44kHz16BitMonoWav_ReturnsIeeeFloat48kHzStereoWav()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("tone 44k mono.wav");
        TestWav.WritePcm16(input, sampleRate: 44_100, channels: 1, frames: 2 * 44_100, frequencyHz: 440, amplitude: 0.5);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.NotNull(result.WavData);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(result.WavData!, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(result.WavData!, 8, 4));

        using var reader = new WaveFileReader(new MemoryStream(result.WavData!));
        Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
        Assert.Equal(CanonicalRate, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(32, reader.WaveFormat.BitsPerSample);

        Assert.InRange(reader.TotalTime.TotalSeconds, 1.99, 2.01);
        Assert.InRange(result.DurationSeconds, 1.99, 2.01);
        Assert.Equal(reader.SampleCount, result.SamplesPerChannel);
        Assert.Equal((double)result.SamplesPerChannel / CanonicalRate, result.DurationSeconds, precision: 9);
    }

    [Fact]
    public void Decode_MonoSine_UpmixesToTwoIdenticalAudibleChannels()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("sine.wav");
        TestWav.WritePcm16(input, sampleRate: 48_000, channels: 1, frames: 48_000, frequencyHz: 1000, amplitude: 0.5);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(48_000, result.SamplesPerChannel); // same rate in and out: frame count is exact
        float[] samples = TestWav.ReadAllFloatSamples(result.WavData!);
        Assert.Equal(2 * 48_000, samples.Length);

        float peak = 0;
        for (int i = 0; i < samples.Length; i += 2)
        {
            Assert.Equal(samples[i], samples[i + 1]); // left == right for a mono source
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        Assert.InRange(peak, 0.3f, 1.0f); // the tone survived decoding (not silence, not clipped garbage)
    }

    [Fact]
    public void Decode_StereoFloat48kWav_IsBitExactPassThrough()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        byte[] canonical = TestWav.CanonicalWavBytes(frames: 4800, amplitude: 0.3f);
        string input = _temp.Combine("canonical.wav");
        File.WriteAllBytes(input, canonical);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(4800, result.SamplesPerChannel);
        Assert.Equal(TestWav.ReadAllFloatSamples(canonical), TestWav.ReadAllFloatSamples(result.WavData!));
    }

    [Fact]
    public void Decode_29Second44kHzWav_IsAccepted()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("29s.wav");
        TestWav.WritePcm16(input, sampleRate: 44_100, channels: 1, frames: 29L * 44_100, amplitude: 0);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.InRange(result.DurationSeconds, 28.99, 29.01);
    }

    [Theory]
    [InlineData("flac", "flac", 0.001)]
    [InlineData("m4a", "aac", 0.05)]
    [InlineData("mp2", "mp2", 0.06)]
    [InlineData("ogg", "flac", 0.001)] // FLAC-in-Ogg: a different container with the same lossless codec
    [InlineData("mka", "pcm_s24le", 0.001)]
    public void Decode_OtherFormatsEncodedByFfmpeg_AreAcceptedWithMatchingDuration(string extension, string codec, double tolerance)
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string wav = _temp.Combine("source.wav");
        TestWav.WritePcm16(wav, sampleRate: 44_100, channels: 2, frames: 44_100 + 22_050, frequencyHz: 440, amplitude: 0.4);
        string input = _temp.Combine($"encoded.{extension}");
        EncodeWithFfmpeg(wav, input, "-c:a", codec);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.InRange(result.DurationSeconds, 1.5 - tolerance, 1.5 + tolerance);
        using var reader = new WaveFileReader(new MemoryStream(result.WavData!));
        Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
        Assert.Equal(CanonicalRate, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
    }

    [Fact]
    public void Decode_VideoContainerWithAudioTrack_DecodesTheAudio()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        // An .mp4 holding a tiny video stream plus an AAC audio track (SPEC 4.2: "MKV/MP4 audio tracks").
        string input = _temp.Combine("clip.mp4");
        RunFfmpeg(
            "-f", "lavfi", "-i", "color=c=black:s=64x64:r=10:d=1",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
            "-c:v", "mpeg4", "-c:a", "aac", "-shortest", input);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.InRange(result.DurationSeconds, 0.9, 1.1);
    }

    [Fact]
    public void Decode_VideoFileWithoutAudio_IsRejectedAsHavingNoAudioStream()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("silent-movie.mkv");
        RunFfmpeg("-f", "lavfi", "-i", "color=c=black:s=64x64:r=10:d=1", "-c:v", "mpeg4", "-an", input);

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.Contains("silent-movie.mkv", result.Error);
        Assert.Contains("no audio", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private void EncodeWithFfmpeg(string input, string output, params string[] codecArgs) =>
        RunFfmpeg(new[] { "-i", input }.Concat(codecArgs).Append(output).ToArray());

    /// <summary>Runs the located FFmpeg to PRODUCE a test input (test setup only; not the code under test).</summary>
    private void RunFfmpeg(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(_ffmpeg!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in new[] { "-nostdin", "-v", "error", "-y" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "FFmpeg (test input generation) timed out.");
        stdout.Wait();
        Assert.True(process.ExitCode == 0, $"FFmpeg failed to generate the test input: {stderr}");
    }

    // ---------------------------------------------------------------- 30-second boundary

    [Fact]
    public void Decode_Exactly30Point000SecondWav_IsAccepted()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        // 48 kHz input so FFmpeg does not resample and the frame count reaching the validator is exact.
        string input = _temp.Combine("exactly-30s.wav");
        TestWav.WritePcm16(input, sampleRate: CanonicalRate, channels: 1, frames: 30L * CanonicalRate, amplitude: 0);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(1_440_000, result.SamplesPerChannel);
        Assert.Equal(30.0, result.DurationSeconds);
        Assert.NotNull(result.WavData);
        using var reader = new WaveFileReader(new MemoryStream(result.WavData!));
        Assert.Equal(1_440_000, reader.SampleCount); // nothing trimmed, nothing padded
    }

    [Fact]
    public void Decode_30Point001SecondWav_IsRejectedWithActualLength()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        using var _ = CultureScope.Invariant();
        string input = _temp.Combine("30.001s.wav");
        TestWav.WritePcm16(input, sampleRate: CanonicalRate, channels: 1, frames: 1_440_048, amplitude: 0);

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.NotNull(result.Error);
        Assert.Contains("30.001", result.Error);
        Assert.Contains("30.000", result.Error);
        // The file name also contains "30.001", so pin the actual-length wording and the name separately.
        Assert.Contains("Sound is 30.001 s long", result.Error);
        Assert.Contains("\"30.001s.wav\"", result.Error);
    }

    [Fact]
    public void Decode_OneFrameOver30SecondsWav_IsRejected()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("30s+1.wav");
        TestWav.WritePcm16(input, sampleRate: CanonicalRate, channels: 1, frames: 1_440_001, amplitude: 0);

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.Contains("30.000", result.Error);
    }

    [Fact]
    public void Decode_FarTooLongWav_IsRejectedNotTrimmed()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        using var _ = CultureScope.Invariant();
        // 45 s at 8 kHz mono keeps the input file small (720 KB).
        string input = _temp.Combine("45s.wav");
        TestWav.WritePcm16(input, sampleRate: 8_000, channels: 1, frames: 45L * 8_000, amplitude: 0);

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.NotNull(result.Error);
        Assert.Contains("30.000", result.Error);
        Assert.Contains("45s.wav", result.Error); // names the file
        Assert.Contains("trim", result.Error, StringComparison.OrdinalIgnoreCase);
        if (FfmpegLocator.FindProbe() is not null)
        {
            // With FFprobe available the real length is reported, not just "longer than 30 s".
            Assert.Contains("45.000", result.Error);
        }
    }

    // ---------------------------------------------------------------- format detection / bad input

    [Fact]
    public void Decode_TextFileRenamedToMp3_IsRejectedWithClearError()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("not really audio.mp3");
        File.WriteAllText(input, string.Concat(Enumerable.Repeat("This is a plain text file, not audio.\r\n", 200)));

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("not really audio.mp3", result.Error); // names the file
        Assert.Contains("Could not decode", result.Error);
    }

    [Fact]
    public void Decode_RandomBinaryRenamedToWav_IsRejectedWithClearError()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("garbage.wav");
        File.WriteAllBytes(input, TestBytes.Deterministic(32 * 1024, seed: 3));

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.Contains("garbage.wav", result.Error);
    }

    [Fact]
    public void Decode_EmptyFile_IsRejected()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("empty.flac");
        File.WriteAllBytes(input, Array.Empty<byte>());

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.Contains("empty.flac", result.Error);
    }

    [Fact]
    public void Decode_WavWithZeroFrames_IsRejectedAsZeroLength()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("zero-frames.wav");
        TestWav.WritePcm16(input, sampleRate: 48_000, channels: 2, frames: 0);

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Decode_NonexistentPath_IsRejectedWithNotFoundError()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("does-not-exist.mp3");

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Null(result.WavData);
        Assert.Contains("does-not-exist.mp3", result.Error);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_NullOrBlankPath_IsRejected(string? input)
    {
        // No FFmpeg run is needed for this; any path works for the constructor.
        var decoder = new FfmpegDecoder(_ffmpeg ?? Path.Combine(_temp.Path, "ffmpeg.exe"));

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Decode_FfmpegMissingAtGivenPath_IsRejectedWithClearError()
    {
        string input = _temp.Combine("tone.wav");
        TestWav.WritePcm16(input, sampleRate: 48_000, channels: 1, frames: 480);
        var decoder = new FfmpegDecoder(_temp.Combine("no-such-dir", "ffmpeg.exe"));

        DecodeResult result = Decode(decoder, input);

        Assert.False(result.Success);
        Assert.Contains("FFmpeg", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_NullOrBlankFfmpegPath_Throws(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FfmpegDecoder(path!));
    }

    [Fact]
    public void Decode_DoesNotWriteAnyFilesNextToInput()
    {
        var decoder = CreateDecoder();
        if (decoder is null) return;
        string input = _temp.Combine("only-file.wav");
        TestWav.WritePcm16(input, sampleRate: 44_100, channels: 1, frames: 4410);

        DecodeResult result = Decode(decoder, input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { input }, Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }
}
