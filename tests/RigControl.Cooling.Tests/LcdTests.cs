using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Cooling.Tests;

public class KrakenLcdProtocolTests
{
    [Fact]
    public void Bulk_header_is_magic_then_type_then_length()
    {
        var h = KrakenLcdProtocol.BulkHeader(KrakenLcdProtocol.AssetLiveFrame, KrakenLcdProtocol.FrameBytes);
        Assert.Equal(Hex.Parse("12 FA 01 E8 AB CD EF 98 76 54 32 10 09 00 00 00 00 C0 12 00", 20), h);
        Assert.Equal(1_228_800, KrakenLcdProtocol.FrameBytes);
    }

    [Fact]
    public void Packets_match_the_reference()
    {
        Assert.Equal(Hex.Parse("36 01 00 01 09"), KrakenLcdProtocol.StartLiveFrame());
        Assert.Equal(Hex.Parse("36 02"), KrakenLcdProtocol.EndTransfer());
        Assert.Equal(Hex.Parse("38 01 02 00"), KrakenLcdProtocol.ShowLiquid());
        Assert.Equal(Hex.Parse("38 01 04 05"), KrakenLcdProtocol.ShowBucket(5));
        Assert.Equal(Hex.Parse("32 01 03 04 10 27 00 02 01"), KrakenLcdProtocol.SetupBucket(3, 0x2710, 0x0200));
        Assert.Equal(Hex.Parse("30 02 01 50 00 00 00 1E"), KrakenLcdProtocol.LiveBrightness(80));
        var enter = KrakenLcdProtocol.EnterLiveMode(80);
        Assert.Equal(Hex.Parse("10 02"), enter[0]);
        Assert.Equal(Hex.Parse("70 02 01 B8 0B"), enter[1]);
        Assert.Equal(Hex.Parse("30 02 00 00 00 00 1E"), enter[6]);
        Assert.Equal(8 + 16 + 1, enter.Count);
    }

    [Fact]
    public void Everything_built_is_allowed_and_nothing_else()
    {
        foreach (var p in KrakenLcdProtocol.EnterLiveMode(50))
            Assert.True(KrakenLcdProtocol.IsAllowed(p) || KrakenProtocol.IsAllowed(p));
        Assert.True(KrakenLcdProtocol.IsAllowed(KrakenLcdProtocol.StartBucketTransfer(15)));
        Assert.True(KrakenLcdProtocol.IsAllowed(KrakenLcdProtocol.QueryBucket(0)));
        Assert.False(KrakenLcdProtocol.IsAllowed(Hex.Parse("38 01 03 00")));
        Assert.False(KrakenLcdProtocol.IsAllowed(Hex.Parse("38 01 04 10")));
        Assert.False(KrakenLcdProtocol.IsAllowed(Hex.Parse("36 01 10")));
        Assert.False(KrakenLcdProtocol.IsAllowed(Hex.Parse("72 01 01 00")));
        Assert.False(KrakenLcdProtocol.IsAllowed(Hex.Parse("70 02 01 B8 01")));
        Assert.False(KrakenLcdProtocol.IsAllowed(Hex.Parse("20 03")));
        Assert.Throws<ArgumentOutOfRangeException>(() => KrakenLcdProtocol.DeleteBucket(16));
        Assert.Throws<ArgumentOutOfRangeException>(() => KrakenLcdProtocol.SetupBucket(0, 24000, 400));
    }

    [Fact]
    public void Replies_are_matched_by_code_or_echo()
    {
        Assert.True(KrakenLcdProtocol.IsReplyTo(Hex.Parse("36 02"), Hex.Parse("37 02")));
        Assert.False(KrakenLcdProtocol.IsReplyTo(Hex.Parse("36 02"), Hex.Parse("75 02")));
        var echo = Hex.Parse("FF");
        echo[14] = 0x36; echo[15] = 0x01;
        Assert.True(KrakenLcdProtocol.IsReplyTo(Hex.Parse("36 01 00 01 09"), echo));
    }

