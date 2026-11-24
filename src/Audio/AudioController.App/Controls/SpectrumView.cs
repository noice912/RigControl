using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioController.App.Controls;

/// <summary>
/// The Effects panel's spectrum strip: a Canvas that draws one bar per band (dBFS mapped from <see cref="FloorDb"/> to
/// 0 dB) plus a thin peak-hold marker above each. Display only — <see cref="Update"/> is fed by the panel's timer.
/// </summary>
public sealed class SpectrumView : Canvas
{
    private static readonly Brush BarBrush = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x3D, 0xD6, 0x8C), Color.FromRgb(0x4F, 0x8E, 0xF7), new Point(0, 1), new Point(0, 0)));

    private static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x6B, 0x72, 0x80)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE7, 0xE9, 0xEE)));
    private static readonly Pen GridPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x9B, 0xA3, 0xB0)), 1));

    private float[] _levels = new float[32];
    private float[] _peaks = new float[32];
    private bool _idle = true;

    public SpectrumView()
    {
        ClipToBounds = true;
        Array.Fill(_levels, -90f);
        Array.Fill(_peaks, -90f);
    }

    /// <summary>The level drawn as an empty bar.</summary>
    public float FloorDb { get; set; } = -90f;

    /// <summary>Number of bars currently drawn.</summary>
    public int BarCount => _levels.Length;

    /// <summary>True while no spectrum is available (idle bars are drawn).</summary>
    public bool IsIdle => _idle;

    /// <summary>New bar levels and peak markers (dBFS); redraws.</summary>
    public void Update(IReadOnlyList<float> levels, IReadOnlyList<float> peaks)
    {
        if (_levels.Length != levels.Count) _levels = new float[levels.Count];
        if (_peaks.Length != peaks.Count) _peaks = new float[peaks.Count];
        for (int i = 0; i < levels.Count; i++) _levels[i] = levels[i];
        for (int i = 0; i < peaks.Count; i++) _peaks[i] = peaks[i];
        _idle = false;
        InvalidateVisual();
    }

    /// <summary>Shows idle bars (no capture).</summary>
    public void ShowIdle()
    {
        _idle = true;
        Array.Fill(_levels, FloorDb);
        Array.Fill(_peaks, FloorDb);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // −30 / −60 dB guide lines.
        foreach (double db in new[] { -30.0, -60.0 })
        {
            double y = Math.Round(h * (db / FloorDb)) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        int n = _levels.Length;
        if (n == 0) return;
        double slot = w / n;
        double gap = Math.Min(2, slot * 0.2);
        for (int i = 0; i < n; i++)
        {
            double x = i * slot + gap / 2;
            double bw = Math.Max(1, slot - gap);
            if (_idle)
            {
                dc.DrawRectangle(IdleBrush, null, new Rect(x, h - 2, bw, 2));
                continue;
            }

            double level = Fraction(_levels[i]);
            double barH = Math.Max(1, level * h);
            dc.DrawRectangle(BarBrush, null, new Rect(x, h - barH, bw, barH));
            double peak = Fraction(_peaks[i]);
            if (peak > 0.01)
            {
                double py = Math.Min(h - 2, h - peak * h);
                dc.DrawRectangle(PeakBrush, null, new Rect(x, py, bw, 1.5));
            }
        }
    }

    private double Fraction(float db) =>
        float.IsFinite(db) ? Math.Clamp(1 - db / FloorDb, 0, 1) : 0;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
