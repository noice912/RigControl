using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace RigControl.Lighting;

/// <summary>
/// How hard one band of the sound hits right now, 0 to 1, from mono samples: two filter stages (low-pass for the
/// bass, high-pass for the treble), a fast-attack / quick-release envelope, and an automatic level (the loudest recent
/// hit counts as full), so it works at any volume. Silence reads 0. Pure arithmetic: no audio device is touched here.
/// </summary>
public sealed class BandFollower
{
    private readonly BiQuadFilter _f1, _f2;
    private readonly float _attack, _release, _peakFall;
    private float _env, _peak;

    private BandFollower(BiQuadFilter f1, BiQuadFilter f2, int rate, float attack, float release)
    {
        _f1 = f1;
        _f2 = f2;
        _attack = Coef(rate, attack);
        _release = Coef(rate, release);
        _peakFall = Coef(rate, 4f); // the "full" mark relaxes over a few seconds
    }

    /// <summary>Kicks and bass lines: below about 120 Hz, falling back over ~150 ms.</summary>
    public static BandFollower Bass(int rate) =>
        new(BiQuadFilter.LowPassFilter(rate, 120, 0.707f), BiQuadFilter.LowPassFilter(rate, 120, 0.707f), rate, 0.005f, 0.150f);

    /// <summary>Vocals, guitars and synths: about 250 Hz to 2 kHz, ~4 ms in, ~120 ms out.</summary>
    public static BandFollower Mid(int rate) =>
        new(BiQuadFilter.HighPassFilter(rate, 250, 0.707f), BiQuadFilter.LowPassFilter(rate, 2000, 0.707f), rate, 0.004f, 0.120f);

    /// <summary>Hi-hats, cymbals and snares: above about 4 kHz, quicker both ways (~2 ms in, ~80 ms out).</summary>
    public static BandFollower Treble(int rate) =>
        new(BiQuadFilter.HighPassFilter(rate, 4000, 0.707f), BiQuadFilter.HighPassFilter(rate, 4000, 0.707f), rate, 0.002f, 0.080f);

    private static float Coef(int rate, float seconds) => 1f - MathF.Exp(-1f / (rate * seconds));

    /// <summary>The newest level, 0 to 1, before gain.</summary>
    public float Level { get; private set; }

    public void Add(ReadOnlySpan<float> mono)
    {
        foreach (var x in mono)
        {
            var v = MathF.Abs(_f2.Transform(_f1.Transform(x)));
            _env += (v - _env) * (v > _env ? _attack : _release);
            _peak = _env > _peak ? _env : _peak + (_env - _peak) * _peakFall;
        }
        // Below a quiet floor nothing is playing (or it is too soft to matter): no pulse.
        Level = _peak < 0.003f ? 0f : Math.Clamp(_env / Math.Max(_peak, 0.02f), 0f, 1f);
    }

    /// <summary>
    /// A level with gain applied (percent, 100 = as heard): more gain reaches "full" on softer hits and lifts the
    /// quiet parts; less gain only lets the strongest hits through.
    /// </summary>
    public static float WithGain(float level, int gain) => Math.Clamp(level * Math.Clamp(gain, 25, 400) / 100f, 0f, 1f);
}

public static class BassFollower
{
    /// <summary>The brightness factor for a bass level: 1 at full bass, down to 1 - strength between hits.</summary>
    public static double Factor(float level, int strength)
    {
        var k = Math.Clamp(strength, 0, 100) / 100.0;
        return (1 - k) + k * Math.Pow(Math.Clamp(level, 0f, 1f), 0.8);
    }

    /// <summary>The colour turned round the colour wheel by up to 60 degrees: level x strength.</summary>
    public static (double R, double G, double B) Shift((double R, double G, double B) c, float level, int strength)
    {
        var degrees = Math.Clamp(level, 0f, 1f) * Math.Clamp(strength, 0, 100) / 100.0 * 60;
        return degrees <= 0.5 ? c : ColorMath.RotateHue(c, degrees);
    }

    /// <summary>A treble flash: the colour mixed toward white (at the given brightness) by level x strength.</summary>
    public static (double R, double G, double B) Sparkle((double R, double G, double B) c, float level, int strength, int brightness)
    {
        var t = Math.Clamp(level, 0f, 1f) * Math.Clamp(strength, 0, 100) / 100.0;
        if (t <= 0) return c;
        var white = 255 * Math.Clamp(brightness, 0, 100) / 100.0;
        return (c.R + (white - c.R) * t, c.G + (white - c.G) * t, c.B + (white - c.B) * t);
    }
}

