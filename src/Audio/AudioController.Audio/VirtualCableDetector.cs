using System.Runtime.InteropServices;
using AudioController.Core.Abstractions;
using AudioController.Core.Devices;
using AudioController.Core.Models;

namespace AudioController.Audio;

/// <summary>
/// Detects VB-Audio Virtual Cable: an active playback endpoint on the cable (its "CABLE Input", where the app writes
/// the mix) and an active recording endpoint on it (its "CABLE Output", what other apps select as their microphone).
/// Rename-proof: the endpoints are recognised by the "(VB-Audio Virtual Cable)" suffix Windows keeps when the user
/// renames them (see <see cref="VirtualDeviceDetector.IsOnVirtualMicCable"/>).
/// </summary>
public static class VirtualCableDetector
{
    /// <summary>Default name fragment of the cable's playback endpoint.</summary>
    public const string CableInputName = VirtualDeviceDetector.CableInputName;

    /// <summary>Default name fragment of the cable's recording endpoint.</summary>
    public const string CableOutputName = VirtualDeviceDetector.CableOutputName;

    /// <summary>
    /// Returns true only when both cable endpoints exist and are active. The ids are set to whichever
    /// endpoints were found (null when missing). Device enumeration failures count as "not installed".
    /// </summary>
    public static bool IsInstalled(IDeviceManager devices, out string? cableInputId, out string? cableOutputId) =>
        IsInstalled(devices, null, out cableInputId, out cableOutputId);

    /// <summary>
    /// As <see cref="IsInstalled(IDeviceManager, out string?, out string?)"/>; the playback side is chosen with
    /// <see cref="VirtualDeviceDetector.ChooseCableFeed"/>: <paramref name="savedOutputId"/> when it is still on the
    /// cable, else the one named "CABLE Input", else the one not named "16ch", else the first. The recording side is
    /// the one named "CABLE Output", else the first on the cable.
    /// </summary>
    public static bool IsInstalled(IDeviceManager devices, string? savedOutputId, out string? cableInputId, out string? cableOutputId)
    {
        ArgumentNullException.ThrowIfNull(devices);

        IReadOnlyList<AudioDeviceInfo> list;
        try
        {
            list = devices.GetDevices();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            cableInputId = null;
            cableOutputId = null;
            return false;
        }

        cableInputId = VirtualDeviceDetector.ChooseCableFeed(list, savedOutputId)?.Id;
        cableOutputId = VirtualDeviceDetector.ChooseCableMic(list)?.Id;
        return cableInputId is not null && cableOutputId is not null;
    }
}
