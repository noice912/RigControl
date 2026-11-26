using System.Buffers.Binary;

namespace RigControl.iPhone.Signing;

/// <summary>
/// A growable big-endian byte buffer. Apple's code-signing structures are all big-endian, even inside a
/// little-endian arm64 binary.
/// </summary>
internal sealed class BlobWriter
{
    private byte[] _buffer = new byte[256];

    public int Length { get; private set; }

    public void U8(byte value)
    {
        Ensure(1);
        _buffer[Length++] = value;
    }

    public void U32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(Length), value);
        Length += 4;
    }

    public void I32(int value) => U32(unchecked((uint)value));

    public void U64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(Length), value);
        Length += 8;
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(Length));
        Length += value.Length;
    }

    public void Zeros(int count)
    {
        Ensure(count);
        _buffer.AsSpan(Length, count).Clear();
        Length += count;
    }

    /// <summary>Overwrites four bytes already written (for lengths and offsets known only at the end).</summary>
    public void PatchU32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(at), value);

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    private void Ensure(int extra)
    {
        if (Length + extra <= _buffer.Length)
            return;
        var size = Math.Max(_buffer.Length * 2, Length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>Magic numbers and slot numbers of Apple's embedded code signature (xnu's cs_blobs.h).</summary>
internal static class CsMagic
{
    public const uint Requirement = 0xfade0c00;
    public const uint RequirementSet = 0xfade0c01;
    public const uint CodeDirectory = 0xfade0c02;
    public const uint EmbeddedSignature = 0xfade0cc0;
    public const uint Entitlements = 0xfade7171;
    public const uint EntitlementsDer = 0xfade7172;
    public const uint BlobWrapper = 0xfade0b01;

    public const uint SlotCodeDirectory = 0;
    public const uint SlotInfo = 1;
    public const uint SlotRequirements = 2;
    public const uint SlotResources = 3;
    public const uint SlotEntitlements = 5;
    public const uint SlotEntitlementsDer = 7;
    public const uint SlotAlternateCodeDirectory = 0x1000;
    public const uint SlotSignature = 0x10000;

    public const byte HashSha1 = 1;
    public const byte HashSha256 = 2;

    /// <summary>execSegFlags: this is the main binary of a process.</summary>
    public const ulong ExecSegMainBinary = 0x1;
    /// <summary>execSegFlags: get-task-allow is set (development signing).</summary>
    public const ulong ExecSegAllowUnsigned = 0x10;

    /// <summary>A blob of the simple form magic + length + payload.</summary>
    public static byte[] Wrap(uint magic, ReadOnlySpan<byte> payload)
    {
        var w = new BlobWriter();
        w.U32(magic);
        w.U32(checked((uint)(payload.Length + 8)));
        w.Bytes(payload);
        return w.ToArray();
    }
}
