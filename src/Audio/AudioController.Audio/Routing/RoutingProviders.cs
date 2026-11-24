using AudioController.Core.AudioMath;
using NAudio.Wave;

namespace AudioController.Audio.Routing;

/// <summary>Live counters shared between the control thread and the render thread of one session.</summary>
internal sealed class PlaybackCounters
{
    /// <summary>Sounds whose main-bus reader is still producing audio (gates the final-stage sum).</summary>
    public int MainActive;

    /// <summary>Sounds still playing on any bus (main or monitor).</summary>
    public int Instances;
}

/// <summary>
/// Byte-level jitter buffer in front of the captured microphone stream. It never alters sample values:
/// it only waits for a small cushion before releasing audio (silence while priming), and discards whole
/// frames when clock drift between the microphone and the output lets latency build up.
/// </summary>
internal sealed class MicJitterBuffer : IWaveProvider
{
    private readonly BufferedWaveProvider _buffer;
    private readonly int _blockAlign;
    private readonly int _primeBytes;
    private readonly int _trimAboveBytes;
    private readonly int _trimToBytes;
    private byte[] _discard = [];
    private bool _priming = true;

    public MicJitterBuffer(BufferedWaveProvider buffer, TimeSpan prime, TimeSpan trimAbove, TimeSpan trimTo)
    {
        _buffer = buffer;
        var format = buffer.WaveFormat;
        _blockAlign = Math.Max(1, format.BlockAlign);
        _primeBytes = Align(format.AverageBytesPerSecond * prime.TotalSeconds);
        _trimAboveBytes = Align(format.AverageBytesPerSecond * trimAbove.TotalSeconds);
        _trimToBytes = Align(format.AverageBytesPerSecond * trimTo.TotalSeconds);
    }

    public WaveFormat WaveFormat => _buffer.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int buffered = _buffer.BufferedBytes;
        if (_priming)
        {
            if (buffered < Math.Max(_primeBytes, count))
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            _priming = false;
        }
        else if (buffered > _trimAboveBytes)
        {
            int drop = buffered - _trimToBytes;
            drop -= drop % _blockAlign;
            if (drop > 0)
            {
                if (_discard.Length < drop)
                {
                    _discard = new byte[drop];
                }

                _buffer.Read(_discard, 0, drop);
                buffered = _buffer.BufferedBytes;
            }
        }

        if (buffered < count)
        {
            // Underrun: deliver what exists (the buffered provider pads with silence), then re-prime.
            _priming = true;
        }

        return _buffer.Read(buffer, offset, count);
    }

    private int Align(double bytes)
    {
        int value = (int)Math.Ceiling(bytes);
        int rem = value % _blockAlign;
        return rem == 0 ? value : value + (_blockAlign - rem);
    }
}

/// <summary>
/// The single final stage feeding the virtual cable. Reads the microphone chain; only while a sound is
/// active it also reads the soundboard bus and sums it in with <see cref="MixerMath.MixInto"/> (the only
/// clamp in the chain). With no sound active the microphone samples are copied out untouched.
/// The peak of every outgoing buffer is recorded for display only.
/// </summary>
internal sealed class RoutingWaveProvider : IWaveProvider
{
    private volatile ISampleProvider _mic;
    private readonly Func<ISampleProvider> _createBoardChain;
    private readonly PlaybackCounters _counters;
    private readonly Action<Exception> _onError;
    private ISampleProvider? _board;
    private float[] _mixBuffer = [];
    private float[] _boardBuffer = [];
    private int _peakBits;
    private int _errorReported;

