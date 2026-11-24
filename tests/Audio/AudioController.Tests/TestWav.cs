using NAudio.Wave;

namespace AudioController.Tests;

/// <summary>Generates WAV test inputs with NAudio. Frame counts are exact, so length tests are deterministic.</summary>
internal static class TestWav
{
    /// <summary>
    /// Writes a 16-bit PCM WAV of exactly <paramref name="frames"/> frames: a sine of
    /// <paramref name="frequencyHz"/> at <paramref name="amplitude"/> (0 = digital silence) on every channel.
    /// </summary>
    public static void WritePcm16(
        string path,
        int sampleRate,
        int channels,
        long frames,
        double frequencyHz = 440.0,
        double amplitude = 0.5)
    {
        var format = new WaveFormat(sampleRate, 16, channels);
        using var writer = new WaveFileWriter(path, format);

        const int chunkFrames = 8192;
        var chunk = new byte[chunkFrames * format.BlockAlign];
        long written = 0;
        while (written < frames)
        {
            int n = (int)Math.Min(chunkFrames, frames - written);
            if (amplitude == 0)
            {
                // Silence: the zero-filled chunk is already correct.
                writer.Write(chunk, 0, n * format.BlockAlign);
            }
            else
            {
                int pos = 0;
                for (int f = 0; f < n; f++)
                {
                    double t = (double)(written + f) / sampleRate;
                    short s = (short)Math.Round(Math.Sin(2 * Math.PI * frequencyHz * t) * amplitude * short.MaxValue);
                    for (int c = 0; c < channels; c++)
                    {
                        chunk[pos++] = (byte)(s & 0xFF);
                        chunk[pos++] = (byte)((s >> 8) & 0xFF);
                    }
                }

                writer.Write(chunk, 0, n * format.BlockAlign);
            }

            written += n;
        }
    }

    /// <summary>Builds a canonical (IEEE float, 48 kHz, stereo) WAV entirely in memory.</summary>
    public static byte[] CanonicalWavBytes(int frames, float amplitude = 0.25f)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(ms), format))
        {
            var samples = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                float v = amplitude * MathF.Sin(2 * MathF.PI * 440f * i / 48000f);
                samples[2 * i] = v;
                samples[2 * i + 1] = -v;
            }

            writer.WriteSamples(samples, 0, samples.Length);
        }

        return ms.ToArray();
    }

    /// <summary>Reads every float sample of an IEEE-float WAV held in memory.</summary>
    public static float[] ReadAllFloatSamples(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var provider = reader.ToSampleProvider();
        var all = new List<float>((int)(reader.SampleCount * reader.WaveFormat.Channels));
        var buffer = new float[16384];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return all.ToArray();
    }
}
