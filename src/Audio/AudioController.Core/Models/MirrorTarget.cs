namespace AudioController.Core.Models;

/// <summary>
/// One extra output of "Also play on" (<see cref="AppSettings.MirrorTargets"/>): a playback device that receives a copy
/// of what plays on the headset, with its own delay and volume. Stored in the encrypted library.
/// </summary>
public sealed class MirrorTarget
{
    /// <summary>Lowest delay in milliseconds.</summary>
    public const int MinDelayMs = 0;

    /// <summary>Highest delay in milliseconds (enough to line up a Bluetooth headset's ~150–250 ms).</summary>
    public const int MaxDelayMs = 500;

    /// <summary>Lowest volume in percent.</summary>
    public const int MinVolumePercent = 0;

    /// <summary>Highest volume in percent (unity gain: the copy is never boosted).</summary>
    public const int MaxVolumePercent = 100;

    /// <summary>The playback endpoint id.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// Its friendly name when it was ticked (or last seen). Re-pairing / re-plugging can give a device a new endpoint id;
    /// the name lets <c>Routing.RoutingPlanner.ReadoptRenamedIds</c> follow it.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>Ticked: the copy plays on this device while "Also play on" is on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Extra delay in milliseconds (0–500), e.g. to line the copy up with a Bluetooth headset.</summary>
    public int DelayMs { get; set; }

    /// <summary>Volume of the copy in percent (0–100, linear gain; 100 % = unchanged).</summary>
    public int VolumePercent { get; set; } = MaxVolumePercent;

    /// <summary>A copy of this entry.</summary>
    public MirrorTarget Clone() => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Enabled = Enabled,
        DelayMs = DelayMs,
        VolumePercent = VolumePercent,
    };
}
