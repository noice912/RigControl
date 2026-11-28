using System.Buffers.Binary;
using System.Text;

namespace RigControl.Radio.Speech;

/// <summary>
/// What RigControl and its speech helper send each other over their private pipe: [type byte][length u32][payload].
/// Every frame is size-capped; the admin app treats whatever the helper sends as plain, untrusted text.
/// </summary>
public static class SpeechWire
{
    public const byte Audio = 1; // app -> helper: 16 kHz mono float32 samples
    public const byte Text = 2;  // helper -> app: the words heard (UTF-8)
    public const byte Ready = 3; // helper -> app: "gpu" or "cpu"
    public const byte Error = 4; // helper -> app: what went wrong

    public const int MaxAudioBytes = 30 * 16000 * sizeof(float);
    public const int MaxTextBytes = 8 * 1024;

    public static Task WriteAudioAsync(Stream s, float[] samples, CancellationToken ct)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return WriteAsync(s, Audio, bytes, ct);
    }

    public static Task WriteTextAsync(Stream s, byte type, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes) bytes = bytes[..MaxTextBytes];
        return WriteAsync(s, type, bytes, ct);
    }

    private static async Task WriteAsync(Stream s, byte type, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > Cap(type)) throw new InvalidDataException("Frame too big.");
        var head = new byte[5];
        head[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(1), payload.Length);
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. EndOfStreamException when the other side closed; InvalidDataException for nonsense.</summary>
    public static async Task<(byte Type, byte[] Payload)> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[5];
        await s.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var type = head[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1));
        if (type is < Audio or > Error || length < 0 || length > Cap(type)) throw new InvalidDataException("Not a speech helper frame.");
        var payload = new byte[length];
        await s.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return (type, payload);
    }

    public static float[] ToSamples(byte[] payload)
    {
        if (payload.Length % sizeof(float) != 0) throw new InvalidDataException("Not whole samples.");
        var samples = new float[payload.Length / sizeof(float)];
        Buffer.BlockCopy(payload, 0, samples, 0, payload.Length);
        return samples;
    }

    public static string ToText(byte[] payload) => new UTF8Encoding(false, false).GetString(payload);

    private static int Cap(byte type) => type == Audio ? MaxAudioBytes : MaxTextBytes;
}
