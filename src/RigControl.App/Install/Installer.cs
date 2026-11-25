using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using AudioController.App.Hosting;
using Microsoft.Win32;
using RigControl.Bundled;

namespace RigControl.App.Install;

/// <param name="Root">The install folder.</param>
/// <param name="SourceExe">The exe being installed (the one running now).</param>
/// <param name="UserSid">The Windows user whose start-up task is moved.</param>
/// <param name="DesktopShortcut">The setup window's tick; null (an update) leaves the desktop as it is.</param>
/// <param name="StartWithWindows">The setup window's tick; null (an update) only moves a task that is already there.</param>
internal sealed record InstallRequest(string Root, string SourceExe, string Version, string UserSid, bool? DesktopShortcut, bool? StartWithWindows);

/// <param name="OldCopies">Older RigControl exes the start-up task used before. They are left on disk.</param>
/// <param name="Notes">Things that did not work but do not stop RigControl from running (said on the finish screen).</param>
internal sealed record InstallResult(InstallLayout Layout, IReadOnlyList<string> OldCopies, IReadOnlyList<string> Notes);

/// <summary>An install step that failed; the message is for the user.</summary>
internal sealed class InstallFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The install steps shared by the setup window and "--update" (design notes, "Install steps"). Runs elevated.
/// Old copies of RigControl are never deleted, and no "previous" copy of the exe is kept.
/// </summary>
internal static class Installer
{
    public const int Steps = 8;

    public static InstallResult Run(InstallRequest request, Action<int, string> progress, Action<string> log)
    {
        if (InstallLayout.RootProblem(request.Root) is { } problem) throw new InstallFailedException(problem);
        var layout = new InstallLayout(request.Root);
        var notes = new List<string>();

        // 1. The running RigControl closes first: it holds its exe, the fans and the audio engine.
        progress(1, "Closing RigControl…");
        if (!RunningCopy.CloseAndWait(layout.AppExe, log))
            throw new InstallFailedException("RigControl did not close within 30 seconds. Close it from its tray icon (Quit) and try again.");

        // 2. The folders, which only administrators may change: RigControl runs as administrator from here.
        progress(2, "Making the install folder…");
        MakeFolders(layout, log);

        // 3. The program itself.
        progress(3, "Copying RigControl…");
        CopyExe(request.SourceExe, layout.AppExe, log);

        // 4. Licences, notices and terms; FFmpeg unpacked now so a copy without administrator rights finds it ready.
        progress(4, "Writing the licences…");
        try
        {
            log($"{LicenceFiles.WriteTo(layout.LicensesDir)} licence and notice files written to {layout.LicensesDir}.");
        }
        catch (Exception ex)
        {
            throw new InstallFailedException("The licence files could not be written: " + ex.Message, ex);
        }
        if (AudioHost.ExtractFfmpegDirectory(layout.FfmpegDir) is { } ffmpeg) log("FFmpeg unpacked to " + ffmpeg);
        else log("FFmpeg was not unpacked (this build may not carry it); RigControl tries again when it is needed.");

        // 5. Shortcuts for everyone on this PC.
        progress(5, "Adding the shortcuts…");
        try
        {
            Shortcuts.Create(Shortcuts.StartMenu, layout.AppExe);
            if (request.DesktopShortcut == true) Shortcuts.Create(Shortcuts.Desktop, layout.AppExe);
            else if (request.DesktopShortcut == false) Shortcuts.Remove(Shortcuts.Desktop);
            else if (File.Exists(Shortcuts.Desktop)) Shortcuts.Create(Shortcuts.Desktop, layout.AppExe);
        }
        catch (Exception ex)
        {
            notes.Add("The shortcuts could not be made: " + ex.Message);
            log("Shortcuts failed: " + ex.Message);
        }

        // 6. Windows' "Installed apps" entry, which also tells later copies and the updater where RigControl is.
        progress(6, "Adding RigControl to Installed apps…");
        try
        {
            UninstallEntry.Write(Registry.LocalMachine, UninstallEntry.KeyPath, layout, request.Version, SizeKb(layout));
        }
        catch (Exception ex)
        {
            throw new InstallFailedException("Windows' Installed apps entry could not be written: " + ex.Message, ex);
        }

        // 7. Accepting the terms is the yes for the helper programs, so the first start installs them without asking again.
        progress(7, "Recording your agreement…");
        try
        {
            new AdminConsentStore().Save(new BundledConsent { AllHelpers = true });
        }
        catch (Exception ex)
        {
            notes.Add("Your agreement could not be recorded, so RigControl asks about the helper programs once more when it starts.");
            log("Saving the helpers' yes failed: " + ex.Message);
        }

        // 8. Start with Windows: this user's task is pointed at the installed exe. Old copies stay where they are.
        progress(8, "Setting up Start with Windows…");
        var oldCopies = MoveTasks(request, layout, notes, log);

        log($"RigControl {request.Version} installed in {layout.Root}.");
        return new InstallResult(layout, oldCopies, notes);
    }

