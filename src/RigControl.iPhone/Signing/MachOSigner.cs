using System.Buffers.Binary;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>What one Mach-O file is signed with, besides the certificate.</summary>
internal sealed record CodeSignOptions(
    string Identifier,
    string? TeamId,
    string? LeafCommonName,
    // The bundle's Info.plist and _CodeSignature/CodeResources as written, for bundle main executables only.
    byte[]? InfoPlist,
    byte[]? CodeResources,
    // Entitlements for app and extension executables; null for frameworks and libraries.
    PlistDict? Entitlements);

/// <summary>
/// Signs a Mach-O file in place in memory: a thin arm64 image or every slice of a universal file. Each image gets
/// (or reuses) an LC_CODE_SIGNATURE command pointing at a new embedded signature appended to __LINKEDIT, whose
/// segment sizes grow to cover it. The code pages are hashed after the header is final, so the hashes cover the
/// updated load commands.
/// </summary>
internal sealed class MachOSigner(CmsSignature cms)
{
    private const int SignatureAlignment = 16;
    private const ulong SegmentAlignment = 0x4000;

    public byte[] Sign(byte[] file, CodeSignOptions options)
    {
        if (FatFile.IsFat(file))
        {
            var slices = FatFile.ReadSlices(file);
            var signed = new List<(FatSlice, byte[])>();
            foreach (var slice in slices)
                signed.Add((slice, SignImage(file.AsSpan((int)slice.Offset, (int)slice.Size), options)));
            return FatFile.Build(signed);
        }
        return SignImage(file, options);
    }

