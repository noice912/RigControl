using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RigControl.Link.Tests;

public class PairingCodeTests
{
    [Fact]
    public void New_code_is_20_crockford_symbols()
    {
        for (var i = 0; i < 200; i++)
        {
            var c = PairingCode.New();
            Assert.Equal(20, c.Length);
            Assert.All(c, ch => Assert.Contains(ch, PairingCode.Alphabet));
            Assert.Equal(c, PairingCode.Normalize(c));
        }
    }

    [Fact]
    public void Alphabet_is_crockford_base32()
    {
        Assert.Equal(32, PairingCode.Alphabet.Distinct().Count());
        foreach (var bad in "ILOU") Assert.DoesNotContain(bad, PairingCode.Alphabet);
    }

    [Fact]
    public void Codes_carry_100_bits_and_are_uniform()
    {
        // 20 symbols of 5 bits each = 100 bits.
        Assert.Equal(100, PairingCode.Length * 5);
        const int n = 4000;
        var seen = new HashSet<string>();
        var counts = new int[32];
        for (var i = 0; i < n; i++)
        {
            var c = PairingCode.New();
            Assert.True(seen.Add(c), "a code repeated");
            foreach (var ch in c) counts[PairingCode.Alphabet.IndexOf(ch)]++;
        }
        // Chi-square over 32 symbols, 80 000 draws. 31 degrees of freedom: 99.99% of uniform runs stay below ~70.
        var expected = n * 20 / 32.0;
        var chi = counts.Sum(k => (k - expected) * (k - expected) / expected);
        Assert.True(chi < 80, $"chi-square {chi:0.0} is too high for a uniform choice");
    }

    [Fact]
    public void Groups_and_display_are_5_by_4()
    {
        var code = "ABCDEFGHJKMNPQRSTVWX";
        Assert.Equal(new[] { "ABCD", "EFGH", "JKMN", "PQRS", "TVWX" }, PairingCode.Groups(code));
        Assert.Equal("ABCD-EFGH-JKMN-PQRS-TVWX", PairingCode.Display(code));
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs-tvwx", "ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("ABCD EFGH JKMN PQRS TVWX", "ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("O0Il-0000-0000-0000-0000", "00110000000000000000")]
    public void Normalize_reads_typed_codes(string typed, string expected)
        => Assert.Equal(expected, PairingCode.Normalize(typed));

    [Theory]
    [InlineData("ABCDEFGHJKMNPQRSTVW")]   // 19
    [InlineData("ABCDEFGHJKMNPQRSTVWXY")] // 21
    [InlineData("ABCDEFGHJKMNPQRSTVWU")]  // U is not Crockford
    [InlineData("ABCDEFGHJKMNPQRSTVW!")]
    [InlineData("")]
    public void Normalize_refuses_bad_codes(string typed) => Assert.Null(PairingCode.Normalize(typed));

    [Fact]
    public void Qr_text_is_exact_and_parses_back()
    {
        var qr = PairingCode.QrText("relay.example.workers.dev", "abcd-efgh-jkmn-pqrs-tvwx");
        Assert.Equal("rigcontrol://pair?relay=relay.example.workers.dev&code=ABCDEFGHJKMNPQRSTVWX", qr);
        Assert.True(PairingCode.TryParseQr(qr, out var host, out var code));
        Assert.Equal("relay.example.workers.dev", host);
        Assert.Equal("ABCDEFGHJKMNPQRSTVWX", code);
        Assert.False(PairingCode.TryParseQr("https://pair?relay=x&code=ABCDEFGHJKMNPQRSTVWX", out _, out _));
        Assert.False(PairingCode.TryParseQr("rigcontrol://pair?relay=a/b&code=ABCDEFGHJKMNPQRSTVWX", out _, out _));
        Assert.False(PairingCode.TryParseQr("rigcontrol://pair?relay=x&code=short", out _, out _));
    }

    [Fact]
    public void Rendezvous_hash_is_sha256_of_the_exact_text()
    {
        var expected = B64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("rigcontrol-pair|ABCDEFGHJKMNPQRSTVWX")));
        Assert.Equal(expected, PairingCode.RendezvousHash("abcd-efgh-jkmn-pqrs-tvwx"));
        Assert.DoesNotContain("=", expected);
    }

