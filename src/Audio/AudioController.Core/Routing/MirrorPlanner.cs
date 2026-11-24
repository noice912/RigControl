using AudioController.Core.Devices;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;

namespace AudioController.Core.Routing;

/// <summary>One extra output the mirror plays on right now, with its settings clamped.</summary>
/// <param name="DeviceId">The playback endpoint id.</param>
/// <param name="DeviceName">Its current friendly name.</param>
/// <param name="DelayMs">Extra delay, 0–500 ms.</param>
/// <param name="VolumePercent">Volume, 0–100 % (linear, never a boost).</param>
public sealed record MirrorRoute(string DeviceId, string DeviceName, int DelayMs, int VolumePercent);

/// <summary>A ticked output that is not played on right now, and why.</summary>
public sealed record MirrorSkip(string DeviceId, string DeviceName, string Reason);

/// <summary>
/// What "Also play on" should do right now (<see cref="MirrorPlanner.Plan"/>): copy <see cref="SourceId"/> (the
/// connected headset, loopback-captured after Equalizer APO) to every <see cref="Routes"/> entry. Pure data.
/// </summary>
public sealed record MirrorPlan
{
    /// <summary>The master switch (<see cref="AppSettings.MirrorEnabled"/>).</summary>
    public bool Requested { get; init; }

    /// <summary>The headset whose sound is copied, while it is connected; else null.</summary>
    public string? SourceId { get; init; }

    /// <summary>Its friendly name (for status texts), or null.</summary>
    public string? SourceName { get; init; }

    /// <summary>The ticked, connected, valid outputs (in the order the user ticked them).</summary>
    public IReadOnlyList<MirrorRoute> Routes { get; init; } = Array.Empty<MirrorRoute>();

    /// <summary>Ticked outputs that cannot be played on right now (not connected, the headset itself, …).</summary>
    public IReadOnlyList<MirrorSkip> Skipped { get; init; } = Array.Empty<MirrorSkip>();

    /// <summary>
    /// Info notes, e.g. Equalizer APO attached to an extra output (it stays neutral there: the copy already carries the
    /// EQ, and the app's Equalizer APO file is scoped to the headset only).
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The mirror runs: requested, a connected headset and at least one valid output.</summary>
    public bool Active => Requested && SourceId is not null && Routes.Count > 0;

    /// <summary>When not <see cref="Active"/>: why (one short sentence); empty while active.</summary>
    public string InactiveReason { get; init; } = "";
}

/// <summary>
/// Pure rules for "Also play on" (the mirror): which playback devices may receive the copy, which ticked ones are played
/// on right now, and the clamping of the stored settings. No I/O; the same inputs always give the same plan.
/// </summary>
/// <remarks>
/// <para>Windows renders a program to one endpoint only, so the app copies the headset instead: it loopback-captures the
/// HEADSET endpoint, which is after Equalizer APO, so the copy already carries the user's EQ and effects (the app never
/// equalizes the extra outputs, and its Equalizer APO file stays scoped to the headset, so an extra output with
/// Equalizer APO attached stays neutral).</para>
/// <para>Never an output: the headset itself (a feedback loop), any endpoint of the virtual-mic cable ("CABLE Input",
/// "CABLE In 16ch", renamed or not — Discord would hear PC audio), the virtual-mic output, the System EQ inlet, or any
/// other virtual device. Only active playback devices are played on.</para>
/// </remarks>
public static class MirrorPlanner
{
    /// <summary>Status while the master switch is off.</summary>
    public const string OffText = "Off";

    /// <summary>The master switch is on but nothing is ticked.</summary>
    public const string NothingTickedText = "Tick an output below.";

    /// <summary>No headset picked yet.</summary>
    public const string PickHeadsetText = "Pick your headset first — the copy is taken from it.";

    /// <summary>The headset is not connected.</summary>
    public const string HeadsetMissingText = "Your headset isn't connected — it starts again when it's back.";

    /// <summary>Info note for Equalizer APO attached to an extra output.</summary>
    public static string ApoOnTargetText(string deviceName) =>
        $"Equalizer APO is also attached to {deviceName}. Audio Controller keeps it neutral there — the copy already has your EQ and effects.";

