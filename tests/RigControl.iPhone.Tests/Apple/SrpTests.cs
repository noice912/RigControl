using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RigControl.iPhone.Apple;

namespace RigControl.iPhone.Tests.Apple;

public class SrpTests
{
    // RFC 5054 appendix B inputs (user, password, salt, a, b). The RFC's expected numbers are for SHA-1 and the
    // 1024-bit group; here the same inputs run with Apple's SHA-256/2048 variant and are checked against the
    // separately written server in Fakes.cs.
    private static readonly byte[] RfcSalt = Convert.FromHexString("BEB25379D1A8581EB5A727673A2441EE");
    private static readonly byte[] RfcA = Convert.FromHexString("60975527035CF2AD1989806F0407210BC81EDC04E2762A56AFD529DDDA2D4393");
    private static readonly byte[] RfcB = Convert.FromHexString("E487CB59D31AC550471E81F00F6928E01DDA08E974A004F49E61F5D105284D20");

    [Fact]
    public void Group_prime_is_the_rfc_5054_safe_prime()
    {
        Assert.Equal(SrpServerReference.N, SrpClient.N);
        Assert.Equal(2048, (int)SrpClient.N.GetBitLength());
        Assert.True(ProbablyPrime(SrpClient.N));
        Assert.True(ProbablyPrime((SrpClient.N - 1) / 2));
    }

    [Fact]
    public void Rfc_inputs_client_and_reference_server_agree()
    {
        var client = new SrpClient(RfcA);
        Assert.Equal(SrpServerReference.ToBytes(BigInteger.ModPow(2, SrpServerReference.FromBytes(RfcA), SrpServerReference.N), 0), client.PublicA);

        var passwordKey = Encoding.UTF8.GetBytes("password123");
        var server = new SrpServerReference("alice", passwordKey, RfcSalt, RfcB);
        var result = client.Process("alice", passwordKey, RfcSalt, server.B);

        var m2 = server.Verify(client.PublicA, result.M1);
        Assert.NotNull(m2);
        Assert.Equal(m2, result.ExpectedM2);
        Assert.Equal(server.SessionKey, result.SessionKey);
        Assert.Equal(server.Premaster, result.PremasterSecret);
        Assert.Equal(SHA256.HashData(result.PremasterSecret), result.SessionKey);
    }

    [Fact]
    public void Random_runs_agree_and_a_wrong_password_is_caught()
    {
        for (var i = 0; i < 5; i++)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = RandomNumberGenerator.GetBytes(32);
            var client = new SrpClient();
            var server = new SrpServerReference("someone@example.com", key, salt);
            var result = client.Process("someone@example.com", key, salt, server.B);
            Assert.Equal(result.ExpectedM2, server.Verify(client.PublicA, result.M1));

            var wrong = new SrpClient().Process("someone@example.com", RandomNumberGenerator.GetBytes(32), salt, server.B);
            Assert.Null(server.Verify(client.PublicA, wrong.M1));
        }
    }

    [Fact]
    public void User_name_goes_into_m1_but_not_into_x()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = RandomNumberGenerator.GetBytes(32);
        var server = new SrpServerReference("a@example.com", key, salt);
        var client = new SrpClient();
        // Same password key, other user name: the shared secret is the same (x ignores the name), M1 is not.
        var other = client.Process("b@example.com", key, salt, server.B);
        Assert.Null(server.Verify(client.PublicA, other.M1));
        Assert.Equal(server.SessionKey, other.SessionKey);
    }

    [Fact]
    public void Bad_server_values_are_refused()
    {
        var client = new SrpClient();
        var salt = new byte[16];
        var key = new byte[32];
        Assert.Throws<AppleException>(() => client.Process("x", key, salt, [0]));
        Assert.Throws<AppleException>(() => client.Process("x", key, salt, SrpServerReference.ToBytes(SrpClient.N, 0)));
        Assert.Throws<AppleException>(() => client.Process("x", key, salt, new byte[257]));
        Assert.Throws<AppleException>(() => client.Process("x", key, [], [5]));
    }

    [Fact]
    public void Password_key_s2k_and_s2k_fo()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("pässword"));
        var salt = RandomNumberGenerator.GetBytes(16);
        Assert.Equal(Rfc2898DeriveBytes.Pbkdf2(hash, salt, 1000, HashAlgorithmName.SHA256, 32),
            SrpClient.PasswordKey(hash, "s2k", salt, 1000));
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(hash).ToLowerInvariant());
        Assert.Equal(Rfc2898DeriveBytes.Pbkdf2(hex, salt, 1000, HashAlgorithmName.SHA256, 32),
            SrpClient.PasswordKey(hash, "s2k_fo", salt, 1000));
        Assert.Throws<AppleException>(() => SrpClient.PasswordKey(hash, "plain", salt, 1000));
    }

    private static bool ProbablyPrime(BigInteger n)
    {
        var d = n - 1;
        var r = 0;
        while (d.IsEven) { d >>= 1; r++; }
        foreach (var a in new BigInteger[] { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29 })
        {
            var x = BigInteger.ModPow(a, d, n);
            if (x.IsOne || x == n - 1)
                continue;
            var composite = true;
            for (var i = 1; i < r && composite; i++)
            {
                x = BigInteger.ModPow(x, 2, n);
                if (x == n - 1)
                    composite = false;
            }
            if (composite)
                return false;
        }
        return true;
    }
}

