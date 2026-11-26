using System.Security.Cryptography.X509Certificates;
using System.Text;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Device;

/// <summary>
/// What Apple's service stored when the iPhone trusted this PC: the host's identity and certificate (with its key)
/// and the iPhone's certificate. Read from usbmuxd, kept in memory only, never logged (ToString shows no keys).
/// </summary>
public sealed class PairRecord
{
    public PairRecord(string hostId, string systemBuid, byte[] hostCertificate, byte[] hostPrivateKey, byte[] deviceCertificate)
    {
        HostId = hostId;
        SystemBuid = systemBuid;
        HostCertificate = hostCertificate;
        HostPrivateKey = hostPrivateKey;
        DeviceCertificate = deviceCertificate;
    }

    public string HostId { get; }
    public string SystemBuid { get; }

    /// <summary>PEM (or DER) bytes as stored in the record.</summary>
    internal byte[] HostCertificate { get; }
    internal byte[] HostPrivateKey { get; }
    internal byte[] DeviceCertificate { get; }

    /// <summary>Reads the record usbmuxd hands out. A record without the keys counts as "not trusted".</summary>
    public static PairRecord FromPlist(PlistDict d)
    {
        var hostId = d.String("HostID");
        var buid = d.String("SystemBUID");
        var hostCert = d.Data("HostCertificate");
        var hostKey = d.Data("HostPrivateKey");
        var deviceCert = d.Data("DeviceCertificate");
        if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(buid) || hostCert is null || hostKey is null || deviceCert is null)
            throw new DeviceException(DeviceErrors.NotTrusted);
        return new PairRecord(hostId, buid, hostCert, hostKey, deviceCert);
    }

    /// <summary>The host certificate with its private key, usable as a TLS client certificate. Caller disposes.</summary>
    internal X509Certificate2 CreateHostCertificate()
    {
        using var pem = X509Certificate2.CreateFromPem(AsPem(HostCertificate, "CERTIFICATE"), Encoding.ASCII.GetString(HostPrivateKey));
        // Windows' TLS (SChannel) can't use a key that only lives in memory, so it goes through a PKCS#12 round trip.
        // The key file this creates is the current user's and is deleted when the certificate is disposed.
        if (OperatingSystem.IsWindows())
            return new X509Certificate2(pem.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.UserKeySet);
        return new X509Certificate2(pem.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>The iPhone's certificate in DER form, which its TLS certificate must equal.</summary>
    internal byte[] DeviceCertificateDer()
    {
        if (DeviceCertificate.Length > 0 && DeviceCertificate[0] == 0x30)
            return DeviceCertificate;
        using var cert = X509Certificate2.CreateFromPem(AsPem(DeviceCertificate, "CERTIFICATE"));
        return cert.RawData;
    }

    private static string AsPem(byte[] bytes, string label) =>
        bytes.Length > 0 && bytes[0] == 0x30
            ? $"-----BEGIN {label}-----\n{Convert.ToBase64String(bytes)}\n-----END {label}-----\n"
            : Encoding.ASCII.GetString(bytes);

    public override string ToString() => "PairRecord (keys not shown)";
}