    /// <summary>
    /// The devices offered in the "Also play on" list: active, real playback devices other than the headset, never an
    /// endpoint of the virtual-mic cable or the virtual-mic output (by name order).
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> TargetChoices(
        IReadOnlyList<AudioDeviceInfo> devices, string? headsetId, string? virtualMicOutputId = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.Where(d => RejectReason(d, headsetId, virtualMicOutputId) is null)
                      .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    /// <summary>
    /// Null when <paramref name="device"/> may receive the copy right now, else a short reason. Checked in this order:
    /// not a playback device, the headset itself, the virtual-mic cable (renamed or not), the virtual-mic output, a
    /// virtual device, not connected.
    /// </summary>
    public static string? RejectReason(AudioDeviceInfo device, string? headsetId, string? virtualMicOutputId = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Kind != DeviceKind.Playback)
        {
            return "not a playback device";
        }

        if (SameId(device.Id, headsetId))
        {
            return "that's your headset itself";
        }

        if (VirtualDeviceDetector.IsOnVirtualMicCable(device.FriendlyName))
        {
            return "that's the virtual mic feed — Discord would hear your PC audio";
        }

        if (SameId(device.Id, virtualMicOutputId))
        {
            return "that's the virtual mic output — Discord would hear your PC audio";
        }

        if (device.IsVirtual)
        {
            return "virtual devices can't be used";
        }

        if (device.State != DeviceActivity.Active)
        {
            return "not connected";
        }

        return null;
    }

    /// <summary>
    /// The mirror for <paramref name="settings"/> on <paramref name="devices"/>. The source is the routing plan's
    /// connected headset (or, without a plan, the saved headset when it is active and not a cable endpoint).
    /// </summary>
    /// <param name="settings">The saved choices (not modified).</param>
    /// <param name="devices">All endpoints Windows reports (any state).</param>
    /// <param name="routing">The current routing plan (headset, virtual-mic output), or null.</param>
    /// <param name="apo">Equalizer APO's state, for the "stays neutral there" note (null = not installed).</param>
    public static MirrorPlan Plan(
        AppSettings settings, IReadOnlyList<AudioDeviceInfo> devices, RoutingPlan? routing = null, EqualizerApoState? apo = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        apo ??= EqualizerApoState.NotInstalled;

        string? headsetId;
        bool headsetMissing;
        if (routing is not null)
        {
            headsetId = routing.HeadsetId;
            headsetMissing = routing.HeadsetMissing;
        }
        else
        {
            var saved = string.IsNullOrWhiteSpace(settings.HeadsetDeviceId)
                ? null
                : devices.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.State == DeviceActivity.Active
                                              && SameId(d.Id, settings.HeadsetDeviceId)
                                              && !VirtualDeviceDetector.IsVirtualMicInput(d.FriendlyName));
            headsetId = saved?.Id;
            headsetMissing = !string.IsNullOrWhiteSpace(settings.HeadsetDeviceId) && saved is null;
        }