    [Fact]
    public void Storage_planning_follows_liquidctl()
    {
        static List<KrakenLcdProtocol.BucketInfo> Empty() => Enumerable.Range(0, 16).Select(_ => new KrakenLcdProtocol.BucketInfo(false, 0, 0)).ToList();

        // Nothing stored: start at 0.
        Assert.Equal(0, KrakenLcdProtocol.PlanOffset(Empty(), 0, 500));

        // Fits in the slot's existing space: reuse it.
        var b = Empty();
        b[2] = new(true, 1000, 800);
        Assert.Equal(1000, KrakenLcdProtocol.PlanOffset(b, 2, 600));

        // Another slot is in the way: go after everything.
        b = Empty();
        b[0] = new(true, 0, 2000);
        Assert.Equal(2000, KrakenLcdProtocol.PlanOffset(b, 1, 500));

        // Full: signal "wipe and start over".
        b = Empty();
        b[0] = new(true, 0, 24000);
        Assert.Equal(-1, KrakenLcdProtocol.PlanOffset(b, 1, 500));
    }

    [Fact]
    public void Bucket_info_parse()
    {
        var r = Hex.Parse("31 04");
        Assert.False(KrakenLcdProtocol.BucketInfo.Parse(r).Occupied);
        r[15] = 3; r[16] = 4; r[17] = 0x10; r[18] = 0x27; r[19] = 0x00; r[20] = 0x02;
        Assert.Equal(new KrakenLcdProtocol.BucketInfo(true, 0x2710, 0x0200), KrakenLcdProtocol.BucketInfo.Parse(r));
    }
}

public class LcdRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-lcd-tests-" + Guid.NewGuid().ToString("N"));

    public LcdRendererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("single")]
    [InlineData("dual")]
    public void Layouts_draw_a_full_frame(string layout)
    {
        using var bmp = LcdRenderer.Draw(layout, "cpuTemp", "gpuTemp", "#FF0000", new LcdMetrics(61, 48, 31.5, 12, 80));
        var frame = LcdRenderer.ToLiveFrame(bmp, 90);
        Assert.Equal(KrakenLcdProtocol.FrameBytes, frame.Length);
        Assert.Contains(frame, v => v > 200);          // something was drawn
        Assert.StartsWith("data:image/png;base64,", LcdRenderer.ToPngDataUrl(bmp));
        // Missing readings must not crash.
        using var empty = LcdRenderer.Draw(layout, "liquidTemp", "gpuLoad", null, LcdMetrics.Empty);
    }

    [Fact]
    public void Live_frames_are_blue_green_red_and_rotate_clockwise()
    {
        using var bmp = new Bitmap(640, 640, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.FillRectangle(Brushes.Red, 0, 0, 320, 320); // top-left quarter
        }
        var upright = LcdRenderer.ToLiveFrame(bmp, 0);
        Assert.Equal(new byte[] { 0, 0, 255 }, upright[..3]);                       // B, G, R of a red pixel
        var turned = LcdRenderer.ToLiveFrame(bmp, 90);
        Assert.Equal(new byte[] { 0, 0, 0 }, turned[..3]);                          // top-left is now black
        Assert.Equal(new byte[] { 0, 0, 255 }, turned[(639 * 3)..(639 * 3 + 3)]);   // top-right is red
    }

    [Fact]
    public void Gif_writer_output_decodes_to_the_same_pixels()
    {
        const int w = 200, h = 200;
        var palette = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            palette[i * 3] = (byte)i;
            palette[i * 3 + 1] = (byte)(255 - i);
            palette[i * 3 + 2] = (byte)(i * 7);
        }
        var rng = new Random(7);
        var noisy = new byte[w * h];       // forces the code table to fill and reset several times
        rng.NextBytes(noisy);
        var flat = new byte[w * h];        // long runs
        for (var i = 0; i < flat.Length; i++) flat[i] = (byte)(i / 5000);

        using var ms = new MemoryStream();
        var gif = new GifWriter(ms, w, h, loop: true);
        gif.AddFrame(palette, noisy, 5);
        gif.AddFrame(palette, flat, 20);
        gif.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        Assert.Equal(2, img.GetFrameCount(FrameDimension.Time));
        foreach (var (frame, indices) in new[] { (0, noisy), (1, flat) })
        {
            img.SelectActiveFrame(FrameDimension.Time, frame);
            using var copy = new Bitmap(img);
            foreach (var (x, y) in new[] { (0, 0), (199, 0), (57, 123), (0, 199), (199, 199), (100, 100) })
            {
                var i = indices[y * w + x];
                var c = copy.GetPixel(x, y);
                Assert.Equal((palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]), (c.R, c.G, c.B));
            }
        }
    }

    [Fact]
    public void A_picture_becomes_a_640_gif()
    {
        var path = Path.Combine(_dir, "wide.png");
        using (var src = new Bitmap(300, 120))
        {
            using (var g = Graphics.FromImage(src)) g.Clear(Color.FromArgb(10, 200, 30));
            src.Save(path, ImageFormat.Png);
        }
        var gif = LcdRenderer.PrepareGif(path, 180);
        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        using var img = Image.FromStream(new MemoryStream(gif));
        Assert.Equal((640, 640), (img.Width, img.Height));
        using var copy = new Bitmap(img);
        var c = copy.GetPixel(320, 320);
        Assert.InRange(c.G, 190, 210);
    }

    [Fact]
    public void An_animation_keeps_its_frames_and_timing()
    {
        // Build a 3-frame animation with the writer itself, then run it through the preparation step.
        var path = Path.Combine(_dir, "anim.gif");
        var palette = new byte[768];
        palette[3] = 255;            // index 1 = red
        palette[7] = 255;            // index 2 = green
        using (var fs = File.Create(path))
        {
            var w = new GifWriter(fs, 64, 64, loop: true);
            for (var f = 0; f < 3; f++) w.AddFrame(palette, Enumerable.Repeat((byte)f, 64 * 64).ToArray(), 25);
            w.Finish();
        }
        var gif = LcdRenderer.PrepareGif(path, 0);
        using var img = Image.FromStream(new MemoryStream(gif));
        Assert.Equal(3, img.GetFrameCount(FrameDimension.Time));
        var delays = img.GetPropertyItem(0x5100)!.Value!;
        Assert.Equal(25, BitConverter.ToInt32(delays, 0));
        img.SelectActiveFrame(FrameDimension.Time, 1);
        using var second = new Bitmap(img);
        Assert.True(second.GetPixel(10, 10).R > 240);
    }
}

