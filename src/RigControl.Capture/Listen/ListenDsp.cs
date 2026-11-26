namespace RigControl.Capture.Listen;

/// <summary>
/// Gain followed by a look-ahead peak limiter, for interleaved float samples. Boosting a quiet source (a capture card)
/// by up to 6x would clip harshly on loud parts; this turns the volume down just before a peak arrives and lets it
/// back up slowly, so nothing ever goes above <see cref="Ceiling"/> and the changes are smooth.
///
/// How it works (per frame, all channels share one gain so the stereo picture stays put): the gain each frame needs
/// (ceiling / peak, at most 1) goes through a minimum over the last N frames, then a slow release, then a moving
/// average over N frames. The sound itself is delayed by N - 1 frames, so every value in that average already covers
/// the frame being played: the gain applied is never more than that frame needs. N is about 1.5 ms.
/// Pure arithmetic, one instance per stream; not thread-safe.
/// </summary>
public sealed class GainLimiter
{
    /// <summary>The loudest any sample can come out: about -0.2 dB below full scale.</summary>
    public const float Ceiling = 0.98f;

    private readonly int _channels;
    private readonly int _window;
    private readonly float _release;
    // Ring of the last _window frames of input (after gain), played _window - 1 frames late.
    private readonly float[] _delay;
    // Ring of the needed gain per frame, for the minimum.
    private readonly float[] _need;
    // Ring of the released held gain, for the moving average.
    private readonly float[] _held;
    private int _pos;
    private double _sum;
    private float _released = 1f;
    private float _gain;

    public GainLimiter(int sampleRate, int channels, float lookAheadMs = 1.5f, float releaseMs = 150f)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _window = Math.Max(2, (int)MathF.Round(sampleRate * lookAheadMs / 1000f));
        _release = 1f - MathF.Exp(-1f / (sampleRate * releaseMs / 1000f));
        _delay = new float[_window * channels];
        _need = new float[_window];
        _held = new float[_window];
        Array.Fill(_need, 1f);
        Array.Fill(_held, 1f);
        _sum = _window;
        Gain = 1f;
        AppliedGain = 1f;
    }

    /// <summary>The boost as a factor (5 = 500 %). Changes take effect from the next sample.</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 16f);
    }

    /// <summary>How many frames the sound comes out later than it went in.</summary>
    public int DelayFrames => _window - 1;

    /// <summary>The limiter's own gain for the newest frame (1 = no limiting). For tests and meters.</summary>
    public float AppliedGain { get; private set; }

    /// <summary>
    /// Boosts and limits <paramref name="samples"/> (interleaved, whole frames) in place. Returns the loudest absolute
    /// sample that came out, 0 to <see cref="Ceiling"/>.
    /// </summary>
    public float Process(Span<float> samples)
    {
        var frames = samples.Length / _channels;
        float loudest = 0f;
        for (var f = 0; f < frames; f++)
        {
            var o = f * _channels;
            // The frame coming in, boosted, and the gain it needs.
            float peak = 0f;
            for (var c = 0; c < _channels; c++)
            {
                var x = samples[o + c] * _gain;
                if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
                samples[o + c] = x;
                var a = MathF.Abs(x);
                if (a > peak) peak = a;
            }
            var need = peak > Ceiling ? Ceiling / peak : 1f;

            // Slot _pos holds the oldest frame: that is the one that goes out now (delay = window - 1 once the new one is in).
            var slot = _pos;
            _need[slot] = need;
            var held = 1f;
            for (var i = 0; i < _window; i++) if (_need[i] < held) held = _need[i];
            // Release: drop at once, rise slowly. Never above the held value, so the guarantee stays.
            _released = held < _released ? held : _released + (held - _released) * _release;
            _sum += _released - _held[slot];
            _held[slot] = _released;
            var g = (float)(_sum / _window);
            if (g > 1f) g = 1f;
            AppliedGain = g;

            // Swap the new frame into the delay line and take out the one from window - 1 frames ago.
            var next = (slot + 1) % _window;
            var d = slot * _channels;
            var outFrom = next * _channels;
            for (var c = 0; c < _channels; c++) _delay[d + c] = samples[o + c];
            for (var c = 0; c < _channels; c++)
            {
                var y = _delay[outFrom + c] * g;
                // The average can be off by rounding; never let that through.
                if (y > Ceiling) y = Ceiling;
                else if (y < -Ceiling) y = -Ceiling;
                samples[o + c] = y;
                var a = MathF.Abs(y);
                if (a > loudest) loudest = a;
            }
            _pos = next;
        }
        return loudest;
    }
}

/// <summary>
/// A bounded queue of interleaved float samples between the capture thread and the playback thread. When more than
/// <see cref="MaxFrames"/> are waiting (the input runs a little faster than the output, or the output stalled), the
/// oldest are dropped, so the delay never creeps up. Reading when empty gives silence. Thread-safe.
/// </summary>
public sealed class BoundedSampleBuffer
{
    private readonly object _gate = new();
    private readonly int _channels;
    private readonly float[] _ring;
    private int _start;
    private int _count; // samples, always whole frames

