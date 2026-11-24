using RigControl.App;

namespace RigControl.App.Tests;

public class PadTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] DualSenseUsb(byte buttons = 0x08, byte ps = 0, byte lx = 128, byte ly = 128)
    {
        var r = new byte[64];
        r[0] = 0x01; r[1] = lx; r[2] = ly; r[3] = 128; r[4] = 128;
        r[8] = buttons; // two before the PS byte; low 4 bits = hat (8 = released)
        r[10] = ps;
        return r;
    }

    [Fact]
    public void DualSense_reports_turn_into_the_same_buttons_as_an_Xbox_controller()
    {
        Assert.Equal(Pad.LOGO, Pad.Parse(1, DualSenseUsb(ps: 1)));
        Assert.Equal(Pad.ACCEPT, Pad.Parse(1, DualSenseUsb(buttons: 0x28)));       // Cross, hat released
        Assert.Equal(Pad.BACK | Pad.UP, Pad.Parse(1, DualSenseUsb(buttons: 0x40))); // Circle with the hat at 0 (up)
        Assert.Equal(Pad.LEFT, Pad.Parse(1, DualSenseUsb(lx: 10)));                 // left stick pushed left

        var bt = new byte[78];
        bt[0] = 0x31; bt[2] = 128; bt[3] = 128; bt[9] = 0x08; bt[11] = 1;          // Bluetooth full report: PS at 11
        Assert.Equal(Pad.LOGO, Pad.Parse(1, bt));

        var ds4 = new byte[64];
        ds4[0] = 0x01; ds4[1] = 128; ds4[2] = 128; ds4[5] = 0x18; ds4[7] = 1;       // DualShock 4 USB: Square + PS
        Assert.Equal(Pad.LOGO | Pad.SECOND, Pad.Parse(2, ds4));
        Assert.Equal(-1, Pad.Parse(1, new byte[] { 0x05, 0, 0, 0, 0, 0, 0, 0 }));   // another report id
        Assert.Equal(-1, Pad.Parse(0, DualSenseUsb()));
    }

    [Fact]
    public void Sticks_and_the_pointer_curve()
    {
        var s = Pad.Sticks(1, DualSenseUsb(lx: 255, ly: 0))!;
        Assert.Equal(1f, s[0], 2);
        Assert.Equal(1f, s[1], 1); // up is positive (a full push reads 128/127, as in RichPresence)
        Assert.Equal(0f, Pad.Curve(0.1f));
        Assert.Equal(1f, Pad.Curve(1f));
        Assert.True(Pad.Curve(-0.5f) < 0);
    }

    [Fact]
    public void The_logo_button_counts_once_even_when_reported_twice()
    {
        var pad = new Pad(createWindow: false) { Dry = true };
        var presses = 0;
        pad.Pressed += () => presses++;
        pad.Step(Pad.LOGO, T0);
        pad.Step(0, T0.AddMilliseconds(50));
        pad.Step(Pad.LOGO, T0.AddMilliseconds(100)); // Steam's copy of the same press
        Assert.Equal(1, presses);
        pad.Step(0, T0.AddMilliseconds(600));
        pad.Step(Pad.LOGO, T0.AddMilliseconds(700));
        Assert.Equal(2, presses);
    }

    [Fact]
    public void Held_directions_repeat_in_the_panel()
    {
        var pad = new Pad(createWindow: false) { Dry = true, Nav = true };
        var moves = new List<string>();
        pad.Moved += moves.Add;
        pad.Step(Pad.DOWN, T0);
        pad.Step(Pad.DOWN, T0.AddMilliseconds(200));   // not yet
        pad.Step(Pad.DOWN, T0.AddMilliseconds(400));   // first repeat after 380 ms
        pad.Step(Pad.DOWN, T0.AddMilliseconds(530));   // then every 120 ms
        pad.Step(Pad.ACCEPT, T0.AddMilliseconds(600));
        pad.Step(Pad.BACK, T0.AddMilliseconds(700));
        Assert.Equal(new[] { "Down", "Down", "Down", "Accept", "Back" }, moves);
    }

    [Fact]
    public void Inside_Discord_the_controller_is_a_mouse()
    {
        var pad = new Pad(createWindow: false) { Dry = true, Nav = true, Pointer = true };
        var moves = new List<string>();
        pad.Moved += moves.Add;
        pad.LX = 1f;
        pad.Step(0, T0);
        Assert.Contains("move(26,0)", pad.Did);
        pad.LX = 0;
        pad.Step(Pad.ACCEPT, T0.AddMilliseconds(20));
        Assert.Contains("leftdown", pad.Did);
        pad.Step(0, T0.AddMilliseconds(40));
        Assert.Contains("leftup", pad.Did);
        pad.Step(Pad.UP, T0.AddMilliseconds(60));
        Assert.Contains("key(Up)", pad.Did);
        pad.Step(Pad.KEYS, T0.AddMilliseconds(80));
        pad.Step(Pad.BACK, T0.AddMilliseconds(100));
        Assert.Equal(new[] { "Keyboard", "Back" }, moves);
    }

    [Fact]
    public void With_the_keyboard_up_every_button_is_reported()
    {
        var pad = new Pad(createWindow: false) { Dry = true, Nav = true, Pointer = true, Keys = true };
        var moves = new List<string>();
        pad.Moved += moves.Add;
        pad.Step(Pad.SECOND, T0);
        pad.Step(Pad.ENTER, T0.AddMilliseconds(20));
        pad.Step(Pad.RIGHT, T0.AddMilliseconds(40));
        Assert.Equal(new[] { "Second", "Enter", "Right" }, moves);
    }

    [Fact]
    public void Any_use_of_a_PlayStation_controller_counts_as_busy()
    {
        Assert.False(Pad.Busy(1, DualSenseUsb()));                  // at rest
        Assert.True(Pad.Busy(1, DualSenseUsb(buttons: 0x28)));      // Cross
        Assert.True(Pad.Busy(1, DualSenseUsb(lx: 250)));            // stick
        var r = DualSenseUsb(); r[6] = 200;                         // R2 pulled
        Assert.True(Pad.Busy(1, r));
        r = DualSenseUsb(); r[9] = 0x02;                            // R1
        Assert.True(Pad.Busy(1, r));
        var ds4 = new byte[64];
        ds4[0] = 0x01; ds4[1] = ds4[2] = ds4[3] = ds4[4] = 128; ds4[5] = 0x08; ds4[7] = 0xFC; // counter bits only
        Assert.False(Pad.Busy(2, ds4));
    }

    [Fact]
    public void Remote_Play_only_runs_while_the_controller_is_still()
    {
        var t = T0;
        Assert.False(GamePanel.ShouldRun(t, t.AddSeconds(-1), DateTime.MinValue));          // not due yet
        Assert.True(GamePanel.ShouldRun(t, t.AddSeconds(-2), t.AddSeconds(-1)));             // due and still
        Assert.False(GamePanel.ShouldRun(t, t.AddSeconds(-2), t.AddMilliseconds(-100)));     // due but just pressed
        Assert.True(GamePanel.ShouldRun(t, t.AddSeconds(-9), t));                            // overdue: runs anyway
    }

    [Theory]
    [InlineData("Ctrl+Alt+G", true)]
    [InlineData("Alt+F9", true)]
    [InlineData("G", false)]
    [InlineData("Ctrl+Banana", false)]
    public void Shortcuts_are_parsed(string text, bool ok) => Assert.Equal(ok, GamePanel.TryParseHotkey(text, out _, out _));
}
