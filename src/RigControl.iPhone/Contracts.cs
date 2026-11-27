using System.Security.Cryptography.X509Certificates;

// The seams between the installer's parts. Each part lives in its own folder and depends only on these and on Plist/.
//   Anisette/  Apple's device-identification headers (CoreADI on Windows)            -> IAnisetteProvider
//   Apple/     Apple ID sign-in (GSA, SRP, two-factor) and developer services         -> AppleAccountClient, DeveloperServicesClient
//   Signing/   re-signing an .ipa for a team, a device list and a profile             -> IpaSigner
//   Device/    usbmuxd, lockdown, AFC and installation_proxy over Apple's USB service -> DeviceService
//   Helper/    the non-admin helper that runs the whole flow (Installer.cs)

namespace RigControl.iPhone.Anisette
{
    /// <summary>
    /// Supplies the headers Apple's sign-in and developer servers want on every request: at least X-Apple-I-MD,
    /// X-Apple-I-MD-M, X-Apple-I-MD-RINFO, X-Apple-I-MD-LU, X-Mme-Device-Id, X-Apple-I-SRL-NO, X-Mme-Client-Info,
    /// X-Apple-I-Client-Time, X-Apple-I-TimeZone, X-Apple-Locale and X-Apple-I-Locale. A new one-time value each call.
    /// </summary>
    public interface IAnisetteProvider
    {
        Task<IReadOnlyDictionary<string, string>> GetHeadersAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A provider Apple can ask to repair itself during sign-in: status 434 hands over re-sync data ("sim") for
    /// <see cref="ResyncAsync"/>, status 433 asks for the machine to be set up again (<see cref="ReprovisionAsync"/>).
    /// </summary>
    public interface IAnisetteRepair
    {
        Task ResyncAsync(byte[] sim, CancellationToken cancellationToken);
        Task ReprovisionAsync(CancellationToken cancellationToken);
    }
}

namespace RigControl.iPhone.Apple
{
    /// <summary>Protects small secrets at rest (DPAPI for the current Windows user in the app; a fake in tests).</summary>
    public interface ISecretProtector
    {
        byte[] Protect(byte[] plain);
        byte[] Unprotect(byte[] protectedBytes);
    }

    public enum SecondFactorKind
    {
        /// <summary>A code was pushed to the person's trusted Apple devices.</summary>
        TrustedDevice,
        /// <summary>A code was sent by text message to <see cref="SecondFactorRequest.Phone"/>.</summary>
        Sms,
    }

    public sealed record TrustedPhone(long Id, string MaskedNumber);

    public sealed record SecondFactorRequest(SecondFactorKind Kind, IReadOnlyList<TrustedPhone> Phones, TrustedPhone? Phone);

    /// <summary>What the person answered: a code, or a request to send it by text instead (to <see cref="SmsPhone"/>).</summary>
    public sealed record SecondFactorAnswer(string? Code, TrustedPhone? SmsPhone = null)
    {
        public static SecondFactorAnswer WithCode(string code) => new(code);
        public static SecondFactorAnswer SendSms(TrustedPhone phone) => new(null, phone);
    }

    /// <summary>Asks the person for the two-factor code. Returns null when they cancel.</summary>
    public interface ISecondFactorPrompt
    {
        Task<SecondFactorAnswer?> AskAsync(SecondFactorRequest request, CancellationToken cancellationToken);
    }

    /// <summary>A signed-in account, enough for developer services. No password inside.</summary>
    public sealed record AppleSession(string AppleId, string Adsid, string Dsid, string XcodeToken, DateTimeOffset Expires);

    public sealed record DeveloperTeam(string Id, string Name, string Type);

    public sealed record RegisteredDevice(string Id, string Udid, string Name);

    /// <summary>The development certificate, with its private key, plus Apple's serial number for it.</summary>
    public sealed record DevelopmentCertificate(X509Certificate2 Certificate, string SerialNumber, string CertificateId);

    public sealed record AppId(string Id, string Identifier, string Name);

    public class AppleException(string message, int code = 0, Exception? inner = null) : Exception(message, inner)
    {
        /// <summary>Apple's own error code when it gave one (for example -20101 wrong password).</summary>
        public int Code { get; } = code;
    }
}

namespace RigControl.iPhone.Signing
{
    /// <summary>
    /// A request to re-sign an .ipa. <see cref="Certificate"/> carries its private key. <see cref="Profiles"/> maps
    /// each bundle id that is signed (the app and any app extensions) to the provisioning profile Apple issued for
    /// it; the main bundle's id becomes <see cref="MainBundleId"/>, and extension ids are rewritten under it.
    /// </summary>
    public sealed record SignRequest(
        string InputIpa,
        string OutputIpa,
        System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate,
        string MainBundleId,
        IReadOnlyDictionary<string, byte[]> Profiles)
    {
        /// <summary>
        /// Certificates to put in each signature after the signing certificate, issuer first. Null (the default)
        /// means Apple's Worldwide Developer Relations G3 intermediate and Apple Root CA, which ship with the app.
        /// </summary>
        public IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>? ExtraCertificates { get; init; }
    }

    /// <summary>The bundles inside an .ipa that need a profile: the app first, then its extensions.</summary>
    public sealed record BundleEntry(string Path, string OriginalBundleId, string Executable, bool IsExtension);
}

namespace RigControl.iPhone.Device
{
    public sealed record DeviceInfo(string Udid, string Name, string ProductType, string ProductVersion, bool Usb);

    public sealed record InstallProgress(string Stage, int Percent);

    public sealed class DeviceException(string message, Exception? inner = null) : Exception(message, inner);
}