internal sealed class FakePipe : ILcdPipe
{
    public List<byte[]> Writes { get; } = new();
    public bool Disposed { get; private set; }
    public bool Fail { get; set; }

    public void Write(byte[] data, int offset, int count)
    {
        if (Fail) throw new IOException("unplugged");
        Writes.Add(data.AsSpan(offset, count).ToArray());
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakePipeSource : ILcdPipeSource
{
    public List<FakePipe> Opened { get; } = new();
    public bool Available { get; set; } = true;

    public ILcdPipe? Open()
    {
        if (!Available) return null;
        var p = new FakePipe();
        Opened.Add(p);
        return p;
    }
}

public class LcdModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-lcdmod-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBus _bus = new();
    private readonly FakeCam _cam = new();
    private readonly FakePipeSource _pipes = new();

    public LcdModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A cooler that answers cooling commands and acknowledges every screen command.</summary>
    private static FakeLink Kraken()
    {
        var link = new FakeLink();
        var brightness = 70;
        var orientation = 1; // 90 degrees
        link.Reply = w =>
        {
            switch (w[0], w[1])
            {
                case (0x74, 0x01):
                {
                    var s = Hex.Parse("75 01");
                    s[15] = 31; s[16] = 4; s[17] = 0x4C; s[18] = 0x09; s[19] = 70; s[23] = 0xAC; s[24] = 0x03; s[25] = 45;
                    return s;
                }
                case (0x10, 0x01):
                {
                    var fw = Hex.Parse("11 01");
                    fw[17] = 2; fw[18] = 1;
                    return fw;
                }
                case (0x30, 0x01):
                {
                    var lcd = Hex.Parse("31 01");
                    lcd[24] = (byte)brightness; lcd[26] = (byte)orientation;
                    return lcd;
                }
                case (0x30, 0x02):
                    if (w[2] == 0x01 && w[6] == 0x01)
                    {
                        brightness = w[3];
                        orientation = w[7];
                    }
                    return null;
                case (0x72, _):
                    return null;
                default:
                {
                    var ack = new byte[64];
                    ack[0] = (byte)(w[0] + 1);
                    ack[1] = w[1];
                    ack[14] = 0x01;
                    return ack;
                }
            }
        };
        return link;
    }

    private async Task<CoolingModule> Start()
    {
        var module = new CoolingModule(_bus, _cam, runLoop: false, lcd: _pipes);
        var ctx = new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, false, "1.0.0", "x.exe"));
        await module.StartAsync(ctx, CancellationToken.None);
        module.Tick();
        return module;
    }

    private static async Task<JsonElement> Call(CoolingModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        var result = await m.InvokeAsync(method, a, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
    }

    private static bool Starts(byte[] packet, params byte[] head) => packet.AsSpan(0, head.Length).SequenceEqual(head);

    [Fact]
    public async Task Nothing_touches_the_screen_until_the_user_chooses_something()
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        m.Tick();
        m.Tick();
        Assert.Empty(_pipes.Opened);
        Assert.DoesNotContain(link.Written, p => p[0] is 0x32 or 0x36 or 0x38);
        var screen = (await Call(m, "getDevices")).GetProperty("devices")[0].GetProperty("screen");
        Assert.Equal("unset", screen.GetProperty("mode").GetString());
        Assert.False(screen.GetProperty("live").GetBoolean());
    }

    [Fact]
    public async Task Infographic_sends_whole_frames_and_keeps_them_coming()
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        m.SetMetrics(new LcdMetrics(62, 55, null, 20, 90));

        var device = await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "infographic", layout = "dual", primary = "cpuTemp", secondary = "gpuTemp" });
        var screen = device.GetProperty("screen");
        Assert.Equal("infographic", screen.GetProperty("mode").GetString());
        Assert.True(screen.GetProperty("live").GetBoolean());
        // Rotation and brightness were taken from the cooler before live mode started.
        Assert.Equal(90, device.GetProperty("lcd").GetProperty("orientation").GetInt32());
        Assert.Equal(70, device.GetProperty("lcd").GetProperty("brightness").GetInt32());

        var pipe = Assert.Single(_pipes.Opened);
        Assert.Equal(KrakenLcdProtocol.BulkHeader(KrakenLcdProtocol.AssetLiveFrame, KrakenLcdProtocol.FrameBytes), pipe.Writes[0]);
        Assert.Equal(KrakenLcdProtocol.FrameBytes, pipe.Writes.Skip(1).Sum(w => w.Length));
        Assert.All(pipe.Writes.Skip(1), w => Assert.True(w.Length <= KrakenLcdProtocol.StreamChunk));

        // The live-mode handshake came first, and the frame was announced before its data and closed after.
        var screenCommands = link.Written.Where(p => p[0] is 0x36 or 0x38 or 0x32 || (p[0] == 0x10 && p[1] == 0x02)).ToList();
        Assert.True(Starts(screenCommands[0], 0x10, 0x02));
        Assert.True(Starts(screenCommands[^2], 0x36, 0x01, 0x00, 0x01, 0x09));
        Assert.True(Starts(screenCommands[^1], 0x36, 0x02));
        Assert.Equal(16, screenCommands.Count(p => Starts(p, 0x32, 0x02)));

        var before = pipe.Writes.Count;
        m.Tick();
        Assert.Equal(before * 2, pipe.Writes.Count);                      // one more frame, no second handshake
        Assert.Equal(1, link.Written.Count(p => Starts(p, 0x10, 0x02)));

        // Changing rotation in live mode needs no command to the cooler; brightness uses the live form.
        var hid = link.Written.Count;
        await Call(m, "setLcd", new { deviceId = "kraken", orientation = 180 });
        Assert.Equal(hid, link.Written.Count);
        await Call(m, "setLcd", new { deviceId = "kraken", brightness = 40 });
        Assert.True(Starts(link.Written[^1], 0x30, 0x02, 0x01, 40, 0x00, 0x00, 0x00, 0x1E));
    }

    [Fact]
    public async Task When_cam_starts_the_screen_is_released_and_left_alone()
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "infographic" });
        var pipe = Assert.Single(_pipes.Opened);

        _cam.State = new CamState(true, true, true);
        var hid = link.Written.Count;
        var bulk = pipe.Writes.Count;
        m.Tick();
        m.Tick();
        Assert.True(pipe.Disposed);
        Assert.Equal(hid, link.Written.Count);
        Assert.Equal(bulk, pipe.Writes.Count);
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "liquid" }))).Code);
        Assert.Equal(hid, link.Written.Count);
    }

    [Fact]
    public async Task Closing_the_app_hands_the_screen_back()
    {
        var link = Kraken();
        _bus.Kraken = link;
        var m = await Start();
        await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "infographic" });
        await m.DisposeAsync();
        var i = link.Written.FindLastIndex(p => Starts(p, 0x38, 0x01, 0x02, 0x00));
        Assert.True(i > 0);
        // ...and the cooler's own rotation is put back (90 degrees, brightness 70).
        Assert.True(Starts(link.Written[^1], 0x30, 0x02, 0x01, 70, 0x00, 0x00, 0x01, 0x01));
        Assert.True(_pipes.Opened[0].Disposed);
    }

    [Fact]
    public async Task A_picture_is_stored_in_a_free_slot_and_switched_to()
    {
        var path = Path.Combine(_dir, "pic.png");
        using (var src = new Bitmap(64, 64))
        {
            using (var g = Graphics.FromImage(src)) g.Clear(Color.Blue);
            src.Save(path, ImageFormat.Png);
        }
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        var device = await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "media", path });
        Assert.Equal("media", device.GetProperty("screen").GetProperty("mode").GetString());
        Assert.Equal("pic.png", device.GetProperty("screen").GetProperty("mediaName").GetString());
        Assert.False(device.GetProperty("screen").GetProperty("live").GetBoolean());

        var pipe = Assert.Single(_pipes.Opened);
        Assert.True(pipe.Disposed);                                        // let go once the picture is stored
        var data = pipe.Writes.Skip(1).SelectMany(w => w).ToArray();
        Assert.Equal(KrakenLcdProtocol.BulkHeader(KrakenLcdProtocol.AssetGif, data.Length), pipe.Writes[0]);
        Assert.Equal("GIF89a"u8.ToArray(), data[..6]);

        var cmds = link.Written.Where(p => p[0] is 0x36 or 0x38 or 0x32 || (p[0] == 0x30 && p[1] == 0x04)).ToList();
        Assert.True(Starts(cmds[0], 0x36, 0x03));
        Assert.Equal(16, cmds.Count(p => Starts(p, 0x30, 0x04)));
        var sizeKb = (int)Math.Ceiling((20 + data.Length) / 1024.0);
        var tail = cmds.Skip(17).ToList();
        Assert.True(Starts(tail[0], 0x32, 0x02, 0x00));
        Assert.True(Starts(tail[1], 0x32, 0x01, 0x00, 0x01, 0x00, 0x00, (byte)sizeKb, (byte)(sizeKb >> 8), 0x01));
        Assert.True(Starts(tail[2], 0x36, 0x01, 0x00, 0x00));
        Assert.True(Starts(tail[3], 0x36, 0x02));
        Assert.True(Starts(tail[4], 0x38, 0x01, 0x04, 0x00));
        Assert.Equal(5, tail.Count);

        // Back to the cooler's own screen.
        await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "liquid" });
        Assert.True(Starts(link.Written[^1], 0x38, 0x01, 0x02, 0x00));
    }

    [Fact]
    public async Task Bad_requests_and_a_missing_data_channel_send_nothing()
    {
        var link = Kraken();
        _bus.Kraken = link;
        _pipes.Available = false;
        await using var m = await Start();
        var hid = link.Written.Count;
        async Task<string> Code(object args) => (await Assert.ThrowsAsync<RpcException>(() => Call(m, "setLcdScreen", args))).Code;

        Assert.Equal("unavailable", await Code(new { deviceId = "kraken", mode = "infographic" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "media", path = Path.Combine(_dir, "missing.gif") }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "infographic", layout = "spiral" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "infographic", accent = "red" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "video" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "fan-controller", mode = "liquid" }));
        Assert.Equal(hid, link.Written.Count);
        Assert.Equal("unset", (await Call(m, "getDevices")).GetProperty("devices")[0].GetProperty("screen").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_screen_that_stops_answering_is_given_up_on_and_reported()
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "infographic" });
        foreach (var p in _pipes.Opened) p.Fail = true;
        _pipes.Available = false;
        m.Tick();
        var screen = (await Call(m, "getDevices")).GetProperty("devices")[0].GetProperty("screen");
        Assert.False(screen.GetProperty("live").GetBoolean());
        m.Tick();
        m.Tick();
        screen = (await Call(m, "getDevices")).GetProperty("devices")[0].GetProperty("screen");
        Assert.NotNull(screen.GetProperty("problem").GetString());
        // Cooling itself keeps working.
        var hid = link.Written.Count;
        m.Tick();
        Assert.True(link.Written.Skip(hid).All(p => p[0] == 0x74));
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("visualizer")]
    [InlineData("web")]
    public async Task Other_live_modes_use_the_same_live_path(string mode)
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        var asked = new List<string?>();
        m.SetWebFrameSource(url =>
        {
            asked.Add(url);
            return null;
        });
        var device = await Call(m, "setLcdScreen", new { deviceId = "kraken", mode, url = "https://example.com/board", accent = "#00AAFF" });
        var screen = device.GetProperty("screen");
        Assert.Equal(mode, screen.GetProperty("mode").GetString());
        Assert.True(screen.GetProperty("live").GetBoolean());
        Assert.Contains(mode, screen.GetProperty("modes").EnumerateArray().Select(x => x.GetString()));
        var pipe = Assert.Single(_pipes.Opened);
        Assert.Equal(KrakenLcdProtocol.FrameBytes, pipe.Writes.Skip(1).Sum(w => w.Length));
        if (mode == "web") Assert.Equal("https://example.com/board", asked[^1]);

        // Switching to another live mode needs no new handshake; leaving live mode closes the web page.
        await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "infographic", layout = "triple", tertiary = "gpuLoad" });
        Assert.Equal(1, link.Written.Count(p => Starts(p, 0x10, 0x02)));
        await Call(m, "setLcdScreen", new { deviceId = "kraken", mode = "liquid" });
        if (mode == "web") Assert.Null(asked[^1]);
    }

    [Fact]
    public async Task Web_mode_needs_a_real_address_and_backgrounds_must_exist()
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        var hid = link.Written.Count;
        async Task<string> Code(object args) => (await Assert.ThrowsAsync<RpcException>(() => Call(m, "setLcdScreen", args))).Code;
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "web" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "web", url = "file:///C:/secret.txt" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "web", url = "javascript:alert(1)" }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "clock", background = Path.Combine(_dir, "nope.gif") }));
        Assert.Equal("invalidArgs", await Code(new { deviceId = "kraken", mode = "infographic", tertiary = "volts" }));
        Assert.Equal(hid, link.Written.Count);
    }

    [Fact]
    public async Task Preview_draws_without_touching_the_cooler()
    {
        var link = Kraken();
        _bus.Kraken = link;
        await using var m = await Start();
        var hid = link.Written.Count;
        var preview = await Call(m, "getLcdPreview", new { layout = "single", primary = "gpuLoad" });
        Assert.StartsWith("data:image/png;base64,", preview.GetProperty("image").GetString());
        Assert.Equal(hid, link.Written.Count);
        Assert.Empty(_pipes.Opened);
    }
}