public class GsaCryptoTests
{
    [Fact]
    public void Gcm_with_12_byte_nonce_matches_dotnet()
    {
        for (var len = 0; len < 70; len += 7)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plain = RandomNumberGenerator.GetBytes(len);
            var aad = RandomNumberGenerator.GetBytes(len % 20);
            var expectedCipher = new byte[len];
            var expectedTag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, expectedCipher, expectedTag, aad);
            var (cipher, tag) = Gcm.Encrypt(key, nonce, plain, aad);
            Assert.Equal(expectedCipher, cipher);
            Assert.Equal(expectedTag, tag);
            Assert.Equal(plain, Gcm.Decrypt(key, nonce, cipher, tag, aad));
        }
    }

    [Fact]
    public void App_token_blob_round_trips_and_tampering_fails()
    {
        var sk = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        var plain = Encoding.UTF8.GetBytes("<plist>token</plist>");
        var et = GsaCrypto.EncryptAppToken(sk, iv, plain);
        Assert.Equal(plain, GsaCrypto.DecryptAppToken(sk, et));

        var tampered = (byte[])et.Clone();
        tampered[25] ^= 1;
        Assert.Throws<AppleException>(() => GsaCrypto.DecryptAppToken(sk, tampered));
        Assert.Throws<AppleException>(() => GsaCrypto.DecryptAppToken(RandomNumberGenerator.GetBytes(32), et));
        Assert.Throws<AppleException>(() => GsaCrypto.DecryptAppToken(sk, [.. "ABC"u8.ToArray(), .. et[3..]]));
        Assert.Throws<AppleException>(() => GsaCrypto.DecryptAppToken(sk, et[..30]));
    }

    [Fact]
    public void Spd_round_trips_and_a_wrong_key_gives_null()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("spd contents");
        var data = GsaCrypto.EncryptSpd(key, plain);
        Assert.Equal(plain, GsaCrypto.DecryptSpd(key, data));
        // A wrong key almost always breaks the padding; when it doesn't, the plist parse fails later.
        var wrong = GsaCrypto.DecryptSpd(RandomNumberGenerator.GetBytes(32), data);
        Assert.True(wrong is null || !wrong.SequenceEqual(plain));
        Assert.Null(GsaCrypto.DecryptSpd(key, [1, 2, 3]));
    }

    [Fact]
    public void Checksum_is_hmac_over_apptokens_adsid_app()
    {
        var sk = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(HMACSHA256.HashData(sk, Encoding.UTF8.GetBytes("apptokens000-adsidcom.apple.gs.xcode.auth")),
            GsaCrypto.AppTokenChecksum(sk, "000-adsid", "com.apple.gs.xcode.auth"));
    }

    [Fact]
    public void Client_info_gets_authkit_in_its_third_part()
    {
        Assert.Equal("<A> <B> <com.apple.AuthKit/1 (com.apple.akd/1.0)>",
            AppleHttp.AuthKitClientInfo("<A> <B> <com.apple.AOSKit/282 (com.apple.dt.Xcode/3594.4.19)>"));
        Assert.Equal("<A> <B>", AppleHttp.AuthKitClientInfo("<A> <B>"));
    }
}
