namespace RigControl.Bios;

/// <summary>The CPU settings RigControl can change, as they are now (plain numbers, not BIOS bytes).</summary>
public sealed record CpuEditsDto(
    /// <summary>"intelPerformance", "intelExtreme" or "asus" (ASUS Advanced OC Profile).</summary>
    string Profile,
    /// <summary>Watts; 0 = Auto.</summary>
    int Pl1, int Pl2,
    /// <summary>Millivolts taken off the core voltage (Offset Mode, minus); 0 = Auto. Negative = the BIOS has a plus offset now.</summary>
    int UndervoltMv,
    /// <summary>All P-cores at this ratio (Sync All Cores); 0 = Auto.</summary>
    int AllCoreRatio,
    IReadOnlyList<string> Notes);

public sealed class CpuChange
{
    public string? Profile { get; set; }
    public int? Pl1 { get; set; }
    public int? Pl2 { get; set; }
    public int? UndervoltMv { get; set; }
    public int? AllCoreRatio { get; set; }
}

/// <summary>
/// The four kinds of CPU edit (Intel profile, power limits, undervolt, all-core ratio), mapped onto the bytes of the
/// ASUS settings variable. Offsets and values come from the BIOS 2301 map (checked at start by name); the limits are
/// RigControl's own: power 35-253 W (Intel's 253 W for this chip), undervolt 0-150 mV and never a plus offset,
/// all-core ratio 30-57. Settings that only count together are set together.
/// </summary>
public sealed class CpuEdits
{
    public const int PowerMin = 35, PowerMax = 253, UndervoltMax = 150, RatioMin = 30, RatioMax = 57;

    // Offsets (BIOS 2301) and what is expected at each, checked against the map before anything is used.
    private static readonly (string Key, int Offset, int Size, string Name)[] Fields =
    {
        ("perfPrefs", 348, 1, "Performance Preferences"),
        ("intelProfile", 352, 1, "Intel Default Settings"),
        ("pl1", 1358, 2, "Long Duration Package Power Limit"),
        ("pl2", 1362, 2, "Short Duration Package Power Limit"),
        ("pCoreRatioMode", 55, 1, "Performance Core Ratio"),
        ("allCoreRatio", 56, 1, "1-Core Ratio Limit"),
        ("coreVoltageMode", 375, 1, "Global Core SVID Voltage"),
        ("offsetSign", 378, 1, "Offset Mode Sign"),
        ("offsetMv", 373, 2, "Offset Voltage"),
    };

    private const int PerfIntel = 0, PerfAsus = 2, IntelPerformance = 1, IntelExtreme = 2;
    private const int RatioAuto = 0, RatioSyncAll = 1, VoltAuto = 0, VoltOffset = 2, SignPlus = 0, SignMinus = 1;

    private readonly Dictionary<string, (int Offset, int Size)> _at;

    private CpuEdits(Dictionary<string, (int, int)> at) => _at = at;

    /// <summary>The edits for this map, or null (with why) when the map isn't the one these offsets were checked against.</summary>
    public static CpuEdits? For(BiosMap map, out string? problem)
    {
        var at = new Dictionary<string, (int, int)>();
        foreach (var f in Fields)
        {
            var s = map.Settings.FirstOrDefault(x => x.Offset == f.Offset);
            if (s is null || s.Size != f.Size || !string.Equals(s.Name, f.Name, StringComparison.Ordinal))
            {
                problem = $"The BIOS map doesn't have \"{f.Name}\" where RigControl expects it, so CPU settings can't be changed.";
                return null;
            }
            at[f.Key] = (f.Offset, f.Size);
        }
        problem = null;
        return new CpuEdits(at);
    }

    private int Get(byte[] d, string key)
    {
        var (o, n) = _at[key];
        return n == 1 ? d[o] : d[o] | (d[o + 1] << 8);
    }

    private void Set(byte[] d, string key, int v)
    {
        var (o, n) = _at[key];
        d[o] = (byte)(v & 0xFF);
        if (n == 2) d[o + 1] = (byte)((v >> 8) & 0xFF);
    }

