namespace AudioController.Core.Models;

public enum DeviceKind
{
    Playback,
    Capture,
}

public enum DeviceActivity
{
    Active,
    Disabled,
    Unplugged,
    NotPresent,
}

/// <summary>Snapshot of one Windows audio endpoint for display and control.</summary>
public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }

    public required string FriendlyName { get; init; }

    public DeviceKind Kind { get; init; }

    public DeviceActivity State { get; init; }

    /// <summary>True when the endpoint lives on the Bluetooth enumerator (BTHENUM etc.).</summary>
    public bool IsBluetooth { get; init; }

    /// <summary>
    /// True for software endpoints (VB-Audio cables, Voicemod, VoiceMeeter, …) rather than real hardware.
    /// See <see cref="Devices.VirtualDeviceDetector"/>.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// DEVPKEY_Device_ContainerId of the physical device this endpoint belongs to (e.g. a headset's
    /// headphones and its microphone share one), or null when unknown. Every built-in / root device shares
    /// the special "internally connected" container, so that value says nothing about two endpoints being
    /// one gadget (see <see cref="Routing.RoutingPlanner.SameContainer"/>).
    /// </summary>
    public string? ContainerId { get; init; }

    public bool IsDefault { get; init; }

    public bool IsDefaultCommunications { get; init; }

    /// <summary>0.0–1.0 master volume scalar; display as 0–100.</summary>
    public float Volume { get; init; }

    public bool Muted { get; init; }

    /// <summary>Null = Windows "Audio Enhancements" status unknown/unsupported for this device.</summary>
    public bool? EnhancementsEnabled { get; init; }
}
