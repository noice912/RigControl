using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RigControl.Cooling;

/// <summary>
/// The Kraken Elite's 640x640 screen. Two ways of putting something on it, both taken from working open-source
/// implementations for this exact cooler (liquidctl's KrakenZ3 driver and kraken-elite-screen-manager, MIT):
///  - "stored": a GIF (animated or a single picture) is written into one of the cooler's 16 storage slots and the
///    cooler plays it by itself, also when RigControl is closed;
///  - "live": whole frames (640x640, 3 bytes per pixel, blue-green-red) are sent one after another, which is how
///    NZXT CAM draws its infographics. Nothing is stored; the picture stops updating when RigControl stops.
/// Screen data travels over a second USB channel (WinUSB, bulk endpoint 2), commands over the normal HID channel.
/// </summary>
public static class KrakenLcdProtocol
{
    public const int Size = 640;
    public const int FrameBytes = Size * Size * 3;
    public const int BucketCount = 16;
    /// <summary>Storage in the cooler, in 1024-byte units.</summary>
    public const int TotalMemoryKb = 24320;
    /// <summary>NZXT CAM sends live frame data in transfers of this size.</summary>
    public const int StreamChunk = 245760;
    public const int UploadChunk = 1024 * 1024;

    public const byte AssetGif = 0x01;
    public const byte AssetLiveFrame = 0x09;

    private static readonly byte[] BulkMagic = { 0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10 };

    private static byte[] Packet(params byte[] head)
    {
        var p = new byte[KrakenProtocol.ReportLength];
        head.CopyTo(p, 0);
        return p;
    }

    private static byte Bucket(int index)
        => index is >= 0 and < BucketCount ? (byte)index : throw new ArgumentOutOfRangeException(nameof(index));

    public static byte[] QueryBucket(int index) => Packet(0x30, 0x04, Bucket(index));
    public static byte[] DeleteBucket(int index) => Packet(0x32, 0x02, Bucket(index));

    public static byte[] SetupBucket(int index, int offsetKb, int sizeKb)
    {
        if (offsetKb < 0 || sizeKb <= 0 || offsetKb + sizeKb > TotalMemoryKb) throw new ArgumentOutOfRangeException(nameof(sizeKb));
        return Packet(0x32, 0x01, Bucket(index), (byte)(index + 1), (byte)offsetKb, (byte)(offsetKb >> 8), (byte)sizeKb, (byte)(sizeKb >> 8), 0x01);
    }

    public static byte[] PrepareTransfer() => Packet(0x36, 0x03);
    public static byte[] StartBucketTransfer(int index) => Packet(0x36, 0x01, Bucket(index));
    public static byte[] StartLiveFrame() => Packet(0x36, 0x01, 0x00, 0x01, AssetLiveFrame);
    public static byte[] EndTransfer() => Packet(0x36, 0x02);
    public static byte[] ShowBucket(int index) => Packet(0x38, 0x01, 0x04, Bucket(index));
    /// <summary>The cooler's own liquid-temperature screen.</summary>
    public static byte[] ShowLiquid() => Packet(0x38, 0x01, 0x02, 0x00);

    /// <summary>Brightness while in live mode (the form NZXT CAM sends there).</summary>
    public static byte[] LiveBrightness(int percent)
        => Packet(0x30, 0x02, 0x01, (byte)Math.Clamp(percent, 0, 100), 0x00, 0x00, 0x00, 0x1E);

    /// <summary>The commands NZXT CAM sends once before it starts sending live frames, in order.</summary>
    public static IReadOnlyList<byte[]> EnterLiveMode(int brightnessPercent)
    {
        var list = new List<byte[]>
        {
            Packet(0x10, 0x02),
            Packet(0x70, 0x02, 0x01, 0xB8, 0x0B),
            KrakenProtocol.GetStatus(),
            Packet(0x36, 0x04),
            KrakenProtocol.GetLcdInfo(),
            PrepareTransfer(),
            Packet(0x30, 0x02, 0x00, 0x00, 0x00, 0x00, 0x1E),
            ShowLiquid(),
        };
        for (var i = 0; i < BucketCount; i++) list.Add(DeleteBucket(i));
        list.Add(LiveBrightness(brightnessPercent));
        return list;
    }

