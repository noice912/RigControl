using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RigControl.iPhone.Apple;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Apple;

/// <summary>
/// Apple's GrandSlam in memory: runs the server side of SRP against a known password, asks for two-factor when
/// told to, and hands out the Xcode app token. Everything it was sent is in <see cref="Handler"/>.Requests.
/// </summary>
internal sealed class FakeGsaServer
{
    public const string Adsid = "000777-08-aaaa-bbbb-cccc";
    public const string IdmsToken = "IDMS-TOKEN-xyz";
    public const string XcodeToken = "XCODE-TOKEN-123";
    public const string Pet = "PET-TOKEN-456";
    public const string DeviceCode = "123456";
    public const string SmsCode = "654321";

    public enum SecondFactor { None, TrustedDevice, Sms }

    private readonly string _password;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _sk = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _c = RandomNumberGenerator.GetBytes(24);
    private SrpServerReference? _srp;
    private string? _user;
    private byte[]? _a;

    public SecondFactor Needs { get; init; }
    public string Protocol { get; init; } = "s2k";
    public bool SpdKeyIsPremaster { get; init; }
    public int Iterations { get; init; } = 1000;
    public bool Trusted { get; private set; }
    public int Logins { get; private set; }
    public int Pushes { get; private set; }
    public readonly List<long> SmsSentTo = [];
    public readonly List<byte[]> PasswordKeys = [];
    public RecordingHandler Handler { get; }

    public FakeGsaServer(string password)
    {
        _password = password;
        Handler = new RecordingHandler(Route);
    }