public class LcdSceneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-scene-tests-" + Guid.NewGuid().ToString("N"));

    public LcdSceneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Gif(int frames)
    {
        var path = Path.Combine(_dir, $"bg{frames}.gif");
        var palette = new byte[768];
        palette[3] = 200; palette[7] = 200; palette[11] = 200;
        using var fs = File.Create(path);
        var w = new GifWriter(fs, 64, 64, loop: frames > 1);
        for (var f = 0; f < frames; f++) w.AddFrame(palette, Enumerable.Repeat((byte)(1 + f % 3), 64 * 64).ToArray(), 20);
        w.Finish();
        return path;
    }

    [Theory]
    [InlineData("infographic", "single")]
    [InlineData("infographic", "dual")]
    [InlineData("infographic", "triple")]
    [InlineData("clock", "single")]
    [InlineData("visualizer", "single")]
    [InlineData("web", "single")]
    public void Every_live_mode_draws_a_full_picture(string mode, string layout)
    {
        using var composer = new LcdComposer(new SampleSpectrum());
        var settings = new LcdSettings { Mode = mode, Layout = layout, Accent = "#22C55E", WebUrl = "https://example.com/" };
        using var picture = composer.Compose(settings, new LcdMetrics(64, 51, 31.2, 18, 77), new DateTime(2026, 11, 15, 10, 9, 36));
        Assert.Equal((640, 640), (picture.Width, picture.Height));
        var frame = LcdRenderer.ToLiveFrame(picture, 0);
        Assert.Contains(frame, v => v > 120);
        // Set RIGCONTROL_SHOTS to a folder to keep the pictures for a look.
        if (Environment.GetEnvironmentVariable("RIGCONTROL_SHOTS") is { Length: > 0 } shots)
            picture.Save(Path.Combine(shots, $"{mode}-{layout}.png"), ImageFormat.Png);
    }

    [Fact]
    public void A_gif_behind_the_numbers_moves_and_a_still_picture_does_not()
    {
        using var composer = new LcdComposer(new SampleSpectrum());
        var still = new LcdSettings { Mode = "infographic", Background = Gif(1) };
        Assert.Equal(1, composer.FramesPerSecond(still));
        var moving = new LcdSettings { Mode = "infographic", Background = Gif(3) };
        Assert.Equal(10, composer.FramesPerSecond(moving));

        // Different moments show different frames of the animation (each frame lasts 0.2 s).
        var day = new DateTime(2026, 11, 15);
        using var a = composer.Compose(moving, LcdMetrics.Empty, day.AddMilliseconds(50));
        using var b = composer.Compose(moving, LcdMetrics.Empty, day.AddMilliseconds(250));
        Assert.NotEqual(a.GetPixel(5, 5), b.GetPixel(5, 5));

        // A background that cannot be read is simply left out.
        using var c = composer.Compose(new LcdSettings { Mode = "clock", Background = Path.Combine(_dir, "missing.png") }, LcdMetrics.Empty, day);
        Assert.Equal(1, composer.FramesPerSecond(new LcdSettings { Mode = "clock" }));
        Assert.Equal(12, composer.FramesPerSecond(new LcdSettings { Mode = "visualizer" }));
    }

    [Fact]
    public void A_web_page_picture_is_shown_and_the_page_is_closed_when_leaving()
    {
        using var composer = new LcdComposer(new SampleSpectrum());
        var asked = new List<string?>();
        byte[] png;
        using (var page = new Bitmap(320, 200))
        {
            using (var g = Graphics.FromImage(page)) g.Clear(Color.FromArgb(250, 10, 10));
            using var ms = new MemoryStream();
            page.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }
        composer.WebFrames = url =>
        {
            asked.Add(url);
            return url is null ? null : png;
        };
        using (var shown = composer.Compose(new LcdSettings { Mode = "web", WebUrl = "https://example.com/x" }, LcdMetrics.Empty, DateTime.Now))
            Assert.True(shown.GetPixel(320, 320).R > 240);
        Assert.Equal("https://example.com/x", asked[^1]);
        using (composer.Compose(new LcdSettings { Mode = "clock" }, LcdMetrics.Empty, DateTime.Now)) { }
        Assert.Null(asked[^1]);
    }
}

