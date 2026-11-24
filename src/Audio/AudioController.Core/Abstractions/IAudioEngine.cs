using AudioController.Core.Models;

namespace AudioController.Core.Abstractions;

/// <summary>Live status for the bottom bar. Display only — never processing.</summary>
public sealed class EngineStatus
{
    public bool Running { get; init; }

    public string? InputDeviceId { get; init; }

    public string? OutputDeviceId { get; init; }

    public bool AnySoundPlaying { get; init; }

    /// <summary>Peak of the outgoing buffer, 0.0–1.0, for the meter.</summary>
    public float PeakLevel { get; init; }

    /// <summary>
    /// The microphone is muted (see <see cref="IAudioEngine.MicMuted"/>): no capture device is open and the
    /// mic branch of the output is silence. Reported whether or not the engine is running.
    /// </summary>
    public bool MicMuted { get; init; }
}

public interface IAudioEngine : IDisposable
{
    bool IsRunning { get; }

    bool AnySoundPlaying { get; }

    /// <summary>
    /// Microphone mute. Muting <b>fully releases</b> the capture device — the WASAPI capture stream is stopped
    /// and disposed, not fed zeros — so Windows stops ducking other audio and a Bluetooth headset leaves its
    /// low-quality hands-free (HFP) mode. While muted the engine keeps rendering: the mic branch is silence
    /// and soundboard audio still reaches the virtual cable exactly as before.
    /// </summary>
    /// <remarks>
    /// Setting it while stopped only records the choice; <see cref="Start"/> honours it and does not open the
    /// microphone at all when muted. Unmuting while running reopens the configured input device and resumes
    /// bit-exact pass-through; if that device is gone, <see cref="EngineError"/> explains why and the engine
    /// stays muted (the getter then still returns true). Never throws.
    /// </remarks>
    bool MicMuted { get; set; }

    /// <summary>
    /// Starts mic → virtual-cable pass-through. Mic audio is copied unchanged;
    /// soundboard audio is summed in only while sounds play.
    /// </summary>
    void Start(string inputDeviceId, string outputDeviceId);

    void Stop();

    /// <summary>Begins playback of a decoded sound (canonical WAV bytes) on the mix bus.</summary>
    void PlaySound(SoundSlot slot, byte[] wavData);

    /// <summary>Stops all playing instances of one slot (used by Toggle / HoldToPlay).</summary>
    void StopSound(Guid slotId);

    bool IsSlotPlaying(Guid slotId);

    void StopAll();

    /// <summary>Local monitor: also play soundboard audio on this output. Never carries mic audio.</summary>
    void SetMonitor(string? deviceId, bool enabled);

    /// <summary>10-band EQ applied to the soundboard bus ONLY — never to the microphone.</summary>
    void SetEq(float[] gainsDb, bool enabled);

    /// <summary>
    /// The whole Effects panel (graphic EQ + Bass Boost, Fidelity, Dynamic Boost, Surround, Ambience) on the soundboard
    /// bus ONLY — never on the microphone. The same snapshot drives Equalizer APO on the headset. Implementations that
    /// only know the 1.4 EQ fall back to <see cref="SetEq"/> with the 10 band gains.
    /// </summary>
    void SetFx(Equalizer.FxState fx) => SetEq(fx.LegacyGainsDb, fx.LegacyEqEnabled);

    /// <summary>
    /// Double-EQ guard: when true the in-app EQ is bypassed on the local MONITOR bus only (Equalizer APO already
    /// equalizes the headset the monitor plays on). The virtual-mic bus keeps the EQ; the microphone is never touched.
    /// </summary>
    void SetMonitorEqBypass(bool bypass);

    event EventHandler<EngineStatus>? StatusChanged;

    event EventHandler<string>? EngineError;
}
