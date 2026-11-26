using System.IO;

namespace RigControl.App.Install;

/// <summary>
/// Where an installed RigControl keeps its files: <c>&lt;root&gt;\app\RigControl.exe</c>, the unpacked FFmpeg in
/// <c>ffmpeg\</c>, the licence texts in <c>licenses\</c>, downloaded updates in <c>updates\</c> and the exe's unpacked
/// native parts in <c>runtime\</c>. The root may also
/// hold RigControl's own OpenRGB copy (<c>OpenRGB\</c>), which belongs to the helper programs and is never removed here.
/// </summary>
internal sealed class InstallLayout
{
    public const string ExeName = "RigControl.exe";

    public InstallLayout(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }
    public string AppDir => Path.Combine(Root, "app");
    public string AppExe => Path.Combine(AppDir, ExeName);
    public string FfmpegDir => Path.Combine(Root, "ffmpeg");
    public string LicensesDir => Path.Combine(Root, "licenses");
    public string UpdatesDir => Path.Combine(Root, "updates");

    /// <summary>
    /// Where the one-file exe unpacks the native parts it carries (WPF's and WebView2's DLLs) when it runs as
    /// administrator, instead of the user's temp folder (see <see cref="NativeFolderGuard"/>).
    /// </summary>
    public string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>The folders the installer makes, and the only ones the uninstaller removes.</summary>
    public IReadOnlyList<string> OwnFolders => new[] { AppDir, FfmpegDir, LicensesDir, UpdatesDir, RuntimeDir };

    /// <summary>C:\Program Files\RigControl (on this PC's Program Files).</summary>
    public static string DefaultRoot() =>
        DefaultRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public static string DefaultRoot(string programFiles) => Path.Combine(programFiles, "RigControl");

    /// <summary>True when this path is the installed exe.</summary>
    public bool IsAppExe(string? path) => SamePath(path, AppExe);

    /// <summary>True when this path is this folder or anything inside it.</summary>
    public bool Contains(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return SamePath(full, Root) || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a.Trim().Trim('"'))),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.Trim().Trim('"'))), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Why this folder can't be the install folder, or null when it can. The uninstaller deletes folders inside it,
    /// so it must be an ordinary folder of its own on a local drive: not a drive's top folder, not Windows, not one of
    /// the system's own folders (Program Files itself, a user's profile). Characters that Windows' command prompt
    /// treats specially are refused too, because the uninstaller's last clean-up runs there.
    /// </summary>
    /// <param name="windowsDir">The Windows folder: nothing inside it is allowed.</param>
    /// <param name="systemFolders">Folders that may hold the install folder but never be it.</param>
    public static string? RootProblem(string? path, string windowsDir, IEnumerable<string> systemFolders)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose a folder.";
        path = path.Trim();
        if (path.IndexOfAny(new[] { '"', '%', '!', '^', '\r', '\n' }) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return "The folder name has characters RigControl can't use (\" % ! ^).";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return "Choose a folder on a drive of this PC, such as C:\\Program Files\\RigControl.";
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return "That is not a valid folder name."; }
        if (Path.GetPathRoot(full) is { } root && SamePath(root, full)) return "Choose a folder of its own, not the top of a drive.";
        if (!string.IsNullOrEmpty(windowsDir) && new InstallLayout(windowsDir).Contains(full)) return "RigControl can't be installed in the Windows folder.";
        foreach (var system in systemFolders)
            if (SamePath(system, full)) return "Choose a folder of its own inside " + full + ", such as " + Path.Combine(full, "RigControl") + ".";
        return null;
    }

    /// <summary>The same check against this PC's own folders.</summary>
    public static string? RootProblem(string? path)
    {
        static string F(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); }
            catch { return string.Empty; }
        }
        var profile = F(Environment.SpecialFolder.UserProfile);
        var system = new List<string>
        {
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonProgramFiles), F(Environment.SpecialFolder.CommonApplicationData),
            profile, F(Environment.SpecialFolder.Desktop), F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.ApplicationData),
        };
        if (Path.GetDirectoryName(profile) is { Length: > 0 } users) system.Add(users);
        system.RemoveAll(string.IsNullOrEmpty);
        return RootProblem(path, F(Environment.SpecialFolder.Windows), system);
    }
}