    private static void MakeFolders(InstallLayout layout, Action<string> log)
    {
        try
        {
            // Only a folder this installer makes itself is ever locked down. A folder that was already there and is open
            // to ordinary programs is refused: whoever made it may own it or have put things inside it beforehand.
            var existed = Directory.Exists(layout.Root);
            Directory.CreateDirectory(layout.Root);
            if (!existed && FolderGuard.FirstOpen(layout.Root) is not null)
            {
                // A new folder outside Program Files takes the rights of the folder above it; give it Program Files' rights.
                InstallFolderAccess.LockToAdministrators(layout.Root);
                log("The install folder was limited to administrators: " + layout.Root);
            }
            RefuseIfOpen(layout.Root);
            // Each folder RigControl writes into is checked on its own too (owner, who may write, not a link elsewhere),
            // so nothing put there before the lock can be used.
            foreach (var dir in layout.OwnFolders)
            {
                Directory.CreateDirectory(dir);
                RefuseIfOpen(dir);
            }
        }
        catch (InstallFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstallFailedException("The install folder could not be made: " + ex.Message, ex);
        }
    }

    private static void RefuseIfOpen(string path)
    {
        if (FolderGuard.FirstOpen(path) is { } open)
            throw new InstallFailedException(
                $"Programs without administrator rights can change {open}. RigControl runs as administrator, so it must be " +
                "installed where only administrators can make changes, such as C:\\Program Files\\RigControl.");
    }

    /// <summary>Writes "RigControl.exe.new" next to the old exe, checks it, then swaps it in. Nothing named "previous" is kept.</summary>
    private static void CopyExe(string source, string target, Action<string> log)
    {
        if (InstallLayout.SamePath(source, target))
        {
            log("Already running from the install folder: the exe is not copied.");
            return;
        }
        var temp = target + ".new";
        try
        {
            File.Copy(source, temp, overwrite: true);
            if (!SameBytes(source, temp)) throw new InstallFailedException("The copied program does not match the original. Try again.");
            // The old exe can take a moment to be let go after its process ended.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException) when (attempt < 20)
                {
                    Thread.Sleep(500);
                }
            }
            // The exe that will run as administrator must itself be out of reach of ordinary programs.
            RefuseIfOpen(target);
            log("RigControl copied to " + target);
        }
        catch (InstallFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstallFailedException("RigControl could not be copied into the install folder (is it still running for another Windows user?): " + ex.Message, ex);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* a leftover .new is replaced next time */ }
        }
    }

    private static bool SameBytes(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    private static long SizeKb(InstallLayout layout)
    {
        long bytes = 0;
        foreach (var dir in layout.OwnFolders)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(f).Length; } catch { /* gone */ }
                }
            }
            catch { /* missing folder */ }
        }
        return bytes / 1024;
    }

    private static IReadOnlyList<string> MoveTasks(InstallRequest request, InstallLayout layout, List<string> notes, Action<string> log)
    {
        try
        {
            var own = Autostart.Ask(Autostart.TaskNameFor(request.UserSid));
            var legacy = Autostart.Ask(Autostart.TaskName);
            var changes = TaskPlan.ForInstall(request.UserSid, own, legacy, layout.AppExe, request.StartWithWindows);
            foreach (var change in changes)
            {
                var error = change.Kind == TaskChangeKind.Remove
                    ? Autostart.Delete(change.TaskName)
                    : Autostart.WriteTask(layout.AppExe, request.UserSid, change.TaskName);
                log(error is null
                    ? $"Start-up task {change.TaskName}: {change.Kind}" + (change.OldCommand is null ? "" : " (was " + change.OldCommand + ")")
                    : $"Start-up task {change.TaskName} could not be changed: {error}");
                if (error is not null) notes.Add("Start with Windows could not be changed: " + error);
            }
            return TaskPlan.OldCopies(changes, layout, File.Exists);
        }
        catch (Exception ex)
        {
            notes.Add("Start with Windows could not be checked: " + ex.Message);
            log("Moving the start-up task failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>Starts the installed RigControl (step 9).</summary>
    public static void Start(InstallLayout layout, bool minimized, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(layout.AppExe) { UseShellExecute = false, WorkingDirectory = layout.AppDir };
            if (minimized) psi.ArgumentList.Add(InstallCommand.MinimizedSwitch);
            Process.Start(psi)?.Dispose();
            log("Started " + layout.AppExe);
        }
        catch (Exception ex)
        {
            log("The installed RigControl could not be started: " + ex.Message);
        }
    }
}
