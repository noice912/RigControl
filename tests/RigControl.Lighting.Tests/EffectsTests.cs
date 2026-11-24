using RigControl.Lighting;

namespace RigControl.Lighting.Tests;

public class EffectsTests
{
    private static byte[] Grid() => new byte[ColorMath.GridW * ColorMath.GridH * 4];

    private static int Pixel(byte[] g, int x, int y)
    {
        var i = (y * ColorMath.GridW + x) * 4;
        return (g[i + 2] << 16) | (g[i + 1] << 8) | g[i];
    }

    [Fact]
    public void Every_effect_in_the_library_paints_without_failing()
    {
        var r = new EffectRenderer(seed: 1);
        var p = new EffectParams();
        foreach (var e in EffectRenderer.Catalog.Where(e => e.Id != EffectRenderer.Screen))
            for (var t = 0.0; t < 3; t += 0.033)
            {
                var g = Grid();
                var main = r.Render(e.Id, p, t, 0.8f, 0.5f, 0.3f, g);
                Assert.InRange(main, 0, 0xFFFFFF);
            }
    }

    [Fact]
    public void Solid_paints_the_chosen_colour_everywhere()
    {
        var g = Grid();
        var main = new EffectRenderer().Render("solid", new EffectParams { Color1 = "#12AB34" }, 0, 0, 0, 0, g);
        Assert.Equal(0x12AB34, main);
        Assert.Equal(0x12AB34, Pixel(g, 0, 0));
        Assert.Equal(0x12AB34, Pixel(g, ColorMath.GridW - 1, ColorMath.GridH - 1));
    }

    [Fact]
    public void The_rainbow_wave_differs_across_the_picture_and_moves_over_time()
    {
        var r = new EffectRenderer();
        var a = Grid();
        r.Render("wave", new EffectParams(), 0, 0, 0, 0, a);
        Assert.NotEqual(Pixel(a, 0, 5), Pixel(a, ColorMath.GridW / 2, 5));
        var b = Grid();
        r.Render("wave", new EffectParams(), 1, 0, 0, 0, b);
        Assert.NotEqual(Pixel(a, 0, 5), Pixel(b, 0, 5));
    }

    [Fact]
    public void The_visualiser_is_dark_in_silence_and_lit_with_bass()
    {
        var r = new EffectRenderer();
        var quiet = Grid();
        r.Render("visualizer", new EffectParams(), 0, 0, 0, 0, quiet);
        Assert.Equal(0, Pixel(quiet, 2, ColorMath.GridH - 1));
        var loud = Grid();
        r.Render("visualizer", new EffectParams(), 0.03, 1, 0, 0, loud);
        Assert.NotEqual(0, Pixel(loud, 2, ColorMath.GridH - 1));
        Assert.Equal(0, Pixel(loud, ColorMath.GridW - 2, ColorMath.GridH - 1)); // no treble: its bars stay down
    }

    [Theory]
    [InlineData("#FF0000", 0xFF0000)]
    [InlineData("00ff80", 0x00FF80)]
    [InlineData("red", -1)]
    [InlineData(null, -1)]
    public void Colours_are_read_as_hex(string? hex, int expected) => Assert.Equal(expected, EffectRenderer.ParseHex(hex, -1));
}

public class MainColorTests
{
    private static LightDeviceDto Dev(string? color, int leds, bool leftOut = false) =>
        new(0, "d", "LED strip", "v", leds, Array.Empty<LightZoneDto>(), Array.Empty<LightModeDto>(), 0, color, leftOut);

    private static LightingStatusDto Status(params LightDeviceDto[] devices) =>
        new(null!, devices, Array.Empty<string>());

    [Fact]
    public void The_main_colour_is_the_one_most_LEDs_show()
    {
        Assert.Equal(0x00FF00, LightingModule.MainColor(Status(Dev("#FF0000", 10), Dev("#00FF00", 60), Dev("#00ff00", 5))));
    }

    [Fact]
    public void Off_and_left_out_devices_dont_count()
    {
        Assert.Equal(0xFF0000, LightingModule.MainColor(Status(Dev("#FF0000", 1), Dev("#000000", 90), Dev("#0000FF", 90, leftOut: true))));
        Assert.Null(LightingModule.MainColor(Status(Dev("#000000", 5), Dev(null, 5))));
    }
}
