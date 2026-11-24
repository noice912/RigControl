using static RigControl.Core.Conflicts.ConflictModules;
using static RigControl.Core.Conflicts.ConflictSeverity;

namespace RigControl.Core.Conflicts;

/// <summary>
/// Every program and Windows setting RigControl knows can get in its way: one entry per rival and feature area.
/// This is data only. To add a rival, add one line to the right list below.
///
/// Rules for this list:
///  - "verified" is true only when the names were checked on a real install or RigControl's own code already uses them.
///    Everything else comes from general knowledge and is shown as "not yet confirmed".
///  - A fix may only point at a button the app already has, and only on a verified entry.
///  - Security settings, VPNs and NZXT CAM are explain-only: they never get a fix.
/// </summary>
public static class ConflictCatalog
{
    /// <summary>The RPC methods a fix is allowed to point at. Each is an action the app already offers on a button.</summary>
    public static readonly IReadOnlyList<string> AllowedFixMethods = new[]
    {
        "lighting.takeOverGcc",        // Lighting page: "Take over" from GIGABYTE Control Center
        "sensors.closeArmouryCrate",   // Cooling page: "Take over from Armoury Crate"
        "overlay.setSettings",         // Games page: "Stop the logo button opening Game Bar"
    };

    private static readonly ConflictFix TakeOverGcc = new(
        "Take over from GIGABYTE Control Center", "lighting.takeOverGcc", null,
        "This closes GIGABYTE Control Center and stops it starting with Windows. You can give control back on the Lighting page.");

    private static readonly ConflictFix TakeOverArmoury = new(
        "Take over from Armoury Crate", "sensors.closeArmouryCrate", null,
        "This closes Armoury Crate and stops its background services until the next restart.");

    private static readonly ConflictFix GameBarButtonOff = new(
        "Stop the logo button opening Game Bar", "overlay.setSettings",
        new Dictionary<string, object> { ["gameBarButtonOff"] = true },
        "This changes a Windows Game Bar setting for your account. Untick the box on the Games page to put it back.");

    // ------------------------------------------------------------------ wording shared by many entries

    private const string LightsBreak = "It also sets the PC's lights, so colours can flicker or switch back.";
    private static string LightsDo(string name) => $"Turn off lighting control in {name}, or close it.";

    private const string FansBreak = "It also talks to the motherboard's sensor chip and can set fan speeds, so readings can glitch and fans may not follow RigControl.";
    private static string FansDo(string name) => $"Use one program for fan control. Turn fan control off in {name}, or close it.";

    private const string SensorsBreak = "It reads the same sensor chip at the same time, which can cause wrong readings now and then.";
    private static string SensorsDo(string name) => $"If readings jump or fans misbehave, close {name}.";

    private const string GpuBreak = "It also sets the graphics card's clocks, power limit or fans, and can undo what RigControl sets.";
    private static string GpuDo(string name) => $"Use one program for graphics card tuning. Reset the tuning in {name}, or close it.";

    private const string RecordBreak = "It can record the screen at the same time as RigControl, which costs performance and can react to the same shortcuts.";
    private static string RecordDo(string name) => $"Turn off recording and instant replay in {name}, or turn them off in RigControl.";

    private const string SoundBreak = "It changes the sound on the same devices, so the equaliser can be applied twice or not at all, and the default device can change.";
    private static string SoundDo(string name) => $"If the sound is wrong, turn off the effects in {name} or close it, then pick your device again on the Audio page.";

    private const string CardBreak = "It also posts a status card to Discord, so two cards can show on your profile.";
    private static string CardDo(string name) => $"Close {name}, or turn Discord status off in RigControl.";

    // ------------------------------------------------------------------ small builders

    private static ConflictEntry App(string id, string module, string name, ConflictSeverity severity, string breaks, string todo,
        string[] processes, string[]? services = null, string[]? tasks = null, string[]? drivers = null,
        bool verified = false, ConflictFix? fix = null, bool explainOnly = false, ConflictKind? kind = null, bool thisUserOnly = false)
        => new(id, module, name,
            kind ?? (processes.Length > 0 ? ConflictKind.Process : services is { Length: > 0 } ? ConflictKind.Service
                : drivers is { Length: > 0 } ? ConflictKind.Driver : ConflictKind.Task),
            new ConflictDetect
            {
                Processes = processes,
                Services = services ?? Array.Empty<string>(),
                Tasks = tasks ?? Array.Empty<string>(),
                Drivers = drivers ?? Array.Empty<string>(),
                ThisUserOnly = thisUserOnly,
            },
            severity, breaks, todo, verified, fix, explainOnly);

