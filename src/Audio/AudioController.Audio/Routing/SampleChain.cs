using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioController.Audio.Routing;

/// <summary>Format helpers: only the conversions strictly needed to match a device format.</summary>
internal static class SampleChain
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>True for 32-bit IEEE float, plain or WAVE_FORMAT_EXTENSIBLE.</summary>
    public static bool IsFloat32(WaveFormat format) =>
        format.BitsPerSample == 32
        && (format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == SubtypeIeeeFloat));

    /// <summary>
    /// Returns a non-extensible format with the identical byte layout (so NAudio's converters accept it),
    /// the same instance when it already is one, or null when the encoding is not PCM/float.
    /// </summary>
    public static WaveFormat? ToPlainEquivalent(WaveFormat format)
    {
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when format.BitsPerSample is 32 or 64:
            case WaveFormatEncoding.Pcm when format.BitsPerSample is 8 or 16 or 24 or 32:
                return format;
        }

        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == SubtypeIeeeFloat && format.BitsPerSample == 32)
            {
                return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            }

            if (extensible.SubFormat == SubtypePcm && format.BitsPerSample is 8 or 16 or 24 or 32)
            {
                return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            }
        }

        return null;
    }

    /// <summary>
    /// Adapts <paramref name="source"/> to the target rate and channel count: channel down-mix before
    /// resampling, up-mix after, and no stage at all when a property already matches.
    /// </summary>
    public static ISampleProvider AdaptTo(ISampleProvider source, int sampleRate, int channels)
    {
        var current = source;
        if (current.WaveFormat.Channels > channels)
        {
            current = new ChannelAdapterSampleProvider(current, channels);
        }

        if (current.WaveFormat.SampleRate != sampleRate)
        {
            current = new WdlResamplingSampleProvider(current, sampleRate);
        }

        if (current.WaveFormat.Channels != channels)
        {
            current = new ChannelAdapterSampleProvider(current, channels);
        }

        return current;
    }
}

/// <summary>Exposes a provider's bytes under an equivalent (non-extensible) format description.</summary>
internal sealed class RelabelWaveProvider(IWaveProvider source, WaveFormat format) : IWaveProvider
{
    public WaveFormat WaveFormat { get; } = format;

    public int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
}

/// <summary>
/// Channel up/down-mix with no other processing: mono is copied to every output channel; N→mono
/// averages; up-mixing copies existing channels and leaves the extra ones silent; down-mixing to two or
/// more channels averages input channel i into output channel i mod N.
/// </summary>
internal sealed class ChannelAdapterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly float[] _foldScale;
    private float[] _sourceBuffer = [];

    public ChannelAdapterSampleProvider(ISampleProvider source, int outChannels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outChannels);
        _source = source;
        _inChannels = source.WaveFormat.Channels;
        _outChannels = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);

        _foldScale = new float[outChannels];
        for (int c = 0; c < outChannels; c++)
        {
            int contributors = 0;
            for (int i = c; i < _inChannels; i += outChannels)
            {
                contributors++;
            }

            _foldScale[c] = contributors > 0 ? 1f / contributors : 0f;
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outChannels;
        int needed = frames * _inChannels;
        if (_sourceBuffer.Length < needed)
        {
            _sourceBuffer = new float[needed];
        }

        int framesRead = _source.Read(_sourceBuffer, 0, needed) / _inChannels;
        var src = _sourceBuffer;
        int o = offset;

        if (_inChannels == 1)
        {
            for (int f = 0; f < framesRead; f++)
            {
                float v = src[f];
                for (int c = 0; c < _outChannels; c++)
                {
                    buffer[o++] = v;
                }
            }
        }
        else if (_outChannels == 1)
        {
            float scale = 1f / _inChannels;
            for (int f = 0, s = 0; f < framesRead; f++)
            {
                float sum = 0f;
                for (int c = 0; c < _inChannels; c++)
                {
                    sum += src[s++];
                }

                buffer[o++] = sum * scale;
            }
        }
        else if (_inChannels < _outChannels)
        {
            for (int f = 0, s = 0; f < framesRead; f++)
            {
                for (int c = 0; c < _inChannels; c++)
                {
                    buffer[o++] = src[s++];
                }

                for (int c = _inChannels; c < _outChannels; c++)
                {
                    buffer[o++] = 0f;
                }
            }
        }
        else
        {
            for (int f = 0, s = 0; f < framesRead; f++, s += _inChannels)
            {
                for (int c = 0; c < _outChannels; c++)
                {
                    float sum = 0f;
                    for (int i = c; i < _inChannels; i += _outChannels)
                    {
                        sum += src[s + i];
                    }

                    buffer[o + c] = sum * _foldScale[c];
                }

                o += _outChannels;
            }
        }

        return framesRead * _outChannels;
    }
}

