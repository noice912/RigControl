using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Link;

/// <summary>
/// A device's two long-term keys (both P-256): the signing key (ECDSA) and the agreement key (ECDH).
/// The id is base64url(SHA-256(signing public key as SubjectPublicKeyInfo DER)). A PC's room is its id.
/// </summary>
public sealed class DeviceKeys : IDisposable
{
    private readonly ECDsa _signing;
    private readonly ECDiffieHellman _agreement;

    private DeviceKeys(ECDsa signing, ECDiffieHellman agreement)
    {
        _signing = signing;
        _agreement = agreement;
        SigningPublic = signing.ExportSubjectPublicKeyInfo();
        AgreementPublic = agreement.ExportSubjectPublicKeyInfo();
        Id = IdOf(SigningPublic);
    }

    /// <summary>Signing public key, SubjectPublicKeyInfo DER.</summary>
    public byte[] SigningPublic { get; }

    /// <summary>Agreement public key, SubjectPublicKeyInfo DER.</summary>
    public byte[] AgreementPublic { get; }

    public string Id { get; }

    public string SigningPublicB64 => B64Url.Encode(SigningPublic);
    public string AgreementPublicB64 => B64Url.Encode(AgreementPublic);

    internal ECDiffieHellman Agreement => _agreement;

    /// <summary>Makes a new pair of keys.</summary>
    public static DeviceKeys Create()
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The id of a device from its signing public key (SPKI DER).</summary>
    public static string IdOf(byte[] signingSpki) => B64Url.Encode(SHA256.HashData(signingSpki));

    /// <summary>
    /// Both private keys as bytes (a small JSON object holding PKCS#8 DER). These bytes are secret: the caller must
    /// protect them before storing them (DPAPI, Keychain, Keystore).
    /// </summary>
    public byte[] ExportPrivate()
    {
        var o = new JsonObject
        {
            ["v"] = 1,
            ["sign"] = B64Url.Encode(_signing.ExportPkcs8PrivateKey()),
            ["agree"] = B64Url.Encode(_agreement.ExportPkcs8PrivateKey()),
        };
        return JsonSerializer.SerializeToUtf8Bytes(o);
    }

    /// <summary>Reads what <see cref="ExportPrivate"/> made. Throws <see cref="LinkException"/> ("badkey") when it cannot.</summary>
    public static DeviceKeys ImportPrivate(byte[] data)
    {
        ECDsa? s = null;
        ECDiffieHellman? a = null;
        try
        {
            var o = JsonNode.Parse(data) as JsonObject ?? throw new FormatException();
            if ((int?)o["v"] != 1) throw new FormatException();
            s = ECDsa.Create();
            s.ImportPkcs8PrivateKey(B64Url.Decode((string)o["sign"]!), out _);
            a = ECDiffieHellman.Create();
            a.ImportPkcs8PrivateKey(B64Url.Decode((string)o["agree"]!), out _);
            if (!IsP256(s.ExportParameters(false).Curve) || !IsP256(a.ExportParameters(false).Curve)) throw new FormatException();
            return new DeviceKeys(s, a);
        }
        catch (Exception ex) when (ex is not LinkException)
        {
            s?.Dispose();
            a?.Dispose();
            throw new LinkException("badkey", "The stored link keys could not be read.", ex);
        }
    }

    /// <summary>ECDSA P-256 SHA-256 over the UTF-8 text, IEEE P1363 (r||s), base64url.</summary>
    public string Sign(string text)
        => B64Url.Encode(_signing.SignData(LinkProtocol.Utf8(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>Checks a signature made by <see cref="Sign"/> with the given signing public key. Never throws.</summary>
    public static bool Verify(byte[] signingSpki, string text, string signatureB64)
    {
        try
        {
            if (!B64Url.TryDecode(signatureB64, out var sig) || sig.Length != 64) return false;
            using var e = ImportSigningPublic(signingSpki);
            return e.VerifyData(LinkProtocol.Utf8(text), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Imports a peer's signing key; throws <see cref="LinkException"/> ("badkey") unless it is a P-256 key.</summary>
    internal static ECDsa ImportSigningPublic(byte[] spki)
    {
        var e = ECDsa.Create();
        try
        {
            e.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || !IsP256(e.ExportParameters(false).Curve)) throw new FormatException();
            return e;
        }
        catch (Exception ex)
        {
            e.Dispose();
            throw new LinkException("badkey", "That is not a P-256 public key.", ex);
        }
    }

    /// <summary>Imports a peer's agreement key; throws <see cref="LinkException"/> ("badkey") unless it is a P-256 key.</summary>
    internal static ECDiffieHellman ImportAgreementPublic(byte[] spki)
    {
        var e = ECDiffieHellman.Create();
        try
        {
            e.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || !IsP256(e.ExportParameters(false).Curve)) throw new FormatException();
            return e;
        }
        catch (Exception ex)
        {
            e.Dispose();
            throw new LinkException("badkey", "That is not a P-256 public key.", ex);
        }
    }

    /// <summary>True for a valid P-256 public key in SPKI DER (either kind; both use id-ecPublicKey).</summary>
    public static bool IsValidPublicKey(byte[] spki)
    {
        try
        {
            using var _ = ImportSigningPublic(spki);
            return true;
        }
        catch (LinkException)
        {
            return false;
        }
    }

    private static bool IsP256(ECCurve curve)
    {
        if (!curve.IsNamed) return false;
        var oid = curve.Oid;
        if (oid.Value == "1.2.840.10045.3.1.7") return true;
        return oid.FriendlyName is "nistP256" or "ECDSA_P256" or "ECDH_P256" or "secp256r1" or "prime256v1";
    }

    public void Dispose()
    {
        _signing.Dispose();
        _agreement.Dispose();
    }
}
