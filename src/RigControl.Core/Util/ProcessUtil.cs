using System.Diagnostics;

namespace RigControl.Core.Util;

public static class ProcessUtil
{
    /// <summary>
    /// Returns which of the given process names (without ".exe", case-insensitive) are running right now.
    /// Never throws; returns an empty list if the process list cannot be read.
    /// </summary>
    public static IReadOnlyList<string> FindRunning(params string[] processNames)
    {
        var found = new List<string>();
        foreach (var name in processNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) found.Add(name);
                foreach (var p in procs) p.Dispose();
            }
            catch
            {
                // Access denied or the process exited while we looked: treat as not found.
            }
        }
        return found;
    }

    public static bool IsRunning(string processName) => FindRunning(processName).Count > 0;
}
