using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Signing;

namespace RigControl.iPhone.Apple;

/// <summary>
/// Apple's sign-in servers (gsa.apple.com) use certificates from Apple's own "Apple Root CA", which Windows does not
/// trust for websites. Apple's apps trust it themselves; so does RigControl, but only for apple.com hosts and only that
/// one root (pinned by its published SHA-256). Everything else is checked exactly as usual: the host name must match,
/// dates must be valid, the chain must build to that root, and the certificate must be for server authentication.
/// Any other host gets plain Windows validation.
/// </summary>
public static class AppleTls
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>An HTTP handler for talking to Apple (no redirects, Apple's root trusted for apple.com only).</summary>
    public static SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            Validate((sender as SslStream)?.TargetHostName, cert as X509Certificate2 ?? (cert is null ? null : new X509Certificate2(cert)),
                chain, errors, AppleCertificates.Root);
        return handler;
    }

    /// <summary>The decision, separate so tests can give their own root.</summary>
    public static bool Validate(string? host, X509Certificate2? certificate, X509Chain? presented, SslPolicyErrors errors,
        X509Certificate2 appleRoot)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        // A wrong name or no certificate is never accepted, whoever issued it.
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        if (host is null || !(host.Equals("apple.com", StringComparison.OrdinalIgnoreCase)
                              || host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(appleRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(ServerAuthOid));
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        if (presented is not null)
            foreach (var element in presented.ChainElements)
                if (!element.Certificate.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        if (!chain.Build(certificate))
            return false;
        // The chain must really end at the pinned root, not a look-alike.
        var top = chain.ChainElements[^1].Certificate;
        return top.RawDataMemory.Span.SequenceEqual(appleRoot.RawDataMemory.Span);
    }
}
