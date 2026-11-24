using NAudio.Wave;

namespace AudioController.Audio.Routing;

/// <summary>
/// Hard limit to ±1.0 full scale, used as the final stage of System EQ (an EQ boost of up to +24 dB can
/// push loud system audio past full scale). Samples already inside ±1.0 pass through bit-exact; NaN
/// (which only a broken upstream stage could produce) becomes silence. No other processing.
/// </summary>
public sealed class ClampSampleProvider : ISampleProvider
{
    /// <summary>The largest magnitude this stage lets through.</summary>
    public const float Limit = 1.0f;

    private readonly ISampleProvider _source;

    public ClampSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int end = offset + Math.Max(read, 0);
        for (int i = offset; i < end; i++)
        {
            float v = buffer[i];
            if (v > Limit)
            {
                buffer[i] = Limit;
            }
            else if (v < -Limit)
            {
                buffer[i] = -Limit;
            }
            else if (float.IsNaN(v))
            {
                buffer[i] = 0f;
            }
        }

        return read;
    }
}
