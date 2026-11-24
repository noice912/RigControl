using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace RigControl.App;

/// <summary>
/// "Start with Windows, as administrator". A program in the normal Run key cannot start elevated without a
/// UAC prompt at every sign-in, so RigControl registers a Windows scheduled task that runs at the user's logon
/// with "highest privileges". Creating or removing the task needs administrator rights once.
///
/// Each Windows user gets a task of their own ("RigControl-" plus their SID), so one user switching the setting
/// never removes or replaces another user's. Older versions made one task called "RigControl" for whoever set it
/// up: that one still counts, and is still used, but only for the user it was made for.
/// </summary>
internal static class Autostart
{
    /// <summary>The one task older versions made. It belongs to the user whose SID is inside it.</summary>
    public const string TaskName = "RigControl";
    public const string InstallSwitch = "--install-autostart";
    public const string RemoveSwitch = "--remove-autostart";

    /// <summary>
    /// Said (log, setup, Health) when RigControl starts as administrator from a folder that ordinary programs can
    /// change: such a program could swap RigControl's files and so get administrator rights at the next sign-in.
    /// </summary>
    public const string OpenFolderWarning =
        "RigControl starts as administrator from a folder that programs without administrator rights can change. " +
        "Such a program could replace RigControl's files. Move the RigControl folder to one only administrators can change " +
        "(for example inside C:\\Program Files), then switch Start with Windows off and on again.";

    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "RigControl";

    /// <summary>The task of one Windows user.</summary>
    public static string TaskNameFor(string userSid) => TaskName + "-" + userSid;

    /// <param name="Task">The name of the task this was read from, when there is one.</param>
    public sealed record State(bool Known, bool Enabled, string? Command, string? Task = null);

    /// <summary>What one task's XML says: switched on, the program it starts, and whose sign-in starts it.</summary>
    public sealed record TaskInfo(bool Enabled, string? Command, string? UserId);

    /// <summary>What Windows answered about one task name. Info is null when the task is there but can't be read.</summary>
    public sealed record TaskAnswer(bool Known, bool Exists, TaskInfo? Info)
    {
        public static readonly TaskAnswer Missing = new(true, false, null);
        public static readonly TaskAnswer Unknown = new(false, false, null);
    }

