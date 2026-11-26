using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RigControl.iPhone.Apple;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Apple;

/// <summary>Apple's developer services in memory: canned replies per action, a CA that signs submitted CSRs.</summary>
internal sealed class FakeDeveloperServices : IDisposable
{
    public const string Team = "ABCDE12345";
    private readonly RSA _caKey = RSA.Create(2048);
    private readonly X509Certificate2 _ca;
    private int _serial = 100;

    public readonly List<PlistDict> Certificates = [];
    public readonly List<string> Revoked = [];
    public readonly List<PlistDict> Devices = [];
    public readonly List<PlistDict> AppIds = [];
    public long? FailSubmitWith { get; set; }
    public int Submits { get; private set; }
    public RecordingHandler Handler { get; }

    public FakeDeveloperServices()
    {
        var req = new CertificateRequest("CN=Fake Apple WWDR", _caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Handler = new RecordingHandler(Route);
    }

    public void Dispose()
    {
        _ca.Dispose();
        _caKey.Dispose();
    }

    public PlistDict AddForeignCertificate(string machineName)
    {
        using var key = RSA.Create(2048);
        var cert = Sign(new CertificateRequest("CN=other", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var entry = CertEntry(cert, machineName);
        Certificates.Add(entry);
        return entry;
    }

    private byte[] Sign(CertificateRequest request)
    {
        var serial = Enumerable.Reverse(BitConverter.GetBytes(++_serial)).ToArray();
        using var cert = request.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7), serial);
        return cert.RawData;
    }

    private PlistDict CertEntry(byte[] der, string machineName)
    {
        using var cert = new X509Certificate2(der);
        return new PlistDict
        {
            ["certificateId"] = "CERT" + cert.SerialNumber,
            ["serialNumber"] = cert.SerialNumber,
            ["name"] = "Apple Development: someone@example.com",
            ["machineName"] = machineName,
            ["machineId"] = "MACHINE",
            ["status"] = "Issued",
            ["certContent"] = der,
            ["expirationDate"] = cert.NotAfter.ToUniversalTime(),
        };
    }

    private HttpResponseMessage Route(Recorded r)
    {
        Assert.Equal("POST", r.Method);
        Assert.Equal("developerservices2.apple.com", r.Uri.Host);
        Assert.Equal("?clientId=XABBG36SBA", r.Uri.Query);
        Assert.Equal("XCODE-TOKEN", r.Header("X-Apple-GS-Token"));
        Assert.Equal("000-adsid", r.Header("X-Apple-I-Identity-Id"));
        Assert.Equal("11.2 (11B41)", r.Header("X-Xcode-Version"));
        Assert.Equal("Xcode", r.Header("User-Agent"));
        Assert.NotEqual("", r.Header("X-Apple-I-MD"));
        var body = PlistFile.ParseDict(r.Body);
        Assert.Equal("XABBG36SBA", body.String("clientId"));
        Assert.Equal("QH65B2", body.String("protocolVersion"));
        Assert.True(Guid.TryParse(body.String("requestId"), out _));

        var action = r.Uri.AbsolutePath["/services/QH65B2/".Length..];
        if (action != "listTeams.action")
            Assert.Equal(Team, body.String("teamId"));
        switch (action)
        {
            case "listTeams.action":
                return Ok(new PlistDict
                {
                    ["teams"] = new List<object>
                    {
                        new PlistDict { ["teamId"] = Team, ["name"] = "Someone (Personal Team)", ["type"] = "Individual", ["status"] = "active" },
                    },
                });
            case "ios/listDevices.action":
                return Ok(new PlistDict { ["devices"] = Devices.Cast<object>().ToList() });
            case "ios/addDevice.action":
            {
                var device = new PlistDict
                {
                    ["deviceId"] = "DEV" + Devices.Count,
                    ["deviceNumber"] = body.String("deviceNumber")!,
                    ["name"] = body.String("name")!,
                };
                Devices.Add(device);
                return Ok(new PlistDict { ["device"] = device });
            }
            case "ios/listAllDevelopmentCerts.action":
                return Ok(new PlistDict { ["certificates"] = Certificates.Cast<object>().ToList() });
            case "ios/revokeDevelopmentCert.action":
            {
                var serial = body.String("serialNumber")!;
                Revoked.Add(serial);
                Certificates.RemoveAll(c => c.String("serialNumber") == serial);
                return Ok(new PlistDict());
            }
            case "ios/submitDevelopmentCSR.action":
            {
                Submits++;
                if (FailSubmitWith is { } code)
                    return Ok(new PlistDict
                    {
                        ["resultCode"] = code,
                        ["userString"] = "There is a problem with the request entity - You already have a current iOS Development certificate or a pending certificate request.",
                    });
                Assert.Equal("RigControl", body.String("machineName"));
                Assert.True(Guid.TryParse(body.String("machineId"), out _));
                var csr = CertificateRequest.LoadSigningRequestPem(body.String("csrContent")!, HashAlgorithmName.SHA256,
                    CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
                Assert.Equal(2048, csr.PublicKey.GetRSAPublicKey()!.KeySize);
                var entry = CertEntry(Sign(csr), "RigControl");
                entry["machineId"] = body.String("machineId")!;
                Certificates.Add(entry);
                return Ok(new PlistDict
                {
                    ["certRequest"] = new PlistDict
                    {
                        ["certificateId"] = entry.String("certificateId")!,
                        ["serialNum"] = entry.String("serialNumber")!,
                    },
                });
            }
            case "ios/listAppIds.action":
                return Ok(new PlistDict { ["appIds"] = AppIds.Cast<object>().ToList(), ["maxQuantity"] = 10L, ["availableQuantity"] = 10L - AppIds.Count });
            case "ios/addAppId.action":
            {
                if (AppIds.Count >= 10)
                    return Ok(new PlistDict
                    {
                        ["resultCode"] = 9401L,
                        ["userString"] = "You have exceeded the maximum App ID limit.",
                    });
                var appId = new PlistDict
                {
                    ["appIdId"] = "APPID" + AppIds.Count,
                    ["identifier"] = body.String("identifier")!,
                    ["name"] = body.String("name")!,
                    ["features"] = new PlistDict(),
                };
                AppIds.Add(appId);
                return Ok(new PlistDict { ["appId"] = appId });
            }
            case "ios/updateAppId.action":
            {
                var appId = AppIds.Single(a => a.String("appIdId") == body.String("appIdId"));
                appId.Dict("features")!["APG3427HIY"] = body.Bool("APG3427HIY")!;
                return Ok(new PlistDict { ["appId"] = appId });
            }
            case "ios/downloadTeamProvisioningProfile.action":
                Assert.Equal("APPID0", body.String("appIdId"));
                return Ok(new PlistDict
                {
                    ["provisioningProfile"] = new PlistDict { ["encodedProfile"] = new byte[] { 0x30, 0x82, 1, 2, 3 } },
                });
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Ok(PlistDict reply)
    {
        reply.TryAdd("resultCode", 0L);
        reply["protocolVersion"] = "QH65B2";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PlistFile.ToXml(reply)) };
    }
}

public sealed class DeveloperServicesClientTests : IDisposable
{
    private readonly FakeDeveloperServices _apple = new();
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "rc-iphone-keys-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProtector _protector = new();
    private readonly DeveloperServicesClient _client;

    public DeveloperServicesClientTests()
    {
        var session = new AppleSession("someone@example.com", "000-adsid", "1", "XCODE-TOKEN", DateTimeOffset.UtcNow.AddDays(1));
        _client = new DeveloperServicesClient(new HttpClient(_apple.Handler), new FakeAnisette(), session)
        {
            CertificateRetryDelay = TimeSpan.Zero,
        };
    }

    public void Dispose()
    {
        _apple.Dispose();
        if (Directory.Exists(_keys))
            Directory.Delete(_keys, recursive: true);
    }

    [Fact]
    public async Task Lists_teams()
    {
        var team = Assert.Single(await _client.ListTeamsAsync());
        Assert.Equal(new DeveloperTeam(FakeDeveloperServices.Team, "Someone (Personal Team)", "Individual"), team);
    }

    [Fact]
    public async Task Devices_are_added_once()
    {
        const string udid = "00008110-001A2B3C4D5E6F70";
        var first = await _client.EnsureDeviceAsync(FakeDeveloperServices.Team, udid, "Test's\niPhone");
        var again = await _client.EnsureDeviceAsync(FakeDeveloperServices.Team, udid, "iPhone");
        Assert.Equal(first, again);
        Assert.Equal("Test'siPhone", first.Name);
        Assert.Single(await _client.ListDevicesAsync(FakeDeveloperServices.Team));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.AddDeviceAsync(FakeDeveloperServices.Team, "not-a-udid", "x"));
    }

    [Fact]
    public async Task Certificate_is_made_then_reused_with_its_key()
    {
        var foreign = _apple.AddForeignCertificate("Someone's Mac");
        var made = await _client.GetOrCreateDevelopmentCertificateAsync(FakeDeveloperServices.Team, _keys, _protector);
        Assert.True(made.Certificate.HasPrivateKey);
        Assert.Equal(1, _apple.Submits);
        Assert.Empty(_apple.Revoked);

        // The key on disk is protected, and the second run reuses the certificate without a new request.
        var keyFile = Assert.Single(Directory.GetFiles(_keys, "*key.bin"));
        Assert.StartsWith("FAKEPROT", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(keyFile)[..8]));
        var machineId = DeveloperServicesClient.MachineId(_keys);
        var again = await _client.GetOrCreateDevelopmentCertificateAsync(FakeDeveloperServices.Team, _keys, _protector);
        Assert.Equal(1, _apple.Submits);
        Assert.Equal(made.SerialNumber, again.SerialNumber);
        Assert.Equal(machineId, DeveloperServicesClient.MachineId(_keys));

        // The key really belongs to the certificate: sign with one, verify with the other.
        var data = "check"u8.ToArray();
        var signature = again.Certificate.GetRSAPrivateKey()!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(made.Certificate.GetRSAPublicKey()!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Contains(_apple.Certificates, c => c.String("serialNumber") == foreign.String("serialNumber"));
    }

    [Fact]
    public async Task Lost_key_revokes_only_this_PCs_old_rigcontrol_certificate()
    {
        var oldOurs = _apple.AddForeignCertificate("RigControl");
        oldOurs["machineId"] = DeveloperServicesClient.MachineId(_keys);
        var otherPc = _apple.AddForeignCertificate("RigControl");   // RigControl on another PC: machine id "MACHINE"
        var foreign = _apple.AddForeignCertificate("Xcode Mac");
        var made = await _client.GetOrCreateDevelopmentCertificateAsync(FakeDeveloperServices.Team, _keys, _protector);
        Assert.Equal([oldOurs.String("serialNumber")!], _apple.Revoked);
        Assert.Contains(_apple.Certificates, c => c.String("serialNumber") == foreign.String("serialNumber"));
        Assert.Contains(_apple.Certificates, c => c.String("serialNumber") == otherPc.String("serialNumber"));
        Assert.True(made.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task Certificate_limit_is_explained_plainly()
    {
        _apple.AddForeignCertificate("AltServer");
        _apple.FailSubmitWith = 7460;
        var e = await Assert.ThrowsAsync<AppleException>(() =>
            _client.GetOrCreateDevelopmentCertificateAsync(FakeDeveloperServices.Team, _keys, _protector));
        Assert.Equal(7460, e.Code);
        Assert.Contains("as many development certificates as Apple allows", e.Message);
        Assert.Empty(_apple.Revoked);
    }

    [Fact]
    public async Task App_ids_profile_and_the_free_limit()
    {
        var appId = await _client.EnsureAppIdAsync(FakeDeveloperServices.Team, "com.rigcontrol.phone.ABCDE12345", "RigControl: Phone!");
        Assert.Equal("RigControlPhone", appId.Name);
        Assert.Equal(appId, await _client.EnsureAppIdAsync(FakeDeveloperServices.Team, "com.rigcontrol.phone.ABCDE12345", "x"));
        var list = await _client.ListAppIdsAsync(FakeDeveloperServices.Team);
        Assert.Equal(10, list.MaxQuantity);
        Assert.Equal(9, list.AvailableQuantity);

        await _client.UpdateAppIdAsync(FakeDeveloperServices.Team, appId.Id, new PlistDict { ["APG3427HIY"] = true });
        Assert.True(_apple.AppIds[0].Dict("features")!.Bool("APG3427HIY"));

        var profile = await _client.DownloadTeamProvisioningProfileAsync(FakeDeveloperServices.Team, appId.Id);
        Assert.Equal(new byte[] { 0x30, 0x82, 1, 2, 3 }, profile);

        for (var i = 1; i < 10; i++)
            await _client.AddAppIdAsync(FakeDeveloperServices.Team, "com.example.app" + i, "App");
        var e = await Assert.ThrowsAsync<AppleException>(() => _client.AddAppIdAsync(FakeDeveloperServices.Team, "com.example.eleven", "App"));
        Assert.Equal(9401, e.Code);
        Assert.Equal("You have exceeded the maximum App ID limit.", e.Message);
    }

    [Fact]
    public async Task Expired_session_and_unreadable_replies_are_errors()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") });
        var session = new AppleSession("a@b.c", "000-adsid", "1", "XCODE-TOKEN", DateTimeOffset.UtcNow);
        var client = new DeveloperServicesClient(new HttpClient(handler), new FakeAnisette(), session);
        var e = await Assert.ThrowsAsync<AppleException>(() => client.ListTeamsAsync());
        Assert.Equal(401, e.Code);

        var missing = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PlistFile.ToXml(new PlistDict { ["resultCode"] = 0L })),
        });
        client = new DeveloperServicesClient(new HttpClient(missing), new FakeAnisette(), session);
        await Assert.ThrowsAsync<AppleException>(() => client.ListTeamsAsync());
    }
}