    public BoundedSampleBuffer(int channels, int maxFrames)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        _channels = channels;
        MaxFrames = maxFrames;
        _ring = new float[maxFrames * channels];
    }

    public int Channels => _channels;
    public int MaxFrames { get; }

    /// <summary>Frames waiting to be played.</summary>
    public int Frames
    {
        get { lock (_gate) return _count / _channels; }
    }

    /// <summary>Frames thrown away so far because the queue was full.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Frames of silence handed out so far because the queue was empty.</summary>
    public long MissingFrames { get; private set; }

    /// <summary>The buffer size for a latency: twice the latency's worth of frames (at least 10 ms).</summary>
    public static int MaxFramesFor(int sampleRate, int latencyMs) => Math.Max(sampleRate / 100, sampleRate * Math.Max(1, latencyMs) * 2 / 1000);

    /// <summary>Adds samples (whole frames; a partial frame at the end is ignored), dropping the oldest when full.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var n = samples.Length - samples.Length % _channels;
        if (n <= 0) return;
        lock (_gate)
        {
            if (n >= _ring.Length)
            {
                // More than fits at once: keep only the newest.
                DroppedFrames += (_count + n - _ring.Length) / _channels;
                samples.Slice(n - _ring.Length, _ring.Length).CopyTo(_ring);
                _start = 0;
                _count = _ring.Length;
                return;
            }
            var over = _count + n - _ring.Length;
            if (over > 0)
            {
                _start = (_start + over) % _ring.Length;
                _count -= over;
                DroppedFrames += over / _channels;
            }
            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(n, _ring.Length - end);
            samples[..first].CopyTo(_ring.AsSpan(end, first));
            if (first < n) samples[first..n].CopyTo(_ring.AsSpan(0, n - first));
            _count += n;
        }
    }

    /// <summary>Fills <paramref name="into"/> completely: waiting samples first, silence for the rest.</summary>
    public void Read(Span<float> into)
    {
        lock (_gate)
        {
            var want = into.Length - into.Length % _channels;
            var n = Math.Min(want, _count);
            var first = Math.Min(n, _ring.Length - _start);
            _ring.AsSpan(_start, first).CopyTo(into);
            if (first < n) _ring.AsSpan(0, n - first).CopyTo(into[first..]);
            _start = (_start + n) % _ring.Length;
            _count -= n;
            if (n < into.Length)
            {
                into[n..].Clear();
                MissingFrames += (want - n) / _channels;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }
}

/// <summary>One sound device as Windows lists it.</summary>
/// <param name="Id">Windows' endpoint id.</param>
/// <param name="Name">The name Windows shows, e.g. "Digital Audio Interface (USB Digital Audio)".</param>
/// <param name="HardwareIds">Hardware paths Windows keeps for the device (e.g. "USB\VID_534D&amp;PID_2109..."), joined; may be empty.</param>
/// <param name="FormFactor">Windows' kind of device (EndpointFormFactor: 4 microphone, 5 headset, 6 handset, ...); -1 unknown.</param>
/// <param name="Enabled">False when the device is switched off in Windows' sound settings.</param>
public sealed record AudioEndpoint(string Id, string Name, string HardwareIds = "", int FormFactor = -1, bool Enabled = true)
{
    /// <summary>
    /// Microphones, headsets and phone handsets are never offered: the sound to play comes from a capture card or a line
    /// input, and opening a Bluetooth headset's microphone switches the headset into its low-quality call mode.
    /// </summary>
    public bool IsMicrophoneLike => FormFactor is 4 or 5 or 6;
}

/// <summary>Which sound input is the capture card when the user left the choice on "automatic".</summary>
public static class CaptureCardPicker
{
    /// <summary>MacroSilicon, the maker of the common cheap USB capture cards (MS2109 / MS2130).</summary>
    public const string MacroSiliconVid = "VID_534D";
    public const string UsbDigitalAudio = "USB Digital Audio";

    /// <summary>The capture card's input, or null: first one on MacroSilicon's USB id, then one named "USB Digital Audio".</summary>
    public static AudioEndpoint? Pick(IEnumerable<AudioEndpoint> inputs)
    {
        var all = inputs.Where(d => !d.IsMicrophoneLike).ToList();
        // A switched-on one first; a switched-off one is still found, so the user can be told to turn it on.
        var list = all.Where(d => d.Enabled).Concat(all.Where(d => !d.Enabled)).ToList();
        return list.FirstOrDefault(IsMacroSilicon)
            ?? list.FirstOrDefault(d => d.Name.Contains(UsbDigitalAudio, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCaptureCard(AudioEndpoint d)
        => IsMacroSilicon(d) || d.Name.Contains(UsbDigitalAudio, StringComparison.OrdinalIgnoreCase);

    private static bool IsMacroSilicon(AudioEndpoint d)
        => d.HardwareIds.Contains(MacroSiliconVid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Copies frames from one channel count to another: mono to all, stereo to the first two, more down to fewer.</summary>
public static class ChannelMap
{
    public static void Map(ReadOnlySpan<float> source, int sourceChannels, Span<float> target, int targetChannels, int frames)
    {
        if (sourceChannels == targetChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(target);
            return;
        }
        for (var f = 0; f < frames; f++)
        {
            var s = f * sourceChannels;
            var t = f * targetChannels;
            if (sourceChannels == 1)
            {
                for (var c = 0; c < targetChannels; c++) target[t + c] = source[s];
            }
            else if (targetChannels == 1)
            {
                float sum = 0;
                for (var c = 0; c < sourceChannels; c++) sum += source[s + c];
                target[t] = sum / sourceChannels;
            }
            else
            {
                for (var c = 0; c < targetChannels; c++) target[t + c] = c < sourceChannels ? source[s + c] : 0f;
            }
        }
    }
}