    /// <summary>Reads one task's XML (what "schtasks /Query /TN name /XML" prints). Null when it is not a task.</summary>
    public static TaskInfo? ReadTask(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml.Trim().TrimStart('\uFEFF'));
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (doc.Root?.Name != ns + "Task") return null;
            var command = doc.Descendants(ns + "Command").FirstOrDefault()?.Value;
            var enabled = !string.Equals(doc.Descendants(ns + "Settings").Elements(ns + "Enabled").FirstOrDefault()?.Value.Trim(), "false",
                StringComparison.OrdinalIgnoreCase);
            var user = doc.Descendants(ns + "Principal").Elements(ns + "UserId").FirstOrDefault()?.Value
                       ?? doc.Descendants(ns + "LogonTrigger").Elements(ns + "UserId").FirstOrDefault()?.Value;
            return new TaskInfo(enabled, command, string.IsNullOrWhiteSpace(user) ? null : user.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the task's user is this SID. A task that names nobody, or somebody else, is not this user's.</summary>
    public static bool BelongsTo(TaskInfo? task, string userSid)
    {
        var user = task?.UserId;
        if (string.IsNullOrWhiteSpace(user)) return false;
        if (string.Equals(user, userSid, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsSid(user)) return false;
        try
        {
            // Windows may show the account as "PC\name" instead of its SID.
            return string.Equals(new NTAccount(user).Translate(typeof(SecurityIdentifier)).Value, userSid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// "Start with Windows" for one user, from what Windows answered about that user's own task and about the old
    /// shared one. Another user's task never counts as this user's.
    /// </summary>
    public static State Decide(string userSid, TaskAnswer own, TaskAnswer legacy)
    {
        // A task under this user's own name is theirs even when its text can't be read.
        if (own.Exists) return new State(true, own.Info?.Enabled ?? true, own.Info?.Command, TaskNameFor(userSid));
        if (legacy.Exists && BelongsTo(legacy.Info, userSid)) return new State(true, legacy.Info!.Enabled, legacy.Info.Command, TaskName);
        return own.Known && legacy.Known ? new State(true, false, null) : new State(false, false, null);
    }

    /// <summary>Reads this user's task. Known=false when Windows would not tell us (e.g. not elevated and access denied).</summary>
    public static State Query() => Query(CurrentUserSid());

    /// <summary>Reads the task of the user with this SID.</summary>
    public static State Query(string userSid) => Decide(userSid, Ask(TaskNameFor(userSid)), Ask(TaskName));

    private static TaskAnswer Ask(string taskName)
    {
        var (code, output, error) = RunSchtasks("/Query", "/TN", taskName, "/XML");
        if (code == 0) return new TaskAnswer(true, true, ReadTask(output));
        // "ERROR: The system cannot find the file specified." => the task does not exist.
        var text = (error + " " + output).ToLowerInvariant();
        return text.Contains("cannot find") || text.Contains("does not exist") ? TaskAnswer.Missing : TaskAnswer.Unknown;
    }

    /// <summary>
    /// The task name to write for this user: the old shared task when it is already theirs (so it keeps its name),
    /// otherwise their own.
    /// </summary>
    public static string TaskToWrite(string userSid, TaskAnswer legacy)
        => legacy.Exists && BelongsTo(legacy.Info, userSid) ? TaskName : TaskNameFor(userSid);

    /// <summary>
    /// Builds the task definition. <paramref name="userSid"/> is the user who signs in (their SID), so the task
    /// still targets the right person when an administrator approved the UAC prompt with a different account.
    /// </summary>
    public static string BuildTaskXml(string exePath, string userSid)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("The program path is empty.", nameof(exePath));
        if (!IsSid(userSid)) throw new ArgumentException("The user id is not a valid SID.", nameof(userSid));
        var exe = SecurityElement.Escape(exePath);
        // Priority 4 = normal. The default for tasks (7) would run the audio engine at below-normal priority.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts RigControl with administrator rights when you sign in, so it can read every sensor and control fans.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userSid}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{userSid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static bool IsSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            _ = new SecurityIdentifier(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
    }

    /// <summary>
    /// Creates or replaces this user's task; no other user's task is touched. Must run elevated. Returns null on
    /// success, otherwise the reason.
    /// </summary>
    public static string? Install(string exePath, string userSid)
    {
        string? file = null;
        try
        {
            if (!File.Exists(exePath)) return "The program file was not found: " + exePath;
            var xml = BuildTaskXml(exePath, userSid);
            var name = TaskToWrite(userSid, Ask(TaskName));
            file = Path.Combine(Path.GetTempPath(), "rigcontrol-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (code, output, error) = RunSchtasks("/Create", "/TN", name, "/XML", file, "/F");
            if (code != 0) return FirstLine(error, output) ?? "Windows refused to create the startup task.";
            RemoveLegacyRunValue();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { if (file is not null) File.Delete(file); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Removes this user's task: their own, and the old shared one only when it was made for them. Must run elevated.
    /// Returns null on success (also when there was none).
    /// </summary>
    public static string? Remove(string userSid)
    {
        try
        {
            RemoveLegacyRunValue();
            string? problem = null;
            var own = Ask(TaskNameFor(userSid));
            if (own.Exists || !own.Known) problem = Delete(TaskNameFor(userSid));
            var legacy = Ask(TaskName);
            if (legacy.Exists && BelongsTo(legacy.Info, userSid)) problem ??= Delete(TaskName);
            return problem;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? Delete(string taskName)
    {
        var (code, output, error) = RunSchtasks("/Delete", "/TN", taskName, "/F");
        if (code == 0) return null;
        var text = (error + " " + output).ToLowerInvariant();
        if (text.Contains("cannot find") || text.Contains("does not exist")) return null;
        return FirstLine(error, output) ?? "Windows refused to remove the startup task.";
    }

    /// <summary>
    /// Runs this exe elevated with an install/remove switch and waits for it. Shows one UAC prompt.
    /// Returns null on success; "cancelled" when the user declined; otherwise the reason.
    /// </summary>
    public static string? RunElevatedHelper(string exePath, bool install)
    {
        try
        {
            // The SID goes along both ways: the prompt may be approved with a different administrator account.
            var args = (install ? InstallSwitch : RemoveSwitch) + " " + CurrentUserSid();
            using var p = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas", Arguments = args });
            if (p is null) return "The helper could not be started.";
            if (!p.WaitForExit(60000)) return "Setting up the startup task took too long.";
            return p.ExitCode == 0 ? null : "The startup task could not be changed (code " + p.ExitCode + "). See the log.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "cancelled";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Handles "--install-autostart &lt;sid&gt;" / "--remove-autostart &lt;sid&gt;" when RigControl is started as a helper.
    /// Returns the process exit code, or null when the arguments are not a helper request.
    /// </summary>
    public static int? HandleCommandLine(string[] args, string exePath, Action<string> log)
    {
        var install = Array.FindIndex(args, a => string.Equals(a, InstallSwitch, StringComparison.OrdinalIgnoreCase));
        if (install >= 0)
        {
            var sid = install + 1 < args.Length && IsSid(args[install + 1]) ? args[install + 1] : CurrentUserSid();
            var error = Install(exePath, sid);
            log(error is null ? "Startup task installed for " + exePath : "Startup task install failed: " + error);
            return error is null ? 0 : 2;
        }
        var remove = Array.FindIndex(args, a => string.Equals(a, RemoveSwitch, StringComparison.OrdinalIgnoreCase));
        if (remove >= 0)
        {
            var sid = remove + 1 < args.Length && IsSid(args[remove + 1]) ? args[remove + 1] : CurrentUserSid();
            var error = Remove(sid);
            log(error is null ? "Startup task removed." : "Startup task removal failed: " + error);
            return error is null ? 0 : 2;
        }
        return null;
    }

    private static void RemoveLegacyRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch
        {
            // An older, non-elevated startup entry may remain; harmless (single instance).
        }
    }

    private static string? FirstLine(params string[] texts)
    {
        foreach (var t in texts)
        {
            var line = t.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is not null) return line;
        }
        return null;
    }

    private static (int Code, string Output, string Error) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks could not be started.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000))
        {
            try { p.Kill(); } catch { /* ignore */ }
            return (-1, string.Empty, "Windows Task Scheduler did not answer.");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }
}
