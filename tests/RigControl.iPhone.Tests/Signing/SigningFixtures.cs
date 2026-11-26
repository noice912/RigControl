using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Signing;

/// <summary>Made-up certificates, profiles, Mach-O files and .ipa files for the signing tests. No network.</summary>
internal static class SigningFixtures
{
    public const string TeamId = "TEAMID1234";

    private static readonly Lazy<X509Certificate2> Developer = new(() => SelfSigned(
        $"CN=Apple Development: Test Person (ABCDE12345), OU={TeamId}, O=Test Person, C=US"));

    private static readonly Lazy<X509Certificate2> ProfileSigner = new(() => SelfSigned("CN=Test Profile Signer"));

    /// <summary>A test "development certificate" (RSA 2048, private key in memory).</summary>
    public static X509Certificate2 Certificate => Developer.Value;

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // A PFX round trip gives a key every platform's CMS code can use.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>A profile shaped like Apple's: CMS SignedData around an XML plist.</summary>
    public static byte[] Profile(string bundleId, X509Certificate2? developer = null, bool wildcard = false,
        DateTime? expires = null)
    {
        var plist = new PlistDict
        {
            ["AppIDName"] = "Test App",
            ["ApplicationIdentifierPrefix"] = new List<object> { TeamId },
            ["CreationDate"] = DateTime.UtcNow.AddDays(-1),
            ["DeveloperCertificates"] = new List<object> { (developer ?? Certificate).RawData },
            ["Entitlements"] = new PlistDict
            {
                ["application-identifier"] = wildcard ? $"{TeamId}.*" : $"{TeamId}.{bundleId}",
                ["com.apple.developer.team-identifier"] = TeamId,
                ["get-task-allow"] = true,
                ["keychain-access-groups"] = new List<object> { $"{TeamId}.*" },
            },
            ["ExpirationDate"] = expires ?? DateTime.UtcNow.AddDays(7),
            ["Name"] = "Test Profile " + bundleId,
            ["ProvisionedDevices"] = new List<object> { "00008110-000000000000001E" },
            ["TeamIdentifier"] = new List<object> { TeamId },
            ["TeamName"] = "Test Person",
            ["TimeToLive"] = 7L,
            ["UUID"] = Guid.NewGuid().ToString(),
            ["Version"] = 1L,
        };
        var cms = new SignedCms(new ContentInfo(PlistFile.ToXml(plist)), detached: false);
        cms.ComputeSignature(new CmsSigner(ProfileSigner.Value), silent: true);
        return cms.Encode();
    }

