using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RigControl.App.Install;

/// <summary>Closing the RigControl that is running now, before its files are replaced or removed.</summary>
internal static class RunningCopy
{
    /// <summary>The single-instance lock's name (see <see cref="SingleInstance"/>).</summary>
    private const string InstanceMutex = @"Local\RigControl.Instance";

    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>Held by setup and the updater while they replace RigControl's files.</summary>
    internal const string UpdatingMutex = @"Local\RigControl.Updating";

    /// <summary>Marks "RigControl's files are being replaced" until disposed. Null when it can't be made (then nothing waits).</summary>
    public static IDisposable? HoldUpdating(Action<string> log)
    {
        try
        {
            var m = new Mutex(true, UpdatingMutex, out var created);
            if (created) return new Held(m);
            m.Dispose();
            return null; // another setup is already replacing the files; it holds the mark
        }
        catch (Exception ex)
        {
            log("Could not mark the update as running: " + ex.Message);
            return null;
        }
    }

    private sealed class Held(Mutex m) : IDisposable
    {
        public void Dispose()
        {
            try { m.ReleaseMutex(); } catch { /* already released */ }
            m.Dispose();
        }
    }

    /// <summary>
    /// Waits (up to <paramref name="max"/>) while setup or the updater replaces RigControl's files, so a copy started in
    /// the middle does not hold the exe being replaced. True when it had to wait.
    /// </summary>
    public static bool WaitWhileUpdating(TimeSpan max, Action<string> log) => WaitWhile(UpdateRunning, max, TimeSpan.FromMilliseconds(500), log);

    internal static bool WaitWhile(Func<bool> busy, TimeSpan max, TimeSpan step, Action<string> log)
    {
        if (!busy()) return false;
        log("RigControl is being updated; this copy waits until that is finished.");
        var until = DateTime.UtcNow + max;
        while (busy() && DateTime.UtcNow < until) Thread.Sleep(step);
        return true;
    }

    private static bool UpdateRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(UpdatingMutex, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // made by the setup that runs as administrator: it is there
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True while a RigControl runs in this Windows session.</summary>
    public static bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(InstanceMutex, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch
        {
            return true; // there, but not ours to open
        }
    }

    /// <summary>
    /// Asks the running copy to close properly (finishing recordings, handing fans back), the way "--exit" does,
    /// and waits for it and for anything else running from <paramref name="exePath"/>. False when something is
    /// still running after <see cref="Wait"/>.
    /// </summary>
    public static bool CloseAndWait(string? exePath, Action<string> log)
    {
        var until = DateTime.UtcNow + Wait;
        if (IsRunning())
        {
            using var probe = new SingleInstance("RigControl");
            log(probe.SignalExit() ? "Asked the running RigControl to close." : "RigControl is running but could not be asked to close.");
            while (IsRunning() && DateTime.UtcNow < until) Thread.Sleep(250);
            if (IsRunning()) return false;
        }
        return exePath is null || WaitForExe(exePath, until);
    }

    /// <summary>Waits for every other process started from this exe (a copy in another Windows session, say).</summary>
    private static bool WaitForExe(string exePath, DateTime until)
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || !InstallLayout.SamePath(p.MainModule?.FileName, exePath)) continue;
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !p.WaitForExit(left)) return false;
                }
                catch
                {
                    // Gone already, or not ours to look at: replacing the file says soon enough if it is still in use.
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Whether any other RigControl process in this Windows session runs with administrator rights: true when one does,
    /// false when every one found runs without them, null when none was found or one could not be looked at.
    /// </summary>
    public static bool? OthersElevated()
    {
        var self = Environment.ProcessId;
        var session = Process.GetCurrentProcess().SessionId;
        bool? answer = null;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.SessionId != session) continue;
                    if (!p.ProcessName.StartsWith("RigControl", StringComparison.OrdinalIgnoreCase)) continue;
                    var elevated = IsElevated(p);
                    if (elevated is null) return null;
                    if (elevated == true) return true;
                    answer = false;
                }
                catch
                {
                    return null;
                }
            }
        }
        return answer;
    }

    private static bool? IsElevated(Process p)
    {
        const uint QueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
        var process = OpenProcess(QueryLimitedInformation, false, p.Id);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                // TokenElevation = 20: one int, non-zero when the process runs with administrator rights.
                return GetTokenInformation(token, 20, out var elevation, sizeof(int), out _) ? elevation != 0 : null;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Waits up to <see cref="Wait"/> for the process with this id to end (the updater's old copy).</summary>
    public static void WaitForPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(Wait);
        }
        catch
        {
            // Already gone.
        }
    }
}

/// <summary>Who may change the install folder.</summary>
internal static class InstallFolderAccess
{
    /// <summary>
    /// Gives a folder RigControl just made the same rights as Program Files: administrators and Windows change it,
    /// everyone else can only read and run. Used for a folder chosen outside Program Files, which would otherwise
    /// take the rights of the folder above it (often open to every program).
    /// </summary>
    public static void LockToAdministrators(string folder)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);
    }
}
