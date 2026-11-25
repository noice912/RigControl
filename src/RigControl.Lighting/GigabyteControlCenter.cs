#if WINDOWS
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using RigControl.Core.Util;
#endif

namespace RigControl.Lighting;

/// <param name="Installed">GIGABYTE Control Center was found (its program or its start-up task).</param>
/// <param name="Running">GCC is running now.</param>
/// <param name="StartsWithWindows">Its start-up task is enabled.</param>
/// <param name="DisabledByRigControl">RigControl turned the start-up task off (and can turn it back on).</param>
public sealed record GccState(bool Installed, bool Running, bool StartsWithWindows, bool DisabledByRigControl);

/// <summary>
/// GIGABYTE Control Center drives the Gigabyte graphics card's lights and keeps overriding what RigControl sends.
/// Only user-triggered methods change anything.
/// </summary>
public interface IGigabyteControlCenter
{
    GccState GetState();
    /// <summary>Closes GCC. Returns false if it is still running.</summary>
    bool Close();
    /// <summary>Turns GCC's start-up task off, or back on (only if RigControl turned it off). Needs administrator.</summary>
    void SetStartsWithWindows(bool enabled);
    /// <summary>Starts GCC now through its own start-up task (as it is set up to run), if it isn't running.</summary>
    void Start();
}

#if WINDOWS // GCC is a Windows program; the other flavour only has NoGigabyteControlCenter below.
public sealed class GigabyteControlCenter : IGigabyteControlCenter
{
    private const string Task = @"\GCC";
    private const string Exe = @"C:\Program Files\GIGABYTE\Control Center\GCC.exe";
    private const string BackupKey = @"Software\RigControl";
    private const string BackupValue = "GccTaskDisabled";

    public GccState GetState()
    {
        var running = Process.GetProcessesByName("GCC").Length > 0;
        var (exists, enabled) = TaskState();
        return new GccState(exists || File.Exists(Exe) || running, running, enabled, DisabledByUs());
    }

    public bool Close()
    {
        foreach (var p in Process.GetProcessesByName("GCC"))
        {
            using (p)
            {
                try
                {
                    // Only the real GCC, not anything else called GCC.exe.
                    var path = p.MainModule?.FileName;
                    if (path is not null && !path.Contains(@"\GIGABYTE\", StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch { /* gone already, or not ours to end */ }
            }
        }
        return Process.GetProcessesByName("GCC").Length == 0;
    }

    public void SetStartsWithWindows(bool enabled)
    {
        var (exists, isEnabled) = TaskState();
        if (!exists) return;
        if (!enabled)
        {
            if (!isEnabled) return;
            Schtasks($"/Change /TN \"{Task}\" /DISABLE");
            using var k = Registry.CurrentUser.CreateSubKey(BackupKey);
            k.SetValue(BackupValue, 1, RegistryValueKind.DWord);
        }
        else
        {
            // Only put back what RigControl itself turned off.
            if (!DisabledByUs()) return;
            Schtasks($"/Change /TN \"{Task}\" /ENABLE");
            using var k = Registry.CurrentUser.CreateSubKey(BackupKey);
            k.DeleteValue(BackupValue, false);
        }
    }

    public void Start()
    {
        if (Process.GetProcessesByName("GCC").Length > 0) return;
        var (exists, enabled) = TaskState();
        if (exists && enabled) Schtasks($"/Run /TN \"{Task}\"", throwOnError: false);
    }

    private static bool DisabledByUs()
    {
        using var k = Registry.CurrentUser.OpenSubKey(BackupKey);
        return k?.GetValue(BackupValue) is int v && v == 1;
    }

    private static (bool Exists, bool Enabled) TaskState()
    {
        // The task's own XML reads the same in every Windows language (the table and CSV forms are translated).
        var (code, output) = Schtasks($"/Query /TN \"{Task}\" /XML", throwOnError: false);
        if (code != 0) return (false, false);
        // Exit code 0 means the task exists. If its XML can't be read it counts as enabled, the careful answer.
        return (true, ScheduledTaskXml.Enabled(output) ?? true);
    }

    private static (int Code, string Output) Schtasks(string args, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var errors = p.StandardError.ReadToEndAsync();
        using var raw = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(raw);
        p.WaitForExit(10000);
        var output = ScheduledTaskXml.Decode(raw.ToArray()) + errors.Result;
        if (throwOnError && p.ExitCode != 0) throw new InvalidOperationException("Windows refused to change GCC's start-up task: " + output.Trim());
        return (p.ExitCode, output);
    }
}

#endif

/// <summary>Tests, other PCs without GCC, and Linux and macOS: nothing is looked at or touched.</summary>
public sealed class NoGigabyteControlCenter : IGigabyteControlCenter
{
    public GccState GetState() => new(false, false, false, false);
    public bool Close() => true;
    public void SetStartsWithWindows(bool enabled) { }
    public void Start() { }
}
