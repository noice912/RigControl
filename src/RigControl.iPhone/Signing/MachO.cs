using System.Buffers.Binary;
using System.Text;

namespace RigControl.iPhone.Signing;

/// <summary>A segment load command of a 64-bit Mach-O, with where its command sits in the header.</summary>
internal sealed record MachOSegment(string Name, int CommandOffset, ulong VmAddr, ulong VmSize, ulong FileOff, ulong FileSize);

/// <summary>
/// One 64-bit little-endian Mach-O image (a thin file, or one slice of a universal file). Only what signing needs
/// is read: the header, the segments, the lowest file offset any section or segment data starts at (to know how
/// much padding follows the load commands), and LC_CODE_SIGNATURE. Every offset is checked against the data, since
/// the input comes from an .ipa we did not build.
/// </summary>
internal sealed class MachOImage
{
    public const uint Magic64 = 0xfeedfacf;
    public const uint Magic32 = 0xfeedface;
    public const uint MagicFat = 0xcafebabe;
    public const uint MagicFat64 = 0xcafebabf;
    public const int HeaderSize = 32;
    public const uint LcSegment64 = 0x19;
    public const uint LcCodeSignature = 0x1d;
    public const uint FileTypeExecute = 2;

    public uint CpuType { get; private init; }
    public uint FileType { get; private init; }
    public uint CommandCount { get; private init; }
    public uint CommandsSize { get; private init; }
    public List<MachOSegment> Segments { get; } = [];

    /// <summary>Offset of the LC_CODE_SIGNATURE command in the header, or -1.</summary>
    public int CodeSignatureCommandOffset { get; private set; } = -1;
    public uint CodeSignatureOffset { get; private set; }
    public uint CodeSignatureSize { get; private set; }

    /// <summary>The first byte after the header that holds section or segment content (the padding ends here).</summary>
    public ulong FirstContentOffset { get; private set; }

    public MachOSegment? Segment(string name) => Segments.FirstOrDefault(s => s.Name == name);

