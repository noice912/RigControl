using AudioController.Core.Devices;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;

namespace AudioController.Core.Routing;

/// <summary>How "EQ everything I hear" can work on this PC right now.</summary>
public enum EqAllMechanism
{
    /// <summary>Not possible: no Equalizer APO on the headset and no spare virtual playback device (or no headset).</summary>
    None,

    /// <summary>
    /// Equalizer APO is attached to the headset: the app writes its EQ into Equalizer APO's config (scoped to the
    /// headset). Works for all PC audio, needs no extra device and stays on when the app is closed.
    /// </summary>
    EqualizerApo,

    /// <summary>
    /// The System EQ router: Windows' default output is pointed at a spare virtual playback device (the inlet) and the
    /// app plays it on the headset through the EQ. Only while the app runs.
    /// </summary>
    Router,
}

/// <summary>
/// Where every stream goes, worked out from the user's two Setup picks (microphone + headset), the
/// "EQ everything I hear" switch, Equalizer APO's state and the devices Windows currently has.
/// Produced by <see cref="RoutingPlanner.Plan"/>; pure data, the app applies it.
/// </summary>
public sealed record RoutingPlan
{
    /// <summary>The real microphone the engine captures (active capture endpoint), or null.</summary>
    public string? MicInputId { get; init; }

    /// <summary>The virtual-mic playback endpoint the engine writes to (the cable's feed, "CABLE Input"), or null when missing.</summary>
    public string? VirtualMicOutputId { get; init; }

    /// <summary>The cable's recording endpoint other apps pick as their microphone ("CABLE Output", renamed or not), or null.</summary>
    public string? VirtualMicCaptureId { get; init; }

    /// <summary>Where "Hear soundboard locally" plays: the headset (or an Advanced override / the fallback).</summary>
    public string? MonitorOutputId { get; init; }

    /// <summary>
    /// The spare virtual playback device Windows' default output is pointed at while the router path runs (never an
    /// endpoint of the virtual-mic cable). Null when there is none. Computed even while the router is off.
    /// </summary>
    public string? InletId { get; init; }

    /// <summary>Where the System EQ router plays the inlet's audio (the headset); null = router stopped.</summary>
    public string? RouterOutputId { get; init; }

    /// <summary>
    /// The Windows default output the app should switch to automatically right now; null = leave it alone
    /// (already right, or a change only the user should trigger, e.g. the "Fix it" button).
    /// </summary>
    public string? DesiredWindowsDefaultOutputId { get; init; }

    /// <summary>
    /// Where the Windows default output belongs in this state: the inlet while the router runs, otherwise the headset
    /// (or <see cref="FallbackOutputId"/> while the headset is missing). Used for explicit user actions
    /// (toggling the switch, picking a headset, "Fix it") and the exit restore.
    /// </summary>
    public string? TargetWindowsDefaultOutputId { get; init; }

    /// <summary>
    /// Windows' default output is on the virtual-mic cable ("CABLE Input"/"CABLE In 16ch", renamed or not): Discord
    /// hears all PC audio and the headset hears nothing. The app shows a banner with a "Fix it" button.
    /// </summary>
    public bool WindowsDefaultIsVirtualMic { get; init; }

    /// <summary>The microphone and the headset are the same gadget (same ContainerId, or same "(name)").</summary>
    public bool MicSharesHeadset { get; init; }

    /// <summary>
    /// <see cref="MicSharesHeadset"/> on a Bluetooth headset: while the mic is open the headset runs in its
    /// hands-free (call) mode — lower quality, and the headset's own EQ pauses. See <see cref="RoutingPlanner.BluetoothCallModeWarning"/>.
    /// </summary>
    public bool BluetoothCallMode { get; init; }

    /// <summary>A headset was picked but is not connected (e.g. Bluetooth off). The router stops; PC audio uses the fallback.</summary>
    public bool HeadsetMissing { get; init; }

    /// <summary>The picked headset while it is connected, else null.</summary>
    public string? HeadsetId { get; init; }

    /// <summary>
    /// A connected real (non-virtual) playback device — where PC audio goes while the headset is missing (or not
    /// picked yet), so the user is never left in silence. Null when the headset is connected.
    /// </summary>
    public string? FallbackOutputId { get; init; }

    /// <summary>The System EQ router runs: "EQ everything" is on, its mechanism is the router, the headset is connected.</summary>
    public bool RoutingActive { get; init; }

    /// <summary>The microphone or the headset has not been picked yet.</summary>
    public bool NeedsSetup { get; init; }