    private byte[] SignImage(ReadOnlySpan<byte> input, CodeSignOptions options)
    {
        var image = MachOImage.Parse(input);
        var linkedit = image.Segment("__LINKEDIT")
            ?? throw new InvalidDataException("The Mach-O has no __LINKEDIT segment, so it can't hold a signature.");
        var linkeditEnd = linkedit.FileOff + linkedit.FileSize;
        foreach (var seg in image.Segments)
            if (seg != linkedit && seg.FileSize > 0 && seg.FileOff + seg.FileSize > linkedit.FileOff)
                throw new InvalidDataException("__LINKEDIT is not the last segment of the Mach-O.");

        // Where the code ends: at the old signature if there is one, otherwise at the end of __LINKEDIT.
        ulong codeEnd;
        var addCommand = image.CodeSignatureCommandOffset < 0;
        if (!addCommand)
        {
            if (image.CodeSignatureOffset < linkedit.FileOff || image.CodeSignatureOffset > linkeditEnd)
                throw new InvalidDataException("The existing code signature is not inside __LINKEDIT.");
            codeEnd = image.CodeSignatureOffset;
        }
        else
        {
            if (linkeditEnd != (ulong)input.Length)
                throw new InvalidDataException("The Mach-O has data after __LINKEDIT; signing would cut it off.");
            var commandsEnd = (ulong)MachOImage.HeaderSize + image.CommandsSize;
            if (commandsEnd + 16 > image.FirstContentOffset)
                throw new InvalidDataException(
                    "The Mach-O has no LC_CODE_SIGNATURE and no room after its load commands to add one. " +
                    "Rebuild it with linker header padding (for example -headerpad_max_install_names).");
            codeEnd = linkeditEnd;
        }

        var signatureOffset = Align(codeEnd, SignatureAlignment);
        if (signatureOffset > int.MaxValue / 2)
            throw new InvalidDataException("The Mach-O is too large to sign.");

        // Blobs that don't depend on the code.
        var requirements = Requirements.Designated(options.Identifier, options.LeafCommonName);
        byte[]? entitlements = null, entitlementsDer = null;
        var isExecutable = image.FileType == MachOImage.FileTypeExecute;
        if (options.Entitlements != null)
        {
            entitlements = CsMagic.Wrap(CsMagic.Entitlements, PlistFile.ToXml(SortedCopy(options.Entitlements)));
            // codesign writes the DER form only for executables.
            if (isExecutable)
                entitlementsDer = EntitlementsDer.Blob(options.Entitlements);
        }

        var special = new byte[]?[8];
        special[CsMagic.SlotInfo] = options.InfoPlist;
        special[CsMagic.SlotRequirements] = requirements;
        special[CsMagic.SlotResources] = options.CodeResources;
        special[CsMagic.SlotEntitlements] = entitlements;
        special[CsMagic.SlotEntitlementsDer] = entitlementsDer;

        var text = image.Segment("__TEXT");
        ulong execFlags = 0;
        if (isExecutable)
        {
            execFlags |= CsMagic.ExecSegMainBinary;
            if (options.Entitlements?.Bool("get-task-allow") == true)
                execFlags |= CsMagic.ExecSegAllowUnsigned;
        }
        var cdInput = new CodeDirectory.Input(options.Identifier, options.TeamId, special,
            text?.FileOff ?? 0, text?.FileSize ?? 0, execFlags);

        // Reserve the signature's space first: its size goes into the header, which is part of the hashed code.
        var blobCount = 4 + (entitlements != null ? 1 : 0) + (entitlementsDer != null ? 1 : 0);
        var reserved = 12 + 8 * blobCount
            + CodeDirectory.Length(cdInput, CsMagic.HashSha1, (long)signatureOffset)
            + CodeDirectory.Length(cdInput, CsMagic.HashSha256, (long)signatureOffset)
            + requirements.Length + (entitlements?.Length ?? 0) + (entitlementsDer?.Length ?? 0)
            + cms.SizeEstimate;
        var signatureSize = (int)Align((ulong)reserved, SignatureAlignment);

        var output = new byte[(long)signatureOffset + signatureSize];
        input[..(int)codeEnd].CopyTo(output);

        // Header: the code signature command, then __LINKEDIT's sizes.
        var cmdAt = image.CodeSignatureCommandOffset;
        if (addCommand)
        {
            cmdAt = MachOImage.HeaderSize + (int)image.CommandsSize;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), image.CommandCount + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20), image.CommandsSize + 16);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cmdAt), MachOImage.LcCodeSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cmdAt + 4), 16);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cmdAt + 8), (uint)signatureOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cmdAt + 12), (uint)signatureSize);

        var newFileSize = signatureOffset + (ulong)signatureSize - linkedit.FileOff;
        var newVmSize = Math.Max(linkedit.VmSize, Align(newFileSize, SegmentAlignment));
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(linkedit.CommandOffset + 32), newVmSize);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(linkedit.CommandOffset + 48), newFileSize);

        var code = output.AsSpan(0, (int)signatureOffset);
        var cd1 = CodeDirectory.Build(cdInput, CsMagic.HashSha1, code);
        var cd2 = CodeDirectory.Build(cdInput, CsMagic.HashSha256, code);
        var signature = cms.Sign([(CsMagic.HashSha1, cd1), (CsMagic.HashSha256, cd2)]);

        var blobs = new List<(uint Slot, byte[] Data)> { (CsMagic.SlotCodeDirectory, cd1), (CsMagic.SlotRequirements, requirements) };
        if (entitlements != null)
            blobs.Add((CsMagic.SlotEntitlements, entitlements));
        if (entitlementsDer != null)
            blobs.Add((CsMagic.SlotEntitlementsDer, entitlementsDer));
        blobs.Add((CsMagic.SlotAlternateCodeDirectory, cd2));
        blobs.Add((CsMagic.SlotSignature, signature));
        var superBlob = SuperBlob(blobs);
        if (superBlob.Length > signatureSize)
            throw new InvalidOperationException("The signature came out larger than the space reserved for it.");
        superBlob.CopyTo(output.AsSpan((int)signatureOffset));
        return output;
    }

    internal static byte[] SuperBlob(IReadOnlyList<(uint Slot, byte[] Data)> blobs)
    {
        var w = new BlobWriter();
        var headerSize = 12 + 8 * blobs.Count;
        var total = headerSize + blobs.Sum(b => b.Data.Length);
        w.U32(CsMagic.EmbeddedSignature);
        w.U32((uint)total);
        w.U32((uint)blobs.Count);
        var offset = headerSize;
        foreach (var (slot, data) in blobs)
        {
            w.U32(slot);
            w.U32((uint)offset);
            offset += data.Length;
        }
        foreach (var (_, data) in blobs)
            w.Bytes(data);
        return w.ToArray();
    }

    /// <summary>A copy with keys sorted at every level, the order Apple's tools write property lists in.</summary>
    internal static PlistDict SortedCopy(PlistDict dict)
    {
        var copy = new PlistDict();
        foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
            copy[key] = dict[key] switch
            {
                PlistDict d => SortedCopy(d),
                List<object> l => l.Select(v => v is PlistDict d2 ? SortedCopy(d2) : v).ToList(),
                var v => v,
            };
        return copy;
    }

    private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;
}
