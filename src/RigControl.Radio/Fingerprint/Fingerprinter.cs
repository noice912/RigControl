using RigControl.Radio.Audio;

namespace RigControl.Radio.Fingerprint;

/// <summary>One peak pair: a hash of (anchor frequency, frequency step, time step) and the anchor's frame.</summary>
public readonly record struct Landmark(uint Hash, int Frame);

/// <summary>
/// Audio fingerprints in the way of Wang's 2003 Shazam paper (own code): the strongest spectral peaks of 16 kHz mono
/// sound, each paired with a few later peaks. The hashes survive loud noise because a song's own peaks stay the
/// strongest in their neighbourhood.
/// </summary>
public static class Fingerprinter
{
    public const int Window = 1024;
    public const int Hop = 512;
    public const double SecondsPerFrame = (double)Hop / RadioAudio.SampleRate;
    private const int Bins = Window / 2;
    private const int FreqReach = 15, TimeReach = 5;
    private const float OverMedianDb = 6f, FloorDb = -70f;
    private const int PeaksPerSecond = 30, FanOut = 5, MaxDt = 63, MaxDf = 127;
    private static readonly float[] Hann = Enumerable.Range(0, Window)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1)))).ToArray();

    public static IReadOnlyList<Landmark> Compute(ReadOnlySpan<float> audio16k)
    {
        var frames = audio16k.Length < Window ? 0 : (audio16k.Length - Window) / Hop + 1;
        if (frames == 0) return [];
        var db = new float[frames, Bins];
        var re = new double[Window];
        var im = new double[Window];
        var norm = 10 * Math.Log10(Window * Window / 4.0);
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < Window; i++) { re[i] = audio16k[f * Hop + i] * Hann[i]; im[i] = 0; }
            Fft(re, im);
            for (var b = 0; b < Bins; b++)
            {
                var p = re[b] * re[b] + im[b] * im[b];
                db[f, b] = p <= 1e-12 ? -120f : (float)(10 * Math.Log10(p) - norm);
            }
        }
        return Pairs(Peaks(db, frames));
    }

    private static List<(int Frame, int Bin, float Db)> Peaks(float[,] db, int frames)
    {
        // Max over +-FreqReach bins, then over +-TimeReach frames: a peak is the maximum of its neighbourhood.
        var fmax = new float[frames, Bins];
        for (var f = 0; f < frames; f++)
            for (var b = 0; b < Bins; b++)
            {
                var m = float.MinValue;
                for (var k = Math.Max(0, b - FreqReach); k <= Math.Min(Bins - 1, b + FreqReach); k++) m = Math.Max(m, db[f, k]);
                fmax[f, b] = m;
            }
        var median = new float[frames];
        var row = new float[Bins];
        for (var f = 0; f < frames; f++)
        {
            for (var b = 0; b < Bins; b++) row[b] = db[f, b];
            Array.Sort(row);
            median[f] = row[Bins / 2];
        }
        var found = new List<(int Frame, int Bin, float Db)>();
        for (var f = 0; f < frames; f++)
            for (var b = 1; b < Bins; b++)
            {
                var v = db[f, b];
                if (v < FloorDb || v < median[f] + OverMedianDb || v < fmax[f, b]) continue;
                var isMax = true;
                for (var g = Math.Max(0, f - TimeReach); g <= Math.Min(frames - 1, f + TimeReach) && isMax; g++)
                    if (fmax[g, b] > v) isMax = false;
                if (isMax) found.Add((f, b, v));
            }
        // At most PeaksPerSecond per second: keep the strongest in each one-second block.
        var block = (int)Math.Ceiling(1 / SecondsPerFrame);
        return found.GroupBy(p => p.Frame / block)
            .SelectMany(g => g.OrderByDescending(p => p.Db).Take(PeaksPerSecond))
            .OrderBy(p => p.Frame).ThenBy(p => p.Bin).ToList();
    }

    private static List<Landmark> Pairs(List<(int Frame, int Bin, float Db)> peaks)
    {
        var result = new List<Landmark>(peaks.Count * FanOut);
        for (var i = 0; i < peaks.Count; i++)
        {
            var a = peaks[i];
            var made = 0;
            for (var j = i + 1; j < peaks.Count && made < FanOut; j++)
            {
                var t = peaks[j];
                var dt = t.Frame - a.Frame;
                if (dt < 1) continue;
                if (dt > MaxDt) break;
                var df = t.Bin - a.Bin;
                if (Math.Abs(df) > MaxDf) continue;
                var hash = (uint)a.Bin | (uint)(df + 128) << 10 | (uint)dt << 18;
                result.Add(new Landmark(hash, a.Frame));
                made++;
            }
        }
        return result;
    }

    /// <summary>In-place radix-2 FFT (length a power of two).</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
