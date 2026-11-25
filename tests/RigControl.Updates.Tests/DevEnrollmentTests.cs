using System.Security.Cryptography;
using System.Text;

namespace RigControl.Updates.Tests;

/// <summary>Stands in for a Windows Hello key: an RSA key in memory. Can be told to sign wrongly or be cancelled.</summary>
internal sealed class FakeDeviceKey : IDeviceKey
{
    private RSA? _key;
    public bool Supported { get; set; } = true;
    public bool SignWrongly { get; set; }
    public bool Cancel { get; set; }
    public int Prompts { get; private set; }

    public Task<bool> SupportedAsync() => Task.FromResult(Supported);

    public Task<byte[]> CreateAsync()
    {
        _key?.Dispose();
        _key = RSA.Create(2048);
        return Task.FromResult(_key.ExportSubjectPublicKeyInfo());
    }

    public Task<byte[]?> OpenAsync() => Task.FromResult(_key?.ExportSubjectPublicKeyInfo());

    public Task<byte[]> SignAsync(byte[] data)
    {
        Prompts++;
        if (Cancel) throw new OperationCanceledException();
        var sig = _key!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (SignWrongly) sig[0] ^= 1;
        return Task.FromResult(sig);
    }

    public Task DeleteAsync()
    {
        _key?.Dispose();
        _key = null;
        return Task.CompletedTask;
    }
}

public sealed class DevEnrollmentTests : IDisposable
{
    private readonly ECDsa _issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeDeviceKey _device = new();

    public void Dispose() => _issuer.Dispose();

    private string IssuerPub => EcSignature.PublicKeyBase64(_issuer);

    private async Task<string> EnrollAsync()
    {
        var request = DevEnrollment.MakeRequest(await _device.CreateAsync());
        var spki = DevEnrollment.ReadRequest(request, out var problem);
        Assert.Null(problem);
        return DevEnrollment.Issue(_issuer, spki!, "tester", new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_token_for_this_PCs_key_unlocks_after_one_signature()
    {
        var token = await EnrollAsync();
        var info = DevEnrollment.Verify(token, IssuerPub, out var problem);
        Assert.Null(problem);
        Assert.Equal("tester", info!.Subject);
        Assert.Equal("2026-11-25", info.IssuedAt);
        Assert.Matches("^[0-9a-f]{16}$", info.Id!);

        Assert.Null(await DevEnrollment.UnlockAsync(token, _device, IssuerPub));
        Assert.Equal(1, _device.Prompts);
    }

    [Fact]
    public async Task A_token_for_another_key_or_an_earlier_registration_does_not()
    {
        var token = await EnrollAsync();
        await _device.CreateAsync(); // registered again: a new key
        Assert.Contains("another PC", await DevEnrollment.UnlockAsync(token, _device, IssuerPub));
        Assert.Equal(0, _device.Prompts);

        await _device.DeleteAsync();
        Assert.Contains("not registered", await DevEnrollment.UnlockAsync(token, _device, IssuerPub));
    }

    [Fact]
    public async Task A_wrong_signature_or_a_cancelled_prompt_stays_locked()
    {
        var token = await EnrollAsync();
        _device.SignWrongly = true;
        Assert.NotNull(await DevEnrollment.UnlockAsync(token, _device, IssuerPub));
        _device.SignWrongly = false;
        _device.Cancel = true;
        Assert.Contains("cancelled", await DevEnrollment.UnlockAsync(token, _device, IssuerPub));
    }

    [Fact]
    public async Task Without_Windows_Hello_it_says_how_to_set_it_up()
    {
        var token = await EnrollAsync();
        _device.Supported = false;
        Assert.Equal("Set up Windows Hello (a PIN is enough) first.", await DevEnrollment.UnlockAsync(token, _device, IssuerPub));
    }

    [Fact]
    public async Task A_forged_or_changed_token_is_refused()
    {
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = await _device.CreateAsync();
        var forged = DevEnrollment.Issue(forger, spki, "x", DateTimeOffset.UtcNow);
        Assert.Null(DevEnrollment.Verify(forged, IssuerPub, out var p1));
        Assert.Contains("signature", p1);
        Assert.NotNull(await DevEnrollment.UnlockAsync(forged, _device, IssuerPub));
        Assert.Equal(0, _device.Prompts);

        // A genuine token whose payload is swapped for one naming another key.
        var real = DevEnrollment.Issue(_issuer, spki, "x", DateTimeOffset.UtcNow).Split('.');
        using var other = RSA.Create(2048);
        var otherPayload = DevEnrollment.Issue(_issuer, other.ExportSubjectPublicKeyInfo(), "x", DateTimeOffset.UtcNow).Split('.')[1];
        Assert.Null(DevEnrollment.Verify(real[0] + "." + otherPayload + "." + real[2], IssuerPub, out _));
    }

    [Fact]
    public void Old_RCDEV1_keys_unlock_nothing()
    {
        // Built exactly like the old pasted keys, and correctly signed by the issuer.
        var payload = DevEnrollment.ToBase64Url(Encoding.UTF8.GetBytes("{\"v\":1,\"sub\":\"x\",\"scope\":[\"dev\"],\"iat\":1,\"id\":\"a\"}"));
        var sig = _issuer.SignData(Encoding.ASCII.GetBytes("RCDEV1." + payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var old = "RCDEV1." + payload + "." + DevEnrollment.ToBase64Url(sig);
        Assert.Null(DevEnrollment.Verify(old, IssuerPub, out var problem));
        Assert.Contains("RCDEV1", problem);
    }

    [Fact]
    public void Requests_are_checked()
    {
        Assert.Null(DevEnrollment.ReadRequest("RCREQ1.abc", out _));
        Assert.Null(DevEnrollment.ReadRequest("RCREQ2.AAAA", out _));
        Assert.Null(DevEnrollment.ReadRequest("", out _));
    }

    [Fact]
    public void The_session_closes_after_15_idle_minutes()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new DevSession(() => now);
        Assert.False(session.Unlocked);
        session.Open();
        now += TimeSpan.FromMinutes(14);
        Assert.True(session.Touch());
        now += TimeSpan.FromMinutes(14);
        Assert.True(session.Unlocked);
        now += TimeSpan.FromMinutes(2);
        Assert.False(session.Unlocked);
        Assert.False(session.Touch());
        session.Open();
        session.Close();
        Assert.False(session.Unlocked);
    }

    [Fact]
    public void The_embedded_issuer_key_matches_the_private_folders_public_key_when_it_is_there()
    {
        // The public copy of the source has no private/ folder; then there is nothing to compare.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RigControl.slnx"))) dir = dir.Parent;
        var pem = dir is null ? null : Path.Combine(dir.FullName, "private", "dev-key-issuer.pub.pem");
        if (pem is null || !File.Exists(pem)) return;
        using var k = EcSignature.ImportPublicKey(File.ReadAllText(pem));
        Assert.Equal(TrustedKeys.DevKeyIssuerPublicKey, EcSignature.PublicKeyBase64(k));
    }
}
