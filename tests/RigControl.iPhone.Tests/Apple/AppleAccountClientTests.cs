using System.Security.Cryptography;
using System.Text;
using RigControl.iPhone.Apple;

namespace RigControl.iPhone.Tests.Apple;

public class AppleAccountClientTests
{
    private const string Password = "Correct-Horse-9!";
    private const string AppleId = "Someone@Example.com";

    private static (AppleAccountClient Client, FakeGsaServer Server) Make(FakeGsaServer server) =>
        (new AppleAccountClient(new HttpClient(server.Handler), new FakeAnisette()), server);

    [Fact]
    public async Task Signs_in_without_two_factor()
    {
        var (client, server) = Make(new FakeGsaServer(Password));
        var password = Password.ToCharArray();
        var result = await client.SignInAsync(AppleId, password, new FakePrompt());

        Assert.All(password, c => Assert.Equal('\0', c));
        Assert.Equal("someone@example.com", result.Session.AppleId);
        Assert.Equal(FakeGsaServer.Adsid, result.Session.Adsid);
        Assert.Equal("1234567890", result.Session.Dsid);
        Assert.Equal(FakeGsaServer.XcodeToken, result.Session.XcodeToken);
        Assert.InRange(result.Session.Expires, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
        Assert.Equal(FakeGsaServer.Pet, result.PasswordEquivalentToken);
        Assert.NotNull(result.PasswordEquivalentTokenExpires);
        Assert.Equal("Test", result.FirstName);
        Assert.Equal(1, server.Logins);
        AssertPasswordNeverSent(server);
    }

    [Fact]
    public async Task Trusted_device_code_then_signs_in_again()
    {
        var (client, server) = Make(new FakeGsaServer(Password) { Needs = FakeGsaServer.SecondFactor.TrustedDevice });
        var prompt = new FakePrompt(
            _ => SecondFactorAnswer.WithCode("000000"), // wrong first: asked again
            _ => SecondFactorAnswer.WithCode(FakeGsaServer.DeviceCode));
        var result = await client.SignInAsync(AppleId, Password.ToCharArray(), prompt);

        Assert.Equal(FakeGsaServer.XcodeToken, result.Session.XcodeToken);
        Assert.Equal(1, server.Pushes);
        Assert.Equal(2, server.Logins);
        Assert.Equal(2, prompt.Requests.Count);
        Assert.All(prompt.Requests, r => Assert.Equal(SecondFactorKind.TrustedDevice, r.Kind));
        Assert.Equal(2, prompt.Requests[0].Phones.Count);
        Assert.Equal("+1 (•••) •••-••12", prompt.Requests[0].Phones[0].MaskedNumber);
        AssertPasswordNeverSent(server);
    }

    [Fact]
    public async Task Switches_from_device_to_text_message_when_asked()
    {
        var (client, server) = Make(new FakeGsaServer(Password) { Needs = FakeGsaServer.SecondFactor.TrustedDevice });
        var prompt = new FakePrompt(
            r => SecondFactorAnswer.SendSms(r.Phones[1]),
            r =>
            {
                Assert.Equal(SecondFactorKind.Sms, r.Kind);
                Assert.Equal(2, r.Phone!.Id);
                return SecondFactorAnswer.WithCode(FakeGsaServer.SmsCode);
            });
        var result = await client.SignInAsync(AppleId, Password.ToCharArray(), prompt);

        Assert.Equal(FakeGsaServer.XcodeToken, result.Session.XcodeToken);
        Assert.Equal([2L], server.SmsSentTo);
        Assert.Equal(2, server.Logins);
        AssertPasswordNeverSent(server);
    }

    [Fact]
    public async Task Text_message_two_factor_with_s2k_fo_and_resend_to_other_phone()
    {
        var (client, server) = Make(new FakeGsaServer(Password) { Needs = FakeGsaServer.SecondFactor.Sms, Protocol = "s2k_fo" });
        var prompt = new FakePrompt(
            r =>
            {
                Assert.Equal(SecondFactorKind.Sms, r.Kind);
                Assert.Equal(1, r.Phone!.Id);
                return SecondFactorAnswer.SendSms(r.Phones[1]);
            },
            r => SecondFactorAnswer.WithCode(FakeGsaServer.SmsCode));
        var result = await client.SignInAsync(AppleId, Password.ToCharArray(), prompt);

        Assert.Equal(FakeGsaServer.XcodeToken, result.Session.XcodeToken);
        Assert.Equal([1L, 2L], server.SmsSentTo);
        AssertPasswordNeverSent(server);
    }

    [Fact]
    public async Task Spd_encrypted_with_the_raw_secret_is_also_read()
    {
        var (client, server) = Make(new FakeGsaServer(Password) { SpdKeyIsPremaster = true });
        var result = await client.SignInAsync(AppleId, Password.ToCharArray(), new FakePrompt());
        Assert.Equal(FakeGsaServer.XcodeToken, result.Session.XcodeToken);
    }

    [Fact]
    public async Task Wrong_password_gives_plain_text_and_clears_the_array()
    {
        var (client, server) = Make(new FakeGsaServer(Password));
        var wrong = "not-the-password".ToCharArray();
        var e = await Assert.ThrowsAsync<AppleException>(() => client.SignInAsync(AppleId, wrong, new FakePrompt()));
        Assert.Equal(-20101, e.Code);
        Assert.StartsWith("The Apple ID or password is wrong.", e.Message);
        Assert.All(wrong, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Cancelling_the_code_prompt_cancels()
    {
        var (client, _) = Make(new FakeGsaServer(Password) { Needs = FakeGsaServer.SecondFactor.TrustedDevice });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SignInAsync(AppleId, Password.ToCharArray(), new FakePrompt(_ => null)));
    }

    [Fact]
    public async Task Unknown_phone_from_the_prompt_is_refused()
    {
        var (client, _) = Make(new FakeGsaServer(Password) { Needs = FakeGsaServer.SecondFactor.TrustedDevice });
        await Assert.ThrowsAsync<AppleException>(() => client.SignInAsync(AppleId, Password.ToCharArray(),
            new FakePrompt(_ => SecondFactorAnswer.SendSms(new TrustedPhone(99, "x")))));
    }

    [Fact]
    public void Error_codes_read_plainly()
    {
        Assert.Contains("appleid.apple.com", AppleErrors.Describe(-22406, "Your account needs repair"));
        Assert.Contains("device identification", AppleErrors.Describe(-80045, null));
        Assert.Contains("locked", AppleErrors.Describe(-20209, null));
        Assert.Equal("Apple refused the sign-in: Something new (code -1).", AppleErrors.Describe(-1, "Something new"));
    }

    // The password may only reach Apple as the SRP proof: never itself, its hash or its PBKDF2 key.
    private static void AssertPasswordNeverSent(FakeGsaServer server)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Password));
        var forbiddenText = new[]
        {
            Password, Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)),
            Convert.ToHexString(hash), Convert.ToHexString(hash).ToLowerInvariant(), Convert.ToBase64String(hash),
        }.Concat(server.PasswordKeys.Select(Convert.ToBase64String)).ToList();
        var forbiddenBytes = new List<byte[]> { Encoding.UTF8.GetBytes(Password), hash };
        forbiddenBytes.AddRange(server.PasswordKeys);

        Assert.NotEmpty(server.Handler.Requests);
        foreach (var r in server.Handler.Requests)
        {
            var text = r.Uri + "\n" + r.Headers + "\n" + r.BodyText;
            foreach (var f in forbiddenText)
                Assert.DoesNotContain(f, text, StringComparison.OrdinalIgnoreCase);
            foreach (var f in forbiddenBytes)
                Assert.True(r.Body.AsSpan().IndexOf(f) < 0, "A request body carried password material.");
        }
    }
}