public class LcdUnitTests
{
    [Fact]
    public void The_screen_can_show_fahrenheit()
    {
        using var composer = new LcdComposer(new SampleSpectrum());
        var s = new LcdSettings { Mode = "infographic", Layout = "triple", Accent = "#22C55E" };
        var m = new LcdMetrics(64, 51, 31.2, 18, 77);
        using var c = composer.Compose(s, m, DateTime.Now);
        using var f = composer.Compose(s, m with { Fahrenheit = true }, DateTime.Now);
        var a = LcdRenderer.ToLiveFrame(c, 0);
        var b = LcdRenderer.ToLiveFrame(f, 0);
        Assert.False(a.AsSpan().SequenceEqual(b));
        if (Environment.GetEnvironmentVariable("RIGCONTROL_SHOTS") is { Length: > 0 } shots)
            f.Save(Path.Combine(shots, "infographic-triple-F.png"), ImageFormat.Png);
    }

    [Fact]
    public void Letters_can_take_the_lights_colour()
    {
        static int CountGreen(System.Drawing.Bitmap b)
        {
            var n = 0;
            for (var y = 0; y < b.Height; y += 2)
                for (var x = 0; x < b.Width; x += 2)
                {
                    var c = b.GetPixel(x, y);
                    if (c.G > 200 && c.R < 60 && c.B < 60) n++;
                }
            return n;
        }
        var metrics = new LcdMetrics(61, 48, 31.5, 12, 80);
        using var normal = new System.Drawing.Bitmap(640, 640);
        using (var g = System.Drawing.Graphics.FromImage(normal)) LcdRenderer.Paint(g, "single", "cpuTemp", "gpuTemp", "liquidTemp", "#8B5CF6", metrics);
        using var green = new System.Drawing.Bitmap(640, 640);
        using (var g = System.Drawing.Graphics.FromImage(green)) LcdRenderer.Paint(g, "single", "cpuTemp", "gpuTemp", "liquidTemp", "#8B5CF6", metrics, System.Drawing.Color.FromArgb(255, 0, 255, 0));
        Assert.Equal(0, CountGreen(normal));
        Assert.True(CountGreen(green) > 200, "the numbers should be drawn in the lights' colour");
    }
}
