using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RigControl.Capture;
using RigControl.Sensors;

namespace RigControl.App;

/// <summary>
/// The small on-screen display in the top-right corner: a red dot and timer while recording, a short message when
/// something is saved, and (optionally) temperatures and load. It never takes clicks or keyboard focus, and it is
/// left out of recordings. It shows over windowed and borderless games; a game in exclusive full screen hides it.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _recording = Line(15, FontWeights.SemiBold);
    private readonly TextBlock _replay = Line(13, FontWeights.Normal);
    private readonly TextBlock _stats = Line(13, FontWeights.Normal);
    private readonly TextBlock _notice = Line(14, FontWeights.SemiBold);
    private readonly TextBlock _extra = Line(13, FontWeights.Normal);
    private string? _extraLines;
    private readonly DispatcherTimer _timer;
    private CaptureStatusDto? _status;
    private SensorSnapshotDto? _snapshot;
    private DateTime _noticeUntil;
    private bool _fahrenheit = true;

    public void SetFahrenheit(bool fahrenheit) => _fahrenheit = fahrenheit;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "RigControl overlay";

        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        foreach (var t in new[] { _recording, _replay, _notice, _stats, _extra }) panel.Children.Add(t);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x10, 0x14, 0x1A)),
            CornerRadius = new CornerRadius(10),
            Child = panel,
        };
        _recording.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
        _replay.Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xB4, 0xC2));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int exStyle = -20;
            const long transparent = 0x20, toolWindow = 0x80, noActivate = 0x08000000, layered = 0x80000;
            SetWindowLongPtr(hwnd, exStyle, new IntPtr(GetWindowLongPtr(hwnd, exStyle).ToInt64() | transparent | toolWindow | noActivate | layered));
            SetWindowDisplayAffinity(hwnd, 0x11); // keep the overlay out of recordings and screenshots
        };
        SizeChanged += (_, _) => Place();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private static TextBlock Line(double size, FontWeight weight) => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = Brushes.White,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 1, 0, 1),
    };

    public void SetStatus(CaptureStatusDto status)
    {
        _status = status;
        Refresh();
    }

    public void SetSnapshot(SensorSnapshotDto snapshot) => _snapshot = snapshot;

    /// <summary>Extra lines an add-on wants shown with the stats (one per line), or null for none.</summary>
    public void SetExtraLines(string? lines)
    {
        _extraLines = string.IsNullOrWhiteSpace(lines) ? null : lines;
        Refresh();
    }

    /// <param name="duration">How long the message stays; by default 3 s, or 6 s for a problem.</param>
    public void ShowNotice(CaptureNoticeDto notice, TimeSpan? duration = null)
    {
        _notice.Text = notice.Text;
        _notice.Foreground = notice.Kind == "error" ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4A)) : Brushes.White;
        _noticeUntil = DateTime.UtcNow + (duration ?? TimeSpan.FromSeconds(notice.Kind == "error" ? 6 : 3));
        Refresh();
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null && block.Text != text) block.Text = text;
    }

    private void Refresh()
    {
        var s = _status;
        string? recording = null;
        if (s is { Recording: true } && DateTimeOffset.TryParse(s.RecordingSince, out var since))
        {
            var t = DateTimeOffset.Now - since;
            recording = $"● REC  {(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }
        Show(_recording, recording);
        Show(_replay, s is { ReplayRunning: true, Settings.OverlayStats: true } ? $"Instant replay on ({s.Settings.ReplayHotkey} to save)" : null);
        _notice.Visibility = DateTime.UtcNow < _noticeUntil ? Visibility.Visible : Visibility.Collapsed;

        string? stats = null;
        if (s is { Settings.OverlayStats: true } && _snapshot is { } snap)
        {
            static string V(double? v, string unit) => v is null ? "--" : Math.Round(v.Value).ToString("0") + unit;
            var f = _fahrenheit;
            string T(double? c) => V(c is null ? null : f ? c * 9 / 5 + 32 : c, f ? "°F" : "°C");
            var cpuTemp = snap.Cpu?.PackageTempC ?? snap.Cpu?.CoreMaxTempC;
            stats = $"GPU  {T(snap.Gpu?.TempC)}  {V(snap.Gpu?.LoadPercent, "%")}\nCPU  {T(cpuTemp)}  {V(snap.Cpu?.LoadPercent, "%")}";
        }
        Show(_stats, stats);

        // An add-on's lines, shown together with the stats (Alt+R).
        Show(_extra, s is { Settings.OverlayStats: true } ? _extraLines : null);

        var any = _recording.Visibility == Visibility.Visible || _replay.Visibility == Visibility.Visible
                  || _notice.Visibility == Visibility.Visible || _stats.Visibility == Visibility.Visible
                  || _extra.Visibility == Visibility.Visible;
        if (any && !IsVisible) Show();
        else if (!any && IsVisible) Hide();
        if (any) Place();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Top + 16;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
