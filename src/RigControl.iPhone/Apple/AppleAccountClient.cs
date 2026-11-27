using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Apple;

/// <summary>What a finished sign-in gives: the session for developer services and, when Apple sent one, the PET.</summary>
public sealed record AppleSignIn(
    AppleSession Session,
    string? PasswordEquivalentToken,
    DateTimeOffset? PasswordEquivalentTokenExpires,
    string? FirstName,
    string? LastName);

/// <summary>
/// Signs in to an Apple ID through Apple's GrandSlam service (the one Xcode uses): SRP-6a so the password itself
/// never leaves the PC, then two-factor if Apple asks for it, then the Xcode app token for developer services.
/// Nothing here logs; the password is cleared from memory as soon as its hash is taken.
/// </summary>
public sealed class AppleAccountClient
{
    public const string GsaUrl = "https://gsa.apple.com/grandslam/GsService2";
    private const string ValidateUrl = "https://gsa.apple.com/grandslam/GsService2/validate";
    private const string TrustedDeviceUrl = "https://gsa.apple.com/auth/verify/trusteddevice";
    private const string AuthUrl = "https://gsa.apple.com/auth";
    private const string PhoneUrl = "https://gsa.apple.com/auth/verify/phone";
    private const string PhoneCodeUrl = "https://gsa.apple.com/auth/verify/phone/securitycode";
    private const string GsaUserAgent = "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0";
    // Few rounds on purpose: each can be a sign-in or a code push, and Apple rate-limits (and may lock) accounts.
    private const int MaxSteps = 4;
    private const int MaxCodeTries = 5;
    private const int MaxIterations = 5_000_000;

    private readonly HttpClient _http;
    private readonly IAnisetteProvider _anisette;

