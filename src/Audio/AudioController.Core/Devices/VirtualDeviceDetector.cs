using AudioController.Core.Models;

namespace AudioController.Core.Devices;

/// <summary>
/// Pure logic for telling software ("virtual") audio endpoints — VB-Audio cables, Voicemod, VoiceMeeter,
/// Steam Streaming and the like — apart from real hardware (sound cards, USB and Bluetooth headsets, HDMI).
/// </summary>
/// <remarks>
/// <para>The device's enumerator (DEVPKEY_Device_EnumeratorName) is the strongest signal. Real audio hardware
/// sits on a hardware bus (HDAUDIO, USB, BTHENUM, BTHHFENUM, INTELAUDIO, PCI, ACPI, …). Virtual audio drivers are
/// installed as root-enumerated or software devices (ROOT, SWD, SW), and so is anything else that is not a
/// known hardware bus. Measured on a real machine: VB-Audio CABLE Input/Output, "Dummy Output (Voicemod)" and
/// "Microphone (Voicemod)" are all ROOT; the Crusher ANC 2 is BTHENUM (A2DP) / BTHHFENUM (hands-free mic).</para>
/// <para>Names refine that:</para>
/// <list type="bullet">
/// <item>Specific virtual products (VB-Audio, a name starting with "CABLE", "Hi-Fi Cable", Voicemod,
/// VoiceMeeter, Steam Streaming, "Dummy Output") are virtual whatever the enumerator says.</item>
/// <item>The generic words "Virtual" and "Cable" only decide when the enumerator is unknown (property
/// absent). With a hardware enumerator they are ignored, so a real USB headset whose driver calls itself
/// "Speakers (HyperX Virtual Surround Sound)" stays a headset.</item>
/// </list>
/// </remarks>
public static class VirtualDeviceDetector
{
    /// <summary>Name fragment of the VB-Audio cable's playback endpoint that feeds the virtual microphone.</summary>
    public const string CableInputName = "CABLE Input";

    /// <summary>The 16-channel playback endpoint of the same VB-Audio cable; it also feeds "CABLE Output".</summary>
    public const string CableIn16ChName = "CABLE In 16ch";

    /// <summary>Name fragment of the VB-Audio cable's recording endpoint (what Discord selects as the mic).</summary>
    public const string CableOutputName = "CABLE Output";

    /// <summary>
    /// The interface suffix on every endpoint of VB-Audio's (first) Virtual Cable. Windows keeps it when the user renames
    /// an endpoint, so it identifies the virtual-mic cable whatever the endpoints are called.
    /// </summary>
    public const string VirtualMicCableSuffix = "(VB-Audio Virtual Cable)";

    /// <summary>Plain label for the cable's playback endpoint the app plays into.</summary>
    public const string FeedLabel = "Virtual mic feed (the app plays into this — never pick it in other apps)";

    /// <summary>Plain label for the cable's recording endpoint other apps use as their microphone.</summary>
    public const string MicLabel = "Virtual mic — choose this as your microphone in Discord";

    /// <summary>Enumerators of real audio hardware buses. Anything starting with "BTH" is Bluetooth hardware too.</summary>
    private static readonly HashSet<string> HardwareEnumerators = new(StringComparer.OrdinalIgnoreCase)
    {
        "HDAUDIO", "USB", "BTHENUM", "BTHHFENUM", "BTHLEDEVICE", "BTHLE", "INTELAUDIO", "DISPLAY",
        "PCI", "ACPI", "SCSI", "HID", "SOUNDWIRE", "SDW", "AVSBUS", "PCMCIA", "1394", "THUNDERBOLT",
    };

    /// <summary>Unmistakable virtual-product names: virtual regardless of the enumerator.</summary>
    private static readonly string[] VirtualProductNames =
    {
        "VB-Audio", "Hi-Fi Cable", "Voicemod", "VoiceMeeter", "Steam Streaming", "Dummy Output",
    };

    /// <summary>Generic hints: only used when the enumerator is unknown.</summary>
    private static readonly string[] WeakVirtualHints = { "Virtual", "Cable" };

