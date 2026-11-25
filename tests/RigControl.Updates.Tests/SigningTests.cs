using System.Security.Cryptography;
using System.Text;

namespace RigControl.Updates.Tests;

public sealed class SigningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-updates-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    [Fact]
    public void Sign_then_verify_round_trips_and_any_change_fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = EcSignature.PublicKeyBase64(key);
        var data = Encoding.UTF8.GetBytes("{\"version\":\"1.1.2\"}");
        var sig = EcSignature.Sign(data, key);

        Assert.Equal(64, Convert.FromBase64String(sig).Length); // IEEE P1363: r and s, 32 bytes each
        Assert.True(EcSignature.VerifyBase64(data, sig, pub));

        var changed = (byte[])data.Clone();
        changed[3] ^= 1;
        Assert.False(EcSignature.VerifyBase64(changed, sig, pub));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(EcSignature.VerifyBase64(data, sig, EcSignature.PublicKeyBase64(other)));
        Assert.False(EcSignature.VerifyBase64(data, "not base64!", pub));
        Assert.False(EcSignature.VerifyBase64(data, null, pub));
    }

    [Fact]
    public void Private_key_pem_round_trips_and_public_only_pem_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var back = EcSignature.ImportPrivateKeyPem(key.ExportPkcs8PrivateKeyPem());
        Assert.Equal(EcSignature.PublicKeyBase64(key), EcSignature.PublicKeyBase64(back));

        Assert.ThrowsAny<CryptographicException>(() => EcSignature.ImportPrivateKeyPem(key.ExportSubjectPublicKeyInfoPem()));
        Assert.ThrowsAny<CryptographicException>(() => EcSignature.ImportPrivateKeyPem("-----BEGIN ENCRYPTED PRIVATE KEY-----\nAAAA\n-----END ENCRYPTED PRIVATE KEY-----"));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsAny<CryptographicException>(() => EcSignature.ImportPrivateKeyPem(p384.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void Embedded_keys_are_P256_public_keys()
    {
        using var u = EcSignature.ImportPublicKey(TrustedKeys.UpdatePublicKey);
        using var d = EcSignature.ImportPublicKey(TrustedKeys.DevKeyIssuerPublicKey);
        Assert.Equal(256, u.KeySize);
        Assert.Equal(256, d.KeySize);
    }

    [Fact]
    public void Release_files_are_signed_and_the_manifest_reads_back()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var exe = Path.Combine(_dir, "in", "RigControl.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(5000));
        var published = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        var files = ReleaseFiles.Make(exe, AppVersion.Parse("1.1.2"), key, Path.Combine(_dir, "out"), published);

        Assert.Equal("RigControl-Setup-v1.1.2.exe", Path.GetFileName(files.SetupExe));
        Assert.Equal(File.ReadAllBytes(exe), File.ReadAllBytes(files.SetupExe));
        var json = File.ReadAllBytes(files.ManifestPath);
        var manifest = UpdateManifest.ParseSigned(json, File.ReadAllText(files.SignaturePath), EcSignature.PublicKeyBase64(key));
        Assert.Equal("1.1.2", manifest.Version.ToString());
        Assert.Equal(5000, manifest.Size);
        Assert.Equal(UpdateManifest.HashFile(exe), manifest.Sha256);
        Assert.Equal(published, manifest.Published);

        var sums = File.ReadAllLines(files.SumsPath);
        Assert.Equal(3, sums.Length);
        Assert.Contains(UpdateManifest.HashFile(exe) + "  RigControl-Setup-v1.1.2.exe", sums);
        Assert.Contains(UpdateManifest.HashFile(files.ManifestPath) + "  update.json", sums);

        // Another key's signature is refused before the text is read.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<UpdateException>(() => UpdateManifest.ParseSigned(json, File.ReadAllText(files.SignaturePath), EcSignature.PublicKeyBase64(other)));
    }

    [Theory]
    [InlineData("{\"version\":\"1.1\",\"file\":\"RigControl-Setup-v1.1.exe\",\"sha256\":\"%SHA%\",\"size\":1,\"published\":\"2026-11-25T00:00:00Z\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"file\":\"..\\\\evil.exe\",\"sha256\":\"%SHA%\",\"size\":1,\"published\":\"2026-11-25T00:00:00Z\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"file\":\"RigControl-Setup-v1.1.2.exe\",\"sha256\":\"ABC\",\"size\":1,\"published\":\"2026-11-25T00:00:00Z\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"file\":\"RigControl-Setup-v1.1.2.exe\",\"sha256\":\"%SHA%\",\"size\":0,\"published\":\"2026-11-25T00:00:00Z\"}")]
    [InlineData("[1,2]")]
    public void Signed_but_malformed_manifests_are_refused(string text)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = Encoding.UTF8.GetBytes(text.Replace("%SHA%", new string('a', 64)));
        Assert.Throws<UpdateException>(() => UpdateManifest.ParseSigned(json, EcSignature.Sign(json, key), EcSignature.PublicKeyBase64(key)));
    }

    [Theory]
    [InlineData("1.1.2", true)]
    [InlineData("v1.1.2", true)]
    [InlineData("1.1", false)]
    [InlineData("1.1.2.0", false)]
    [InlineData("1.-1.2", false)]
    [InlineData(" 1.1.2 ", true)]
    [InlineData("1.1.x", false)]
    public void Versions_parse_strictly(string text, bool ok) => Assert.Equal(ok, AppVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_by_number()
    {
        Assert.True(AppVersion.Parse("1.10.0") > AppVersion.Parse("1.9.9"));
        Assert.True(AppVersion.Parse("2.0.0") > AppVersion.Parse("1.99.99"));
        Assert.Equal(AppVersion.Parse("v1.1.2"), AppVersion.Parse("1.1.2"));
        Assert.True(AppVersion.TryParseLoose("1.1.2+abc", out var loose));
        Assert.Equal("1.1.2", loose!.Value.ToString());
    }
}
