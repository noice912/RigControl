using System.Diagnostics;
using System.Globalization;

namespace RigControl.Core.Platform;

/// <summary>
/// The current user's own running programs.
/// Windows: programs in this sign-in session. Linux: programs whose owner is this user (read from /proc).
/// macOS: what "ps -x" lists, which is this user's programs only.
/// </summary>
public sealed class OwnProcessQuery : IProcessQuery
{
    public IReadOnlyList<ProcessEntry> OwnProcesses()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows();
            if (OperatingSystem.IsLinux()) return Linux("/proc");
            if (OperatingSystem.IsMacOS()) return MacOs();
        }
        catch
        {
            // The system would not say: an empty list means "nothing is known to run".
        }
        return Array.Empty<ProcessEntry>();
    }

    public bool IsRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        foreach (var p in OwnProcesses())
            if (string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<ProcessEntry> Windows()
    {
        var list = new List<ProcessEntry>();
        int mine;
        using (var me = Process.GetCurrentProcess()) mine = me.SessionId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId == mine) list.Add(new ProcessEntry(p.Id, p.ProcessName));
            }
            catch
            {
                // Ended while we looked, or not ours to see.
            }
            finally
            {
                p.Dispose();
            }
        }
        return list;
    }

    /// <summary>Reads a /proc-style folder. Internal so a test can point it at a made-up folder.</summary>
    internal static List<ProcessEntry> Linux(string procRoot)
    {
        var list = new List<ProcessEntry>();
        var myUid = UidFromStatus(Path.Combine(procRoot, "self", "status"));
        if (myUid is null) return list;
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                if (UidFromStatus(Path.Combine(dir, "status")) != myUid) continue;
                // "comm" is the program's short name (at most 15 characters, as Linux keeps it).
                var name = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                if (name.Length > 0) list.Add(new ProcessEntry(pid, name));
            }
            catch
            {
                // The program ended while we looked.
            }
        }
        return list;
    }

    /// <summary>The real user id from a /proc/&lt;pid&gt;/status file (a line like "Uid: 1000 1000 1000 1000"), or null.</summary>
    internal static string? UidFromStatus(string statusPath)
    {
        if (!File.Exists(statusPath)) return null;
        foreach (var line in File.ReadLines(statusPath))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
            var parts = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }
        return null;
    }

    private static List<ProcessEntry> MacOs()
    {
        var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("pid=,comm=");
        using var ps = Process.Start(psi);
        if (ps is null) return new List<ProcessEntry>();
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(3000);
        return ParsePs(text);
    }

    /// <summary>Parses "ps -x -o pid=,comm=" lines: a number, then the program's full path (which may hold spaces).</summary>
    internal static List<ProcessEntry> ParsePs(string output)
    {
        var list = new List<ProcessEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            if (!int.TryParse(line[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            var path = line[(space + 1)..].Trim();
            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            if (name.Length > 0) list.Add(new ProcessEntry(pid, name));
        }
        return list;
    }
}
