using System.Diagnostics;
using AudioController.Core.AudioMath;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace AudioController.App.Services;

/// <summary>
/// The Effects panel's live spectrum: WASAPI loopback capture of the HEADSET endpoint (what you hear, after Equalizer
/// APO), a 2048-point Hann-windowed FFT (NAudio <see cref="FastFourierTransform"/>) and <see cref="SpectrumMapper"/>'s
/// 32 log-spaced bars from 20 Hz to 20 kHz. Audio stays in memory (a 2048-sample ring) — nothing is recorded or stored.
/// </summary>
/// <remarks>
/// Runs only while the panel asks for it (window visible, not minimized): <see cref="Stop"/> disposes the capture.
/// Never throws: a busy, missing or vanished device leaves <see cref="Error"/> set and <see cref="TryGetBars"/> false
/// (idle bars). <see cref="TryGetBars"/> is called on the UI thread; capture packets arrive on NAudio's capture thread.
/// </remarks>
public sealed class SpectrumAnalyzer : IDisposable
{
    /// <summary>FFT length.</summary>
    public const int FftSize = 2048;

    /// <summary>Number of bars.</summary>
    public const int BarCount = 32;

    private const int FftOrder = 11; // 2^11 = 2048

    /// <summary>No packet for this long = nothing is playing (loopback delivers nothing then): bars fall to the floor.</summary>
    private static readonly TimeSpan SilenceAfter = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = SpectrumMapper.HannWindow(FftSize);
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2 + 1];
    private int _ringPos;
    private long _lastData;
    private int _generation;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private SpectrumMapper? _mapper;
    private Func<byte[], int, int, float>? _readSample;
    private int _frameBytes;
    private int _bytesPerSample;
    private int _channels;
    private string? _deviceId;
    private string? _error;
    private bool _disposed;

    /// <summary>Raised (on any thread) after a start finished or failed, or the capture stopped by itself.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The endpoint asked for last (null when stopped).</summary>
    public string? DeviceId
    {
        get
        {
            lock (_gate) return _deviceId;
        }
    }

    /// <summary>True while a loopback capture is running.</summary>
    public bool IsCapturing
    {
        get
        {
            lock (_gate) return _capture is not null;
        }
    }

    /// <summary>Why the spectrum is idle (device busy, gone, …), or null.</summary>
    public string? Error
    {
        get
        {
            lock (_gate) return _error;
        }
    }

    /// <summary>
    /// Starts listening to <paramref name="deviceId"/> (stops any earlier capture first). The device is opened on the
    /// thread pool; failures end up in <see cref="Error"/>. Never throws.
    /// </summary>
    public void Start(string deviceId)
    {
        int generation;
        WasapiCapture? old;
        MMDevice? oldDevice;
        lock (_gate)
        {
            if (_disposed) return;
            generation = ++_generation;
            old = _capture;
            oldDevice = _device;
            _capture = null;
            _device = null;
            _mapper = null;
            _deviceId = deviceId;
            _error = null;
            Array.Clear(_ring);
        }

        DisposeLater(old, oldDevice);
        Task.Run(() => Open(deviceId, generation));
    }

    /// <summary>Stops listening and releases the device. Idempotent; never throws.</summary>
    public void Stop()
    {
        WasapiCapture? old;
        MMDevice? oldDevice;
        lock (_gate)
        {
            _generation++;
            old = _capture;
            oldDevice = _device;
            _capture = null;
            _device = null;
            _mapper = null;
            _deviceId = null;
            _error = null;
        }

        DisposeLater(old, oldDevice);
    }

    /// <summary>
    /// The current spectrum in dBFS (<see cref="BarCount"/> values; the floor while nothing plays). False when idle
    /// (not capturing): the caller shows idle bars.
    /// </summary>
    public bool TryGetBars(Span<float> barsDb)
    {
        lock (_gate)
        {
            var mapper = _mapper;
            if (_capture is null || mapper is null)
            {
                return false;
            }

            if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastData)) > SilenceAfter)
            {
                barsDb[..BarCount].Fill(mapper.FloorDb);
                return true;
            }

            // Oldest → newest, windowed.
            for (int i = 0; i < FftSize; i++)
            {
                float s = _ring[(_ringPos + i) % FftSize];
                _fft[i].X = s * _window[i];
                _fft[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, FftOrder, _fft);
            for (int k = 0; k <= FftSize / 2; k++)
            {
                float x = _fft[k].X;
                float y = _fft[k].Y;
                _magnitudes[k] = MathF.Sqrt(x * x + y * y);
            }

            mapper.Map(_magnitudes, barsDb);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }

    private void Open(string deviceId, int generation)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        try
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                device = enumerator.GetDevice(deviceId);
            }

            if (device.State != DeviceState.Active)
            {
                throw new InvalidOperationException("the headset isn't connected");
            }

            capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            var reader = SampleReader(format, out int bytesPerSample)
                         ?? throw new InvalidOperationException($"its audio format ({format.Encoding}, {format.BitsPerSample}-bit) can't be read");

            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    throw new OperationCanceledException();
                }

                _readSample = reader;
                _bytesPerSample = bytesPerSample;
                _channels = Math.Max(1, format.Channels);
                _frameBytes = _bytesPerSample * _channels;
                _mapper = new SpectrumMapper(format.SampleRate, FftSize, BarCount);
                _lastData = 0;
            }

            var started = capture;
            capture.DataAvailable += (_, e) => OnData(started, e);
            capture.RecordingStopped += (_, e) => OnStopped(started, e.Exception);
            capture.StartRecording();

            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    throw new OperationCanceledException();
                }

                _capture = capture;
                _device = device;
                _error = null;
            }
        }
        catch (OperationCanceledException)
        {
            DisposeLater(capture, device);
            return;
        }
        catch (Exception ex)
        {
            DisposeLater(capture, device);
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _mapper = null;
                    _error = "Spectrum unavailable: " + Describe(ex);
                }
            }
        }

        RaiseStateChanged();
    }

    private void OnData(WasapiCapture source, WaveInEventArgs e)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(source, _capture) && _capture is not null) return;
                var read = _readSample;
                if (read is null || _frameBytes == 0) return;
                int frames = e.BytesRecorded / _frameBytes;
                for (int f = 0; f < frames; f++)
                {
                    int o = f * _frameBytes;
                    float sum = 0f;
                    for (int c = 0; c < _channels; c++)
                    {
                        sum += read(e.Buffer, o + c * _bytesPerSample, _bytesPerSample);
                    }

                    _ring[_ringPos] = sum / _channels;
                    _ringPos = (_ringPos + 1) % FftSize;
                }

                if (frames > 0)
                {
                    Volatile.Write(ref _lastData, Stopwatch.GetTimestamp());
                }
            }
        }
        catch (Exception)
        {
            // A malformed packet is dropped; the display must never break the capture thread.
        }
    }

    private void OnStopped(WasapiCapture source, Exception? exception)
    {
        MMDevice? device = null;
        lock (_gate)
        {
            if (!ReferenceEquals(source, _capture)) return;
            _capture = null;
            device = _device;
            _device = null;
            _mapper = null;
            _error = "Spectrum stopped: " + (exception is null ? "the headset stopped playing" : Describe(exception));
        }

        // Never dispose a capture from its own thread (Dispose joins it).
        DisposeLater(source, device);
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber failure must not escape a capture or pool thread.
        }
    }

    /// <summary>Reads one sample of the capture format as float, or null for a format that can't be read.</summary>
    private static Func<byte[], int, int, float>? SampleReader(WaveFormat format, out int bytesPerSample)
    {
        bytesPerSample = format.BitsPerSample / 8;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
                     || (format is WaveFormatExtensible ext2 && ext2.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));
        return (isFloat, isPcm, format.BitsPerSample) switch
        {
            (true, _, 32) => static (b, o, _) => BitConverter.ToSingle(b, o),
            (_, true, 16) => static (b, o, _) => BitConverter.ToInt16(b, o) / 32768f,
            (_, true, 24) => static (b, o, _) => ((b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)) << 8 >> 8) / 8388608f,
            (_, true, 32) => static (b, o, _) => BitConverter.ToInt32(b, o) / 2147483648f,
            _ => null,
        };
    }

    private static void DisposeLater(WasapiCapture? capture, MMDevice? device)
    {
        if (capture is null && device is null) return;
        Task.Run(() =>
        {
            try { capture?.StopRecording(); } catch (Exception) { /* already stopped */ }
            try { capture?.Dispose(); } catch (Exception) { /* best effort */ }
            try { device?.Dispose(); } catch (Exception) { /* best effort */ }
        });
    }

    private static string Describe(Exception ex) => ex switch
    {
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88890004 => "the headset was disconnected",
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x8889000A => "the headset is in use by another program",
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x80070490 => "the headset isn't connected",
        _ => ex.Message,
    };
}
