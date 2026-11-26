using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Link;

/// <summary>AES-256-GCM sealing (12-byte nonce in front, 16-byte tag at the end) and the key derivations.</summary>
public static class LinkCrypto
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;

    /// <summary>nonce || ciphertext || tag.</summary>
    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        if (key.Length != KeySize) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        if (nonce.Length != NonceSize) throw new ArgumentException("The nonce must be 12 bytes.", nameof(nonce));
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.CopyTo(output);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length, TagSize), aad);
        return output;
    }

    /// <summary>Seals with a fresh random nonce.</summary>
    public static byte[] SealRandom(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return Seal(key, nonce, plaintext, aad);
    }

    /// <summary>Opens what <see cref="Seal"/> made. Throws <see cref="LinkException"/> ("sealed") when it was changed or the key is wrong.</summary>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> aad)
    {
        if (key.Length != KeySize) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        if (sealedData.Length < NonceSize + TagSize) throw new LinkException("sealed", "The sealed data is too short.");
        var ctLen = sealedData.Length - NonceSize - TagSize;
        var plain = new byte[ctLen];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(sealedData[..NonceSize], sealedData.Slice(NonceSize, ctLen), sealedData.Slice(NonceSize + ctLen, TagSize), plain, aad);
        }
        catch (CryptographicException ex)
        {
            throw new LinkException("sealed", "The sealed data could not be opened (wrong key, or it was changed).", ex);
        }
        return plain;
    }

    /// <summary>
    /// The session key of a (PC, phone) pairing: HKDF-SHA256(ikm = ECDH(my agreement key, peer agreement key),
    /// salt = the two ids sorted (ordinal) joined by '|', then '|' + epoch, info = "rigcontrol-link-v1 session",
    /// 32 bytes). The same on both ends; a new pairing (new epoch) gives a new key.
    /// </summary>
    public static byte[] SessionKey(DeviceKeys mine, byte[] peerAgreementSpki, string peerId, string epoch)
    {
        if (!LinkProtocol.IsEpoch(epoch)) throw new LinkException("badepoch", "The pairing epoch is missing or malformed.");
        using var peer = DeviceKeys.ImportAgreementPublic(peerAgreementSpki);
        var z = mine.Agreement.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            var ids = new[] { mine.Id, peerId };
            Array.Sort(ids, StringComparer.Ordinal);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, z, KeySize,
                LinkProtocol.Utf8(string.Join("|", ids) + "|" + epoch), LinkProtocol.Utf8(LinkProtocol.SessionInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(z);
        }
    }

    /// <summary>Seals a JSON object with the pairing key K (no AAD): base64url(nonce || ciphertext || tag).</summary>
    public static string SealBox(byte[] boxKey, JsonObject content)
        => B64Url.Encode(SealRandom(boxKey, JsonSerializer.SerializeToUtf8Bytes(content), ReadOnlySpan<byte>.Empty));

    /// <summary>Opens a pairing box. Throws <see cref="LinkException"/> ("wrongcode") when K does not open it.</summary>
    public static JsonObject OpenBox(byte[] boxKey, string box)
    {
        if (!B64Url.TryDecode(box, out var data)) throw new LinkException("badbox", "The pairing answer was not readable.");
        byte[] plain;
        try { plain = Open(boxKey, data, ReadOnlySpan<byte>.Empty); }
        catch (LinkException ex) { throw new LinkException("wrongcode", "The code did not match. Check it and try again.", ex); }
        try
        {
            return JsonNode.Parse(plain) as JsonObject ?? throw new LinkException("badbox", "The pairing answer was not readable.");
        }
        catch (JsonException ex)
        {
            throw new LinkException("badbox", "The pairing answer was not readable.", ex);
        }
    }

    internal static void WriteCounter(Span<byte> nonce, byte direction, ulong counter)
    {
        nonce[0] = direction;
        RandomNumberGenerator.Fill(nonce.Slice(1, 3));
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), counter);
    }

    internal static (byte Direction, ulong Counter) ReadCounter(ReadOnlySpan<byte> nonce)
        => (nonce[0], BinaryPrimitives.ReadUInt64BigEndian(nonce.Slice(4, 8)));
}