    /// <summary>The 20 bytes that go down the data channel before the picture data.</summary>
    public static byte[] BulkHeader(byte assetType, int dataLength)
    {
        var h = new byte[20];
        BulkMagic.CopyTo(h, 0);
        h[12] = assetType;
        BitConverter.GetBytes((uint)dataLength).CopyTo(h, 16);
        return h;
    }

    /// <summary>True if this is a screen command RigControl may send (checked in addition to the cooling allow-list).</summary>
    public static bool IsAllowed(ReadOnlySpan<byte> p)
    {
        if (p.Length != KrakenProtocol.ReportLength) return false;
        return (p[0], p[1]) switch
        {
            (0x10, 0x02) => true,
            (0x70, 0x02) => p[2] == 0x01 && p[3] == 0xB8 && p[4] == 0x0B,
            (0x30, 0x04) => p[2] < BucketCount,
            (0x30, 0x02) => (p[2] == 0x01 && p[3] <= 100 && p[6] == 0x00 && p[7] == 0x1E) || (p[2] == 0x00 && p[3] == 0x00 && p[6] == 0x1E),
            (0x32, 0x01) => p[2] < BucketCount && p[3] == p[2] + 1 && p[8] == 0x01,
            (0x32, 0x02) => p[2] < BucketCount,
            (0x36, 0x01) => p[2] < BucketCount && (p[3] == 0x00 || (p[2] == 0x00 && p[3] == 0x01 && p[4] == AssetLiveFrame)),
            (0x36, 0x02) or (0x36, 0x03) or (0x36, 0x04) => true,
            (0x38, 0x01) => (p[2] == 0x02 && p[3] == 0x00) || (p[2] == 0x04 && p[3] < BucketCount),
            _ => false,
        };
    }

    /// <summary>True if <paramref name="reply"/> answers <paramref name="command"/> (reply code = command code + 1, or an FF echo).</summary>
    public static bool IsReplyTo(ReadOnlySpan<byte> command, ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 16) return false;
        if (reply[0] == (byte)(command[0] + 1) && reply[1] == command[1]) return true;
        return reply[0] == 0xFF && reply[14] == command[0] && reply[15] == command[1];
    }

    /// <summary>A storage slot as the cooler reports it (reply to <see cref="QueryBucket"/>).</summary>
    public sealed record BucketInfo(bool Occupied, int OffsetKb, int SizeKb)
    {
        public static BucketInfo Parse(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < 21) return new BucketInfo(false, 0, 0);
            var occupied = false;
            for (var i = 15; i < reply.Length; i++) occupied |= reply[i] != 0;
            return new BucketInfo(occupied, reply[17] | (reply[18] << 8), reply[19] | (reply[20] << 8));
        }
    }

    /// <summary>
    /// Where in the cooler's storage a slot's data should go (same rules as liquidctl): reuse the slot's space if
    /// the data fits or nothing else is in the way, else append after everything, else use free space at the start.
    /// Returns -1 if there is no room, in which case every slot is deleted and the data goes to slot 0 at offset 0.
    /// </summary>
    public static int PlanOffset(IReadOnlyList<BucketInfo> buckets, int index, int sizeKb)
    {
        var cur = buckets[index];
        if (sizeKb <= cur.SizeKb) return cur.OffsetKb;
        int min = cur.OffsetKb, max = 0;
        var overlap = false;
        for (var i = 0; i < buckets.Count; i++)
        {
            var start = buckets[i].OffsetKb;
            var end = start + buckets[i].SizeKb;
            if (end > max) max = end;
            if (start < min) min = start;
            if ((start > cur.OffsetKb && start < cur.OffsetKb + sizeKb) || (start < cur.OffsetKb && end > start) || (start == cur.OffsetKb && i != index))
                overlap = true;
        }
        if (!overlap && cur.OffsetKb + sizeKb <= TotalMemoryKb) return cur.OffsetKb;
        if (max + sizeKb < TotalMemoryKb) return max;
        if (sizeKb < min) return 0;
        return -1;
    }
}

