using System.Diagnostics;
using Microsoft.Win32;

namespace RigControl.Bundled;

/// <summary>An installed program RigControl has taken over from, with its own uninstaller.</summary>
public sealed record OldApp(string Id, string Name, string? Version, string Why);

/// <summary>
/// Programs RigControl replaces (GIGABYTE Control Center, Armoury Crate). Uninstalling only ever runs the program's own
/// uninstaller from its Windows "Apps" entry, when the user asks, one at a time.
/// </summary>
public static class OldApps
{
    private static readonly (string Prefix, string Why)[] Replaced =
    {
        ("GIGABYTE Control Center", "RigControl runs the graphics card's lights and fans."),
        ("Armoury Crate", "RigControl runs the lights, fans and driver updates."),
        ("ARMOURY CRATE", "RigControl runs the lights, fans and driver updates."),
    };

    private sealed record Entry(string Id, string Name, string? Version, string Why, string Uninstall);

    private static IEnumerable<Entry> Scan()
    {
        foreach (var view in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(view);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var e = key.OpenSubKey(sub);
                if (e?.GetValue("DisplayName") is not string name || e.GetValue("UninstallString") is not string uninstall) continue;
                if (e.GetValue("SystemComponent") is int sys && sys == 1) continue;
                var match = Replaced.FirstOrDefault(r => name.StartsWith(r.Prefix, StringComparison.Ordinal));
                if (match.Prefix is null) continue;
                yield return new Entry(sub, name, e.GetValue("DisplayVersion") as string, match.Why, uninstall);
            }
        }
    }

    public static IReadOnlyList<OldApp> List() => Scan().Select(e => new OldApp(e.Id, e.Name, e.Version, e.Why)).ToList();

    /// <summary>Starts the program's own uninstaller (its window then takes over). Only for an id that <see cref="List"/> returned.</summary>
    public static string Uninstall(string id)
    {
        var e = Scan().FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("That program isn't installed (any more).");
        var (exe, args) = BundledApps.SplitCommand(e.Uninstall);
        if (exe.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(@"\MsiExec.exe", StringComparison.OrdinalIgnoreCase))
        {
            exe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            args = args.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
        }
        if (!Path.IsPathRooted(exe) || !File.Exists(exe)) throw new ArgumentException($"{e.Name}'s uninstaller is missing ({exe}).");
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        return e.Name;
    }
}