    public AppleAccountClient(HttpClient http, IAnisetteProvider anisette)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _anisette = anisette ?? throw new ArgumentNullException(nameof(anisette));
    }

    /// <summary>
    /// Signs in. <paramref name="password"/> is cleared (filled with zeros) before this returns or throws; only its
    /// SHA-256 is kept, in memory, until the sign-in finishes (Apple wants a second sign-in after two-factor).
    /// Throws <see cref="AppleException"/> with plain text, or <see cref="OperationCanceledException"/> when the
    /// person cancels the two-factor prompt.
    /// </summary>
    public async Task<AppleSignIn> SignInAsync(string appleId, char[] password, ISecondFactorPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] passwordHash;
        try
        {
            ArgumentNullException.ThrowIfNull(prompt);
            if (password.Length == 0)
                throw new AppleException("Type the Apple ID password.");
            passwordHash = HashPassword(password);
        }
        finally
        {
            Array.Clear(password);
        }

        try
        {
            var username = NormalizeAppleId(appleId);
            var login = await LoginAsync(username, passwordHash, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TrustedPhone>? phones = null;
            for (var step = 0; login.Next != NextStep.Done; step++)
            {
                if (step >= MaxSteps)
                    throw new AppleException("Apple kept asking for more sign-in steps. Try again later.");
                phones ??= await GetTrustedPhonesAsync(login, cancellationToken).ConfigureAwait(false);
                if (login.Next == NextStep.SmsCode)
                    await SmsFlowAsync(login, phones, phones.FirstOrDefault(), prompt, cancellationToken).ConfigureAwait(false);
                else
                    await DeviceFlowAsync(login, phones, login.Next == NextStep.DeviceCode, prompt, cancellationToken).ConfigureAwait(false);
                // Apple's way: after the code is accepted, sign in again and the account is trusted on this machine.
                // Apple often answers that sign-in "busy" (503) right after a code: wait and try again, twice at most.
                login = await LoginAfterCodeAsync(username, passwordHash, cancellationToken).ConfigureAwait(false);
            }

            var (token, expires) = await WithAnisetteRepairAsync(() => GetAppTokenAsync(login, cancellationToken), cancellationToken).ConfigureAwait(false);
            var session = new AppleSession(username, login.Adsid, login.Dsid, token, expires, LastTokenClientInfo);
            var (pet, petExpires) = ReadPet(login.Spd);
            return new AppleSignIn(session, pet, petExpires, login.Spd.String("fn"), login.Spd.String("ln"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordHash);
        }
    }

    /// <summary>Waits between the busy retries of the sign-in that follows an accepted code.</summary>
    internal static TimeSpan[] AfterCodeRetryDelays { get; set; } = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];

    private async Task<GsaLogin> LoginAfterCodeAsync(string username, byte[] passwordHash, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await LoginAsync(username, passwordHash, ct).ConfigureAwait(false);
            }
            catch (AppleException e) when (e.Code == BusyCode && attempt < AfterCodeRetryDelays.Length)
            {
                await Task.Delay(AfterCodeRetryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (AppleException e) when (e.Code == BusyCode)
            {
                throw new AppleException("Your code was accepted, but Apple's sign-in server stayed busy for the last step. " +
                    "Wait about 10 minutes and sign in again: this PC is now trusted, so it shouldn't ask for a code.", BusyCode);
            }
        }
    }

    /// <summary>AppleException.Code for Apple's "busy" answers (HTTP 503/429).</summary>
    internal const int BusyCode = 503;

    internal static string NormalizeAppleId(string? appleId)
    {
        var id = appleId?.Trim() ?? "";
        if (id.Length is 0 or > 320 || id.Any(char.IsControl) || id.Any(char.IsWhiteSpace))
            throw new AppleException("Type the Apple ID (the email address you sign in to Apple with).");
        return id.ToLowerInvariant();
    }

    private static byte[] HashPassword(char[] password)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(password)];
        try
        {
            Encoding.UTF8.GetBytes(password, 0, password.Length, bytes, 0);
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // ---- GrandSlam sign-in (SRP) ----

    internal enum NextStep { Done, PushThenDeviceCode, DeviceCode, SmsCode }

    internal sealed record GsaLogin(PlistDict Spd, string Adsid, string Dsid, string IdmsToken, NextStep Next);

    /// <summary>
    /// Apple asked for this PC's device identification to be re-synced (434, with data) or set up again (433). Not a
    /// password failure: the provider repairs itself and the same sign-in step is done once more.
    /// </summary>
    internal sealed class AnisetteRepairNeeded(byte[]? sim, bool reprovision, string stage, int hsc, long ec)
        : AppleException(AppleErrors.Describe(-80045, null) + $" (Apple status {hsc} at the {stage} step)", -80045)
    {
        public byte[]? Sim { get; } = sim;
        public bool Reprovision { get; } = reprovision;
        public string Stage { get; } = stage;
        public int Hsc { get; } = hsc;
        public long Ec { get; } = ec;
    }

    private Task<GsaLogin> LoginAsync(string username, byte[] passwordHash, CancellationToken ct) =>
        WithAnisetteRepairAsync(() => LoginOnceAsync(username, passwordHash, ct), ct);

    /// <summary>Runs one GrandSlam step; if Apple asks for the device identification to be repaired, repairs and repeats (twice at most).</summary>
    private async Task<T> WithAnisetteRepairAsync<T>(Func<Task<T>> step, CancellationToken ct)
    {
        for (var repairs = 0; ; repairs++)
        {
            try
            {
                return await step().ConfigureAwait(false);
            }
            catch (AnisetteRepairNeeded need) when (repairs >= 2 || _anisette is not IAnisetteRepair
                                                    || (!need.Reprovision && need.Sim is null))
            {
                throw new AppleException(AppleErrors.Describe(-80045, null) +
                    $" (Apple status {need.Hsc} at the {need.Stage} step, code {need.Ec}; " +
                    (need.Reprovision || need.Sim is not null ? $"refreshed {repairs} time(s) already" : "Apple sent no re-sync data") + ")",
                    -80045);
            }
            catch (AnisetteRepairNeeded need) when (_anisette is IAnisetteRepair repair)
            {
                try
                {
                    if (need.Reprovision)
                        await repair.ReprovisionAsync(ct).ConfigureAwait(false);
                    else
                        await repair.ResyncAsync(need.Sim!, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException and not AppleException)
                {
                    throw new AppleException("Apple asked RigControl to refresh this PC's device identification, and that " +
                        "didn't work: " + e.Message, -80045, e);
                }
            }
        }
    }

    private async Task<GsaLogin> LoginOnceAsync(string username, byte[] passwordHash, CancellationToken ct)
    {
        var anisette = await AppleHttp.AnisetteAsync(_anisette, ct).ConfigureAwait(false);
        var cpd = ClientProvidedData(anisette);
        var srp = new SrpClient();

        var init = await GsaRequestAsync(anisette, new PlistDict
        {
            ["A2k"] = srp.PublicA,
            ["cpd"] = cpd,
            ["o"] = "init",
            ["ps"] = new List<object> { "s2k", "s2k_fo" },
            ["u"] = username,
        }, ct).ConfigureAwait(false);

        var salt = init.Data("s") ?? throw Malformed("s");
        var serverB = init.Data("B") ?? throw Malformed("B");
        var iterations = init.Long("i") ?? throw Malformed("i");
        var c = init.String("c") ?? throw Malformed("c");
        var protocol = init.String("sp") ?? "s2k";
        if (iterations is < 1 or > MaxIterations || c.Length > 4096)
            throw Malformed("i");

        var passwordKey = SrpClient.PasswordKey(passwordHash, protocol, salt, (int)iterations);
        SrpResult proof;
        try
        {
            proof = srp.Process(username, passwordKey, salt, serverB);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
        }

        try
        {
            var complete = await GsaRequestAsync(anisette, new PlistDict
            {
                ["M1"] = proof.M1,
                ["c"] = c,
                ["cpd"] = cpd,
                ["o"] = "complete",
                ["u"] = username,
            }, ct).ConfigureAwait(false);

            var m2 = complete.Data("M2") ?? throw Malformed("M2");
            if (!CryptographicOperations.FixedTimeEquals(m2, proof.ExpectedM2))
                throw new AppleException("Apple's sign-in server could not be verified (its proof didn't match). Try again.");

            var spdBytes = complete.Data("spd") ?? throw Malformed("spd");
            var spd = ReadSpd(spdBytes, proof);

            var adsid = Text(spd.String("adsid"), "adsid");
            var idms = Text(spd.String("GsIdmsToken"), "GsIdmsToken", 8192);
            var dsid = spd.TryGetValue("DsPrsId", out var d) ? d switch
            {
                long l => l.ToString(CultureInfo.InvariantCulture),
                string s when s.Length <= 64 => s,
                _ => "",
            } : "";

            var status = complete.Dict("Status") ?? complete;
            return new GsaLogin(spd, adsid, dsid, idms, NextFrom(status, spd));
        }
        finally
        {
            proof.Clear();
        }
    }

    private static NextStep NextFrom(PlistDict status, PlistDict spd)
    {
        var au = status.String("au");
        switch (au)
        {
            case "trustedDeviceSecondaryAuth": return NextStep.PushThenDeviceCode;
            case "secondaryAuth": return NextStep.SmsCode;
            case null or "repair": break; // "repair" without an error means two-factor isn't set up
            default:
                if (ReadPet(spd).Pet is not null)
                    return NextStep.Done;
                throw new AppleException("Apple asks for a sign-in step RigControl can't do here (" + AppleErrors.Trim(au) +
                    "). Sign in once at https://appleid.apple.com, then try again.");
        }
        // 409: a code was already sent to the trusted devices.
        return status.Long("hsc") == 409 ? NextStep.DeviceCode : NextStep.Done;
    }

    private static PlistDict ClientProvidedData(IReadOnlyDictionary<string, string> anisette)
    {
        var cpd = new PlistDict
        {
            // Strings, not booleans: that's how the working clients send them.
            ["bootstrap"] = "true",
            ["icscrec"] = "true",
            ["pbe"] = "false",
            ["prkgen"] = "true",
            ["svct"] = "iCloud",
            ["loc"] = (AppleHttp.Find(anisette, "X-Apple-Locale") ?? "en_US"),
        };
        foreach (var (k, v) in anisette)
            if (!string.Equals(k, "X-Mme-Client-Info", StringComparison.OrdinalIgnoreCase))
                cpd[k] = v;
        return cpd;
    }

    private async Task<PlistDict> GsaRequestAsync(IReadOnlyDictionary<string, string> anisette, PlistDict request, CancellationToken ct,
        string? clientInfoOverride = null)
    {
        var body = new PlistDict
        {
            ["Header"] = new PlistDict { ["Version"] = "1.0.1" },
            ["Request"] = request,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, GsaUrl)
        {
            Content = new ByteArrayContent(PlistFile.ToXml(body)),
        };
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "text/x-xml-plist");
        message.Headers.TryAddWithoutValidation("Accept", "*/*");
        message.Headers.TryAddWithoutValidation("User-Agent", GsaUserAgent);
        // Sign-in: the provider's own description (Apple's Windows one). The app-token step passes its candidates.
        var clientInfo = clientInfoOverride ?? AppleHttp.Find(anisette, "X-Mme-Client-Info");
        if (clientInfo is not null)
            message.Headers.TryAddWithoutValidation("X-MMe-Client-Info", clientInfo);

        var reply = await AppleHttp.SendAsync(_http, message, ct).ConfigureAwait(false);
        if (reply.Status == HttpStatusCode.TooManyRequests)
            throw new AppleException("Apple says there were too many sign-in attempts. Wait a few minutes, then try again.");
        var dict = ParsePlist(reply);
        var response = dict.Dict("Response") ?? throw Malformed("Response");
        CheckStatus(response, request.String("o") ?? "sign-in");
        return response;
    }

    /// <summary>
    /// Decrypts and reads spd (the account details Apple sends after the password proof). The key is HMAC(K, ...) with
    /// K = H(S) as in SRP, or S itself as one current client does: both are tried, and the one that gives a readable
    /// property list wins (a wrong key can pass the padding check by chance). When neither reads, the error names the
    /// shape of the data and the parser's reason, never its content.
    /// </summary>
    internal static PlistDict ReadSpd(byte[] spdBytes, SrpResult proof)
    {
        var reasons = new List<string>();
        foreach (var (name, key) in new[] { ("K", proof.SessionKey), ("S", proof.PremasterSecret) })
        {
            var plain = GsaCrypto.DecryptSpd(key, spdBytes);
            if (plain is null)
            {
                reasons.Add(name + ": padding");
                continue;
            }
            try
            {
                return PlistFile.ParseDict(plain);
            }
            catch (Exception e) when (e is FormatException or System.Xml.XmlException or OverflowException or ArgumentException)
            {
                // Only RigControl's own fixed messages (PlistFormatException) or an XML error's position are kept:
                // other messages can quote the decrypted data.
                var why = e switch
                {
                    RigControl.iPhone.Plist.PlistFormatException p => p.Message,
                    System.Xml.XmlException x => $"line {x.LineNumber} position {x.LinePosition}",
                    _ => "no details",
                };
                reasons.Add($"{name}: {Shape(plain)} {plain.Length} bytes, {e.GetType().Name}: {why}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        throw new AppleException("Apple's sign-in reply could not be read (" + string.Join("; ", reasons) + ").");
    }

    /// <summary>What kind of data it looks like: "xml", "bplist" or "binary" (nothing of the content itself).</summary>
    private static string Shape(byte[] plain)
    {
        var head = plain.AsSpan(0, Math.Min(plain.Length, 64));
        var start = head.TrimStart((byte)0xEF).TrimStart((byte)0xBB).TrimStart((byte)0xBF).TrimStart(" \t\r\n"u8);
        if (start.StartsWith("<?xml"u8) || start.StartsWith("<plist"u8) || start.StartsWith("<!DOCTYPE"u8)) return "xml";
        if (start.StartsWith("bplist"u8)) return "bplist";
        return "binary";
    }

    private static PlistDict ParsePlist(AppleHttp.Reply reply)
    {
        if ((int)reply.Status is 503 or 429)
            throw new AppleException($"Apple's sign-in server is busy right now (HTTP {(int)reply.Status}). Wait a few minutes, then try again.", BusyCode);
        try
        {
            return PlistFile.ParseDict(reply.Body);
        }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException or OverflowException or ArgumentException)
        {
            throw new AppleException($"Apple's sign-in server sent a reply RigControl couldn't read (HTTP {(int)reply.Status}).", 0, e);
        }
    }

    /// <summary>Throws for a non-zero Status.ec (or ec on the dict itself).</summary>
    internal static void CheckStatus(PlistDict response) => CheckStatus(response, "reply");

    /// <summary>
    /// Throws for a non-zero Status.ec, or for Apple's device-identification statuses (434 re-sync, 433 set up again).
    /// <paramref name="stage"/> names the step for the message (Apple's codes and text are not secret).
    /// </summary>
    internal static void CheckStatus(PlistDict response, string stage)
    {
        var status = response.Dict("Status") ?? response;
        if (status.Long("hsc") == 434)
        {
            byte[]? sim = null;
            try { sim = status.String("X-Apple-I-MD-DATA") is { Length: > 0 and <= 8192 } s ? Convert.FromBase64String(s) : null; }
            catch (FormatException) { }
            throw new AnisetteRepairNeeded(sim, reprovision: false, stage, 434, status.Long("ec") ?? 0);
        }
        if (status.Long("hsc") == 433)
            throw new AnisetteRepairNeeded(null, reprovision: true, stage, 433, status.Long("ec") ?? 0);
        var ec = status.Long("ec") ?? 0;
        if (ec != 0)
        {
            var text = AppleErrors.Describe(ec, status.String("em"));
            text += ec is -80045 or -80046
                ? $" (Apple code {ec} at the {stage} step{(status.String("em") is { Length: > 0 and < 300 } em ? ": " + em : "")})"
                : $" [at the {stage} step]";
            throw new AppleException(text, (int)Math.Clamp(ec, int.MinValue, int.MaxValue));
        }
    }

    // ---- Two-factor ----

    private async Task DeviceFlowAsync(GsaLogin login, IReadOnlyList<TrustedPhone> phones, bool alreadySent,
        ISecondFactorPrompt prompt, CancellationToken ct)
    {
        if (!alreadySent)
        {
            var push = await TwoFactorRequestAsync(login, HttpMethod.Get, TrustedDeviceUrl, json: false, null, null, ct).ConfigureAwait(false);
            if ((int)push.Status is < 200 or > 299)
                throw new AppleException($"Apple couldn't send the code to your devices (HTTP {(int)push.Status}).");
        }
        for (var tries = 0; ; tries++)
        {
            if (tries >= MaxCodeTries)
                throw new AppleException(AppleErrors.Describe(-21669, null) + " Too many tries; start the sign-in again.", -21669);
            var answer = await AskAsync(prompt, new SecondFactorRequest(SecondFactorKind.TrustedDevice, phones, null), ct).ConfigureAwait(false);
            if (answer.SmsPhone is { } phone)
            {
                await SmsFlowAsync(login, phones, phone, prompt, ct).ConfigureAwait(false);
                return;
            }
            var code = CheckCode(answer.Code);
            if (code is null)
                continue;
            var reply = await TwoFactorRequestAsync(login, HttpMethod.Get, ValidateUrl, json: false, null, code, ct).ConfigureAwait(false);
            PlistDict dict;
            try
            {
                dict = PlistFile.ParseDict(reply.Body);
            }
            catch (Exception e) when (e is FormatException or System.Xml.XmlException)
            {
                if ((int)reply.Status is >= 200 and <= 299)
                    return;
                throw new AppleException($"Apple didn't accept the code (HTTP {(int)reply.Status}).", 0, e);
            }
            var status = dict.Dict("Response") ?? dict;
            var ec = (status.Dict("Status") ?? status).Long("ec") ?? 0;
            if (ec == -21669)
                continue;
            CheckStatus(status);
            if ((int)reply.Status is < 200 or > 299)
                throw new AppleException($"Apple didn't accept the code (HTTP {(int)reply.Status}).");
            return;
        }
    }

    private async Task SmsFlowAsync(GsaLogin login, IReadOnlyList<TrustedPhone> phones, TrustedPhone? phone,
        ISecondFactorPrompt prompt, CancellationToken ct)
    {
        if (phone is null)
            throw new AppleException("Apple wants a text-message code, but no trusted phone number was found for this Apple ID.");
        await SendSmsAsync(login, phone, ct).ConfigureAwait(false);
        for (var tries = 0; ; tries++)
        {
            if (tries >= MaxCodeTries)
                throw new AppleException(AppleErrors.Describe(-21669, null) + " Too many tries; start the sign-in again.", -21669);
            var answer = await AskAsync(prompt, new SecondFactorRequest(SecondFactorKind.Sms, phones, phone), ct).ConfigureAwait(false);
            if (answer.SmsPhone is { } other)
            {
                phone = other;
                await SendSmsAsync(login, phone, ct).ConfigureAwait(false);
                continue;
            }
            var code = CheckCode(answer.Code);
            if (code is null)
                continue;
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                securityCode = new { code },
                phoneNumber = new { id = phone.Id },
                mode = "sms",
            });
            var reply = await TwoFactorRequestAsync(login, HttpMethod.Post, PhoneCodeUrl, json: true, body, null, ct).ConfigureAwait(false);
            if ((int)reply.Status is >= 200 and <= 299)
                return;
            var error = ServiceError(reply.Body);
            if (error?.Code == "-21669")
                continue;
            throw new AppleException(error is null
                ? $"Apple didn't accept the code (HTTP {(int)reply.Status})."
                : AppleErrors.Describe(ParseCode(error.Code), error.Message), ParseCode(error?.Code));
        }
    }

    private async Task SendSmsAsync(GsaLogin login, TrustedPhone phone, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { phoneNumber = new { id = phone.Id }, mode = "sms" });
        var reply = await TwoFactorRequestAsync(login, HttpMethod.Put, PhoneUrl, json: true, body, null, ct).ConfigureAwait(false);
        if ((int)reply.Status is >= 200 and <= 299)
            return;
        var error = ServiceError(reply.Body);
        // Apple won't send another text yet, but the last code still works; 412 means a code is already on its way.
        if (error?.Code is "-22979" or "-22981" || (error is null && reply.Status == HttpStatusCode.PreconditionFailed))
            return;
        throw new AppleException(error is null
            ? $"Apple couldn't send the text message (HTTP {(int)reply.Status})."
            : AppleErrors.Describe(ParseCode(error.Code), error.Message), ParseCode(error?.Code));
    }

    private static async Task<SecondFactorAnswer> AskAsync(ISecondFactorPrompt prompt, SecondFactorRequest request, CancellationToken ct)
    {
        var answer = await prompt.AskAsync(request, ct).ConfigureAwait(false);
        if (answer is null || (answer.Code is null && answer.SmsPhone is null))
            throw new OperationCanceledException("The Apple ID sign-in was cancelled.");
        if (answer.SmsPhone is { } p && !request.Phones.Any(x => x.Id == p.Id))
            throw new AppleException("That phone number isn't one of this Apple ID's trusted numbers.");
        return answer;
    }

    // Codes are digits only (they go into a header); anything else counts as a wrong try.
    private static string? CheckCode(string? code)
    {
        code = code?.Trim();
        return code is { Length: >= 4 and <= 10 } && code.All(char.IsAsciiDigit) ? code : null;
    }

    private async Task<IReadOnlyList<TrustedPhone>> GetTrustedPhonesAsync(GsaLogin login, CancellationToken ct)
    {
        try
        {
            var reply = await TwoFactorRequestAsync(login, HttpMethod.Get, AuthUrl, json: true, null, null, ct).ConfigureAwait(false);
            return ParsePhones(reply.Body);
        }
        catch (AppleException)
        {
            // Not fatal: people with a trusted device don't need the list.
            return [];
        }
    }

    internal static IReadOnlyList<TrustedPhone> ParsePhones(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("trustedPhoneNumbers", out var list) || list.ValueKind != JsonValueKind.Array)
                return [];
            var phones = new List<TrustedPhone>();
            foreach (var p in list.EnumerateArray().Take(20))
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id) || !id.TryGetInt64(out var idValue))
                    continue;
                var number = p.TryGetProperty("numberWithDialCode", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                if (number.Length > 64 || number.Any(char.IsControl))
                    number = "";
                if (number.Length == 0 && p.TryGetProperty("lastTwoDigits", out var two) && two.ValueKind == JsonValueKind.String
                    && two.GetString() is { Length: <= 4 } t && t.All(char.IsAsciiDigit))
                    number = "ending in " + t;
                phones.Add(new TrustedPhone(idValue, number));
            }
            return phones;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record SmsError(string Code, string? Message);

    private static SmsError? ServiceError(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("serviceErrors", out var errors) || errors.ValueKind != JsonValueKind.Array
                || errors.GetArrayLength() == 0 || errors[0].ValueKind != JsonValueKind.Object)
                return null;
            var first = errors[0];
            var code = first.TryGetProperty("code", out var c) ? c.ValueKind switch
            {
                JsonValueKind.String => c.GetString() ?? "",
                JsonValueKind.Number => c.GetRawText(),
                _ => "",
            } : "";
            var message = first.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new SmsError(code, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ParseCode(string? code) =>
        int.TryParse(code, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private async Task<AppleHttp.Reply> TwoFactorRequestAsync(GsaLogin login, HttpMethod method, string url, bool json,
        byte[]? body, string? securityCode, CancellationToken ct)
    {
        var anisette = await AppleHttp.AnisetteAsync(_anisette, ct).ConfigureAwait(false);
        using var message = new HttpRequestMessage(method, url);
        var type = json ? "application/json" : "text/x-xml-plist";
        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        foreach (var (k, v) in anisette)
            if (!string.Equals(k, "X-Mme-Client-Info", StringComparison.OrdinalIgnoreCase))
                message.Headers.TryAddWithoutValidation(k, v);
        var clientInfo = AppleHttp.AuthKitClientInfo(AppleHttp.Find(anisette, "X-Mme-Client-Info"));
        if (clientInfo is not null)
            message.Headers.TryAddWithoutValidation("X-Mme-Client-Info", clientInfo);
        message.Headers.TryAddWithoutValidation("X-Apple-App-Info", AppleHttp.XcodeApp);
        message.Headers.TryAddWithoutValidation("X-Xcode-Version", AppleHttp.XcodeVersion);
        message.Headers.TryAddWithoutValidation("User-Agent", "Xcode");
        message.Headers.TryAddWithoutValidation("Accept-Language", "en-us");
        message.Headers.TryAddWithoutValidation("Accept", type);
        message.Headers.TryAddWithoutValidation("X-Apple-Identity-Token",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(login.Adsid + ":" + login.IdmsToken)));
        var locale = AppleHttp.Find(anisette, "X-Apple-Locale");
        if (locale is not null)
            message.Headers.TryAddWithoutValidation("Loc", locale);
        if (securityCode is not null)
            message.Headers.TryAddWithoutValidation("security-code", securityCode);
        return await AppleHttp.SendAsync(_http, message, ct).ConfigureAwait(false);
    }

    // ---- App token ----

    private async Task<(string Token, DateTimeOffset Expires)> GetAppTokenAsync(GsaLogin login, CancellationToken ct)
    {
        var sk = login.Spd.Data("sk") ?? throw Malformed("sk");
        var c = login.Spd.Data("c") ?? throw Malformed("c");
        if (sk.Length != 32 || c.Length > 4096)
            throw Malformed("sk");
        // Apple gives the Xcode token only to some client descriptions, and which ones depends on the device
        // identification. Asking again sends no password (it uses the sign-in's own token), so the candidates are
        // tried in turn and the first one Apple accepts is kept for developer services.
        PlistDict? response = null;
        string? accepted = null;
        var refused = new List<string>();
        var provider = AppleHttp.Find(await AppleHttp.AnisetteAsync(_anisette, ct).ConfigureAwait(false), "X-Mme-Client-Info");
        foreach (var candidate in TokenClientInfos(provider))
        {
            var anisette = await AppleHttp.AnisetteAsync(_anisette, ct).ConfigureAwait(false);
            try
            {
                response = await GsaRequestAsync(anisette, new PlistDict
                {
                    ["app"] = new List<object> { AppleHttp.XcodeApp },
                    ["c"] = c,
                    ["checksum"] = GsaCrypto.AppTokenChecksum(sk, login.Adsid, AppleHttp.XcodeApp),
                    ["cpd"] = ClientProvidedData(anisette),
                    ["o"] = "apptokens",
                    ["t"] = login.IdmsToken,
                    ["u"] = login.Adsid,
                }, ct, candidate).ConfigureAwait(false);
                accepted = candidate;
                break;
            }
            catch (AppleException e) when (e is not AnisetteRepairNeeded && e.Code is not (-20101 or -20209 or -20210))
            {
                refused.Add($"{Short(candidate)}: {(e.Code != 0 ? e.Code.ToString(CultureInfo.InvariantCulture) : e.Message)}");
            }
        }
        if (response is null || accepted is null)
            throw new AppleException("Apple accepted the password and code but wouldn't give RigControl the developer token (" +
                string.Join("; ", refused) + ").", -80046);
        LastTokenClientInfo = accepted;

        var et = response.Data("et") ?? throw Malformed("et");
        var plain = GsaCrypto.DecryptAppToken(sk, et);
        PlistDict token;
        try
        {
            token = PlistFile.ParseDict(plain);
        }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // The token is secret: no inner exception, and only RigControl's own fixed plist sentences are kept.
            var why = e is RigControl.iPhone.Plist.PlistFormatException p ? " (" + p.Message + ")" : "";
            throw new AppleException("Apple's app token could not be read" + why + ".");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        var statusCode = token.Long("status-code");
        if (statusCode is not null and not 200)
            throw new AppleException($"Apple didn't give RigControl access to developer services (status {statusCode}).");
        var app = token.Dict("t")?.Dict(AppleHttp.XcodeApp) ?? throw Malformed("t");
        var value = Text(app.String("token"), "token", 8192);
        return (value, ExpiryOf(app) ?? DateTimeOffset.UtcNow.AddHours(24));
    }

    /// <summary>The description Apple accepted for the last developer token (saved with the session).</summary>
    internal string? LastTokenClientInfo { get; private set; }

    /// <summary>Descriptions tried for the developer token, most likely first.</summary>
    internal static IEnumerable<string> TokenClientInfos(string? provider)
    {
        var list = new List<string> { RigControl.iPhone.Anisette.CoreAdiAnisette.MacAkdClientInfo };
        if (AppleHttp.AuthKitClientInfo(provider) is { } authKit) list.Add(authKit);
        if (provider is not null) list.Add(provider);
        return list.Distinct(StringComparer.Ordinal);
    }

    private static string Short(string clientInfo) =>
        clientInfo.StartsWith("<MacBookPro", StringComparison.Ordinal) ? "Mac" :
        clientInfo.Contains("AuthKit/1 (com.apple.akd", StringComparison.Ordinal) ? "Windows+AuthKit" : "Windows";

    /// <summary>The PET (password-equivalent token) Apple may include in spd, with its expiry.</summary>
    internal static (string? Pet, DateTimeOffset? Expires) ReadPet(PlistDict spd)
    {
        var pet = spd.Dict("t")?.Dict("com.apple.gs.idms.pet");
        var token = pet?.String("token");
        if (token is null || token.Length is 0 or > 8192 || token.Any(char.IsControl))
            return (null, null);
        return (token, ExpiryOf(pet!));
    }

    // "expiry" is a Unix time (milliseconds in the replies seen so far; seconds accepted too); "duration" is seconds.
    internal static DateTimeOffset? ExpiryOf(PlistDict dict)
    {
        var now = DateTimeOffset.UtcNow;
        if (dict.Long("expiry") is { } expiry and > 0)
        {
            var when = expiry > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(expiry, 253_402_300_799_000))
                : DateTimeOffset.FromUnixTimeSeconds(expiry);
            if (when > now.AddYears(-1) && when < now.AddYears(10))
                return when;
        }
        if (dict.Long("duration") is { } duration and > 0 and < 10L * 365 * 24 * 3600)
            return now.AddSeconds(duration);
        return null;
    }

    private static string Text(string? value, string field, int max = 256)
    {
        if (value is null || value.Length == 0 || value.Length > max || value.Any(c => c < ' ' || c > '~'))
            throw Malformed(field);
        return value;
    }

    private static AppleException Malformed(string field) =>
        new($"Apple's sign-in server sent an incomplete reply (missing or bad '{field}').");
}