/// <summary>The screen's data channel. Abstracted so tests never touch real hardware.</summary>
public interface ILcdPipe : IDisposable
{
    void Write(byte[] data, int offset, int count);
}

public interface ILcdPipeSource
{
    /// <summary>Opens the Kraken Elite's screen data channel, or returns null if it cannot be opened.</summary>
    ILcdPipe? Open();
}

/// <summary>Used where no real hardware may be touched (tests): the data channel is never available.</summary>
public sealed class NoLcdPipeSource : ILcdPipeSource
{
    public ILcdPipe? Open() => null;
}

/// <summary>The real data channel: interface 0 of the cooler, driven by Windows' built-in WinUSB driver.</summary>
public sealed class WinUsbLcdPipeSource : ILcdPipeSource
{
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_1E71&PID_300C&MI_00";

    public ILcdPipe? Open()
    {
        foreach (var path in DevicePaths())
        {
            var pipe = WinUsbPipe.TryOpen(path);
            if (pipe is not null) return pipe;
        }
        return null;
    }

    /// <summary>Builds the device path Windows uses for each known instance of the screen interface.</summary>
    internal static IEnumerable<string> DevicePaths()
    {
        var result = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EnumKey);
            if (key is null) return result;
            foreach (var instance in key.GetSubKeyNames())
            {
                using var p = key.OpenSubKey(instance + @"\Device Parameters");
                var guids = p?.GetValue("DeviceInterfaceGUIDs") as string[] ?? (p?.GetValue("DeviceInterfaceGUID") is string one ? new[] { one } : null);
                var guid = guids?.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
                if (guid is null) continue;
                result.Add(@"\\?\USB#VID_1E71&PID_300C&MI_00#" + instance + "#" + guid.ToLowerInvariant());
            }
        }
        catch
        {
            // no access or no such device: nothing to open
        }
        return result;
    }

    private sealed class WinUsbPipe : ILcdPipe
    {
        private const byte Endpoint = 0x02;
        private const uint PipeTransferTimeout = 0x03;
        private readonly SafeFileHandle _file;
        private IntPtr _usb;

        private WinUsbPipe(SafeFileHandle file, IntPtr usb)
        {
            _file = file;
            _usb = usb;
        }

        public static WinUsbPipe? TryOpen(string path)
        {
            const uint genericReadWrite = 0xC0000000, shareReadWrite = 0x3, openExisting = 3, overlapped = 0x40000000;
            var file = CreateFile(path, genericReadWrite, shareReadWrite, IntPtr.Zero, openExisting, overlapped, IntPtr.Zero);
            if (file.IsInvalid)
            {
                file.Dispose();
                return null;
            }
            if (!WinUsb_Initialize(file, out var usb))
            {
                file.Dispose();
                return null;
            }
            var timeout = 5000u;
            WinUsb_SetPipePolicy(usb, Endpoint, PipeTransferTimeout, 4, ref timeout);
            return new WinUsbPipe(file, usb);
        }

        public void Write(byte[] data, int offset, int count)
        {
            if (_usb == IntPtr.Zero) throw new ObjectDisposedException(nameof(WinUsbPipe));
            var buffer = data;
            if (offset != 0)
            {
                buffer = new byte[count];
                Buffer.BlockCopy(data, offset, buffer, 0, count);
            }
            if (!WinUsb_WritePipe(_usb, Endpoint, buffer, (uint)count, out var written, IntPtr.Zero) || written != count)
                throw new IOException($"Sending screen data failed (Windows error {Marshal.GetLastWin32Error()}, {written} of {count} bytes).");
        }

        public void Dispose()
        {
            if (_usb != IntPtr.Zero)
            {
                WinUsb_Free(_usb);
                _usb = IntPtr.Zero;
            }
            _file.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinUsb_Free(IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint written, IntPtr overlapped);
    }
}

