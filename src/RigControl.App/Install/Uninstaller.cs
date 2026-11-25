using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

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
            // The running uninstaller's own folder can only go once it has ended.
            if (layout.Contains(runningExe) && new InstallLayout(dir).Contains(runningExe))
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

        string? userData = null;
        if (deleteUserData)
        {
            userData = UserDataFolder(userSid);
            if (userData is null) notes.Add("Your settings were kept: the uninstall runs as another Windows account, which can't tell which folder is yours.");
        }

        var script = CleanupScript.Build(later, layout.Root, userData);
        if (script is not null)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                    Arguments = script,
                };
                Process.Start(psi)?.Dispose();
                log("The last files are removed a few seconds after this window closes.");
            }
            catch (Exception ex)
            {
                notes.Add("Some files could not be removed: " + ex.Message + " Delete " + layout.Root + " yourself.");
            }
        }
        log("RigControl uninstalled from " + layout.Root + ".");
        return new UninstallResult(notes);
    }

    /// <summary>
    /// %LOCALAPPDATA%\RigControl (settings, logs, the embedded Discord sign-in) of the user the uninstall is for.
    /// Null when this process runs as another account: its own folder would be the wrong one.
    /// </summary>
    private static string? UserDataFolder(string userSid)
    {
        try
        {
            if (!string.Equals(Autostart.CurrentUserSid(), userSid, StringComparison.OrdinalIgnoreCase)) return null;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "RigControl");
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The command prompt line that finishes an uninstall after RigControl has ended (an exe can't delete its own
/// folder while it runs). It waits a few seconds, deletes, and tries once more in case a file was still held.
/// </summary>
internal static class CleanupScript
{
    /// <param name="folders">Folders to delete with everything in them.</param>
    /// <param name="rootIfEmpty">Removed only when nothing is left in it.</param>
    /// <param name="userData">The user's data folder, when they asked for it to go.</param>
    /// <returns>The arguments for cmd.exe, or null when there is nothing left to do.</returns>
    public static string? Build(IEnumerable<string> folders, string? rootIfEmpty, string? userData)
    {
        var all = folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (userData is not null) all.Add(userData);
        if (all.Count == 0 && rootIfEmpty is null) return null;
        foreach (var path in all.Append(rootIfEmpty ?? ""))
            if (path.IndexOfAny(new[] { '"', '%', '!', '^', '\r', '\n' }) >= 0)
                throw new ArgumentException("A folder name has characters the command prompt would change: " + path);

        // "ping" to this PC itself is the usual way to wait in a command prompt without a console (no port is opened).
        const string pause = "ping -n 4 127.0.0.1 >nul";
        var parts = new List<string> { pause };
        foreach (var f in all) parts.Add($"rmdir /s /q \"{f}\" 2>nul");
        parts.Add(pause);
        foreach (var f in all) parts.Add($"if exist \"{f}\" rmdir /s /q \"{f}\" 2>nul");
        if (rootIfEmpty is not null) parts.Add($"rmdir \"{rootIfEmpty}\" 2>nul");
        return "/d /c \"" + string.Join(" & ", parts) + "\"";
    }
}
