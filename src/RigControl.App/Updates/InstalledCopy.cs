using System.IO;
using Microsoft.Win32;

namespace RigControl.App.Updates;

/// <summary>
/// Is this RigControl the installed one? The installer writes its folder to the uninstall key
/// (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RigControl, InstallLocation), and the installed program is
/// always &lt;InstallLocation&gt;\app\RigControl.exe. Only that copy updates itself; any other copy only says an update exists.
/// </summary>
internal static class InstalledCopy
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RigControl";

    /// <summary>The install folder when <paramref name="exePath"/> is the installed program; otherwise null.</summary>
    public static string? InstallFolderFor(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var location = ReadInstallLocation();
        return Matches(exePath, location) ? Path.GetFullPath(location!) : null;
    }

    /// <summary>The rule on its own (kept apart from the registry so it can be tested).</summary>
    public static bool Matches(string exePath, string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(installLocation.Trim().Trim('"'), "app", "RigControl.exe"));
            return string.Equals(Path.GetFullPath(exePath), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Where downloaded updates go: &lt;install&gt;\updates (only administrators can change it).</summary>
    public static string UpdatesFolder(string installFolder) => Path.Combine(installFolder, "updates");

    private static string? ReadInstallLocation()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }
}