/// <summary>Runs an <see cref="FxChain"/> (EQ + effects) over a soundboard bus or the System EQ route (never the microphone).</summary>
internal sealed class FxSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly FxChain _fx;

    public FxSampleProvider(ISampleProvider source, FxChain fx)
    {
        if (source.WaveFormat.SampleRate != fx.SampleRate || source.WaveFormat.Channels != fx.Channels)
        {
            throw new ArgumentException("The effects chain format does not match the bus format.", nameof(fx));
        }

        _source = source;
        _fx = fx;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            _fx.Process(buffer, offset, read);
        }

        return read;
    }
}

/// <summary>
/// Wraps one playing sound. Detects the end of the source (a short read), releases the underlying
/// reader, and reports the end exactly once. A mixer removes inputs that return short reads, so an
/// ended wrapper is dropped from the bus automatically.
/// </summary>
internal sealed class EndTrackingSampleProvider : ISampleProvider
{
    private readonly object _gate = new();
    private ISampleProvider? _source;
    private IDisposable? _owned;
    private Action? _onEnded;
    private int _ended;

    public EndTrackingSampleProvider(ISampleProvider source, IDisposable? owned, Action onEnded)
    {
        _source = source;
        _owned = owned;
        _onEnded = onEnded;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public bool HasEnded => Volatile.Read(ref _ended) != 0;

    public int Read(float[] buffer, int offset, int count)
    {
        int read;
        bool endedNow = false;
        lock (_gate)
        {
            var source = _source;
            if (source is null)
            {
                return 0;
            }

            try
            {
                read = source.Read(buffer, offset, count);
            }
            catch (Exception)
            {
                // A broken reader ends this one sound; it must never break the audio stream.
                read = 0;
            }

            if (read < count)
            {
                endedNow = ReleaseLocked();
            }
        }

        if (endedNow)
        {
            RaiseEnded();
        }

        return Math.Max(read, 0);
    }

    /// <summary>Stops this sound (idempotent) and reports the end.</summary>
    public void End()
    {
        bool endedNow;
        lock (_gate)
        {
            endedNow = ReleaseLocked();
        }

        if (endedNow)
        {
            RaiseEnded();
        }
    }

    /// <summary>Releases the reader without reporting an end (used before the sound was ever started).</summary>
    public void Discard()
    {
        lock (_gate)
        {
            _onEnded = null;
            ReleaseLocked();
        }
    }

    private bool ReleaseLocked()
    {
        if (_source is null)
        {
            return false;
        }

        _source = null;
        try
        {
            _owned?.Dispose();
        }
        catch (Exception)
        {
            // Disposing an in-memory reader cannot meaningfully fail; ignore.
        }

        _owned = null;
        Volatile.Write(ref _ended, 1);
        return true;
    }

    private void RaiseEnded()
    {
        var callback = Interlocked.Exchange(ref _onEnded, null);
        try
        {
            callback?.Invoke();
        }
        catch (Exception)
        {
            // Bookkeeping callbacks must never throw into the audio thread.
        }
    }
}
