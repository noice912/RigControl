namespace RigControl.Lighting;

/// <summary>The settings one effect keeps (each effect remembers its own).</summary>
public sealed class EffectParams
{
    /// <summary>1 to 100.</summary>
    public int Speed { get; set; } = 50;
    public string Color1 { get; set; } = "#FF0040";
    public string Color2 { get; set; } = "#0040FF";
    /// <summary>"right", "left", "up" or "down".</summary>
    public string Direction { get; set; } = "right";
    /// <summary>How big the pattern is, 1 to 100.</summary>
    public int Size { get; set; } = 50;

    public EffectParams Clone() => (EffectParams)MemberwiseClone();
}

/// <summary>One entry in the effects library. <see cref="Uses"/> names the settings the effect reads.</summary>
public sealed record EffectInfo(string Id, string Name, string Description, string Group, bool Audio, IReadOnlyList<string> Uses);

/// <summary>
/// Animated lighting effects. Each one paints the same small picture the screen sync uses (BGRA,
/// <see cref="ColorMath.GridW"/> x <see cref="ColorMath.GridH"/>), so the layout, per-LED sending, audio pulses and
/// graphics-card throttling all work the same for every effect.
/// </summary>
public sealed class EffectRenderer
{
    public const string Screen = "screen";

    public static readonly IReadOnlyList<EffectInfo> Catalog = new EffectInfo[]
    {
        new(Screen, "Screen ambience", "Every light takes the colours of your screen or a capture card.", "Screen", false, Array.Empty<string>()),
        new("solid", "Solid colour", "One steady colour everywhere.", "Simple", false, new[] { "color1" }),
        new("breathing", "Breathing", "A colour that slowly fades in and out.", "Simple", false, new[] { "color1", "speed" }),
        new("cycle", "Colour cycle", "Every light moves through the whole colour wheel together.", "Simple", false, new[] { "speed" }),
        new("wave", "Rainbow wave", "A rainbow that rolls across your setup.", "Animated", false, new[] { "speed", "size", "direction" }),
        new("spiral", "Rainbow spiral", "A rainbow turning round the middle.", "Animated", false, new[] { "speed", "size" }),
        new("gradient", "Gradient sweep", "Two colours flowing into each other.", "Animated", false, new[] { "color1", "color2", "speed", "size", "direction" }),
        new("stars", "Starfield", "Stars twinkling on a background colour.", "Animated", false, new[] { "color1", "color2", "speed", "size" }),
        new("rain", "Colour rain", "Drops of colour falling down.", "Animated", false, new[] { "color1", "color2", "speed", "size" }),
        new("visualizer", "Audio visualiser", "Bars that jump with the bass, mids and treble of what the PC plays.", "Audio", true, new[] { "color1", "color2" }),
        new("ripple", "Bass ripple", "Every bass hit sends a ring of colour out from the middle.", "Audio", true, new[] { "color1", "speed", "size" }),
        new("audiocolor", "Sound colour", "The colour follows the music: bass is red, mids green, treble blue.", "Audio", true, Array.Empty<string>()),
    };

    public static bool Exists(string id) => Catalog.Any(e => e.Id == id);
    public static bool IsAudio(string id) => Catalog.FirstOrDefault(e => e.Id == id)?.Audio ?? false;

    private const int W = ColorMath.GridW, H = ColorMath.GridH;
    private readonly Random _rng;
    private readonly List<(double X, double Y, double Born, double Life)> _stars = new();
    private readonly List<(double X, double Y, double Speed, double Len)> _drops = new();
    private readonly List<(double Born, int Hue)> _rings = new();
    private readonly float[] _bars = new float[3];
    private double _lastT;
    private float _prevBass;
    private double _lastRing = -10;

    public EffectRenderer(int? seed = null) => _rng = seed is { } s ? new Random(s) : new Random();

