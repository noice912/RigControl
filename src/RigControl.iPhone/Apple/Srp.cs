using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RigControl.iPhone.Apple;

/// <summary>
/// The SRP-6a client side as Apple's GrandSlam sign-in speaks it:
///   group      RFC 5054 2048-bit (N below, g = 2), hash SHA-256
///   k          H(N | PAD(g))
///   u          H(PAD(A) | PAD(B))
///   x          H(s | H(":" | p))           the user name is left out of x; p is the PBKDF2 password key
///   S          (B - k*g^x) ^ (a + u*x) mod N
///   K          H(S)
///   M1         H(H(N) xor H(PAD(g)) | H(I) | s | A | B | K)   A as sent, B as received
///   M2         H(A | M1 | K)
/// PAD means left-padded with zeros to N's length (256 bytes).
/// </summary>
internal sealed class SrpClient
{
    internal const int Length = 256;

    internal static readonly BigInteger N = Unsigned(Convert.FromHexString(
        "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13D" +
        "D52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B8" +
        "55F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481" +
        "F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6" +
        "AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F5475" +
        "9B65E372FCD68EF20FA7111F9E4AFF73"));

    internal static readonly BigInteger G = 2;

    private readonly BigInteger _a;

    /// <summary>A = g^a mod N, as sent to Apple (big-endian, no leading zeros).</summary>
    public byte[] PublicA { get; }

    /// <param name="privateA">Only tests pass this; otherwise 32 random bytes are used, as Apple's own clients do.</param>
    public SrpClient(byte[]? privateA = null)
    {
        var a = privateA ?? RandomNumberGenerator.GetBytes(32);
        _a = Unsigned(a);
        if (_a.IsZero)
            throw new ArgumentException("The SRP private value is zero.");
        var aPub = BigInteger.ModPow(G, _a, N);
        PublicA = Bytes(aPub);
        if (privateA is null)
            CryptographicOperations.ZeroMemory(a);
    }

    /// <summary>
    /// Works out the proof from the server's salt and B. Throws <see cref="AppleException"/> when B is unusable.
    /// </summary>
    public SrpResult Process(string username, byte[] passwordKey, byte[] salt, byte[] serverB)
    {
        if (serverB.Length is 0 or > Length)
            throw new AppleException("Apple's sign-in server sent an unusable reply (B).");
        if (salt.Length is 0 or > 1024)
            throw new AppleException("Apple's sign-in server sent an unusable reply (salt).");
        var b = Unsigned(serverB);
        if ((b % N).IsZero)
            throw new AppleException("Apple's sign-in server sent an unusable reply (B).");

        var u = Unsigned(Hash(Pad(PublicA), Pad(serverB)));
        if (u.IsZero)
            throw new AppleException("Apple's sign-in server sent an unusable reply (u).");
        var k = Unsigned(Hash(Bytes(N), Pad(Bytes(G))));

        var inner = Hash(":"u8.ToArray(), passwordKey);
        var x = Unsigned(Hash(salt, inner));
        CryptographicOperations.ZeroMemory(inner);

        var gx = BigInteger.ModPow(G, x, N);
        var baseValue = Mod(b - k * gx, N);
        var s = BigInteger.ModPow(baseValue, _a + u * x, N);
        var premaster = Bytes(s);
        var key = Hash(premaster);

        var hn = Hash(Bytes(N));
        var hg = Hash(Pad(Bytes(G)));
        for (var i = 0; i < hn.Length; i++)
            hn[i] ^= hg[i];
        var m1 = Hash(hn, Hash(Encoding.UTF8.GetBytes(username)), salt, PublicA, serverB, key);
        var m2 = Hash(PublicA, m1, key);
        return new SrpResult(m1, m2, key, premaster);
    }

    internal static byte[] Hash(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts)
            h.AppendData(p);
        return h.GetHashAndReset();
    }

    internal static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    internal static byte[] Bytes(BigInteger value) =>
        value.IsZero ? [0] : value.ToByteArray(isUnsigned: true, isBigEndian: true);

    internal static byte[] Pad(byte[] value)
    {
        if (value.Length >= Length)
            return value;
        var padded = new byte[Length];
        value.CopyTo(padded, Length - value.Length);
        return padded;
    }

    private static BigInteger Mod(BigInteger value, BigInteger m)
    {
        var r = value % m;
        return r.Sign < 0 ? r + m : r;
    }

    /// <summary>
    /// The password key Apple's SRP uses: PBKDF2-SHA256 over SHA-256(password), or over the lowercase hex text of
    /// that hash when Apple picked "s2k_fo". 32 bytes.
    /// </summary>
    internal static byte[] PasswordKey(ReadOnlySpan<byte> passwordSha256, string protocol, byte[] salt, int iterations)
    {
        byte[] input = protocol switch
        {
            "s2k" => passwordSha256.ToArray(),
            "s2k_fo" => LowerHex(passwordSha256),
            _ => throw new AppleException($"Apple asked for a sign-in method RigControl doesn't know ({protocol})."),
        };
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(input, salt, iterations, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    // Lowercase hex straight into bytes, so no string copy of the password hash is left behind.
    private static byte[] LowerHex(ReadOnlySpan<byte> data)
    {
        const string digits = "0123456789abcdef";
        var hex = new byte[data.Length * 2];
        for (var i = 0; i < data.Length; i++)
        {
            hex[2 * i] = (byte)digits[data[i] >> 4];
            hex[2 * i + 1] = (byte)digits[data[i] & 0xF];
        }
        return hex;
    }
}

/// <summary>M1 to send, the M2 Apple must answer with, the session key K = H(S) and the raw S.</summary>
internal sealed record SrpResult(byte[] M1, byte[] ExpectedM2, byte[] SessionKey, byte[] PremasterSecret)
{
    public void Clear()
    {
        CryptographicOperations.ZeroMemory(SessionKey);
        CryptographicOperations.ZeroMemory(PremasterSecret);
    }
}
