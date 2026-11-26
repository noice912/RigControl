using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RigControl.Capture.Listen;

/// <summary>What the listen module needs from Windows' sound system. Replaceable in tests.</summary>
public interface IListenEngine
{
    /// <summary>Sound inputs that are switched on and connected.</summary>
    IReadOnlyList<AudioEndpoint> Inputs();

    /// <summary>Sound outputs that are switched on and connected.</summary>
    IReadOnlyList<AudioEndpoint> Outputs();

    /// <summary>Windows' default output for media, or null when there is none.</summary>
    string? DefaultOutputId();

    /// <summary>Starts playing <paramref name="input"/> on <paramref name="output"/>. Throws when it cannot.</summary>
    IListenSession Start(AudioEndpoint input, AudioEndpoint output, float gain, int latencyMs);
}

/// <summary>One running input-to-output path.</summary>
public interface IListenSession : IDisposable
{
    /// <summary>The boost as a factor (5 = 500 %); can be changed while it runs.</summary>
    float Gain { get; set; }

    /// <summary>True once the input or the output stopped by itself (unplugged, device changed, driver error).</summary>
    bool Faulted { get; }

    /// <summary>Why it stopped, when <see cref="Faulted"/>.</summary>
    string? Problem { get; }

    /// <summary>The loudest sample (after boost and limiter, 0 to 1) since the last call.</summary>
    float TakePeak();
}

/// <summary>
/// The real path: WASAPI capture (shared, event-driven) -> float -> boost + limiter -> bounded queue -> resampler when
/// the rates differ -> channel map -> WASAPI output (shared, low latency). The sound only ever lives in these memory
/// buffers; nothing is written to disk or sent anywhere.
/// </summary>
public sealed class WasapiListenEngine : IListenEngine
{
    public IReadOnlyList<AudioEndpoint> Inputs() => List(DataFlow.Capture);

    public IReadOnlyList<AudioEndpoint> Outputs() => List(DataFlow.Render);

    public string? DefaultOutputId()
    {
        using var e = new MMDeviceEnumerator();
        if (!e.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return d.ID;
    }

    private static IReadOnlyList<AudioEndpoint> List(DataFlow flow)
    {
        var list = new List<AudioEndpoint>();
        using var e = new MMDeviceEnumerator();
        foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (d)
            {
                string name;
                try { name = d.FriendlyName; }
                catch { name = "Sound device"; }
                list.Add(new AudioEndpoint(d.ID, name, HardwareIds(d)));
            }
        }
        return list;
    }

    /// <summary>
    /// Every text property of the endpoint that names a USB id ("VID_..."). Which property holds the parent device's
    /// hardware path differs between Windows versions, so all of them are looked at.
    /// </summary>
    private static string HardwareIds(MMDevice d)
    {
        var found = new List<string>();
        try
        {
            var props = d.Properties;
            for (var i = 0; i < props.Count; i++)
            {
                try
                {
                    if (props.GetValue(i).Value is string s && s.Contains("VID_", StringComparison.OrdinalIgnoreCase)) found.Add(s);
                }
                catch
                {
                    // A property of a type NAudio cannot read: not a text one.
                }
            }
        }
        catch
        {
            // No properties readable: the name is still used.
        }
        return string.Join("|", found);
    }

    public IListenSession Start(AudioEndpoint input, AudioEndpoint output, float gain, int latencyMs)
    {
        using var e = new MMDeviceEnumerator();
        MMDevice? i = null, o = null;
        try
        {
            i = e.GetDevice(input.Id);
            o = e.GetDevice(output.Id);
            var s = new WasapiListenSession(i, o, gain, latencyMs);
            return s;
        }
        catch (Exception ex)
        {
            i?.Dispose();
            o?.Dispose();
            throw new InvalidOperationException(Explain(ex), ex);
        }
    }

    internal static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException or COMException { HResult: unchecked((int)0x80070005) } =>
            "Windows is not letting RigControl use sound inputs. Turn on Settings > Privacy & security > Microphone > \"Let desktop apps access your microphone\".",
        COMException { HResult: unchecked((int)0x88890004) } => "The device was unplugged or changed.", // AUDCLNT_E_DEVICE_INVALIDATED
        COMException { HResult: unchecked((int)0x8889000A) } => "Another program is using the device on its own (exclusive mode).", // AUDCLNT_E_DEVICE_IN_USE
        InvalidOperationException { InnerException: null } => ex.Message,
        _ => "The sound device could not be opened: " + ex.Message,
    };
}

internal sealed class WasapiListenSession : IListenSession
{
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");

    private enum Kind { Float32, Pcm16, Pcm24, Pcm32 }

    private readonly MMDevice _inputDevice;
    private readonly MMDevice _outputDevice;
    private readonly WasapiCapture _capture;
    private readonly WasapiOut _out;
    private readonly GainLimiter _limiter;
    private readonly BoundedSampleBuffer _buffer;
    private readonly Kind _kind;
    private readonly object _peakGate = new();
    private float[] _scratch = Array.Empty<float>();
    private float _peak;
    private volatile bool _stopping;
    private volatile string? _problem;

    public WasapiListenSession(MMDevice input, MMDevice output, float gain, int latencyMs)
    {
        _inputDevice = input;
        _outputDevice = output;
        _capture = new WasapiCapture(input, true, Math.Max(10, latencyMs / 2)) { ShareMode = AudioClientShareMode.Shared };
        var cf = _capture.WaveFormat;
        _kind = KindOf(cf) ?? throw new InvalidOperationException($"The input's sound format ({cf.Encoding}, {cf.BitsPerSample}-bit) can't be read.");
        var inRate = cf.SampleRate;
        var inChannels = Math.Max(1, cf.Channels);
        _limiter = new GainLimiter(inRate, inChannels) { Gain = gain };
        _buffer = new BoundedSampleBuffer(inChannels, BoundedSampleBuffer.MaxFramesFor(inRate, latencyMs));

        WaveFormat mix;
        using (var client = output.AudioClient) mix = client.MixFormat;
        var outChannels = Math.Max(1, mix.Channels);
        // Shared mode always takes its own mix format; when that is not 32-bit float (very rare) ask for plain float.
        var outFormat = IsFloat(mix) ? mix : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, outChannels);

