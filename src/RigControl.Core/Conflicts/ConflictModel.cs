namespace RigControl.Core.Conflicts;

/// <summary>How much a conflict matters. Serialised as "info", "warn", "blocks".</summary>
public enum ConflictSeverity
{
    /// <summary>Worth knowing; nothing is wrong yet.</summary>
    Info,
    /// <summary>Can override or disturb a RigControl feature.</summary>
    Warn,
    /// <summary>A RigControl feature does not work while this is there.</summary>
    Blocks,
}

/// <summary>What kind of thing was found. Serialised as "process", "service", "driver", "registry", "setting", "task".</summary>
public enum ConflictKind
{
    Process,
    Service,
    Driver,
    Registry,
    Setting,
    Task,
}

/// <summary>The feature areas a conflict can belong to (the "module" of an entry).</summary>
public static class ConflictModules
{
    public const string Sensors = "sensors";
    public const string Cooling = "cooling";
    public const string Lighting = "lighting";
    public const string Capture = "capture";
    public const string Audio = "audio";
    public const string Nvidia = "nvidia";
    public const string Drivers = "drivers";
    public const string Presence = "presence";
    public const string Overlay = "overlay";
    public const string Bios = "bios";
    public const string System = "system";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Sensors, Cooling, Lighting, Capture, Audio, Nvidia, Drivers, Presence, Overlay, Bios, System,
    };
}

/// <summary>Where a registry value lives. Only these two roots are ever read.</summary>
public enum RegistryRoot
{
    CurrentUser,
    LocalMachine,
}

public enum RegistryCheck
{
    /// <summary>The value is there and is the expected number.</summary>
    Is,
    /// <summary>The value is missing, is not a number, or is a different number.</summary>
    IsNot,
    /// <summary>The value is there, whatever it holds.</summary>
    ValueExists,
    /// <summary>The key is there.</summary>
    KeyExists,
}

/// <summary>One read-only look at a registry value (or key, when <paramref name="Value"/> is null).</summary>
public sealed record RegistryTest(RegistryRoot Root, string Key, string? Value, RegistryCheck Check, long Expected = 0)
{
    /// <summary>The place that is read, e.g. "HKCU\Software\Microsoft\Lighting|AmbientLightingEnabled". Used as the snapshot key.</summary>
    public string Place => (Root == RegistryRoot.CurrentUser ? "HKCU\\" : "HKLM\\") + Key + (Value is null ? "" : "|" + Value);

    /// <summary>True when this test is met by what was read. Something that could not be read never matches.</summary>
    public bool Matches(RegistryReading? reading)
    {
        if (reading is null) return false;
        return Check switch
        {
            RegistryCheck.KeyExists => reading.KeyExists,
            RegistryCheck.ValueExists => reading.ValueExists,
            RegistryCheck.Is => reading.Number == Expected,
            RegistryCheck.IsNot => reading.Number != Expected,
            _ => false,
        };
    }
}

/// <summary>What was found at one registry place. <see cref="Number"/> is null when the value is missing or not a number.</summary>
public sealed record RegistryReading(bool KeyExists, bool ValueExists, long? Number);

/// <summary>
/// How one rival is recognised. A rival is found when ANY listed program, service, driver or task is there,
/// or when ALL registry tests match, or when ALL facts are true. Names are compared without caring about
/// upper or lower case; a name that ends in "*" matches anything that starts with the part before it.
/// </summary>
public sealed record ConflictDetect
{
    /// <summary>Running program names without ".exe".</summary>
    public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When set, a running program only counts if its file is NOT inside a folder path containing this text
    /// (used to tell someone else's OpenRGB from RigControl's own copy). A program whose path cannot be read does not count.
    /// </summary>
    public string? ProcessPathMustNotContain { get; init; }

    /// <summary>
    /// When true, a running program only counts if it runs in this Windows user's own session. For programs each
    /// user runs for themselves (Discord status tools, overlays, controller tools): another signed-in user's copy
    /// does not get in this user's way.
    /// </summary>
    public bool ThisUserOnly { get; init; }

    /// <summary>Windows services that are running (service names, not display names).</summary>
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();

    /// <summary>Kernel drivers that are loaded (service names of the drivers).</summary>
    public IReadOnlyList<string> Drivers { get; init; } = Array.Empty<string>();

    /// <summary>Scheduled tasks that are switched on, e.g. "\GCC". A path ending in "\" means any task in that folder.</summary>
    public IReadOnlyList<string> Tasks { get; init; } = Array.Empty<string>();

    /// <summary>Registry tests; all of them must match.</summary>
    public IReadOnlyList<RegistryTest> Registry { get; init; } = Array.Empty<RegistryTest>();

    /// <summary>Names from <see cref="ConflictFacts"/>; all of them must be true.</summary>
    public IReadOnlyList<string> Facts { get; init; } = Array.Empty<string>();
}

/// <summary>Things the probe works out itself because they are not a simple name or registry value.</summary>
public static class ConflictFacts
{
    /// <summary>RigControl is not running as administrator.</summary>
    public const string NotElevated = "notElevated";
    /// <summary>Windows is waiting for a restart to finish an update or install.</summary>
    public const string PendingRestart = "pendingRestart";
    /// <summary>Another signed-in Windows user is also running RigControl.</summary>
    public const string OtherSessionRunsRigControl = "otherSessionRunsRigControl";
    /// <summary>RigControl starts with Windows as administrator from a folder that programs without administrator rights can change.</summary>
    public const string ProgramFolderOpen = "programFolderOpen";
}

/// <summary>Names of the numbers a probe can report in <see cref="ConflictSnapshot.Counts"/>.</summary>
public static class ConflictCounts
{
    /// <summary>How many Windows users are signed in with a desktop right now.</summary>
    public const string Sessions = "sessions";
}

/// <summary>
/// Points at an action the app ALREADY offers on a button today (an RPC method the user triggers). The Health page
/// may show it as a button; nothing ever runs it without a click. <paramref name="Confirm"/>, when set, is the
/// question to ask the user before calling the method.
/// </summary>
public sealed record ConflictFix(string Label, string Method, IReadOnlyDictionary<string, object>? Args = null, string? Confirm = null);

/// <summary>One rival program or Windows setting RigControl knows about.</summary>
/// <param name="Id">Stable id, "module.short-name", e.g. "lighting.signalrgb".</param>
/// <param name="Module">One of <see cref="ConflictModules"/>.</param>
/// <param name="Name">The name a user knows it by.</param>
/// <param name="Verified">True only when the names were checked on a real install or are already used by RigControl's own code.</param>
/// <param name="Fix">Only allowed on verified entries that are not <paramref name="ExplainOnly"/>.</param>
/// <param name="ExplainOnly">Security settings, VPNs and programs RigControl must never close: detect and explain, never a fix.</param>
public sealed record ConflictEntry(
    string Id,
    string Module,
    string Name,
    ConflictKind Kind,
    ConflictDetect Detect,
    ConflictSeverity Severity,
    string WhatItBreaks,
    string WhatToDo,
    bool Verified = false,
    ConflictFix? Fix = null,
    bool ExplainOnly = false);

/// <summary>A rival that is there right now. This is what the UI receives.</summary>
/// <param name="Evidence">What gave it away, in plain words, e.g. "GCC.exe is running".</param>
public sealed record ConflictFinding(
    string Id,
    string Module,
    string Name,
    ConflictKind Kind,
    ConflictSeverity Severity,
    string WhatItBreaks,
    string WhatToDo,
    bool Verified,
    ConflictFix? Fix,
    string Evidence);
