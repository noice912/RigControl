namespace RigControl.Radio.Audio;

/// <summary>Constants for the sound the detector works on.</summary>
public static class RadioAudio
{
    /// <summary>Everything is analysed as 16 kHz mono (speech and fingerprints need nothing above 8 kHz).</summary>
    public const int SampleRate = 16000;
}

/// <summary>
/// Turns interleaved float audio at any rate into 16 kHz mono, a block at a time: channels averaged, a 7.2 kHz
/// low-pass (windowed sinc, so nothing above 8 kHz folds back), then samples taken every inputRate/16000 input samples
/// (linear interpolation between neighbours when that isn't a whole number, e.g. 44.1 kHz). Keeps its state between blocks.
/// </summary>
public sealed class MonoResampler
{
    private const int Taps = 63;
    private readonly int _channels;
    private readonly double _step;
    private readonly float[] _kernel = new float[Taps];
    private readonly float[] _history = new float[Taps];
    private int _head;
    private long _count;      // filtered samples made so far
    private double _next;     // index (in filtered samples) of the next output sample
    private float _previous;  // the filtered sample before the newest

    public MonoResampler(int inputRate, int channels)
    {
        if (inputRate < 8000 || inputRate > 384000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _step = inputRate / (double)RadioAudio.SampleRate;
        var fc = Math.Min(7200.0 / inputRate, 0.45); // cut-off as a share of the input rate
        double sum = 0;
        for (var i = 0; i < Taps; i++)
        {
            var m = i - (Taps - 1) / 2.0;
            var sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Taps - 1));
            _kernel[i] = (float)(sinc * window);
            sum += _kernel[i];
        }
        for (var i = 0; i < Taps; i++) _kernel[i] = (float)(_kernel[i] / sum);
    }

    /// <summary>Converts one block. Returns how many 16 kHz samples were written to <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        var written = 0;
        var frames = interleaved.Length / _channels;
        for (var f = 0; f < frames; f++)
        {
            float mono = 0;
            for (var c = 0; c < _channels; c++) mono += interleaved[f * _channels + c];
            mono /= _channels;
            _history[_head] = mono;
            _head = (_head + 1) % Taps;
            float filtered = 0;
            for (var i = 0; i < Taps; i++) filtered += _kernel[i] * _history[(_head + i) % Taps];

            var k = _count++;
            while (_next <= k)
            {
                if (written >= output.Length) throw new ArgumentException("The output block is too small.", nameof(output));
                var t = _next - (k - 1);
                output[written++] = t >= 1 ? filtered : _previous + (filtered - _previous) * (float)t;
                _next += _step;
            }
            _previous = filtered;
        }
        return written;
    }
}