        ISampleProvider source = new QueueProvider(_buffer, inRate, inChannels);
        if (inRate != mix.SampleRate) source = new WdlResamplingSampleProvider(source, mix.SampleRate);
        _out = new WasapiOut(output, AudioClientShareMode.Shared, true, Math.Max(10, latencyMs));
        _out.Init(new FloatWaveProvider(source, outFormat));

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, a) => Stopped(a.Exception, "The input stopped.");
        _out.PlaybackStopped += (_, a) => Stopped(a.Exception, "The output stopped.");
        try
        {
            _capture.StartRecording();
            _out.Play();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public float Gain
    {
        get => _limiter.Gain;
        set => _limiter.Gain = value;
    }

    public bool Faulted => _problem is not null;
    public string? Problem => _problem;

    public float TakePeak()
    {
        lock (_peakGate)
        {
            var p = _peak;
            _peak = 0;
            return p;
        }
    }

    private void Stopped(Exception? ex, string fallback)
    {
        if (_stopping) return;
        _problem = ex is null ? fallback : WasapiListenEngine.Explain(ex);
    }

    private static bool IsFloat(WaveFormat f)
        => f.BitsPerSample == 32 && (f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible x && x.SubFormat == FloatSubtype));

    private static Kind? KindOf(WaveFormat f)
    {
        if (IsFloat(f)) return Kind.Float32;
        var pcm = f.Encoding == WaveFormatEncoding.Pcm || (f is WaveFormatExtensible x && x.SubFormat == PcmSubtype);
        if (!pcm) return null;
        return f.BitsPerSample switch { 16 => Kind.Pcm16, 24 => Kind.Pcm24, 32 => Kind.Pcm32, _ => null };
    }

    private void OnData(object? sender, WaveInEventArgs a)
    {
        if (_stopping || a.BytesRecorded <= 0) return;
        var bytes = a.Buffer.AsSpan(0, a.BytesRecorded);
        var count = _kind switch
        {
            Kind.Float32 => bytes.Length / 4,
            Kind.Pcm16 => bytes.Length / 2,
            Kind.Pcm24 => bytes.Length / 3,
            _ => bytes.Length / 4,
        };
        if (_scratch.Length < count) _scratch = new float[count];
        var s = _scratch.AsSpan(0, count);
        switch (_kind)
        {
            case Kind.Float32:
                MemoryMarshal.Cast<byte, float>(bytes[..(count * 4)]).CopyTo(s);
                break;
            case Kind.Pcm16:
                var p16 = MemoryMarshal.Cast<byte, short>(bytes[..(count * 2)]);
                for (var i = 0; i < count; i++) s[i] = p16[i] / 32768f;
                break;
            case Kind.Pcm24:
                for (var i = 0; i < count; i++)
                {
                    var v = bytes[3 * i] | (bytes[3 * i + 1] << 8) | ((sbyte)bytes[3 * i + 2] << 16);
                    s[i] = v / 8388608f;
                }
                break;
            default:
                var p32 = MemoryMarshal.Cast<byte, int>(bytes[..(count * 4)]);
                for (var i = 0; i < count; i++) s[i] = p32[i] / 2147483648f;
                break;
        }
        var loudest = _limiter.Process(s);
        _buffer.Write(s);
        lock (_peakGate) if (loudest > _peak) _peak = loudest;
    }

    public void Dispose()
    {
        _stopping = true;
        try { _capture.StopRecording(); } catch { /* ignore */ }
        try { _out.Stop(); } catch { /* ignore */ }
        try { _capture.Dispose(); } catch { /* ignore */ }
        try { _out.Dispose(); } catch { /* ignore */ }
        try { _inputDevice.Dispose(); } catch { /* ignore */ }
        try { _outputDevice.Dispose(); } catch { /* ignore */ }
        _buffer.Clear();
    }

    /// <summary>The queue as a float source at the input's rate and channel count. Never runs dry: silence instead.</summary>
    private sealed class QueueProvider : ISampleProvider
    {
        private readonly BoundedSampleBuffer _buffer;

        public QueueProvider(BoundedSampleBuffer buffer, int rate, int channels)
        {
            _buffer = buffer;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            _buffer.Read(buffer.AsSpan(offset, count));
            return count;
        }
    }

    /// <summary>Float samples to the output's own format (the device mix format), mapping channels and clamping to +-1.</summary>
    private sealed class FloatWaveProvider : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _inChannels;
        private readonly int _outChannels;
        private float[] _in = Array.Empty<float>();

        public FloatWaveProvider(ISampleProvider source, WaveFormat format)
        {
            _source = source;
            _inChannels = source.WaveFormat.Channels;
            _outChannels = format.Channels;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / (4 * _outChannels);
            var need = frames * _inChannels;
            if (_in.Length < need) _in = new float[need];
            var got = 0;
            while (got < need)
            {
                var n = _source.Read(_in, got, need - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < need) Array.Clear(_in, got, need - got);
            var target = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * _outChannels * 4));
            ChannelMap.Map(_in.AsSpan(0, need), _inChannels, target, _outChannels, frames);
            for (var i = 0; i < target.Length; i++) target[i] = Math.Clamp(target[i], -1f, 1f);
            return frames * _outChannels * 4;
        }
    }
}
