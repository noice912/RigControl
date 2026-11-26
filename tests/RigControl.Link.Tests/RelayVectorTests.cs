using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RigControl.Link.Tests;

/// <summary>The test vector of relay/README.md, so this side is proven byte-compatible with the relay.</summary>
public class RelayVectorTests
{
    private const string D = "TYZpsf8nnOtU2hG5CrIbpr2t5zLCvJS1Fg8Zz6CoLWU";
    private const string Pub = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXBkWlhfTI7ssupvLHvSM3b0LyJ_7IruW1xR88mgLuZ7ZZQnX8ZtbGtG1Ey6WBVDQvVAtWs-SGyTcDNBTqzAmPA";
    private const string Id = "s-cAp3rrut4EfrWaw6fc5Ykr_lc0qYFZmXVsTT2CQ8k";
    private const string Nonce = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string HelloText = "rigcontrol-link-v1|AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8|s-cAp3rrut4EfrWaw6fc5Ykr_lc0qYFZmXVsTT2CQ8k";
    private const string Sig = "TNnWHUJntqq5_mS-kax7XgIs-eYlApHejbhUY_Q3AbTqUq7UGQTsN0zzRTTceiAJhXBDU0kaMOTkHKkUPoF_Lw";

    [Fact]
    public void Scalar_is_sha256_of_the_label()
        => Assert.Equal(D, B64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("rigcontrol-link test vector"))));

    [Fact]
    public void Id_hello_text_and_signature_match()
    {
        var pub = B64Url.Decode(Pub);
        Assert.Equal(91, pub.Length);
        Assert.Equal(Id, DeviceKeys.IdOf(pub));
        Assert.Equal(32, B64Url.Decode(Nonce).Length);
        Assert.Equal(HelloText, LinkProtocol.HelloSignedText(Nonce, Id));
        Assert.True(DeviceKeys.Verify(pub, HelloText, Sig));
        Assert.False(DeviceKeys.Verify(pub, LinkProtocol.HelloSignedText(Nonce, Id + "x"), Sig));
    }

    [Fact]
    public void Public_key_comes_from_the_scalar_and_our_signature_verifies()
    {
        // Q = d*G: import d alone and let the crypto library compute the public point.
        using var full = ECDsa.Create();
        try
        {
            full.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = B64Url.Decode(D) });
        }
        catch (CryptographicException)
        {
            return; // this system's crypto library cannot derive Q from d; the other test covers the vector
        }
        Assert.Equal(Pub, B64Url.Encode(full.ExportSubjectPublicKeyInfo()));
        var sig = full.SignData(Encoding.UTF8.GetBytes(HelloText), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.Equal(64, sig.Length);
        Assert.True(DeviceKeys.Verify(B64Url.Decode(Pub), HelloText, B64Url.Encode(sig)));
    }

    [Fact]
    public void Own_keys_have_the_relay_form()
    {
        using var k = DeviceKeys.Create();
        Assert.Equal(91, k.SigningPublic.Length);
        Assert.Equal(43, k.Id.Length);
        Assert.Equal(86, k.Sign("x").Length); // 64 bytes
    }

    [Fact]
    public void Base64url_is_strict()
    {
        Assert.Throws<FormatException>(() => B64Url.Decode("AB=="));
        Assert.Throws<FormatException>(() => B64Url.Decode("AB+/"));
        // The unused low bits of the last character must be zero: "AA" is 0x00, "AB" is not canonical.
        Assert.Throws<FormatException>(() => B64Url.Decode("AB"));
        Assert.Equal(new byte[] { 0 }, B64Url.Decode("AA"));
    }

    [Theory]
    [InlineData("lighting.setEffect", "effect", "wave")]
    [InlineData("presence.setCustom", "id", "abc")]
    public void Trailing_newlines_are_refused(string action, string field, string value)
    {
        foreach (var end in new[] { "\n", "\r\n" })
        {
            var args = new JsonObject { [field] = value + end };
            if (action == "presence.setCustom") args["on"] = true;
            Assert.Null(LinkActions.Check(action, args, out _));
            args[field] = value;
            Assert.NotNull(LinkActions.Check(action, args, out _));
        }
    }
}
