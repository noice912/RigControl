namespace AudioController.Core.AudioMath;

/// <summary>What the drift controller wants done before the next block is read.</summary>
public enum DriftAction
{
    /// <summary>Nothing: the buffer is close enough to its setpoint.</summary>
    None,

    /// <summary>Skip one frame (the buffer is filling up: the source clock runs faster than the output's).</summary>
    DropFrame,

    /// <summary>Repeat one frame (the buffer is draining: the source clock runs slower than the output's).</summary>
    DuplicateFrame,

    /// <summary>
    /// Discard <see cref="DriftDecision.Frames"/> frames at once: the buffer is far above its setpoint (a stall followed
    /// by a burst), so latency is cut straight back instead of drifting down one frame at a time.
    /// </summary>
    Trim,
}

/// <summary>One drift-correction decision: an action and how many frames it affects (1 for drop/duplicate).</summary>
public readonly record struct DriftDecision(DriftAction Action, int Frames)
{
    /// <summary>No correction.</summary>
    public static DriftDecision None { get; } = new(DriftAction.None, 0);

    /// <summary>Skip one frame.</summary>
    public static DriftDecision Drop { get; } = new(DriftAction.DropFrame, 1);

    /// <summary>Repeat one frame.</summary>
    public static DriftDecision Duplicate { get; } = new(DriftAction.DuplicateFrame, 1);
}

/// <summary>
/// The pure drift-correction rule for "Also play on": the copy is captured on the headset's clock and played on another
/// device's clock, so its buffer slowly fills or drains. Tiny corrections (one frame per block) keep it near its
/// setpoint, so a session of many hours neither overflows nor underruns and latency never grows.
/// </summary>
public static class DriftCorrection
{
    /// <summary>
    /// Decides the correction for one block (deterministic, no state of its own).
    /// </summary>
    /// <param name="smoothedFillFrames">The buffer fill averaged over recent blocks (the raw fill jitters by a packet).</param>
    /// <param name="fillFrames">The fill right now.</param>
    /// <param name="setpointFrames">Where the fill should sit.</param>
    /// <param name="toleranceFrames">How far the average may wander before a correction starts.</param>
    /// <param name="hardLimitFrames">How far above the setpoint the raw fill may ever get before it is trimmed at once.</param>
    /// <param name="ongoing">The correction running in the previous block: it continues (one frame per block) until the
    /// average crosses the setpoint, then stops (hysteresis, so corrections come in short runs rather than flicker).</param>
    public static DriftDecision Decide(
        double smoothedFillFrames, int fillFrames, int setpointFrames, int toleranceFrames, int hardLimitFrames, DriftAction ongoing)
    {
        if (fillFrames - setpointFrames > Math.Max(0, hardLimitFrames))
        {
            return new DriftDecision(DriftAction.Trim, fillFrames - setpointFrames);
        }

        double error = smoothedFillFrames - setpointFrames;
        if (ongoing == DriftAction.DropFrame)
        {
            return error > 0 ? DriftDecision.Drop : DriftDecision.None;
        }

        if (ongoing == DriftAction.DuplicateFrame)
        {
            return error < 0 ? DriftDecision.Duplicate : DriftDecision.None;
        }

        if (error > toleranceFrames)
        {
            return DriftDecision.Drop;
        }

        if (error < -toleranceFrames)
        {
            return DriftDecision.Duplicate;
        }

        return DriftDecision.None;
    }
}

