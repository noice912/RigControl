using AudioController.Core.Equalizer;
using NAudio.Dsp;

namespace AudioController.Audio;

/// <summary>
/// The Effects panel's in-app chain for the SOUNDBOARD bus (and the System EQ router): graphic EQ bands → Bass Boost
/// (low shelf) → Fidelity (high shelf + air peak) → Dynamic Boost (loudness-contour shelves) → Surround (mid/side
/// width, stereo only) → Ambience (a light Schroeder reverb: 4 combs + 2 all-passes, RT60 0.35 s, wet up to 0.35).
/// The same <see cref="FxState"/> drives Equalizer APO on the headset, so Discord hears the soundboard the way the user
/// does. Never applied to the microphone path (SPEC 4.3).
/// </summary>
/// <remarks>
/// <para><see cref="SetState"/> and <see cref="Bypassed"/> may be called from any thread while audio runs: the filters
/// are rebuilt and swapped atomically. <see cref="Process"/> must be called by one thread at a time (the audio thread
/// that owns this instance).</para>
/// <para>Identity cases are bit-exact: a neutral state, Surround at 0 % and Ambience at 0 % never touch the samples.
/// After half a second of digital silence the filters and the reverb are reset and processing pauses, so silence stays
/// exactly zero (the reverb tail is below −85 dB by then).</para>
/// </remarks>
public sealed class FxChain
{
    private readonly object _buildLock = new();
    private readonly int _settleSamples;
    private readonly SchroederReverb[] _reverbs;
    private volatile Stages _stages;
    private volatile bool _bypassed;

    // Audio-thread state (only touched by Process).
    private Stages? _lastStages;
    private int _silentSamples;
    private bool _settled;

    /// <summary>Creates a neutral chain for interleaved float audio.</summary>
    /// <param name="sampleRate">Sample rate of the processed audio in Hz.</param>
    /// <param name="channels">Interleaved channel count (Surround needs 2).</param>
    public FxChain(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        SampleRate = sampleRate;
        Channels = channels;
        _settleSamples = (int)Math.Min(int.MaxValue, (long)sampleRate * channels / 2);
        _reverbs = new SchroederReverb[channels];
        for (int c = 0; c < channels; c++)
        {
            _reverbs[c] = new SchroederReverb(sampleRate, c);
        }

        _stages = Stages.Build(FxState.Neutral, sampleRate, channels);
    }

    /// <summary>Sample rate this instance processes.</summary>
    public int SampleRate { get; }

    /// <summary>Interleaved channel count this instance processes.</summary>
    public int Channels { get; }

    /// <summary>The state last set.</summary>
    public FxState State => _stages.State;

    /// <summary>
    /// When true, <see cref="Process"/> leaves the buffer untouched (the monitor bus while Equalizer APO already applies
    /// the same effects on the headset it plays on).
    /// </summary>
    public bool Bypassed
    {
        get => _bypassed;
        set => _bypassed = value;
    }

    /// <summary>True when <see cref="Process"/> would change the audio (not bypassed and not neutral).</summary>
    public bool IsActive => !_bypassed && !_stages.IsIdentity;

    /// <summary>Applies <paramref name="state"/> (rebuilds the filters; the reverb keeps running while it stays on).</summary>
    public void SetState(FxState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_buildLock)
        {
            _stages = Stages.Build(state, SampleRate, Channels);
        }
    }

    /// <summary>Applies the effects to interleaved float samples in place.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(count), "offset/count lie outside the buffer.");
        }

        if (_bypassed || count == 0)
        {
            return;
        }

        var stages = _stages;
        if (stages.IsIdentity)
        {
            _lastStages = stages;
            return;
        }

        if (!ReferenceEquals(stages, _lastStages))
        {
            // Ambience switched on (again): start from a quiet room, never from a stale tail.
            if (stages.Wet > 0 && (_lastStages is null || _lastStages.Wet == 0))
            {
                ResetReverbs();
            }

            _lastStages = stages;
        }

        if (IsSilent(buffer, offset, count))
        {
            _silentSamples = (int)Math.Min(int.MaxValue, (long)_silentSamples + count);
            if (_silentSamples >= _settleSamples)
            {
                if (!_settled)
                {
                    _settled = true;
                    ResetState(stages);
                }

                return;
            }
        }
        else
        {
            _silentSamples = 0;
            _settled = false;
        }

        int channels = Channels;
        int end = offset + (count / channels * channels);
        RunBank(stages.Bank, buffer, offset, end - offset, channels);

        if (channels == 2 && stages.SideGain != 1f)
        {
            float sideGain = stages.SideGain;
            for (int i = offset; i < end; i += 2)
            {
                float l = buffer[i];
                float r = buffer[i + 1];
                float mid = 0.5f * (l + r);
                float side = 0.5f * (l - r) * sideGain;
                buffer[i] = mid + side;
                buffer[i + 1] = mid - side;
            }
        }

        if (stages.Wet > 0)
        {
            float wet = stages.Wet;
            for (int c = 0; c < channels; c++)
            {
                var reverb = _reverbs[c];
                float gain = wet * reverb.Normalization;
                for (int i = offset + c; i < end; i += channels)
                {
                    float x = buffer[i];
                    buffer[i] = x + gain * reverb.Process(x);
                }
            }
        }
    }

    /// <summary>True when every sample in the range is exactly 0.</summary>
    internal static bool IsSilent(float[] buffer, int offset, int count)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            if (buffer[i] != 0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Runs every band's per-channel biquad over whole frames of the range, band by band.</summary>
    internal static void RunBank(BiQuadFilter[][] bank, float[] buffer, int offset, int count, int channels)
    {
        if (bank.Length == 0)
        {
            return;
        }

        int end = offset + (count / channels * channels);
        foreach (var band in bank)
        {
            for (int c = 0; c < channels; c++)
            {
                var filter = band[c];
                for (int i = offset + c; i < end; i += channels)
                {
                    buffer[i] = filter.Transform(buffer[i]);
                }
            }
        }
    }

    private void ResetReverbs()
    {
        foreach (var r in _reverbs)
        {
            r.Reset();
        }
    }

    private void ResetState(Stages current)
    {
        ResetReverbs();
        lock (_buildLock)
        {
            if (ReferenceEquals(_stages, current))
            {
                var fresh = Stages.Build(current.State, SampleRate, Channels);
                _stages = fresh;
                _lastStages = fresh;
            }
        }
    }

    /// <summary>One immutable set of parameters + fresh filters, swapped as a whole.</summary>
    private sealed class Stages
    {
        private Stages(FxState state, BiQuadFilter[][] bank, float sideGain, float wet)
        {
            State = state;
            Bank = bank;
            SideGain = sideGain;
            Wet = wet;
            IsIdentity = bank.Length == 0 && sideGain == 1f && wet == 0f;
        }

        public FxState State { get; }

        public BiQuadFilter[][] Bank { get; }

        /// <summary>Side (L−R) gain = 1 + 2k; 1 = Surround off.</summary>
        public float SideGain { get; }

        public float Wet { get; }

        public bool IsIdentity { get; }

        public static Stages Build(FxState state, int sampleRate, int channels)
        {
            var filters = state.EqFilters.Concat(state.EffectFilters).ToList();
            var bank = GraphicEq.BuildBank(filters, sampleRate, channels);
            float sideGain = channels == 2 && state.SurroundWidth > 0 ? (float)(1 + 2 * state.SurroundWidth) : 1f;
            return new Stages(state, bank, sideGain, (float)state.AmbienceWet);
        }
    }
}

