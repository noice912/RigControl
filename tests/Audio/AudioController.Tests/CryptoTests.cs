using System.Security.Cryptography;
using System.Text;
using AudioController.Storage;
using Xunit;

namespace AudioController.Tests;

/// <summary>SPEC 4.4 / 7 "Encryption": AES-256-GCM with layout [ACV1][nonce 12][tag 16][ciphertext].</summary>
public sealed class CryptoTests
{
    private const int HeaderBytes = 4 + 12 + 16;

    public static TheoryData<int> PlaintextSizes => new()
    {
        0,
        1,
        15,
        16,
        17,
        1000,
        64 * 1024,
        1024 * 1024,      // ~1 MB, roughly a short sound
        1024 * 1024 + 3,  // not a multiple of the AES block size
    };

    [Theory]
    [MemberData(nameof(PlaintextSizes))]
    public void EncryptThenDecrypt_VariousSizes_ReturnsOriginalPlaintext(int size)
    {
        var key = TestBytes.RandomKey();
        var plaintext = TestBytes.Deterministic(size, seed: size);

        var encrypted = AesGcmCrypto.Encrypt(key, plaintext);
        var decrypted = AesGcmCrypto.Decrypt(key, encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [MemberData(nameof(PlaintextSizes))]
    public void Encrypt_VariousSizes_OutputHasAcv1MagicAndExactLayoutLength(int size)
    {
        var encrypted = AesGcmCrypto.Encrypt(TestBytes.RandomKey(), TestBytes.Deterministic(size));

        Assert.Equal(4 + 12 + 16 + size, encrypted.Length);
        Assert.Equal("ACV1", Encoding.ASCII.GetString(encrypted, 0, 4));
        Assert.Equal(new byte[] { 0x41, 0x43, 0x56, 0x31 }, encrypted[..4]);
        Assert.Equal(AesGcmCrypto.Magic, encrypted[..4]);
    }

    [Fact]
    public void Constants_MatchSpecifiedLayout()
    {
        Assert.Equal(32, AesGcmCrypto.KeyBytes);
        Assert.Equal(12, AesGcmCrypto.NonceBytes);
        Assert.Equal(16, AesGcmCrypto.TagBytes);
        Assert.Equal("ACV1"u8.ToArray(), AesGcmCrypto.Magic);
    }

    [Fact]
    public void Encrypt_NonEmptyPlaintext_CiphertextDiffersFromPlaintext()
    {
        var plaintext = Encoding.UTF8.GetBytes("{\"Boards\":[{\"Name\":\"Memes\"}]} RIFF....WAVEfmt ");

        var encrypted = AesGcmCrypto.Encrypt(TestBytes.RandomKey(), plaintext);

        Assert.NotEqual(plaintext, encrypted[HeaderBytes..]);
        Assert.False(TestBytes.Contains(encrypted, Encoding.UTF8.GetBytes("Boards")));
        Assert.False(TestBytes.Contains(encrypted, Encoding.ASCII.GetBytes("RIFF")));
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        var plaintext = TestBytes.Deterministic(4096);
        var encrypted = AesGcmCrypto.Encrypt(TestBytes.RandomKey(), plaintext);

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(TestBytes.RandomKey(), encrypted));
    }

    [Fact]
    public void Decrypt_KeyDifferingInOneBit_ThrowsCryptographicException()
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(100));
        var nearlySameKey = (byte[])key.Clone();
        nearlySameKey[31] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(nearlySameKey, encrypted));
    }

    [Fact]
    public void Decrypt_WrongKeyOnEmptyPlaintext_ThrowsCryptographicException()
    {
        // Even with no ciphertext at all, the tag must authenticate the (empty) message.
        var encrypted = AesGcmCrypto.Encrypt(TestBytes.RandomKey(), Array.Empty<byte>());

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(TestBytes.RandomKey(), encrypted));
    }

    [Theory]
    [InlineData(0, 0)]      // first ciphertext byte, lowest bit
    [InlineData(0, 7)]      // first ciphertext byte, highest bit
    [InlineData(2047, 3)]   // middle
    [InlineData(4095, 0)]   // last ciphertext byte
    public void Decrypt_SingleFlippedCiphertextBit_ThrowsCryptographicException(int ciphertextIndex, int bit)
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(4096));
        encrypted[HeaderBytes + ciphertextIndex] ^= (byte)(1 << bit);

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(key, encrypted));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 4)]
    [InlineData(15, 7)]
    public void Decrypt_SingleFlippedTagBit_ThrowsCryptographicException(int tagIndex, int bit)
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(512));
        encrypted[4 + 12 + tagIndex] ^= (byte)(1 << bit);

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(key, encrypted));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Decrypt_SingleFlippedNonceBit_ThrowsCryptographicException(int nonceIndex)
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(512));
        encrypted[4 + nonceIndex] ^= 0x10;

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(key, encrypted));
    }

    [Fact]
    public void Decrypt_FlippedTagBitOnEmptyPlaintext_ThrowsCryptographicException()
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, Array.Empty<byte>());
        encrypted[^1] ^= 0x01; // last byte of the tag (no ciphertext follows)

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(key, encrypted));
    }

    [Fact]
    public void Decrypt_CiphertextTruncatedByOneByte_ThrowsCryptographicException()
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(1000));

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(key, encrypted[..^1]));
    }

    [Fact]
    public void Decrypt_ExtraTrailingByte_ThrowsCryptographicException()
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(1000));
        var extended = encrypted.Concat(new byte[] { 0x00 }).ToArray();

        Assert.ThrowsAny<CryptographicException>(() => AesGcmCrypto.Decrypt(key, extended));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(31)] // one byte short of magic + nonce + tag
    public void Decrypt_TruncatedBelowHeaderSize_ThrowsInvalidDataException(int length)
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(64));

        Assert.Throws<InvalidDataException>(() => AesGcmCrypto.Decrypt(key, encrypted[..length]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Decrypt_CorruptedMagicByte_ThrowsInvalidDataException(int magicIndex)
    {
        var key = TestBytes.RandomKey();
        var encrypted = AesGcmCrypto.Encrypt(key, TestBytes.Deterministic(64));
        encrypted[magicIndex] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => AesGcmCrypto.Decrypt(key, encrypted));
    }

    [Fact]
    public void Decrypt_PlainRiffWavFile_ThrowsInvalidDataException()
    {
        var wav = TestWav.CanonicalWavBytes(frames: 480);

        Assert.Throws<InvalidDataException>(() => AesGcmCrypto.Decrypt(TestBytes.RandomKey(), wav));
    }

    [Fact]
    public void Encrypt_SamePlaintextTwice_ProducesDifferentNoncesAndCiphertexts_BothDecrypt()
    {
        var key = TestBytes.RandomKey();
        var plaintext = TestBytes.Deterministic(2048);

        var first = AesGcmCrypto.Encrypt(key, plaintext);
        var second = AesGcmCrypto.Encrypt(key, plaintext);

        Assert.NotEqual(first[4..16], second[4..16]);           // nonce
        Assert.NotEqual(first[HeaderBytes..], second[HeaderBytes..]); // ciphertext
        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, AesGcmCrypto.Decrypt(key, first));
        Assert.Equal(plaintext, AesGcmCrypto.Decrypt(key, second));
    }

    [Fact]
    public void Encrypt_ManyCalls_NeverRepeatsANonce()
    {
        var key = TestBytes.RandomKey();
        var plaintext = new byte[16];
        var nonces = new HashSet<string>();

        for (int i = 0; i < 1000; i++)
        {
            var encrypted = AesGcmCrypto.Encrypt(key, plaintext);
            Assert.True(nonces.Add(Convert.ToHexString(encrypted, 4, 12)), $"Nonce repeated on call {i}.");
        }
    }

    [Fact]
    public void Encrypt_DoesNotModifyInputs()
    {
        var key = TestBytes.RandomKey();
        var keyCopy = (byte[])key.Clone();
        var plaintext = TestBytes.Deterministic(300);
        var plaintextCopy = (byte[])plaintext.Clone();

        _ = AesGcmCrypto.Encrypt(key, plaintext);

        Assert.Equal(keyCopy, key);
        Assert.Equal(plaintextCopy, plaintext);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void EncryptAndDecrypt_KeyNot32Bytes_ThrowArgumentException(int keyLength)
    {
        var badKey = new byte[keyLength];
        var valid = AesGcmCrypto.Encrypt(TestBytes.RandomKey(), new byte[8]);

        Assert.Throws<ArgumentException>(() => AesGcmCrypto.Encrypt(badKey, new byte[8]));
        Assert.Throws<ArgumentException>(() => AesGcmCrypto.Decrypt(badKey, valid));
    }

    [Fact]
    public void EncryptAndDecrypt_NullArguments_ThrowArgumentNullException()
    {
        var key = TestBytes.RandomKey();

        Assert.Throws<ArgumentNullException>(() => AesGcmCrypto.Encrypt(null!, new byte[1]));
        Assert.Throws<ArgumentNullException>(() => AesGcmCrypto.Encrypt(key, null!));
        Assert.Throws<ArgumentNullException>(() => AesGcmCrypto.Decrypt(null!, new byte[40]));
        Assert.Throws<ArgumentNullException>(() => AesGcmCrypto.Decrypt(key, null!));
    }
}
