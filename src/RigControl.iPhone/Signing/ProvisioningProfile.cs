using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>
/// A provisioning profile (.mobileprovision): CMS SignedData whose content is an XML property list. Only the
/// parts signing needs are read. The CMS signature is Apple's; the iPhone checks it, so it is not checked here.
/// </summary>
internal sealed class ProvisioningProfile
{
    public const int MaxSize = 4 << 20;

    public required byte[] Raw { get; init; }
    public required PlistDict Content { get; init; }
    public required string TeamId { get; init; }
    public required PlistDict Entitlements { get; init; }

    public string? Name => Content.String("Name");
    public DateTime? ExpirationDate => Content.Date("ExpirationDate");

    public IEnumerable<byte[]> DeveloperCertificates =>
        Content.Array("DeveloperCertificates")?.OfType<byte[]>() ?? [];

    public static ProvisioningProfile Parse(byte[] raw)
    {
        if (raw.Length is 0 or > MaxSize)
            throw new InvalidDataException("The provisioning profile is empty or too large.");
        byte[] content;
        try
        {
            var cms = new SignedCms();
            cms.Decode(raw);
            content = cms.ContentInfo.Content;
        }
        catch (CryptographicException e)
        {
            throw new InvalidDataException("The provisioning profile is not valid CMS data.", e);
        }
        var plist = PlistFile.ParseDict(content);
        var entitlements = plist.Dict("Entitlements")
            ?? throw new InvalidDataException("The provisioning profile has no Entitlements.");
        var team = plist.Array("TeamIdentifier")?.OfType<string>().FirstOrDefault()
            ?? plist.Array("ApplicationIdentifierPrefix")?.OfType<string>().FirstOrDefault()
            ?? entitlements.String("com.apple.developer.team-identifier")
            ?? throw new InvalidDataException("The provisioning profile names no team.");
        return new ProvisioningProfile { Raw = raw, Content = plist, TeamId = team, Entitlements = entitlements };
    }

    public bool IncludesCertificate(X509Certificate2 certificate)
    {
        var der = certificate.RawData;
        return DeveloperCertificates.Any(c => c.AsSpan().SequenceEqual(der));
    }

    /// <summary>
    /// The entitlements to sign <paramref name="bundleId"/> with: the profile's own, with a wildcard
    /// application-identifier narrowed to this bundle. An explicit identifier for a different bundle is refused,
    /// since the iPhone would refuse the app.
    /// </summary>
    public PlistDict EntitlementsFor(string bundleId)
    {
        var result = MachOSigner.SortedCopy(Entitlements);
        var expected = $"{TeamId}.{bundleId}";
        var appId = result.String("application-identifier");
        if (appId == null || appId.EndsWith('*'))
        {
            if (appId != null && !expected.StartsWith(appId[..^1], StringComparison.Ordinal))
                throw new InvalidDataException($"The profile's app id {appId} does not cover {bundleId}.");
            result["application-identifier"] = expected;
        }
        else if (appId != expected)
        {
            throw new InvalidDataException($"The profile is for {appId}, not {expected}.");
        }
        if (!result.ContainsKey("com.apple.developer.team-identifier"))
            result["com.apple.developer.team-identifier"] = TeamId;
        return MachOSigner.SortedCopy(result);
    }
}
