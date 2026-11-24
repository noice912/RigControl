using AudioController.Core.Models;

namespace AudioController.Core.Abstractions;

public enum DeviceRole
{
    Default,
    Communications,
}

public interface IDeviceManager : IDisposable
{
    /// <summary>
    /// All playback + capture endpoints (active, disabled, unplugged).
    /// Only Active devices are valid routing targets.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    void SetDefaultDevice(string deviceId, DeviceRole role);

    /// <param name="volumeScalar">0.0–1.0.</param>
    void SetVolume(string deviceId, float volumeScalar);

    void SetMute(string deviceId, bool muted);

    /// <summary>
    /// Best-effort Windows "Audio Enhancements" toggle.
    /// Returns false where Windows does not allow it for this device.
    /// </summary>
    bool TrySetEnhancements(string deviceId, bool enabled);

    /// <summary>
    /// Best-effort shared-mode mix format of an active endpoint (sample rate, channel count), or null when unknown.
    /// Used for the Equalizer APO effects (Surround's channel mixing needs stereo; the Ambience impulse response must
    /// match the device's rate). Never throws.
    /// </summary>
    (int SampleRate, int Channels)? GetMixFormat(string deviceId) => null;

    event EventHandler? DevicesChanged;
}
