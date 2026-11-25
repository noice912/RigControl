using System.Security;
using System.Text;

namespace RigControl.Core.Platform;

/// <summary>
/// "Start when I sign in" on Linux (a .desktop file in the user's autostart folder) and macOS (a LaunchAgent file).
/// Both are plain files in the user's own home folder; nothing is installed and no administrator rights are used.
/// On Windows this is not used: the Windows app has its own startup task.
/// </summary>
public sealed class UnixAutostart : IAutostart
{
    public enum Kind { None, LinuxDesktopFile, MacLaunchAgent }

    public const string LinuxFileName = "rigcontrol.desktop";
    public const string MacLabel = "io.github.rigcontrol";

    private readonly Kind _kind;
    private readonly string _folder;

    public UnixAutostart(Kind kind, string folder)
    {
        _kind = kind;
        _folder = folder;
    }

    /// <summary>The right kind and folder for the system this process runs on.</summary>
    public static UnixAutostart ForThisSystem()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux() && home.Length > 0)
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathRooted(config)) config = Path.Combine(home, ".config");
            return new UnixAutostart(Kind.LinuxDesktopFile, Path.Combine(config, "autostart"));
        }
        if (OperatingSystem.IsMacOS() && home.Length > 0)
            return new UnixAutostart(Kind.MacLaunchAgent, Path.Combine(home, "Library", "LaunchAgents"));
        return new UnixAutostart(Kind.None, string.Empty);
    }

    public bool Supported => _kind != Kind.None;

    public string FilePath => _kind switch
    {
        Kind.LinuxDesktopFile => Path.Combine(_folder, LinuxFileName),
        Kind.MacLaunchAgent => Path.Combine(_folder, MacLabel + ".plist"),
        _ => string.Empty,
    };

    public bool IsEnabled() => Supported && File.Exists(FilePath);

    public string? SetEnabled(bool enabled, string executablePath)
    {
        if (!Supported) return "Starting at sign-in is not available on this system.";
        try
        {
            if (!enabled)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return null;
            }
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
                return "The program's own location is not known, so it cannot be started at sign-in.";
            if (executablePath.Any(char.IsControl))
                return "The program's location has characters that cannot be written to a startup file.";
            Directory.CreateDirectory(_folder);
            var text = _kind == Kind.LinuxDesktopFile ? DesktopFile(executablePath) : LaunchAgent(executablePath);
            File.WriteAllText(FilePath, text, new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return "The startup file could not be changed: " + ex.Message;
        }
    }

    /// <summary>
    /// A desktop entry as the XDG autostart rules describe it. The path goes inside double quotes; there the "Exec"
    /// rules want a backslash before each of: double quote, backtick, dollar sign, backslash. The file format itself
    /// then reads "\\" as one backslash, so every backslash written by the first rule is doubled once more.
    /// A percent sign is written twice (a single one would start a field code).
    /// </summary>
    internal static string DesktopFile(string executablePath)
    {
        const string backslash = "\\";
        var quoted = new StringBuilder("\"");
        foreach (var c in executablePath)
        {
            if (c == '\\') quoted.Append(backslash + backslash + backslash + backslash);
            else if (c is '"' or '`' or '$') quoted.Append(backslash + backslash).Append(c);
            else if (c == '%') quoted.Append("%%");
            else quoted.Append(c);
        }
        quoted.Append('"');
        return "[Desktop Entry]\n" +
               "Type=Application\n" +
               "Name=RigControl\n" +
               "Comment=Starts RigControl when you sign in\n" +
               "Exec=" + quoted + "\n" +
               "Terminal=false\n" +
               "X-GNOME-Autostart-enabled=true\n";
    }

    internal static string LaunchAgent(string executablePath) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n" +
        "<dict>\n" +
        "  <key>Label</key>\n" +
        "  <string>" + MacLabel + "</string>\n" +
        "  <key>ProgramArguments</key>\n" +
        "  <array>\n" +
        "    <string>" + SecurityElement.Escape(executablePath) + "</string>\n" +
        "  </array>\n" +
        "  <key>RunAtLoad</key>\n" +
        "  <true/>\n" +
        "  <key>ProcessType</key>\n" +
        "  <string>Interactive</string>\n" +
        "</dict>\n" +
        "</plist>\n";
}
