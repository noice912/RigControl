using System.IO.Compression;
using RigControl.iPhone.Plist;
using RigControl.iPhone.Signing;
using static RigControl.iPhone.Tests.Signing.SigningFixtures;

namespace RigControl.iPhone.Tests.Signing;

public class IpaSignerTests
{
    [Fact]
    public void Lists_the_app_then_its_extensions()
    {
        using var dir = new TempDir();
        var ipa = dir.File("in.ipa");
        WriteSampleIpa(ipa);

        var bundles = IpaInspector.ListBundles(ipa);

        Assert.Equal(2, bundles.Count);
        Assert.Equal(new BundleEntry("Payload/Sample.app", "com.example.sample", "Sample", false), bundles[0]);
        Assert.Equal(new BundleEntry("Payload/Sample.app/PlugIns/Widget.appex", "com.example.sample.widget", "Widget", true), bundles[1]);
    }

    [Fact]
    public async Task Signs_a_sample_ipa_inside_out()
    {
        using var dir = new TempDir();
        var input = dir.File("in.ipa");
        var output = dir.File("out.ipa");
        WriteSampleIpa(input);
        var profiles = new Dictionary<string, byte[]>
        {
            ["org.test.app"] = Profile("org.test.app"),
            ["org.test.app.widget"] = Profile("org.test.app.widget", wildcard: true),
        };
        var messages = new List<string>();

        await IpaSigner.SignAsync(new SignRequest(input, output, Certificate, "org.test.app", profiles),
            new SyncProgress(messages.Add));

        Assert.Contains("Signing Payload/Sample.app/Frameworks/Lib.framework/Lib", messages);
        var unpacked = Path.Combine(dir.Path, "unpacked");
        using (var zip = ZipFile.OpenRead(output))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName == "iTunesMetadata.plist");
            Assert.All(zip.Entries, e => Assert.StartsWith("Payload/", e.FullName));
            var exe = zip.GetEntry("Payload/Sample.app/Sample")!;
            Assert.Equal(0x81ED, (exe.ExternalAttributes >> 16) & 0xFFFF);
            Assert.Equal(0x81A4, (zip.GetEntry("Payload/Sample.app/Assets.car")!.ExternalAttributes >> 16) & 0xFFFF);
            zip.ExtractToDirectory(unpacked);
        }
        AssertMadeOnUnix(output);

        var app = Path.Combine(unpacked, "Payload", "Sample.app");
        var appex = Path.Combine(app, "PlugIns", "Widget.appex");
        var framework = Path.Combine(app, "Frameworks", "Lib.framework");

        // Bundle ids and profiles.
        Assert.Equal("org.test.app", PlistFile.ParseDict(File.ReadAllBytes(Path.Combine(app, "Info.plist"))).String("CFBundleIdentifier"));
        Assert.Equal("org.test.app.widget", PlistFile.ParseDict(File.ReadAllBytes(Path.Combine(appex, "Info.plist"))).String("CFBundleIdentifier"));
        Assert.Equal(profiles["org.test.app"], File.ReadAllBytes(Path.Combine(app, "embedded.mobileprovision")));
        Assert.Equal(profiles["org.test.app.widget"], File.ReadAllBytes(Path.Combine(appex, "embedded.mobileprovision")));
        Assert.Equal("com.example.lib", PlistFile.ParseDict(File.ReadAllBytes(Path.Combine(framework, "Info.plist"))).String("CFBundleIdentifier"));

        // Every Mach-O, inside-out.
        SignatureVerifier.VerifyFile(File.ReadAllBytes(Path.Combine(framework, "Lib")), new SignatureVerifier.Expect(
            "com.example.lib", TeamId, Certificate, File.ReadAllBytes(Path.Combine(framework, "Info.plist")),
            File.ReadAllBytes(Path.Combine(framework, "_CodeSignature", "CodeResources"))));
        Assert.Equal(2, SignatureVerifier.VerifyFile(File.ReadAllBytes(Path.Combine(app, "Frameworks", "libextra.dylib")),
            new SignatureVerifier.Expect("libextra", TeamId, Certificate)).Count);
        SignatureVerifier.VerifyFile(File.ReadAllBytes(Path.Combine(appex, "Widget")), new SignatureVerifier.Expect(
            "org.test.app.widget", TeamId, Certificate, File.ReadAllBytes(Path.Combine(appex, "Info.plist")),
            File.ReadAllBytes(Path.Combine(appex, "_CodeSignature", "CodeResources")),
            $"{TeamId}.org.test.app.widget", Executable: true));
        SignatureVerifier.VerifyFile(File.ReadAllBytes(Path.Combine(app, "Sample")), new SignatureVerifier.Expect(
            "org.test.app", TeamId, Certificate, File.ReadAllBytes(Path.Combine(app, "Info.plist")),
            File.ReadAllBytes(Path.Combine(app, "_CodeSignature", "CodeResources")),
            $"{TeamId}.org.test.app", Executable: true));

        // Seals.
        SignatureVerifier.VerifyCodeResources(framework, "Lib");
        SignatureVerifier.VerifyCodeResources(appex, "Widget");
        SignatureVerifier.VerifyCodeResources(app, "Sample");
        var seal = PlistFile.ParseDict(File.ReadAllBytes(Path.Combine(app, "_CodeSignature", "CodeResources")));
        var files2 = seal.Dict("files2")!;
        Assert.True(files2.Dict("en.lproj/Localizable.strings")!.Bool("optional"));
        Assert.Null(files2.Dict("Base.lproj/Main.storyboardc/Info.plist")!.Bool("optional"));
        Assert.True(files2.ContainsKey("PlugIns/Widget.appex/Widget"));
        Assert.True(files2.ContainsKey("PlugIns/Widget.appex/_CodeSignature/CodeResources"));
        Assert.True(files2.ContainsKey("embedded.mobileprovision"));
        Assert.False(files2.ContainsKey("PkgInfo"));
        Assert.True(seal.Dict("files")!.ContainsKey("Info.plist"));   // the old "files" list keeps Info.plist
    }

    [Fact]
    public async Task Refuses_a_profile_for_another_certificate_or_app()
    {
        using var dir = new TempDir();
        var input = dir.File("in.ipa");
        WriteSampleIpa(input);
        var other = Profile("org.test.app", developer: OtherCertificate());

        await Assert.ThrowsAsync<InvalidOperationException>(() => IpaSigner.SignAsync(new SignRequest(input,
            dir.File("a.ipa"), Certificate, "org.test.app",
            new Dictionary<string, byte[]> { ["org.test.app"] = other, ["org.test.app.widget"] = Profile("org.test.app.widget") })));

        await Assert.ThrowsAsync<InvalidDataException>(() => IpaSigner.SignAsync(new SignRequest(input,
            dir.File("b.ipa"), Certificate, "org.test.app",
            new Dictionary<string, byte[]> { ["org.test.app"] = Profile("org.other.app"), ["org.test.app.widget"] = Profile("org.test.app.widget") })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => IpaSigner.SignAsync(new SignRequest(input,
            dir.File("c.ipa"), Certificate, "org.test.app",
            new Dictionary<string, byte[]> { ["org.test.app"] = Profile("org.test.app") })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => IpaSigner.SignAsync(new SignRequest(input,
            dir.File("d.ipa"), Certificate, "org.test.app",
            new Dictionary<string, byte[]>
            {
                ["org.test.app"] = Profile("org.test.app", expires: DateTime.UtcNow.AddDays(-1)),
                ["org.test.app.widget"] = Profile("org.test.app.widget"),
            })));
        Assert.False(File.Exists(dir.File("a.ipa")));
    }

    [Theory]
    [InlineData("Payload/../evil.txt")]
    [InlineData("Payload/App.app/../../evil.txt")]
    [InlineData("/Payload/abs.txt")]
    [InlineData("Payload\\App.app\\win.txt")]
    [InlineData("C:/Payload/x.txt")]
    [InlineData("Payload/App.app/trailing.")]
    public void Refuses_unsafe_entry_names(string name)
    {
        using var dir = new TempDir();
        var ipa = dir.File("bad.ipa");
        using (var zip = ZipFile.Open(ipa, ZipArchiveMode.Create))
            using (var s = zip.CreateEntry(name).Open())
                s.Write("x"u8);
        Directory.CreateDirectory(dir.File("out"));
        Assert.Throws<InvalidDataException>(() => IpaArchive.Extract(ipa, dir.File("out"), default));
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.txt")));
    }

    [Fact]
    public void Refuses_symbolic_links_and_case_twins()
    {
        using var dir = new TempDir();
        var link = dir.File("link.ipa");
        using (var zip = ZipFile.Open(link, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("Payload/App.app/link");
            e.ExternalAttributes = 0xA1FF << 16;
            using var s = e.Open();
            s.Write("/etc/passwd"u8);
        }
        Directory.CreateDirectory(dir.File("o1"));
        Assert.Throws<InvalidDataException>(() => IpaArchive.Extract(link, dir.File("o1"), default));

        var twins = dir.File("twins.ipa");
        using (var zip = ZipFile.Open(twins, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("Payload/App.app/File").Open()) s.Write("a"u8);
            using (var s = zip.CreateEntry("Payload/App.app/file").Open()) s.Write("b"u8);
        }
        Directory.CreateDirectory(dir.File("o2"));
        Assert.Throws<InvalidDataException>(() => IpaArchive.Extract(twins, dir.File("o2"), default));
    }

    [Theory]
    [InlineData("com.old.app.widget", "com.old.app", "org.new", "org.new.widget")]
    [InlineData("com.old.app.share.ext", "com.old.app", "org.new", "org.new.share.ext")]
    [InlineData("com.elsewhere.widget", "com.old.app", "org.new", "org.new.widget")]
    public void Extension_ids_follow_the_new_main_id(string original, string main, string newMain, string expected) =>
        Assert.Equal(expected, IpaSigner.ExtensionId(original, main, newMain));

    [RealIpaFact]
    public void Real_app_lists_one_bundle()
    {
        var bundles = IpaInspector.ListBundles(RealIpa()!);
        var app = Assert.Single(bundles);
        Assert.Equal("Payload/RigControl.app", app.Path);
        Assert.Equal("RigControl.Mobile", app.Executable);
        Assert.False(app.IsExtension);
        Assert.False(string.IsNullOrEmpty(app.OriginalBundleId));
    }

    [RealIpaFact]
    public async Task Real_app_signs_and_verifies()
    {
        using var dir = new TempDir();
        var output = dir.File("signed.ipa");
        var request = new SignRequest(RealIpa()!, output, Certificate, "org.test.rigcontrol",
            new Dictionary<string, byte[]> { ["org.test.rigcontrol"] = Profile("org.test.rigcontrol") });

        await IpaSigner.SignAsync(request);

        var unpacked = Path.Combine(dir.Path, "unpacked");
        ZipFile.ExtractToDirectory(output, unpacked);
        var app = Path.Combine(unpacked, "Payload", "RigControl.app");
        var info = File.ReadAllBytes(Path.Combine(app, "Info.plist"));
        Assert.Equal("org.test.rigcontrol", PlistFile.ParseDict(info).String("CFBundleIdentifier"));
        SignatureVerifier.VerifyFile(File.ReadAllBytes(Path.Combine(app, "RigControl.Mobile")), new SignatureVerifier.Expect(
            "org.test.rigcontrol", TeamId, Certificate, info,
            File.ReadAllBytes(Path.Combine(app, "_CodeSignature", "CodeResources")),
            $"{TeamId}.org.test.rigcontrol", Executable: true));
        SignatureVerifier.VerifyCodeResources(app, "RigControl.Mobile");
        AssertMadeOnUnix(output);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 OtherCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Someone Else", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>Every central directory record says "made by Unix" so the file modes count.</summary>
    private static void AssertMadeOnUnix(string zipPath)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var eocd = bytes.Length - 22;   // no archive comment is written
        Assert.Equal(0x06054b50u, BitConverter.ToUInt32(bytes, eocd));
        int count = BitConverter.ToUInt16(bytes, eocd + 10), at = (int)BitConverter.ToUInt32(bytes, eocd + 16);
        Assert.True(count > 0);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0x02014b50u, BitConverter.ToUInt32(bytes, at));
            Assert.Equal(3, bytes[at + 5]);
            at += 46 + BitConverter.ToUInt16(bytes, at + 28) + BitConverter.ToUInt16(bytes, at + 30) + BitConverter.ToUInt16(bytes, at + 32);
        }
    }

    /// <summary>Progress that reports on the calling thread (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
