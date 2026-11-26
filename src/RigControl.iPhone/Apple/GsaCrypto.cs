using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RigControl.iPhone.Apple;

/// <summary>The encryption around GrandSlam's replies: the spd blob after sign-in and the app-token blob.</summary>
internal static class GsaCrypto
{
    /// <summary>
    /// spd is AES-256-CBC (PKCS#7) with key HMAC-SHA256(key, "extra data key:") and IV the first 16 bytes of
    /// HMAC-SHA256(key, "extra data iv:"). Returns null when the padding is wrong (wrong key).
    /// </summary>
    public static byte[]? DecryptSpd(byte[] key, byte[] data)
    {
        if (data.Length == 0 || data.Length % 16 != 0 || data.Length > 1024 * 1024)
            return null;
        var aesKey = HMACSHA256.HashData(key, "extra data key:"u8);
        var iv = HMACSHA256.HashData(key, "extra data iv:"u8).AsSpan(0, 16).ToArray();
        try
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;
            return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    /// <summary>The same scheme the other way round; used by the test server.</summary>
    internal static byte[] EncryptSpd(byte[] key, byte[] plain)
    {
        var aesKey = HMACSHA256.HashData(key, "extra data key:"u8);
        var iv = HMACSHA256.HashData(key, "extra data iv:"u8).AsSpan(0, 16).ToArray();
        using var aes = Aes.Create();
        aes.Key = aesKey;
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

    /// <summary>checksum = HMAC-SHA256(sk, "apptokens" | adsid | app).</summary>
    public static byte[] AppTokenChecksum(byte[] sk, string adsid, string app) =>
        HMACSHA256.HashData(sk, Encoding.UTF8.GetBytes("apptokens" + adsid + app));

    /// <summary>
    /// The "et" blob: "XYZ" | 16-byte IV | ciphertext | 16-byte tag, AES-256-GCM with key sk and "XYZ" as the
    /// additional data. Throws <see cref="AppleException"/> when it is malformed or fails authentication.
    /// </summary>
    public static byte[] DecryptAppToken(byte[] sk, byte[] et)
    {
        if (sk.Length != 32)
            throw new AppleException("Apple's sign-in reply had a session key of the wrong size.");
        if (et.Length < 3 + 16 + 16 || et.Length > 1024 * 1024 || !et.AsSpan(0, 3).SequenceEqual("XYZ"u8))
            throw new AppleException("Apple's app token is in a format RigControl doesn't know.");
        var iv = et.AsSpan(3, 16).ToArray();
        var cipher = et.AsSpan(19, et.Length - 19 - 16).ToArray();
        var tag = et.AsSpan(et.Length - 16).ToArray();
        return Gcm.Decrypt(sk, iv, cipher, tag, "XYZ"u8.ToArray())
            ?? throw new AppleException("Apple's app token could not be decrypted.");
    }

    internal static byte[] EncryptAppToken(byte[] sk, byte[] iv, byte[] plain)
    {
        var (cipher, tag) = Gcm.Encrypt(sk, iv, plain, "XYZ"u8.ToArray());
        return [.. "XYZ"u8.ToArray(), .. iv, .. cipher, .. tag];
    }
}

/// <summary>
/// AES-GCM for any IV length (NIST SP 800-38D). .NET's AesGcm only takes 12-byte nonces and Apple's app token
/// uses 16. Small inputs only (a few KB), so the plain bit-by-bit GHASH is fine.
/// </summary>
internal static class Gcm
{
    public static (byte[] Cipher, byte[] Tag) Encrypt(byte[] key, byte[] iv, byte[] plain, byte[] aad)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var h = Block(aes, new byte[16]);
        var j0 = J0(h, iv);
        var cipher = Ctr(aes, j0, plain);
        var tag = Tag(aes, h, j0, aad, cipher);
        return (cipher, tag);
    }

    /// <summary>Null when the tag doesn't match.</summary>
    public static byte[]? Decrypt(byte[] key, byte[] iv, byte[] cipher, byte[] tag, byte[] aad)
    {
        if (iv.Length == 0 || tag.Length != 16)
            return null;
        using var aes = Aes.Create();
        aes.Key = key;
        var h = Block(aes, new byte[16]);
        var j0 = J0(h, iv);
        var expected = Tag(aes, h, j0, aad, cipher);
        if (!CryptographicOperations.FixedTimeEquals(expected, tag))
            return null;
        return Ctr(aes, j0, cipher);
    }

    private static byte[] Block(Aes aes, byte[] block) => aes.EncryptEcb(block, PaddingMode.None);

    private static byte[] J0(byte[] h, byte[] iv)
    {
        if (iv.Length == 12)
        {
            var j = new byte[16];
            iv.CopyTo(j, 0);
            j[15] = 1;
            return j;
        }
        var g = new GHash(h);
        g.Update(iv);
        var lengths = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(lengths.AsSpan(8), (ulong)iv.Length * 8);
        g.UpdateBlock(lengths);
        return g.Result();
    }

    private static byte[] Ctr(Aes aes, byte[] j0, byte[] input)
    {
        if (input.Length == 0)
            return [];
        var blocks = (input.Length + 15) / 16;
        var counters = new byte[blocks * 16];
        var counter = BinaryPrimitives.ReadUInt32BigEndian(j0.AsSpan(12));
        for (var i = 0; i < blocks; i++)
        {
            j0.AsSpan(0, 12).CopyTo(counters.AsSpan(i * 16));
            BinaryPrimitives.WriteUInt32BigEndian(counters.AsSpan(i * 16 + 12), unchecked(counter + (uint)i + 1));
        }
        var stream = aes.EncryptEcb(counters, PaddingMode.None);
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
            output[i] = (byte)(input[i] ^ stream[i]);
        return output;
    }

    private static byte[] Tag(Aes aes, byte[] h, byte[] j0, byte[] aad, byte[] cipher)
    {
        var g = new GHash(h);
        g.Update(aad);
        g.Update(cipher);
        var lengths = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(lengths, (ulong)aad.Length * 8);
        BinaryPrimitives.WriteUInt64BigEndian(lengths.AsSpan(8), (ulong)cipher.Length * 8);
        g.UpdateBlock(lengths);
        var s = g.Result();
        var e = Block(aes, j0);
        for (var i = 0; i < 16; i++)
            s[i] ^= e[i];
        return s;
    }

    private sealed class GHash(byte[] h)
    {
        private readonly ulong _hHi = BinaryPrimitives.ReadUInt64BigEndian(h), _hLo = BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
        private ulong _yHi, _yLo;

        // Whole data, zero-padded to a block boundary.
        public void Update(byte[] data)
        {
            for (var at = 0; at < data.Length; at += 16)
            {
                var block = new byte[16];
                data.AsSpan(at, Math.Min(16, data.Length - at)).CopyTo(block);
                UpdateBlock(block);
            }
        }

        public void UpdateBlock(byte[] block)
        {
            _yHi ^= BinaryPrimitives.ReadUInt64BigEndian(block);
            _yLo ^= BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(8));
            (_yHi, _yLo) = Multiply(_yHi, _yLo, _hHi, _hLo);
        }

        public byte[] Result()
        {
            var r = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(r, _yHi);
            BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(8), _yLo);
            return r;
        }

        // Multiplication in GF(2^128) with GCM's bit order (SP 800-38D, algorithm 1).
        private static (ulong, ulong) Multiply(ulong xHi, ulong xLo, ulong yHi, ulong yLo)
        {
            ulong zHi = 0, zLo = 0, vHi = yHi, vLo = yLo;
            for (var i = 0; i < 128; i++)
            {
                var bit = i < 64 ? (xHi >> (63 - i)) & 1 : (xLo >> (127 - i)) & 1;
                if (bit != 0)
                {
                    zHi ^= vHi;
                    zLo ^= vLo;
                }
                var lsb = vLo & 1;
                vLo = (vLo >> 1) | (vHi << 63);
                vHi >>= 1;
                if (lsb != 0)
                    vHi ^= 0xE100000000000000UL;
            }
            return (zHi, zLo);
        }
    }
}