/// <summary>
/// Applies <see cref="DriftCorrection.Decide"/> block after block: keeps an exponential average of the fill (so a
/// packet-sized sawtooth does not trigger corrections) and the running correction. Deterministic.
/// </summary>
public sealed class DriftController
{
    /// <summary>Creates a controller.</summary>
    /// <param name="setpointFrames">Where the fill should sit.</param>
    /// <param name="toleranceFrames">Dead band around the setpoint.</param>
    /// <param name="hardLimitFrames">Raw excess above the setpoint that is trimmed at once.</param>
    /// <param name="smoothing">Weight of each new fill in the average (0 &lt; s ≤ 1).</param>
    public DriftController(int setpointFrames, int toleranceFrames, int hardLimitFrames, double smoothing = 0.05)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(setpointFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(toleranceFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(hardLimitFrames);
        if (!(smoothing > 0 && smoothing <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(smoothing), "Smoothing must be in (0, 1].");
        }

        SetpointFrames = setpointFrames;
        ToleranceFrames = toleranceFrames;
        HardLimitFrames = hardLimitFrames;
        Smoothing = smoothing;
        SmoothedFill = setpointFrames;
    }

    /// <summary>Where the fill should sit.</summary>
    public int SetpointFrames { get; }

    /// <summary>Dead band around the setpoint.</summary>
    public int ToleranceFrames { get; }

    /// <summary>Raw excess above the setpoint that is trimmed at once.</summary>
    public int HardLimitFrames { get; }

    /// <summary>Weight of each new fill in the average.</summary>
    public double Smoothing { get; }

    /// <summary>The averaged fill.</summary>
    public double SmoothedFill { get; private set; }

    /// <summary>The correction that ran in the last block (None, DropFrame or DuplicateFrame).</summary>
    public DriftAction Ongoing { get; private set; }

    /// <summary>Starts over from <paramref name="fillFrames"/> (after priming), with no correction running.</summary>
    public void Reset(int fillFrames)
    {
        SmoothedFill = fillFrames;
        Ongoing = DriftAction.None;
    }

    /// <summary>Feeds the fill before a block is read and returns the correction to apply to that block.</summary>
    public DriftDecision Update(int fillFrames)
    {
        SmoothedFill += Smoothing * (fillFrames - SmoothedFill);
        var decision = DriftCorrection.Decide(SmoothedFill, fillFrames, SetpointFrames, ToleranceFrames, HardLimitFrames, Ongoing);
        switch (decision.Action)
        {
            case DriftAction.Trim:
                // Latency is back at the setpoint now; the average starts again from there.
                SmoothedFill = SetpointFrames;
                Ongoing = DriftAction.None;
                break;
            case DriftAction.DropFrame:
            case DriftAction.DuplicateFrame:
                Ongoing = decision.Action;
                break;
            default:
                Ongoing = DriftAction.None;
                break;
        }

        return decision;
    }
}

/// <summary>Sizes of a <see cref="MirrorBuffer"/>, in frames.</summary>
/// <param name="SetpointFrames">Jitter cushion the buffer is kept at (and primed to).</param>
/// <param name="ToleranceFrames">Dead band of the drift correction.</param>
/// <param name="HardLimitFrames">Excess above the setpoint that is trimmed at once.</param>
/// <param name="CapacityFrames">Most frames ever held (older frames are discarded beyond it).</param>
/// <param name="FadeFrames">Fade length at an underrun (out) and after priming (in), so gaps never click.</param>
public sealed record MirrorBufferOptions(int SetpointFrames, int ToleranceFrames, int HardLimitFrames, int CapacityFrames, int FadeFrames)
{
    /// <summary>Options in milliseconds at <paramref name="sampleRate"/>.</summary>
    public static MirrorBufferOptions ForMilliseconds(
        int sampleRate, double setpointMs, double toleranceMs = 10, double hardLimitMs = 150, double capacityMs = 1000, double fadeMs = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        int F(double ms) => (int)Math.Round(ms * sampleRate / 1000.0, MidpointRounding.AwayFromZero);
        int setpoint = Math.Max(1, F(setpointMs));
        return new MirrorBufferOptions(setpoint, F(toleranceMs), F(hardLimitMs), Math.Max(setpoint + F(hardLimitMs) + 1, F(capacityMs)), F(fadeMs));
    }
}

/// <summary>Counters of one <see cref="MirrorBuffer"/> (diagnostics).</summary>
public readonly record struct MirrorBufferStats(long DroppedFrames, long DuplicatedFrames, long TrimmedFrames, long OverflowFrames, long Underruns);

/// <summary>
/// The per-target buffer of "Also play on": the capture thread writes the headset's audio, the target's render thread
/// reads it. It primes to a small cushion before playing, pads silence whenever the source delivers nothing (Windows'
/// loopback sends no packets while nothing plays), and keeps the fill near the cushion with
/// <see cref="DriftController"/> so latency stays constant for hours. Underruns fade out and priming fades in, so
/// gaps never click. Thread-safe (one lock); pure, no I/O.
/// </summary>
public sealed class MirrorBuffer
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private readonly float[] _last;
    private readonly DriftController _drift;
    private int _head;
    private int _count;
    private bool _priming = true;
    private bool _hasLast;
    private int _fadeInRemaining;
    private long _dropped;
    private long _duplicated;
    private long _trimmed;
    private long _overflow;
    private long _underruns;

