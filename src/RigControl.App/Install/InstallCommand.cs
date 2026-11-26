using System.Globalization;

namespace RigControl.App.Install;

/// <summary>What the command line asks for, as far as installing goes.</summary>
internal enum InstallMode
{
    /// <summary>No install switch: what happens depends on where the exe runs (see <see cref="InstallCommand.Decide"/>).</summary>
    None,
    /// <summary>"--setup": show the setup window even where it would not show by itself (development builds).</summary>
    Setup,
    /// <summary>"--install": the setup window, already elevated.</summary>
    Install,
    /// <summary>"--update --after-pid &lt;pid&gt; [--minimized]": the auto-updater's silent install.</summary>
    Update,
    /// <summary>"--uninstall [--quiet]".</summary>
    Uninstall,
    /// <summary>"--portable": run from where it is, no setup.</summary>
    Portable,
}

/// <summary>What RigControl does at start, from the command line and where it runs.</summary>
internal enum StartAction
{
    RunApp,
    Setup,
    Update,
    Uninstall,
}

/// <param name="UserSid">"--user &lt;sid&gt;": the Windows user who started setup before the elevation prompt (whose
/// start-up task is moved), since the prompt may be approved with another administrator account.</param>
/// <param name="Reinstall">"--reinstall": the user chose Reinstall although this version is installed already.</param>
/// <param name="Quiet">"--quiet": no window (QuietUninstallString).</param>
internal sealed record InstallCommand(InstallMode Mode, int? AfterPid, bool Minimized, string? UserSid, bool Reinstall, bool Quiet)
{
    public const string SetupSwitch = "--setup";
    public const string InstallSwitch = "--install";
    public const string UpdateSwitch = "--update";
    public const string UninstallSwitch = "--uninstall";
    public const string PortableSwitch = "--portable";
    public const string UserSwitch = "--user";
    public const string ReinstallSwitch = "--reinstall";
    public const string QuietSwitch = "--quiet";
    public const string MinimizedSwitch = "--minimized";

    public static InstallCommand Parse(IReadOnlyList<string> args)
    {
        bool Has(string s) => args.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase));
        string? After(string s)
        {
            for (var i = 0; i + 1 < args.Count; i++)
                if (string.Equals(args[i], s, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        // One mode only. The ones that change the PC the least win when several are given by mistake, except that an
        // update (only ever started by RigControl itself) is never mistaken for an uninstall.
        var mode = Has(UpdateSwitch) ? InstallMode.Update
            : Has(UninstallSwitch) ? InstallMode.Uninstall
            : Has(InstallSwitch) ? InstallMode.Install
            : Has(SetupSwitch) ? InstallMode.Setup
            : Has(PortableSwitch) ? InstallMode.Portable
            : InstallMode.None;
        int? pid = int.TryParse(After(App.AfterPidSwitch), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : null;
        var sid = After(UserSwitch);
        // Passed on in Windows' own S-1-... form, never as typed (an SDDL short name such as "BA" also counts as a SID).
        sid = Autostart.IsSid(sid) ? new System.Security.Principal.SecurityIdentifier(sid!).Value : null;
        return new InstallCommand(mode, pid, Has(MinimizedSwitch), sid, Has(ReinstallSwitch), Has(QuietSwitch));
    }

    /// <summary>
    /// What to do at start. With no switches at all, a copy outside the install folder is the setup program (the
    /// release is one exe). Anything else (--minimized from a start-up task, --after-pid from "restart as
    /// administrator") keeps the old behaviour, so a copy that already starts with Windows never turns into a setup
    /// window at sign-in. Development builds run the app unless "--setup" (or "--reinstall") asks for the window.
    /// </summary>
    public StartAction Decide(int argCount, bool runsInstalledExe, bool developmentBuild) => Mode switch
    {
        InstallMode.Update => StartAction.Update,
        InstallMode.Uninstall => StartAction.Uninstall,
        InstallMode.Install or InstallMode.Setup => StartAction.Setup,
        InstallMode.Portable => StartAction.RunApp,
        // "--reinstall" on its own means the setup window too, like "--setup".
        _ when Reinstall => StartAction.Setup,
        _ when runsInstalledExe => StartAction.RunApp,
        _ when argCount == 0 && !developmentBuild => StartAction.Setup,
        _ => StartAction.RunApp,
    };

    /// <summary>
    /// What this exe does with these arguments, from where it runs. Touches no WPF type, so the first check in
    /// <see cref="NativeFolderGuard"/> can ask it too.
    /// </summary>
    public static StartAction ActionAt(IReadOnlyList<string> args, string? exe, InstalledCopy? installed)
    {
#if DEBUG
        const bool development = true;
#else
        const bool development = false;
#endif
        var runsInstalled = installed is not null && installed.Layout.IsAppExe(exe);
        return Parse(args).Decide(args.Count, runsInstalled, development);
    }

    /// <summary>The arguments for the elevated copy of a setup started without administrator rights.</summary>
    public static string ElevatedInstallArgs(string userSid, bool reinstall) =>
        InstallSwitch + " " + UserSwitch + " " + userSid + (reinstall ? " " + ReinstallSwitch : "");

    /// <summary>The arguments for the elevated copy of an uninstall started without administrator rights.</summary>
    public static string ElevatedUninstallArgs(string userSid, bool quiet) =>
        UninstallSwitch + " " + UserSwitch + " " + userSid + (quiet ? " " + QuietSwitch : "");

    /// <summary>What the auto-updater passes to the new setup exe (the contract in the design notes).</summary>
    public static string UpdateArgs(int pid, bool minimized) =>
        UpdateSwitch + " " + App.AfterPidSwitch + " " + pid.ToString(CultureInfo.InvariantCulture) + (minimized ? " " + MinimizedSwitch : "");
}

/// <summary>Version numbers as RigControl writes them ("1.1.2", maybe with "v" in front or "+build" after).</summary>
internal static class InstallVersion
{
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var cut = t.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        // 1.1 and 1.1.0 are the same version.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>True when the installed version is this one or newer. An unreadable installed version is not.</summary>
    public static bool InstalledIsSameOrNewer(string? installed, string? mine)
    {
        // Full versions, test builds included (1.1.3-rc.1 is older than 1.1.3).
        if (RigControl.Updates.AppVersion.TryParseLoose(installed, out var x) && RigControl.Updates.AppVersion.TryParseLoose(mine, out var y))
            return x.Value >= y.Value;
        var a = Parse(installed);
        var b = Parse(mine);
        return a is not null && b is not null && a >= b;
    }
}
