using System.Runtime.InteropServices;
using System.Windows;

namespace RigControl.App;

/// <summary>
/// Where the in-game panel goes on the screen the mouse is on, worked out from that screen's real size in pixels and its
/// own scale setting (100%, 125%, 150% ...). The scale is read from Windows for that monitor, never from a WPF window:
/// a window that has not been shown yet reports 100%, which on a 150% screen made the panel two thirds of the size its
/// page lays out for, so its edges were cut off.
/// </summary>
public static class PanelPlacement
{
    /// <summary>The panel's size and both windows' places: pixels for Windows, the size in page units for the page.</summary>
    public readonly record struct Placement(Int32Rect Screen, Int32Rect Panel, double Scale, double PanelWidthDip, double PanelHeightDip);

    /// <summary>
    /// The panel for a screen of <paramref name="screen"/> pixels at <paramref name="scale"/> (1.5 = 150%): as wide as
    /// the wide layout when the screen has room for it (else the narrow one), three quarters of the height (at least
    /// <paramref name="minHeight"/>, at most <paramref name="maxHeight"/>), always with a margin, centred.
    /// </summary>
    public static Placement For(Int32Rect screen, double scale, double baseWidth, double wideWidth, double minHeight, double maxHeight)
    {
        if (!(scale >= 0.5 && scale <= 8)) scale = 1;
        double areaW = screen.Width / scale, areaH = screen.Height / scale;
        const double margin = 16;
        var width = areaW >= wideWidth + 32 ? wideWidth : baseWidth;
        width = Math.Max(320, Math.Min(width, areaW - 2 * margin));
        var height = Math.Clamp(areaH * 0.75, minHeight, maxHeight);
        height = Math.Max(240, Math.Min(height, areaH - 2 * margin));
        var pw = (int)Math.Round(width * scale);
        var ph = (int)Math.Round(height * scale);
        var panel = new Int32Rect(screen.X + (screen.Width - pw) / 2, screen.Y + (screen.Height - ph) / 2, pw, ph);
        return new Placement(screen, panel, scale, width, height);
    }

    /// <summary>The monitor under the mouse: its full area in pixels and its scale.</summary>
    public static (Int32Rect Screen, double Scale) MonitorUnderMouse()
    {
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info)) return (new Int32Rect(0, 0, 1920, 1080), 1);
        var r = info.rcMonitor;
        double scale = 1;
        if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        return (new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), scale);
    }

    /// <summary>Puts a window exactly on <paramref name="r"/> (pixels), without changing its z-order or activating it.</summary>
    public static void Apply(IntPtr hwnd, Int32Rect r)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
