using System.Security.Cryptography;

namespace RigControl.Updates;

/// <summary>
/// The public keys this build carries. Both are PUBLIC: they can only check signatures, never make them.
/// The matching private keys are never in any repository, the app or a release.
/// </summary>
public static class TrustedKeys
{
    /// <summary>Checks update.json.sig on every release (ECDSA P-256, SubjectPublicKeyInfo, base64).</summary>
    public const string UpdatePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwKWuCeHDKEMMq5iYQs6+CY2JKeiHlGfrHuptQfw46xEqJWIf41WKcRNZL3XfGnNYpQOPGR0MscPfHhZaowBqlg==";

    /// <summary>Checks dev keys (RCDEV1...). Same as private/dev-key-issuer.pub.pem.</summary>
    public const string DevKeyIssuerPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEu7FfjH3wurbD1yIFf6usgzo8EZvxeozVo+DJVIWoQFupI8CaV5FdbqXLILyJtnFmfJpTvdThrBt4KXV3n2wthQ==";
}

/// <summary>
/// ECDSA P-256 with SHA-256, signatures in IEEE P1363 form (r and s, 32 bytes each). The one implementation used by
/// the app (checking) and by the signer (making), so the two can never disagree about the format.
/// </summary>
public static class EcSignature
{
    public const int SignatureBytes = 64;

    /// <summary>Signs <paramref name="data"/> and returns the signature as base64.</summary>
    public static string Sign(ReadOnlySpan<byte> data, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        RequireP256(privateKey);
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    /// <summary>True only when <paramref name="signature"/> is a valid signature of exactly these bytes by this public key.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKeyBase64)
    {
        if (signature.Length != SignatureBytes) return false;
        try
        {
            using var key = ImportPublicKey(publicKeyBase64);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The same, for a signature written as base64 (update.json.sig). Anything that is not base64 fails.</summary>
    public static bool VerifyBase64(ReadOnlySpan<byte> data, string? signatureBase64, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        return Verify(data, signature, publicKeyBase64);
    }

    /// <summary>A public key given as base64 of its SubjectPublicKeyInfo, or as a PEM "PUBLIC KEY" block.</summary>
    public static ECDsa ImportPublicKey(string publicKey)
    {
        var key = ECDsa.Create();
        try
        {
            if (publicKey.Contains("-----BEGIN", StringComparison.Ordinal)) key.ImportFromPem(publicKey);
            else key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            RequireP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads a private key from PEM text (PKCS#8 "PRIVATE KEY" or "EC PRIVATE KEY"). The text is never logged or
    /// repeated in a message. A key protected with a password is refused with a plain sentence.
    /// </summary>
    public static ECDsa ImportPrivateKeyPem(string pem)
    {
        if (pem.Contains("ENCRYPTED", StringComparison.Ordinal))
            throw new CryptographicException("The signing key file is protected with a password. Use the key without a password.");
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            RequireP256(key);
            // A file holding only a public key imports fine but cannot sign.
            _ = key.ExportParameters(includePrivateParameters: true);
            return key;
        }
        catch (ArgumentException)
        {
            key.Dispose();
            throw new CryptographicException("The signing key file does not hold an ECDSA P-256 private key in PEM form.");
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The public half of a key, as base64 of its SubjectPublicKeyInfo (the form <see cref="TrustedKeys"/> uses).</summary>
    public static string PublicKeyBase64(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static void RequireP256(ECDsa key)
    {
        if (key.KeySize != 256)
            throw new CryptographicException("The key is not an ECDSA P-256 key.");
    }
}
