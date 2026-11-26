using System.ComponentModel;
using System.Diagnostics;
using AudioController.App.Hosting;
using RigControl.Core.Logging;
using RigControl.Core.Modules;

namespace RigControl.App.Install;

/// <summary>
/// The one place App.OnStartup asks "is this a setup, an update or an uninstall?" (design notes, "One exe does
/// everything"). The release is one exe: outside the install folder and without switches it is the setup program;
/// from &lt;install&gt;\app it is RigControl. Returns an exit code when the work is done and RigControl must not start.
/// </summary>
internal static class InstallEntry
{
    public static int? Handle(string[] args, ILog log)
    {
        var exe = Environment.ProcessPath ?? string.Empty;
        var command = InstallCommand.Parse(args);
        var installed = UninstallEntry.Find();
        var runsInstalled = installed is not null && installed.Layout.IsAppExe(exe);
        var action = InstallCommand.ActionAt(args, exe, installed);

        if (runsInstalled && action == StartAction.RunApp)
        {
            // An installed RigControl keeps FFmpeg in its own folder, which only administrators can change.
            AudioHost.FfmpegRoot = installed!.Layout.FfmpegDir;
            // Old unpacked native parts and setup's copies are removed once this copy is up and running.
            if (AppEnvironment.DetectElevated()) StartupCleanup.Schedule(installed.Layout, NativeFolderGuard.CurrentExtractDirs(), exe, log);
        }

        try
        {
            return action switch
            {
                StartAction.Setup => Setup(command, exe, installed, log),
                StartAction.Update => Update(command, exe, log),
                StartAction.Uninstall => Uninstall(command, exe, installed, log),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            log.Error("Setup failed.", ex);
            return 2;
        }
    }

    /// <summary>This exe's version, with a test build's pre-release part ("1.1.3-rc.1").</summary>
    private static string Version => Updates.RunningVersion.Text;

    private static int Setup(InstallCommand command, string exe, InstalledCopy? installed, ILog log)
    {
        var elevated = AppEnvironment.DetectElevated();
        var sid = command.UserSid ?? Autostart.CurrentUserSid();
        if (installed is not null && !command.Reinstall && InstallVersion.InstalledIsSameOrNewer(installed.Version, Version))
        {
            switch (InstalledWindow.Ask(installed, Version))
            {
                case InstalledWindow.Choice.Open:
                    Installer.Start(installed.Layout, minimized: false, log.Info);
                    return 0;
                case InstalledWindow.Choice.Reinstall:
                    if (!elevated) return RelaunchElevated(exe, InstallCommand.ElevatedInstallArgs(sid, reinstall: true), log);
                    break;
                default:
                    return 0;
            }
        }
        else if (!elevated)
        {
            return RelaunchElevated(exe, SetupRelaunchArgs(command, sid), log);
        }

        var window = new SetupWindow(Version, exe, sid, installed?.Layout.Root, log.Info);
        window.ShowDialog();
        if (window.Result is { } result && window.StartNow) Installer.Start(result.Layout, minimized: false, log.Info);
        return 0;
    }

    /// <summary>
    /// The arguments for the elevated copy of a setup that asked for the admin prompt before any choice was shown. A
    /// "--reinstall" given to this copy goes along, so the elevated copy does not ask "Open or install again?" a second
    /// time and end there.
    /// </summary>
    internal static string SetupRelaunchArgs(InstallCommand command, string sid) =>
        InstallCommand.ElevatedInstallArgs(sid, command.Reinstall);

    /// <summary>
    /// "--update --after-pid &lt;pid&gt; [--minimized]" (the auto-updater's contract): no window; wait for the old copy,
    /// install into the existing install folder, start the installed exe.
    /// </summary>
    private static int Update(InstallCommand command, string exe, ILog log)
    {
        if (!AppEnvironment.DetectElevated())
        {
            // The updater runs from an elevated RigControl, so this is unusual; one prompt rather than no update.
            return RelaunchElevated(exe, command.AfterPid is { } p
                ? InstallCommand.UpdateArgs(p, command.Minimized)
                : InstallCommand.UpdateSwitch + (command.Minimized ? " " + InstallCommand.MinimizedSwitch : ""), log);
        }
        if (command.AfterPid is { } pid) RunningCopy.WaitForPid(pid);
        var installed = UninstallEntry.Find();
        if (installed is null)
        {
            log.Warn("--update: RigControl is not installed on this PC (no Installed apps entry), so there is nothing to update.");
            return 2;
        }
        var request = new InstallRequest(installed.Layout.Root, exe, Version, Autostart.CurrentUserSid(), DesktopShortcut: null, StartWithWindows: null);
        var code = 0;
        try
        {
            Installer.Run(request, (step, text) => log.Info($"Update step {step}/{Installer.Steps}: {text}"), log.Info);
        }
        catch (Exception ex)
        {
            log.Error("The update did not finish; starting the RigControl that is installed.", ex);
            code = 2;
        }
        Installer.Start(installed.Layout, command.Minimized, log.Info);
        return code;
    }

    private static int Uninstall(InstallCommand command, string exe, InstalledCopy? installed, ILog log)
    {
        var sid = command.UserSid ?? Autostart.CurrentUserSid();
        if (installed is null)
        {
            log.Warn("--uninstall: RigControl is not installed on this PC.");
            if (!command.Quiet)
                System.Windows.MessageBox.Show("RigControl is not installed on this PC, so there is nothing to remove.", "Uninstall RigControl");
            return 1;
        }
        if (!AppEnvironment.DetectElevated()) return RelaunchElevated(exe, InstallCommand.ElevatedUninstallArgs(sid, command.Quiet), log);

        if (command.Quiet)
        {
            var result = Uninstaller.Run(installed.Layout, sid, deleteUserData: false, exe, s => log.Info(s), log.Info);
            foreach (var note in result.Notes) log.Warn(note);
            return 0;
        }
        new UninstallWindow(installed, sid, log.Info).ShowDialog();
        return 0;
    }

    /// <summary>Starts this exe again as administrator (one prompt) and lets it carry on. 1223 = the user said no.</summary>
    private static int RelaunchElevated(string exe, string arguments, ILog log)
    {
        try
        {
            // The elevated copy gets this process's environment, from which NativeFolderGuard.Check has already taken
            // every variable that makes .NET load other code (it checks again on its own side).
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = arguments });
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log.Info("Administrator permission was not granted, so nothing was installed or removed.");
            return 1223;
        }
    }
}
