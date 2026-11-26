using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Signing;

/// <summary>
/// Re-reads signed output with its own parsing (none of the signer's code) and asserts what iOS checks: the
/// LC_CODE_SIGNATURE and __LINKEDIT agree, the superblob is well formed, both CodeDirectories hash every page and
/// every special slot correctly, the CMS signature verifies over the first CodeDirectory with the cdhash
/// attributes matching both, and the entitlements blobs say what was expected.
/// </summary>
internal static class SignatureVerifier
{
    internal sealed record Expect(
        string Identifier,
        string? TeamId,
        X509Certificate2 Certificate,
        byte[]? InfoPlist = null,
        byte[]? CodeResources = null,
        string? ApplicationIdentifier = null,
        bool Executable = false);

    internal sealed record Result(Dictionary<uint, byte[]> Blobs, byte[] CodeDirectory1, byte[] CodeDirectory2);

    public static List<Result> VerifyFile(byte[] file, Expect expect)
    {
        var results = new List<Result>();
        if (BinaryPrimitives.ReadUInt32BigEndian(file) == 0xcafebabe)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4));
            for (var i = 0; i < n; i++)
            {
                var e = file.AsSpan(8 + i * 20);
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
                var align = (int)BinaryPrimitives.ReadUInt32BigEndian(e[16..]);
                Assert.Equal(0, offset % (1 << align));
                results.Add(VerifyImage(file.AsSpan(offset, size).ToArray(), expect));
            }
        }
        else
        {
            results.Add(VerifyImage(file, expect));
        }
        return results;
    }

    private static Result VerifyImage(byte[] image, Expect expect)
    {
        Assert.Equal(0xfeedfacfu, BinaryPrimitives.ReadUInt32LittleEndian(image));
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16));
        var fileType = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12));
        int at = 32, sigCount = 0;
        uint dataOff = 0, dataSize = 0;
        ulong leFileOff = 0, leFileSize = 0, leVmSize = 0, textOff = 0, textSize = 0;
        for (var i = 0; i < ncmds; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 4));
            if (cmd == 0x19)
            {
                var name = Encoding.ASCII.GetString(image, at + 8, 16).TrimEnd('\0');
                if (name == "__LINKEDIT")
                {
                    leVmSize = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 32));
                    leFileOff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 40));
                    leFileSize = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 48));
                }
                if (name == "__TEXT")
                {
                    textOff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 40));
                    textSize = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 48));
                }
            }
            if (cmd == 0x1d)
            {
                sigCount++;
                Assert.Equal(16u, size);
                dataOff = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 8));
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 12));
            }
            at += (int)size;
        }
        Assert.Equal(1, sigCount);
        Assert.Equal(0u, dataOff % 16);
        Assert.Equal((long)image.Length, (long)dataOff + dataSize);
        Assert.Equal(leFileOff + leFileSize, (ulong)image.Length);
        Assert.True(leVmSize >= leFileSize && leVmSize % 0x4000 == 0, "__LINKEDIT vmsize must cover the file size, page aligned");

        // Superblob.
        var sig = image.AsSpan((int)dataOff, (int)dataSize);
        Assert.Equal(0xfade0cc0u, BinaryPrimitives.ReadUInt32BigEndian(sig));
        var total = BinaryPrimitives.ReadUInt32BigEndian(sig[4..]);
        Assert.True(total <= dataSize);
        var count = BinaryPrimitives.ReadUInt32BigEndian(sig[8..]);
        var blobs = new Dictionary<uint, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(sig[(12 + i * 8)..]);
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(sig[(16 + i * 8)..]);
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(sig[(off + 4)..]);
            Assert.True(off + len <= total, "blob inside the superblob");
            blobs.Add(type, sig.Slice(off, len).ToArray());
        }
        Assert.Contains(0u, blobs.Keys);
        Assert.Contains(2u, blobs.Keys);
        Assert.Contains(0x1000u, blobs.Keys);
        Assert.Contains(0x10000u, blobs.Keys);
        Assert.Equal(0xfade0c01u, BinaryPrimitives.ReadUInt32BigEndian(blobs[2]));

        var code = image.AsSpan(0, (int)dataOff);
        var execFlags = fileType == 2 ? 1ul : 0ul;
        if (expect.ApplicationIdentifier != null && fileType == 2)
            execFlags |= 0x10;   // the test profiles all have get-task-allow
        CheckCodeDirectory(blobs[0], 1, 20, code, blobs, expect, textOff, textSize, execFlags);
        CheckCodeDirectory(blobs[0x1000], 2, 32, code, blobs, expect, textOff, textSize, execFlags);

        // Entitlements.
        if (expect.ApplicationIdentifier != null)
        {
            Assert.Equal(0xfade7171u, BinaryPrimitives.ReadUInt32BigEndian(blobs[5]));
            var ents = PlistFile.ParseDict(blobs[5].AsSpan(8));
            Assert.Equal(expect.ApplicationIdentifier, ents.String("application-identifier"));
            if (expect.Executable)
            {
                Assert.Equal(0xfade7172u, BinaryPrimitives.ReadUInt32BigEndian(blobs[7]));
                Assert.Equal(expect.ApplicationIdentifier, ReadDerEntitlements(blobs[7].AsSpan(8).ToArray())["application-identifier"]);
            }
        }
        else
        {
            Assert.DoesNotContain(5u, blobs.Keys);
            Assert.DoesNotContain(7u, blobs.Keys);
        }

        CheckCms(blobs[0x10000], blobs[0], blobs[0x1000], expect.Certificate);
        return new Result(blobs, blobs[0], blobs[0x1000]);
    }

    private static void CheckCodeDirectory(byte[] cd, byte hashType, int hashSize, ReadOnlySpan<byte> code,
        Dictionary<uint, byte[]> blobs, Expect expect, ulong textOff, ulong textSize, ulong execFlags)
    {
        uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(o));
        ulong U64(int o) => BinaryPrimitives.ReadUInt64BigEndian(cd.AsSpan(o));
        string Str(int o) => Encoding.UTF8.GetString(cd, o, Array.IndexOf(cd, (byte)0, o) - o);
        byte[] H(ReadOnlySpan<byte> d) => hashType == 1 ? SHA1.HashData(d) : SHA256.HashData(d);

        Assert.Equal(0xfade0c02u, U32(0));
        Assert.Equal((uint)cd.Length, U32(4));
        Assert.Equal(0x20400u, U32(8));
        var hashOffset = (int)U32(16);
        var nSpecial = (int)U32(24);
        var nCode = (int)U32(28);
        Assert.Equal((uint)code.Length, U32(32));
        Assert.Equal(hashSize, cd[36]);
        Assert.Equal(hashType, cd[37]);
        Assert.Equal(12, cd[39]);
        Assert.Equal(expect.Identifier, Str((int)U32(20)));
        var teamOffset = (int)U32(48);
        Assert.Equal(expect.TeamId, teamOffset == 0 ? null : Str(teamOffset));
        Assert.Equal(textOff, U64(64));
        Assert.Equal(textSize, U64(72));
        Assert.Equal(execFlags, U64(80));

        Assert.Equal((code.Length + 4095) / 4096, nCode);
        for (var i = 0; i < nCode; i++)
        {
            var page = code.Slice(i * 4096, Math.Min(4096, code.Length - i * 4096));
            Assert.True(H(page).AsSpan().SequenceEqual(cd.AsSpan(hashOffset + i * hashSize, hashSize)), $"page {i} hash");
        }

        byte[] Slot(int n) => n > nSpecial ? new byte[hashSize] : cd.AsSpan(hashOffset - n * hashSize, hashSize).ToArray();
        byte[] Zero() => new byte[hashSize];
        Assert.Equal(expect.InfoPlist == null ? Zero() : H(expect.InfoPlist), Slot(1));
        Assert.Equal(H(blobs[2]), Slot(2));
        Assert.Equal(expect.CodeResources == null ? Zero() : H(expect.CodeResources), Slot(3));
        Assert.Equal(blobs.TryGetValue(5, out var e) ? H(e) : Zero(), Slot(5));
        Assert.Equal(blobs.TryGetValue(7, out var d) ? H(d) : Zero(), Slot(7));
    }

    private static void CheckCms(byte[] wrapper, byte[] cd1, byte[] cd2, X509Certificate2 certificate)
    {
        Assert.Equal(0xfade0b01u, BinaryPrimitives.ReadUInt32BigEndian(wrapper));
        var cms = new SignedCms(new ContentInfo(cd1), detached: true);
        cms.Decode(wrapper.AsSpan(8).ToArray());
        cms.CheckSignature(verifySignatureOnly: true);
        var signer = Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
        Assert.Equal(certificate.Thumbprint, signer.Certificate!.Thumbprint);
        Assert.Equal("2.16.840.1.101.3.4.2.1", signer.DigestAlgorithm.Value);

        var plistAttr = signer.SignedAttributes.Cast<CryptographicAttributeObject>()
            .Single(a => a.Oid.Value == "1.2.840.113635.100.9.1");
        var plist = PlistFile.ParseDict(new AsnReader(plistAttr.Values[0].RawData, AsnEncodingRules.DER).ReadOctetString());
        var hashes = plist.Array("cdhashes")!.Cast<byte[]>().ToList();
        Assert.Equal(2, hashes.Count);
        Assert.Equal(SHA1.HashData(cd1), hashes[0]);
        Assert.Equal(SHA256.HashData(cd2)[..20], hashes[1]);

        var agility = signer.SignedAttributes.Cast<CryptographicAttributeObject>()
            .Single(a => a.Oid.Value == "1.2.840.113635.100.9.2");
        var found = new Dictionary<string, byte[]>();
        foreach (var value in agility.Values)
        {
            var seq = new AsnReader(value.RawData, AsnEncodingRules.DER).ReadSequence();
            found[seq.ReadObjectIdentifier()] = seq.ReadOctetString();
        }
        Assert.Equal(SHA256.HashData(cd2), found["2.16.840.1.101.3.4.2.1"]);
        Assert.Equal(SHA1.HashData(cd1), found["1.3.14.3.2.26"]);
    }

    /// <summary>Reads Apple's DER entitlements back into simple .NET values (dict → Dictionary, array → List).</summary>
    public static Dictionary<string, object> ReadDerEntitlements(byte[] der)
    {
        var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.Application, 16, true));
        Assert.True(outer.ReadInteger() == 1);
        return (Dictionary<string, object>)ReadDer(outer);
    }

    private static object ReadDer(AsnReader r)
    {
        var tag = r.PeekTag();
        if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 16)
        {
            var dict = new Dictionary<string, object>();
            var set = r.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 16, true));
            string? previous = null;
            while (set.HasData)
            {
                var pair = set.ReadSequence();
                var key = pair.ReadCharacterString(UniversalTagNumber.UTF8String);
                Assert.True(previous == null || string.CompareOrdinal(previous, key) < 0, "DER keys sorted");
                previous = key;
                dict[key] = ReadDer(pair);
            }
            return dict;
        }
        if (tag.HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var list = new List<object>();
            var seq = r.ReadSequence();
            while (seq.HasData)
                list.Add(ReadDer(seq));
            return list;
        }
        if (tag.HasSameClassAndValue(Asn1Tag.Boolean))
            return r.ReadBoolean();
        if (tag.HasSameClassAndValue(Asn1Tag.Integer))
            return (long)r.ReadInteger();
        return r.ReadCharacterString(UniversalTagNumber.UTF8String);
    }

    /// <summary>
    /// Checks a bundle's CodeResources against the files on disk: every files2 hash and hash2 matches, and every
    /// file iOS would expect sealed (all but the main executable, _CodeSignature/, Info.plist and PkgInfo) is there.
    /// </summary>
    public static void VerifyCodeResources(string bundleDir, string mainExecutable)
    {
        var plist = PlistFile.ParseDict(File.ReadAllBytes(Path.Combine(bundleDir, "_CodeSignature", "CodeResources")));
        var files2 = plist.Dict("files2")!;
        var files = plist.Dict("files")!;
        Assert.NotNull(plist.Dict("rules"));
        Assert.NotNull(plist.Dict("rules2"));
        foreach (var (rel, value) in files2)
        {
            var bytes = File.ReadAllBytes(Path.Combine(bundleDir, rel));
            var entry = (PlistDict)value;
            Assert.Equal(SHA1.HashData(bytes), entry.Data("hash"));
            Assert.Equal(SHA256.HashData(bytes), entry.Data("hash2"));
        }
        foreach (var (rel, value) in files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(bundleDir, rel));
            var hash = value as byte[] ?? ((PlistDict)value).Data("hash");
            Assert.Equal(SHA1.HashData(bytes), hash);
        }
        var onDisk = Directory.EnumerateFiles(bundleDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(bundleDir, f).Replace('\\', '/'))
            .Where(f => f != mainExecutable && !f.StartsWith("_CodeSignature/", StringComparison.Ordinal)
                && f is not ("Info.plist" or "PkgInfo"))
            .ToList();
        foreach (var f in onDisk)
            Assert.True(files2.ContainsKey(f), $"{f} is sealed in files2");
        Assert.False(files2.ContainsKey(mainExecutable));
        Assert.False(files2.ContainsKey("Info.plist"));
    }
}
