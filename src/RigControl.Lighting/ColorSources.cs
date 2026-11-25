#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using System.Text.RegularExpressions;
using WinRT;
#endif

namespace RigControl.Lighting;

public sealed record CaptureDeviceDto(string Id, string Name, bool Present);

/// <summary>The colour of a picture, ported from RichPresence's RPRgb.</summary>
public static class ColorMath
{
    /// <summary>
    /// The average colour of BGRA pixels on a grid of about 48x27 samples. Colourful pixels count more than grey ones
    /// (weight 1 + max channel - min channel); without that most screens average to a muddy grey. Returns 0xRRGGBB.
    /// </summary>
    public static int Average(byte[]? bgra, int width, int height)
    {
        if (bgra is null || width <= 0 || height <= 0 || bgra.Length < 4 * width * height) return 0;
        long r = 0, g = 0, b = 0, w = 0;
        int stepX = Math.Max(1, width / 48), stepY = Math.Max(1, height / 27);
        for (var y = stepY / 2; y < height; y += stepY)
            for (var x = stepX / 2; x < width; x += stepX)
            {
                var i = 4 * (y * width + x);
                int cb = bgra[i], cg = bgra[i + 1], cr = bgra[i + 2];
                int max = Math.Max(cr, Math.Max(cg, cb)), min = Math.Min(cr, Math.Min(cg, cb));
                var wt = 1 + (max - min);
                r += cr * wt; g += cg * wt; b += cb * wt; w += wt;
            }
        if (w == 0) return 0;
        return ((int)(r / w) << 16) | ((int)(g / w) << 8) | (int)(b / w);
    }

    /// <summary>
    /// The weighted average colour of a video frame in its own layout, sampled on a 48x27 grid. Planes as Windows
    /// describes them: (start, stride) for plane 0, and for NV12 plane 1 (the interleaved U/V half-size plane).
    /// format: "BGRA8", "NV12" or "YUY2". Returns 0xRRGGBB, or -1 for a layout it can't read.
    /// </summary>
    public static int AverageFrame(byte[] data, string format, int width, int height, int start0, int stride0, int start1 = 0, int stride1 = 0)
    {
        var sample = SampleFrame(data, format, width, height, start0, stride0, start1, stride1);
        return sample is null ? -1 : Average(sample, GridW, GridH);
    }

    /// <summary>The small picture the lights are worked out from (BGRA, <see cref="GridW"/> x <see cref="GridH"/>).</summary>
    public const int GridW = 48, GridH = 27;

    /// <summary>A video frame shrunk to the 48x27 BGRA grid (one sample per cell), or null for a layout it can't read.</summary>
    public static byte[]? SampleFrame(byte[] data, string format, int width, int height, int start0, int stride0, int start1 = 0, int stride1 = 0)
    {
        const int SW = GridW, SH = GridH;
        var sample = new byte[SW * SH * 4];
        for (var sy = 0; sy < SH; sy++)
            for (var sx = 0; sx < SW; sx++)
            {
                var x = Math.Min(width - 1, (int)((sx + 0.5) * width / SW));
                var y = Math.Min(height - 1, (int)((sy + 0.5) * height / SH));
                int r, g, b;
                switch (format)
                {
                    case "BGRA8":
                    {
                        var i = start0 + y * stride0 + 4 * x;
                        if (i + 2 >= data.Length) return null;
                        b = data[i]; g = data[i + 1]; r = data[i + 2];
                        break;
                    }
                    case "NV12":
                    {
                        var yi = start0 + y * stride0 + x;
                        var ci = start1 + (y / 2) * stride1 + (x / 2) * 2;
                        if (yi >= data.Length || ci + 1 >= data.Length) return null;
                        (r, g, b) = Yuv(data[yi], data[ci], data[ci + 1]);
                        break;
                    }
                    case "YUY2":
                    {
                        var bi = start0 + y * stride0 + (x / 2) * 4;
                        if (bi + 3 >= data.Length) return null;
                        (r, g, b) = Yuv(data[bi + (x % 2 == 0 ? 0 : 2)], data[bi + 1], data[bi + 3]);
                        break;
                    }
                    default:
                        return null;
                }
                var o = 4 * (sy * SW + sx);
                sample[o] = (byte)b; sample[o + 1] = (byte)g; sample[o + 2] = (byte)r; sample[o + 3] = 255;
            }
        return sample;
    }

