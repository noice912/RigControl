using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Bundled;
using RigControl.Core.Conflicts;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.App;

/// <summary>Settings section "firstRun": whether the first-time setup has been finished (or skipped).</summary>
public sealed class FirstRunSettings
{
    public const string Section = "firstRun";

    /// <summary>Null until RigControl has decided once whether this is a new installation.</summary>
    public bool? Done { get; set; }
}

/// <summary>
/// The one yes for all helper programs ("Install and continue"), kept where only administrators can write
/// (HKLM\SOFTWARE\RigControl\Bundled, value "allHelpers", like the BIOS original). The settings file sits in the
/// user's own folder, where any program can write, so nothing found there ever installs a driver or changes a service.
/// </summary>
public sealed class AdminConsentStore
{
    public const string DefaultKey = @"SOFTWARE\RigControl\Bundled";
    public const string AllHelpersValue = "allHelpers";

    /// <summary>The yes or no per program that an earlier development build kept here. No longer read; removed on the next save.</summary>
    private static readonly string[] OldValues = { "openrgb", "openrgbService", "pawnio", "equalizerapo" };

    private readonly Microsoft.Win32.RegistryKey _hive;
    private readonly string _key;

    public AdminConsentStore() : this(Microsoft.Win32.Registry.LocalMachine, DefaultKey) { }

    /// <summary>Tests pass a throwaway key of their own.</summary>
    public AdminConsentStore(Microsoft.Win32.RegistryKey hive, string key)
    {
        _hive = hive;
        _key = key;
    }

    /// <summary>
    /// The saved yes. Anything missing or unreadable is "no yes yet". An answer per program from an earlier
    /// development build does not count: the user sees the new screen once and says yes to all of it there.
    /// </summary>
    public BundledConsent Load()
    {
        var consent = new BundledConsent();
        try
        {
            using var k = _hive.OpenSubKey(_key);
            consent.AllHelpers = k?.GetValue(AllHelpersValue) is int n && n == 1;
        }
        catch
        {
            // no yes yet
        }
        return consent;
    }

    /// <summary>Saves the yes (or takes it away). Needs administrator rights for the real key.</summary>
    public void Save(BundledConsent consent)
    {
        using var k = _hive.CreateSubKey(_key, writable: true);
        if (consent.AllHelpers) k.SetValue(AllHelpersValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
        else k.DeleteValue(AllHelpersValue, throwOnMissingValue: false);
        foreach (var old in OldValues) k.DeleteValue(old, throwOnMissingValue: false);
    }
}

/// <summary>Where the yes for the helper programs comes from and goes to.</summary>
public static class BundledConsentSource
{
    /// <summary>
    /// Only the administrators-only store counts, whoever reads it: whatever the settings file says is ignored.
    /// A copy of RigControl without administrator rights may read the yes (to show where things stand) but can
    /// never give it.
    /// </summary>
    public static BundledConsent Load(bool elevated, AdminConsentStore admin, SettingsStore settings) => admin.Load();

