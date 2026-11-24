namespace AudioController.Core.AudioMath;

/// <summary>
/// An exact whole-frame delay for interleaved float audio ("Also play on": line a copy up with a slower Bluetooth
/// headset). Pure DSP, no I/O.
/// </summary>
/// <remarks>
/// <para>Every input frame is kept in a ring buffer of <see cref="MaxDelayFrames"/> + 1 frames; the output is the frame
/// <see cref="DelayFrames"/> frames back. A delay of 0 returns the input untouched (bit-exact); any other delay returns
/// exactly the input delayed by that many frames (silence before the first input).</para>
/// <para><see cref="SetDelay"/> may be called from any thread while audio runs; the change is picked up at the next
/// <see cref="Process"/> call and, when a crossfade length is set, faded linearly from the old tap to the new one so
/// moving the slider never clicks. <see cref="Process"/> must be called by one thread at a time.</para>
/// </remarks>
public sealed class DelayLine
{
    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private readonly int _crossfadeFrames;
    private int _write;
    private int _delay;
    private int _fromDelay;
    private int _fadeRemaining;
    private volatile int _requested;

    /// <summary>Creates a delay line holding silence.</summary>
    /// <param name="channels">Interleaved channel count.</param>
    /// <param name="maxDelayFrames">The longest delay that can be set.</param>
    /// <param name="crossfadeFrames">Length of the fade when the delay changes (0 = switch instantly).</param>
    /// <param name="delayFrames">The initial delay (clamped to 0..<paramref name="maxDelayFrames"/>).</param>
    public DelayLine(int channels, int maxDelayFrames, int crossfadeFrames = 0, int delayFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelayFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(crossfadeFrames);
        Channels = channels;
        MaxDelayFrames = maxDelayFrames;
        _capacityFrames = maxDelayFrames + 1;
        _ring = new float[checked(_capacityFrames * channels)];
        _crossfadeFrames = crossfadeFrames;
        _delay = Math.Clamp(delayFrames, 0, maxDelayFrames);
        _requested = _delay;
    }

    /// <summary>Interleaved channel count.</summary>
    public int Channels { get; }

    /// <summary>The longest delay that can be set, in frames.</summary>
    public int MaxDelayFrames { get; }

    /// <summary>The delay last set, in frames (applied from the next <see cref="Process"/> call).</summary>
    public int DelayFrames => _requested;

    /// <summary>Frames for a delay in milliseconds at <paramref name="sampleRate"/> (rounded to the nearest frame; ≤ 0 = 0).</summary>
    public static int FramesFor(double milliseconds, int sampleRate) =>
        double.IsFinite(milliseconds) && milliseconds > 0 && sampleRate > 0
            ? (int)Math.Min(int.MaxValue, Math.Round(milliseconds * sampleRate / 1000.0, MidpointRounding.AwayFromZero))
            : 0;

    /// <summary>Sets the delay in frames (clamped to 0..<see cref="MaxDelayFrames"/>). Any thread.</summary>
    public void SetDelay(int frames) => _requested = Math.Clamp(frames, 0, MaxDelayFrames);

    /// <summary>Forgets the history (the next output is silence for the delay's length). Audio thread or while stopped.</summary>
    public void Reset()
    {
        Array.Clear(_ring);
        _write = 0;
        _fadeRemaining = 0;
        _delay = _requested;
    }

    /// <summary>Delays interleaved samples in place. A trailing partial frame is left untouched.</summary>
    public void Process(Span<float> buffer)
    {
        int channels = Channels;
        int frames = buffer.Length / channels;
        if (frames == 0)
        {
            return;
        }

        int requested = _requested;
        if (requested != _delay && _fadeRemaining == 0)
        {
            if (_crossfadeFrames == 0)
            {
                _delay = requested;
            }
            else
            {
                _fromDelay = _delay;
                _delay = requested;
                _fadeRemaining = _crossfadeFrames;
            }
        }

        var ring = _ring;
        int capacity = _capacityFrames;
        int write = _write;
        for (int f = 0, i = 0; f < frames; f++, i += channels)
        {
            int slot = write * channels;
            for (int c = 0; c < channels; c++)
            {
                ring[slot + c] = buffer[i + c];
            }

            if (_fadeRemaining > 0)
            {
                float t = (float)(_crossfadeFrames - _fadeRemaining + 1) / _crossfadeFrames;
                int from = Wrap(write - _fromDelay, capacity) * channels;
                int to = Wrap(write - _delay, capacity) * channels;
                for (int c = 0; c < channels; c++)
                {
                    buffer[i + c] = ring[from + c] * (1f - t) + ring[to + c] * t;
                }

                _fadeRemaining--;
            }
            else if (_delay != 0)
            {
                int tap = Wrap(write - _delay, capacity) * channels;
                for (int c = 0; c < channels; c++)
                {
                    buffer[i + c] = ring[tap + c];
                }
            }

            // Delay 0 and no fade: the input stays in the buffer untouched (bit-exact).
            write = write + 1 == capacity ? 0 : write + 1;
        }

        _write = write;
    }

    private static int Wrap(int index, int capacity) => index < 0 ? index + capacity : index;
}

/// <summary>
/// A linear volume stage (0–1, never a boost) for interleaved float audio. At unity it leaves the samples untouched
/// (bit-exact); a change is ramped across one block so moving the slider never clicks. Pure DSP, no I/O.
/// </summary>
/// <remarks><see cref="Gain"/> may be set from any thread; <see cref="Process"/> must be called by one thread at a time.</remarks>
public sealed class GainStage
{
    private int _targetBits;
    private float _current;

    /// <summary>Creates a stage at <paramref name="gain"/> (clamped to 0..1).</summary>
    public GainStage(int channels, float gain = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        Channels = channels;
        float g = Clamp(gain);
        _current = g;
        _targetBits = BitConverter.SingleToInt32Bits(g);
    }

    /// <summary>Interleaved channel count.</summary>
    public int Channels { get; }

    /// <summary>The linear gain (0..1; NaN = 0). Any thread.</summary>
    public float Gain
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _targetBits));
        set => Volatile.Write(ref _targetBits, BitConverter.SingleToInt32Bits(Clamp(value)));
    }

    /// <summary>The linear gain for a volume in percent: 0–100 → 0–1 (100 % = unity, 50 % = exactly half).</summary>
    public static float FromPercent(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    /// <summary>Applies the gain in place (whole frames; a trailing partial frame is left untouched).</summary>
    public void Process(Span<float> buffer)
    {
        int channels = Channels;
        int frames = buffer.Length / channels;
        if (frames == 0)
        {
            return;
        }

        int samples = frames * channels;
        float target = Gain;
        float current = _current;
        if (current == target)
        {
            if (target == 1f)
            {
                return; // unity: bit-exact
            }

            if (target == 0f)
            {
                buffer[..samples].Clear();
                return;
            }

            for (int i = 0; i < samples; i++)
            {
                buffer[i] *= target;
            }

            return;
        }

        float step = (target - current) / frames;
        for (int f = 0, i = 0; f < frames; f++, i += channels)
        {
            float g = f == frames - 1 ? target : current + step * (f + 1);
            for (int c = 0; c < channels; c++)
            {
                buffer[i + c] *= g;
            }
        }

        _current = target;
    }

    private static float Clamp(float gain) => float.IsNaN(gain) ? 0f : Math.Clamp(gain, 0f, 1f);
}
