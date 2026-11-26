using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RigControl.iPhone.Signing;

/// <summary>
/// Apple's public CA certificates that codesign puts in every signature next to the developer certificate:
/// the Worldwide Developer Relations G3 intermediate (issuer of "Apple Development" certificates) and the
/// Apple Root CA. Both were downloaded from apple.com/certificateauthority on 2026-11-26; the root's SHA-256
/// is Apple's published fingerprint, the G3 certificate verifies under it, and both matched the copies bundled
/// with the open-source apple-codesign project. The pinned hashes are checked again every time they load.
/// </summary>
internal static class AppleCertificates
{
    private const string WwdrG3Sha256 = "DCF21878C77F4198E4B4614F03D696D89C66C66008D4244E1B99161AAC91601F";
    private const string RootSha256 = "B0B1730ECBC7FF4505142C49F1295E6EDA6BCAED7E2C68C5BE91B5A11001F024";

    private static readonly Lazy<IReadOnlyList<X509Certificate2>> Chain = new(() =>
    [
        Load("AppleWWDRCAG3.cer", WwdrG3Sha256),
        Load("AppleIncRootCertificate.cer", RootSha256),
    ]);

    /// <summary>WWDR G3 then Apple Root CA.</summary>
    public static IReadOnlyList<X509Certificate2> DefaultChain => Chain.Value;

    /// <summary>Apple Root CA (pinned): also the root of Apple's sign-in servers' TLS certificates.</summary>
    public static X509Certificate2 Root => Chain.Value[1];

    private static X509Certificate2 Load(string name, string sha256)
    {
        var resource = $"RigControl.iPhone.Signing.AppleCertificates.{name}";
        using var stream = typeof(AppleCertificates).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Built-in certificate {name} is missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"Built-in certificate {name} does not match its pinned hash.");
        return new X509Certificate2(bytes);
    }
}