    /// <summary>
    /// Saves the yes, as administrator only. Without administrator rights nothing is saved anywhere: a yes from a
    /// process that any program can talk to is not trusted, and RigControl asks again when it runs as administrator.
    /// </summary>
    public static void Save(bool elevated, AdminConsentStore admin, SettingsStore settings, BundledConsent consent)
    {
        if (elevated) admin.Save(consent);
    }
}

/// <summary>A graphics card as Windows lists it: its hardware id ("PCI\VEN_10DE&amp;DEV_2782...") and its name.</summary>
public sealed record DisplayAdapter(string? PnpId, string? Name);

/// <summary>What the first-time setup needs to know about this PC and Windows account. Read-only; replaced in tests.</summary>
public class FirstRunPc
{
    /// <summary>
    /// The graphics cards Windows has drivers set up for, read from Windows' own list (nothing is changed). The list
    /// can hold cards that are no longer in the PC, so it is only used to look up a card another part already found.
    /// </summary>
    public virtual IReadOnlyList<DisplayAdapter> DisplayAdapters()
    {
        var list = new List<DisplayAdapter>();
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return list;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var key = cls.OpenSubKey(name);
                    var id = key?.GetValue("MatchingDeviceId") as string;
                    var desc = key?.GetValue("DriverDesc") as string;
                    if (!string.IsNullOrWhiteSpace(id)) list.Add(new DisplayAdapter(id, desc));
                }
                catch { /* not readable: skip this one */ }
            }
        }
        catch { /* the names from the other parts are shown as they are */ }
        return list;
    }

    private readonly Lazy<bool> _folderOpen;

    public FirstRunPc(string executablePath)
    {
        _folderOpen = new Lazy<bool>(() => !string.IsNullOrEmpty(executablePath) && FolderGuard.OthersCanChange(executablePath));
    }

    /// <summary>How many OTHER Windows users are signed in with a desktop right now (fast user switching, remote sessions).</summary>
    public virtual int OtherUsersSignedIn()
    {
        try
        {
            int mine;
            using (var me = Process.GetCurrentProcess()) mine = me.SessionId;
            var others = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    try { if (p.SessionId != mine) others.Add(p.SessionId); }
                    catch { /* gone already */ }
                }
            }
            return others.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// False when this Windows account is a standard one: it can never run RigControl as administrator by itself,
    /// not even from the start-up task. True when it could not be worked out.
    /// </summary>
    public virtual bool AccountIsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return true;
            // "Limited" is an administrator account that Windows runs without its rights until a prompt is approved.
            return !GetTokenInformation(id.Token, TokenElevationType, out var type, sizeof(int), out _) || type == TokenElevationTypeLimited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when RigControl runs as a different Windows account than the one signed in to this desktop.</summary>
    public virtual bool RunsAsAnotherAccount() => RigControl.Presence.DesktopUser.IsAnotherAccount();

    /// <summary>True when programs without administrator rights can change RigControl's own program file or its folders.</summary>
    public virtual bool ProgramFolderOpen() => _folderOpen.Value;

    private const int TokenElevationType = 18;
    private const int TokenElevationTypeLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int needed);
}

public sealed record FirstRunHardwareDto(string Label, string Value);

public sealed record FirstRunFeatureDto(string Id, string Name, bool Available, string? Reason);

/// <summary>What "app.getFirstRun" returns: the system scan and where the first-time setup stands.</summary>
/// <param name="Helpers">The helper programs RigControl needs. While its NeedsYes is true the setup step is shown, also on a PC whose setup is done.</param>
/// <param name="RestartSuggested">A PC restart is worth offering, for one of the two reasons below.</param>
/// <param name="RestartForAutostart">"Start with Windows" was just switched on (or RigControl is not yet running as administrator).</param>
/// <param name="RestartForEqualiser">Equalizer APO was just installed; it only works after one restart.</param>
/// <param name="OtherUsersSignedIn">How many other Windows users are signed in now. A restart closes their programs too.</param>
/// <param name="AccountIsAdministrator">False for a standard Windows account, which can never start RigControl as administrator by itself.</param>
/// <param name="OtherAccount">RigControl runs as a different Windows account than the one signed in to this desktop.</param>
/// <param name="ProgramFolderOpen">Programs without administrator rights can change RigControl's own folder.</param>
public sealed record FirstRunDto(
    bool Done,
    bool Elevated,
    bool Autostart,
    bool RestartSuggested,
    IReadOnlyList<FirstRunHardwareDto> Hardware,
    IReadOnlyList<FirstRunFeatureDto> Features,
    BundledSummary Helpers,
    IReadOnlyList<ConflictFinding> Findings,
    bool RestartForAutostart = false,
    bool RestartForEqualiser = false,
    int OtherUsersSignedIn = 0,
    bool AccountIsAdministrator = true,
    bool OtherAccount = false,
    bool ProgramFolderOpen = false);