    [Fact]
    public void Box_key_is_hkdf_with_the_exact_strings()
    {
        var expected = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes("ABCDEFGHJKMNPQRSTVWX"), 32,
            Encoding.UTF8.GetBytes("rigcontrol-pair-v1"), Encoding.UTF8.GetBytes("box"));
        Assert.Equal(expected, PairingCode.BoxKey("abcd-efgh-jkmn-pqrs-tvwx"));
    }
}

public class CryptoTests
{
    [Fact]
    public void Hkdf_matches_rfc5869_case_1()
    {
        // The HKDF used everywhere here is .NET's; this pins it to the RFC's first test vector.
        var ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt, info);
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", Convert.ToHexString(okm).ToLowerInvariant());
    }

    [Fact]
    public void Aes_gcm_round_trip_with_nonce_in_front()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("hello phone");
        var aad = Encoding.UTF8.GetBytes("aad");
        var sealedData = LinkCrypto.SealRandom(key, plain, aad);
        Assert.Equal(12 + plain.Length + 16, sealedData.Length);
        Assert.Equal(plain, LinkCrypto.Open(key, sealedData, aad));
    }

    [Fact]
    public void Tampered_wrong_key_or_wrong_aad_fail()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var aad = Encoding.UTF8.GetBytes("aad");
        var sealedData = LinkCrypto.SealRandom(key, Encoding.UTF8.GetBytes("hello"), aad);
        for (var i = 0; i < sealedData.Length; i++)
        {
            var copy = (byte[])sealedData.Clone();
            copy[i] ^= 0x01;
            Assert.Equal("sealed", Assert.Throws<LinkException>(() => LinkCrypto.Open(key, copy, aad)).Code);
        }
        Assert.Throws<LinkException>(() => LinkCrypto.Open(RandomNumberGenerator.GetBytes(32), sealedData, aad));
        Assert.Throws<LinkException>(() => LinkCrypto.Open(key, sealedData, Encoding.UTF8.GetBytes("other")));
    }

    [Fact]
    public void Box_opens_with_the_right_code_only()
    {
        var code = PairingCode.New();
        var box = LinkCrypto.SealBox(PairingCode.BoxKey(code), new JsonObject { ["pc"] = "x" });
        Assert.Equal("x", (string)LinkCrypto.OpenBox(PairingCode.BoxKey(code), box)["pc"]!);
        var other = PairingCode.New();
        Assert.Equal("wrongcode", Assert.Throws<LinkException>(() => LinkCrypto.OpenBox(PairingCode.BoxKey(other), box)).Code);
    }

    [Fact]
    public void Session_key_is_the_same_on_both_ends()
    {
        using var pc = DeviceKeys.Create();
        using var phone = DeviceKeys.Create();
        var a = LinkCrypto.SessionKey(pc, phone.AgreementPublic, phone.Id, SecureChannelTests.Epoch);
        var b = LinkCrypto.SessionKey(phone, pc.AgreementPublic, pc.Id, SecureChannelTests.Epoch);
        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
        using var stranger = DeviceKeys.Create();
        Assert.NotEqual(a, LinkCrypto.SessionKey(stranger, pc.AgreementPublic, pc.Id, SecureChannelTests.Epoch));
        Assert.NotEqual(a, LinkCrypto.SessionKey(pc, phone.AgreementPublic, phone.Id, SecureChannelTests.OtherEpoch));
    }

    [Fact]
    public void Session_key_follows_the_documented_derivation()
    {
        using var pc = DeviceKeys.Create();
        using var phone = DeviceKeys.Create();
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(phone.AgreementPublic, out _);
        var z = pc.Agreement.DeriveRawSecretAgreement(peer.PublicKey);
        var ids = new[] { pc.Id, phone.Id };
        Array.Sort(ids, StringComparer.Ordinal);
        var expected = HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, Encoding.UTF8.GetBytes(ids[0] + "|" + ids[1] + "|" + SecureChannelTests.Epoch),
            Encoding.UTF8.GetBytes("rigcontrol-link-v1 session"));
        Assert.Equal(expected, LinkCrypto.SessionKey(pc, phone.AgreementPublic, phone.Id, SecureChannelTests.Epoch));
    }
}