/// <summary>
/// Listens to what the PC plays (WASAPI loopback of the default output, the same way the audio spectrum does) and
/// keeps a bass and a treble <see cref="BandFollower"/> up to date. Audio stays in memory; nothing is recorded or
/// saved. When the default output changes, it moves to the new one.
/// </summary>
public sealed class BassMeter : IDisposable
{
    private readonly object _gate = new();
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private BandFollower? _bass, _mid, _treble;
    private float[] _mono = Array.Empty<float>();
    private DateTime _lastData = DateTime.MinValue;
    private DateTime _lastCheck = DateTime.MinValue;
    private string? _deviceId;

    public string? DeviceName { get; private set; }
    public string? Problem { get; private set; }

    /// <summary>The bass level now (0 when nothing has played for 200 ms). Opens or moves the capture as needed.</summary>
    public float Level => Read(bass: true);

    /// <summary>The mid-range level now (0 when nothing has played for 200 ms).</summary>
    public float MidLevel => Read(band: 1);

    /// <summary>The treble level now (0 when nothing has played for 200 ms).</summary>
    public float TrebleLevel => Read(band: 2);

    private float Read(bool bass) => Read(bass ? 0 : 2);

    private float Read(int band)
    {
        if ((DateTime.UtcNow - _lastCheck).TotalSeconds >= 5 || (_capture is null && (DateTime.UtcNow - _lastCheck).TotalSeconds >= 2)) Check();
        lock (_gate)
        {
            var f = band switch { 0 => _bass, 1 => _mid, _ => _treble };
            if (f is null || (DateTime.UtcNow - _lastData).TotalMilliseconds > 200) return 0f;
            return f.Level;
        }
    }

    private void Check()
    {
        _lastCheck = DateTime.UtcNow;
        try
        {
            using var e = new MMDeviceEnumerator();
            if (!e.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                Close();
                Problem = "no sound output";
                return;
            }
            var id = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            if (id == _deviceId && _capture is not null) return;
            Close();
            var device = e.GetDevice(id);
            var capture = new WasapiLoopbackCapture(device);
            var f = capture.WaveFormat;
            if (!(f.Encoding == WaveFormatEncoding.IeeeFloat && f.BitsPerSample == 32)
                && !(f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32))
            {
                capture.Dispose();
                device.Dispose();
                throw new InvalidOperationException($"its audio format ({f.Encoding}, {f.BitsPerSample}-bit) can't be read");
            }
            lock (_gate)
            {
                _bass = BandFollower.Bass(f.SampleRate);
                _mid = BandFollower.Mid(f.SampleRate);
                _treble = BandFollower.Treble(f.SampleRate);
            }
            var channels = Math.Max(1, f.Channels);
            capture.DataAvailable += (_, a) => OnData(a.Buffer, a.BytesRecorded, channels);
            capture.RecordingStopped += (_, _) => { lock (_gate) _lastData = DateTime.MinValue; };
            capture.StartRecording();
            _capture = capture;
            _device = device;
            _deviceId = id;
            DeviceName = device.FriendlyName;
            Problem = null;
        }
        catch (Exception ex)
        {
            Close();
            Problem = ex.Message;
        }
    }

    private void OnData(byte[] buffer, int bytes, int channels)
    {
        var frames = bytes / 4 / channels;
        if (frames <= 0) return;
        lock (_gate)
        {
            if (_bass is null || _mid is null || _treble is null) return;
            if (_mono.Length < frames) _mono = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToSingle(buffer, 4 * (i * channels + c));
                _mono[i] = sum / channels;
            }
            _bass.Add(_mono.AsSpan(0, frames));
            _mid.Add(_mono.AsSpan(0, frames));
            _treble.Add(_mono.AsSpan(0, frames));
            _lastData = DateTime.UtcNow;
        }
    }

    private void Close()
    {
        try { _capture?.StopRecording(); } catch { /* ignore */ }
        try { _capture?.Dispose(); } catch { /* ignore */ }
        try { _device?.Dispose(); } catch { /* ignore */ }
        _capture = null;
        _device = null;
        _deviceId = null;
        lock (_gate)
        {
            _bass = null;
            _mid = null;
            _treble = null;
        }
    }

    public void Dispose() => Close();
}