    private static ConflictEntry Setting(string id, string module, string name, ConflictSeverity severity, string breaks, string todo,
        RegistryTest[] registry, bool verified = false, ConflictFix? fix = null, bool explainOnly = false)
        => new(id, module, name, ConflictKind.Setting, new ConflictDetect { Registry = registry },
            severity, breaks, todo, verified, fix, explainOnly);

    private static ConflictEntry Fact(string id, string name, ConflictSeverity severity, string breaks, string todo, string fact,
        bool verified = false)
        => new(id, ConflictModules.System, name, ConflictKind.Setting, new ConflictDetect { Facts = new[] { fact } },
            severity, breaks, todo, verified, null, ExplainOnly: true);

    private static RegistryTest User(string key, string value, RegistryCheck check, long expected = 0)
        => new(RegistryRoot.CurrentUser, key, value, check, expected);

    private static RegistryTest Machine(string key, string value, RegistryCheck check, long expected = 0)
        => new(RegistryRoot.LocalMachine, key, value, check, expected);

    private static string[] P(params string[] names) => names;

    // ------------------------------------------------------------------ registry places

    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameDvrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    /// <summary>The folder RigControl keeps its own OpenRGB in (see RigControl.Bundled).</summary>
    public const string OwnOpenRgbFolder = @"\RigControl\OpenRGB\";

    // ------------------------------------------------------------------ the names RigControl's own modules already use
    // Keep these the same as the lists in the modules (see the tests in RigControl.Core.Tests/Conflicts).

    /// <summary>Same programs as LightingModule.Rivals (process name, shown name).</summary>
    public static readonly IReadOnlyList<(string Process, string Name)> LightingRivals = new[]
    {
        ("LightingService", "Armoury Crate lighting"), ("ArmouryCrate", "Armoury Crate"), ("NZXT CAM", "NZXT CAM"),
        ("GCC", "GIGABYTE Control Center"), ("RazerAppEngine", "Razer Synapse"), ("Razer Synapse 3", "Razer Synapse"),
        ("iCUE", "Corsair iCUE"), ("SignalRgb", "SignalRGB"),
    };

    /// <summary>Same programs as LightingModule.Streamers: they show the capture card themselves.</summary>
    public static readonly IReadOnlyList<string> Streamers = new[]
    {
        "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core",
    };

    /// <summary>Same as NzxtCam.AppProcesses and its service name (RigControl.Cooling).</summary>
    public static readonly IReadOnlyList<string> NzxtCamProcesses = new[] { "NZXT CAM", "cam_helper" };
    public const string NzxtCamService = "CAMService";

    /// <summary>Same as GigabyteControlCenter (RigControl.Lighting).</summary>
    public const string GccProcess = "GCC";
    public const string GccTask = @"\GCC";

    /// <summary>Same as AsusArmouryCrate.Programs / Services and its task folder (RigControl.Sensors).</summary>
    public static readonly IReadOnlyList<string> ArmouryProcesses = new[]
    {
        "ArmouryCrate", "ArmouryCrate.UserSessionHelper", "ArmouryCrate.Service", "ArmourySocketServer", "ArmourySwAgent",
        "ArmouryHtmlDebugServer", "asus_framework", "AcPowerNotification", "LightingService", "ROGLiveService",
    };
    public static readonly IReadOnlyList<string> ArmouryServices = new[]
    {
        "ArmouryCrateService", "LightingService", "asComSvc", "AsusFanControlService", "ROG Live Service", "AsusCertService",
    };
    public const string ArmouryTaskFolder = @"\ASUS\";

    /// <summary>
    /// Same program as PresenceModule.IsRichPresenceRunning (RigControl.Presence). Like that check, the entry only
    /// counts a copy in this Windows user's own session.
    /// </summary>
    public const string RichPresenceProcess = "RichPresence";

    // ------------------------------------------------------------------ the catalog

    public static readonly IReadOnlyList<ConflictEntry> All = Build();

