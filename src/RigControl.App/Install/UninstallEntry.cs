using Microsoft.Win32;

namespace RigControl.App.Install;

/// <summary>An installed RigControl, as its uninstall entry describes it.</summary>
internal sealed record InstalledCopy(InstallLayout Layout, string? Version);

/// <summary>
/// RigControl's entry in Windows' "Installed apps" (HKLM\...\Uninstall\RigControl). Only administrators can write
/// there, so it is also how a copy learns where RigControl is installed: a value in the user's own settings could
/// be changed by any program.
/// </summary>
internal static class UninstallEntry
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RigControl";
    public const string Publisher = "RigControl";
    public const string AboutUrl = "https://github.com/noice912/RigControl";

    /// <summary>The values the entry holds, in the order they are written.</summary>
    public static IReadOnlyList<(string Name, object Value, RegistryValueKind Kind)> Values(InstallLayout layout, string version, long sizeKb)
    {
        var exe = layout.AppExe;
        return new (string, object, RegistryValueKind)[]
        {
            ("DisplayName", "RigControl", RegistryValueKind.String),
            ("DisplayVersion", version, RegistryValueKind.String),
            ("Publisher", Publisher, RegistryValueKind.String),
            ("InstallLocation", layout.Root, RegistryValueKind.String),
            ("DisplayIcon", exe, RegistryValueKind.String),
            ("UninstallString", $"\"{exe}\" {InstallCommand.UninstallSwitch}", RegistryValueKind.String),
            ("QuietUninstallString", $"\"{exe}\" {InstallCommand.UninstallSwitch} {InstallCommand.QuietSwitch}", RegistryValueKind.String),
            ("EstimatedSize", (int)Math.Clamp(sizeKb, 0, int.MaxValue), RegistryValueKind.DWord),
            ("NoModify", 1, RegistryValueKind.DWord),
            ("NoRepair", 1, RegistryValueKind.DWord),
            ("URLInfoAbout", AboutUrl, RegistryValueKind.String),
        };
    }

    /// <summary>Writes the entry. Needs administrator rights for the real key; tests pass a throwaway key.</summary>
    public static void Write(RegistryKey hive, string keyPath, InstallLayout layout, string version, long sizeKb)
    {
        using var key = hive.CreateSubKey(keyPath, writable: true);
        foreach (var (name, value, kind) in Values(layout, version, sizeKb)) key.SetValue(name, value, kind);
    }

    /// <summary>The installed copy the entry points at, or null when there is none (or it is not usable).</summary>
    public static InstalledCopy? Read(RegistryKey hive, string keyPath)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key?.GetValue("InstallLocation") is not string location) return null;
            if (InstallLayout.RootProblem(location, string.Empty, Array.Empty<string>()) is not null) return null;
            return new InstalledCopy(new InstallLayout(location), key.GetValue("DisplayVersion") as string);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(RegistryKey hive, string keyPath) => hive.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

    /// <summary>The installed RigControl on this PC, from the real entry. Reading needs no administrator rights.</summary>
    public static InstalledCopy? Find() =>
        Read(Registry.LocalMachine, KeyPath) is { } copy && InstallLayout.RootProblem(copy.Layout.Root) is null ? copy : null;
}
