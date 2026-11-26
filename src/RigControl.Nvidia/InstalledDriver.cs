using System.Globalization;
using System.Management;

namespace RigControl.Nvidia;

/// <summary>
/// The NVIDIA driver version as Windows has it installed right now. The driver session RigControl opens at start keeps
/// answering with the version that was loaded then, so after NVIDIA's installer replaces the driver only Windows knows
/// the new one until RigControl restarts.
/// </summary>
public static class InstalledDriver
{
    /// <summary>
    /// Turns Windows' driver version ("32.0.16.2140") into NVIDIA's ("621.40"): the last digit of the third part and
    /// the four digits of the fourth part are NVIDIA's five digits. Null when the text is not in that form.
    /// </summary>
    public static string? FromWindowsVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        if (parts[3].Length > 4) return null;
        var digits = parts[2][^1] + parts[3].PadLeft(4, '0');
        var number = int.Parse(digits, CultureInfo.InvariantCulture);
        return $"{number / 100}.{number % 100:00}";
    }

    /// <summary>How long WMI may take to answer before the read gives up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The newest NVIDIA display driver Windows lists for a graphics card that is present now, or null. Can take up to
    /// <see cref="Timeout"/> (WMI busy), so callers read it on a background thread.
    /// </summary>
    public static string? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT Name, DriverVersion, AdapterCompatibility FROM Win32_VideoController"),
                new System.Management.EnumerationOptions { Timeout = Timeout, ReturnImmediately = true, Rewindable = false });
            string? best = null;
            decimal bestValue = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    var maker = (o["AdapterCompatibility"] as string) ?? (o["Name"] as string) ?? "";
                    if (!maker.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = FromWindowsVersion(o["DriverVersion"] as string);
                    if (version is null || !decimal.TryParse(version, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) continue;
                    if (value > bestValue) (best, bestValue) = (version, value);
                }
            }
            return best;
        }
        catch
        {
            return null; // WMI busy or broken: the version read at start is used instead
        }
    }
}