    /// <summary>The entries for one feature area.</summary>
    public static IEnumerable<ConflictEntry> For(string module)
        => All.Where(e => string.Equals(e.Module, module, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ConflictEntry> Build()
    {
        var list = new List<ConflictEntry>();
        list.AddRange(LightingEntries());
        list.AddRange(CoolingEntries());
        list.AddRange(SensorEntries());
        list.AddRange(NvidiaEntries());
        list.AddRange(CaptureEntries());
        list.AddRange(AudioEntries());
        list.AddRange(PresenceEntries());
        list.AddRange(OverlayEntries());
        list.AddRange(DriverEntries());
        list.AddRange(BiosEntries());
        list.AddRange(SystemEntries());
        return list;
    }

    private static IEnumerable<ConflictEntry> LightingEntries()
    {
        yield return App("lighting.armoury-crate", Lighting, "Armoury Crate", Warn, LightsBreak,
            "Turn off Aura lighting in Armoury Crate, or use Take over from Armoury Crate on the Cooling page.",
            P("ArmouryCrate", "LightingService"), services: P("LightingService"), verified: true);
        yield return App("lighting.nzxt-cam", Lighting, "NZXT CAM", Warn, LightsBreak,
            "Turn off lighting in NZXT CAM, or close it yourself. RigControl never closes NZXT CAM by itself.",
            P("NZXT CAM"), verified: true, explainOnly: true);
        yield return App("lighting.gcc", Lighting, "GIGABYTE Control Center", Warn,
            "It also sets the graphics card's lights, and two programs writing at once can lock the card's light chip.",
            "Use Take over on the Lighting page, or leave the graphics card's lights to GIGABYTE Control Center.",
            P(GccProcess), tasks: P(GccTask), verified: true, fix: TakeOverGcc);
        yield return App("lighting.razer-synapse", Lighting, "Razer Synapse", Warn, LightsBreak, LightsDo("Razer Synapse"),
            P("RazerAppEngine", "Razer Synapse 3"), verified: true);
        yield return App("lighting.razer-chroma", Lighting, "Razer Chroma", Warn,
            "Razer's Chroma service keeps setting Razer lights even when Synapse is closed.",
            "Turn off Chroma effects in Razer Synapse, or close Razer's apps.",
            P("RazerChromaSDKServer", "Razer Chroma SDK Server"), services: P("Razer Chroma SDK Service", "Razer Chroma SDK Server"));
        yield return App("lighting.icue", Lighting, "Corsair iCUE", Warn, LightsBreak, LightsDo("iCUE"),
            P("iCUE"), verified: true);
        yield return App("lighting.corsair-service", Lighting, "Corsair background service", Info,
            "Corsair's service can keep setting Corsair lights after iCUE's window is closed.",
            "If Corsair lights keep switching back, quit iCUE from its tray icon.",
            P("Corsair.Service", "CueLLAccessService"), services: P("CorsairService", "CorsairLLAService"));
        yield return App("lighting.signalrgb", Lighting, "SignalRGB", Warn, LightsBreak, LightsDo("SignalRGB"),
            P("SignalRgb"), verified: true);
        yield return App("lighting.msi-center", Lighting, "MSI Center (Mystic Light)", Warn, LightsBreak, LightsDo("MSI Center"),
            P("MSI Center", "MSI.CentralServer", "MysticLight", "LEDKeeper2"), services: P("MSI Central Service", "Mystic_Light_Service"));
        yield return App("lighting.rgb-fusion", Lighting, "GIGABYTE RGB Fusion", Warn, LightsBreak, LightsDo("RGB Fusion"),
            P("RGBFusion", "SelLedV2"));
        yield return App("lighting.asrock-polychrome", Lighting, "ASRock Polychrome", Warn, LightsBreak, LightsDo("ASRock Polychrome"),
            P("AsrPolychromeRGB", "ASRRGBLED"));
        yield return App("lighting.jacknet", Lighting, "JackNet RGB Sync", Warn, LightsBreak, LightsDo("JackNet RGB Sync"),
            P("JackNet RGB Sync"));
        yield return App("lighting.hue-sync", Lighting, "Philips Hue Sync", Info,
            "It also sets the room lights, so they can jump between its colours and RigControl's.",
            "Stop syncing in Hue Sync while RigControl's lights follow the screen.",
            P("HueSync", "Hue Sync"));
        yield return new ConflictEntry("lighting.openrgb-other", Lighting, "Another copy of OpenRGB", ConflictKind.Process,
            new ConflictDetect { Processes = P("OpenRGB"), ProcessPathMustNotContain = OwnOpenRgbFolder },
            Info,
            "An OpenRGB that RigControl did not install is running. RigControl uses whichever one answers, and two copies can fight over the lights.",
            "If lights flicker or devices are missing, close the other OpenRGB and press Reconnect on the Lighting page.",
            Verified: true);
        yield return App("lighting.streaming-app", Lighting, "OBS or another streaming app", Info,
            "Only one program can open the capture card. While a streaming app runs, RigControl leaves the card to it.",
            "Nothing to do with OBS: light sync reads the picture from OBS instead (the WebSocket server in OBS must be on).",
            Streamers.ToArray(), verified: true);
        yield return Setting("lighting.dynamic-lighting", Lighting, "Windows Dynamic Lighting", Info,
            "Windows can take over keyboards, mice and other devices that support Dynamic Lighting.",
            "If a device ignores RigControl's colours, open Settings > Personalisation > Dynamic Lighting and turn it off for that device.",
            new[] { User(@"Software\Microsoft\Lighting", "AmbientLightingEnabled", RegistryCheck.Is, 1) }, verified: true);
    }

    private static IEnumerable<ConflictEntry> CoolingEntries()
    {
        yield return App("cooling.nzxt-cam", Cooling, "NZXT CAM", Blocks,
            "NZXT CAM is controlling the cooler, so RigControl only shows it and can't change pump, fans or the screen.",
            "Use Take over from NZXT CAM on the Cooling page, or close NZXT CAM yourself. RigControl never closes it by itself.",
            NzxtCamProcesses.ToArray(), services: P(NzxtCamService), verified: true, explainOnly: true);
        yield return App("cooling.signalrgb", Cooling, "SignalRGB", Warn,
            "It can open NZXT coolers and fan controllers too, which can stop RigControl reaching them.",
            "Turn off the NZXT devices in SignalRGB, or close it.",
            P("SignalRgb"), verified: true);
    }

    private static IEnumerable<ConflictEntry> SensorEntries()
    {
        yield return App("sensors.armoury-crate", Sensors, "Armoury Crate", Warn, FansBreak,
            "Use Take over from Armoury Crate on the Cooling page, or turn off fan control in Armoury Crate.",
            ArmouryProcesses.ToArray(), services: ArmouryServices.ToArray(),
            verified: true, fix: TakeOverArmoury);
        // The take-over button above only closes what is running. What starts again at the next sign-in is its own
        // line with no button: other ASUS tools keep tasks in the same folder, so it is not stated as fact.
        yield return App("sensors.asus-startup-tasks", Sensors, "ASUS start-up tasks", Info,
            "ASUS programs have start-up tasks switched on. If Armoury Crate is installed, they start it again when you sign in.",
            "Nothing to do if fans and readings are fine. To stop Armoury Crate coming back, tick 'Also stop Armoury Crate starting with Windows' when you take over on the Cooling page.",
            P(), tasks: P(ArmouryTaskFolder), explainOnly: true);
        yield return App("sensors.fancontrol", Sensors, "FanControl", Warn, FansBreak, FansDo("FanControl"), P("FanControl"));
        yield return App("sensors.argus-monitor", Sensors, "Argus Monitor", Warn, FansBreak, FansDo("Argus Monitor"),
            P("ArgusMonitor"), services: P("ArgusMonitorService"));
        yield return App("sensors.speedfan", Sensors, "SpeedFan", Warn, FansBreak, FansDo("SpeedFan"), P("speedfan"));
        yield return App("sensors.afterburner", Sensors, "MSI Afterburner", Info, SensorsBreak, SensorsDo("MSI Afterburner"),
            P("MSIAfterburner", "RTSS"));
        yield return App("sensors.hwinfo", Sensors, "HWiNFO", Info, SensorsBreak, SensorsDo("HWiNFO"),
            P("HWiNFO64", "HWiNFO32", "HWiNFO"));
        yield return App("sensors.aida64", Sensors, "AIDA64", Info, SensorsBreak, SensorsDo("AIDA64"), P("aida64"));
        yield return App("sensors.hardware-monitor", Sensors, "Another hardware monitor", Info, SensorsBreak,
            SensorsDo("the other hardware monitor"),
            P("LibreHardwareMonitor", "OpenHardwareMonitor", "HWMonitor", "HWMonitorPro"));
        yield return App("sensors.icue", Sensors, "Corsair iCUE", Info,
            "It reads sensors and runs Corsair fans and pumps itself.",
            "Leave Corsair fans to iCUE, or close it if readings jump.",
            P("iCUE"), verified: true);
        yield return App("sensors.msi-center", Sensors, "MSI Center", Warn, FansBreak, FansDo("MSI Center"),
            P("MSI Center", "MSI.CentralServer"), services: P("MSI Central Service"));
        yield return App("sensors.winring0", Sensors, "Old WinRing0 driver", Info,
            "Another program loaded the old WinRing0 driver to reach the same chips. Windows flags that driver as unsafe, and two programs on one chip can cause wrong readings.",
            "Find the program that uses it (often an older monitoring or lighting tool) and update or close it.",
            Array.Empty<string>(), drivers: P("WinRing0_1_2_0", "WinRing0x64", "WinRing0"));
    }

    private static IEnumerable<ConflictEntry> NvidiaEntries()
    {
        yield return App("nvidia.afterburner", Nvidia, "MSI Afterburner", Warn, GpuBreak, GpuDo("MSI Afterburner"), P("MSIAfterburner"));
        yield return App("nvidia.gcc", Nvidia, "GIGABYTE Control Center", Warn, GpuBreak,
            "Use Take over on the Lighting page, or do the graphics card tuning in GIGABYTE Control Center only.",
            P(GccProcess), verified: true, fix: TakeOverGcc);
        yield return App("nvidia.nvidia-app", Nvidia, "NVIDIA app", Info,
            "Its automatic tuning sets the graphics card's clocks itself and can replace RigControl's.",
            "In the NVIDIA app, turn off Automatic Tuning under System > Performance if you tune the card in RigControl.",
            P("NVIDIA app", "NVIDIA GeForce Experience"));
        yield return App("nvidia.gpu-tweak", Nvidia, "ASUS GPU Tweak", Warn, GpuBreak, GpuDo("GPU Tweak"),
            P("GPUTweakIII", "GPUTweakII", "GPU Tweak III"));
        yield return App("nvidia.evga-precision", Nvidia, "EVGA Precision", Warn, GpuBreak, GpuDo("EVGA Precision"),
            P("PrecisionX_x64", "PrecisionX1"));
        yield return App("nvidia.firestorm", Nvidia, "ZOTAC FireStorm", Warn, GpuBreak, GpuDo("FireStorm"), P("FireStorm"));
        yield return App("nvidia.thundermaster", Nvidia, "Palit ThunderMaster", Warn, GpuBreak, GpuDo("ThunderMaster"),
            P("ThunderMaster", "TMGPU"));
        yield return App("nvidia.fancontrol", Nvidia, "FanControl", Info,
            "It can run the graphics card's fans too, so two fan curves can fight.",
            "Set the graphics card's fans in one program only.",
            P("FanControl"));
    }

    private static IEnumerable<ConflictEntry> CaptureEntries()
    {
        var captureOn = User(GameDvrKey, "AppCaptureEnabled", RegistryCheck.IsNot, 0);
        var policyAllows = Machine(GameDvrPolicyKey, "AllowGameDVR", RegistryCheck.IsNot, 0);

        yield return Setting("capture.game-bar-background", Capture, "Xbox Game Bar background recording", Warn,
            "Windows is also recording in the background all the time, next to RigControl's instant replay. That costs performance twice.",
            "Open Settings > Gaming > Captures and turn off 'Record what happened', or turn off instant replay in RigControl.",
            new[] { User(GameDvrKey, "HistoricalCaptureEnabled", RegistryCheck.Is, 1), captureOn, policyAllows }, verified: true);
        yield return Setting("capture.game-bar", Capture, "Xbox Game Bar recording", Info,
            "Xbox Game Bar can record too (Win+Alt+R and Win+Alt+G). That is fine unless both record at once.",
            "Nothing to do. To switch it off, open Settings > Gaming > Captures.",
            new[] { captureOn, User(@"System\GameConfigStore", "GameDVR_Enabled", RegistryCheck.IsNot, 0), policyAllows }, verified: true);
        yield return App("capture.shadowplay", Capture, "NVIDIA overlay (Instant Replay)", Warn,
            "NVIDIA's overlay uses Alt+F9, Alt+F10 and Alt+R too, the same shortcuts as RigControl's recording, so one key press can start two recordings.",
            "Change the shortcuts in the NVIDIA app or in RigControl, and use instant replay in only one of them.",
            P("nvsphelper64", "NVIDIA Share", "NVIDIA Overlay"));
        yield return App("capture.amd-relive", Capture, "AMD Software (ReLive)", Info, RecordBreak, RecordDo("AMD Software"),
            P("RadeonSoftware", "AMDRSServ"));
        yield return App("capture.obs", Capture, "OBS", Info,
            "If OBS is recording or its replay buffer is on, two recorders run at once.",
            "Nothing to do while streaming. For clips, use the replay buffer in OBS or instant replay in RigControl, not both.",
            P("obs64", "obs32", "obs"), verified: true);
        yield return App("capture.medal", Capture, "Medal", Warn, RecordBreak, RecordDo("Medal"), P("Medal", "MedalEncoder"));
        yield return App("capture.overwolf", Capture, "Overwolf (Outplayed)", Warn, RecordBreak, RecordDo("Outplayed"),
            P("Overwolf", "OverwolfBrowser", "Outplayed"));
        yield return App("capture.steam-recording", Capture, "Steam game recording", Info,
            "Steam can record games in the background too. RigControl can't see whether that is switched on.",
            "In Steam, open Settings > Game Recording and choose Recording Off if you clip with RigControl.",
            P("steam"), verified: true);
    }

    private static IEnumerable<ConflictEntry> AudioEntries()
    {
        yield return App("audio.voicemeeter", Audio, "Voicemeeter", Warn, SoundBreak, SoundDo("Voicemeeter"),
            P("voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64", "voicemeeter_x64", "voicemeeterpro_x64"));
        yield return App("audio.sonar", Audio, "SteelSeries Sonar", Warn, SoundBreak, SoundDo("SteelSeries GG"),
            P("SteelSeriesSonar", "SteelSeriesGG"));
        yield return App("audio.nahimic", Audio, "Nahimic", Warn, SoundBreak, SoundDo("Nahimic"),
            P("NahimicSvc64", "NahimicSvc32", "Nahimic3"), services: P("NahimicService", "Nahimic_Service"));
        yield return App("audio.dolby", Audio, "Dolby audio", Info, SoundBreak, SoundDo("Dolby Access"),
            P("DolbyAccess", "DolbyDAX2API"), services: P("DolbyDAXAPI"));
        yield return App("audio.dts", Audio, "DTS audio", Info, SoundBreak, SoundDo("DTS Sound Unbound"),
            Array.Empty<string>(), services: P("DtsApo4Service"));
        yield return App("audio.fxsound", Audio, "FxSound", Warn, SoundBreak, SoundDo("FxSound"), P("FxSound"));
        yield return App("audio.nvidia-broadcast", Audio, "NVIDIA Broadcast", Info,
            "It adds its own microphone and speaker devices, and Windows may make them the default.",
            "On the Audio page, check that the microphone and speakers you want are chosen.",
            P("NVIDIA Broadcast", "NVIDIA Broadcast UI"));
        yield return App("audio.peace", Audio, "Peace equaliser", Warn,
            "Peace writes the same Equalizer APO settings file as RigControl, so two equalisers can apply at once.",
            "Use one equaliser: turn Peace off, or turn off the equaliser in RigControl.",
            P("Peace"));
    }

    private static IEnumerable<ConflictEntry> PresenceEntries()
    {
        yield return App("presence.richpresence", Presence, "RichPresence", Blocks,
            "RichPresence is running, so RigControl's Discord status waits. That way two cards never show together.",
            "Close RichPresence. RigControl's status then carries on by itself.",
            P(RichPresenceProcess), verified: true, thisUserOnly: true);
        yield return App("presence.premid", Presence, "PreMiD", Warn, CardBreak, CardDo("PreMiD"), P("PreMiD"), thisUserOnly: true);
        yield return App("presence.other-tools", Presence, "Another Discord status tool", Warn, CardBreak, CardDo("the other status tool"),
            P("EasyRP", "CustomRP", "Music Presence"), thisUserOnly: true);
    }

    private static IEnumerable<ConflictEntry> OverlayEntries()
    {
        yield return Setting("overlay.game-bar-logo-button", Overlay, "Xbox Game Bar on the controller's logo button", Info,
            "Windows opens Xbox Game Bar when you press the controller's logo button, so it opens together with RigControl's panel.",
            "Tick 'Stop the logo button opening Game Bar' in the panel settings on the Games page. Win+G still opens Game Bar.",
            new[] { User(GameBarKey, "UseNexusForGameBarEnabled", RegistryCheck.IsNot, 0) }, verified: true, fix: GameBarButtonOff);
        yield return App("overlay.steam", Overlay, "Steam (Big Picture and Steam Input)", Info,
            "Steam can also open on the controller's logo button and can remap the controller.",
            "In Steam, open Settings > Controller and turn off 'Guide button focuses Steam' if both open at once.",
            P("steam"), verified: true, thisUserOnly: true);
        yield return App("overlay.ds4windows", Overlay, "DS4Windows", Warn,
            "It can hide the real controller from other programs and show a pretend Xbox controller instead, so RigControl may see no controller or two.",
            "Close DS4Windows, or turn off 'Hide DS4 Controller' in its settings.",
            P("DS4Windows"), drivers: P("HidHide"), thisUserOnly: true);
        yield return App("overlay.dsx", Overlay, "DSX", Warn,
            "It takes over the DualSense controller, so the logo button and controller navigation may not reach RigControl's panel.",
            "Close DSX while you use RigControl's panel with the controller.",
            P("DSX"), thisUserOnly: true);
        yield return App("overlay.rewasd", Overlay, "reWASD", Warn,
            "It remaps and can hide the controller, so buttons may not reach RigControl's panel.",
            "Turn off the remap in reWASD, or close it.",
            P("reWASD", "reWASDTray"), services: P("reWASDService"), thisUserOnly: true);
        yield return App("overlay.discord", Overlay, "Discord overlay", Info,
            "Discord has its own in-game overlay. It only matters if you see two overlays on top of each other.",
            "Nothing to do. To switch it off: Discord > User Settings > Game Overlay.",
            P("Discord"), verified: true, thisUserOnly: true);
        yield return App("overlay.nvidia-overlay", Overlay, "NVIDIA overlay", Info,
            "NVIDIA's overlay opens on Alt+Z and uses other Alt shortcuts that RigControl may use too.",
            "If a shortcut opens both, change it in the NVIDIA app or in RigControl.",
            P("NVIDIA Overlay", "NVIDIA Share"), thisUserOnly: true);
    }

    private static IEnumerable<ConflictEntry> DriverEntries()
    {
        yield return App("drivers.armoury-crate", Drivers, "Armoury Crate", Info,
            "It installs driver updates as well, so a driver can change without RigControl knowing.",
            "Install drivers from one place. Turn off automatic updates in Armoury Crate if you use RigControl for them.",
            P("ArmouryCrate"), verified: true);
        yield return App("drivers.nvidia-app", Drivers, "NVIDIA app", Info,
            "It downloads graphics drivers itself, so a driver can be installed twice or changed without RigControl knowing.",
            "Install graphics drivers from one place: the NVIDIA app or RigControl.",
            P("NVIDIA app", "NVIDIA GeForce Experience"));
        yield return App("drivers.intel-dsa", Drivers, "Intel Driver & Support Assistant", Info,
            "It installs Intel drivers itself.",
            "Install drivers from one place.",
            P("DSATray"), services: P("DSAService"));
        yield return Setting("drivers.windows-update", Drivers, "Windows Update installs drivers", Info,
            "Windows Update may replace a driver you installed with its own version.",
            "Nothing to do. If a driver keeps changing back, check Windows Update > Advanced options > Optional updates.",
            new[]
            {
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", RegistryCheck.IsNot, 1),
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", RegistryCheck.IsNot, 0),
            });
    }

    private static IEnumerable<ConflictEntry> BiosEntries()
    {
        yield return App("bios.xtu", Bios, "Intel XTU", Info,
            "It sets processor power limits and voltage from Windows, so what the CPU page shows from the BIOS may not be what is in use.",
            "Nothing to do. Just know that XTU's settings win while it runs.",
            P("XtuService", "XtuUiLauncher"), services: P("XTU3SERVICE"));
        yield return App("bios.throttlestop", Bios, "ThrottleStop", Info,
            "It sets processor power limits and voltage from Windows, so what the CPU page shows from the BIOS may not be what is in use.",
            "Nothing to do. Just know that ThrottleStop's settings win while it runs.",
            P("ThrottleStop"));
    }

    private static IEnumerable<ConflictEntry> SystemEntries()
    {
        yield return Fact("system.not-elevated", "RigControl is not running as administrator", Warn,
            "Sensors, fan control, some lights and the take-over buttons need administrator rights.",
            "Close RigControl, right-click it and choose Run as administrator.",
            ConflictFacts.NotElevated, verified: true);
        yield return Fact("system.other-session", "Another Windows user is also running RigControl", Warn,
            "Two copies of RigControl set the same fans and lights, so they can undo each other.",
            "Close RigControl in the other Windows account, or sign that account out.",
            ConflictFacts.OtherSessionRunsRigControl, verified: true);
        // Info, not a warning: it is said once in the setup where the choice is made, and an install that is
        // already there (often one person's own PC) should not carry a warning count for ever.
        yield return Fact("system.program-folder-open", "RigControl's folder can be changed by any program", Info,
            "RigControl starts as administrator when you sign in, from a folder that programs without administrator rights can change. Such a program could replace RigControl's files and get administrator rights that way.",
            "Close RigControl, move its folder to one only administrators can change (for example inside C:\\Program Files), start it from there and switch Start with Windows off and on again in Settings.",
            ConflictFacts.ProgramFolderOpen, verified: true);
        yield return Fact("system.pending-restart", "Windows is waiting for a restart", Warn,
            "An update or install is not finished. Drivers and helper programs may not work properly until the PC restarts.",
            "Restart the PC when it suits you.",
            ConflictFacts.PendingRestart);
        yield return Setting("system.fast-startup", ConflictModules.System, "Windows Fast Startup", Info,
            "With Fast Startup, 'Shut down' does not fully restart Windows. A stuck light chip or an old device list can carry over to the next start.",
            "If lights or devices act up after a shut down, choose Restart instead. You can turn Fast Startup off in Control Panel > Power Options > Choose what the power buttons do.",
            new[] { Machine(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", RegistryCheck.Is, 1) },
            verified: true, explainOnly: true);
        yield return Setting("system.memory-integrity", ConflictModules.System, "Memory integrity (Core isolation)", Info,
            "Windows blocks older low-level drivers. RigControl's own driver is allowed, so sensors and fans keep working. Tools that lower the processor's voltage from Windows can't work while it is on.",
            "Nothing to do. Leave it on: it protects the PC. RigControl never changes this.",
            new[] { Machine(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", RegistryCheck.Is, 1) },
            verified: true, explainOnly: true);
        yield return Setting("system.driver-blocklist", ConflictModules.System, "Microsoft vulnerable driver blocklist", Info,
            "Windows refuses to load drivers known to be unsafe, such as the old WinRing0 driver that some monitoring and lighting tools use. RigControl does not use those.",
            "Nothing to do. Leave it on. RigControl never changes this.",
            new[] { Machine(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable", RegistryCheck.Is, 1) },
            verified: true, explainOnly: true);
        yield return Setting("system.controlled-folder-access", ConflictModules.System, "Controlled folder access", Warn,
            "Windows Security may stop RigControl saving recordings and screenshots to your Videos, Pictures or Documents folders.",
            "If saving fails, open Windows Security > Virus & threat protection > Ransomware protection > Allow an app through Controlled folder access, and add RigControl. RigControl never changes this itself.",
            new[] { Machine(@"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", "EnableControlledFolderAccess", RegistryCheck.Is, 1) },
            explainOnly: true);
        yield return Setting("system.game-mode", ConflictModules.System, "Windows Game Mode", Info,
            "While a game runs, Windows gives background programs less time. Recording and light sync can get a little less smooth.",
            "Nothing to do. If recordings stutter only in games, try turning Game Mode off in Settings > Gaming > Game Mode.",
            new[] { User(GameBarKey, "AutoGameModeEnabled", RegistryCheck.IsNot, 0) },
            verified: true, explainOnly: true);
        yield return App("system.vpn", ConflictModules.System, "VPN (NordVPN)", Info,
            "A VPN can hide devices on your home network, such as room lights, from RigControl.",
            "If those stop answering, allow local network access in the VPN's settings. RigControl never closes or changes a VPN.",
            P("NordVPN"), services: P("nordvpn-service"), verified: true, explainOnly: true);
    }
}
