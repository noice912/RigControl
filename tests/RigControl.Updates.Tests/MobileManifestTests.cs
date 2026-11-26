using System.Security.Cryptography;
using System.Text;
using RigControl.Updates;

namespace RigControl.Updates.Tests;

public class MobileManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-mobile-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (string Ipa, string Json, string Sig) MakeOne(string version = "1.2.3-rc.1")
    {
        Directory.CreateDirectory(_dir);
        var src = Path.Combine(_dir, "in.ipa");
        File.WriteAllBytes(src, Encoding.ASCII.GetBytes("PK fake ipa"));
        Assert.True(AppVersion.TryParse(version, out var v));
        return MobileManifest.Make(src, v, _key, Path.Combine(_dir, "out"));
    }

    private string Pub => EcSignature.PublicKeyBase64(_key);

    [Fact]
    public void Made_files_verify_and_match_the_ipa()
    {
        var (ipa, json, sig) = MakeOne();
        Assert.EndsWith("RigControl-Mobile-v1.2.3-rc.1.ipa", ipa);
        var m = MobileManifest.ParseSigned(File.ReadAllBytes(json), File.ReadAllText(sig).Trim(), Pub);
        Assert.True(m.Matches(ipa));
        File.AppendAllText(ipa, "x");
        Assert.False(m.Matches(ipa));
    }

    [Fact]
    public void A_changed_byte_or_another_key_is_refused()
    {
        var (_, json, sig) = MakeOne();
        var bytes = File.ReadAllBytes(json);
        bytes[^3] ^= 1;
        Assert.Throws<UpdateException>(() => MobileManifest.ParseSigned(bytes, File.ReadAllText(sig).Trim(), Pub));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<UpdateException>(() =>
            MobileManifest.ParseSigned(File.ReadAllBytes(json), File.ReadAllText(sig).Trim(), EcSignature.PublicKeyBase64(other)));
    }

    [Fact]
    public void A_signed_update_json_is_not_accepted_as_the_phone_manifest_or_the_reverse()
    {
        Directory.CreateDirectory(_dir);
        var exe = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(exe, [1, 2, 3]);
        Assert.True(AppVersion.TryParse("1.2.3", out var v));
        var set = ReleaseFiles.Make(exe, v, _key, Path.Combine(_dir, "rel"), DateTimeOffset.UtcNow);
        Assert.Throws<UpdateException>(() => MobileManifest.ParseSigned(File.ReadAllBytes(set.ManifestPath), File.ReadAllText(set.SignaturePath).Trim(), Pub));

        var (_, json, sig) = MakeOne("1.2.3");
        Assert.Throws<UpdateException>(() => UpdateManifest.ParseSigned(File.ReadAllBytes(json), File.ReadAllText(sig).Trim(), Pub));
    }
}