    /// <summary>
    /// Paints <paramref name="grid"/> (BGRA, W x H) at time <paramref name="t"/> seconds, and returns the one colour
    /// (0xRRGGBB) for lights that aren't placed on the layout.
    /// </summary>
    public int Render(string id, EffectParams p, double t, float bass, float mid, float treble, byte[] grid)
    {
        var dt = Math.Clamp(t - _lastT, 0, 0.5);
        _lastT = t;
        var speed = Math.Clamp(p.Speed, 1, 100) / 50.0; // 0.02 .. 2
        var size = Math.Clamp(p.Size, 1, 100) / 50.0;
        var c1 = ParseHex(p.Color1, 0xFF0040);
        var c2 = ParseHex(p.Color2, 0x0040FF);
        switch (id)
        {
            case "solid":
                Fill(grid, c1);
                return c1;
            case "breathing":
            {
                var k = 0.5 - 0.5 * Math.Cos(t * Math.PI * 2 * 0.25 * speed);
                var c = Scale(c1, 0.04 + 0.96 * k);
                Fill(grid, c);
                return c;
            }
            case "cycle":
            {
                var c = Hsv(t * 36 * speed, 1, 1);
                Fill(grid, c);
                return c;
            }
            case "wave":
            {
                var (dx, dy) = Dir(p.Direction);
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var pos = (dx * x / (double)W + dy * y / (double)H) / size;
                        Set(grid, x, y, Hsv(pos * 360 - t * 90 * speed, 1, 1));
                    }
                return Hsv(0.5 / size * 360 - t * 90 * speed, 1, 1);
            }
            case "spiral":
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var ax = (x - W / 2.0) / H;
                        var ay = (y - H / 2.0) / H;
                        var angle = Math.Atan2(ay, ax) * 180 / Math.PI;
                        var r = Math.Sqrt(ax * ax + ay * ay);
                        Set(grid, x, y, Hsv(angle + r * 360 / size - t * 120 * speed, 1, 1));
                    }
                return Hsv(-t * 120 * speed, 1, 1);
            case "gradient":
            {
                var (dx, dy) = Dir(p.Direction);
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var pos = (dx * x / (double)W + dy * y / (double)H) / size - t * 0.25 * speed;
                        Set(grid, x, y, Mix(c1, c2, 0.5 - 0.5 * Math.Cos(pos * Math.PI * 2)));
                    }
                return Mix(c1, c2, 0.5 - 0.5 * Math.Cos(t * 0.25 * speed * Math.PI * 2));
            }
            case "stars":
            {
                var want = (int)(8 + 50 * size);
                var life = 2.5 / Math.Max(0.2, speed);
                _stars.RemoveAll(s => t - s.Born > s.Life);
                while (_stars.Count < want && _rng.NextDouble() < 0.5)
                    _stars.Add((_rng.Next(W), _rng.Next(H), t, life * (0.6 + 0.8 * _rng.NextDouble())));
                Fill(grid, c2);
                foreach (var s in _stars)
                {
                    var age = (t - s.Born) / s.Life;
                    var k = Math.Sin(Math.Clamp(age, 0, 1) * Math.PI);
                    Set(grid, (int)s.X, (int)s.Y, Mix(c2, c1, k));
                }
                return ColorMath.Average(grid, W, H);
            }
            case "rain":
            {
                var want = (int)(6 + 30 * size);
                _drops.RemoveAll(d => d.Y - d.Len > H);
                while (_drops.Count < want && _rng.NextDouble() < 0.3)
                    _drops.Add((_rng.Next(W), -_rng.Next(H / 2), (8 + 14 * _rng.NextDouble()) * speed, 3 + 5 * _rng.NextDouble()));
                for (var i = 0; i < _drops.Count; i++)
                {
                    var d = _drops[i];
                    _drops[i] = d with { Y = d.Y + d.Speed * dt };
                }
                Fill(grid, c2);
                foreach (var d in _drops)
                    for (var j = 0; j < (int)d.Len; j++)
                    {
                        var y = (int)d.Y - j;
                        if (y >= 0 && y < H) Set(grid, (int)d.X, y, Mix(c2, c1, 1 - j / d.Len));
                    }
                return ColorMath.Average(grid, W, H);
            }
            case "visualizer":
            {
                // Three groups of bars (bass, mids, treble), rising quickly and falling slowly, low colour to high colour.
                var levels = new[] { bass, mid, treble };
                for (var b = 0; b < 3; b++)
                    _bars[b] = levels[b] > _bars[b] ? levels[b] : Math.Max(levels[b], _bars[b] - (float)dt * 1.5f);
                for (var x = 0; x < W; x++)
                {
                    var band = Math.Min(2, x * 3 / W);
                    var inBand = (x % (W / 3)) / (double)(W / 3);
                    var level = _bars[band] * (0.75 + 0.25 * Math.Sin(inBand * Math.PI));
                    var height = level * H;
                    for (var y = 0; y < H; y++)
                    {
                        var fromBottom = H - 1 - y;
                        Set(grid, x, y, fromBottom < height ? Mix(c1, c2, fromBottom / (double)(H - 1)) : 0);
                    }
                }
                var loud = Math.Max(_bars[0], Math.Max(_bars[1], _bars[2]));
                return Scale(Mix(c1, c2, loud), 0.15 + 0.85 * loud);
            }
            case "ripple":
            {
                // A new ring on each rising bass hit (at most every 0.12 s); rings fade as they grow.
                if (bass > 0.45f && bass > _prevBass + 0.08f && t - _lastRing > 0.12)
                {
                    _lastRing = t;
                    _rings.Add((t, (int)(t * 40 % 360)));
                }
                _prevBass = bass;
                var life = 1.6 / Math.Max(0.2, speed);
                _rings.RemoveAll(r => t - r.Born > life);
                var hsvC1 = ToHsv(c1);
                var thickness = 0.06 + 0.1 * size;
                Fill(grid, 0);
                var main = 0;
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var ax = (x - W / 2.0) / H;
                        var ay = (y - H / 2.0) / H;
                        var r = Math.Sqrt(ax * ax + ay * ay);
                        double best = 0;
                        var bestHue = 0;
                        foreach (var ring in _rings)
                        {
                            var age = (t - ring.Born) / life;
                            var radius = age * 1.1;
                            var k = Math.Max(0, 1 - Math.Abs(r - radius) / thickness) * (1 - age);
                            if (k > best) { best = k; bestHue = ring.Hue; }
                        }
                        if (best > 0) Set(grid, x, y, Hsv(hsvC1.H + bestHue * 0.25, hsvC1.S, best));
                    }
                main = Scale(c1, Math.Clamp(bass, 0.05f, 1f));
                return main;
            }
            case "audiocolor":
            {
                var total = bass + mid + treble;
                var c = total < 0.02f ? 0x000000
                    : Rgb(255 * bass / Math.Max(total, 1e-3) * Math.Min(1, total),
                          255 * mid / Math.Max(total, 1e-3) * Math.Min(1, total),
                          255 * treble / Math.Max(total, 1e-3) * Math.Min(1, total));
                Fill(grid, c);
                return c;
            }
            default:
                Fill(grid, 0);
                return 0;
        }
    }

    // ---------------------------------------------------------------- colour helpers

    public static int ParseHex(string? hex, int fallback)
    {
        if (hex is null) return fallback;
        var s = hex.Trim().TrimStart('#');
        return s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : fallback;
    }

    private static (double, double) Dir(string? d) => d switch
    {
        "left" => (-1, 0),
        "up" => (0, -1),
        "down" => (0, 1),
        _ => (1, 0),
    };

    private static int Rgb(double r, double g, double b) =>
        ((int)Math.Clamp(r, 0, 255) << 16) | ((int)Math.Clamp(g, 0, 255) << 8) | (int)Math.Clamp(b, 0, 255);

    private static int Scale(int c, double k) => Rgb(((c >> 16) & 255) * k, ((c >> 8) & 255) * k, (c & 255) * k);

    internal static int Mix(int a, int b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Rgb(((a >> 16) & 255) * (1 - k) + ((b >> 16) & 255) * k,
                   ((a >> 8) & 255) * (1 - k) + ((b >> 8) & 255) * k,
                   (a & 255) * (1 - k) + (b & 255) * k);
    }

    internal static int Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Rgb((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    private static (double H, double S, double V) ToHsv(int c)
    {
        double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, b = (c & 255) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static void Set(byte[] grid, int x, int y, int rgb)
    {
        var i = (y * W + x) * 4;
        grid[i] = (byte)(rgb & 255);
        grid[i + 1] = (byte)((rgb >> 8) & 255);
        grid[i + 2] = (byte)((rgb >> 16) & 255);
        grid[i + 3] = 255;
    }

    private static void Fill(byte[] grid, int rgb)
    {
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                Set(grid, x, y, rgb);
    }
}
