using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RigControl.App.Install;

/// <param name="Notes">What could not be removed, said to the user.</param>
internal sealed record UninstallResult(IReadOnlyList<string> Notes);

/// <summary>
/// Removes an installed RigControl ("--uninstall"). Runs elevated. The helper programs stay (the terms say so), and
/// so does the helpers' yes under HKLM\SOFTWARE\RigControl. Only the folders the installer made are removed; the
/// install folder itself goes only when nothing else is left in it (RigControl's OpenRGB copy may live there).
/// </summary>
internal static class Uninstaller
{
    public static UninstallResult Run(InstallLayout layout, string userSid, bool deleteUserData, string runningExe, Action<string> progress, Action<string> log)
    {
        var notes = new List<string>();

        // Every folder the last clean-up may have to delete is checked before anything is removed: one whose name the
        // command prompt would change is left out of the clean-up (and said), rather than found out half-way through.
        string? userData = null;
        if (deleteUserData)
        {
            userData = UserDataFolder(userSid, log);
            if (userData is null) notes.Add("Your settings were kept: RigControl could not be sure which folder is yours.");
        }
        // The user's own data folder is deleted here, not by that line (see DeleteUserData).
        var candidates = new List<string>(layout.OwnFolders) { layout.Root };
        var unsafeForCleanup = candidates.Where(p => !CleanupScript.CanQuote(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in unsafeForCleanup)
            notes.Add($"{path} has characters the command prompt would change, so it is not removed after closing. Delete it yourself.");

        progress("Closing RigControl…");
        if (!RunningCopy.CloseAndWait(layout.AppExe, log))
            notes.Add("RigControl was still running, so some of its files may be left. Restart the PC and delete " + layout.AppDir + ".");

        progress("Removing Start with Windows…");
        try
        {
            var changes = TaskPlan.ForUninstall(userSid, Autostart.Ask(Autostart.TaskNameFor(userSid)), Autostart.Ask(Autostart.TaskName), layout);
            foreach (var c in changes)
            {
                var error = Autostart.Delete(c.TaskName);
                log(error is null ? "Start-up task removed: " + c.TaskName : $"Start-up task {c.TaskName} not removed: {error}");
                if (error is not null) notes.Add("The start-up task could not be removed: " + error);
            }
        }
        catch (Exception ex)
        {
            notes.Add("The start-up task could not be checked: " + ex.Message);
        }

        progress("Removing the shortcuts…");
        foreach (var link in new[] { Shortcuts.StartMenu, Shortcuts.Desktop })
        {
            try { Shortcuts.Remove(link); }
            catch (Exception ex) { notes.Add($"{link} could not be removed: {ex.Message}"); }
        }

        progress("Removing RigControl from Installed apps…");
        try { UninstallEntry.Delete(Registry.LocalMachine, UninstallEntry.KeyPath); }
        catch (Exception ex) { notes.Add("The Installed apps entry could not be removed: " + ex.Message); }

        progress("Removing RigControl's files…");
        var later = new List<string>();
        foreach (var dir in layout.OwnFolders)
        {
            // The running uninstaller's own folder can only go once it has ended. The unpacked native parts go last
            // too: this window may still load one of them (an administrator's copy of RigControl unpacks there).
            if ((layout.Contains(runningExe) && new InstallLayout(dir).Contains(runningExe)) || InstallLayout.SamePath(dir, layout.RuntimeDir))
            {
                later.Add(dir);
                continue;
            }
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                log($"{dir} not removed now ({ex.Message}); trying again after closing.");
                later.Add(dir);
            }
        }

        var script = CleanupScript.Build(later.Where(d => !unsafeForCleanup.Contains(d)),
            unsafeForCleanup.Contains(layout.Root) ? null : layout.Root,
            userData: null, Environment.ProcessId, Environment.SystemDirectory);
        if (script is not null)
        {
            try
            {
                // Runs as administrator, so nothing may come from a folder other programs can write to: the command
                // prompt and every program it starts are named by their full path in System32, it starts in System32,
                // and NoDefaultCurrentDirectoryInExePath stops it looking in its current folder for programs at all.
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory,
                    Arguments = script,
                };
                psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
                // The line's own marker starts empty (it resets it too), and nothing that loads code is passed on.
                psi.Environment.Remove("done");
                NativeFolderGuard.RemoveCodeLoading(psi.Environment);
                Process.Start(psi)?.Dispose();
                log("The last files are removed once this window has closed.");
            }
            catch (Exception ex)
            {
                notes.Add("Some files could not be removed: " + ex.Message + " Delete " + layout.Root + " yourself.");
            }
        }
        log("RigControl uninstalled from " + layout.Root + ".");
        // Last, and with nothing logged after it: the log itself lives in that folder and would only be made again.
        if (userData is not null && DeleteUserData(userSid, userData, log) is { } kept) notes.Add(kept);
        return new UninstallResult(notes);
    }

    /// <summary>
    /// Deletes the user's data folder, after checking it again with the same rules as at the start: the user owns that
    /// folder and could have swapped it, or a folder inside it, for a link to somewhere else meanwhile. So the delete
    /// itself runs with the user's own normal rights (their limited token, see <see cref="LimitedToken"/>), never this
    /// process's administrator rights: whatever a link points at, nothing the user could not delete themselves goes.
    /// Without such a token the folder is kept. RigControl has closed by now, but its browser views may let go of
    /// their files a moment later, so it tries a few times. Returns a note for the user when something was left, else
    /// null. A link inside the folder is removed as a link, never followed (.NET's recursive delete does not go
    /// through links).
    /// </summary>
    private static string? DeleteUserData(string userSid, string expected, Action<string> log)
        => DeleteUserData(expected, () => UserDataFolder(userSid, _ => { }), () => LimitedToken.ForUser(userSid, log), log);

    /// <param name="currentFolder">The data folder as checked now (null when it may not be deleted any more).</param>
    /// <param name="limitedToken">The user's token without administrator rights, or null when there is none. The
    /// handle is disposed here.</param>
    internal static string? DeleteUserData(string expected, Func<string?> currentFolder, Func<SafeAccessTokenHandle?> limitedToken, Action<string> log)
    {
        log("Deleting your settings, logs and Discord sign-in in " + expected + ".");
        SafeAccessTokenHandle? token = null;
        try
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                if (currentFolder() is not { } folder || !InstallLayout.SamePath(folder, expected))
                    return $"Your settings were kept in {expected}: their folder changed during the uninstall, so RigControl did not delete it.";
                token ??= limitedToken();
                if (token is null || token.IsInvalid)
                    return $"Your settings were kept in {expected}: RigControl could not get your normal (non-administrator) rights to delete them. Delete that folder yourself.";
                try
                {
                    var gone = WindowsIdentity.RunImpersonated(token, () =>
                    {
                        if (!Directory.Exists(folder)) return true;
                        Directory.Delete(folder, recursive: true);
                        return true;
                    });
                    if (gone) return null;
                }
                catch when (attempt < 5)
                {
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    return $"Some of your settings could not be deleted ({ex.Message}). Delete {folder} yourself.";
                }
            }
            return null;
        }
        finally
        {
            token?.Dispose();
        }
    }

    /// <summary>
    /// %LOCALAPPDATA%\RigControl (settings, logs, the embedded Discord sign-in) of the user the uninstall is for, or
    /// null when it can't safely be deleted. This runs as administrator, but the user can point their own folders
    /// anywhere (a redirected AppData, a link to another folder), so the folder is only used when it sits inside the
    /// profile folder Windows itself records for this user and no folder on its path is a link.
    /// </summary>
    private static string? UserDataFolder(string userSid, Action<string> log)
    {
        try
        {
            // Another account's own folder would be the wrong one.
            if (!string.Equals(Autostart.CurrentUserSid(), userSid, StringComparison.OrdinalIgnoreCase)) return null;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return null;
            var folder = Path.Combine(local, "RigControl");
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + userSid);
            var profile = key?.GetValue("ProfileImagePath") is string p ? Environment.ExpandEnvironmentVariables(p) : null;
            if (UserDataProblem(folder, profile, HasLink) is { } problem)
            {
                log("Your settings folder was kept: " + problem);
                return null;
            }
            return folder;
        }
        catch (Exception ex)
        {
            log("Your settings folder was kept: it could not be checked (" + ex.Message + ").");
            return null;
        }
    }

    /// <summary>Why this data folder must not be deleted, or null when it may. Pure, so it can be tested.</summary>
    /// <param name="hasLink">True when this one folder (not its parents) is a link (reparse point).</param>
    internal static string? UserDataProblem(string folder, string? profile, Func<string, bool> hasLink)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "Windows does not say where this user's profile folder is.";
        string full, home;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        }
        catch
        {
            return "its name is not a valid folder name.";
        }
        if (!full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return full + " is not inside the profile folder " + home + ".";
        for (var dir = full; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetPathRoot(dir) is { } root && InstallLayout.SamePath(root, dir)) break;
            if (hasLink(dir)) return dir + " is a link to another folder.";
        }
        return null;
    }

    private static bool HasLink(string dir)
    {
        var info = new DirectoryInfo(dir);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

/// <summary>
/// The command prompt line that finishes an uninstall after RigControl has ended (an exe can't delete its own
/// folder while it runs). It waits until the uninstaller's process is gone (at most about ten minutes: the finish
/// window may stay open a while), deletes, and tries once more in case a file was still held.
/// </summary>
internal static class CleanupScript
{
    /// <summary>How many one-second rounds the line waits for the uninstaller to end.</summary>
    public const int MaxWaitRounds = 600;

    // Inside quotes cmd still changes %…% and (with delayed expansion) !…!, and a quote or line break would end the
    // quoted name. & | < > ( ) are taken as they are between quotes, so "Program Files (x86)" is fine.
    private static readonly char[] Special = { '"', '%', '!', '^', '\r', '\n' };

    /// <summary>True when this folder name can be put in the command line safely: nothing cmd would change or split on.</summary>
    public static bool CanQuote(string? path) => !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(Special) < 0;

    /// <param name="folders">Folders to delete with everything in them.</param>
    /// <param name="rootIfEmpty">Removed only when nothing is left in it.</param>
    /// <param name="userData">The user's data folder, when they asked for it to go.</param>
    /// <param name="waitForPid">The process to wait for (the uninstaller itself).</param>
    /// <param name="systemDir">System32: the programs the line starts are named by their full path there.</param>
    /// <returns>The arguments for cmd.exe, or null when there is nothing left to do. A folder that can't be quoted
    /// safely (see <see cref="CanQuote"/>) is left out, never half-quoted.</returns>
    public static string? Build(IEnumerable<string> folders, string? rootIfEmpty, string? userData, int waitForPid, string systemDir)
    {
        var all = folders.Where(CanQuote).ToList();
        if (userData is not null && CanQuote(userData)) all.Add(userData);
        if (rootIfEmpty is not null && !CanQuote(rootIfEmpty)) rootIfEmpty = null;
        if (all.Count == 0 && rootIfEmpty is null) return null;
        if (!CanQuote(systemDir) || systemDir.Contains(' ')) throw new ArgumentException("The system folder's name can't be used in the command prompt: " + systemDir);

        var ping = Path.Combine(systemDir, "PING.EXE");
        var tasklist = Path.Combine(systemDir, "tasklist.exe");
        var find = Path.Combine(systemDir, "find.exe");
        // "ping" to this PC itself is the usual way to wait in a command prompt without a console (no port is opened).
        // Each round asks whether the uninstaller still runs; once it has ended, "done" is set and the remaining
        // rounds do nothing.
        // "for" and "if" each take the rest of the line as their command, so both are wrapped in brackets: otherwise
        // every later "& …" would run inside the loop, or be skipped along with a false "if".
        // "done" is emptied first, so a "done" this command prompt was given from outside can't skip the wait.
        var wait = $"set \"done=\" & (for /l %i in (1,1,{MaxWaitRounds}) do @if not defined done ({tasklist} /FI \"PID eq {waitForPid}\" /NH | {find} \" {waitForPid} \" >nul && {ping} -n 2 127.0.0.1 >nul || set done=1))";
        var pause = $"{ping} -n 3 127.0.0.1 >nul";
        var parts = new List<string> { wait };
        foreach (var f in all) parts.Add($"rmdir /s /q \"{f}\" 2>nul");
        parts.Add(pause);
        foreach (var f in all) parts.Add($"(if exist \"{f}\" rmdir /s /q \"{f}\" 2>nul)");
        if (rootIfEmpty is not null) parts.Add($"rmdir \"{rootIfEmpty}\" 2>nul");
        // /s: cmd takes everything between the first and the last quote as it is.
        return "/d /s /c \"" + string.Join(" & ", parts) + "\"";
    }
}
