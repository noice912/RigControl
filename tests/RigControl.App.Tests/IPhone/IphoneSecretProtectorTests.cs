using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RigControl.App.IPhone;
using RigControl.iPhone.Helper;

namespace RigControl.App.Tests.IPhone;

/// <summary>The iPhone installer's real DPAPI protection (current Windows user) around an imported certificate.</summary>
public class IphoneSecretProtectorTests
{
    [Fact]
    public void Dpapi_round_trip_and_nothing_readable_on_disk()
    {
        var protector = new IphoneSecretProtector();
        var plain = RandomNumberGenerator.GetBytes(64);
        var sealedBytes = protector.Protect(plain);
        Assert.NotEqual(plain, sealedBytes);
        Assert.Equal(-1, sealedBytes.AsSpan().IndexOf(plain.AsSpan(0, 16)));
        Assert.Equal(plain, protector.Unprotect(sealedBytes));
        sealedBytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedBytes));
    }

    [Fact]
    public void An_imported_certificate_is_kept_under_dpapi()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rc-imported-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Apple Development: Test (TEAMID1234), OU=TEAMID1234", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var p12 = cert.Export(X509ContentType.Pkcs12, "secret");
            var profile = TestProfile.For(cert.RawData, DateTime.UtcNow.AddDays(6));

            var store = new ImportedIdentityStore(folder, new IphoneSecretProtector());
            var info = store.Import(p12, "secret".ToCharArray(), profile);
            Assert.Equal("TEAMID1234", info.TeamId);

            var onDisk = File.ReadAllBytes(Path.Combine(folder, "certificate.p12.protected"));
            Assert.Equal(-1, onDisk.AsSpan().IndexOf(cert.RawData.AsSpan(0, 32)));
            var loaded = store.Load();
            Assert.NotNull(loaded);
            using (loaded.Value.Certificate)
                Assert.True(loaded.Value.Certificate.HasPrivateKey);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}

/// <summary>A minimal Apple-shaped profile (CMS around an XML plist) for these tests.</summary>
internal static class TestProfile
{
    public static byte[] For(byte[] developerCertificate, DateTime expires)
    {
        var plist = new RigControl.iPhone.Plist.PlistDict
        {
            ["DeveloperCertificates"] = new List<object> { developerCertificate },
            ["Entitlements"] = new RigControl.iPhone.Plist.PlistDict { ["application-identifier"] = "TEAMID1234.*" },
            ["ExpirationDate"] = expires,
            ["Name"] = "Test",
            ["ProvisionedDevices"] = new List<object> { "00008110-000000000000001E" },
            ["TeamIdentifier"] = new List<object> { "TEAMID1234" },
        };
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Profile Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signer = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var cms = new System.Security.Cryptography.Pkcs.SignedCms(
            new System.Security.Cryptography.Pkcs.ContentInfo(RigControl.iPhone.Plist.PlistFile.ToXml(plist)), detached: false);
        cms.ComputeSignature(new System.Security.Cryptography.Pkcs.CmsSigner(signer), silent: true);
        return cms.Encode();
    }
}