    // ---------------------------------------------------------------- "EQ everything I hear"

    /// <summary>How "EQ everything I hear" works right now (None = the checkbox is disabled).</summary>
    public EqAllMechanism EqAllMechanism { get; init; }

    /// <summary>
    /// The user's choice (<see cref="AppSettings.EqAllAudio"/>; while never chosen: on only when System EQ was on in an
    /// older version — never switched on just because Equalizer APO is there).
    /// </summary>
    public bool EqAllRequested { get; init; }

    /// <summary>What the checkbox shows: requested and possible.</summary>
    public bool EqAllActive => EqAllRequested && EqAllMechanism != EqAllMechanism.None;

    /// <summary>
    /// The text under the checkbox: how it works ("via Equalizer APO — …"), or why it can't (with guidance).
    /// </summary>
    public string EqAllStatusText { get; init; } = "";

    /// <summary>Equalizer APO is installed on this PC.</summary>
    public bool ApoInstalled { get; init; }

    /// <summary>Equalizer APO is attached to the connected headset.</summary>
    public bool ApoAttachedToHeadset { get; init; }

    /// <summary>
    /// Write the EQ into Equalizer APO's config for the headset (requested, mechanism = Equalizer APO). When false the
    /// app's Equalizer APO file must be neutral (or left alone while the headset is missing).
    /// </summary>
    public bool ApoActive { get; init; }

    /// <summary>
    /// Equalizer APO is installed but not attached to the (connected) headset: show
    /// <see cref="RoutingPlanner.ApoNotAttachedText"/> and the "Open Device Selector" button.
    /// </summary>
    public bool ShowApoDeviceSelectorHint { get; init; }

    /// <summary>
    /// Endpoints Equalizer APO is attached to that it should not be on: any recording endpoint (a microphone) or an
    /// endpoint of the virtual-mic cable. The app writes nothing for them (its file is scoped to the headset).
    /// </summary>
    public IReadOnlyList<string> ApoOtherAttachmentIds { get; init; } = Array.Empty<string>();

    /// <summary>One info note per <see cref="ApoOtherAttachmentIds"/> entry (<see cref="RoutingPlanner.ApoAlsoAttachedText"/>).</summary>
    public IReadOnlyList<string> ApoInfoNotes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Double-EQ guard: Equalizer APO already equalizes the headset and the soundboard monitor plays there, so the
    /// engine's in-app EQ on the MONITOR bus is bypassed (the virtual-mic bus keeps its EQ for Discord).
    /// </summary>
    public bool MonitorEqBypass { get; init; }

    /// <summary>
    /// Problems with the picks, for the Setup card. The missing virtual cable and the misrouted Windows default have
    /// their own banners and are not repeated here; neither is the Bluetooth call-mode warning (see
    /// <see cref="BluetoothCallMode"/>) nor the "EQ everything" guidance (<see cref="EqAllStatusText"/>).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>An inlet exists, so the router path could work.</summary>
    public bool InletAvailable => InletId is not null;
}

/// <summary>
/// Pure routing rules: who plays where, how "EQ everything I hear" works, and when Windows' default output should
/// move. No I/O; the same inputs always give the same plan.
/// </summary>
/// <remarks>
/// <para>Overrides from Settings → Advanced are honoured when they are valid: <see cref="AppSettings.OutputDeviceId"/>
/// (virtual-mic output), <see cref="AppSettings.MonitorDeviceId"/> (monitor), <see cref="AppSettings.SystemEqSourceDeviceId"/>
/// (inlet) and <see cref="AppSettings.SystemEqOutputDeviceId"/> (router output). The app writes the plan's
/// choices back into those fields, and picking a headset resets the monitor and router output to it.</para>
/// <para>"EQ everything I hear" prefers Equalizer APO when it is attached to the headset (no extra device, no change
/// of the Windows default output, keeps working when the app is closed); otherwise it uses the router when a spare
/// virtual playback device (the inlet) exists; otherwise it is unavailable, with guidance.</para>
/// </remarks>
public static class RoutingPlanner
{
    /// <summary>Guidance when "EQ everything" has no way to work (no Equalizer APO, no spare virtual device).</summary>
    public const string NoInletText =
        "Needs Equalizer APO (free, sourceforge.net/projects/equalizerapo): install it, tick only your headset under " +
        "Playback devices in its Device Selector, then restart the PC. (A second virtual output device such as " +
        "VB-Audio CABLE A/B also works, while this app is open.)";