/// <summary>
/// A light Schroeder reverb for one channel: 4 parallel feedback combs (Freeverb's tunings, each channel spread by 23
/// samples so left and right decorrelate) into 2 series all-passes. RT60 = <see cref="AmbienceImpulse.LengthSeconds"/>.
/// <see cref="Normalization"/> scales the wet output to unit impulse energy, like the Equalizer APO impulse response's
/// tail, so the same wet level sounds the same on both paths.
/// </summary>
internal sealed class SchroederReverb
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356];
    private static readonly int[] AllPassTunings = [556, 441];
    private const int StereoSpread = 23;
    private const float AllPassGain = 0.5f;
    private const float CombMix = 0.25f;

    private readonly float[][] _combs;
    private readonly float[] _combFeedback;
    private readonly int[] _combIndex;
    private readonly float[][] _allPasses;
    private readonly int[] _allPassIndex;

    public SchroederReverb(int sampleRate, int channelIndex)
    {
        double scale = sampleRate / 44100.0;
        int spread = (channelIndex % 2) * StereoSpread;
        _combs = new float[CombTunings.Length][];
        _combFeedback = new float[CombTunings.Length];
        _combIndex = new int[CombTunings.Length];
        double energy = 0;
        for (int i = 0; i < CombTunings.Length; i++)
        {
            int delay = Math.Max(1, (int)Math.Round((CombTunings[i] + spread) * scale));
            _combs[i] = new float[delay];
            double g = Math.Pow(10, -3.0 * delay / (sampleRate * AmbienceImpulse.LengthSeconds));
            _combFeedback[i] = (float)g;
            energy += CombMix * CombMix / (1 - g * g);
        }

        _allPasses = new float[AllPassTunings.Length][];
        _allPassIndex = new int[AllPassTunings.Length];
        for (int i = 0; i < AllPassTunings.Length; i++)
        {
            _allPasses[i] = new float[Math.Max(1, (int)Math.Round((AllPassTunings[i] + spread) * scale))];
        }

        // Combs with distinct delays are nearly uncorrelated and all-passes keep energy: impulse energy ≈ Σ mix²/(1−g²).
        Normalization = (float)(1 / Math.Sqrt(energy));
    }

    /// <summary>Gain that brings the reverb's impulse response to unit energy.</summary>
    public float Normalization { get; }

    /// <summary>One sample in, the reverberated (wet only) sample out.</summary>
    public float Process(float input)
    {
        float sum = 0f;
        for (int i = 0; i < _combs.Length; i++)
        {
            var buf = _combs[i];
            int idx = _combIndex[i];
            float y = buf[idx];
            float next = input + _combFeedback[i] * y;
            buf[idx] = MathF.Abs(next) < 1e-20f ? 0f : next; // no denormals
            _combIndex[i] = idx + 1 == buf.Length ? 0 : idx + 1;
            sum += y;
        }

        float x = sum * CombMix;
        for (int i = 0; i < _allPasses.Length; i++)
        {
            var buf = _allPasses[i];
            int idx = _allPassIndex[i];
            float delayed = buf[idx];
            float w = x + AllPassGain * delayed;
            buf[idx] = MathF.Abs(w) < 1e-20f ? 0f : w;
            _allPassIndex[i] = idx + 1 == buf.Length ? 0 : idx + 1;
            x = delayed - AllPassGain * w;
        }

        return x;
    }

    /// <summary>Silences the tail.</summary>
    public void Reset()
    {
        foreach (var c in _combs)
        {
            Array.Clear(c);
        }

        foreach (var a in _allPasses)
        {
            Array.Clear(a);
        }
    }
}