    /// <summary>
    /// A small arm64 Mach-O: __TEXT (one 0x4000 page holding a __text section), optional __DATA, and __LINKEDIT.
    /// <paramref name="headerRoom"/> false puts the first section right after the load commands.
    /// </summary>
    public static byte[] MachO(uint fileType = 2, bool headerRoom = true, int linkeditSize = 0x230, int seed = 1,
        uint cpuSubtype = 0)
    {
        const int textSize = 0x4000;
        var commands = new List<byte[]>();
        int commandsSize = 72 + 80 + 72;   // __TEXT with one section, __LINKEDIT
        var sectionOffset = headerRoom ? 0x400 : 32 + commandsSize;

        commands.Add(Segment("__TEXT", 0x100000000, textSize, 0, textSize, 5, ("__text", (uint)sectionOffset, 0x200)));
        commands.Add(Segment("__LINKEDIT", 0x100000000 + textSize, 0x4000, textSize, (ulong)linkeditSize, 1));

        var file = new byte[textSize + linkeditSize];
        new Random(seed).NextBytes(file.AsSpan(sectionOffset));
        var h = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 0x0100000c);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], cpuSubtype);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], fileType);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)commands.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)commandsSize);
        var at = 32;
        foreach (var c in commands)
        {
            c.CopyTo(file, at);
            at += c.Length;
        }
        return file;
    }

    private static byte[] Segment(string name, ulong vmAddr, ulong vmSize, ulong fileOff, ulong fileSize, uint prot,
        params (string Name, uint Offset, ulong Size)[] sections)
    {
        var cmd = new byte[72 + 80 * sections.Length];
        var s = cmd.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x19);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)cmd.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(s[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], vmAddr);
        BinaryPrimitives.WriteUInt64LittleEndian(s[32..], vmSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s[40..], fileOff);
        BinaryPrimitives.WriteUInt64LittleEndian(s[48..], fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], prot);
        BinaryPrimitives.WriteUInt32LittleEndian(s[60..], prot);
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], (uint)sections.Length);
        for (var i = 0; i < sections.Length; i++)
        {
            var sect = s.Slice(72 + i * 80, 80);
            Encoding.ASCII.GetBytes(sections[i].Name).CopyTo(sect);
            Encoding.ASCII.GetBytes(name).CopyTo(sect[16..]);
            BinaryPrimitives.WriteUInt64LittleEndian(sect[32..], vmAddr + sections[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(sect[40..], sections[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(sect[48..], sections[i].Offset);
        }
        return cmd;
    }

    /// <summary>A universal file holding the given thin images, each on a 16 KB boundary.</summary>
    public static byte[] Fat(params byte[][] images)
    {
        var offsets = new List<int>();
        var at = 0x4000;
        foreach (var image in images)
        {
            offsets.Add(at);
            at = (at + image.Length + 0x3fff) / 0x4000 * 0x4000;
        }
        var file = new byte[offsets[^1] + images[^1].Length];
        BinaryPrimitives.WriteUInt32BigEndian(file, 0xcafebabe);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)images.Length);
        for (var i = 0; i < images.Length; i++)
        {
            var e = file.AsSpan(8 + i * 20);
            BinaryPrimitives.WriteUInt32BigEndian(e, BinaryPrimitives.ReadUInt32LittleEndian(images[i].AsSpan(4)));
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], BinaryPrimitives.ReadUInt32LittleEndian(images[i].AsSpan(8)));
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)images[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], 14);
            images[i].CopyTo(file, offsets[i]);
        }
        return file;
    }

    public static byte[] InfoPlist(string bundleId, string executable, string packageType = "APPL") =>
        PlistFile.ToXml(new PlistDict
        {
            ["CFBundleExecutable"] = executable,
            ["CFBundleIdentifier"] = bundleId,
            ["CFBundlePackageType"] = packageType,
            ["CFBundleShortVersionString"] = "1.0",
            ["UIDeviceFamily"] = new List<object> { 1L, 2L },
        });

    /// <summary>
    /// An .ipa with an app, an embedded framework, a loose universal dylib, an app extension, localized
    /// resources, and a file outside Payload/ that must be left out.
    /// </summary>
    public static void WriteSampleIpa(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, byte[] data, int mode = 0x1A4)
        {
            var e = zip.CreateEntry(name);
            e.ExternalAttributes = (0x8000 | mode) << 16;
            using var s = e.Open();
            s.Write(data);
        }
        const string app = "Payload/Sample.app/";
        Add(app + "Info.plist", InfoPlist("com.example.sample", "Sample"));
        Add(app + "Sample", MachO(seed: 1), 0x1ED);
        Add(app + "PkgInfo", "APPL????"u8.ToArray());
        Add(app + "Assets.car", Encoding.ASCII.GetBytes(new string('a', 5000)));
        Add(app + "en.lproj/Localizable.strings", "\"a\" = \"b\";"u8.ToArray());
        Add(app + "Base.lproj/Main.storyboardc/Info.plist", "x"u8.ToArray());
        Add(app + "Frameworks/Lib.framework/Info.plist", InfoPlist("com.example.lib", "Lib", "FMWK"));
        Add(app + "Frameworks/Lib.framework/Lib", MachO(fileType: 6, seed: 2), 0x1ED);
        Add(app + "Frameworks/libextra.dylib", Fat(MachO(fileType: 6, seed: 3), MachO(fileType: 6, seed: 4, cpuSubtype: 2)), 0x1ED);
        Add(app + "PlugIns/Widget.appex/Info.plist", InfoPlist("com.example.sample.widget", "Widget", "XPC!"));
        Add(app + "PlugIns/Widget.appex/Widget", MachO(seed: 5), 0x1ED);
        Add("iTunesMetadata.plist", "ignored"u8.ToArray());
    }

    /// <summary>The unsigned MAUI build of the phone app, when it's on this machine (never committed).</summary>
    public static string? RealIpa()
    {
        var fromEnv = Environment.GetEnvironmentVariable("RIGCONTROL_TEST_IPA");
        if (!string.IsNullOrEmpty(fromEnv))
            return File.Exists(fromEnv) ? fromEnv : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "mobile-builds", "RigControl-Mobile.ipa");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>A fact that is skipped when the real phone-app .ipa isn't on this machine.</summary>
public sealed class RealIpaFactAttribute : FactAttribute
{
    public RealIpaFactAttribute()
    {
        if (SigningFixtures.RealIpa() == null)
            Skip = "The phone app's .ipa (mobile-builds/RigControl-Mobile.ipa or RIGCONTROL_TEST_IPA) is not here.";
    }
}

/// <summary>A temporary folder deleted at the end of a test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-sign-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
