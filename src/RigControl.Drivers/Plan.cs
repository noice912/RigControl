using System.Globalization;
using System.Text.RegularExpressions;

namespace RigControl.Drivers;

/// <summary>One download on ASUS's support site for this motherboard.</summary>
public sealed record AsusFile(
    string Id, string Category, string Title, string Version, DateOnly? Released, string Size, string Url, string? Sha256);

/// <summary>A driver Windows has installed for a device.</summary>
public sealed record InstalledDriver(string DeviceClass, string DeviceName, string Version, string? Provider);

/// <summary>A driver update Windows Update offers.</summary>
public sealed record WindowsUpdateDriver(string Id, string Title, string? DriverClass, string? Date, double SizeMB, bool NeedsLicense);

/// <summary>
/// One row of the Drivers page. State:
///  "update" (ASUS has a newer one), "current" (same version), "newer" (yours is newer than ASUS's),
///  "missing" (no such device driver found), "unknown" (versions cannot be compared).
/// </summary>
public sealed record DriverRowDto(
    string Key, string Name, string Category, string? Device, string? InstalledVersion,
    string? AvailableVersion, string? AvailableTitle, string? Released, string? Size, string? FileId, string State, string? Note);

/// <summary>Works out, for each part of the board, which ASUS download applies and how it compares with what is installed.</summary>
public static class DriverPlan
{
    /// <summary>The parts of the board RigControl looks after, with how to find their ASUS download and their installed device.</summary>
    internal sealed record Part(string Key, string Name, string Category, Regex Title, Regex? Device, string? DeviceClass, bool Comparable = true, string? Note = null);

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Part[] Parts =
    {
        new("lan", "Wired network (Intel I226)", "LAN", R(@"I22[56]"), R(@"I22[56]"), "NET"),
        new("wifi", "Wi-Fi", "Wireless", R(@"Wi-?Fi"), R(@"Wi-?Fi"), "NET"),
        new("bluetooth", "Bluetooth", "Bluetooth", R(@"Bluetooth|\bBT\b"), R(@"Bluetooth"), "BLUETOOTH"),
        new("audio", "Audio (Realtek)", "Audio", R(@"Realtek.*USB Audio"), R(@"Realtek.*USB Audio"), "MEDIA"),
        new("me", "Intel Management Engine", "Chipset", R(@"Management Engine|Intel ME\b"), R(@"Management Engine Interface"), "SYSTEM"),
        new("chipset", "Intel chipset", "Chipset", R(@"Chipset\s+driver"), R(@"LPC Controller|SMBus"), "SYSTEM", Comparable: false,
            Note: "ASUS numbers this package differently from the version Windows shows, so RigControl cannot tell whether it is newer. It only adds device names; installing it is optional."),
        new("rst", "Intel Rapid Storage", "SATA", R(@"Rapid Storage"), R(@"Rapid Storage|VMD"), null),
    };

    /// <summary>Compares dotted version numbers part by part. Null if either is not a version number.</summary>
    public static int? CompareVersions(string? a, string? b)
    {
        static int[]? Parse(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var parts = v.Trim().TrimStart('v', 'V').Split('.');
            var numbers = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
            return numbers;
        }
        var x = Parse(a);
        var y = Parse(b);
        if (x is null || y is null) return null;
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var p = i < x.Length ? x[i] : 0;
            var q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }

    public static IReadOnlyList<DriverRowDto> Build(IReadOnlyList<AsusFile> asus, IReadOnlyList<InstalledDriver> installed)
    {
        var rows = new List<DriverRowDto>();
        foreach (var part in Parts)
        {
            var newest = asus
                .Where(f => string.Equals(f.Category, part.Category, StringComparison.OrdinalIgnoreCase) && part.Title.IsMatch(f.Title))
                .OrderByDescending(f => f.Released ?? DateOnly.MinValue)
                .ThenByDescending(f => f.Version, Comparer<string>.Create((p, q) => CompareVersions(p, q) ?? 0))
                .FirstOrDefault();
            var device = part.Device is null
                ? null
                : installed
                    .Where(d => part.Device.IsMatch(d.DeviceName) && (part.DeviceClass is null || string.Equals(d.DeviceClass, part.DeviceClass, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(d => d.Version, Comparer<string>.Create((p, q) => CompareVersions(p, q) ?? 0))
                    .FirstOrDefault();

            // A part this PC does not have (for example the storage driver when no RAID/VMD is in use) is left out.
            if (device is null && part.Key == "rst") continue;
            if (newest is null && device is null) continue;

            string state;
            string? note = part.Note;
            if (device is null) state = "missing";
            else if (newest is null) state = "unknown";
            else if (!part.Comparable) state = "unknown";
            else
            {
                var c = CompareVersions(device.Version, newest.Version);
                state = c is null ? "unknown" : c < 0 ? "update" : c == 0 ? "current" : "newer";
                if (state == "newer") note = "Windows Update or the maker already gave you a newer driver than ASUS offers, so nothing to do.";
            }
            if (device is null) note = "No device using this driver was found. If the device is switched off in the BIOS, nothing is needed.";

            rows.Add(new DriverRowDto(part.Key, part.Name, part.Category, device?.DeviceName, device?.Version,
                newest?.Version, newest?.Title.Trim(), newest?.Released?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), newest?.Size,
                newest?.Id, state, note));
        }
        return rows;
    }

    /// <summary>Reads ASUS's support answer (GetPDDrivers) into downloads. Anything not on ASUS's download servers is dropped.</summary>
    public static IReadOnlyList<AsusFile> ParseAsus(System.Text.Json.JsonElement root)
    {
        var list = new List<AsusFile>();
        if (!root.TryGetProperty("Result", out var result) || !result.TryGetProperty("Obj", out var cats)) return list;
        foreach (var cat in cats.EnumerateArray())
        {
            var category = cat.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            if (!cat.TryGetProperty("Files", out var files)) continue;
            foreach (var f in files.EnumerateArray())
            {
                string S(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                var url = f.TryGetProperty("DownloadUrl", out var d) && d.TryGetProperty("Global", out var g) ? g.GetString() : null;
                if (!AsusDownloads.IsAllowedUrl(url)) continue;
                DateOnly? released = DateOnly.TryParseExact(S("ReleaseDate"), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : null;
                var sha = S("sha256");
                list.Add(new AsusFile(S("Id"), category, S("Title"), S("Version"), released, S("FileSize"), url!,
                    Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$") ? sha.ToLowerInvariant() : null));
            }
        }
        return list;
    }
}

/// <summary>Rules for what RigControl will download from ASUS and run.</summary>
public static class AsusDownloads
{
    /// <summary>True only for https links on ASUS's download servers.</summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
        return u.Host.Equals("dlcdnets.asus.com", StringComparison.OrdinalIgnoreCase)
               || u.Host.Equals("dlcdn.asus.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Companies whose signed installers RigControl will start from an ASUS driver package.</summary>
    public static readonly string[] TrustedPublishers = { "ASUSTeK", "Intel Corporation", "Realtek Semiconductor" };

    /// <summary>
    /// Picks the installer inside an unpacked package: ASUS's own "AsusSetup.exe" first, then a "Setup.exe", then
    /// the only .exe at the top level. Null if it is not clear which one is meant.
    /// </summary>
    public static string? FindInstaller(string folder)
    {
        var exes = Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
            .Select(p => (Path: p, Depth: Path.GetRelativePath(folder, p).Count(c => c == Path.DirectorySeparatorChar)))
            .OrderBy(e => e.Depth).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        string? Named(string name) => exes.FirstOrDefault(e => string.Equals(Path.GetFileName(e.Path), name, StringComparison.OrdinalIgnoreCase)).Path;
        var pick = Named("AsusSetup.exe") ?? Named("Setup.exe") ?? Named("Install.exe");
        if (pick is not null) return pick;
        if (exes.Count == 0) return null;
        var top = exes.Where(e => e.Depth == exes[0].Depth).ToList();
        return top.Count == 1 ? top[0].Path : null;
    }
}