/// <summary>Readings an infographic can show. Any of them may be unknown.</summary>
public sealed record LcdMetrics(double? CpuTempC, double? GpuTempC, double? LiquidTempC, double? CpuLoadPercent, double? GpuLoadPercent)
{
    /// <summary>Show temperatures in °F instead of °C (the gauge fill is the same either way).</summary>
    public bool Fahrenheit { get; init; }

    public static readonly LcdMetrics Empty = new(null, null, null, null, null);
}

/// <summary>Draws the infographic layouts and turns pictures into what the cooler's screen accepts.</summary>
public static class LcdRenderer
{
    public static readonly string[] Layouts = { "single", "dual", "triple" };
    public static readonly string[] Sources = { "cpuTemp", "gpuTemp", "liquidTemp", "cpuLoad", "gpuLoad" };

    private sealed record Reading(string Label, string Text, string Unit, double Fraction);

    private static Reading Read(string source, LcdMetrics m)
    {
        var f = m.Fahrenheit;
        Reading Temp(string label, double? v) => new(label,
            v is null ? "--" : Math.Round(f ? v.Value * 9 / 5 + 32 : v.Value).ToString("0"), f ? "°F" : "°C",
            v is null ? 0 : Math.Clamp((v.Value - 20) / 80.0, 0, 1));
        static Reading Load(string label, double? v) => new(label, v is null ? "--" : Math.Round(v.Value).ToString("0"), "%", v is null ? 0 : Math.Clamp(v.Value / 100.0, 0, 1));
        return source switch
        {
            "gpuTemp" => Temp("GPU", m.GpuTempC),
            "liquidTemp" => Temp("LIQUID", m.LiquidTempC),
            "cpuLoad" => Load("CPU LOAD", m.CpuLoadPercent),
            "gpuLoad" => Load("GPU LOAD", m.GpuLoadPercent),
            _ => Temp("CPU", m.CpuTempC),
        };
    }

    public static Color ParseAccent(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return Color.FromArgb(255, 0x8B, 0x5C, 0xF6);
    }