    /// <summary>Creates an empty (priming) buffer.</summary>
    public MirrorBuffer(int channels, int sampleRate, MirrorBufferOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SetpointFrames, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CapacityFrames, options.SetpointFrames, nameof(options));
        Channels = channels;
        SampleRate = sampleRate;
        Options = options;
        _capacityFrames = options.CapacityFrames;
        _ring = new float[checked(_capacityFrames * channels)];
        _last = new float[channels];
        _drift = new DriftController(options.SetpointFrames, options.ToleranceFrames, options.HardLimitFrames);
    }

    /// <summary>Interleaved channel count.</summary>
    public int Channels { get; }

    /// <summary>Sample rate (for converting frames to time).</summary>
    public int SampleRate { get; }

    /// <summary>The sizes in use.</summary>
    public MirrorBufferOptions Options { get; }

    /// <summary>Frames waiting to be read.</summary>
    public int BufferedFrames
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>True while the buffer outputs silence waiting for the cushion to fill (at the start and after an underrun).</summary>
    public bool IsPriming
    {
        get
        {
            lock (_gate)
            {
                return _priming;
            }
        }
    }

    /// <summary>The counters so far.</summary>
    public MirrorBufferStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new MirrorBufferStats(_dropped, _duplicated, _trimmed, _overflow, _underruns);
            }
        }
    }

    /// <summary>Appends interleaved samples (whole frames; a trailing partial frame is ignored). Beyond the capacity the oldest frames are dropped.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        int channels = Channels;
        int frames = samples.Length / channels;
        if (frames == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (frames > _capacityFrames)
            {
                int skip = frames - _capacityFrames;
                _overflow += skip;
                samples = samples[(skip * channels)..];
                frames = _capacityFrames;
            }

            int excess = _count + frames - _capacityFrames;
            if (excess > 0)
            {
                DiscardLocked(excess);
                _overflow += excess;
            }

            int tail = (_head + _count) % _capacityFrames;
            int first = Math.Min(frames, _capacityFrames - tail);
            samples[..(first * channels)].CopyTo(_ring.AsSpan(tail * channels));
            if (first < frames)
            {
                samples[(first * channels)..(frames * channels)].CopyTo(_ring);
            }

            _count += frames;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> completely: buffered audio (with this block's drift correction), or silence
    /// while priming / after an underrun. A trailing partial frame is zeroed.
    /// </summary>
    public void Read(Span<float> destination)
    {
        int channels = Channels;
        int frames = destination.Length / channels;
        destination[(frames * channels)..].Clear();
        if (frames == 0)
        {
            return;
        }

        lock (_gate)
        {
            var options = Options;
            if (_priming)
            {
                if (_count < Math.Max(options.SetpointFrames, frames))
                {
                    destination[..(frames * channels)].Clear();
                    return;
                }

                _priming = false;
                _drift.Reset(_count);
                _fadeInRemaining = options.FadeFrames;
            }
            else
            {
                var decision = _drift.Update(_count);
                switch (decision.Action)
                {
                    case DriftAction.Trim:
                        int trim = Math.Min(decision.Frames, _count);
                        DiscardLocked(trim);
                        _trimmed += trim;
                        break;
                    case DriftAction.DropFrame when _count > frames:
                        DiscardLocked(1);
                        _dropped++;
                        break;
                    case DriftAction.DuplicateFrame when _hasLast:
                        _last.CopyTo(destination);
                        _duplicated++;
                        ReadFramesLocked(destination, 1, frames);
                        return;
                }
            }

            ReadFramesLocked(destination, 0, frames);
        }
    }

    /// <summary>Drops everything and primes again (e.g. when the source restarts).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
            _priming = true;
            _hasLast = false;
            _fadeInRemaining = 0;
        }
    }

    /// <summary>Copies frames [start, frames) of the block from the ring; pads, fades and re-primes on an underrun.</summary>
    private void ReadFramesLocked(Span<float> destination, int start, int frames)
    {
        int channels = Channels;
        int need = frames - start;
        int take = Math.Min(need, _count);
        int first = Math.Min(take, _capacityFrames - _head);
        _ring.AsSpan(_head * channels, first * channels).CopyTo(destination[(start * channels)..]);
        if (first < take)
        {
            _ring.AsSpan(0, (take - first) * channels).CopyTo(destination[((start + first) * channels)..]);
        }

        _head = (_head + take) % _capacityFrames;
        _count -= take;
        int real = start + take;

        if (_fadeInRemaining > 0 && real > 0)
        {
            int fade = Options.FadeFrames;
            int done = fade - _fadeInRemaining;
            int n = Math.Min(_fadeInRemaining, real);
            for (int f = 0; f < n; f++)
            {
                float g = (float)(done + f + 1) / (fade + 1);
                for (int c = 0; c < channels; c++)
                {
                    destination[f * channels + c] *= g;
                }
            }

            _fadeInRemaining -= n;
        }

        if (take < need)
        {
            // Underrun: the source went quiet (or stalled). Fade the last real frames out so the gap never clicks, pad
            // silence, and prime again so the next audio starts at the same latency.
            int fade = Math.Min(Options.FadeFrames, real);
            for (int f = 0; f < fade; f++)
            {
                float g = (float)(fade - f) / (fade + 1);
                int frame = real - fade + f;
                for (int c = 0; c < channels; c++)
                {
                    destination[frame * channels + c] *= g;
                }
            }

            destination[(real * channels)..(frames * channels)].Clear();
            _priming = true;
            _hasLast = false;
            _fadeInRemaining = 0;
            _underruns++;
            return;
        }

        destination.Slice((frames - 1) * channels, channels).CopyTo(_last);
        _hasLast = true;
    }

    private void DiscardLocked(int frames)
    {
        frames = Math.Min(frames, _count);
        _head = (_head + frames) % _capacityFrames;
        _count -= frames;
    }
}