public class DeviceKeyTests
{
    [Fact]
    public void Id_is_b64url_sha256_of_the_signing_spki()
    {
        using var k = DeviceKeys.Create();
        Assert.Equal(B64Url.Encode(SHA256.HashData(k.SigningPublic)), k.Id);
        Assert.Equal(43, k.Id.Length);
        Assert.DoesNotContain('=', k.Id);
        using var e = ECDsa.Create();
        e.ImportSubjectPublicKeyInfo(k.SigningPublic, out _);
        Assert.Equal(256, e.KeySize);
    }

    [Fact]
    public void Private_keys_round_trip()
    {
        using var k = DeviceKeys.Create();
        using var back = DeviceKeys.ImportPrivate(k.ExportPrivate());
        Assert.Equal(k.Id, back.Id);
        Assert.Equal(k.AgreementPublic, back.AgreementPublic);
        Assert.True(DeviceKeys.Verify(k.SigningPublic, "text", back.Sign("text")));
        Assert.Equal("badkey", Assert.Throws<LinkException>(() => DeviceKeys.ImportPrivate(Encoding.UTF8.GetBytes("{}"))).Code);
    }

    [Fact]
    public void Signatures_are_p1363_over_utf8()
    {
        using var k = DeviceKeys.Create();
        var sig = k.Sign("rigcontrol-link-v1|abc|room");
        Assert.True(B64Url.TryDecode(sig, out var raw));
        Assert.Equal(64, raw.Length);
        using var e = ECDsa.Create();
        e.ImportSubjectPublicKeyInfo(k.SigningPublic, out _);
        Assert.True(e.VerifyData(Encoding.UTF8.GetBytes("rigcontrol-link-v1|abc|room"), raw, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(DeviceKeys.Verify(k.SigningPublic, "rigcontrol-link-v1|abc|roon", sig));
    }

    [Fact]
    public void Base64url_has_no_padding_and_refuses_standard_base64()
    {
        Assert.Equal("-_8", B64Url.Encode(new byte[] { 0xfb, 0xff }));
        Assert.Equal(new byte[] { 0xfb, 0xff }, B64Url.Decode("-_8"));
        Assert.False(B64Url.TryDecode("+/8=", out _));
    }
}

public class SignedStringTests
{
    [Fact]
    public void Hello_text_is_byte_for_byte()
    {
        Assert.Equal("rigcontrol-link-v1|NONCE|ROOM", LinkProtocol.HelloSignedText("NONCE", "ROOM"));
        Assert.Equal(Encoding.UTF8.GetBytes("rigcontrol-link-v1|n-_|r"), Encoding.UTF8.GetBytes(LinkProtocol.HelloSignedText("n-_", "r")));
    }

    [Fact]
    public void Allow_text_is_byte_for_byte()
    {
        Assert.Equal("rigcontrol-link-v1|allow|ROOM|a,b,c", LinkProtocol.AllowSignedText("ROOM", new[] { "a", "b", "c" }));
        Assert.Equal("rigcontrol-link-v1|allow|ROOM|", LinkProtocol.AllowSignedText("ROOM", Array.Empty<string>()));
        Assert.Equal("rigcontrol-link-v1|allow|ROOM|x", LinkProtocol.AllowSignedText("ROOM", new[] { "x" }));
    }

    [Fact]
    public void Message_aad_is_byte_for_byte()
        => Assert.Equal("rigcontrol-link-v1|FROM|TO|1234567890123|EPOCH", LinkProtocol.MessageAad("FROM", "TO", 1234567890123, "EPOCH"));

    [Fact]
    public void Pair_text_is_byte_for_byte()
        => Assert.Equal("rigcontrol-pair|ABC", LinkProtocol.PairHashText("ABC"));
}
