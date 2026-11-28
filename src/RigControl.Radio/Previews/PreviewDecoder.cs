using NAudio.Wave;
using RigControl.Radio.Audio;

namespace RigControl.Radio.Previews;

/// <summary>
/// Turns a downloaded song preview (MP3 or AAC) into 16 kHz mono sound with Windows' own decoders (Media Foundation).
/// Runs only in the speech helper, which has no administrator rights: a broken file can't harm the admin app.
/// </summary>
public static class PreviewDecoder
{
    /// <summary>At most this much of a preview is decoded (previews are 30 s).</summary>
    public const int MaxSeconds = 30;

    public static float[] Decode(byte[] compressed)
    {
        using var ms = new MemoryStream(compressed, writable: false);
        using var reader = new StreamMediaFoundationReader(ms);
        var source = reader.ToSampleProvider();
        var rate = source.WaveFormat.SampleRate;
        var channels = source.WaveFormat.Channels;
        if (rate is < 8000 or > 192000 || channels is < 1 or > 8) throw new InvalidDataException("Not a usable sound format.");
        var resampler = new MonoResampler(rate, channels);
        var output = new List<float>(RadioAudio.SampleRate * MaxSeconds);
        var buffer = new float[rate * channels / 4];
        var mono = new float[buffer.Length / channels * RadioAudio.SampleRate / rate + 32];
        var max = RadioAudio.SampleRate * MaxSeconds;
        int n;
        while (output.Count < max && (n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            var got = resampler.Process(buffer.AsSpan(0, n - n % channels), mono);
            for (var i = 0; i < got && output.Count < max; i++) output.Add(mono[i]);
        }
        return output.ToArray();
    }
}
