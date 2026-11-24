using RigControl.Lighting;

namespace RigControl.Lighting.Tests;

public class NanoleafTests
{
    private const string Layout = """
        {"numPanels":4,"sideLength":0,"positionData":[
          {"panelId":11,"x":0,"y":0,"o":0,"shapeType":2},
          {"panelId":22,"x":200,"y":0,"o":0,"shapeType":2},
          {"panelId":33,"x":100,"y":100,"o":0,"shapeType":2},
          {"panelId":99,"x":50,"y":50,"o":0,"shapeType":16}]}
        """;

    [Fact]
    public void Panels_are_fitted_to_the_box_with_y_down_and_connectors_left_out()
    {
        var p = NanoleafClient.ParseLayout(Layout, 0);
        Assert.Equal(new[] { 11, 22, 33 }, p.Select(x => x.Id));
        Assert.Equal((0.0, 1.0), (p[0].X, p[0].Y));
        Assert.Equal((1.0, 1.0), (p[1].X, p[1].Y));
        Assert.Equal((0.5, 0.0), (p[2].X, p[2].Y)); // the highest panel is at the top of the picture
    }

    [Fact]
    public void The_wall_orientation_turns_the_panels()
    {
        var p = NanoleafClient.ParseLayout(Layout, 180);
        Assert.Equal(1.0, p.First(x => x.Id == 11).X, 6); // turned upside down: left becomes right
        Assert.Equal(0.0, p.First(x => x.Id == 22).X, 6);
        Assert.Equal(1.0, p.First(x => x.Id == 33).Y, 6); // and the top panel goes to the bottom
    }

    [Fact]
    public void Lines_bars_get_their_two_ends_at_their_angle()
    {
        const string lines = """
            {"numPanels":2,"sideLength":0,"positionData":[
              {"panelId":1,"x":0,"y":0,"o":0,"shapeType":18},
              {"panelId":2,"x":100,"y":0,"o":90,"shapeType":18}]}
            """;
        var p = NanoleafClient.ParseLayout(lines, 0);
        Assert.Equal(p[0].Y1, p[0].Y2, 6);          // the first bar lies flat
        Assert.True(p[0].X2 > p[0].X1);
        Assert.Equal(p[1].X1, p[1].X2, 6);          // the second stands upright
        Assert.NotEqual(p[1].Y1, p[1].Y2);
        Assert.All(p, x => Assert.InRange(x.X1, 0, 1));
    }

    [Fact]
    public void A_stream_frame_is_count_then_id_RGBW_and_transition_big_endian()
    {
        var b = NanoleafClient.Packet(new[] { (0x1234, 0xAABBCC) }, 1);
        Assert.Equal(new byte[] { 0, 1, 0x12, 0x34, 0xAA, 0xBB, 0xCC, 0, 0, 1 }, b);
    }
}
