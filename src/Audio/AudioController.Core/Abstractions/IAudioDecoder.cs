namespace AudioController.Core.Abstractions;

/// <summary>Result of decoding one input file to the canonical internal format.</summary>
public sealed class DecodeResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public double DurationSeconds { get; init; }

    public long SamplesPerChannel { get; init; }

    /// <summary>Canonical internal format: WAV, IEEE float 32-bit, 48 000 Hz, stereo.</summary>
    public byte[]? WavData { get; init; }

    public static DecodeResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Decodes any supported audio file to the canonical internal WAV format, enforcing the
/// 30-second maximum. Implementations must never write decoded audio to disk.
/// </summary>
public interface IAudioDecoder
{
    DecodeResult Decode(string inputFilePath);
}