    /// <summary>The mechanism text while Equalizer APO does the work.</summary>
    public const string ApoViaText = "via Equalizer APO — stays on even when this app is closed";

    /// <summary>The mechanism text while Equalizer APO could do the work but "EQ everything I hear" is unticked.</summary>
    public const string ApoAvailableText =
        "via Equalizer APO — tick to apply the EQ to all PC audio on your headset (it stays on even when this app is closed)";

    /// <summary>"EQ everything" needs the headset picked first.</summary>
    public const string PickHeadsetFirstText = "Pick your headset first.";

    /// <summary>The amber warning for a Bluetooth headset whose own mic is the microphone.</summary>
    public const string BluetoothCallModeWarning =
        "Your headset's built-in mic switches Bluetooth headphones into call mode while it is on: lower sound quality, " +
        "and the headset's own EQ (Skull-iQ / Sensory Bass) pauses. Click Mute Mic when you're not talking — or use a " +
        "separate USB mic for full quality all the time.";

    /// <summary>The misrouted-default banner text.</summary>
    public const string VirtualMicDefaultWarning =
        "Windows is sending your PC audio into the virtual microphone — Discord can hear it and your headset can't.";

    /// <summary>Asked for while the microphone or headset is unpicked.</summary>
    public const string PickDevicesText = "Pick your microphone and headset to get started";

    /// <summary>
    /// GUID_CONTAINERID_INTERNALLY_CONNECTED_DEVICE: every built-in and root-enumerated device (VB-Audio, Voicemod,
    /// even some USB audio) reports this container, so it never means "same gadget".
    /// </summary>
    public static readonly Guid InternalContainerId = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>Products other software listens to (mixers, streaming); never picked as the inlet automatically.</summary>
    private static readonly string[] BusyVirtualProducts =
    {
        "VoiceMeeter", "Steam Streaming", "NVIDIA Virtual Audio", "Oculus Virtual Audio",
    };

    /// <summary>The mechanism text while the router does the work.</summary>
    public static string RouterViaText(string inletName) =>
        $"via Audio Controller — Windows plays into \"{inletName}\" and the app passes it on to your headset (only while the app is open)";

    /// <summary>Shown (with the "Open Device Selector" button) when Equalizer APO is installed but not on the headset.</summary>
    public static string ApoNotAttachedText(string headsetName) =>
        $"Equalizer APO isn't attached to {headsetName}. Open its Device Selector, tick {headsetName} under Playback devices, then restart the PC.";

    /// <summary>Info note for an Equalizer APO attachment to a microphone or to the virtual cable.</summary>
    public static string ApoAlsoAttachedText(string deviceName) =>
        $"Equalizer APO is also attached to {deviceName}. Audio Controller keeps it neutral there; you can untick it in Device Selector.";

    // ------------------------------------------------------------------ plan

