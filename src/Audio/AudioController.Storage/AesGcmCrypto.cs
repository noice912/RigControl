using System.Security.Cryptography;

namespace AudioController.Storage;

/// <summary>
/// AES-256-GCM authenticated encryption for every file in the vault.
/// </summary>
/// <remarks>
/// Encrypted layout: <c>[magic "ACV1" 4 bytes][nonce 12 bytes][tag 16 bytes][ciphertext N bytes]</c>.
/// A fresh random nonce is generated for every call to <see cref="Encrypt"/>. Decryption either
/// returns the complete, authenticated plaintext or throws; it never returns partial data.
/// </remarks>
public static class AesGcmCrypto
{
    /// <summary>Key length in bytes (AES-256).</summary>
    public const int KeyBytes = 32;

    /// <summary>GCM nonce length in bytes.</summary>
    public const int NonceBytes = 12;

    /// <summary>GCM authentication tag length in bytes.</summary>
    public const int TagBytes = 16;

    /// <summary>File signature, ASCII "ACV1". Exposed for callers that inspect files on disk.</summary>
    public static readonly byte[] Magic = { 0x41, 0x43, 0x56, 0x31 };

    // Private immutable copy so a caller mutating the public array can never change the format.
    private static ReadOnlySpan<byte> MagicSignature => "ACV1"u8;

    private const int MagicBytes = 4;
    private const int NonceOffset = MagicBytes;
    private const int TagOffset = NonceOffset + NonceBytes;
    private const int HeaderBytes = TagOffset + TagBytes;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with a new random nonce.
    /// </summary>
    /// <param name="key">The 32-byte AES-256 key.</param>
    /// <param name="plaintext">The data to encrypt. May be empty.</param>
    /// <returns>The encrypted file bytes: magic, nonce, tag, ciphertext.</returns>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes.</exception>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(plaintext);

        var output = new byte[HeaderBytes + plaintext.Length];
        var outputSpan = output.AsSpan();

        MagicSignature.CopyTo(outputSpan[..MagicBytes]);

        var nonce = outputSpan.Slice(NonceOffset, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        var tag = outputSpan.Slice(TagOffset, TagBytes);
        var ciphertext = outputSpan[HeaderBytes..];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return output;
    }

    /// <summary>
    /// Verifies and decrypts bytes produced by <see cref="Encrypt"/>.
    /// </summary>
    /// <param name="key">The 32-byte AES-256 key.</param>
    /// <param name="fileBytes">The full encrypted file contents.</param>
    /// <returns>The authenticated plaintext.</returns>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes.</exception>
    /// <exception cref="InvalidDataException">The data is truncated or does not start with the "ACV1" signature.</exception>
    /// <exception cref="CryptographicException">Authentication failed: wrong key or the data was altered.</exception>
    public static byte[] Decrypt(byte[] key, byte[] fileBytes)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(fileBytes);

        if (fileBytes.Length < HeaderBytes)
        {
            throw new InvalidDataException(
                $"Encrypted data is truncated: {fileBytes.Length} bytes, but at least {HeaderBytes} bytes " +
                "(signature, nonce and authentication tag) are required.");
        }

        ReadOnlySpan<byte> input = fileBytes;

        if (!input[..MagicBytes].SequenceEqual(MagicSignature))
        {
            throw new InvalidDataException(
                "Encrypted data has an unrecognized signature (expected \"ACV1\"). " +
                "The file is not an Audio Controller vault file or it has been damaged.");
        }

        var nonce = input.Slice(NonceOffset, NonceBytes);
        var tag = input.Slice(TagOffset, TagBytes);
        var ciphertext = input[HeaderBytes..];

        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch
        {
            // AesGcm already clears the destination on tag mismatch; clear again so no
            // unauthenticated bytes can survive any failure path.
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }

        return plaintext;
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeyBytes)
        {
            throw new ArgumentException(
                $"The key must be exactly {KeyBytes} bytes for AES-256; got {key.Length} bytes.",
                nameof(key));
        }
    }
}
