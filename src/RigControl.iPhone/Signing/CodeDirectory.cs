using System.Security.Cryptography;
using System.Text;

namespace RigControl.iPhone.Signing;

/// <summary>
/// Builds a CodeDirectory blob (magic 0xfade0c02), version 0x20400: the layout with codeLimit64 and the executable
/// segment fields that current iOS expects. It holds the identifier, the team id, one hash per special slot
/// (Info.plist, requirements, CodeResources, entitlements, DER entitlements) and one hash per 4 KB page of the
/// file up to the signature.
/// </summary>
internal static class CodeDirectory
{
    public const uint Version = 0x20400;
    public const int HeaderSize = 88;
    public const int PageSizeLog2 = 12;
    public const int PageSize = 1 << PageSizeLog2;

    /// <summary>What goes in one CodeDirectory besides the hash type.</summary>
    internal sealed record Input(
        string Identifier,
        string? TeamId,
        // Index 1..7 = special slot number; null = slot not used (written as zeros if below the highest).
        byte[]?[] SpecialSlotData,
        ulong ExecSegBase,
        ulong ExecSegLimit,
        ulong ExecSegFlags);

    public static int HashSize(byte hashType) => hashType == CsMagic.HashSha1 ? 20 : 32;

    public static byte[] Hash(byte hashType, ReadOnlySpan<byte> data) =>
        hashType == CsMagic.HashSha1 ? SHA1.HashData(data) : SHA256.HashData(data);

    public static int HighestSpecialSlot(Input input)
    {
        for (var i = input.SpecialSlotData.Length - 1; i >= 1; i--)
            if (input.SpecialSlotData[i] != null)
                return i;
        return 0;
    }

    public static int Length(Input input, byte hashType, long codeLimit)
    {
        var hs = HashSize(hashType);
        var pages = (codeLimit + PageSize - 1) / PageSize;
        return checked(HeaderSize + Encoding.UTF8.GetByteCount(input.Identifier) + 1
            + (input.TeamId == null ? 0 : Encoding.UTF8.GetByteCount(input.TeamId) + 1)
            + HighestSpecialSlot(input) * hs + (int)pages * hs);
    }

    /// <summary>Builds the blob. <paramref name="code"/> is the file from offset 0 up to the signature.</summary>
    public static byte[] Build(Input input, byte hashType, ReadOnlySpan<byte> code)
    {
        if (input.Identifier.Length == 0 || input.Identifier.Contains('\0'))
            throw new ArgumentException("The code identifier is empty or invalid.");
        var hs = HashSize(hashType);
        var nSpecial = HighestSpecialSlot(input);
        var nCode = (code.Length + PageSize - 1) / PageSize;
        var ident = Encoding.UTF8.GetBytes(input.Identifier);
        var team = input.TeamId == null ? null : Encoding.UTF8.GetBytes(input.TeamId);
        var identOffset = HeaderSize;
        var teamOffset = team == null ? 0 : identOffset + ident.Length + 1;
        var hashOffset = identOffset + ident.Length + 1 + (team == null ? 0 : team.Length + 1) + nSpecial * hs;
        var length = hashOffset + nCode * hs;

        var w = new BlobWriter();
        w.U32(CsMagic.CodeDirectory);
        w.U32((uint)length);
        w.U32(Version);
        w.U32(0);                              // flags: none for a real (non ad-hoc) signature
        w.U32((uint)hashOffset);
        w.U32((uint)identOffset);
        w.U32((uint)nSpecial);
        w.U32((uint)nCode);
        w.U32((uint)code.Length);              // codeLimit (files here are always under 4 GB)
        w.U8((byte)hs);
        w.U8(hashType);
        w.U8(0);                               // platform
        w.U8(PageSizeLog2);
        w.U32(0);                              // spare2
        w.U32(0);                              // scatterOffset
        w.U32((uint)teamOffset);
        w.U32(0);                              // spare3
        w.U64(0);                              // codeLimit64: only used above 4 GB
        w.U64(input.ExecSegBase);
        w.U64(input.ExecSegLimit);
        w.U64(input.ExecSegFlags);
        w.Bytes(ident);
        w.U8(0);
        if (team != null)
        {
            w.Bytes(team);
            w.U8(0);
        }
        // Special slots are stored backwards: slot n first, slot 1 right before the first page hash.
        for (var slot = nSpecial; slot >= 1; slot--)
        {
            var data = input.SpecialSlotData[slot];
            if (data == null)
                w.Zeros(hs);
            else
                w.Bytes(Hash(hashType, data));
        }
        for (var page = 0; page < nCode; page++)
        {
            var start = page * PageSize;
            w.Bytes(Hash(hashType, code.Slice(start, Math.Min(PageSize, code.Length - start))));
        }
        if (w.Length != length)
            throw new InvalidOperationException("CodeDirectory length mismatch.");
        return w.ToArray();
    }
}
