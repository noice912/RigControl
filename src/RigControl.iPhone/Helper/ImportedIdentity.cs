using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Apple;
using RigControl.iPhone.Signing;

namespace RigControl.iPhone.Helper;

/// <summary>What an imported signing identity is good for, shown in the installer window. Nothing secret.</summary>
public sealed record ImportedIdentityInfo(
    string CertificateName,
    string TeamId,
    string? ProfileName,
    string AppId,
    DateTimeOffset ProfileExpires,
    DateTimeOffset CertificateExpires,
    IReadOnlyList<string> Devices,
    bool AllDevices)
{
    /// <summary>When an app signed with this identity stops opening: the profile or the certificate, whichever is first.</summary>
    public DateTimeOffset Expires => ProfileExpires < CertificateExpires ? ProfileExpires : CertificateExpires;

    public bool Covers(string udid) =>
        AllDevices || Devices.Any(d => string.Equals(d, udid, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A signing certificate (.p12) and provisioning profile (.mobileprovision) the person brings themselves, for when
/// Apple won't give a free developer token to this PC. The .p12 password is used once to open the file and never kept;
/// the certificate and its private key are stored re-exported (no password) and encrypted with the given protector
/// (DPAPI for the current Windows user in the app). The profile is not secret and is stored as it is.
/// </summary>
public sealed class ImportedIdentityStore(string folder, ISecretProtector protector)
{
    private const string KeyFile = "certificate.p12.protected";
    private const string ProfileFile = "profile.mobileprovision";
    private const int MaxP12Bytes = 1 << 20;

    private string KeyPath => Path.Combine(folder, KeyFile);
    private string ProfilePath => Path.Combine(folder, ProfileFile);

    /// <summary>
    /// Checks the pair and stores it. <paramref name="password"/> is wiped whatever happens. Throws
    /// <see cref="InvalidDataException"/> with a plain message when the pair can't be used.
    /// </summary>
    public ImportedIdentityInfo Import(byte[] p12, char[] password, byte[] profileBytes, DateTimeOffset? now = null)
    {
        byte[]? export = null;
        try
        {
            if (p12.Length is 0 or > MaxP12Bytes)
                throw new InvalidDataException("That .p12 file is empty or too large.");
            using var certificate = OpenP12(p12, password);
            var info = Check(certificate, profileBytes, now ?? DateTimeOffset.UtcNow);

            // Stored without the person's password, encrypted for this Windows user instead.
            export = certificate.Export(X509ContentType.Pkcs12);
            var sealedKey = protector.Protect(export);
            Directory.CreateDirectory(folder);
            WriteAtomic(KeyPath, sealedKey);
            WriteAtomic(ProfilePath, profileBytes);
            return info;
        }
        finally
        {
            Array.Clear(password);
            if (export is not null)
                CryptographicOperations.ZeroMemory(export);
        }
    }

    /// <summary>The stored pair's details, or null when none is stored or it can no longer be read.</summary>
    public ImportedIdentityInfo? Info()
    {
        var loaded = Load();
        if (loaded is null)
            return null;
        using (loaded.Value.Certificate)
            return Describe(loaded.Value.Certificate, ProvisioningProfile.Parse(loaded.Value.Profile));
    }

    /// <summary>The certificate (with its private key; the caller disposes it) and the profile bytes, or null.</summary>
    public (X509Certificate2 Certificate, byte[] Profile)? Load()
    {
        if (!File.Exists(KeyPath) || !File.Exists(ProfilePath))
            return null;
        byte[]? plain = null;
        try
        {
            plain = protector.Unprotect(File.ReadAllBytes(KeyPath));
            var certificate = new X509Certificate2(plain, (string?)null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                return null;
            }
            return (certificate, File.ReadAllBytes(ProfilePath));
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        foreach (var path in new[] { KeyPath, ProfilePath })
            if (File.Exists(path))
                File.Delete(path);
    }

    /// <summary>
    /// The bundle id the app gets when signed with this profile: the profile's own for an explicit App ID, or the
    /// app's original id under a wildcard one.
    /// </summary>
    public static string BundleIdFor(ImportedIdentityInfo info, string originalMainBundleId)
    {
        var prefix = info.TeamId + ".";
        var appId = info.AppId.StartsWith(prefix, StringComparison.Ordinal) ? info.AppId[prefix.Length..] : info.AppId;
        if (appId == "*")
            return originalMainBundleId;
        if (appId.EndsWith(".*", StringComparison.Ordinal))
        {
            var stem = appId[..^1];
            return originalMainBundleId.StartsWith(stem, StringComparison.Ordinal)
                ? originalMainBundleId
                : throw new InvalidDataException($"The profile's App ID ({info.AppId}) doesn't cover {originalMainBundleId}.");
        }
        return appId;
    }

    internal static X509Certificate2 OpenP12(byte[] p12, char[] password)
    {
        var collection = new X509Certificate2Collection();
        try
        {
            collection.Import(p12, password.AsSpan(), X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException e)
        {
            throw new InvalidDataException("That .p12 file couldn't be opened: the password is wrong or the file is damaged.", e);
        }
        X509Certificate2? chosen = null;
        foreach (var c in collection)
        {
            if (chosen is null && c.HasPrivateKey)
                chosen = c;
            else
                c.Dispose();
        }
        return chosen ?? throw new InvalidDataException("That .p12 file has no certificate with its private key.");
    }

    internal static ImportedIdentityInfo Check(X509Certificate2 certificate, byte[] profileBytes, DateTimeOffset now)
    {
        ProvisioningProfile profile;
        try
        {
            profile = ProvisioningProfile.Parse(profileBytes);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            throw new InvalidDataException("That .mobileprovision file isn't a provisioning profile RigControl can read.", e);
        }
        if (profile.DeveloperCertificates.Any() && !profile.IncludesCertificate(certificate))
            throw new InvalidDataException("That provisioning profile wasn't made for this certificate. Export the profile " +
                "that lists this certificate (or the certificate the profile was made for).");
        var info = Describe(certificate, profile);
        if (info.CertificateExpires <= now)
            throw new InvalidDataException($"That certificate expired on {info.CertificateExpires.LocalDateTime:d}.");
        if (info.ProfileExpires <= now)
            throw new InvalidDataException($"That provisioning profile expired on {info.ProfileExpires.LocalDateTime:d}.");
        return info;
    }

    internal static ImportedIdentityInfo Describe(X509Certificate2 certificate, ProvisioningProfile profile)
    {
        var devices = profile.Content.Array("ProvisionedDevices")?.OfType<string>().ToList() ?? [];
        var all = profile.Content.Bool("ProvisionsAllDevices") == true;
        var expires = profile.ExpirationDate is { } d
            ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc))
            : throw new InvalidDataException("That provisioning profile has no expiry date.");
        return new ImportedIdentityInfo(
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            profile.TeamId,
            profile.Name,
            profile.Entitlements.String("application-identifier") ?? profile.TeamId + ".*",
            expires,
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
            devices,
            all);
    }

    /// <summary>Expiry of a profile file on its own (for pre-signed apps).</summary>
    public static DateTimeOffset? ProfileExpiry(byte[] profileBytes)
    {
        try
        {
            return ProvisioningProfile.Parse(profileBytes).ExpirationDate is { } d
                ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc))
                : null;
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".new";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
