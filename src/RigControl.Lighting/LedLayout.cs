namespace RigControl.Lighting;

/// <summary>Where one zone of a device sits on the picture (SignalRGB-style layout). X, Y, W, H are 0 to 1 of the picture.</summary>
public sealed class LayoutItem
{
    /// <summary>The device's stable key (<see cref="RgbController.Key"/>).</summary>
    public string Key { get; set; } = "";
    /// <summary>The zone, or <see cref="LedLayout.WholeDevice"/> (-1) for the whole device in one box.</summary>
    public int Zone { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    /// <summary>A strip runs the other way (right to left, or bottom to top).</summary>
    public bool Reverse { get; set; }
}

public sealed class LayoutSettings
{
    /// <summary>Each placed light takes the colour under it; lights not placed take the whole picture's colour.</summary>
    public bool PerLed { get; set; }
    public List<LayoutItem> Items { get; set; } = new();
}

/// <summary>Works out which part of the picture each LED looks at, and its colour.</summary>
public static class LedLayout
{
    public const int MaxItems = 200;

    /// <summary>The Zone value of a box that holds the whole device.</summary>
    public const int WholeDevice = -1;

    /// <summary>
    /// The whole device as one zone: its only lit zone when it has just one (a keyboard keeps its grid), otherwise
    /// all its LEDs in order as one strip.
    /// </summary>
    public static (RgbZone Zone, int Start) WholeZone(RgbController dev)
    {
        var lit = dev.Zones.Select((z, i) => (z, i)).Where(t => t.z.LedsCount > 0).ToList();
        if (lit.Count == 1) return (lit[0].z, ZoneStart(dev, lit[0].i));
        return (new RgbZone { Name = "Whole device", LedsCount = (uint)dev.Colors.Count }, 0);
    }

    /// <summary>The zone a box stands for and where its LEDs start in the device's colour list; null if it doesn't exist.</summary>
    public static (RgbZone Zone, int Start)? Resolve(RgbController dev, int zone)
    {
        if (zone == WholeDevice) return WholeZone(dev);
        if (zone < 0 || zone >= dev.Zones.Count) return null;
        return (dev.Zones[zone], ZoneStart(dev, zone));
    }

    /// <summary>
    /// The rectangle (0 to 1 of the picture) each LED of a placed zone looks at, by LED number within the zone.
    /// A zone with a grid (a keyboard) uses its grid; any other zone is a strip along its longer side.
    /// </summary>
    public static IEnumerable<(int Led, double X, double Y, double W, double H)> LedRects(LayoutItem item, RgbZone zone)
    {
        var count = (int)zone.LedsCount;
        if (count <= 0) yield break;
        if (zone.Matrix is { } map && zone.MatrixWidth > 0 && zone.MatrixHeight > 0)
        {
            double cw = item.W / zone.MatrixWidth, ch = item.H / zone.MatrixHeight;
            for (var row = 0; row < zone.MatrixHeight; row++)
                for (var col = 0; col < zone.MatrixWidth; col++)
                {
                    var led = map[row * zone.MatrixWidth + col];
                    if (led >= count) continue; // 0xFFFFFFFF: no LED in this cell
                    yield return ((int)led, item.X + col * cw, item.Y + row * ch, cw, ch);
                }
            yield break;
        }
        var across = item.W >= item.H;
        for (var i = 0; i < count; i++)
        {
            var pos = item.Reverse ? count - 1 - i : i;
            if (across)
            {
                var w = item.W / count;
                yield return (i, item.X + pos * w, item.Y, w, item.H);
            }
            else
            {
                var h = item.H / count;
                yield return (i, item.X, item.Y + pos * h, item.W, h);
            }
        }
    }

    /// <summary>The first LED of each zone in the device's colour list.</summary>
    public static int ZoneStart(RgbController dev, int zone)
    {
        var start = 0;
        for (var z = 0; z < zone && z < dev.Zones.Count; z++) start += (int)dev.Zones[z].LedsCount;
        return start;
    }

    /// <summary>
    /// The colour (0xRRGGBB) every LED of a device should take: placed zones from the part of the picture under each LED,
    /// everything else the whole picture's colour (<paramref name="average"/>). Null when none of its zones are placed.
    /// </summary>
    public static int[]? Targets(RgbController dev, IReadOnlyList<LayoutItem> items, byte[]? grid, int gridW, int gridH, int average)
    {
        if (grid is null || dev.Colors.Count == 0) return null;
        int[]? result = null;
        // A whole-device box first, so boxes for single zones of the same device win where both exist.
        foreach (var item in items.Where(i => i.Key == dev.Key).OrderBy(i => i.Zone == WholeDevice ? 0 : 1))
        {
            if (Resolve(dev, item.Zone) is not { } r) continue;
            if (result is null)
            {
                result = new int[dev.Colors.Count];
                Array.Fill(result, average);
            }
            var start = r.Start;
            foreach (var (led, x, y, w, h) in LedRects(item, r.Zone))
            {
                var i = start + led;
                if (i >= 0 && i < result.Length) result[i] = ColorMath.Region(grid, gridW, gridH, x, y, w, h);
            }
        }
        return result;
    }

    /// <summary>A placed device that can only show one colour: the colour under its first placed zone. Null when not placed.</summary>
    public static int? SingleColor(RgbController dev, IReadOnlyList<LayoutItem> items, byte[]? grid, int gridW, int gridH)
    {
        if (grid is null) return null;
        var item = items.FirstOrDefault(i => i.Key == dev.Key && Resolve(dev, i.Zone) is not null);
        return item is null ? null : ColorMath.Region(grid, gridW, gridH, item.X, item.Y, item.W, item.H);
    }

    /// <summary>Checks and tidies items from the page: inside the picture, at least a sliver in size, one per zone.</summary>
    public static List<LayoutItem> Clean(IEnumerable<LayoutItem> items)
    {
        var list = new List<LayoutItem>();
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.Key) || it.Key.Length > 500 || it.Zone is < WholeDevice or > 255) throw new ArgumentException("A placed light names no device.");
            if (!double.IsFinite(it.X) || !double.IsFinite(it.Y) || !double.IsFinite(it.W) || !double.IsFinite(it.H)) throw new ArgumentException("A placed light has no position.");
            var w = Math.Clamp(it.W, 0.01, 1);
            var h = Math.Clamp(it.H, 0.01, 1);
            var x = Math.Clamp(it.X, 0, 1 - w);
            var y = Math.Clamp(it.Y, 0, 1 - h);
            list.RemoveAll(o => o.Key == it.Key && o.Zone == it.Zone);
            list.Add(new LayoutItem { Key = it.Key, Zone = it.Zone, X = x, Y = y, W = w, H = h, Reverse = it.Reverse });
        }
        if (list.Count > MaxItems) throw new ArgumentException($"At most {MaxItems} placed zones.");
        return list;
    }
}