    /// <summary>
    /// The weighted average colour (as <see cref="Average"/>) of the grid cells a rectangle covers (0 to 1 of the
    /// picture); a rectangle smaller than a cell takes the cell under its middle. Returns 0xRRGGBB.
    /// </summary>
    public static int Region(byte[] grid, int gw, int gh, double x, double y, double w, double h)
    {
        if (grid.Length < 4 * gw * gh || gw <= 0 || gh <= 0) return 0;
        var x0 = (int)Math.Floor(Math.Clamp(x, 0, 1) * gw);
        var y0 = (int)Math.Floor(Math.Clamp(y, 0, 1) * gh);
        var x1 = (int)Math.Ceiling(Math.Clamp(x + w, 0, 1) * gw);
        var y1 = (int)Math.Ceiling(Math.Clamp(y + h, 0, 1) * gh);
        if (x1 <= x0 || y1 <= y0)
        {
            x0 = Math.Clamp((int)((x + w / 2) * gw), 0, gw - 1);
            y0 = Math.Clamp((int)((y + h / 2) * gh), 0, gh - 1);
            x1 = x0 + 1;
            y1 = y0 + 1;
        }
        x0 = Math.Min(x0, gw - 1); y0 = Math.Min(y0, gh - 1);
        long r = 0, g = 0, b = 0, wt = 0;
        for (var cy = y0; cy < y1 && cy < gh; cy++)
            for (var cx = x0; cx < x1 && cx < gw; cx++)
            {
                var i = 4 * (cy * gw + cx);
                int cb = grid[i], cg = grid[i + 1], cr = grid[i + 2];
                var k = 1 + (Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb)));
                r += cr * k; g += cg * k; b += cb * k; wt += k;
            }
        return wt == 0 ? 0 : ((int)(r / wt) << 16) | ((int)(g / wt) << 8) | (int)(b / wt);
    }

    /// <summary>Video YUV (BT.709, studio range, as HD capture cards send) to RGB.</summary>
    public static (int R, int G, int B) Yuv(int y, int u, int v)
    {
        double c = 1.164 * (y - 16), d = u - 128, e = v - 128;
        static int Clamp(double x) => (int)Math.Clamp(Math.Round(x), 0, 255);
        return (Clamp(c + 1.793 * e), Clamp(c - 0.213 * d - 0.533 * e), Clamp(c + 2.112 * d));
    }

    /// <summary>One smoothing step: 35% of the way toward the new colour, scaled by brightness (0-100).</summary>
    public static (double R, double G, double B) Smooth((double R, double G, double B) cur, int target, int brightness, double follow = 0.35)
    {
        var k = Math.Clamp(brightness, 0, 100) / 100.0;
        return (cur.R + (((target >> 16) & 255) * k - cur.R) * follow,
                cur.G + (((target >> 8) & 255) * k - cur.G) * follow,
                cur.B + ((target & 255) * k - cur.B) * follow);
    }

    public static uint ToOpenRgb((double R, double G, double B) c) =>
        (uint)((byte)c.R | ((byte)c.G << 8) | ((byte)c.B << 16)); // OpenRGB packs colours as 0x00BBGGRR

    /// <summary>
    /// The same hue made bright and saturated, so every kind of light shows it clearly (a dim muddy average looks
    /// different on RAM, fans and keyboards). Grey is pushed apart by half again, then the brightest channel goes to
    /// full; a dark picture stays dim (full only from a channel of 48 up), and black stays black. 0xRRGGBB in and out.
    /// </summary>
    public static int Vivid(int rgb)
    {
        double r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        var max = Math.Max(r, Math.Max(g, b));
        if (max < 1) return 0;
        var grey = (r + g + b) / 3;
        r = Math.Max(0, grey + (r - grey) * 1.5);
        g = Math.Max(0, grey + (g - grey) * 1.5);
        b = Math.Max(0, grey + (b - grey) * 1.5);
        var top = Math.Max(r, Math.Max(g, b));
        if (top < 1) return 0;
        var k = Math.Min(255, max * 255 / 48) / top;
        static int Ch(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
        return (Ch(r * k) << 16) | (Ch(g * k) << 8) | Ch(b * k);
    }

    /// <summary>The same colour turned round the colour wheel (HSV hue + degrees), keeping its brightness and strength.</summary>
    public static (double R, double G, double B) RotateHue((double R, double G, double B) c, double degrees)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min < 1e-6) return c; // grey has no hue
        var d = max - min;
        double h = max == c.R ? (c.G - c.B) / d % 6 : max == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
        h = ((h * 60 + degrees) % 360 + 360) % 360 / 60;
        var x = d * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (d, x, 0.0),
            < 2 => (x, d, 0.0),
            < 3 => (0.0, d, x),
            < 4 => (0.0, x, d),
            < 5 => (x, 0.0, d),
            _ => (d, 0.0, x),
        };
        return (r + min, g + min, b + min);
    }

    /// <summary>True when any LED's red, green or blue moved by at least <paramref name="threshold"/> (OpenRGB colours).</summary>
    public static bool Differs(IReadOnlyList<uint> a, IReadOnlyList<uint> b, int threshold)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
            for (var shift = 0; shift <= 16; shift += 8)
                if (Math.Abs((int)((a[i] >> shift) & 255) - (int)((b[i] >> shift) & 255)) >= threshold) return true;
        return false;
    }

    public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6");
}

