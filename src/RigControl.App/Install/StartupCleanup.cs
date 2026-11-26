using System.IO;
using RigControl.Core.Logging;

namespace RigControl.App.Install;

/// <summary>
/// Leftovers the installed RigControl removes once it is up and running (as administrator): the native parts older
/// versions unpacked into &lt;install&gt;\runtime\RigControl\&lt;bundle id&gt;, and the admin-only copies setup ran from
/// (&lt;install&gt;\updates\setup-&lt;random&gt;, see <see cref="NativeFolderGuard.StageCopy"/>). A folder still in use (by a
/// setup window that is still open, or a copy of RigControl in another Windows session) is left for next time.
/// </summary>
internal static class StartupCleanup
{
    /// <summary>How long after start the clean-up waits, so setup's own copy has ended by then.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The bundle folders under runtime\RigControl that are not the one this process unpacked into. Empty when this
    /// process did not unpack there: then it can't tell which one is in use. Pure, so it can be tested.
    /// </summary>
    /// <param name="children">The folders in &lt;install&gt;\runtime\RigControl.</param>
    /// <param name="currentExtractDirs">Where this process's native parts were unpacked.</param>
    public static IReadOnlyList<string> OldBundleFolders(InstallLayout layout, IEnumerable<string> children, IEnumerable<string> currentExtractDirs)
    {
        var parent = BundleParent(layout);
        var current = currentExtractDirs.Where(d => InstallLayout.SamePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(d)), parent)).ToList();
        if (current.Count == 0) return Array.Empty<string>();
        return children.Where(c => InstallLayout.SamePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(c)), parent)
                                   && !current.Any(d => InstallLayout.SamePath(d, c))).ToList();
    }

    /// <summary>The setup copies under &lt;install&gt;\updates, except the one the running exe is in. Pure.</summary>
    public static IReadOnlyList<string> OldSetupFolders(InstallLayout layout, IEnumerable<string> children, string? runningExe)
        => children.Where(c => InstallLayout.SamePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(c)), layout.UpdatesDir)
                               && Path.GetFileName(Path.TrimEndingDirectorySeparator(c)).StartsWith(NativeFolderGuard.StagePrefix, StringComparison.OrdinalIgnoreCase)
                               && !new InstallLayout(c).Contains(runningExe)).ToList();

    /// <summary>&lt;install&gt;\runtime\RigControl: .NET unpacks into &lt;base&gt;\&lt;exe name&gt;\&lt;bundle id&gt;.</summary>
    public static string BundleParent(InstallLayout layout) => Path.Combine(layout.RuntimeDir, Path.GetFileNameWithoutExtension(InstallLayout.ExeName));

    /// <summary>Runs <see cref="Run"/> on a background thread after <see cref="Delay"/>. Never throws.</summary>
    public static void Schedule(InstallLayout layout, IReadOnlyList<string> currentExtractDirs, string runningExe, ILog log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay).ConfigureAwait(false);
                Run(layout, currentExtractDirs, runningExe, log);
            }
            catch (Exception ex)
            {
                log.Warn("Removing old unpacked files failed: " + ex.Message);
            }
        });
    }

    public static void Run(InstallLayout layout, IReadOnlyList<string> currentExtractDirs, string runningExe, ILog log)
    {
        var bundleParent = BundleParent(layout);
        var candidates = new List<string>();
        candidates.AddRange(OldBundleFolders(layout, Children(bundleParent), currentExtractDirs));
        candidates.AddRange(OldSetupFolders(layout, Children(layout.UpdatesDir), runningExe));
        if (candidates.Count == 0) return;

        var removed = new List<string>();
        var kept = 0;
        foreach (var dir in candidates)
        {
            if (TryRemove(dir)) removed.Add(Path.GetFileName(dir));
            else kept++;
        }
        // One line for the lot.
        if (removed.Count > 0 || kept > 0)
            log.Info($"Removed {removed.Count} old folder(s) of unpacked files and setup copies" +
                     (removed.Count == 0 ? "" : " (" + string.Join(", ", removed) + ")") +
                     (kept == 0 ? "." : $"; {kept} still in use, left for next time."));
    }

    private static IEnumerable<string> Children(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Renames the folder first: Windows refuses that while a file in it is open (a DLL still loaded, an exe still
    /// running), so a folder in use is never half deleted. A link is removed as a link, never followed.
    /// </summary>
    private static bool TryRemove(string dir)
    {
        var moved = dir + ".old-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.Move(dir, moved);
        }
        catch
        {
            return false;
        }
        try
        {
            if (new DirectoryInfo(moved).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(moved, recursive: false);
            else Directory.Delete(moved, recursive: true);
        }
        catch
        {
            // Renamed already; the name no longer matches anything in use and the uninstaller removes it with the rest.
        }
        return true;
    }
}
