namespace RigControl.Desktop;

/// <summary>
/// How the page is handed to the window. See <see cref="UiSite"/>. Both ways hand it over from memory through the
/// window's own address: RigControl opens no network port for its page, not even on this computer.
/// </summary>
internal enum UiMode
{
    /// <summary>From memory through the window's own address (app://rigcontrol/), scripts loaded as modules.</summary>
    Scheme,
    /// <summary>The same address, with the script handed over as a plain script for a web view that refuses modules there.</summary>
    Classic,
}

/// <summary>What the command line asked for.</summary>
internal sealed record StartOptions(bool Smoke, UiMode? Ui, bool Help, bool Version, string? Unknown)
{
    public const string HelpText =
        "RigControl for Linux and macOS\n" +
        "  --smoke         open the window, wait for the page to connect, then close. For build machines.\n" +
        "                  It uses a temporary folder and touches no lights and no Discord.\n" +
        "  --ui=MODE       how the page reaches the window: scheme or classic.\n" +
        "                  Left out: scheme is tried first, then classic.\n" +
        "  --version       print the version\n" +
        "  --help          print this";

    public static StartOptions Parse(IReadOnlyList<string> args)
    {
        bool smoke = false, help = false, version = false;
        UiMode? ui = null;
        string? unknown = null;
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)) smoke = true;
            else if (a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "-h") help = true;
            else if (a.Equals("--version", StringComparison.OrdinalIgnoreCase)) version = true;
            else if (a.StartsWith("--ui=", StringComparison.OrdinalIgnoreCase) && ParseMode(a[5..]) is { } m) ui = m;
            // macOS adds an argument like -psn_0_12345 when an app is opened from Finder.
            else if (a.StartsWith("-psn_", StringComparison.Ordinal)) continue;
            else unknown ??= a;
        }
        return new StartOptions(smoke, ui, help, version, unknown);
    }

    public static UiMode? ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "scheme" => UiMode.Scheme,
        "classic" => UiMode.Classic,
        // "loopback" (a small local server) existed in early test builds and was removed: RigControl opens no port.
        _ => null,
    };

    public static string Name(UiMode mode) => mode.ToString().ToLowerInvariant();
}

/// <summary>Reasons not to start at all, each one plain sentence for the terminal.</summary>
internal static class StartupGate
{
    public const string OnWindows =
        "This is the Linux and macOS version of RigControl. On Windows, use the Windows app (RigControl.exe).";

    public const string AsRoot =
        "RigControl must not run as root. Start it as your normal user, without sudo.";

    public const string NoDesktop =
        "RigControl needs a desktop session to show its window, and none was found (DISPLAY and WAYLAND_DISPLAY are empty).";

    public const string AlreadyRunning = "RigControl is already running.";

    public const string NoWebView =
        "RigControl could not open its window because a system library it needs is missing. " +
        "On Linux, install libwebkit2gtk-4.1 and libnotify " +
        "(Debian and Ubuntu: sudo apt install libwebkit2gtk-4.1-0 libnotify4; Fedora: sudo dnf install webkit2gtk4.1 libnotify) " +
        "and start RigControl again.";

    public const string PageNeverConnected =
        "RigControl closed because its window could not show the interface. " +
        "Updating the system's web view (libwebkit2gtk-4.1 on Linux, macOS itself on a Mac) and starting RigControl again may help.";

    /// <summary>The sentence to print, or null when RigControl may start.</summary>
    public static string? Check(bool isWindows, bool isLinux, bool isRoot, Func<string, string?> env)
    {
        if (isWindows) return OnWindows;
        if (isRoot) return AsRoot;
        if (isLinux && string.IsNullOrEmpty(env("DISPLAY")) && string.IsNullOrEmpty(env("WAYLAND_DISPLAY"))) return NoDesktop;
        return null;
    }
}
