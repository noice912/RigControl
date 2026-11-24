using AudioController.Core.Equalizer;

namespace AudioController.Audio;

/// <summary>
/// The composition-root view of <see cref="EqualizerApoWriter"/>: the one knob the app's view model turns to keep
/// Equalizer APO's <c>AudioController.txt</c> in step with the EQ sliders. The semantic
/// <see cref="WriteApoState"/> entry point owns the Generate + Schedule choice so the view model never has to compose
/// the content itself (and tests can swap in a fake writer that records calls).
/// </summary>
public interface IEqualizerApoWriter : IDisposable
{
    /// <summary>Raised (on the writing thread) with a readable message when a debounced write fails.</summary>
    event EventHandler<string>? WriteFailed;

    /// <summary>Raised (on the writing thread) after a write changed a file.</summary>
    event EventHandler? Written;

    /// <summary>Equalizer APO's config folder.</summary>
    string ConfigDirectory { get; }

    /// <summary>The app's own file (<c>&lt;config&gt;\AudioController.txt</c>).</summary>
    string FilePath { get; }

    /// <summary>True when the app's file exists (the app has written it before).</summary>
    bool FileExists { get; }

    /// <summary>
    /// Writes the right <c>AudioController.txt</c> content for the current EQ state (debounced ~150 ms):
    /// when Equalizer APO is attached to the chosen headset and a selector can be derived, the Generate'd EQ
    /// scoped to the headset (and <c>config.txt</c> gets its include line); otherwise a flat / scoped-off file,
    /// so stale filters pointing at an unused device never linger. Never throws from the schedule call itself.
    /// </summary>
    /// <param name="gainsDb">The 10 band gains (clamped and 0.01 dB-skipped by Generate).</param>
    /// <param name="eqEnabled">The app's "Enable EQ" switch.</param>
    /// <param name="headsetDeviceId">The chosen headset's Windows endpoint id, or null.</param>
    /// <param name="apoAttachedToHeadset">True when Equalizer APO is attached to the chosen headset AND the user ticked
    /// "EQ everything I hear" (plan.ApoActive). False writes the flat file.</param>
    /// <param name="autoPreamp">Lower the level by the biggest boost so a boosted band cannot clip.</param>
    void WriteApoState(float[] gainsDb, bool eqEnabled, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp);

    /// <summary>
    /// The Effects panel's entry point: like <see cref="WriteApoState"/> but for the whole <paramref name="fx"/> snapshot
    /// (graphic EQ — 10 adjustable or 31 third-octave bands — plus Bass Boost, Fidelity, Dynamic Boost, Surround and
    /// Ambience). The real writer also saves <c>ambience.wav</c> next to <c>AudioController.txt</c> while Ambience is on.
    /// Writers that only know the 1.4 ten gains (older implementations, test fakes) fall back to
    /// <see cref="WriteApoState"/> with the 10 band gains.
    /// </summary>
    /// <param name="fx">The Effects snapshot (<see cref="FxState.From"/>).</param>
    /// <param name="headsetDeviceId">The chosen headset's Windows endpoint id, or null.</param>
    /// <param name="apoAttachedToHeadset">plan.ApoActive (Equalizer APO on the headset and "EQ everything I hear" on).</param>
    /// <param name="autoPreamp">Pre-amp = −(largest band boost + largest effect boost).</param>
    /// <param name="channels">The headset's channel count (Surround's channel mixing is stereo-only).</param>
    /// <param name="sampleRate">The headset's sample rate (the Ambience impulse response must match it).</param>
    void WriteFxState(FxState fx, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp,
        int channels = 2, int sampleRate = AmbienceImpulse.DefaultSampleRate)
        => WriteApoState(fx.LegacyGainsDb, fx.LegacyEqEnabled, headsetDeviceId, apoAttachedToHeadset, autoPreamp);

    /// <summary>
    /// Writes <paramref name="content"/> now (when different) and, when <paramref name="ensureInclude"/>, makes sure
    /// <c>config.txt</c> includes it. Returns null on success, else a readable error. Never throws. Cancels a
    /// pending <see cref="Schedule"/>d write.
    /// </summary>
    string? WriteNow(string content, bool ensureInclude);

    /// <summary>Queues a write; only the last content queued within ~150 ms is written.</summary>
    void Schedule(string content, bool ensureInclude);

    /// <summary>Writes a pending <see cref="Schedule"/>d content now (no-op when nothing is pending). Never throws.</summary>
    void Flush();
}