    /// <summary>
    /// True when the endpoint is a software device rather than real hardware.
    /// </summary>
    /// <param name="enumeratorName">DEVPKEY_Device_EnumeratorName of the endpoint's device (e.g. "ROOT",
    /// "USB", "BTHENUM"), or null when Windows did not report it.</param>
    /// <param name="friendlyName">The endpoint's friendly name, e.g. "CABLE Input (VB-Audio Virtual Cable)".</param>
    public static bool IsVirtual(string? enumeratorName, string friendlyName)
    {
        string name = friendlyName ?? "";
        if (IsVirtualProductName(name))
        {
            return true;
        }

        string? enumerator = NormalizeEnumerator(enumeratorName);
        if (enumerator is null)
        {
            // Unknown bus: fall back to the generic name hints.
            return WeakVirtualHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        // ROOT, SWD, SW and any other non-hardware enumerator are software devices.
        return !IsHardwareEnumerator(enumerator);
    }

    /// <summary>True for names of well-known virtual audio products (see the class remarks).</summary>
    public static bool IsVirtualProductName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        string name = friendlyName.Trim();
        return name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase)
               || VirtualProductNames.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="enumeratorName"/> is a known hardware audio bus.</summary>
    public static bool IsHardwareEnumerator(string? enumeratorName)
    {
        string? enumerator = NormalizeEnumerator(enumeratorName);
        return enumerator is not null
               && (HardwareEnumerators.Contains(enumerator)
                   || enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the endpoint is on the VB-Audio Virtual Cable whose recording side is the virtual microphone. Rename-proof:
    /// Windows keeps the interface suffix when the user renames an endpoint ("CABLE Output" → "Audio Controller Mic"
    /// shows as "Audio Controller Mic (VB-Audio Virtual Cable)"), so a name ending with
    /// <see cref="VirtualMicCableSuffix"/> is on the cable; names without the suffix count when they start with
    /// "CABLE Input", "CABLE In 16ch" or "CABLE Output". VB-Audio CABLE A/B ("… (VB-Audio Cable A)") and the Hi-Fi
    /// Cable are separate cables and do not match. Name-only: callers apply it to playback or recording endpoints.
    /// </summary>
    public static bool IsOnVirtualMicCable(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        string name = friendlyName.Trim();
        return name.EndsWith(VirtualMicCableSuffix, StringComparison.OrdinalIgnoreCase)
               || name.StartsWith(CableInputName, StringComparison.OrdinalIgnoreCase)
               || name.StartsWith(CableIn16ChName, StringComparison.OrdinalIgnoreCase)
               || name.StartsWith(CableOutputName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// For a playback endpoint name: true when it is on the virtual-mic cable (<see cref="IsOnVirtualMicCable"/>) — its
    /// "CABLE Input", its 16-channel "CABLE In 16ch", or either of them renamed. Both feed the virtual microphone, so
    /// anything played there is heard by Discord. A name starting with "CABLE Output" (the recording side) is not.
    /// </summary>
    public static bool IsVirtualMicInput(string? friendlyName) =>
        IsOnVirtualMicCable(friendlyName)
        && !friendlyName!.TrimStart().StartsWith(CableOutputName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For a recording endpoint name: true for the virtual-mic cable's recording side ("CABLE Output", or renamed —
    /// see <see cref="IsOnVirtualMicCable"/>), which is never a real microphone. Names starting with "CABLE Input" /
    /// "CABLE In 16ch" (the playback side) are not.
    /// </summary>
    public static bool IsVirtualMicOutput(string? friendlyName)
    {
        if (friendlyName is null)
        {
            return false;
        }

        string name = friendlyName.TrimStart();
        if (name.StartsWith(CableInputName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(CableIn16ChName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsOnVirtualMicCable(name) || name.Contains(CableOutputName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for an endpoint of the virtual-mic cable (playback or recording; see <see cref="IsOnVirtualMicCable"/>).</summary>
    public static bool IsVirtualMicCable(AudioDeviceInfo device) =>
        device is not null
        && (device.Kind == DeviceKind.Playback ? IsVirtualMicInput(device.FriendlyName) : IsVirtualMicOutput(device.FriendlyName));

    /// <summary>
    /// The cable's playback endpoint the app plays the mix into (the "virtual mic feed"), among active devices:
    /// <paramref name="savedOutputId"/> when it is still on the cable; else the one whose name contains "CABLE Input";
    /// else the one whose name does not contain "16ch"; else the first (by name). Null when the cable has no active
    /// playback endpoint.
    /// </summary>
    public static AudioDeviceInfo? ChooseCableFeed(IReadOnlyList<AudioDeviceInfo> devices, string? savedOutputId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var onCable = devices
            .Where(d => d.Kind == DeviceKind.Playback && d.State == DeviceActivity.Active && IsVirtualMicInput(d.FriendlyName))
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return onCable.FirstOrDefault(d => SameId(d.Id, savedOutputId))
               ?? onCable.FirstOrDefault(d => d.FriendlyName.Contains(CableInputName, StringComparison.OrdinalIgnoreCase))
               ?? onCable.FirstOrDefault(d => !IsSixteenChannelName(d.FriendlyName))
               ?? onCable.FirstOrDefault();
    }

    /// <summary>
    /// The cable's recording endpoint other apps pick as their microphone (the "virtual mic"), among active devices: the
    /// one whose name contains "CABLE Output", else the first (by name). Null when there is none.
    /// </summary>
    public static AudioDeviceInfo? ChooseCableMic(IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var onCable = devices
            .Where(d => d.Kind == DeviceKind.Capture && d.State == DeviceActivity.Active && IsVirtualMicOutput(d.FriendlyName))
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return onCable.FirstOrDefault(d => d.FriendlyName.Contains(CableOutputName, StringComparison.OrdinalIgnoreCase))
               ?? onCable.FirstOrDefault();
    }

    /// <summary>
    /// True for a cable playback endpoint the app never lists: any playback endpoint on the virtual-mic cable other than
    /// the chosen feed (<paramref name="feedId"/>, see <see cref="ChooseCableFeed"/>) — in practice the 16-channel
    /// "CABLE In 16ch", renamed or not. With no feed known, names containing "16ch" are hidden.
    /// </summary>
    public static bool IsHiddenCableEndpoint(AudioDeviceInfo device, string? feedId)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Kind != DeviceKind.Playback || !IsVirtualMicInput(device.FriendlyName))
        {
            return false;
        }

        return feedId is null ? IsSixteenChannelName(device.FriendlyName) : !SameId(device.Id, feedId);
    }

    /// <summary>
    /// "Audio Controller Mic (VB-Audio Virtual Cable)" → "Audio Controller Mic"; "CABLE Output (VB-Audio Virtual Cable)"
    /// → "CABLE Output". Other names are returned trimmed.
    /// </summary>
    public static string ShortCableName(string friendlyName)
    {
        string name = (friendlyName ?? "").Trim();
        if (name.EndsWith(VirtualMicCableSuffix, StringComparison.OrdinalIgnoreCase) && name.Length > VirtualMicCableSuffix.Length)
        {
            string head = name[..^VirtualMicCableSuffix.Length].TrimEnd();
            if (head.Length > 0)
            {
                return head;
            }
        }

        return name;
    }

    private static bool IsSixteenChannelName(string? name) =>
        name is not null && name.Contains("16ch", StringComparison.OrdinalIgnoreCase);

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"ROOT", " root ", "ROOT\\MEDIA\\0000" → "ROOT"; blank → null.</summary>
    private static string? NormalizeEnumerator(string? enumeratorName)
    {
        if (string.IsNullOrWhiteSpace(enumeratorName))
        {
            return null;
        }

        string e = enumeratorName.Trim();
        int slash = e.IndexOf('\\');
        if (slash > 0)
        {
            e = e[..slash];
        }

        return e.Length == 0 ? null : e;
    }
}
