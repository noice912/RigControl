using System.Windows;

namespace RigControl.App.Tests;

public class PanelPlacementTests
{
    private static PanelPlacement.Placement At(int w, int h, double scale) =>
        PanelPlacement.For(new Int32Rect(0, 0, w, h), scale, GamePanel.BaseWidth, GamePanel.WideWidth, GamePanel.PanelHeight, GamePanel.MaxPanelHeight);

    [Fact]
    public void A_1440p_screen_at_150_percent_gets_the_wide_panel_in_pixels_of_that_scale()
    {
        // 2560x1440 at 150% is 1707x960 in page units: room for the wide layout.
        var p = At(2560, 1440, 1.5);
        Assert.Equal(GamePanel.WideWidth, p.PanelWidthDip);
        Assert.Equal(2352, p.Panel.Width);                 // 1568 x 1.5, not 1568 pixels
        Assert.Equal(720, p.PanelHeightDip);               // three quarters of 960
        Assert.Equal(1080, p.Panel.Height);
        Assert.Equal((2560 - 2352) / 2, p.Panel.X);        // centred
        Assert.Equal(new Int32Rect(0, 0, 2560, 1440), p.Screen); // the shaded background covers the screen exactly
    }

    [Theory]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1920, 1080, 1.25)]
    [InlineData(1920, 1080, 1.5)]
    [InlineData(2560, 1440, 1.0)]
    [InlineData(3840, 2160, 1.5)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(1366, 768, 1.0)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(3440, 1440, 1.0)]
    public void The_panel_always_fits_on_the_screen_with_a_margin(int w, int h, double scale)
    {
        var p = At(w, h, scale);
        Assert.True(p.Panel.X >= 0 && p.Panel.Y >= 0);
        Assert.True(p.Panel.X + p.Panel.Width <= w && p.Panel.Y + p.Panel.Height <= h);
        Assert.True(p.PanelWidthDip <= w / scale - 32 + 0.001);
        Assert.True(p.PanelHeightDip <= h / scale - 32 + 0.001);
    }

    [Fact]
    public void A_small_screen_gets_the_narrow_layout()
    {
        // 1920x1080 at 150% is 1280x720 in page units: no room for the 1568-wide layout.
        var p = At(1920, 1080, 1.5);
        Assert.Equal(GamePanel.BaseWidth, p.PanelWidthDip);
    }

    [Fact]
    public void A_second_screen_to_the_left_keeps_the_panel_on_that_screen()
    {
        var p = PanelPlacement.For(new Int32Rect(-1920, 0, 1920, 1080), 1.0, GamePanel.BaseWidth, GamePanel.WideWidth, GamePanel.PanelHeight, GamePanel.MaxPanelHeight);
        Assert.True(p.Panel.X >= -1920 && p.Panel.X + p.Panel.Width <= 0);
    }

    [Fact]
    public void A_nonsense_scale_is_treated_as_100_percent()
    {
        Assert.Equal(1, At(1920, 1080, 0).Scale);
        Assert.Equal(1, At(1920, 1080, double.NaN).Scale);
    }
}