    public static MachOImage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("The Mach-O file is too short.");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic == Magic32)
            throw new InvalidDataException("32-bit Mach-O code can't be signed for current iPhones (arm64 only).");
        if (magic != Magic64)
            throw new InvalidDataException("Not a 64-bit little-endian Mach-O image.");

        var image = new MachOImage
        {
            CpuType = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            FileType = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            CommandCount = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            CommandsSize = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
        };
        if (image.CommandsSize > data.Length - HeaderSize || image.CommandCount > image.CommandsSize / 8)
            throw new InvalidDataException("The Mach-O load commands run past the end of the file.");

        var first = (ulong)data.Length;
        var at = HeaderSize;
        var end = HeaderSize + (int)image.CommandsSize;
        for (var i = 0; i < image.CommandCount; i++)
        {
            if (at + 8 > end)
                throw new InvalidDataException("The Mach-O load commands are damaged.");
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (size < 8 || size % 8 != 0 || size > (uint)(end - at))
                throw new InvalidDataException("A Mach-O load command has a bad size.");
            var command = data.Slice(at, (int)size);

            if (cmd == LcSegment64)
            {
                if (size < 72)
                    throw new InvalidDataException("A Mach-O segment command is too short.");
                var name = ReadName(command[8..24]);
                var seg = new MachOSegment(name, at,
                    BinaryPrimitives.ReadUInt64LittleEndian(command[24..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(command[32..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(command[40..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(command[48..]));
                if (seg.FileOff > (ulong)data.Length || seg.FileSize > (ulong)data.Length - seg.FileOff)
                    throw new InvalidDataException($"The Mach-O segment {name} runs past the end of the file.");
                image.Segments.Add(seg);
                if (seg.FileSize > 0 && seg.FileOff > 0)
                    first = Math.Min(first, seg.FileOff);

                var sections = BinaryPrimitives.ReadUInt32LittleEndian(command[64..]);
                if (sections > (size - 72) / 80)
                    throw new InvalidDataException($"The Mach-O segment {name} lists more sections than fit.");
                for (var s = 0; s < sections; s++)
                {
                    var sect = command.Slice(72 + s * 80, 80);
                    var offset = BinaryPrimitives.ReadUInt32LittleEndian(sect[48..]);
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(sect[64..]);
                    var type = flags & 0xff;
                    // Zero-fill sections (S_ZEROFILL 1, S_GB_ZEROFILL 0xc, S_THREAD_LOCAL_ZEROFILL 0x12) take no file space.
                    if (offset != 0 && type is not (0x1 or 0xc or 0x12))
                        first = Math.Min(first, offset);
                }
            }
            else if (cmd == LcCodeSignature)
            {
                if (size < 16 || image.CodeSignatureCommandOffset >= 0)
                    throw new InvalidDataException("The Mach-O has a damaged LC_CODE_SIGNATURE.");
                image.CodeSignatureCommandOffset = at;
                image.CodeSignatureOffset = BinaryPrimitives.ReadUInt32LittleEndian(command[8..]);
                image.CodeSignatureSize = BinaryPrimitives.ReadUInt32LittleEndian(command[12..]);
                if ((ulong)image.CodeSignatureOffset + image.CodeSignatureSize > (ulong)data.Length)
                    throw new InvalidDataException("The Mach-O code signature runs past the end of the file.");
            }
            at += (int)size;
        }
        image.FirstContentOffset = first;
        return image;
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        var len = raw.IndexOf((byte)0);
        return Encoding.ASCII.GetString(len < 0 ? raw : raw[..len]);
    }

    /// <summary>True when the bytes start like a Mach-O image or a universal (fat) Mach-O file.</summary>
    public static bool LooksLikeMachO(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8)
            return false;
        var le = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (le is Magic64 or Magic32)
            return true;
        var be = BinaryPrimitives.ReadUInt32BigEndian(head);
        // Java class files share 0xcafebabe; their next field (a version, 45 or more) tells them apart.
        return be is MagicFat or MagicFat64 && BinaryPrimitives.ReadUInt32BigEndian(head[4..]) is >= 1 and <= 32;
    }

    public static bool FileLooksLikeMachO(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var f = File.OpenRead(path);
        return f.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && LooksLikeMachO(head);
    }
}

/// <summary>One slice of a universal file, as written in its fat header.</summary>
internal sealed record FatSlice(uint CpuType, uint CpuSubtype, uint Offset, uint Size, uint Align);

/// <summary>Reads and writes the big-endian header of a universal ("fat") Mach-O file.</summary>
internal static class FatFile
{
    public const int MaxSlices = 32;

    public static bool IsFat(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(data) is MachOImage.MagicFat or MachOImage.MagicFat64
        && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) is >= 1 and <= MaxSlices;

    public static List<FatSlice> ReadSlices(ReadOnlySpan<byte> data)
    {
        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (magic == MachOImage.MagicFat64)
            throw new InvalidDataException("Universal files with 64-bit offsets are not supported.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (count is 0 or > MaxSlices || data.Length < 8 + count * 20)
            throw new InvalidDataException("The universal file header is damaged.");
        var slices = new List<FatSlice>();
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(8 + i * 20, 20);
            var slice = new FatSlice(
                BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadUInt32BigEndian(e[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(e[8..]), BinaryPrimitives.ReadUInt32BigEndian(e[12..]),
                BinaryPrimitives.ReadUInt32BigEndian(e[16..]));
            if (slice.Offset < 8 + count * 20 || (ulong)slice.Offset + slice.Size > (ulong)data.Length || slice.Align > 20)
                throw new InvalidDataException("A slice of the universal file lies outside it.");
            slices.Add(slice);
        }
        var sorted = slices.OrderBy(s => s.Offset).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if ((ulong)sorted[i - 1].Offset + sorted[i - 1].Size > sorted[i].Offset)
                throw new InvalidDataException("Slices of the universal file overlap.");
        return slices;
    }

    /// <summary>Lays the slices out again (each on its own alignment) after signing changed their sizes.</summary>
    public static byte[] Build(IReadOnlyList<(FatSlice Header, byte[] Data)> slices)
    {
        long at = 8 + 20L * slices.Count;
        var offsets = new long[slices.Count];
        for (var i = 0; i < slices.Count; i++)
        {
            var align = 1L << (int)slices[i].Header.Align;
            at = (at + align - 1) / align * align;
            offsets[i] = at;
            at += slices[i].Data.Length;
        }
        if (at > uint.MaxValue)
            throw new InvalidDataException("The signed universal file would be larger than 4 GB.");

        var output = new byte[at];
        BinaryPrimitives.WriteUInt32BigEndian(output, MachOImage.MagicFat);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)slices.Count);
        for (var i = 0; i < slices.Count; i++)
        {
            var e = output.AsSpan(8 + i * 20, 20);
            var h = slices[i].Header;
            BinaryPrimitives.WriteUInt32BigEndian(e, h.CpuType);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], h.CpuSubtype);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)slices[i].Data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], h.Align);
            slices[i].Data.CopyTo(output.AsSpan((int)offsets[i]));
        }
        return output;
    }
}
