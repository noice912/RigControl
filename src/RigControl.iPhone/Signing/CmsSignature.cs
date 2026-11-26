using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>
/// The signature blob (slot 0x10000, magic 0xfade0b01): a detached CMS SignedData over the first CodeDirectory,
/// made with the developer certificate's private key. Like codesign it carries two Apple attributes that bind
/// every CodeDirectory (the SHA-1 one and the SHA-256 alternate) to the signature:
///   1.2.840.113635.100.9.1  an XML plist { cdhashes = [ each CD hash, truncated to 20 bytes ] }
///   1.2.840.113635.100.9.2  SEQUENCE { digest OID, OCTET STRING full CD hash } per CodeDirectory
/// </summary>
internal sealed class CmsSignature
{
    public const string CdHashesPlistOid = "1.2.840.113635.100.9.1";
    public const string CdHashesOid = "1.2.840.113635.100.9.2";
    private const string Sha1Oid = "1.3.14.3.2.26";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    private readonly X509Certificate2 _certificate;
    private readonly IReadOnlyList<X509Certificate2> _chain;
    private int _sizeEstimate = -1;

    public CmsSignature(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain)
    {
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The signing certificate has no private key.", nameof(certificate));
        _certificate = certificate;
        _chain = chain;
    }

    /// <summary>Space to reserve for the blob. Measured once by signing a dummy CodeDirectory, plus slack.</summary>
    public int SizeEstimate
    {
        get
        {
            if (_sizeEstimate < 0)
            {
                var dummy = new byte[CodeDirectory.HeaderSize];
                _sizeEstimate = Sign([(CsMagic.HashSha1, dummy), (CsMagic.HashSha256, dummy)]).Length + 512;
            }
            return _sizeEstimate;
        }
    }

    /// <summary>
    /// Signs the first CodeDirectory; all of them (in superblob order) go into the cdhash attributes.
    /// Returns the wrapped blob ready for the superblob.
    /// </summary>
    public byte[] Sign(IReadOnlyList<(byte HashType, byte[] Blob)> codeDirectories)
    {
        var cdHashes = new List<object>();
        var agility = new AsnEncodedDataCollection();
        foreach (var (type, blob) in codeDirectories)
        {
            var hash = CodeDirectory.Hash(type, blob);
            cdHashes.Add(hash[..20]);
            var w = new AsnWriter(AsnEncodingRules.DER);
            w.PushSequence();
            w.WriteObjectIdentifier(type == CsMagic.HashSha1 ? Sha1Oid : Sha256Oid);
            w.WriteOctetString(hash);
            w.PopSequence();
            agility.Add(new AsnEncodedData(CdHashesOid, w.Encode()));
        }
        var plist = new PlistDict { ["cdhashes"] = cdHashes };
        var plistOctets = new AsnWriter(AsnEncodingRules.DER);
        plistOctets.WriteOctetString(PlistFile.ToXml(plist));

        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _certificate)
        {
            DigestAlgorithm = new Oid(Sha256Oid),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        foreach (var cert in _chain)
            signer.Certificates.Add(cert);
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        signer.SignedAttributes.Add(new AsnEncodedData(CdHashesPlistOid, plistOctets.Encode()));
        signer.SignedAttributes.Add(new CryptographicAttributeObject(new Oid(CdHashesOid), agility));

        var cms = new SignedCms(new ContentInfo(codeDirectories[0].Blob), detached: true);
        cms.ComputeSignature(signer, silent: true);
        return CsMagic.Wrap(CsMagic.BlobWrapper, cms.Encode());
    }
}