#if WINDOWS // The screen and capture cards are read with Windows' own calls; the other flavour has no picture to follow.
/// <summary>The main screen, shrunk to 48x27 with GDI (StretchBlt), about 15 times a second. Exclusive full screen and some HDR output read as black.</summary>
public sealed class ScreenSource : IScreenPicture
{
    private const int W = ColorMath.GridW, H = ColorMath.GridH;
    private readonly byte[] _buf = new byte[W * H * 4];

    /// <summary>The last picture read (48x27 BGRA). Read on the sync thread only.</summary>
    public byte[] Grid => _buf;

    public int Read()
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, W, H);
        var old = SelectObject(dc, bmp);
        try
        {
            SetStretchBltMode(dc, 4); // HALFTONE: a true average, not every n-th pixel
            StretchBlt(dc, 0, 0, W, H, screen, 0, 0, GetSystemMetrics(0), GetSystemMetrics(1), 0x00CC0020);
            var bi = new BitmapInfo { Size = 40, Width = W, Height = -H, Planes = 1, BitCount = 32 };
            SelectObject(dc, old);
            if (GetDIBits(dc, bmp, 0, H, _buf, ref bi, 0) == 0) return 0;
            return ColorMath.Average(_buf, W, H);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
        public int Colors; // one RGBQUAD, unused for 32-bit
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfo bi, uint usage);
}

