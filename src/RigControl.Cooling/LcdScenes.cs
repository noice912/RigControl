using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using NAudio.Dsp;
using NAudio.Wave;

namespace RigControl.Cooling;

/// <summary>How loud each part of what the PC is playing is right now, for the audio visualizer. Abstracted for tests.</summary>
public interface ISpectrumSource : IDisposable
{
    /// <summary><paramref name="count"/> values from 0 (silent) to 1 (loud), low notes first. Starts listening on first use.</summary>
    float[] Bars(int count);

    /// <summary>Stops listening until <see cref="Bars"/> is called again.</summary>
    void Stop();
}

/// <summary>Used in tests and for previews: a fixed, pleasant-looking pattern; nothing is listened to.</summary>
public sealed class SampleSpectrum : ISpectrumSource
{
    public float[] Bars(int count)
    {
        var bars = new float[count];
        for (var i = 0; i < count; i++) bars[i] = 0.25f + 0.6f * (float)Math.Abs(Math.Sin(i * 0.55) * Math.Cos(i * 0.21));
        return bars;
    }

    public void Stop() { }
    public void Dispose() { }
}

/// <summary>The real thing: listens to the default playback device (WASAPI loopback) and measures its frequencies.</summary>
public sealed class LoopbackSpectrum : ISpectrumSource
{
    private const int FftBits = 10;
    private const int FftSize = 1 << FftBits;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize * 2];
    private int _pos;
    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private DateTime _lastData = DateTime.MinValue;
    private float[] _smooth = Array.Empty<float>();
    private DateTime _retryAfter = DateTime.MinValue;

    public float[] Bars(int count)
    {
        lock (_gate)
        {
            EnsureStarted();
            if (_smooth.Length != count) _smooth = new float[count];
            var fresh = new float[count];
            // Windows sends nothing while the PC is silent, so old samples must not be shown as sound.
            if (_capture is not null && DateTime.UtcNow - _lastData < TimeSpan.FromMilliseconds(300))
            {
                var data = new Complex[FftSize];
                for (var i = 0; i < FftSize; i++)
                {
                    var sample = _ring[(_pos - FftSize + i + _ring.Length) % _ring.Length];
                    data[i].X = (float)(sample * FastFourierTransform.HannWindow(i, FftSize));
                    data[i].Y = 0;
                }
                FastFourierTransform.FFT(true, FftBits, data);
                var binHz = _sampleRate / (double)FftSize;
                for (var b = 0; b < count; b++)
                {
                    // Bands are spaced like musical pitch: evenly on a logarithmic scale from 40 Hz to 16 kHz.
                    var lo = 40 * Math.Pow(400, b / (double)count);
                    var hi = 40 * Math.Pow(400, (b + 1) / (double)count);
                    var from = Math.Clamp((int)(lo / binHz), 1, FftSize / 2 - 1);
                    var to = Math.Clamp((int)Math.Ceiling(hi / binHz), from + 1, FftSize / 2);
                    double peak = 0;
                    for (var k = from; k < to; k++) peak = Math.Max(peak, Math.Sqrt(data[k].X * data[k].X + data[k].Y * data[k].Y));
                    var db = 20 * Math.Log10(peak + 1e-9);
                    fresh[b] = (float)Math.Clamp((db + 72) / 54, 0, 1);
                }
            }
            for (var b = 0; b < count; b++) _smooth[b] = Math.Max(fresh[b], _smooth[b] * 0.72f); // rise at once, fall gently
            return (float[])_smooth.Clone();
        }
    }

    private void EnsureStarted()
    {
        if (_capture is not null || DateTime.UtcNow < _retryAfter) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            _sampleRate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += OnData;
            capture.StartRecording();
            _capture = capture;
        }
        catch
        {
            _retryAfter = DateTime.UtcNow.AddSeconds(10); // no playback device right now
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = sender as WasapiLoopbackCapture;
        if (capture is null || e.BytesRecorded <= 0) return;
        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample is not (2 or 4)) return;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        lock (_gate)
        {
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var offset = (f * channels + c) * bytesPerSample;
                    sum += bytesPerSample == 4 ? BitConverter.ToSingle(e.Buffer, offset) : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                }
                _ring[_pos] = sum / channels;
                _pos = (_pos + 1) % _ring.Length;
            }
            _lastData = DateTime.UtcNow;
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.StopRecording();
            capture.Dispose();
        }
        catch
        {
            // the device went away
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Draws whatever the screen is set to show, one upright 640x640 picture at a time: infographics (optionally over a
/// picture or GIF), a clock, an audio visualizer, or a web page. Keeps what it needs between frames (the decoded
/// background, the sound listener) and lets go of it when the mode changes.
/// </summary>
public sealed class LcdComposer : IDisposable
{
    public static readonly string[] LiveModes = { "infographic", "clock", "visualizer", "web" };
    private const int MaxBackgroundFrames = 60;
    private const int VisualizerBars = 48;

    private readonly ISpectrumSource _spectrum;
    private readonly List<(Bitmap Frame, int EndsAtCs)> _background = new();
    private string? _backgroundPath;
    private bool _webActive;

    public LcdComposer(ISpectrumSource spectrum) => _spectrum = spectrum;

    /// <summary>Gives the newest picture of a web page as PNG bytes (null while it loads). Called with null to close the page.</summary>
    public Func<string?, byte[]?>? WebFrames { get; set; }

    /// <summary>The lights' colour while "letters follow the lights" applies (null = normal letters).</summary>
    public Func<Color?>? TextColor { get; set; }

    public static bool IsLive(string mode) => LiveModes.Contains(mode);

    /// <summary>How often the screen should be redrawn for this setting.</summary>
    public int FramesPerSecond(LcdSettings l)
    {
        return l.Mode switch
        {
            "visualizer" => 12,
            "web" => 4,
            // Following the lights: 10 a second so moving effects (colour cycle, breathing) look smooth on the screen too.
            "clock" or "infographic" => BackgroundIsAnimated(l) || TextColor?.Invoke() is not null ? 10 : 1,
            _ => 1,
        };
    }

    private bool BackgroundIsAnimated(LcdSettings l)
    {
        EnsureBackground(l.Mode is "infographic" or "clock" ? l.Background : null);
        return _background.Count > 1;
    }

    private void EnsureBackground(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = null;
        if (path == _backgroundPath) return;
        foreach (var (frame, _) in _background) frame.Dispose();
        _background.Clear();
        _backgroundPath = path;
        if (path is null) return;
        try
        {
            using var source = Image.FromFile(path);
            var count = 1;
            var animated = source.FrameDimensionsList.Contains(FrameDimension.Time.Guid);
            if (animated) count = Math.Max(1, source.GetFrameCount(FrameDimension.Time));
            var delays = new int[count];
            Array.Fill(delays, 10);
            if (animated && count > 1 && source.GetPropertyItem(0x5100)?.Value is { } raw)
                for (var i = 0; i < count && i * 4 + 3 < raw.Length; i++)
                {
                    var d = BitConverter.ToInt32(raw, i * 4);
                    delays[i] = d <= 1 ? 10 : Math.Min(d, 6000);
                }
            // A long animation is thinned out (keeping its speed) so it does not fill memory.
            var step = (count + MaxBackgroundFrames - 1) / MaxBackgroundFrames;
            var clock = 0;
            for (var i = 0; i < count; i += step)
            {
                if (animated && count > 1) source.SelectActiveFrame(FrameDimension.Time, i);
                for (var k = i; k < Math.Min(count, i + step); k++) clock += delays[k];
                _background.Add((LcdRenderer.FitToScreen(source, 0), clock));
            }
        }
        catch
        {
            // unreadable picture: carry on without a background
            foreach (var (frame, _) in _background) frame.Dispose();
            _background.Clear();
        }
    }

    private Bitmap Canvas(string? backgroundPath, DateTime now, int dim)
    {
        EnsureBackground(backgroundPath);
        const int s = KrakenLcdProtocol.Size;
        var bmp = new Bitmap(s, s, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        if (_background.Count == 0)
        {
            g.Clear(Color.Black);
            return bmp;
        }
        var total = _background[^1].EndsAtCs;
        var at = total <= 0 ? 0 : (long)(now.TimeOfDay.TotalMilliseconds / 10) % total;
        var frame = _background.FirstOrDefault(f => at < f.EndsAtCs).Frame ?? _background[0].Frame;
        g.DrawImageUnscaled(frame, 0, 0);
        // Darken the picture so the numbers stay readable on top of it.
        using var shade = new SolidBrush(Color.FromArgb(dim, 0, 0, 0));
        g.FillRectangle(shade, 0, 0, s, s);
        return bmp;
    }

    /// <summary>Draws the current picture for these settings. The caller disposes it.</summary>
    public Bitmap Compose(LcdSettings l, LcdMetrics metrics, DateTime now)
    {
        if (l.Mode != "visualizer") _spectrum.Stop();
        if (l.Mode != "web" && _webActive)
        {
            _webActive = false;
            try { WebFrames?.Invoke(null); } catch { /* ignore */ }
        }

        switch (l.Mode)
        {
            case "clock":
            {
                var bmp = Canvas(l.Background, now, 130);
                using var g = Graphics.FromImage(bmp);
                LcdScenes.PaintClock(g, TextColor?.Invoke() ?? LcdRenderer.ParseAccent(l.Accent), now);
                return bmp;
            }
            case "visualizer":
            {
                var bmp = Canvas(null, now, 0);
                using var g = Graphics.FromImage(bmp);
                LcdScenes.PaintVisualizer(g, TextColor?.Invoke() ?? LcdRenderer.ParseAccent(l.Accent), _spectrum.Bars(VisualizerBars));
                return bmp;
            }
            case "web":
            {
                _webActive = true;
                byte[]? png = null;
                try { png = WebFrames?.Invoke(l.WebUrl); } catch { /* shown as loading */ }
                if (png is not null)
                {
                    try
                    {
                        using var ms = new MemoryStream(png);
                        using var page = Image.FromStream(ms);
                        return LcdRenderer.FitToScreen(page, 0);
                    }
                    catch
                    {
                        // fall through to the placeholder
                    }
                }
                var bmp = Canvas(null, now, 0);
                using var g = Graphics.FromImage(bmp);
                LcdScenes.PaintMessage(g, WebFrames is null ? "Web pages need the RigControl window" : "Loading page…");
                return bmp;
            }
            default:
            {
                var bmp = Canvas(l.Background, now, 120);
                using var g = Graphics.FromImage(bmp);
                LcdRenderer.Paint(g, l.Layout, l.Primary, l.Secondary, l.Tertiary, l.Accent, metrics, TextColor?.Invoke());
                return bmp;
            }
        }
    }

    /// <summary>Lets go of everything that is only needed while the screen is being drawn live.</summary>
    public void Release()
    {
        _spectrum.Stop();
        if (_webActive)
        {
            _webActive = false;
            try { WebFrames?.Invoke(null); } catch { /* ignore */ }
        }
        EnsureBackground(null);
    }

    public void Dispose()
    {
        Release();
        _spectrum.Dispose();
    }
}

/// <summary>The drawing for the clock, the visualizer and short messages.</summary>
public static class LcdScenes
{
    private const int S = KrakenLcdProtocol.Size;
    private const float C = S / 2f;

    private static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    private static PointF Polar(double degreesFromTop, float radius)
    {
        var a = (degreesFromTop - 90) * Math.PI / 180;
        return new PointF(C + (float)(Math.Cos(a) * radius), C + (float)(Math.Sin(a) * radius));
    }

    public static void PaintClock(Graphics g, Color accent, DateTime now)
    {
        Setup(g);
        var soft = Color.FromArgb(255, 0x9A, 0xA4, 0xB2);
        using (var minor = new Pen(Color.FromArgb(255, 0x4A, 0x52, 0x5E), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var major = new Pen(Color.White, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            for (var i = 0; i < 60; i++)
            {
                var isHour = i % 5 == 0;
                g.DrawLine(isHour ? major : minor, Polar(i * 6, isHour ? 262 : 276), Polar(i * 6, 292));
            }
        }
        using (var font = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(soft))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(now.ToString("ddd d").ToUpperInvariant(), font, brush, new RectangleF(170, 410, 300, 50), center);

        var seconds = now.Second + now.Millisecond / 1000.0;
        var minutes = now.Minute + seconds / 60;
        var hours = now.Hour % 12 + minutes / 60;
        using (var hour = new Pen(Color.White, 16) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(hour, Polar(hours * 30 + 180, 28), Polar(hours * 30, 150));
        using (var minute = new Pen(Color.White, 10) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(minute, Polar(minutes * 6 + 180, 34), Polar(minutes * 6, 232));
        using (var second = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(second, Polar(now.Second * 6 + 180, 44), Polar(now.Second * 6, 252));
        using var dot = new SolidBrush(accent);
        g.FillEllipse(dot, C - 13, C - 13, 26, 26);
    }

    public static void PaintVisualizer(Graphics g, Color accent, float[] bars)
    {
        Setup(g);
        const float inner = 118, reach = 172;
        using var track = new Pen(Color.FromArgb(255, 0x1E, 0x23, 0x2B), 10) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var bar = new Pen(accent, 10) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (var i = 0; i < bars.Length; i++)
        {
            // Low notes at the top, running clockwise and mirrored on the left half so the ring looks balanced.
            var half = bars.Length / 2;
            var value = Math.Clamp(i < half ? bars[i] : bars[bars.Length - 1 - i], 0, 1);
            var angle = i * 360.0 / bars.Length + 180.0 / bars.Length;
            g.DrawLine(track, Polar(angle, inner), Polar(angle, inner + 6));
            if (value > 0.02f) g.DrawLine(bar, Polar(angle, inner), Polar(angle, inner + 6 + reach * value));
        }
        using var ring = new Pen(Color.FromArgb(255, 0x2A, 0x30, 0x3A), 4);
        g.DrawEllipse(ring, C - 96, C - 96, 192, 192);
    }

    public static void PaintMessage(Graphics g, string text)
    {
        Setup(g);
        using var font = new Font("Segoe UI", 36, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(255, 0x9A, 0xA4, 0xB2));
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(90, 220, 460, 200), center);
    }
}