/// <summary>
/// Decides whether the first-time setup is shown. A PC that already used RigControl (its settings file was there
/// before this start) counts as set up, so nothing is asked again; only a new data folder shows the setup.
/// </summary>
public static class FirstRunState
{
    /// <summary>True when a settings file with something in it is already there.</summary>
    public static bool SettingsExist(string settingsPath)
    {
        try
        {
            var file = new FileInfo(settingsPath);
            return file.Exists && file.Length > 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The answer when nothing was recorded yet: an installation that was already in use is done.</summary>
    public static bool Decide(bool? recorded, bool settingsExistedAtStart) => recorded ?? settingsExistedAtStart;

    /// <summary>Records the decision once, at start, so later starts don't have to guess. Returns "done".</summary>
    public static bool Initialise(SettingsStore settings, bool settingsExistedAtStart, ILog log)
    {
        var recorded = settings.Get<FirstRunSettings>(FirstRunSettings.Section).Done;
        var done = Decide(recorded, settingsExistedAtStart);
        if (recorded is null)
        {
            settings.Update<FirstRunSettings>(FirstRunSettings.Section, s => s.Done = done);
            log.Info(done
                ? "RigControl was already set up on this PC: the first-time setup is not shown."
                : "This is a new RigControl folder: the first-time setup will be shown.");
        }
        return done;
    }

    public static bool IsDone(SettingsStore settings) => settings.Get<FirstRunSettings>(FirstRunSettings.Section).Done == true;

    public static void MarkDone(SettingsStore settings) =>
        settings.Update<FirstRunSettings>(FirstRunSettings.Section, s => s.Done = true);
}

/// <summary>
/// The system scan's two lists, built only from what the modules already report (no hardware is touched here).
/// </summary>
public static class FirstRunScan
{
    /// <summary>Everything the lists are built from. A module's answer is null when it did not answer.</summary>
    public sealed record Answers(
        IReadOnlyList<ModuleInfo> Modules,
        JsonNode? Sensors = null,
        JsonNode? Nvidia = null,
        JsonNode? Cooling = null,
        JsonNode? Lighting = null,
        JsonNode? Gcc = null,
        JsonNode? Bios = null,
        JsonNode? Presence = null,
        string AudioState = "starting",
        string? AudioMessage = null,
        bool RichPresenceFound = false,
        string? Windows = null,
        double? MemoryGb = null,
        JsonNode? Cpu = null,
        IReadOnlyList<DisplayAdapter>? Adapters = null);

    public static IReadOnlyList<FirstRunHardwareDto> Hardware(Answers a)
    {
        var list = new List<FirstRunHardwareDto>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(new FirstRunHardwareDto(label, value.Trim()));
        }

        // The processor's own name, with its generation from the built-in list when that list knows it.
        var processor = Text(a.Cpu?["name"]) ?? Text(a.Sensors?["cpu"]?["name"]);
        var generation = Flag(a.Cpu?["known"]) == true ? Text(a.Cpu?["summary"]) : null;
        Add("Processor", processor is null || generation is null ? processor : $"{processor} ({generation})");
        var board = Text(a.Bios?["board"]) ?? Text(a.Sensors?["board"]?["name"]);
        var bios = Text(a.Bios?["biosVersion"]);
        Add("Motherboard", board is null ? null : bios is null ? board : $"{board} (BIOS {bios})");

        var cards = Names(a.Nvidia?["gpus"]);
        if (cards.Count == 0 && Text(a.Sensors?["gpu"]?["name"]) is { } card) cards.Add(card);
        Add(cards.Count > 1 ? "Graphics cards" : "Graphics card",
            string.Join(", ", cards.Select(c => CardFamily(a, c) is { } family ? $"{c} ({family})" : c)));

        var memory = Number(a.Sensors?["memory"]?["totalGB"]) ?? a.MemoryGb;
        if (memory is > 0) Add("Memory", $"{Math.Round(memory.Value):0} GB");
        Add("Windows", a.Windows);

        var nzxt = Names(a.Cooling?["devices"]);
        if (nzxt.Count > 0) Add(nzxt.Count > 1 ? "NZXT devices" : "NZXT device", string.Join(", ", nzxt));

        var lights = Names(a.Lighting?["devices"]);
        if (lights.Count > 0)
            Add("Lights", (lights.Count == 1 ? "1 device: " : $"{lights.Count} devices: ") + string.Join(", ", lights.Take(6)) +
                          (lights.Count > 6 ? $" and {lights.Count - 6} more" : ""));

        Add("Discord", Text(a.Presence?["discordState"]) switch
        {
            "attached" or "found" or "noAnswer" => "Running",
            "otherUser" => "Running under another Windows user",
            "notRunning" => "Not running",
            _ => null,
        });
        return list;
    }

    /// <summary>
    /// The card's family from the built-in card list ("GeForce RTX 40 series"): what the NVIDIA part already looked up,
    /// or a lookup by the hardware id Windows keeps for a card of exactly this name. Null when the list does not know it.
    /// </summary>
    private static string? CardFamily(Answers a, string card)
    {
        if (a.Nvidia?["gpus"] is JsonArray gpus)
            foreach (var g in gpus)
                if (Text(g?["name"]) == card && Text(g?["family"]) is { } family) return family;
        foreach (var adapter in a.Adapters ?? Array.Empty<DisplayAdapter>())
        {
            if (!SameCard(adapter.Name, card)) continue;
            var known = RigControl.Core.Hardware.GpuDatabase.Lookup(adapter.PnpId, card);
            if (known.Known && !string.IsNullOrEmpty(known.Family)) return known.Family;
        }
        return null;
    }

    private static bool SameCard(string? a, string? b)
    {
        static string Tidy(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var x = Tidy(a);
        return x.Length > 0 && string.Equals(x, Tidy(b), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<FirstRunFeatureDto> Features(Answers a)
    {
        var list = new List<FirstRunFeatureDto>();
        void Add(string id, string name, (bool Ok, string? Reason) state) => list.Add(new FirstRunFeatureDto(id, name, state.Ok, state.Ok ? null : state.Reason));

        Add("sensors", "Temperatures and motherboard fans", Module(a, "sensors"));
        Add("cooling", "NZXT cooler and fan controller: pump, fans and screen",
            Both(Module(a, "cooling"), a.Cooling is null || Names(a.Cooling["devices"]).Count > 0, "No NZXT cooler or fan controller was found."));
        Add("nvidia", "NVIDIA graphics: driver settings, displays and overclocking",
            Both(Module(a, "nvidia"), a.Nvidia is null || Flag(a.Nvidia["available"]) != false, "No NVIDIA graphics card with its driver was found."));
        Add("lighting", "Lights through OpenRGB",
            Both(Module(a, "lighting"), a.Lighting is null || Flag(a.Lighting["openRgb"]?["installed"]) != false,
                "OpenRGB is not set up on this PC yet. It is one of the programs RigControl needs."));
        // Recording only knows NVIDIA's video encoder, and its one "works with less" state is "the video converter is
        // missing", which means it cannot record at all.
        Add("capture", "Recording and instant replay",
            Both(Capture(a), a.Nvidia is null || Flag(a.Nvidia["available"]) != false, "Needs an NVIDIA graphics card with its driver."));
        Add("audio", "Audio: soundboard, virtual microphone and equaliser",
            a.AudioState == "running" ? (true, null) : (false, a.AudioMessage ?? "The Audio section did not start."));
        // A build without Discord application ids starts the module but can show nothing on Discord.
        Add("presence", "Discord status",
            Both(Module(a, "presence"), Flag(a.Presence?["setUp"]) != false,
                Text(a.Presence?["setupNote"]) ?? "Discord status isn't set up in this build."));
        Add("overlay", "In-game panel (Ctrl+Alt+G)", (true, null));
        Add("drivers", "Driver updates", Module(a, "drivers"));
        Add("cpu", "Processor: details and Windows power settings", Module(a, "cpu"));
        Add("bios", "Processor settings in the BIOS",
            Both(Module(a, "bios"), a.Bios is null || Flag(a.Bios["available"]) != false,
                Text(a.Bios?["reason"]) ?? "Only for the board and BIOS version it was made for."));
        Add("gcc", "Taking the lights over from GIGABYTE Control Center",
            Flag(a.Gcc?["installed"]) == true ? (true, null) : (false, "GIGABYTE Control Center is not installed."));
        Add("richpresence", "Copying settings from RichPresence",
            a.RichPresenceFound ? (true, null) : (false, "RichPresence was not found on this PC."));
        return list;
    }

    /// <summary>"Not available on this PC: " (the CPU page's wording) marks a feature that does not fit this PC at all.</summary>
    private const string NotAvailablePrefix = RigControl.Bios.BiosModule.NotAvailablePrefix;

    private static (bool Ok, string? Reason) Module(Answers a, string name)
    {
        var m = a.Modules.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (m is null) return (false, "Not part of this build.");
        return m.State switch
        {
            ModuleState.Ready => (true, null),
            ModuleState.Degraded when m.Message is { } msg && msg.StartsWith(NotAvailablePrefix, StringComparison.Ordinal)
                => (false, Sentence(msg[NotAvailablePrefix.Length..])),
            ModuleState.Degraded => (true, null),
            ModuleState.Failed => (false, string.IsNullOrWhiteSpace(m.Message) ? "It could not start. The log says why." : m.Message),
            ModuleState.Stopped => (false, "It is switched off."),
            _ => (false, "RigControl is still checking this."),
        };
    }

    private static (bool Ok, string? Reason) Capture(Answers a)
    {
        var m = a.Modules.FirstOrDefault(x => string.Equals(x.Name, "capture", StringComparison.OrdinalIgnoreCase));
        return m is { State: ModuleState.Degraded }
            ? (false, string.IsNullOrWhiteSpace(m.Message) ? "Recording is not available on this PC." : m.Message)
            : Module(a, "capture");
    }

    private static (bool Ok, string? Reason) Both((bool Ok, string? Reason) module, bool found, string notFound)
        => !module.Ok ? module : found ? (true, null) : (false, notFound);

    private static string Sentence(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string? Text(JsonNode? node)
    {
        try
        {
            return node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static List<string> Names(JsonNode? array)
    {
        var names = new List<string>();
        if (array is not JsonArray items) return names;
        foreach (var item in items)
            if (Text(item?["name"]) is { } name && !names.Contains(name)) names.Add(name);
        return names;
    }
}

/// <summary>
/// Restarting the PC at the end of the first-time setup. Only ever called from the "app.restartPc" RPC, after the
/// user said yes in the app; Windows shows its own notice and the restart can be cancelled for 30 seconds.
/// </summary>
public sealed class PcRestart
{
    public const int Seconds = 30;
    private const int NothingToCancel = 1116; // ERROR_NO_SHUTDOWN_IN_PROGRESS

    private readonly Func<IReadOnlyList<string>, (int Code, string Output)> _shutdown;

    /// <param name="shutdown">Runs Windows' shutdown.exe with these arguments (replaced in tests).</param>
    public PcRestart(Func<IReadOnlyList<string>, (int Code, string Output)>? shutdown = null)
    {
        _shutdown = shutdown ?? RunShutdown;
    }

    /// <summary>What the user is told when a restart would also close another signed-in user's programs.</summary>
    public const string OtherUsersWarning =
        "Someone else is signed in to this PC. A restart closes their programs too, and they lose anything they have not saved.";

    /// <summary>
    /// Asks Windows to restart in <see cref="Seconds"/> seconds. Throws (plain words) when Windows refuses.
    /// While other Windows users are signed in it refuses, unless the user was told and said yes to that
    /// (<paramref name="evenWithOtherUsers"/>): Windows would close their programs without asking them.
    /// </summary>
    public int Schedule(int otherUsersSignedIn = 0, bool evenWithOtherUsers = false)
    {
        if (otherUsersSignedIn > 0 && !evenWithOtherUsers) throw new OtherUsersSignedInException();
        var (code, output) = _shutdown(new[]
        {
            "/r", "/t", Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/c", "RigControl is restarting your PC to finish setting up. You can cancel this in RigControl.",
        });
        if (code != 0) throw new InvalidOperationException(Reason(code, output));
        return Seconds;
    }

    /// <summary>Cancels a restart that is counting down. False when there was none to cancel.</summary>
    public bool Cancel()
    {
        var (code, output) = _shutdown(new[] { "/a" });
        if (code == 0) return true;
        if (code == NothingToCancel) return false;
        throw new InvalidOperationException(Reason(code, output));
    }

    private static string Reason(int code, string output)
    {
        var text = output.Trim();
        return text.Length > 0 ? text : $"Windows answered with code {code}.";
    }

    private static (int Code, string Output) RunShutdown(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows' shutdown program could not be started.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10000))
        {
            try { p.Kill(); } catch { /* already gone */ }
            throw new InvalidOperationException("Windows' shutdown program did not answer.");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>A restart was asked for while other Windows users are signed in, without the user having agreed to that.</summary>
public sealed class OtherUsersSignedInException : InvalidOperationException
{
    public OtherUsersSignedInException() : base(PcRestart.OtherUsersWarning + " Restart from the Windows Start menu when they are done.") { }
}

/// <summary>
/// The first-time setup behind "app.getFirstRun", "app.installHelpers" and "app.completeFirstRun": the system scan,
/// the one yes for the helper programs RigControl needs, and "start with Windows". Nothing is installed before that yes.
/// </summary>
public sealed class FirstRunService
{
    private static readonly TimeSpan WaitForModules = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan WaitForAnswer = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WaitForInstall = TimeSpan.FromMinutes(4);

    private readonly SettingsStore _settings;
    private readonly ModuleHost _host;
    private readonly bool _elevated;
    private readonly ILog _log;
    private readonly Func<BundledApps?> _bundled;
    private readonly Func<(string State, string? Message)> _audio;
    private readonly Func<bool> _autostartOn;
    private readonly Action<bool> _setAutostart;
    private readonly Func<string> _windowsName;
    private readonly FirstRunPc _pc;
    private volatile bool _restartWorthIt;

    /// <param name="autostartOn">Whether RigControl starts with Windows now.</param>
    /// <param name="setAutostart">Turns "start with Windows" on or off; throws an <see cref="RpcException"/> when it could not.</param>
    /// <param name="windowsName">The Windows version as text (replaced in tests, which never read the real registry).</param>
    /// <param name="pc">Facts about this PC and Windows account (replaced in tests).</param>
    public FirstRunService(SettingsStore settings, ModuleHost host, bool elevated, ILog log, Func<BundledApps?> bundled,
        Func<(string State, string? Message)> audio, Func<bool> autostartOn, Action<bool> setAutostart, Func<string>? windowsName = null,
        FirstRunPc? pc = null)
    {
        _windowsName = windowsName ?? WindowsName;
        _pc = pc ?? new FirstRunPc(Environment.ProcessPath ?? string.Empty);
        _settings = settings;
        _host = host;
        _elevated = elevated;
        _log = log;
        _bundled = bundled;
        _audio = audio;
        _autostartOn = autostartOn;
        _setAutostart = setAutostart;
    }

    public Task<FirstRunDto> GetAsync(CancellationToken ct) => BuildAsync(false, false, ct);

    /// <summary>How many other Windows users are signed in right now (asked again just before a restart).</summary>
    public int OtherUsersSignedIn()
    {
        try { return Math.Max(0, _pc.OtherUsersSignedIn()); }
        catch { return 0; }
    }

    /// <summary>
    /// "Install and continue": the user's one yes for all helper programs, then they are set up straight away.
    /// Only a copy of RigControl that runs as administrator takes the yes; any other is told to restart as
    /// administrator first, and the user presses the button again there. Also used to try again after a failure.
    /// </summary>
    public async Task<FirstRunDto> InstallHelpersAsync(CancellationToken ct)
    {
        var bundled = _bundled() ?? throw RpcException.Unavailable("The helper programs are not ready yet. Try again in a moment.");
        if (!_elevated)
            throw RpcException.NeedsAdmin("Installing these programs needs administrator rights. Restart RigControl as administrator; it shows this step again and installs after you press the button there.");

        if (bundled.Summary().Pending)
        {
            var equaliserBefore = Installed(bundled, BundledIds.EqualizerApo);
            var driverBefore = Installed(bundled, BundledIds.PawnIo);
            try
            {
                bundled.GiveConsent();
            }
            catch (InvalidOperationException ex)
            {
                throw RpcException.NeedsAdmin(ex.Message);
            }
            _log.Info("The user pressed \"Install and continue\": setting up the helper programs RigControl needs.");

            var install = Task.Run(bundled.EnsureAll, CancellationToken.None);
            await Task.WhenAny(install, Task.Delay(WaitForInstall, ct)).ConfigureAwait(false);
            // Equalizer APO only works after Windows has been restarted once.
            if (!equaliserBefore && Installed(bundled, BundledIds.EqualizerApo)) _restartWorthIt = true;
            // The sensors were opened without the driver; tell them it is there now so they look again.
            if (!driverBefore && Installed(bundled, BundledIds.PawnIo)) await TellSensorsAboutTheDriver(ct).ConfigureAwait(false);
        }
        // The reason for a restart is told here too: the step is also shown by itself on a PC whose setup is done.
        return await BuildAsync(false, _restartWorthIt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes the setup. "Start with Windows" only changes when the wanted value differs from now.
    /// Nothing is installed here before the yes.
    /// </summary>
    public async Task<FirstRunDto> CompleteAsync(bool? autostart, CancellationToken ct)
    {
        var before = _autostartOn();
        var want = autostart ?? before;
        if (want != before) _setAutostart(want); // a refusal stops here: the setup is not marked as finished

        FirstRunState.MarkDone(_settings);
        _log.Info("First-time setup finished.");

        // After the yes, anything that did not get set up at the first try is tried once more.
        if (_elevated && _bundled() is { } bundled && bundled.Summary() is { Pending: true, NeedsYes: false })
            _ = Task.Run(bundled.EnsureAll, CancellationToken.None);

        // A restart only helps "start with Windows" on an account that can run as administrator at all.
        var forAutostart = want && (!before || !_elevated) && (_elevated || AccountIsAdministrator());
        return await BuildAsync(forAutostart, _restartWorthIt, ct).ConfigureAwait(false);
    }

    private bool AccountIsAdministrator()
    {
        try { return _pc.AccountIsAdministrator(); }
        catch { return true; }
    }

    private async Task TellSensorsAboutTheDriver(CancellationToken ct)
    {
        try
        {
            await Task.WhenAny(AskObject("sensors", "driverInstalled", ct), Task.Delay(WaitForAnswer, ct)).ConfigureAwait(false);
        }
        catch
        {
            // The sensors also notice by themselves a few seconds later.
        }
    }

    private static bool Installed(BundledApps bundled, string id) => bundled.Items().Any(i => i.Id == id && i.Installed);

    private async Task<FirstRunDto> BuildAsync(bool restartForAutostart, bool restartForEqualiser, CancellationToken ct)
    {
        // The modules start in the background; give them a moment so the scan is not a list of "still checking".
        var until = DateTime.UtcNow + WaitForModules;
        while (DateTime.UtcNow < until && _host.Describe().Any(m => m.State is ModuleState.NotStarted or ModuleState.Starting))
            await Task.Delay(250, ct).ConfigureAwait(false);

        var sensors = Ask("sensors", "getSnapshot", ct);
        var nvidia = Ask("nvidia", "getInfo", ct);
        var cooling = Ask("cooling", "getDevices", ct);
        var lighting = Ask("lighting", "getStatus", ct);
        var gcc = Ask("lighting", "getGcc", ct);
        var bios = Ask("bios", "getAvailability", ct);
        var cpu = Ask("cpu", "getInfo", ct);
        var presence = Ask("presence", "getStatus", ct);
        var health = AskObject("health", "getReport", ct);
        var all = Task.WhenAll(sensors, nvidia, cooling, lighting, gcc, bios, presence, cpu, health.ContinueWith(_ => (JsonNode?)null, TaskScheduler.Default));
        await Task.WhenAny(all, Task.Delay(WaitForAnswer, ct)).ConfigureAwait(false);

        var (audioState, audioMessage) = SafeAudio();
        var answers = new FirstRunScan.Answers(
            _host.Describe(), Done(sensors), Done(nvidia), Done(cooling), Done(lighting), Done(gcc), Done(bios), Done(presence),
            audioState, audioMessage, RichPresenceFound(), _windowsName(), MemoryGb(), Done(cpu), SafeAdapters());

        IReadOnlyList<ConflictFinding> findings = Array.Empty<ConflictFinding>();
        if (health.IsCompletedSuccessfully && health.Result is RigControl.Health.HealthReportDto report) findings = report.Findings;

        var helpers = BundledSummary.Nothing;
        try
        {
            if (_bundled() is { } bundled) helpers = bundled.Summary();
        }
        catch (Exception ex)
        {
            _log.Warn("The helper programs could not be listed: " + ex.Message);
        }

        bool autostart;
        try { autostart = _autostartOn(); }
        catch { autostart = false; }

        bool otherAccount, folderOpen;
        try { otherAccount = _elevated && _pc.RunsAsAnotherAccount(); }
        catch { otherAccount = false; }
        try { folderOpen = _pc.ProgramFolderOpen(); }
        catch { folderOpen = false; }

        return new FirstRunDto(FirstRunState.IsDone(_settings), _elevated, autostart, restartForAutostart || restartForEqualiser,
            FirstRunScan.Hardware(answers), FirstRunScan.Features(answers), helpers, findings,
            restartForAutostart, restartForEqualiser, OtherUsersSignedIn(), _elevated || AccountIsAdministrator(), otherAccount, folderOpen);
    }

    /// <summary>Asks one module for what it already knows. Null when the module is missing, not ready or says no.</summary>
    private async Task<JsonNode?> Ask(string module, string method, CancellationToken ct)
    {
        try
        {
            var result = await AskObject(module, method, ct).ConfigureAwait(false);
            return result is null ? null : JsonSerializer.SerializeToNode(result, JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
    }

    private async Task<object?> AskObject(string module, string method, CancellationToken ct)
    {
        try
        {
            var m = _host.Modules.FirstOrDefault(x => x.Name == module);
            if (m is null) return null;
            return await Task.Run(() => m.InvokeAsync(method, null, ct), ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? Done(Task<JsonNode?> task) => task.IsCompletedSuccessfully ? task.Result : null;

    private (string State, string? Message) SafeAudio()
    {
        try { return _audio(); }
        catch { return ("failed", null); }
    }

    private static bool RichPresenceFound()
    {
        try
        {
            return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichPresence"));
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<DisplayAdapter> SafeAdapters()
    {
        try { return _pc.DisplayAdapters(); }
        catch { return Array.Empty<DisplayAdapter>(); }
    }

    private static double? MemoryGb()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d; }
        catch { return null; }
    }

    /// <summary>"Windows 11 24H2 (build 26100)". Windows 11 still calls itself 10 in places, so the build number decides.</summary>
    private static string WindowsName()
    {
        var build = Environment.OSVersion.Version.Build;
        var name = build >= 22000 ? "Windows 11" : "Windows 10";
        string? release = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            release = key?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            // the build number is enough
        }
        return string.IsNullOrWhiteSpace(release) ? $"{name} (build {build})" : $"{name} {release} (build {build})";
    }
}