    /// <summary>Works out the routing for <paramref name="settings"/> on <paramref name="devices"/>.</summary>
    /// <param name="settings">The saved choices (not modified).</param>
    /// <param name="devices">All endpoints Windows reports (any state).</param>
    /// <param name="windowsDefaultOutputId">The current Windows default playback endpoint, or null.</param>
    /// <param name="apo">Equalizer APO's state (null = not installed).</param>
    /// <param name="windowsDefaultIsAutomatic">
    /// The current Windows default is nobody's manual choice: this app put it there, or Windows picked it after the
    /// previous default went away (<see cref="RoutingCoordinator.IsAutomaticDefault"/>). Only such a default (or one
    /// left on the router's inlet while System EQ was saved on) is moved off an inlet while routing is off.
    /// </param>
    public static RoutingPlan Plan(
        AppSettings settings, IReadOnlyList<AudioDeviceInfo> devices, string? windowsDefaultOutputId, EqualizerApoState? apo = null,
        bool windowsDefaultIsAutomatic = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        apo ??= EqualizerApoState.NotInstalled;

        var warnings = new List<string>();

        // ---- microphone (never the virtual mic itself: that would loop)
        bool micChosen = !string.IsNullOrWhiteSpace(settings.InputDeviceId);
        var mic = FindActive(devices, settings.InputDeviceId, DeviceKind.Capture);
        if (mic is not null && VirtualDeviceDetector.IsVirtualMicOutput(mic.FriendlyName))
        {
            warnings.Add($"\"{VirtualDeviceDetector.ShortCableName(mic.FriendlyName)}\" is the virtual microphone itself — pick your real microphone.");
            mic = null;
        }
        else if (micChosen && mic is null)
        {
            warnings.Add($"Your microphone \"{NameOf(devices, settings.InputDeviceId, "saved microphone")}\" isn't connected.");
        }

        // ---- headset (never the virtual-mic cable: Discord would hear everything)
        bool headsetChosen = !string.IsNullOrWhiteSpace(settings.HeadsetDeviceId);
        var headset = FindActive(devices, settings.HeadsetDeviceId, DeviceKind.Playback);
        bool headsetIsCable = headset is not null && VirtualDeviceDetector.IsVirtualMicInput(headset.FriendlyName);
        if (headsetIsCable)
        {
            warnings.Add($"\"{VirtualDeviceDetector.ShortCableName(headset!.FriendlyName)}\" is the virtual microphone's feed, not a headset — pick your headphones or speakers.");
            headset = null;
        }

        bool headsetMissing = headsetChosen && headset is null;

        // ---- virtual-mic output: a valid Advanced override, else the cable's feed (rename-proof)
        var virtualMicOut = FindActive(devices, settings.OutputDeviceId, DeviceKind.Playback);
        if (virtualMicOut is not null && SameId(virtualMicOut.Id, headset?.Id))
        {
            virtualMicOut = null;
        }

        virtualMicOut ??= FindCableInput(devices);
        var virtualMicCapture = virtualMicOut is not null && VirtualDeviceDetector.IsVirtualMicInput(virtualMicOut.FriendlyName)
            ? VirtualDeviceDetector.ChooseCableMic(devices)
            : null;

        // ---- inlet: a valid Advanced override, else the best spare virtual playback device
        var inlet = FindInlet(settings, devices, headset?.Id, virtualMicOut?.Id);

        // ---- "EQ everything I hear": Equalizer APO on the headset, else the router, else nothing
        bool apoOnHeadset = headset is not null && apo.IsAttachedTo(headset.Id);

        // Never chosen: on only for a pre-1.4 System EQ user who had opted in. Equalizer APO being attached to the
        // headset is not consent to re-EQ (and pre-amp) all PC audio, so it waits for the tick.
        bool eqAllRequested = settings.EqAllAudio ?? settings.SystemEqEnabled;
        var mechanism = headset is null ? EqAllMechanism.None
            : apoOnHeadset ? EqAllMechanism.EqualizerApo
            : inlet is not null ? EqAllMechanism.Router
            : EqAllMechanism.None;

        AudioDeviceInfo? routerOutput = null;
        if (eqAllRequested && mechanism == EqAllMechanism.Router)
        {
            var overrideOut = FindActive(devices, settings.SystemEqOutputDeviceId, DeviceKind.Playback);
            routerOutput = overrideOut is not null
                           && !VirtualDeviceDetector.IsVirtualMicInput(overrideOut.FriendlyName)
                           && !SameId(overrideOut.Id, inlet!.Id)
                           && !SameId(overrideOut.Id, virtualMicOut?.Id)
                ? overrideOut
                : headset;
        }

        bool routingActive = routerOutput is not null;
        bool apoActive = eqAllRequested && mechanism == EqAllMechanism.EqualizerApo;
        bool showApoHint = apo.Installed && headset is not null && !apoOnHeadset;

        string eqAllText = mechanism switch
        {
            EqAllMechanism.EqualizerApo => eqAllRequested ? ApoViaText : ApoAvailableText,
            EqAllMechanism.Router => RouterViaText(inlet!.FriendlyName),
            _ when !headsetChosen => PickHeadsetFirstText,
            _ when headsetMissing => "Your headset isn't connected.",
            _ when showApoHint => ApoNotAttachedText(headset!.FriendlyName),
            _ => NoInletText,
        };

        // ---- Equalizer APO on a microphone or on the virtual cable: harmless (the app's file is headset-only), but say so
        var otherIds = new List<string>();
        var notes = new List<string>();
        if (apo.Installed)
        {
            foreach (var id in apo.AttachedEndpointIds.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            {
                var known = devices.FirstOrDefault(d => SameId(d.Id, id));
                bool capture = known?.Kind == DeviceKind.Capture || (known is null && EqualizerApoConfig.IsCaptureEndpointId(id));
                bool onCable = known is not null && VirtualDeviceDetector.IsVirtualMicCable(known);
                if (!capture && !onCable)
                {
                    continue;
                }

                otherIds.Add(id);
                notes.Add(ApoAlsoAttachedText(known?.FriendlyName ?? (capture ? "a recording device" : "a playback device")));
            }
        }

        // ---- the Windows default today
        var windowsDefault = FindActive(devices, windowsDefaultOutputId, DeviceKind.Playback);
        bool defaultIsVirtualMic = windowsDefault is not null
                                   && (VirtualDeviceDetector.IsVirtualMicInput(windowsDefault.FriendlyName)
                                       || SameId(windowsDefault.Id, virtualMicOut?.Id));

        // ---- fallback while the headset is missing: never leave the user in silence
        AudioDeviceInfo? fallback = null;
        if (headset is null)
        {
            fallback = FindFallback(devices, windowsDefault, inlet?.Id, virtualMicOut?.Id);
            if (headsetMissing && !headsetIsCable)
            {
                string name = NameOf(devices, settings.HeadsetDeviceId, "saved headset");
                warnings.Add(fallback is null
                    ? $"Your headset \"{name}\" isn't connected."
                    : $"Your headset \"{name}\" isn't connected — PC audio plays on \"{fallback.FriendlyName}\" until it's back.");
            }
        }

        // ---- soundboard monitor: a valid override, else the headset (else the fallback)
        var monitor = FindActive(devices, settings.MonitorDeviceId, DeviceKind.Playback);
        if (monitor is not null
            && (VirtualDeviceDetector.IsVirtualMicInput(monitor.FriendlyName)
                || SameId(monitor.Id, virtualMicOut?.Id)
                || SameId(monitor.Id, inlet?.Id)))
        {
            monitor = null;
        }

        monitor ??= headset ?? fallback;

        // ---- where the Windows default belongs, and what to change automatically
        string? target = routingActive ? inlet!.Id : (headset ?? fallback)?.Id;
        string? desired = DesiredDefault(
            target, windowsDefault, defaultIsVirtualMic, routingActive, headsetMissing,
            inlet?.Id, settings.SystemEqSourceDeviceId, windowsDefaultIsAutomatic, settings.SystemEqEnabled);

        // ---- the headset's own mic (Bluetooth call mode)
        bool shares = mic is not null && headset is not null && SameGadget(mic, headset);

        return new RoutingPlan
        {
            MicInputId = mic?.Id,
            VirtualMicOutputId = virtualMicOut?.Id,
            VirtualMicCaptureId = virtualMicCapture?.Id,
            MonitorOutputId = monitor?.Id,
            InletId = inlet?.Id,
            RouterOutputId = routerOutput?.Id,
            DesiredWindowsDefaultOutputId = desired,
            TargetWindowsDefaultOutputId = target,
            WindowsDefaultIsVirtualMic = defaultIsVirtualMic,
            MicSharesHeadset = shares,
            BluetoothCallMode = shares && (headset!.IsBluetooth || mic!.IsBluetooth),
            HeadsetMissing = headsetMissing,
            HeadsetId = headset?.Id,
            FallbackOutputId = fallback?.Id,
            RoutingActive = routingActive,
            NeedsSetup = !micChosen || !headsetChosen,
            EqAllMechanism = mechanism,
            EqAllRequested = eqAllRequested,
            EqAllStatusText = eqAllText,
            ApoInstalled = apo.Installed,
            ApoAttachedToHeadset = apoOnHeadset,
            ApoActive = apoActive,
            ShowApoDeviceSelectorHint = showApoHint,
            ApoOtherAttachmentIds = otherIds.AsReadOnly(),
            ApoInfoNotes = notes.AsReadOnly(),
            MonitorEqBypass = apoActive && monitor is not null && SameId(monitor.Id, headset?.Id),
            Warnings = warnings.AsReadOnly(),
        };
    }

    /// <summary>
    /// The automatic default-output change (null = none):
    /// routing → the inlet; routing off (including while the headset is missing, when the target is the fallback) →
    /// the target only when the default is silent and nobody chose it: no default at all, or an inlet (the current or
    /// the saved one) that this app — or Windows, after the previous default went away — put it on, or that was left
    /// there while System EQ was saved on (a run that ended without restoring it). A default the user picked, even a
    /// spare virtual device feeding OBS, is never moved. A default on the virtual-mic cable is left for the user's
    /// "Fix it" click — except while the headset is missing, when it is always rescued (Discord would hear PC audio).
    /// </summary>
    private static string? DesiredDefault(
        string? target,
        AudioDeviceInfo? windowsDefault,
        bool defaultIsVirtualMic,
        bool routingActive,
        bool headsetMissing,
        string? inletId,
        string? previousInletId,
        bool defaultIsAutomatic,
        bool systemEqWasOn)
    {
        if (target is null || SameId(windowsDefault?.Id, target))
        {
            return null;
        }

        if (defaultIsVirtualMic)
        {
            // Headset connected: banner + "Fix it". Headset missing: always rescued onto the fallback.
            return headsetMissing ? target : null;
        }

        if (routingActive)
        {
            return target;
        }

        bool onInlet = windowsDefault is not null
                       && (SameId(windowsDefault.Id, inletId) || SameId(windowsDefault.Id, previousInletId));
        bool silentInlet = windowsDefault is null || (onInlet && (defaultIsAutomatic || systemEqWasOn));
        return silentInlet ? target : null;
    }

    // ------------------------------------------------------------------ device choices

    /// <summary>
    /// The inlet: <see cref="AppSettings.SystemEqSourceDeviceId"/> when it is a usable playback device, otherwise
    /// the best active virtual playback device — "CABLE-A", "CABLE-B", "Hi-Fi Cable", "Dummy Output", then any other
    /// (2-channel endpoints before "16ch" ones, then by name). Never an endpoint of the virtual-mic cable (renamed or
    /// not), the headset or the virtual-mic output; mixers and streaming devices other apps listen to (VoiceMeeter,
    /// Steam Streaming, …) are only used when chosen in Settings → Advanced.
    /// </summary>
    public static AudioDeviceInfo? FindInlet(AppSettings settings, IReadOnlyList<AudioDeviceInfo> devices, string? headsetId, string? virtualMicOutputId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);

        bool Usable(AudioDeviceInfo d) =>
            d.Kind == DeviceKind.Playback && d.State == DeviceActivity.Active
            && !VirtualDeviceDetector.IsVirtualMicInput(d.FriendlyName)
            && !SameId(d.Id, headsetId)
            && !SameId(d.Id, virtualMicOutputId);

        var chosen = FindActive(devices, settings.SystemEqSourceDeviceId, DeviceKind.Playback);
        if (chosen is not null && Usable(chosen))
        {
            return chosen;
        }

        return devices
            .Where(d => Usable(d) && d.IsVirtual && !IsBusyVirtualProduct(d.FriendlyName))
            .OrderBy(d => InletTier(d.FriendlyName))
            .ThenBy(d => d.FriendlyName.Contains("16ch", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// The virtual-mic cable's feed (its "CABLE Input", found rename-proof — see
    /// <see cref="VirtualDeviceDetector.ChooseCableFeed"/>), or null.
    /// </summary>
    public static AudioDeviceInfo? FindCableInput(IReadOnlyList<AudioDeviceInfo> devices, string? savedOutputId = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return VirtualDeviceDetector.ChooseCableFeed(devices, savedOutputId);
    }

    /// <summary>
    /// A real playback device to use while the headset is missing: the current Windows default when it is real,
    /// else the first active non-virtual playback device (by name). Never a virtual device, the inlet or the
    /// virtual-mic output.
    /// </summary>
    private static AudioDeviceInfo? FindFallback(IReadOnlyList<AudioDeviceInfo> devices, AudioDeviceInfo? windowsDefault, string? inletId, string? virtualMicOutputId)
    {
        bool Real(AudioDeviceInfo d) =>
            d.Kind == DeviceKind.Playback && d.State == DeviceActivity.Active && !d.IsVirtual
            && !VirtualDeviceDetector.IsVirtualMicInput(d.FriendlyName)
            && !SameId(d.Id, inletId) && !SameId(d.Id, virtualMicOutputId);

        if (windowsDefault is not null && Real(windowsDefault))
        {
            return windowsDefault;
        }

        return devices.Where(Real)
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Choices for the Setup card's Microphone list: active, real capture devices (never the virtual mic).</summary>
    public static IReadOnlyList<AudioDeviceInfo> MicrophoneChoices(IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.Where(d => d.Kind == DeviceKind.Capture && d.State == DeviceActivity.Active && !d.IsVirtual
                                  && !VirtualDeviceDetector.IsVirtualMicOutput(d.FriendlyName))
                      .ToList();
    }

    /// <summary>Choices for the Setup card's Headset list: active, real playback devices (never a cable endpoint).</summary>
    public static IReadOnlyList<AudioDeviceInfo> HeadsetChoices(IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.Where(d => d.Kind == DeviceKind.Playback && d.State == DeviceActivity.Active && !d.IsVirtual
                                  && !VirtualDeviceDetector.IsVirtualMicInput(d.FriendlyName))
                      .ToList();
    }

    // ------------------------------------------------------------------ migration

    /// <summary>
    /// One-time upgrade of pre-1.4 settings (and first-run defaults). Only acts while
    /// <see cref="AppSettings.HeadsetDeviceId"/> is null: the headset becomes the System EQ output, else the
    /// monitor device, else the Windows default output when it is real hardware, else the first active real
    /// Bluetooth playback device, else the first active real playback device. Saved choices count even while that
    /// device is disconnected. Returns true when anything changed. Pure: only <paramref name="settings"/> is modified.
    /// "EQ everything I hear" is not decided here: while <see cref="AppSettings.EqAllAudio"/> is unset the plan
    /// defaults it to on only when System EQ was on before (Equalizer APO alone waits for the user's tick).
    /// </summary>
    public static bool MigrateSettings(AppSettings settings, IReadOnlyList<AudioDeviceInfo> devices, string? windowsDefaultOutputId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);

        if (!string.IsNullOrWhiteSpace(settings.HeadsetDeviceId))
        {
            return false;
        }

        bool RealPlayback(AudioDeviceInfo d) =>
            d.Kind == DeviceKind.Playback && !d.IsVirtual
            && !VirtualDeviceDetector.IsVirtualMicInput(d.FriendlyName)
            && !SameId(d.Id, settings.OutputDeviceId);

        // Saved choices count even while that headset is disconnected (it is still the user's headset).
        string? Saved(string? id) =>
            string.IsNullOrWhiteSpace(id) ? null : devices.FirstOrDefault(d => SameId(d.Id, id) && RealPlayback(d))?.Id;

        var activeReal = devices.Where(d => d.State == DeviceActivity.Active && RealPlayback(d))
                                .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                                .ToList();

        string? pick = Saved(settings.SystemEqOutputDeviceId)
                       ?? Saved(settings.MonitorDeviceId)
                       ?? activeReal.FirstOrDefault(d => SameId(d.Id, windowsDefaultOutputId))?.Id
                       ?? activeReal.FirstOrDefault(d => d.IsBluetooth)?.Id
                       ?? activeReal.FirstOrDefault()?.Id;
        if (pick is null)
        {
            return false;
        }

        settings.HeadsetDeviceId = pick;
        return true;
    }

    // ------------------------------------------------------------------ re-paired devices

    /// <summary>
    /// Keeps the two Setup picks pointing at the user's devices across Bluetooth re-pairing. When Windows re-pairs a
    /// device it deletes the old endpoints and creates new ones with new ids, so a saved id can vanish from the device
    /// list entirely. For the microphone and the headset: when the saved id is still listed (in any state — a merely
    /// disconnected device keeps its id) the saved name is refreshed to its current name; when it is gone and exactly
    /// one active, real device of the same kind has the saved name, that device is adopted (the headset's monitor and
    /// System EQ output follow when they pointed at the old id). Ambiguous names or no saved name change nothing.
    /// The "Also play on" outputs (<see cref="AppSettings.MirrorTargets"/>) follow by name the same way (never onto the
    /// headset or onto an output another entry already uses).
    /// Returns true when anything changed. Pure: only <paramref name="settings"/> is modified.
    /// </summary>
    public static bool ReadoptRenamedIds(AppSettings settings, IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);

        bool changed = false;

        string? micId = settings.InputDeviceId, micName = settings.InputDeviceName;
        if (Readopt(devices, DeviceKind.Capture, ref micId, ref micName))
        {
            settings.InputDeviceId = micId;
            settings.InputDeviceName = micName;
            changed = true;
        }

        string? oldHeadset = settings.HeadsetDeviceId;
        string? headsetId = oldHeadset, headsetName = settings.HeadsetDeviceName;
        if (Readopt(devices, DeviceKind.Playback, ref headsetId, ref headsetName))
        {
            if (!SameId(headsetId, oldHeadset))
            {
                if (SameId(settings.MonitorDeviceId, oldHeadset)) settings.MonitorDeviceId = headsetId;
                if (SameId(settings.SystemEqOutputDeviceId, oldHeadset)) settings.SystemEqOutputDeviceId = headsetId;
            }

            settings.HeadsetDeviceId = headsetId;
            settings.HeadsetDeviceName = headsetName;
            changed = true;
        }

        // "Also play on" outputs follow the same rule (an optical / USB output re-plugged on another port, a re-paired
        // speaker). A target is never re-adopted onto the headset or onto a device another target already uses.
        if (settings.MirrorTargets is { Count: > 0 } targets)
        {
            foreach (var target in targets)
            {
                if (target is null)
                {
                    continue;
                }

                string? targetId = target.DeviceId, targetName = target.DeviceName;
                if (!Readopt(devices, DeviceKind.Playback, ref targetId, ref targetName))
                {
                    continue;
                }

                if (!SameId(targetId, target.DeviceId)
                    && (SameId(targetId, settings.HeadsetDeviceId)
                        || targets.Any(t => t is not null && !ReferenceEquals(t, target) && SameId(t.DeviceId, targetId))))
                {
                    continue;
                }

                target.DeviceId = targetId!;
                target.DeviceName = targetName;
                changed = true;
            }
        }

        return changed;
    }

    private static bool Readopt(IReadOnlyList<AudioDeviceInfo> devices, DeviceKind kind, ref string? id, ref string? name)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string savedId = id;
        var listed = devices.FirstOrDefault(d => d.Kind == kind && SameId(d.Id, savedId));
        if (listed is not null)
        {
            if (string.Equals(listed.FriendlyName, name, StringComparison.Ordinal))
            {
                return false;
            }

            name = listed.FriendlyName;
            return true;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string savedName = name;
        var matches = devices.Where(d => d.Kind == kind && d.State == DeviceActivity.Active && !d.IsVirtual
                                         && !VirtualDeviceDetector.IsOnVirtualMicCable(d.FriendlyName)
                                         && string.Equals(d.FriendlyName, savedName, StringComparison.OrdinalIgnoreCase))
                             .Take(2)
                             .ToList();
        if (matches.Count != 1)
        {
            return false;
        }

        id = matches[0].Id;
        name = matches[0].FriendlyName;
        return true;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// True when two endpoints belong to the same physical gadget: equal ContainerIds (ignoring the shared
    /// "internally connected" and empty containers), or — when either id is unknown or unusable — the same name in
    /// parentheses, e.g. "Headset (Crusher ANC 2)" and "Headphones (Crusher ANC 2)" (a trailing " Hands-Free" /
    /// " Stereo" profile word is ignored).
    /// </summary>
    public static bool SameGadget(AudioDeviceInfo a, AudioDeviceInfo b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (UsableContainer(a.ContainerId) is { } ca && UsableContainer(b.ContainerId) is { } cb)
        {
            return ca == cb;
        }

        string? na = GadgetName(ParenthesizedName(a.FriendlyName));
        string? nb = GadgetName(ParenthesizedName(b.FriendlyName));
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when both container ids parse to the same usable (not empty / not "internal") GUID.</summary>
    public static bool SameContainer(string? a, string? b) =>
        UsableContainer(a) is { } ca && UsableContainer(b) is { } cb && ca == cb;

    /// <summary>"Headset (Crusher ANC 2)" → "Crusher ANC 2"; "Line (Voicemod Virtual Audio Device (WDM))" → "Voicemod Virtual Audio Device (WDM)".</summary>
    public static string? ParenthesizedName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return null;
        }

        int open = friendlyName.IndexOf('(');
        int close = friendlyName.LastIndexOf(')');
        if (open < 0 || close <= open + 1)
        {
            return null;
        }

        string inner = friendlyName[(open + 1)..close].Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>"Crusher ANC 2 Hands-Free AG Audio" / "Crusher ANC 2 Stereo" / "Crusher ANC 2 Hands-Free" → "Crusher ANC 2".</summary>
    private static string? GadgetName(string? inner)
    {
        if (inner is null)
        {
            return null;
        }

        foreach (var suffix in new[] { " Hands-Free AG Audio", " Hands-Free", " Stereo" })
        {
            if (inner.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && inner.Length > suffix.Length)
            {
                return inner[..^suffix.Length].TrimEnd();
            }
        }

        return inner;
    }

    private static Guid? UsableContainer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id.Trim(), out var g))
        {
            return null;
        }

        return g == Guid.Empty || g == InternalContainerId ? null : g;
    }

    private static int InletTier(string name)
    {
        if (name.Contains("CABLE-A", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("CABLE-B", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("Dummy Output", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static bool IsBusyVirtualProduct(string name) =>
        BusyVirtualProducts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static AudioDeviceInfo? FindActive(IReadOnlyList<AudioDeviceInfo> devices, string? id, DeviceKind kind) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : devices.FirstOrDefault(d => d.Kind == kind && d.State == DeviceActivity.Active && SameId(d.Id, id));

    private static string NameOf(IReadOnlyList<AudioDeviceInfo> devices, string? id, string unknown) =>
        devices.FirstOrDefault(d => SameId(d.Id, id))?.FriendlyName ?? unknown;

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