    public CpuEditsDto Read(byte[] d)
    {
        var notes = new List<string>();
        var perf = Get(d, "perfPrefs");
        var profile = perf == PerfAsus ? "asus" : Get(d, "intelProfile") == IntelPerformance ? "intelPerformance" : "intelExtreme";
        int pl1 = Get(d, "pl1"), pl2 = Get(d, "pl2");
        if (perf != PerfAsus && (pl1 != 0 || pl2 != 0)) notes.Add("Power limits only count with the ASUS Advanced OC Profile; Intel's profile uses its own.");
        var mode = Get(d, "coreVoltageMode");
        var mv = Get(d, "offsetMv");
        var undervolt = mode == VoltOffset ? (Get(d, "offsetSign") == SignMinus ? mv : -mv) : 0;
        if (mode is not (VoltAuto or VoltOffset)) notes.Add("The core voltage is in a mode RigControl doesn't change (Manual or Adaptive); set it in the BIOS.");
        if (undervolt < 0) notes.Add($"The BIOS has a plus offset of {-undervolt} mV. RigControl only takes voltage off.");
        var ratioMode = Get(d, "pCoreRatioMode");
        var ratio = ratioMode == RatioSyncAll ? Get(d, "allCoreRatio") : 0;
        if (ratioMode is not (RatioAuto or RatioSyncAll)) notes.Add("The P-core ratio is set per core in the BIOS; RigControl only does Auto or one ratio for all cores.");
        return new CpuEditsDto(profile, pl1, pl2, undervolt, ratio, notes);
    }

    /// <summary>A copy of the variable with the changes made, or an error saying what isn't allowed. Lines describing each change go to <paramref name="changes"/>.</summary>
    public byte[] Apply(byte[] current, CpuChange c, List<string> changes)
    {
        var d = (byte[])current.Clone();
        var now = Read(current);

        var profile = c.Profile ?? now.Profile;
        int pl1 = c.Pl1 ?? now.Pl1, pl2 = c.Pl2 ?? now.Pl2;
        if (pl1 is not 0 and (< PowerMin or > PowerMax) || pl2 is not 0 and (< PowerMin or > PowerMax))
            throw new ArgumentException($"Power limits are Auto or {PowerMin} to {PowerMax} W.");
        if (pl1 != 0 && pl2 != 0 && pl2 < pl1) throw new ArgumentException("The short-term power limit (PL2) can't be lower than the long-term one (PL1).");
        // Custom power limits only count outside Intel's own profile.
        if ((c.Pl1 is > 0 || c.Pl2 is > 0) && profile != "asus")
        {
            if (c.Profile is not null) throw new ArgumentException("Custom power limits need the ASUS Advanced OC Profile; Intel's profile uses its own limits.");
            profile = "asus";
        }
        switch (profile)
        {
            case "intelPerformance": Set(d, "perfPrefs", PerfIntel); Set(d, "intelProfile", IntelPerformance); break;
            case "intelExtreme": Set(d, "perfPrefs", PerfIntel); Set(d, "intelProfile", IntelExtreme); break;
            case "asus": Set(d, "perfPrefs", PerfAsus); break;
            default: throw new ArgumentException("Unknown profile.");
        }
        Set(d, "pl1", pl1);
        Set(d, "pl2", pl2);

        if (c.UndervoltMv is { } uv)
        {
            if (uv < 0 || uv > UndervoltMax) throw new ArgumentException($"The undervolt is 0 (off) to {UndervoltMax} mV. RigControl never adds voltage.");
            if (uv == 0)
            {
                Set(d, "coreVoltageMode", VoltAuto);
                Set(d, "offsetSign", SignPlus);
                Set(d, "offsetMv", 0);
            }
            else
            {
                Set(d, "coreVoltageMode", VoltOffset);
                Set(d, "offsetSign", SignMinus);
                Set(d, "offsetMv", uv);
            }
        }

        if (c.AllCoreRatio is { } ratio)
        {
            if (ratio != 0 && (ratio < RatioMin || ratio > RatioMax)) throw new ArgumentException($"The all-core ratio is Auto or {RatioMin} to {RatioMax}.");
            Set(d, "pCoreRatioMode", ratio == 0 ? RatioAuto : RatioSyncAll);
            Set(d, "allCoreRatio", ratio);
        }

        var after = Read(d);
        if (after.Profile != now.Profile) changes.Add($"profile {now.Profile} -> {after.Profile}");
        if (after.Pl1 != now.Pl1) changes.Add($"PL1 {W(now.Pl1)} -> {W(after.Pl1)}");
        if (after.Pl2 != now.Pl2) changes.Add($"PL2 {W(now.Pl2)} -> {W(after.Pl2)}");
        if (after.UndervoltMv != now.UndervoltMv) changes.Add($"undervolt {now.UndervoltMv} mV -> {after.UndervoltMv} mV");
        if (after.AllCoreRatio != now.AllCoreRatio) changes.Add($"all-core ratio {R(now.AllCoreRatio)} -> {R(after.AllCoreRatio)}");
        return d;
    }

    private static string W(int w) => w == 0 ? "Auto" : w + " W";
    private static string R(int r) => r == 0 ? "Auto" : "x" + r;

    /// <summary>The byte offsets a CPU edit may touch (everything else must stay as it was).</summary>
    public IEnumerable<int> TouchedBytes() => _at.Values.SelectMany(v => Enumerable.Range(v.Offset, v.Size));
}
