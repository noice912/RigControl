using System.Globalization;
using System.Text.RegularExpressions;

namespace RigControl.Core.Hardware;

/// <summary>
/// What a processor says about itself through CPUID: the vendor text ("GenuineIntel", "AuthenticAMD") and the
/// family, model and stepping numbers, already combined with their "extended" parts.
/// </summary>
public readonly partial record struct CpuSignature(string VendorId, int Family, int Model, int Stepping)
{
    /// <summary>Decodes register EAX of CPUID leaf 1.</summary>
    public static CpuSignature FromCpuid(string vendorId, uint eax)
    {
        int stepping = (int)(eax & 0xF);
        int model = (int)((eax >> 4) & 0xF);
        int family = (int)((eax >> 8) & 0xF);
        int extModel = (int)((eax >> 16) & 0xF);
        int extFamily = (int)((eax >> 20) & 0xFF);
        // The extended model counts for families 6 and 15; the extended family only for family 15.
        if (family is 6 or 15) model |= extModel << 4;
        if (family == 15) family += extFamily;
        return new CpuSignature(vendorId, family, model, stepping);
    }

    [GeneratedRegex(@"Family\s+(\d+)\s+Model\s+(\d+)\s+Stepping\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPattern();

    /// <summary>
    /// Reads the text Windows keeps for a processor, e.g. "Intel64 Family 6 Model 183 Stepping 1" with the
    /// manufacturer "GenuineIntel" (Win32_Processor Description and Manufacturer, or the registry's Identifier and
    /// VendorIdentifier under HARDWARE\DESCRIPTION\System\CentralProcessor\0).
    /// </summary>
    public static bool TryParseWindows(string? manufacturer, string? description, out CpuSignature signature)
    {
        signature = default;
        if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(description)) return false;
        var m = WindowsPattern().Match(description);
        if (!m.Success) return false;
        signature = new CpuSignature(manufacturer.Trim(),
            int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>
/// What the processor table says about one CPU. <see cref="Known"/> is false when this version has no entry for the
/// family and model; <see cref="Summary"/> then holds the CPU's own name and says it is newer than this version knows
/// (or, for a CPU from before the table starts, older than the list goes).
/// </summary>
public sealed record CpuGenerationInfo(
    bool Known,
    /// <summary>"Intel", "AMD" or "Other".</summary>
    string Vendor,
    /// <summary>Intel: "13th gen Core", "Core Ultra series 2", "Xeon Scalable 4th gen". AMD: "Zen 4".</summary>
    string? Generation,
    /// <summary>E.g. "Raptor Lake", "Raphael".</summary>
    string? CodeName,
    /// <summary>What it is sold as, e.g. "Desktop", "Laptop", "Ryzen 7000 desktop".</summary>
    string? Line,
    /// <summary>One line to show, e.g. "13th gen Core (Raptor Lake), desktop".</summary>
    string Summary);

/// <summary>
/// Processor generations and code names by CPUID family, model and (where it matters) stepping, for Intel (Sandy
/// Bridge and newer) and AMD (Zen and newer). The exact model name always comes from the CPU itself; this table only
/// adds which generation it belongs to. Only entries that are certain are listed: a missing entry shows as "newer
/// than this version knows", which is better than a wrong one. Where the numbers come from is written down in
/// tools/hardware-db/README.md. To add a CPU, add one line to <see cref="Intel"/> or <see cref="Amd"/>.
/// </summary>
public static partial class CpuDatabase
{
    public const string IntelVendorId = "GenuineIntel";
    public const string AmdVendorId = "AuthenticAMD";

    /// <summary>Shown for a CPU this version has no entry for.</summary>
    public const string UnknownNote = "newer than this version knows";

    /// <summary>Shown for a CPU from before the table starts: AMD before Zen, Intel before Sandy Bridge and the early Atoms.</summary>
    public const string OlderNote = "older than this version's list goes";

    /// <summary>
    /// Intel family 6 models from before the table starts. Intel's model numbers do not follow age (Westmere 0x2C is
    /// above Sandy Bridge 0x2A), so they are named one by one: Pentium M and Core (0x09, 0x0D, 0x0E), Core 2 (0x0F,
    /// 0x16, 0x17, 0x1D), Nehalem (0x1A, 0x1E, 0x1F, 0x2E), Westmere (0x25, 0x2C, 0x2F) and the Atoms before Apollo
    /// Lake (Bonnell 0x1C, 0x26; Saltwell 0x27, 0x35, 0x36; Silvermont 0x37, 0x4A, 0x4D, 0x5A, 0x5D; Airmont 0x4C).
    /// </summary>
    private static readonly HashSet<int> IntelOlderModels =
    [
        0x09, 0x0D, 0x0E, 0x0F, 0x16, 0x17, 0x1D, 0x1A, 0x1E, 0x1F, 0x2E, 0x25, 0x2C, 0x2F,
        0x1C, 0x26, 0x27, 0x35, 0x36, 0x37, 0x4A, 0x4D, 0x5A, 0x5D, 0x4C,
    ];

    private const string Desktop = "Desktop";
    private const string Laptop = "Laptop";
    private const string Both = "Desktop and laptop";
    private const string Server = "Server and workstation";
    private const string LowPower = "Low-power PCs";

    /// <summary>
    /// One Intel family 6 model. GenMin/GenMax: when the CPU's own name carries a Core model number (i7-13700K), the
    /// generation is read from it if it falls in this range; 0 means the name is never used.
    /// </summary>
    private sealed record IntelRow(int Model, int SteppingMin, int SteppingMax, string CodeName, string Generation, int GenMin, int GenMax, string Line);

    private static IntelRow I(int model, string codeName, string generation, int genMin, int genMax, string line, int steppingMin = 0, int steppingMax = 15)
        => new(model, steppingMin, steppingMax, codeName, generation, genMin, genMax, line);

    private static readonly IntelRow[] Intel =
    [
        // ---- Core, 2nd to 11th generation
        I(0x2A, "Sandy Bridge", "2nd gen Core", 2, 2, Both),
        I(0x2D, "Sandy Bridge-E", "Core i7 Extreme or Xeon E5", 0, 0, Server),
        I(0x3A, "Ivy Bridge", "3rd gen Core", 3, 3, Both),
        I(0x3E, "Ivy Bridge-E", "Core i7 Extreme or Xeon E5 v2", 0, 0, Server),
        I(0x3C, "Haswell", "4th gen Core", 4, 4, Both),
        I(0x45, "Haswell", "4th gen Core", 4, 4, Laptop),
        I(0x46, "Haswell", "4th gen Core", 4, 4, Both),
        I(0x3F, "Haswell-E", "Core i7 Extreme or Xeon E5 v3", 0, 0, Server),
        I(0x3D, "Broadwell", "5th gen Core", 5, 5, Laptop),
        I(0x47, "Broadwell", "5th gen Core", 5, 5, Both),
        I(0x4F, "Broadwell-E", "Core i7 Extreme or Xeon E5 v4", 0, 0, Server),
        I(0x56, "Broadwell-DE", "Xeon D", 0, 0, Server),
        I(0x4E, "Skylake", "6th gen Core", 6, 6, Laptop),
        I(0x5E, "Skylake", "6th gen Core", 6, 6, Both),
        I(0x55, "Skylake-X", "Core X or Xeon Scalable 1st gen", 0, 0, Server, 0, 4),
        I(0x55, "Cascade Lake", "Core X or Xeon Scalable 2nd gen", 0, 0, Server, 5, 7),
        I(0x55, "Cooper Lake", "Xeon Scalable 3rd gen", 0, 0, Server, 10, 11),
        I(0x8E, "Kaby Lake / Amber Lake", "7th gen Core", 7, 8, Laptop, 9, 9),
        I(0x8E, "Kaby Lake Refresh / Coffee Lake", "8th gen Core", 8, 8, Laptop, 10, 10),
        I(0x8E, "Whiskey Lake", "8th gen Core", 8, 8, Laptop, 11, 11),
        I(0x8E, "Whiskey Lake / Comet Lake", "8th or 10th gen Core", 8, 10, Laptop, 12, 12),
        I(0x9E, "Kaby Lake", "7th gen Core", 7, 7, Both, 9, 9),
        I(0x9E, "Coffee Lake", "8th gen Core", 8, 9, Both, 10, 11),
        I(0x9E, "Coffee Lake Refresh", "9th gen Core", 9, 9, Both, 12, 13),
        I(0x66, "Cannon Lake", "8th gen Core", 8, 8, Laptop),
        I(0xA5, "Comet Lake", "10th gen Core", 10, 10, Both),
        I(0xA6, "Comet Lake", "10th gen Core", 10, 10, Laptop),
        I(0x7E, "Ice Lake", "10th gen Core", 10, 10, Laptop),
        I(0x8C, "Tiger Lake", "11th gen Core", 11, 11, Laptop),
        I(0x8D, "Tiger Lake", "11th gen Core", 11, 11, Laptop),
        I(0xA7, "Rocket Lake", "11th gen Core", 11, 11, Desktop),

        // ---- Core with performance and efficiency cores
        I(0x97, "Alder Lake", "12th gen Core", 12, 13, Desktop),
        I(0x9A, "Alder Lake", "12th gen Core", 12, 13, Laptop),
        I(0xB7, "Raptor Lake", "13th or 14th gen Core", 13, 14, Desktop),
        I(0xBF, "Raptor Lake", "13th or 14th gen Core", 13, 14, Desktop),
        I(0xBA, "Raptor Lake", "13th gen Core or Core series 1", 13, 13, Laptop),
        I(0xAA, "Meteor Lake", "Core Ultra series 1", 0, 0, Laptop),
        I(0xAC, "Meteor Lake", "Core Ultra series 1", 0, 0, Laptop),
        I(0xC6, "Arrow Lake", "Core Ultra series 2", 0, 0, Desktop),
        I(0xC5, "Arrow Lake", "Core Ultra series 2", 0, 0, Laptop),
        I(0xB5, "Arrow Lake", "Core Ultra series 2", 0, 0, Laptop),
        I(0xBD, "Lunar Lake", "Core Ultra series 2", 0, 0, Laptop),
        I(0xCC, "Panther Lake", "Core Ultra series 3", 0, 0, Laptop),

        // ---- Xeon
        I(0x6A, "Ice Lake-SP", "Xeon Scalable 3rd gen", 0, 0, Server),
        I(0x6C, "Ice Lake-D", "Xeon D", 0, 0, Server),
        I(0x8F, "Sapphire Rapids", "Xeon Scalable 4th gen or Xeon W", 0, 0, Server),
        I(0xCF, "Emerald Rapids", "Xeon Scalable 5th gen", 0, 0, Server),
        I(0xAD, "Granite Rapids", "Xeon 6 with P-cores", 0, 0, Server),
        I(0xAF, "Sierra Forest", "Xeon 6 with E-cores", 0, 0, Server),

        // ---- Small cores only
        I(0x5C, "Apollo Lake", "Pentium, Celeron or Atom", 0, 0, LowPower),
        I(0x7A, "Gemini Lake", "Pentium or Celeron", 0, 0, LowPower),
        I(0x96, "Elkhart Lake", "Pentium, Celeron or Atom", 0, 0, LowPower),
        I(0x9C, "Jasper Lake", "Pentium or Celeron", 0, 0, LowPower),
        I(0xBE, "Alder Lake-N", "Intel Processor N-series or Core i3 N-series", 0, 0, LowPower),
    ];

    /// <summary>One AMD model range. A null code name means "this range is this Zen generation, the exact chip is not listed".</summary>
    private sealed record AmdRow(int Family, int ModelMin, int ModelMax, string Generation, string? CodeName, string? Line);

    private static AmdRow A(int family, int modelMin, int modelMax, string generation, string? codeName = null, string? line = null)
        => new(family, modelMin, modelMax, generation, codeName, line);

    /// <summary>Exact models first: the first row that fits wins, so the wide ranges at the end of each family only catch the rest.</summary>
    private static readonly AmdRow[] Amd =
    [
        // ---- family 17h: Zen, Zen+, Zen 2
        A(0x17, 0x01, 0x01, "Zen", "Summit Ridge / Whitehaven / Naples", "Ryzen 1000, Threadripper 1000 or EPYC 7001"),
        A(0x17, 0x08, 0x08, "Zen+", "Pinnacle Ridge / Colfax", "Ryzen 2000 or Threadripper 2000"),
        A(0x17, 0x11, 0x11, "Zen", "Raven Ridge", "Ryzen 2000 with graphics"),
        A(0x17, 0x18, 0x18, "Zen+", "Picasso", "Ryzen 3000 with graphics"),
        A(0x17, 0x20, 0x20, "Zen", "Dali", "Athlon and entry-level laptop"),
        A(0x17, 0x31, 0x31, "Zen 2", "Castle Peak / Rome", "Threadripper 3000 or EPYC 7002"),
        A(0x17, 0x60, 0x60, "Zen 2", "Renoir", "Ryzen 4000 with graphics"),
        A(0x17, 0x68, 0x68, "Zen 2", "Lucienne", "Ryzen 5000 laptop"),
        A(0x17, 0x71, 0x71, "Zen 2", "Matisse", "Ryzen 3000 desktop"),
        A(0x17, 0x90, 0x91, "Zen 2", "Van Gogh", "Handheld"),
        A(0x17, 0xA0, 0xA0, "Zen 2", "Mendocino", "Ryzen 7020 laptop"),
        A(0x17, 0x00, 0x2F, "Zen or Zen+"),
        A(0x17, 0x50, 0x5F, "Zen"),
        A(0x17, 0x30, 0x4F, "Zen 2"),
        A(0x17, 0x60, 0x7F, "Zen 2"),
        A(0x17, 0xA0, 0xAF, "Zen 2"),

        // ---- family 19h: Zen 3, Zen 3+, Zen 4
        A(0x19, 0x01, 0x01, "Zen 3", "Milan", "EPYC 7003"),
        A(0x19, 0x08, 0x08, "Zen 3", "Chagall", "Threadripper PRO 5000"),
        A(0x19, 0x21, 0x21, "Zen 3", "Vermeer", "Ryzen 5000 desktop"),
        A(0x19, 0x40, 0x4F, "Zen 3+", "Rembrandt", "Ryzen 6000 or 7035 laptop"),
        A(0x19, 0x50, 0x5F, "Zen 3", "Cezanne / Barcelo", "Ryzen 5000 with graphics"),
        A(0x19, 0x11, 0x11, "Zen 4", "Genoa", "EPYC 9004"),
        A(0x19, 0x18, 0x18, "Zen 4", "Storm Peak", "Threadripper 7000"),
        A(0x19, 0x60, 0x6F, "Zen 4", "Raphael / Dragon Range", "Ryzen 7000 desktop or HX laptop"),
        A(0x19, 0x70, 0x7F, "Zen 4", "Phoenix / Hawk Point", "Ryzen 7040 or 8040 laptop, Ryzen 8000G desktop"),
        A(0x19, 0xA0, 0xAF, "Zen 4c", "Bergamo / Siena", "EPYC 9004 or 8004"),
        A(0x19, 0x00, 0x0F, "Zen 3"),
        A(0x19, 0x20, 0x3F, "Zen 3"),
        A(0x19, 0x10, 0x1F, "Zen 4"),
        A(0x19, 0x80, 0x9F, "Zen 4"),

        // ---- family 1Ah: Zen 5
        A(0x1A, 0x00, 0x0F, "Zen 5", "Turin / Shimada Peak", "EPYC 9005 or Threadripper 9000"),
        A(0x1A, 0x10, 0x1F, "Zen 5c", "Turin Dense", "EPYC 9005"),
        A(0x1A, 0x20, 0x2F, "Zen 5", "Strix Point", "Ryzen AI 300 laptop"),
        A(0x1A, 0x40, 0x4F, "Zen 5", "Granite Ridge / Fire Range", "Ryzen 9000 desktop or HX laptop"),
        A(0x1A, 0x60, 0x6F, "Zen 5", "Krackan Point", "Ryzen AI 300 laptop"),
        A(0x1A, 0x70, 0x7F, "Zen 5", "Strix Halo", "Ryzen AI Max laptop"),
    ];

    /// <summary>How many entries the table has (Intel models, AMD model ranges).</summary>
    public static (int Intel, int Amd) Counts => (Intel.Length, Amd.Length);

    /// <summary>"Intel", "AMD" or "Other", from the CPUID vendor text.</summary>
    public static string VendorName(string? vendorId) => vendorId?.Trim() switch
    {
        IntelVendorId => "Intel",
        AmdVendorId => "AMD",
        _ => "Other",
    };

    [GeneratedRegex(@"\bi[3579]-(\d{4,5})", RegexOptions.CultureInvariant)]
    private static partial Regex CoreNumberPattern();

    /// <summary>The generation a Core model number says: "i7-8550U" is 8, "i9-13900KF" is 13. Null when the name has none.</summary>
    internal static int? CoreGenerationFromName(string? brand)
    {
        if (string.IsNullOrEmpty(brand)) return null;
        var m = CoreNumberPattern().Match(brand);
        if (!m.Success) return null;
        var digits = m.Groups[1].ValueSpan;
        // Five digits (13900) and four digits starting with 1 (1365U: laptops from the 10th gen on) carry two.
        return int.Parse(digits.Length == 5 || digits[0] == '1' ? digits[..2] : digits[..1], CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\b(?:i[3579]-)?\d{3,5}HX\b", RegexOptions.CultureInvariant)]
    private static partial Regex HxPattern();

    /// <summary>
    /// Intel's HX laptop chips are desktop chips in a laptop package and report the desktop model number
    /// (i9-13980HX is model 0xB7 like the i9-13900K). Only the CPU's own name tells them apart.
    /// </summary>
    internal static bool IsHxLaptopName(string? brand) => !string.IsNullOrEmpty(brand) && HxPattern().IsMatch(brand);

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    /// <summary>
    /// Looks a processor up. <paramref name="brand"/> is the CPU's own name ("13th Gen Intel(R) Core(TM) i9-13900KF");
    /// it is shown when the model is not in the table, and settles the generation where one model number covers two
    /// (Raptor Lake is sold as both 13th and 14th gen).
    /// </summary>
    public static CpuGenerationInfo Lookup(CpuSignature signature, string? brand = null)
    {
        var vendor = VendorName(signature.VendorId);
        if (vendor == "Intel" && signature.Family == 6)
        {
            foreach (var row in Intel)
            {
                if (row.Model != signature.Model || signature.Stepping < row.SteppingMin || signature.Stepping > row.SteppingMax) continue;
                var generation = row.Generation;
                if (row.GenMin > 0 && CoreGenerationFromName(brand) is { } named && named >= row.GenMin && named <= row.GenMax)
                    generation = Ordinal(named) + " gen Core";
                var line = row.Line == Desktop && IsHxLaptopName(brand) ? Laptop : row.Line;
                return new CpuGenerationInfo(true, vendor, generation, row.CodeName, line,
                    $"{generation} ({row.CodeName}), {char.ToLowerInvariant(line[0])}{line[1..]}");
            }
        }
        else if (vendor == "AMD")
        {
            foreach (var row in Amd)
            {
                if (row.Family != signature.Family || signature.Model < row.ModelMin || signature.Model > row.ModelMax) continue;
                var summary = row.CodeName is null ? row.Generation : $"{row.Generation} ({row.CodeName}), {row.Line}";
                return new CpuGenerationInfo(true, vendor, row.Generation, row.CodeName, row.Line, summary);
            }
        }
        var older = vendor switch
        {
            "AMD" => signature.Family < 0x17,
            // Family 15 is the Pentium 4 and Pentium D; below 6 is the Pentium and older.
            "Intel" => signature.Family < 6 || signature.Family == 15 || (signature.Family == 6 && IntelOlderModels.Contains(signature.Model)),
            _ => false,
        };
        return Unknown(vendor, brand, older);
    }

    /// <summary>
    /// The answer for a CPU that is not in the table: its own name, and that it is newer than this version knows, or
    /// (<paramref name="older"/>) that it is from before the list starts.
    /// </summary>
    public static CpuGenerationInfo Unknown(string vendor, string? brand, bool older = false)
    {
        var name = string.IsNullOrWhiteSpace(brand) ? "This processor" : brand.Trim();
        return new CpuGenerationInfo(false, vendor, null, null, null, $"{name}: {(older ? OlderNote : UnknownNote)}");
    }
}
