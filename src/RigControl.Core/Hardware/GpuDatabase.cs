using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.Core.Hardware;

/// <summary>
/// What the graphics card table says about one PCI device. <see cref="Known"/> is false when the id is not in this
/// version's table; <see cref="Name"/> is then the fallback name (the one Windows reports) or a plain "unknown" text.
/// </summary>
public sealed record GpuInfo(
    bool Known,
    /// <summary>"NVIDIA", "AMD", "Intel" or "Unknown".</summary>
    string Vendor,
    /// <summary>The name to show, e.g. "NVIDIA GeForce RTX 4070 Ti".</summary>
    string Name,
    /// <summary>The chip's own name, e.g. "AD104". Null when the table has none.</summary>
    string? Chip,
    /// <summary>E.g. "GeForce RTX 40 series", "Radeon RX 7000 series", "Arc A-series". Null when the name does not say.</summary>
    string? Family,
    /// <summary>E.g. "Ada Lovelace", "RDNA 3", "Alchemist". Null when the chip name does not say.</summary>
    string? Architecture,
    int VendorId,
    int DeviceId);

/// <summary>Which pci.ids snapshot the table was made from. Devices is 0 when the table is missing from this build.</summary>
public sealed record GpuDatabaseInfo(string Version, string Date, string Sha256, int Devices, int Nvidia, int Amd, int Intel);

/// <summary>
/// Graphics cards by PCI vendor and device id, for NVIDIA (10DE), AMD (1002) and Intel (8086). The table is a text
/// file inside this assembly, made from the PCI ID Project's pci.ids by tools/hardware-db/build-gpu-table.py (see
/// PCI-IDS-NOTICE.txt). "Known" means known to that snapshot: a newer card falls back to the name Windows gives.
/// </summary>
public static partial class GpuDatabase
{
    public const int NvidiaVendorId = 0x10DE;
    public const int AmdVendorId = 0x1002;
    public const int IntelVendorId = 0x8086;

    private const string ResourceSuffix = "gpu-table.txt";

    private sealed record Row(string Chip, string Name, string Family, string Architecture);

    private sealed record Table(GpuDatabaseInfo Info, Dictionary<int, Row> Rows);

    private static readonly Lazy<Table> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _loads;

    /// <summary>How many times the table was read from the assembly (it is read once, on first use).</summary>
    internal static int LoadCount => Volatile.Read(ref _loads);

    public static GpuDatabaseInfo Info => Loaded.Value.Info;

    /// <summary>"NVIDIA", "AMD", "Intel", or "Unknown" for any other vendor id.</summary>
    public static string VendorName(int vendorId) => vendorId switch
    {
        NvidiaVendorId => "NVIDIA",
        AmdVendorId => "AMD",
        IntelVendorId => "Intel",
        _ => "Unknown",
    };

    [GeneratedRegex(@"VEN_([0-9A-F]{4}).*?DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PnpIdPattern();

    /// <summary>Reads the ids out of a Windows device id such as "PCI\VEN_10DE&amp;DEV_2782&amp;SUBSYS_...".</summary>
    public static bool TryParsePnpId(string? pnpDeviceId, out int vendorId, out int deviceId)
    {
        vendorId = 0;
        deviceId = 0;
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return false;
        var m = PnpIdPattern().Match(pnpDeviceId);
        if (!m.Success) return false;
        vendorId = int.Parse(m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        deviceId = int.Parse(m.Groups[2].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Looks a card up by its Windows device id. <paramref name="fallbackName"/> is the name Windows reports; it is
    /// used when the id is not in the table (or cannot be read at all).
    /// </summary>
    public static GpuInfo Lookup(string? pnpDeviceId, string? fallbackName = null)
        => TryParsePnpId(pnpDeviceId, out var vendor, out var device)
            ? Lookup(vendor, device, fallbackName)
            : new GpuInfo(false, "Unknown", Fallback(fallbackName, null), null, null, null, 0, 0);

    /// <summary>Looks a card up by PCI vendor and device id.</summary>
    public static GpuInfo Lookup(int vendorId, int deviceId, string? fallbackName = null)
    {
        var vendor = VendorName(vendorId);
        if (!Loaded.Value.Rows.TryGetValue(Key(vendorId, deviceId), out var row))
            return new GpuInfo(false, vendor, Fallback(fallbackName, $"{vendorId:X4}:{deviceId:X4}"), null, null, null, vendorId, deviceId);

        // Some entries carry a chip name only ("GP106"): the name Windows gives is the better one to show then.
        string name;
        if (row.Name.Length > 0)
            name = row.Name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? row.Name : vendor + " " + row.Name;
        else
            name = string.IsNullOrWhiteSpace(fallbackName) ? vendor + " " + row.Chip : fallbackName.Trim();
        return new GpuInfo(true, vendor, name, NullIfEmpty(row.Chip), NullIfEmpty(row.Family), NullIfEmpty(row.Architecture), vendorId, deviceId);
    }

    private static string Fallback(string? fallbackName, string? ids)
        => !string.IsNullOrWhiteSpace(fallbackName) ? fallbackName.Trim()
            : ids is null ? "Unknown graphics card" : $"Unknown graphics card ({ids})";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static int Key(int vendorId, int deviceId) => (vendorId << 16) | (deviceId & 0xFFFF);

    private static Table Load()
    {
        Interlocked.Increment(ref _loads);
        var asm = typeof(GpuDatabase).Assembly;
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resource is null) return Parse(TextReader.Null);
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader);
    }

    /// <summary>Reads the table's text form. Lines it does not understand are skipped.</summary>
    private static Table Parse(TextReader reader)
    {
        var rows = new Dictionary<int, Row>(3000);
        string version = "", date = "", sha = "";
        int vendor = 0, nvidia = 0, amd = 0, intel = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '!')
            {
                var space = line.IndexOf(' ');
                if (space < 0) continue;
                var value = line[(space + 1)..].Trim();
                switch (line[1..space])
                {
                    case "version": version = value; break;
                    case "date": date = value; break;
                    case "sha256": sha = value; break;
                }
                continue;
            }
            if (line[0] == '@')
            {
                var bar = line.IndexOf('|');
                var id = bar < 0 ? line.AsSpan(1) : line.AsSpan(1, bar - 1);
                if (!int.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendor)) vendor = 0;
                continue;
            }
            if (vendor == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 5 || !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var device)) continue;
            rows[Key(vendor, device)] = new Row(parts[1], parts[2], parts[3], parts[4]);
            switch (vendor)
            {
                case NvidiaVendorId: nvidia++; break;
                case AmdVendorId: amd++; break;
                case IntelVendorId: intel++; break;
            }
        }
        return new Table(new GpuDatabaseInfo(version, date, sha, rows.Count, nvidia, amd, intel), rows);
    }
}
