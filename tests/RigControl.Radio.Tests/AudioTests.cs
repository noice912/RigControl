using RigControl.Radio.Audio;

namespace RigControl.Radio.Tests;

public class AudioTests
{
    [Fact]
    public void Resamples_48k_stereo_to_16k_mono()
    {
        const int seconds = 2;
        var input = new float[48000 * seconds * 2];
        for (var i = 0; i < 48000 * seconds; i++)
        {
            var v = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000.0) * 0.5f;
            input[2 * i] = v;
            input[2 * i + 1] = v;
        }
        var r = new MonoResampler(48000, 2);
        var output = new float[input.Length];
        var n = r.Process(input, output);
        Assert.InRange(n, 48000 * seconds / 3 - 1, 48000 * seconds / 3 + 1);
        Assert.Equal(16000, RadioAudio.SampleRate);
        // Zero crossings over the middle second: a 1 kHz tone crosses zero 2000 times a second.
        var mid = output.AsSpan(8000, 16000);
        var crossings = 0;
        for (var i = 1; i < mid.Length; i++) if (mid[i - 1] < 0 != mid[i] < 0) crossings++;
        Assert.InRange(crossings, 1990, 2010);
        Assert.InRange(mid.ToArray().Max(), 0.4f, 0.6f); // low-pass leaves a 1 kHz tone at full level
    }

    [Fact]
    public void Resamples_44k1_without_losing_time()
    {
        var input = new float[44100 * 3]; // 3 s mono
        var r = new MonoResampler(44100, 1);
        var output = new float[input.Length];
        var n = r.Process(input.AsSpan(0, 44100), output) + r.Process(input.AsSpan(44100), output.AsSpan(16000));
        Assert.InRange(n, 48000 - 2, 48000 + 2);
    }

    [Fact]
    public void Ring_buffer_keeps_the_last_samples()
    {
        var b = new RingBuffer(50);
        var data = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        b.Write(data);
        Assert.Equal(Enumerable.Range(90, 10).Select(i => (float)i), b.Last(10));
        Assert.Equal(50, b.Last(80).Length); // never more than it holds
    }

    [Fact]
    public void Rms_of_silence_is_zero()
    {
        var b = new RingBuffer(100);
        b.Write(new float[100]);
        Assert.Equal(0f, b.Rms(100));
        b.Write(Enumerable.Repeat(0.5f, 100).ToArray());
        Assert.Equal(0.5f, b.Rms(100), 3);
    }
}
