using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Apple;

namespace RigControl.iPhone.Tests.Apple;

public class AppleTlsTests
{
    private static (X509Certificate2 Root, X509Certificate2 Leaf) Make(string leafName)
    {
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Test Apple Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=" + leafName, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(leafName);
        leafReq.CertificateExtensions.Add(san.Build());
        var leaf = leafReq.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), [1, 2, 3, 4]);
        return (new X509Certificate2(root.Export(X509ContentType.Cert)), leaf);
    }

    [Fact]
    public void Apples_root_is_trusted_for_apple_hosts_only()
    {
        var (root, leaf) = Make("gsa.apple.com");
        Assert.True(AppleTls.Validate("gsa.apple.com", leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, root));
        Assert.False(AppleTls.Validate("gsa.apple.com.evil.example", leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, root));
        Assert.False(AppleTls.Validate("evil.example", leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, root));
    }

    [Fact]
    public void A_name_mismatch_or_another_root_is_refused()
    {
        var (root, leaf) = Make("gsa.apple.com");
        var (otherRoot, _) = Make("x.apple.com");
        Assert.False(AppleTls.Validate("gsa.apple.com", leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, root));
        Assert.False(AppleTls.Validate("gsa.apple.com", leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, otherRoot));
        Assert.False(AppleTls.Validate("gsa.apple.com", null, null, SslPolicyErrors.RemoteCertificateNotAvailable, root));
    }

    [Fact]
    public void A_normally_valid_certificate_needs_nothing_extra()
    {
        var (root, leaf) = Make("example.com");
        Assert.True(AppleTls.Validate("example.com", leaf, null, SslPolicyErrors.None, root));
    }

    /// <summary>Talks to Apple's real sign-in server. Opt-in: RIGCONTROL_LIVE_APPLE=1.</summary>
    [Fact]
    public async Task Live_gsa_apple_com_is_reachable()
    {
        if (Environment.GetEnvironmentVariable("RIGCONTROL_LIVE_APPLE") != "1")
            return;
        using var http = new HttpClient(AppleTls.CreateHandler());
        using var r = await http.GetAsync("https://gsa.apple.com/grandslam/GsService2/lookup");
        // Any HTTP answer means the TLS connection to Apple was accepted (the lookup itself wants client headers).
        Assert.True((int)r.StatusCode is >= 200 and < 600);
    }
}
