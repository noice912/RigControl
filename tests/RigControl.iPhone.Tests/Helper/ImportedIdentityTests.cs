using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Helper;
using RigControl.iPhone.Signing;
using RigControl.iPhone.Tests.Apple;
using RigControl.iPhone.Tests.Signing;
using static RigControl.iPhone.Tests.Signing.SigningFixtures;

namespace RigControl.iPhone.Tests.Helper;

public class ImportedIdentityTests
{
    private static byte[] P12(X509Certificate2 certificate, string password) =>
        certificate.Export(X509ContentType.Pkcs12, password);

    [Fact]
    public void Import_stores_the_key_protected_and_reads_the_profile()
    {
        using var dir = new TempDir();
        var protector = new FakeProtector();
        var store = new ImportedIdentityStore(dir.File("imported"), protector);
        // Whole seconds (plist dates have no fractions), before the test certificate's own end (30 days).
        var now = DateTime.UtcNow;
        var expires = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddDays(6);
        var profile = Profile("org.test.app", wildcard: true, expires: expires);
        var password = "pa55word".ToCharArray();

        var info = store.Import(P12(Certificate, "pa55word"), password, profile);

        Assert.All(password, c => Assert.Equal('\0', c));
        Assert.Equal(1, protector.Protects);
        Assert.Equal(TeamId, info.TeamId);
        Assert.Equal(new DateTimeOffset(expires), info.ProfileExpires);
        Assert.Equal(info.ProfileExpires, info.Expires); // the test certificate runs 30 days, the profile ends first
        Assert.Equal($"{TeamId}.*", info.AppId);
        Assert.True(info.Covers("00008110-000000000000001E"));
        Assert.False(info.Covers("00008110-0000000000000FFF"));

        // On disk: the key only in protected form, never the .p12 as given or the password.
        var sealedKey = File.ReadAllBytes(Path.Combine(dir.File("imported"), "certificate.p12.protected"));
        Assert.StartsWith("FAKEPROT", System.Text.Encoding.ASCII.GetString(sealedKey, 0, 8));
        Assert.Equal(-1, IndexOf(sealedKey, "pa55word"u8.ToArray()));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        using (loaded.Value.Certificate)
        {
            Assert.True(loaded.Value.Certificate.HasPrivateKey);
            Assert.Equal(Certificate.Thumbprint, loaded.Value.Certificate.Thumbprint);
        }
        Assert.Equal(profile, loaded.Value.Profile);
        var again = store.Info()!;
        Assert.Equal((info.TeamId, info.AppId, info.Expires), (again.TeamId, again.AppId, again.Expires));
        Assert.Equal(info.Devices, again.Devices);

        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void A_wrong_password_stores_nothing()
    {
        using var dir = new TempDir();
        var store = new ImportedIdentityStore(dir.File("imported"), new FakeProtector());
        var password = "wrong".ToCharArray();
        var e = Assert.Throws<InvalidDataException>(() => store.Import(P12(Certificate, "right"), password, Profile("org.test.app")));
        Assert.Contains("password is wrong", e.Message);
        Assert.All(password, c => Assert.Equal('\0', c));
        Assert.Null(store.Load());
    }

    [Fact]
    public void A_profile_for_another_certificate_is_refused()
    {
        using var dir = new TempDir();
        var store = new ImportedIdentityStore(dir.File("imported"), new FakeProtector());
        using var other = OtherCertificate();
        var e = Assert.Throws<InvalidDataException>(() =>
            store.Import(P12(Certificate, "x"), "x".ToCharArray(), Profile("org.test.app", developer: other)));
        Assert.Contains("wasn't made for this certificate", e.Message);
    }

    [Fact]
    public void An_expired_profile_is_refused()
    {
        using var dir = new TempDir();
        var store = new ImportedIdentityStore(dir.File("imported"), new FakeProtector());
        var e = Assert.Throws<InvalidDataException>(() => store.Import(P12(Certificate, "x"), "x".ToCharArray(),
            Profile("org.test.app", expires: DateTime.UtcNow.AddDays(-1))));
        Assert.Contains("expired", e.Message);
    }

    [Fact]
    public void Profile_expiry_is_parsed_from_the_profile()
    {
        var expires = new DateTime(2026, 12, 3, 4, 5, 6, DateTimeKind.Utc);
        Assert.Equal(new DateTimeOffset(expires), ImportedIdentityStore.ProfileExpiry(Profile("org.x", expires: expires)));
        Assert.Null(ImportedIdentityStore.ProfileExpiry([1, 2, 3]));
    }

    [Fact]
    public void Bundle_id_follows_the_profiles_app_id()
    {
        ImportedIdentityInfo Info(string appId) => new("n", TeamId, "p", appId, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, [], true);
        Assert.Equal("io.github.rigcontrol.mobile", ImportedIdentityStore.BundleIdFor(Info($"{TeamId}.*"), "io.github.rigcontrol.mobile"));
        Assert.Equal("io.github.rigcontrol.mobile", ImportedIdentityStore.BundleIdFor(Info($"{TeamId}.io.github.*"), "io.github.rigcontrol.mobile"));
        Assert.Equal("com.me.phone", ImportedIdentityStore.BundleIdFor(Info($"{TeamId}.com.me.phone"), "io.github.rigcontrol.mobile"));
        Assert.Throws<InvalidDataException>(() => ImportedIdentityStore.BundleIdFor(Info($"{TeamId}.com.other.*"), "io.github.rigcontrol.mobile"));
    }

    [Fact]
    public async Task An_imported_identity_re_signs_an_ipa()
    {
        using var dir = new TempDir();
        var store = new ImportedIdentityStore(dir.File("imported"), new FakeProtector());
        var profile = Profile("org.test.app", wildcard: true);
        store.Import(P12(Certificate, "x"), "x".ToCharArray(), profile);
        var input = dir.File("in.ipa");
        var output = dir.File("out.ipa");
        WriteSampleIpa(input);

        var loaded = store.Load()!.Value;
        using var certificate = loaded.Certificate;
        var info = store.Info()!;
        var bundles = IpaInspector.ListBundles(input);
        var mainId = ImportedIdentityStore.BundleIdFor(info, bundles[0].OriginalBundleId);
        var profiles = bundles.ToDictionary(b => b.OriginalBundleId, _ => loaded.Profile);
        await IpaSigner.SignAsync(new SignRequest(input, output, certificate, mainId, profiles));

        var signed = IpaInspector.ListBundles(output);
        Assert.Equal("com.example.sample", signed[0].OriginalBundleId);
        using var zip = System.IO.Compression.ZipFile.OpenRead(output);
        var embedded = zip.GetEntry("Payload/Sample.app/embedded.mobileprovision")!;
        using var s = embedded.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(profile, ms.ToArray());
        Assert.NotNull(zip.GetEntry("Payload/Sample.app/_CodeSignature/CodeResources"));
    }

    private static X509Certificate2 OtherCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Someone else", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return new X509Certificate2(c.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
