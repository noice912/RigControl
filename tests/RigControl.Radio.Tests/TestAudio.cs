using RigControl.Radio.Audio;

namespace RigControl.Radio.Tests;

/// <summary>Made-up "songs" for the tests: chords that change every quarter second plus drum hits, from a seed.</summary>
internal static class TestAudio
{
    public static float[] Song(int seed, double seconds)
    {
        var rate = RadioAudio.SampleRate;
        var n = (int)(seconds * rate);
        var audio = new float[n];
        var rng = new Random(seed);
        var noteLen = rate / 4;
        double[] freqs = [];
        for (var i = 0; i < n; i++)
        {
            if (i % noteLen == 0)
                freqs = Enumerable.Range(0, 3).Select(_ => 110 * Math.Pow(2, rng.Next(0, 48) / 12.0)).ToArray();
            var t = i / (double)rate;
            double v = 0;
            foreach (var f in freqs) v += Math.Sin(2 * Math.PI * f * t) + 0.4 * Math.Sin(4 * Math.PI * f * t);
            audio[i] = (float)(v / 8);
        }
        var drums = new Random(seed * 7919 + 1);
        for (var beat = 0; beat < n; beat += rate / 2)
            for (var j = 0; j < rate / 20 && beat + j < n; j++)
                audio[beat + j] += (float)((drums.NextDouble() * 2 - 1) * 0.3 * (1 - j / (rate / 20.0)));
        return audio;
    }

    /// <summary>White plus brown noise with the same power as <paramref name="signal"/> (0 dB), added to a copy.</summary>
    public static float[] WithNoise(float[] signal, int seed)
    {
        var rng = new Random(seed);
        var noise = new double[signal.Length];
        double brown = 0;
        for (var i = 0; i < noise.Length; i++)
        {
            var w = rng.NextDouble() * 2 - 1;
            brown = Math.Clamp(brown * 0.98 + w * 0.1, -1, 1);
            noise[i] = w + brown * 3;
        }
        double ps = signal.Average(s => (double)s * s), pn = noise.Average(x => x * x);
        var k = Math.Sqrt(ps / pn);
        return signal.Select((s, i) => (float)(s + noise[i] * k)).ToArray();
    }

    public static float[] Slice(float[] audio, double from, double to) =>
        audio[(int)(from * RadioAudio.SampleRate)..(int)(to * RadioAudio.SampleRate)];
}