    private HttpResponseMessage Route(Recorded r)
    {
        var path = r.Uri.AbsolutePath;
        if (r.Uri.Host != "gsa.apple.com")
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        return (r.Method, path) switch
        {
            ("POST", "/grandslam/GsService2") => GsService(r),
            ("GET", "/grandslam/GsService2/validate") => Validate(r),
            ("GET", "/auth/verify/trusteddevice") => Push(r),
            ("GET", "/auth") => Phones(r),
            ("PUT", "/auth/verify/phone") => SendSms(r),
            ("POST", "/auth/verify/phone/securitycode") => CheckSms(r),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage GsService(Recorded r)
    {
        Assert.Equal("text/x-xml-plist", r.Header("Content-Type"));
        var body = PlistFile.ParseDict(r.Body);
        Assert.Equal("1.0.1", body.Dict("Header")!.String("Version"));
        var req = body.Dict("Request")!;
        // Sign-in uses the provider's description; the token step tries its own candidates first.
        if (req.String("o") == "apptokens")
            Assert.StartsWith("<MacBookPro", r.Header("X-MMe-Client-Info"));
        else
            Assert.StartsWith("<MacBookPro13,2>", r.Header("X-MMe-Client-Info"));
        var cpd = req.Dict("cpd")!;
        Assert.Equal("true", cpd.String("bootstrap"));
        Assert.Equal("iCloud", cpd.String("svct"));
        Assert.NotNull(cpd.String("X-Apple-I-MD"));
        Assert.NotNull(cpd.String("X-Apple-I-MD-M"));
        Assert.False(cpd.ContainsKey("X-Mme-Client-Info"));

        switch (req.String("o"))
        {
            case "init":
            {
                Assert.Equal(["s2k", "s2k_fo"], req.Array("ps")!.Cast<string>());
                _user = req.String("u");
                _a = req.Data("A2k");
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_password));
                var input = Protocol == "s2k_fo" ? Encoding.ASCII.GetBytes(Convert.ToHexString(hash).ToLowerInvariant()) : hash;
                var key = Rfc2898DeriveBytes.Pbkdf2(input, _salt, Iterations, HashAlgorithmName.SHA256, 32);
                PasswordKeys.Add(key);
                _srp = new SrpServerReference(_user!, key, _salt);
                return Reply(new PlistDict
                {
                    ["Status"] = Ok(),
                    ["s"] = _salt,
                    ["B"] = _srp.B,
                    ["i"] = (long)Iterations,
                    ["c"] = "context-1",
                    ["sp"] = Protocol,
                });
            }
            case "complete":
            {
                Assert.Equal("context-1", req.String("c"));
                Assert.Equal(_user, req.String("u"));
                var m2 = _srp!.Verify(_a!, req.Data("M1")!);
                if (m2 is null)
                    return Reply(new PlistDict { ["Status"] = Error(-20101, "Your Apple ID or password was entered incorrectly.") });
                Logins++;
                var spd = new PlistDict
                {
                    ["adsid"] = Adsid,
                    ["GsIdmsToken"] = IdmsToken,
                    ["DsPrsId"] = 1234567890L,
                    ["sk"] = _sk,
                    ["c"] = _c,
                    ["fn"] = "Test",
                    ["ln"] = "Person",
                    ["t"] = new PlistDict
                    {
                        ["com.apple.gs.idms.pet"] = new PlistDict
                        {
                            ["token"] = Pet,
                            ["expiry"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
                        },
                    },
                };
                var status = Ok();
                if (!Trusted && Needs == SecondFactor.TrustedDevice)
                    status["au"] = "trustedDeviceSecondaryAuth";
                if (!Trusted && Needs == SecondFactor.Sms)
                    status["au"] = "secondaryAuth";
                var key = SpdKeyIsPremaster ? _srp.Premaster! : _srp.SessionKey!;
                return Reply(new PlistDict
                {
                    ["Status"] = status,
                    ["M2"] = m2,
                    ["spd"] = GsaCrypto.EncryptSpd(key, PlistFile.ToXml(spd)),
                });
            }
            case "apptokens":
            {
                Assert.True(Trusted || Needs == SecondFactor.None);
                Assert.Equal(Adsid, req.String("u"));
                Assert.Equal(IdmsToken, req.String("t"));
                Assert.Equal(_c, req.Data("c"));
                Assert.Equal(["com.apple.gs.xcode.auth"], req.Array("app")!.Cast<string>());
                Assert.Equal(HMACSHA256.HashData(_sk, Encoding.UTF8.GetBytes("apptokens" + Adsid + "com.apple.gs.xcode.auth")),
                    req.Data("checksum"));
                var token = new PlistDict
                {
                    ["status-code"] = 200L,
                    ["t"] = new PlistDict
                    {
                        ["com.apple.gs.xcode.auth"] = new PlistDict
                        {
                            ["token"] = XcodeToken,
                            ["duration"] = 31536000L,
                            ["expiry"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                        },
                    },
                };
                return Reply(new PlistDict
                {
                    ["Status"] = Ok(),
                    ["et"] = GsaCrypto.EncryptAppToken(_sk, RandomNumberGenerator.GetBytes(16), PlistFile.ToXml(token)),
                });
            }
            default:
                throw new InvalidOperationException("Unknown operation.");
        }
    }

    private void CheckIdentity(Recorded r)
    {
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Adsid + ":" + IdmsToken)), r.Header("X-Apple-Identity-Token"));
        Assert.Equal("com.apple.gs.xcode.auth", r.Header("X-Apple-App-Info"));
        // Codes use the identification's own client description, unchanged.
        Assert.StartsWith("<MacBookPro13,2>", r.Header("X-Mme-Client-Info"));
        Assert.NotEqual("", r.Header("X-Apple-I-MD"));
    }

    private HttpResponseMessage Push(Recorded r)
    {
        CheckIdentity(r);
        Pushes++;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private HttpResponseMessage Validate(Recorded r)
    {
        CheckIdentity(r);
        if (r.Header("security-code") != DeviceCode)
            return Bare(new PlistDict { ["ec"] = -21669L, ["em"] = "Incorrect verification code." });
        Trusted = true;
        return Bare(new PlistDict { ["ec"] = 0L, ["em"] = "" });
    }

    private HttpResponseMessage Phones(Recorded r)
    {
        CheckIdentity(r);
        return Json(HttpStatusCode.OK, """
            {"trustedPhoneNumbers":[
              {"id":1,"numberWithDialCode":"+1 (•••) •••-••12","lastTwoDigits":"12","pushMode":"sms"},
              {"id":2,"numberWithDialCode":"+44 ••••• ••••34","lastTwoDigits":"34","pushMode":"sms"}]}
            """);
    }

    private HttpResponseMessage SendSms(Recorded r)
    {
        CheckIdentity(r);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal("sms", doc.RootElement.GetProperty("mode").GetString());
        SmsSentTo.Add(doc.RootElement.GetProperty("phoneNumber").GetProperty("id").GetInt64());
        return Json(HttpStatusCode.OK, "{}");
    }

    private HttpResponseMessage CheckSms(Recorded r)
    {
        CheckIdentity(r);
        using var doc = JsonDocument.Parse(r.Body);
        var code = doc.RootElement.GetProperty("securityCode").GetProperty("code").GetString();
        var phone = doc.RootElement.GetProperty("phoneNumber").GetProperty("id").GetInt64();
        if (code != SmsCode || phone != SmsSentTo[^1])
            return Json(HttpStatusCode.BadRequest, """{"serviceErrors":[{"code":"-21669","title":"Incorrect","message":"Incorrect verification code."}]}""");
        Trusted = true;
        return Json(HttpStatusCode.OK, "{}");
    }

    private static PlistDict Ok() => new() { ["ec"] = 0L, ["em"] = "", ["hsc"] = 200L };

    private static PlistDict Error(long ec, string em) => new() { ["ec"] = ec, ["em"] = em, ["hsc"] = 401L };

    private static HttpResponseMessage Reply(PlistDict response) =>
        Bare(new PlistDict { ["Header"] = new PlistDict(), ["Response"] = response });

    private static HttpResponseMessage Bare(PlistDict dict) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(PlistFile.ToXml(dict)),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
