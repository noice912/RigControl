namespace RigControl.Radio.Audio;

/// <summary>The last N samples of 16 kHz mono sound. Thread-safe: the capture thread writes, the detector reads.</summary>
public sealed class RingBuffer(int capacitySamples)
{
    private readonly float[] _data = new float[capacitySamples > 0 ? capacitySamples : throw new ArgumentOutOfRangeException(nameof(capacitySamples))];
    private readonly object _gate = new();
    private int _head;
    private int _filled;

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            foreach (var s in samples)
            {
                _data[_head] = s;
                _head = (_head + 1) % _data.Length;
            }
            _filled = Math.Min(_data.Length, _filled + samples.Length);
        }
    }

    /// <summary>Forgets everything heard so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _filled = 0;
        }
    }

    /// <summary>The newest <paramref name="samples"/> samples, oldest first (fewer when it doesn't hold that many yet).</summary>
    public float[] Last(int samples)
    {
        lock (_gate)
        {
            var n = Math.Clamp(samples, 0, _filled);
            var result = new float[n];
            var start = (_head - n + _data.Length) % _data.Length;
            for (var i = 0; i < n; i++) result[i] = _data[(start + i) % _data.Length];
            return result;
        }
    }

    /// <summary>Root-mean-square level of the newest <paramref name="samples"/> samples (0 = silence).</summary>
    public float Rms(int samples)
    {
        var last = Last(samples);
        if (last.Length == 0) return 0;
        double sum = 0;
        foreach (var s in last) sum += s * s;
        return (float)Math.Sqrt(sum / last.Length);
    }
}