        string? virtualMicOutputId = routing?.VirtualMicOutputId;
        var headset = headsetId is null ? null : devices.FirstOrDefault(d => SameId(d.Id, headsetId));
        var routes = new List<MirrorRoute>();
        var skipped = new List<MirrorSkip>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in settings.MirrorTargets ?? new List<MirrorTarget>())
        {
            if (target is null || !target.Enabled || string.IsNullOrWhiteSpace(target.DeviceId) || !seen.Add(target.DeviceId))
            {
                continue;
            }

            var device = devices.FirstOrDefault(d => SameId(d.Id, target.DeviceId));
            string name = device?.FriendlyName ?? target.DeviceName ?? "saved output";
            string? reason = device is null ? "not connected" : RejectReason(device, headsetId, virtualMicOutputId);
            if (reason is not null)
            {
                skipped.Add(new MirrorSkip(target.DeviceId, name, reason));
                continue;
            }

            routes.Add(new MirrorRoute(device!.Id, name, ClampDelay(target.DelayMs), ClampVolume(target.VolumePercent)));
            if (apo.IsAttachedTo(device.Id))
            {
                notes.Add(ApoOnTargetText(name));
            }
        }

        string inactive = !settings.MirrorEnabled ? OffText
            : string.IsNullOrWhiteSpace(settings.HeadsetDeviceId) && headsetId is null ? PickHeadsetText
            : headsetId is null || headsetMissing ? HeadsetMissingText
            : routes.Count == 0 && skipped.Count == 0 ? NothingTickedText
            : routes.Count == 0 ? $"Nothing to play on: {DescribeSkips(skipped)}."
            : "";

        return new MirrorPlan
        {
            Requested = settings.MirrorEnabled,
            SourceId = headsetMissing ? null : headsetId,
            SourceName = headset?.FriendlyName,
            Routes = routes.AsReadOnly(),
            Skipped = skipped.AsReadOnly(),
            Notes = notes.AsReadOnly(),
            InactiveReason = inactive,
        };
    }

    /// <summary>"A (not connected), B (that's your headset itself)".</summary>
    public static string DescribeSkips(IEnumerable<MirrorSkip> skips) =>
        string.Join(", ", skips.Select(s => $"\"{s.DeviceName}\" ({s.Reason})"));

    /// <summary>0–500 ms.</summary>
    public static int ClampDelay(int delayMs) => Math.Clamp(delayMs, MirrorTarget.MinDelayMs, MirrorTarget.MaxDelayMs);

    /// <summary>0–100 %.</summary>
    public static int ClampVolume(int percent) => Math.Clamp(percent, MirrorTarget.MinVolumePercent, MirrorTarget.MaxVolumePercent);

    /// <summary>
    /// Repairs the stored mirror settings in place: a missing list becomes empty; null entries, blank ids and repeated
    /// ids (the first one wins) are removed; delay and volume are clamped; ids and names are trimmed. Returns true when
    /// anything changed. Never drops a valid entry.
    /// </summary>
    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool changed = false;
        if (settings.MirrorTargets is null)
        {
            settings.MirrorTargets = new List<MirrorTarget>();
            return true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<MirrorTarget>(settings.MirrorTargets.Count);
        foreach (var target in settings.MirrorTargets)
        {
            string? id = target?.DeviceId?.Trim();
            if (target is null || string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                changed = true;
                continue;
            }

            if (!string.Equals(id, target.DeviceId, StringComparison.Ordinal))
            {
                target.DeviceId = id;
                changed = true;
            }

            string? name = string.IsNullOrWhiteSpace(target.DeviceName) ? null : target.DeviceName.Trim();
            if (!string.Equals(name, target.DeviceName, StringComparison.Ordinal))
            {
                target.DeviceName = name;
                changed = true;
            }

            int delay = ClampDelay(target.DelayMs);
            int volume = ClampVolume(target.VolumePercent);
            if (delay != target.DelayMs || volume != target.VolumePercent)
            {
                target.DelayMs = delay;
                target.VolumePercent = volume;
                changed = true;
            }

            kept.Add(target);
        }

        if (changed)
        {
            settings.MirrorTargets.Clear();
            settings.MirrorTargets.AddRange(kept);
        }

        return changed;
    }

    /// <summary>
    /// The stored entry for <paramref name="deviceId"/>, added (enabled, 0 ms, 100 %) when there is none; the name is
    /// refreshed when given. For the UI's checkboxes.
    /// </summary>
    public static MirrorTarget GetOrAdd(AppSettings settings, string deviceId, string? deviceName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        settings.MirrorTargets ??= new List<MirrorTarget>();
        var existing = Find(settings, deviceId);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                existing.DeviceName = deviceName;
            }

            return existing;
        }

        var added = new MirrorTarget { DeviceId = deviceId.Trim(), DeviceName = deviceName, Enabled = true };
        settings.MirrorTargets.Add(added);
        return added;
    }

    /// <summary>The stored entry for <paramref name="deviceId"/>, or null.</summary>
    public static MirrorTarget? Find(AppSettings settings, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return deviceId is null ? null : settings.MirrorTargets?.FirstOrDefault(t => t is not null && SameId(t.DeviceId, deviceId));
    }

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