    /// <summary>Draws one infographic, upright (not yet rotated for how the cooler is mounted).</summary>
    public static Bitmap Draw(string layout, string primary, string secondary, string? accentHex, LcdMetrics metrics)
    {
        const int s = KrakenLcdProtocol.Size;
        var bmp = new Bitmap(s, s, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        Paint(g, layout, primary, secondary, "liquidTemp", accentHex, metrics);
        return bmp;
    }

    /// <summary>Draws an infographic's rings and numbers on top of whatever is already on the canvas (black, or a picture).</summary>
    /// <param name="text">When set, the readings' numbers and labels are drawn in this colour (the lights' colour).</param>
    public static void Paint(Graphics g, string layout, string primary, string secondary, string tertiary, string? accentHex, LcdMetrics metrics, Color? text = null)
    {
        const int s = KrakenLcdProtocol.Size;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        // While the letters follow the lights, the ring and labels take the lights' colour too.
        var accent = text ?? ParseAccent(accentHex);
        var dim = Color.FromArgb(255, 0x26, 0x2B, 0x33);
        var soft = Color.FromArgb(255, 0x9A, 0xA4, 0xB2);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        if (layout == "triple")
        {
            // One large reading on top with its own gauge, two smaller ones side by side underneath.
            var a = Read(primary, metrics);
            var b = Read(secondary, metrics);
            var c = Read(tertiary, metrics);
            var ring = new Rectangle(30, 30, s - 60, s - 60);
            using (var track = new Pen(dim, 20) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var fill = new Pen(accent, 20) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(track, ring, 180, 180);
                if (a.Fraction > 0) g.DrawArc(fill, ring, 180, (float)(180 * a.Fraction));
            }
            using (var divider = new Pen(dim, 3))
            {
                g.DrawLine(divider, 110, 338, s - 110, 338);
                g.DrawLine(divider, s / 2, 356, s / 2, 520);
            }
            DrawReading(g, a, new RectangleF(150, 80, 340, 250), 150, accent, soft, center, text);
            DrawReading(g, b, new RectangleF(95, 350, 215, 185), 88, accent, soft, center, text);
            DrawReading(g, c, new RectangleF(330, 350, 215, 185), 88, accent, soft, center, text);
        }
        else if (layout == "dual")
        {
            var a = Read(primary, metrics);
            var b = Read(secondary, metrics);
            var ring = new Rectangle(30, 30, s - 60, s - 60);
            using (var track = new Pen(dim, 22) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var fill = new Pen(accent, 22) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Left half-ring for the first reading, right half-ring for the second; both fill from the bottom up.
                g.DrawArc(track, ring, 100, 160);
                g.DrawArc(track, ring, 280, 160);
                if (a.Fraction > 0) g.DrawArc(fill, ring, 100, (float)(160 * a.Fraction));
                if (b.Fraction > 0) g.DrawArc(fill, ring, 80, (float)(-160 * b.Fraction));
            }
            using (var divider = new Pen(dim, 3)) g.DrawLine(divider, s / 2, 190, s / 2, 450);
            DrawReading(g, a, new RectangleF(60, 190, 250, 260), 118, accent, soft, center, text);
            DrawReading(g, b, new RectangleF(330, 190, 250, 260), 118, accent, soft, center, text);
        }
        else
        {
            var a = Read(primary, metrics);
            var ring = new Rectangle(34, 34, s - 68, s - 68);
            using (var track = new Pen(dim, 30) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var fill = new Pen(accent, 30) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(track, ring, 135, 270);
                if (a.Fraction > 0) g.DrawArc(fill, ring, 135, (float)(270 * a.Fraction));
            }
            DrawReading(g, a, new RectangleF(90, 150, 460, 340), 210, accent, soft, center, text);
        }
    }

    private static void DrawReading(Graphics g, Reading r, RectangleF box, float valueSize, Color accent, Color soft, StringFormat center, Color? text = null)
    {
        using var valueFont = new Font("Segoe UI", valueSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", valueSize * 0.26f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", valueSize * 0.24f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(text ?? Color.White);
        using var softBrush = new SolidBrush(soft);
        using var accentBrush = new SolidBrush(text ?? accent);
        var third = box.Height / 5f;
        g.DrawString(r.Label, labelFont, accentBrush, new RectangleF(box.X, box.Y, box.Width, third), center);
        g.DrawString(r.Text, valueFont, white, new RectangleF(box.X, box.Y + third, box.Width, third * 3), center);
        g.DrawString(r.Unit, unitFont, softBrush, new RectangleF(box.X, box.Y + third * 4, box.Width, third), center);
    }

    /// <summary>Turns a drawn picture into one live frame: rotated for the mounting, 3 bytes per pixel in blue-green-red order.</summary>
    public static byte[] ToLiveFrame(Bitmap upright, int rotationDegrees)
    {
        using var bmp = (Bitmap)upright.Clone();
        var flip = ((rotationDegrees % 360 + 360) % 360) switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(flip);
        var rect = new Rectangle(0, 0, KrakenLcdProtocol.Size, KrakenLcdProtocol.Size);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            // 640 pixels x 3 bytes is a multiple of 4, so rows have no padding and the memory layout is exactly B,G,R.
            var bytes = new byte[KrakenLcdProtocol.FrameBytes];
            if (data.Stride == KrakenLcdProtocol.Size * 3)
            {
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                for (var y = 0; y < KrakenLcdProtocol.Size; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * KrakenLcdProtocol.Size * 3, KrakenLcdProtocol.Size * 3);
            }
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static string ToPngDataUrl(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// Loads a picture or GIF, crops it to fill the round 640x640 screen, rotates it for the mounting and returns
    /// it as a GIF (animated if the source was), which is the stored format the cooler plays. If an animation is
    /// too big for the cooler's storage, every second frame is dropped (keeping the speed) until it fits.
    /// </summary>
    public static byte[] PrepareGif(string path, int rotationDegrees, int maxBytes = (KrakenLcdProtocol.TotalMemoryKb - 64) * 1024)
    {
        using var source = Image.FromFile(path);
        var animated = false;
        var count = 1;
        try
        {
            animated = source.FrameDimensionsList.Contains(FrameDimension.Time.Guid);
            if (animated) count = Math.Max(1, source.GetFrameCount(FrameDimension.Time));
        }
        catch
        {
            // not an animation
        }
        var delays = new int[count];
        for (var i = 0; i < count; i++) delays[i] = 10;
        if (animated && count > 1)
        {
            try
            {
                var item = source.GetPropertyItem(0x5100); // frame delays, 1/100 s each
                for (var i = 0; i < count && item?.Value is not null && i * 4 + 3 < item.Value.Length; i++)
                {
                    var d = BitConverter.ToInt32(item.Value, i * 4);
                    delays[i] = d <= 1 ? 10 : Math.Min(d, 6000); // players treat 0-1 as 100 ms too
                }
            }
            catch
            {
                // keep 100 ms
            }
        }

        const int maxFrames = 600;
        for (var step = 1; step <= 16; step *= 2)
        {
            if ((count + step - 1) / step > maxFrames) continue;
            using var ms = new MemoryStream();
            var moving = count > step;
            var gif = new GifWriter(ms, KrakenLcdProtocol.Size, KrakenLcdProtocol.Size, loop: moving);
            var fits = true;
            for (var i = 0; i < count && fits; i += step)
            {
                if (animated && count > 1) source.SelectActiveFrame(FrameDimension.Time, i);
                var delay = 0;
                for (var k = i; k < Math.Min(count, i + step); k++) delay += delays[k];
                using var frame = FitToScreen(source, rotationDegrees);
                Quantise(frame, out var palette, out var indices);
                gif.AddFrame(palette, indices, moving ? delay : 0);
                fits = ms.Length <= maxBytes;
            }
            if (!fits) continue;
            gif.Finish();
            if (ms.Length <= maxBytes) return ms.ToArray();
        }
        throw new InvalidOperationException("That animation is too long for the cooler's screen storage (about 23 MB). Pick a shorter one.");
    }

    /// <summary>Crop-fills an image into a 640x640 picture and rotates it clockwise for the mounting.</summary>
    internal static Bitmap FitToScreen(Image source, int rotationDegrees)
    {
        const int s = KrakenLcdProtocol.Size;
        var bmp = new Bitmap(s, s, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var scale = Math.Max(s / (float)source.Width, s / (float)source.Height);
            var w = source.Width * scale;
            var h = source.Height * scale;
            g.DrawImage(source, (s - w) / 2f, (s - h) / 2f, w, h);
        }
        var flip = ((rotationDegrees % 360 + 360) % 360) switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(flip);
        return bmp;
    }

    /// <summary>Reduces a picture to its best 256 colours (GIF allows no more per frame).</summary>
    private static void Quantise(Bitmap bmp, out byte[] palette, out byte[] indices)
    {
        const int s = KrakenLcdProtocol.Size;
        var bgr = ToLiveFrame(bmp, 0);
        var src = System.Windows.Media.Imaging.BitmapSource.Create(s, s, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, bgr, s * 3);
        var pal = new System.Windows.Media.Imaging.BitmapPalette(src, 256);
        var indexed = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Indexed8, pal, 0);
        indices = new byte[s * s];
        indexed.CopyPixels(indices, s, 0);
        palette = new byte[768];
        var colors = indexed.Palette?.Colors ?? pal.Colors;
        for (var i = 0; i < colors.Count && i < 256; i++)
        {
            palette[i * 3] = colors[i].R;
            palette[i * 3 + 1] = colors[i].G;
            palette[i * 3 + 2] = colors[i].B;
        }
    }
}

/// <summary>A minimal GIF89a writer: full frames, 256 colours each, optional endless loop.</summary>
public sealed class GifWriter
{
    private readonly Stream _out;
    private readonly int _width;
    private readonly int _height;

    public GifWriter(Stream output, int width, int height, bool loop)
    {
        _out = output;
        _width = width;
        _height = height;
        _out.Write("GIF89a"u8);
        WriteU16(width);
        WriteU16(height);
        _out.Write(new byte[] { 0x70, 0x00, 0x00 }); // no global colour table
        if (loop)
        {
            _out.Write(new byte[] { 0x21, 0xFF, 0x0B });
            _out.Write("NETSCAPE2.0"u8);
            _out.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 }); // repeat forever
        }
    }

    private void WriteU16(int v)
    {
        _out.WriteByte((byte)v);
        _out.WriteByte((byte)(v >> 8));
    }

    /// <param name="palette">768 bytes: 256 colours as R,G,B.</param>
    /// <param name="indices">One palette index per pixel, row by row.</param>
    /// <param name="delayCentiseconds">How long the frame stays up, in 1/100 s.</param>
    public void AddFrame(byte[] palette, byte[] indices, int delayCentiseconds)
    {
        if (palette.Length != 768) throw new ArgumentException("The palette must have 256 colours.", nameof(palette));
        if (indices.Length != _width * _height) throw new ArgumentException("Wrong number of pixels.", nameof(indices));
        _out.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 }); // graphic control: leave the frame in place
        WriteU16(delayCentiseconds);
        _out.Write(new byte[] { 0x00, 0x00 });
        _out.WriteByte(0x2C);
        WriteU16(0);
        WriteU16(0);
        WriteU16(_width);
        WriteU16(_height);
        _out.WriteByte(0x87); // local colour table with 256 entries
        _out.Write(palette);
        WriteLzw(indices);
    }

    public void Finish() => _out.WriteByte(0x3B);

    private void WriteLzw(byte[] indices)
    {
        const int minCodeSize = 8;
        const int clear = 1 << minCodeSize;
        const int end = clear + 1;
        _out.WriteByte(minCodeSize);

        var block = new byte[255];
        var blockLength = 0;
        var bits = 0;
        var bitCount = 0;
        var codeSize = minCodeSize + 1;
        var next = end + 1;
        var table = new Dictionary<int, int>(8192);

        void Emit(int code, bool wasClear)
        {
            bits |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block[blockLength++] = (byte)bits;
                bits >>= 8;
                bitCount -= 8;
                if (blockLength == 255)
                {
                    _out.WriteByte(255);
                    _out.Write(block, 0, 255);
                    blockLength = 0;
                }
            }
            if (wasClear) codeSize = minCodeSize + 1;
            else if (next > (1 << codeSize) - 1 && codeSize < 12) codeSize++;
        }

        Emit(clear, true);
        var prefix = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var c = indices[i];
            var key = (prefix << 8) | c;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }
            Emit(prefix, false);
            prefix = c;
            if (next < 4096)
            {
                table[key] = next++;
            }
            else
            {
                table.Clear();
                next = end + 1;
                Emit(clear, true);
            }
        }
        Emit(prefix, false);
        Emit(end, false);
        if (bitCount > 0) block[blockLength++] = (byte)bits;
        if (blockLength > 0)
        {
            _out.WriteByte((byte)blockLength);
            _out.Write(block, 0, blockLength);
        }
        _out.WriteByte(0x00);
    }
}