/// <summary>
/// A capture card's picture (RichPresence 3.3.3's capture worker, ported): MediaCapture shared first (a program
/// already showing the picture keeps it), else on its own; the device switched to its smallest uncompressed format
/// (MJPG/H264 can't be turned into pixels here), frames asked for as BGRA8 and averaged about 14 times a second.
/// </summary>
public sealed class CaptureSource : ICapturePicture
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[]? _bytes;
    private DateTime _lastFrame = DateTime.UtcNow;

    public string? Size { get; private set; }
    public string? Formats { get; private set; }
    /// <summary>True when the card was opened shared (other programs such as OBS can use it at the same time).</summary>
    public bool Shared { get; private set; }
    /// <summary>The newest frame shrunk to 48x27 BGRA, or null before the first frame.</summary>
    public byte[]? Grid { get; private set; }

    public static async Task<IReadOnlyList<CaptureDeviceDto>> ListAsync()
    {
        var all = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        return all.Select(d => new CaptureDeviceDto(d.Id, d.Name, true)).ToList();
    }

    /// <summary>Camera access for desktop apps switched off in Windows' privacy settings (looks the same as "busy" otherwise).</summary>
    public static bool CameraBlockedByPrivacy()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged");
                if (string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return true;
                using var all = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam");
                if (string.Equals(all?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* unreadable: assume allowed */ }
        }
        return false;
    }

    /// <param name="allowExclusive">
    /// When sharing fails, take the card for RigControl alone. Never while a program such as OBS shows the card: that
    /// locks it out.
    /// </param>
    public async Task StartAsync(string deviceId, bool allowExclusive = true)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        MediaFrameSourceGroup? group = null;
        MediaFrameSourceInfo? info = null;
        foreach (var g in groups)
        {
            foreach (var si in g.SourceInfos)
            {
                if (si.SourceKind == MediaFrameSourceKind.Color && (g.Id == deviceId || si.DeviceInformation?.Id == deviceId))
                {
                    group = g;
                    info = si;
                    break;
                }
            }
            if (group is not null) break;
        }
        if (group is null) throw new InvalidOperationException("that capture device isn't plugged in");

        var modes = allowExclusive
            ? new[] { MediaCaptureSharingMode.SharedReadOnly, MediaCaptureSharingMode.ExclusiveControl }
            : new[] { MediaCaptureSharingMode.SharedReadOnly };
        foreach (var mode in modes)
        {
            var cap = new MediaCapture();
            try
            {
                await cap.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = mode,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                }).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                _capture = cap;
                Shared = mode == MediaCaptureSharingMode.SharedReadOnly;
                break;
            }
            catch
            {
                cap.Dispose();
                if (mode == modes[^1] && !allowExclusive)
                    throw new InvalidOperationException("it can't be shared with the program that is showing it");
                if (mode == MediaCaptureSharingMode.ExclusiveControl)
                    throw new InvalidOperationException(CameraBlockedByPrivacy()
                        ? "camera access for desktop apps is off in Windows Settings > Privacy & security > Camera"
                        : "another program is using it");
            }
        }

        MediaFrameSource? src = null;
        foreach (var pair in _capture!.FrameSources)
        {
            var s = pair.Value;
            if (s?.Info.SourceKind == MediaFrameSourceKind.Color && (src is null || s.Info.Id == info!.Id)) src = s;
        }
        if (src is null) throw new InvalidOperationException("the device has no picture stream");
        var plain = src.SupportedFormats.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Subtype ?? "", "^(YUY2|NV12|UYVY|RGB24|RGB32|ARGB32|I420|IYUV|YV12)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        Formats = string.Join(", ", src.SupportedFormats.Select(f => $"{f.Subtype} {f.VideoFormat.Width}x{f.VideoFormat.Height}").Distinct());
        // The card stays at its full picture: an uncompressed format it is already set to is kept as it is. Only a
        // compressed one (MJPG/H264 can't be read here) is switched, to the largest uncompressed picture, fastest first
        // (when sharing, the format can't be changed at all). Frames are read in their own layout, not converted.
        static bool Readable(string? subtype) => subtype is not null && Regex.IsMatch(subtype, "^(NV12|YUY2|RGB32|ARGB32)$", RegexOptions.IgnoreCase);
        if (!Readable(src.CurrentFormat?.Subtype))
        {
            try
            {
                var best = src.SupportedFormats.Where(f => Readable(f.Subtype))
                    .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                    .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                    .ThenBy(f => string.Equals(f.Subtype, "NV12", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .FirstOrDefault();
                if (best is not null) await src.SetFormatAsync(best).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch { /* shared: keep whatever it is set to */ }
        }
        _reader = Readable(src.CurrentFormat?.Subtype)
            ? await _capture.CreateFrameReaderAsync(src).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            : await _capture.CreateFrameReaderAsync(src, MediaEncodingSubtypes.Bgra8).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var started = await _reader.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (started != MediaFrameReaderStartStatus.Success)
        {
            if (plain.Count == 0) throw new InvalidOperationException("it only sends compressed video, which can't be read for colours");
            var cf = src.CurrentFormat;
            throw new InvalidOperationException($"the picture didn't start ({started}; it is set to {cf?.Subtype} {cf?.VideoFormat?.Width}x{cf?.VideoFormat?.Height})");
        }
        _lastFrame = DateTime.UtcNow;
    }

    /// <summary>The newest frame's colour, or null when no new frame came. Throws once nothing has come for 5 s.</summary>
    public int? Read()
    {
        using var frame = _reader?.TryAcquireLatestFrame();
        var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bmp is null)
        {
            if ((DateTime.UtcNow - _lastFrame).TotalSeconds > 5) throw new InvalidOperationException("no picture is coming from it");
            return null;
        }
        using (bmp)
        {
            var format = bmp.BitmapPixelFormat switch
            {
                BitmapPixelFormat.Bgra8 => "BGRA8",
                BitmapPixelFormat.Nv12 => "NV12",
                BitmapPixelFormat.Yuy2 => "YUY2",
                _ => null,
            };
            if (format is null)
            {
                // Anything else: let Windows turn this one frame into plain pixels.
                using var plainBmp = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                return FromBitmap(plainBmp, "BGRA8");
            }
            return FromBitmap(bmp, format);
        }
    }

    private int? FromBitmap(SoftwareBitmap bmp, string format)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        using var buffer = bmp.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var ptr, out var capacity);
        if (ptr == IntPtr.Zero || capacity == 0) return null;
        if (_bytes is null || _bytes.Length != capacity) _bytes = new byte[capacity];
        Marshal.Copy(ptr, _bytes, 0, (int)capacity);
        var p0 = buffer.GetPlaneDescription(0);
        var p1 = buffer.GetPlaneCount() > 1 ? buffer.GetPlaneDescription(1) : p0;
        var grid = ColorMath.SampleFrame(_bytes, format, w, h, p0.StartIndex, p0.Stride, p1.StartIndex, p1.Stride);
        Size = $"{w}x{h} {format}";
        _lastFrame = DateTime.UtcNow;
        if (grid is null) return null;
        Grid = grid;
        return ColorMath.Average(grid, ColorMath.GridW, ColorMath.GridH);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    public void Dispose()
    {
        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _capture?.Dispose(); } catch { /* ignore */ }
        _reader = null;
        _capture = null;
    }
}
#endif
