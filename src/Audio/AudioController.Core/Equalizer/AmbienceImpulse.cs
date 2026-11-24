using System.Buffers.Binary;

namespace AudioController.Core.Equalizer;

/// <summary>
/// The impulse response behind Equalizer APO's <c>Convolution: ambience.wav</c> line (pure; the writer saves the
/// bytes next to <c>AudioController.txt</c>): a unit impulse at t = 0 (the dry signal, unchanged) plus a short,
/// exponentially decaying noise tail — a light room ambience.
/// </summary>
/// <remarks>
/// <para>Stereo, 32-bit IEEE float, <see cref="LengthSeconds"/> long, at the device's sample rate (Equalizer APO
/// refuses an impulse response whose rate differs from the device's; 48 kHz by default). The tail falls by 60 dB over
/// the file (RT60 = 0.35 s), is normalized to unit energy and scaled by the wet level
/// <see cref="FxEffects.AmbienceWet"/>, so its energy relative to the dry impulse is wet² (−9.1 dB at 100 %) — the
/// same level as the app's own soundboard-bus reverb. Left and right use independent noise (decorrelated, so the
/// ambience sounds wide). Deterministic: the same level and rate always give the same bytes, so an unchanged
/// slider never rewrites the file.</para>
/// </remarks>
public static class AmbienceImpulse
{
    /// <summary>The file name, next to <see cref="EqualizerApoConfig.FileName"/> in Equalizer APO's config folder.</summary>
    public const string FileName = "ambience.wav";

    /// <summary>Default sample rate in Hz.</summary>
    public const int DefaultSampleRate = 48000;

    /// <summary>Stereo.</summary>
    public const int Channels = 2;

    /// <summary>Length of the impulse response (and the RT60 of its tail) in seconds.</summary>
    public const double LengthSeconds = 0.35;

    /// <summary>The tail's level at the end of the file, relative to its start.</summary>
    public const double DecayDb = -60;

    private const uint SeedLeft = 0x9E3779B9u;
    private const uint SeedRight = 0x85EBCA6Bu;

    /// <summary>Frames in the file at <paramref name="sampleRate"/>.</summary>
    public static int FrameCount(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 768000);
        return (int)Math.Round(sampleRate * LengthSeconds, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The impulse response for an Ambience slider value: <c>[channel][frame]</c>. Frame 0 is exactly 1.0 on both
    /// channels (the dry signal); frames 1… hold the tail (all zero at 0 %).
    /// </summary>
    public static float[][] Generate(int percent, int sampleRate = DefaultSampleRate)
    {
        int frames = FrameCount(sampleRate);
        double wet = FxEffects.AmbienceWet(percent);
        var result = new float[Channels][];
        for (int c = 0; c < Channels; c++)
        {
            var ch = new float[frames];
            ch[0] = 1f;
            if (wet > 0)
            {
                FillTail(ch, sampleRate, wet, c == 0 ? SeedLeft : SeedRight);
            }

            result[c] = ch;
        }

        return result;
    }

    /// <summary>The <see cref="FileName"/> bytes for an Ambience slider value (RIFF/WAVE, IEEE float, stereo).</summary>
    public static byte[] CreateWav(int percent, int sampleRate = DefaultSampleRate) => ToWav(Generate(percent, sampleRate), sampleRate);

    /// <summary>
    /// A RIFF/WAVE file (format 3 = IEEE float, 32-bit, with a <c>fact</c> chunk) holding <paramref name="channels"/>
    /// interleaved. All channels must have the same length.
    /// </summary>
    public static byte[] ToWav(float[][] channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentOutOfRangeException.ThrowIfZero(channels.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        int channelCount = channels.Length;
        int frames = channels[0].Length;
        if (channels.Any(c => c is null || c.Length != frames))
        {
            throw new ArgumentException("All channels must have the same length.", nameof(channels));
        }

        const int bytesPerSample = 4;
        int dataBytes = checked(frames * channelCount * bytesPerSample);
        const int fmtChunkBytes = 18; // WAVEFORMATEX with cbSize = 0
        const int factChunkBytes = 4;
        int riffSize = 4 + (8 + fmtChunkBytes) + (8 + factChunkBytes) + (8 + dataBytes);
        var bytes = new byte[8 + riffSize];
        var span = bytes.AsSpan();
        int o = 0;

        WriteAscii(span, ref o, "RIFF");
        WriteU32(span, ref o, (uint)riffSize);
        WriteAscii(span, ref o, "WAVE");

        WriteAscii(span, ref o, "fmt ");
        WriteU32(span, ref o, fmtChunkBytes);
        WriteU16(span, ref o, 3); // WAVE_FORMAT_IEEE_FLOAT
        WriteU16(span, ref o, (ushort)channelCount);
        WriteU32(span, ref o, (uint)sampleRate);
        WriteU32(span, ref o, (uint)(sampleRate * channelCount * bytesPerSample));
        WriteU16(span, ref o, (ushort)(channelCount * bytesPerSample));
        WriteU16(span, ref o, 32);
        WriteU16(span, ref o, 0); // cbSize

        WriteAscii(span, ref o, "fact");
        WriteU32(span, ref o, factChunkBytes);
        WriteU32(span, ref o, (uint)frames);

        WriteAscii(span, ref o, "data");
        WriteU32(span, ref o, (uint)dataBytes);
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channelCount; c++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[o..], channels[c][f]);
                o += bytesPerSample;
            }
        }

        return bytes;
    }

    /// <summary>
    /// Exponentially decaying uniform noise from frame 1 on (−60 dB at the end), normalized to unit energy, × wet.
    /// </summary>
    private static void FillTail(float[] ch, int sampleRate, double wet, uint seed)
    {
        int frames = ch.Length;
        var tail = new double[frames];
        uint state = seed;
        double energy = 0;
        double decayPerSecond = DecayDb / 20.0 / LengthSeconds; // log10 amplitude per second
        for (int i = 1; i < frames; i++)
        {
            double envelope = Math.Pow(10, decayPerSecond * i / sampleRate);
            double noise = NextUniform(ref state);
            double v = noise * envelope;
            tail[i] = v;
            energy += v * v;
        }

        double scale = energy > 0 ? wet / Math.Sqrt(energy) : 0;
        for (int i = 1; i < frames; i++)
        {
            ch[i] = (float)(tail[i] * scale);
        }
    }

    /// <summary>xorshift32 → uniform in [−1, 1). Deterministic on every platform.</summary>
    private static double NextUniform(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state / 2147483648.0 - 1.0;
    }

    private static void WriteAscii(Span<byte> span, ref int o, string text)
    {
        foreach (char ch in text)
        {
            span[o++] = (byte)ch;
        }
    }

    private static void WriteU16(Span<byte> span, ref int o, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[o..], value);
        o += 2;
    }

    private static void WriteU32(Span<byte> span, ref int o, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[o..], value);
        o += 4;
    }
}