    /// <param name="outputFormat">The render device format (32-bit float, plain or extensible).</param>
    /// <param name="mic">Microphone chain already at the output rate and channel count.</param>
    /// <param name="createBoardChain">Builds the soundboard chain at the output format (bus → EQ → adapt).
    /// Called at the start of each burst of activity so resampler history never leaks between bursts.</param>
    /// <param name="counters">Session counters; <see cref="PlaybackCounters.MainActive"/> gates the sum.</param>
    /// <param name="onError">Called once (on the render thread) if this stage ever fails; must not throw.</param>
    public RoutingWaveProvider(
        WaveFormat outputFormat,
        ISampleProvider mic,
        Func<ISampleProvider> createBoardChain,
        PlaybackCounters counters,
        Action<Exception> onError)
    {
        EnsureMatchesOutput(mic, outputFormat);
        WaveFormat = outputFormat;
        _mic = mic;
        _createBoardChain = createBoardChain;
        _counters = counters;
        _onError = onError;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Swaps in a new microphone chain (used when an unmuted capture comes back in a different format).
    /// The render thread picks it up on its next read; the old chain is simply no longer read.
    /// </summary>
    public void ReplaceMic(ISampleProvider mic)
    {
        ArgumentNullException.ThrowIfNull(mic);
        EnsureMatchesOutput(mic, WaveFormat);
        _mic = mic;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / sizeof(float);
        int bytes = samples * sizeof(float);
        try
        {
            if (_mixBuffer.Length < samples)
            {
                _mixBuffer = new float[samples];
            }

            var mic = _mic;
            int micRead = Math.Max(0, mic.Read(_mixBuffer, 0, samples));
            if (micRead < samples)
            {
                Array.Clear(_mixBuffer, micRead, samples - micRead);
            }

            if (Volatile.Read(ref _counters.MainActive) > 0)
            {
                _board ??= _createBoardChain();
                if (_boardBuffer.Length < samples)
                {
                    _boardBuffer = new float[samples];
                }

                int boardRead = Math.Max(0, _board.Read(_boardBuffer, 0, samples));
                if (boardRead < samples)
                {
                    Array.Clear(_boardBuffer, boardRead, samples - boardRead);
                }

                MixerMath.MixInto(_mixBuffer, 0, samples, _boardBuffer, 0);
            }
            else
            {
                _board = null;
            }

            RecordPeak(_mixBuffer, samples);
            Buffer.BlockCopy(_mixBuffer, 0, buffer, offset, bytes);
        }
        catch (Exception ex)
        {
            Array.Clear(buffer, offset, bytes);
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                try
                {
                    _onError(ex);
                }
                catch (Exception)
                {
                    // Error reporting must never throw into the render thread.
                }
            }
        }

        if (bytes < count)
        {
            Array.Clear(buffer, offset + bytes, count - bytes);
        }

        return count;
    }

    /// <summary>Returns the highest absolute sample value since the previous call and resets it.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    private static void EnsureMatchesOutput(ISampleProvider mic, WaveFormat outputFormat)
    {
        if (mic.WaveFormat.SampleRate != outputFormat.SampleRate || mic.WaveFormat.Channels != outputFormat.Channels)
        {
            throw new ArgumentException("The microphone chain must already match the output format.", nameof(mic));
        }
    }

    private void RecordPeak(float[] data, int count)
    {
        float max = 0f;
        for (int i = 0; i < count; i++)
        {
            float a = MathF.Abs(data[i]);
            if (a > max)
            {
                max = a;
            }
        }

        // Non-negative IEEE floats order the same as their bit patterns, so a CAS on the bits is a float max.
        int bits = BitConverter.SingleToInt32Bits(max);
        int current;
        do
        {
            current = Volatile.Read(ref _peakBits);
            if (bits <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakBits, bits, current) != current);
    }
}

/// <summary>Feeds a float sample chain to a render device whose format is 32-bit float (plain or extensible).</summary>
internal sealed class FloatWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly Action<Exception> _onError;
    private float[] _samples = [];
    private int _errorReported;

    public FloatWaveProvider(ISampleProvider source, WaveFormat outputFormat, Action<Exception> onError)
    {
        if (source.WaveFormat.SampleRate != outputFormat.SampleRate || source.WaveFormat.Channels != outputFormat.Channels)
        {
            throw new ArgumentException("The source must already match the output format.", nameof(source));
        }

        _source = source;
        _onError = onError;
        WaveFormat = outputFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / sizeof(float);
        int bytes = samples * sizeof(float);
        try
        {
            if (_samples.Length < samples)
            {
                _samples = new float[samples];
            }

            int read = Math.Max(0, _source.Read(_samples, 0, samples));
            if (read < samples)
            {
                Array.Clear(_samples, read, samples - read);
            }

            Buffer.BlockCopy(_samples, 0, buffer, offset, bytes);
        }
        catch (Exception ex)
        {
            Array.Clear(buffer, offset, bytes);
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                try
                {
                    _onError(ex);
                }
                catch (Exception)
                {
                    // Error reporting must never throw into the render thread.
                }
            }
        }

        if (bytes < count)
        {
            Array.Clear(buffer, offset + bytes, count - bytes);
        }

        return count;
    }
}
