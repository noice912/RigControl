using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RigControl.Capture.Listen;

namespace RigControl.Radio;

/// <summary>Where the detector's sound comes from. The real one opens a Windows sound input in shared mode.</summary>
public interface IRadioInput
{
    /// <summary>Connected sound inputs that are not microphones.</summary>
    IReadOnlyList<AudioEndpoint> Inputs();

    /// <summary>
    /// Starts listening to <paramref name="input"/>: <paramref name="onAudio"/> gets interleaved float samples, the
    /// sample rate and the channel count; <paramref name="onStopped"/> is called if Windows stops it (device gone).
    /// Dispose to stop. Throws when it can't be opened.
    /// </summary>
    IDisposable Open(AudioEndpoint input, Action<float[], int, int> onAudio, Action<Exception?>? onStopped = null);
}

/// <summary>The capture card's sound through WASAPI in shared mode (so "Capture card sound" can use it at the same time).</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiRadioInput : IRadioInput
{
    private readonly WasapiListenEngine _devices = new();

    public IReadOnlyList<AudioEndpoint> Inputs() => _devices.Inputs();

    public IDisposable Open(AudioEndpoint input, Action<float[], int, int> onAudio, Action<Exception?>? onStopped = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(input.Id);
        var capture = new WasapiCapture(device, false, 100);
        var format = capture.WaveFormat;
        var isFloat = format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat
            || format is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var isPcm = !isFloat && format.BitsPerSample is 16 or 24 or 32;
        if (!isFloat && !isPcm)
        {
            capture.Dispose();
            device.Dispose();
            throw new NotSupportedException($"The capture card's sound format ({format}) isn't supported.");
        }
        capture.DataAvailable += (_, e) =>
        {
            var bytesPerSample = format.BitsPerSample / 8;
            var n = e.BytesRecorded / bytesPerSample;
            var samples = new float[n];
            var b = e.Buffer;
            for (var i = 0; i < n; i++)
            {
                var o = i * bytesPerSample;
                samples[i] = isFloat ? BitConverter.ToSingle(b, o)
                    : format.BitsPerSample switch
                    {
                        16 => BitConverter.ToInt16(b, o) / 32768f,
                        24 => (b[o] | b[o + 1] << 8 | (sbyte)b[o + 2] << 16) / 8388608f,
                        _ => BitConverter.ToInt32(b, o) / 2147483648f,
                    };
            }
            onAudio(samples, format.SampleRate, format.Channels);
        };
        capture.RecordingStopped += (_, e) => onStopped?.Invoke(e.Exception);
        capture.StartRecording();
        return new Session(capture, device);
    }

    private sealed class Session(WasapiCapture capture, MMDevice device) : IDisposable
    {
        public void Dispose()
        {
            try { capture.StopRecording(); } catch (Exception) { /* already stopped */ }
            capture.Dispose();
            device.Dispose();
        }
    }
}
