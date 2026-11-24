using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using RigControl.Core.Util;

namespace RigControl.Sensors;

/// <param name="Installed">Any part of ASUS Armoury Crate was found on this PC.</param>
/// <param name="Running">Friendly names of the parts that are running now (empty = nothing running).</param>
/// <param name="StartsWithWindows">Something of Armoury Crate is still set to start by itself.</param>
public sealed record ArmouryState(bool Installed, IReadOnlyList<string> Running, bool StartsWithWindows);

/// <summary>
/// Everything RigControl knows or does about ASUS Armoury Crate, whose background parts also drive the
/// motherboard's fans and lights and so fight with RigControl. Only user-triggered methods change anything.
/// </summary>
public interface IArmouryCrate
{
    ArmouryState GetState();

    /// <summary>Stops Armoury Crate's programs and services (needs administrator). Returns false if some of it is still running.</summary>
    bool Close();

    /// <summary>Turns Armoury Crate's own ways of starting (scheduled tasks, services) off or back on. Needs administrator.</summary>
    void SetStartsWithWindows(bool enabled);
}

public sealed class AsusArmouryCrate : IArmouryCrate
{
    private static readonly (string Process, string Name)[] Programs =
    {
        ("ArmouryCrate", "Armoury Crate"),
        ("ArmouryCrate.UserSessionHelper", "Armoury Crate helper"),
        ("ArmouryCrate.Service", "Armoury Crate service"),
        ("ArmourySocketServer", "Armoury Crate helper"),
        ("ArmourySwAgent", "Armoury Crate helper"),
        ("ArmouryHtmlDebugServer", "Armoury Crate helper"),
        ("asus_framework", "ASUS framework"),
        ("AcPowerNotification", "ASUS power helper"),
        ("LightingService", "Aura lighting service"),
        ("ROGLiveService", "ROG Live Service"),
    };

    private static readonly (string Service, string Name)[] Services =
    {
        ("ArmouryCrateService", "Armoury Crate service"),
        ("LightingService", "Aura lighting service"),
        ("asComSvc", "ASUS Com service"),
        ("AsusFanControlService", "ASUS fan control service"),
        ("ROG Live Service", "ROG Live Service"),
        ("AsusCertService", "ASUS certificate service"),
    };

    private const string BackupKey = @"Software\RigControl";
    private const string BackupServices = "ArmouryAutoServices";
    private const string BackupTasks = "ArmouryDisabledTasks";

    public ArmouryState GetState()
    {
        var running = new List<string>();
        foreach (var (process, name) in Programs)
            if (IsRunning(process) && !running.Contains(name)) running.Add(name);

        var anyService = false;
        var auto = false;
        foreach (var (service, name) in Services)
        {
            try
            {
                using var sc = new ServiceController(service);
                var status = sc.Status; // throws if the service does not exist
                anyService = true;
                if (status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending && !running.Contains(name)) running.Add(name);
                auto |= sc.StartType == ServiceStartMode.Automatic;
            }
            catch
            {
                // not installed
            }
        }

        // A task that is running right now is listed as "Running" even after it has been switched off, so tasks
        // RigControl switched off itself are not counted.
        var tasks = AsusTasks();
        var switchedOff = SwitchedOffTasks();
        return new ArmouryState(anyService || running.Count > 0 || tasks.Count > 0, running,
            auto || tasks.Any(t => t.Enabled && !switchedOff.Contains(t.Name)));
    }

    private static bool IsRunning(string process)
    {
        try
        {
            var procs = Process.GetProcessesByName(process);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool Close()
    {
        // Services first, so they do not restart the programs.
        foreach (var (service, _) in Services)
        {
            try
            {
                using var sc = new ServiceController(service);
                if (sc.Status is ServiceControllerStatus.Stopped) continue;
                if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            catch
            {
                // not installed, or would not stop: the final check reports it
            }
        }
        foreach (var (process, _) in Programs)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(process); }
            catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(4000);
                }
                catch
                {
                    // already gone or not allowed; the final check reports it
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return GetState().Running.Count == 0;
    }

    public void SetStartsWithWindows(bool enabled)
    {
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKey, writable: true);
        if (!enabled)
        {
            // Remember exactly what was switched off, so switching back on restores only that.
            var services = new List<string>(backup.GetValue(BackupServices) as string[] ?? Array.Empty<string>());
            foreach (var (service, _) in Services)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    if (sc.StartType != ServiceStartMode.Automatic) continue;
                }
                catch
                {
                    continue;
                }
                if (Run("sc.exe", "config", service, "start=", "demand") && !services.Contains(service)) services.Add(service);
            }
            backup.SetValue(BackupServices, services.ToArray(), RegistryValueKind.MultiString);

            var tasks = new List<string>(backup.GetValue(BackupTasks) as string[] ?? Array.Empty<string>());
            var refused = new List<string>();
            foreach (var task in AsusTasks().Where(t => t.Enabled && !tasks.Contains(t.Name)))
            {
                if (Run("schtasks.exe", "/Change", "/TN", task.Name, "/DISABLE")) tasks.Add(task.Name);
                else refused.Add(task.Name);
            }
            backup.SetValue(BackupTasks, tasks.ToArray(), RegistryValueKind.MultiString);
            if (refused.Count > 0)
                throw new InvalidOperationException("Windows refused to switch off: " + string.Join(", ", refused) + ".");
            if (GetState().StartsWithWindows)
                throw new InvalidOperationException("Windows did not switch off everything that starts Armoury Crate.");
            return;
        }
        else
        {
            foreach (var service in backup.GetValue(BackupServices) as string[] ?? Array.Empty<string>())
                Run("sc.exe", "config", service, "start=", "auto");
            foreach (var task in backup.GetValue(BackupTasks) as string[] ?? Array.Empty<string>())
                Run("schtasks.exe", "/Change", "/TN", task, "/ENABLE");
            backup.DeleteValue(BackupServices, throwOnMissingValue: false);
            backup.DeleteValue(BackupTasks, throwOnMissingValue: false);
        }
    }

    private static HashSet<string> SwitchedOffTasks()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(BackupKey);
            return new HashSet<string>(key?.GetValue(BackupTasks) as string[] ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal sealed record AsusTask(string Name, bool Enabled);

    /// <summary>
    /// Scheduled tasks in Windows' "\ASUS\" folder (that is where Armoury Crate puts its helpers). Read from the
    /// task scheduler's XML, which is the same in every Windows language.
    /// </summary>
    private static List<AsusTask> AsusTasks()
    {
        var result = new List<AsusTask>();
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "/Query", "/XML", "ONE" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return result;
            using var raw = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(raw);
            p.WaitForExit(10000);
            return AsusTasks(ScheduledTaskXml.Decode(raw.ToArray()));
        }
        catch
        {
            // treat as no tasks
        }
        return result;
    }

    /// <summary>The "\ASUS\" tasks in what "schtasks /Query /XML ONE" printed.</summary>
    internal static List<AsusTask> AsusTasks(string xml) =>
        ScheduledTaskXml.List(xml)
            .Where(t => t.Path.StartsWith(@"\ASUS\", StringComparison.OrdinalIgnoreCase))
            .Select(t => new AsusTask(t.Path, t.Enabled))
            .ToList();

    private static bool Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe)) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Used in tests: no Armoury Crate, and nothing is ever touched.</summary>
public sealed class NoArmouryCrate : IArmouryCrate
{
    public ArmouryState GetState() => new(false, Array.Empty<string>(), false);
    public bool Close() => true;
    public void SetStartsWithWindows(bool enabled) { }
}
