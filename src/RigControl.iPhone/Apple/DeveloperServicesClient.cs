using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Apple;

/// <summary>A development certificate as Apple lists it (no private key).</summary>
public sealed record DevelopmentCertificateInfo(
    string CertificateId,
    string SerialNumber,
    string? Name,
    string? MachineName,
    string? MachineId,
    string? Status,
    byte[]? Content,
    DateTime? Expires);

/// <summary>The App IDs of a team plus Apple's limits when it states them.</summary>
public sealed record AppIdList(IReadOnlyList<AppId> AppIds, long? MaxQuantity, long? AvailableQuantity);

/// <summary>
/// Apple's developer services (the "QH65B2" API Xcode uses), enough for a free Apple ID: teams, devices,
/// development certificates, App IDs and team provisioning profiles. Every call needs a signed-in
/// <see cref="AppleSession"/>; Apple's refusals come back as <see cref="AppleException"/> with Apple's own text.
/// </summary>
public sealed class DeveloperServicesClient
{
    public const string BaseUrl = "https://developerservices2.apple.com/services/QH65B2/";
    public const string ClientId = "XABBG36SBA";
    public const string ProtocolVersion = "QH65B2";
    public const string MachineName = "RigControl";

    private const string KeyFile = "development-key.bin";
    private const string MachineIdFile = "machine-id.txt";
    private const int MaxProfileBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IAnisetteProvider _anisette;
    private readonly AppleSession _session;

    /// <summary>Wait between "submitted" and "listed" when Apple hasn't published the new certificate yet.</summary>
    internal TimeSpan CertificateRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public DeveloperServicesClient(HttpClient http, IAnisetteProvider anisette, AppleSession session)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _anisette = anisette ?? throw new ArgumentNullException(nameof(anisette));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    // ---- Teams ----

    public async Task<IReadOnlyList<DeveloperTeam>> ListTeamsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync("listTeams.action", new PlistDict(), cancellationToken).ConfigureAwait(false);
        return Items(reply, "teams").Select(t => new DeveloperTeam(
            Id(t.String("teamId"), "teamId"),
            Clean(t.String("name")) ?? "",
            Clean(t.String("type")) ?? "")).ToList();
    }

    // ---- Devices ----

    public async Task<IReadOnlyList<RegisteredDevice>> ListDevicesAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync("ios/listDevices.action", Team(teamId), cancellationToken).ConfigureAwait(false);
        return Items(reply, "devices").Select(Device).ToList();
    }

    public async Task<RegisteredDevice> AddDeviceAsync(string teamId, string udid, string name, CancellationToken cancellationToken = default)
    {
        var body = Team(teamId);
        body["deviceNumber"] = Udid(udid);
        body["name"] = DeviceName(name);
        var reply = await RequestAsync("ios/addDevice.action", body, cancellationToken).ConfigureAwait(false);
        return Device(reply.Dict("device") ?? throw Malformed("device"));
    }

    /// <summary>Registers the device unless the team already has it.</summary>
    public async Task<RegisteredDevice> EnsureDeviceAsync(string teamId, string udid, string name, CancellationToken cancellationToken = default)
    {
        var existing = (await ListDevicesAsync(teamId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(d => string.Equals(d.Udid, udid, StringComparison.OrdinalIgnoreCase));
        return existing ?? await AddDeviceAsync(teamId, udid, name, cancellationToken).ConfigureAwait(false);
    }

    private static RegisteredDevice Device(PlistDict d) => new(
        Clean(d.String("deviceId")) ?? "",
        Id(d.String("deviceNumber"), "deviceNumber"),
        Clean(d.String("name")) ?? "");

    // ---- Certificates ----

    public async Task<IReadOnlyList<DevelopmentCertificateInfo>> ListDevelopmentCertificatesAsync(string teamId,
        CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync("ios/listAllDevelopmentCerts.action", Team(teamId), cancellationToken).ConfigureAwait(false);
        var list = new List<DevelopmentCertificateInfo>();
        foreach (var c in Items(reply, "certificates"))
        {
            var content = c.Data("certContent");
            if (content is { Length: > 64 * 1024 })
                content = null;
            list.Add(new DevelopmentCertificateInfo(
                Id(c.String("certificateId"), "certificateId"),
                Id(c.String("serialNumber") ?? c.String("serialNum"), "serialNumber"),
                Clean(c.String("name")),
                Clean(c.String("machineName")),
                Clean(c.String("machineId")),
                Clean(c.String("status")),
                content,
                c.Date("expirationDate")));
        }
        return list;
    }

    /// <summary>Sends a PKCS#10 request (PEM). Returns Apple's certificate id for the new certificate.</summary>
    public async Task<string> SubmitDevelopmentCsrAsync(string teamId, string csrPem, string machineId,
        CancellationToken cancellationToken = default)
    {
        var body = Team(teamId);
        body["csrContent"] = csrPem;
        body["machineId"] = machineId;
        body["machineName"] = MachineName;
        var reply = await RequestAsync("ios/submitDevelopmentCSR.action", body, cancellationToken).ConfigureAwait(false);
        var request = reply.Dict("certRequest") ?? throw Malformed("certRequest");
        return Id(request.String("certificateId"), "certificateId");
    }

    public async Task RevokeDevelopmentCertificateAsync(string teamId, string serialNumber, CancellationToken cancellationToken = default)
    {
        var body = Team(teamId);
        body["serialNumber"] = Id(serialNumber, "serialNumber");
        await RequestAsync("ios/revokeDevelopmentCert.action", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// RigControl's development certificate for the team, with its private key. Reuses the one Apple already has if
    /// its key is still in <paramref name="keyFolder"/>; otherwise revokes certificates RigControl made earlier
    /// (machine name "RigControl", key lost) and makes a new one. Certificates made by other tools are never revoked:
    /// when a free account is at its limit, the error says so.
    /// </summary>
    public async Task<DevelopmentCertificate> GetOrCreateDevelopmentCertificateAsync(string teamId, string keyFolder,
        ISecretProtector protector, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyFolder);
        ArgumentNullException.ThrowIfNull(protector);
        var keyPath = Path.Combine(keyFolder, KeyPrefix(teamId) + KeyFile);
        var certificates = await ListDevelopmentCertificatesAsync(teamId, cancellationToken).ConfigureAwait(false);

        using (var stored = LoadKey(keyPath, protector))
        {
            if (stored is not null)
            {
                var publicKey = stored.ExportSubjectPublicKeyInfo();
                foreach (var info in certificates)
                {
                    if (info.Content is null || (info.Expires is { } exp && exp <= DateTime.UtcNow.AddHours(1)))
                        continue;
                    using var cert = TryLoad(info.Content);
                    if (cert?.GetRSAPublicKey() is { } rsa && rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey))
                        return Attach(info, stored);
                }
            }
        }

        // Only certificates THIS PC made (same machine id) are revoked: one made by RigControl on another PC or
        // Windows user may still be signing apps there.
        var thisMachine = MachineId(keyFolder);
        foreach (var old in certificates.Where(c => c.MachineName == MachineName && c.MachineId == thisMachine))
            await RevokeDevelopmentCertificateAsync(teamId, old.SerialNumber, cancellationToken).ConfigureAwait(false);

        using var key = RSA.Create(2048);
        var csr = CreateCsr(key);
        string certificateId;
        try
        {
            certificateId = await SubmitDevelopmentCsrAsync(teamId, csr, thisMachine, cancellationToken).ConfigureAwait(false);
        }
        catch (AppleException e) when (e.Code == 7460)
        {
            throw new AppleException(
                "This Apple ID already has as many development certificates as Apple allows (other apps such as Xcode, " +
                "AltStore or Sideloadly made them). Revoke one in that app, or at https://developer.apple.com/account " +
                "(Certificates), then try again. Apple said: " + e.Message, e.Code, e);
        }
        SaveKey(keyPath, protector, key);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(CertificateRetryDelay, cancellationToken).ConfigureAwait(false);
            var info = (await ListDevelopmentCertificatesAsync(teamId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(c => c.CertificateId == certificateId && c.Content is not null);
            if (info is not null)
                return Attach(info, key);
        }
        throw new AppleException("Apple made the development certificate but didn't list it yet. Try again in a minute.");
    }

    /// <summary>A PKCS#10 certificate request (PEM) for the key. Apple replaces the subject with its own.</summary>
    internal static string CreateCsr(RSA key)
    {
        var request = new CertificateRequest("CN=RigControl, O=RigControl, C=US", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSigningRequestPem();
    }

    private static DevelopmentCertificate Attach(DevelopmentCertificateInfo info, RSA key)
    {
        using var publicOnly = TryLoad(info.Content!) ?? throw new AppleException("Apple sent a development certificate RigControl couldn't read.");
        // The certificate gets its own copy of the key, so the caller's RSA object can be disposed.
        X509Certificate2 withKey;
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var copy = RSA.Create();
        try
        {
            copy.ImportPkcs8PrivateKey(pkcs8, out _);
            withKey = publicOnly.CopyWithPrivateKey(copy);
        }
        catch (CryptographicException e)
        {
            copy.Dispose();
            throw new AppleException("Apple's development certificate doesn't match RigControl's key.", 0, e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        return new DevelopmentCertificate(withKey, info.SerialNumber, info.CertificateId);
    }

    private static X509Certificate2? TryLoad(byte[] der)
    {
        try
        {
            return new X509Certificate2(der);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // One key file per team, so two Apple IDs on one PC don't overwrite each other's keys.
    private static string KeyPrefix(string teamId) => Id(teamId, "teamId") + "-";

    private static RSA? LoadKey(string path, ISecretProtector protector)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is 0 or > 64 * 1024)
            return null;
        byte[]? pkcs8 = null;
        try
        {
            pkcs8 = protector.Unprotect(File.ReadAllBytes(path));
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
        finally
        {
            if (pkcs8 is not null)
                CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static void SaveKey(string path, ISecretProtector protector, RSA key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        byte[] protectedBytes;
        try
        {
            protectedBytes = protector.Protect(pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>A random id made once and kept in the key folder, so Apple sees the same machine every time.</summary>
    internal static string MachineId(string keyFolder)
    {
        var path = Path.Combine(keyFolder, MachineIdFile);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (Guid.TryParse(text, out var existing))
                return existing.ToString("D").ToUpperInvariant();
        }
        Directory.CreateDirectory(keyFolder);
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        File.WriteAllText(path, id);
        return id;
    }

    // ---- App IDs and profiles ----

    public async Task<AppIdList> ListAppIdsAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync("ios/listAppIds.action", Team(teamId), cancellationToken).ConfigureAwait(false);
        var ids = Items(reply, "appIds").Select(AppIdFrom).ToList();
        return new AppIdList(ids, reply.Long("maxQuantity"), reply.Long("availableQuantity"));
    }

    /// <summary>
    /// Adds an App ID. A free account may add 10 in 7 days (and run 3 apps); Apple's refusal text is passed on.
    /// </summary>
    public async Task<AppId> AddAppIdAsync(string teamId, string identifier, string name, CancellationToken cancellationToken = default)
    {
        var body = Team(teamId);
        body["identifier"] = BundleId(identifier);
        body["name"] = AppIdName(name);
        var reply = await RequestAsync("ios/addAppId.action", body, cancellationToken).ConfigureAwait(false);
        return AppIdFrom(reply.Dict("appId") ?? throw Malformed("appId"));
    }

    /// <summary>Turns App ID features on or off (feature codes as keys, e.g. "APG3427HIY" for app groups).</summary>
    public async Task<AppId> UpdateAppIdAsync(string teamId, string appIdId, PlistDict features, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        var body = Team(teamId);
        body["appIdId"] = Id(appIdId, "appIdId");
        foreach (var (k, v) in features)
        {
            if (k is "teamId" or "appIdId" or "clientId" or "protocolVersion" or "requestId" or "userLocale")
                throw new ArgumentException($"'{k}' isn't a feature.", nameof(features));
            body[k] = v;
        }
        var reply = await RequestAsync("ios/updateAppId.action", body, cancellationToken).ConfigureAwait(false);
        return AppIdFrom(reply.Dict("appId") ?? throw Malformed("appId"));
    }

    /// <summary>The team's App ID for this bundle id, added if missing.</summary>
    public async Task<AppId> EnsureAppIdAsync(string teamId, string identifier, string name, CancellationToken cancellationToken = default)
    {
        var list = await ListAppIdsAsync(teamId, cancellationToken).ConfigureAwait(false);
        return list.AppIds.FirstOrDefault(a => string.Equals(a.Identifier, identifier, StringComparison.Ordinal))
            ?? await AddAppIdAsync(teamId, identifier, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The team provisioning profile (.mobileprovision bytes) for the App ID.</summary>
    public async Task<byte[]> DownloadTeamProvisioningProfileAsync(string teamId, string appIdId, CancellationToken cancellationToken = default)
    {
        var body = Team(teamId);
        body["appIdId"] = Id(appIdId, "appIdId");
        var reply = await RequestAsync("ios/downloadTeamProvisioningProfile.action", body, cancellationToken).ConfigureAwait(false);
        var profile = reply.Dict("provisioningProfile")?.Data("encodedProfile") ?? throw Malformed("encodedProfile");
        if (profile.Length is 0 or > MaxProfileBytes)
            throw Malformed("encodedProfile");
        return profile;
    }

    private static AppId AppIdFrom(PlistDict d) => new(
        Id(d.String("appIdId"), "appIdId"),
        Id(d.String("identifier"), "identifier"),
        Clean(d.String("name")) ?? "");

    // ---- Requests ----

    private async Task<PlistDict> RequestAsync(string action, PlistDict fields, CancellationToken ct)
    {
        var anisette = await AppleHttp.AnisetteAsync(_anisette, ct).ConfigureAwait(false);
        var body = new PlistDict
        {
            ["clientId"] = ClientId,
            ["protocolVersion"] = ProtocolVersion,
            ["requestId"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            ["userLocale"] = new List<object> { "en_US" },
        };
        foreach (var (k, v) in fields)
            body[k] = v;

        using var message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + action + "?clientId=" + ClientId)
        {
            Content = new ByteArrayContent(PlistFile.ToXml(body)),
        };
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "text/x-xml-plist");
        foreach (var (k, v) in anisette)
            if (!string.Equals(k, "X-Mme-Client-Info", StringComparison.OrdinalIgnoreCase))
                message.Headers.TryAddWithoutValidation(k, v);
        // The description Apple accepted for the developer token, else the AuthKit form of the provider's.
        var clientInfo = _session.ClientInfo ?? AppleHttp.AuthKitClientInfo(AppleHttp.Find(anisette, "X-Mme-Client-Info"));
        if (clientInfo is not null)
            message.Headers.TryAddWithoutValidation("X-Mme-Client-Info", clientInfo);
        message.Headers.TryAddWithoutValidation("Accept", "text/x-xml-plist");
        message.Headers.TryAddWithoutValidation("Accept-Language", "en-us");
        message.Headers.TryAddWithoutValidation("User-Agent", "Xcode");
        message.Headers.TryAddWithoutValidation("X-Xcode-Version", AppleHttp.XcodeVersion);
        message.Headers.TryAddWithoutValidation("X-Apple-App-Info", AppleHttp.XcodeApp);
        message.Headers.TryAddWithoutValidation("X-Apple-I-Identity-Id", _session.Adsid);
        message.Headers.TryAddWithoutValidation("X-Apple-GS-Token", _session.XcodeToken);

        var reply = await AppleHttp.SendAsync(_http, message, ct).ConfigureAwait(false);
        PlistDict dict;
        try
        {
            dict = PlistFile.ParseDict(reply.Body);
        }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException or OverflowException or ArgumentException)
        {
            throw reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new AppleException("The Apple sign-in has expired. Sign in again.", 401, e)
                : new AppleException($"Apple's developer service sent a reply RigControl couldn't read (HTTP {(int)reply.Status}).", 0, e);
        }

        var code = dict.Long("resultCode") ?? 0;
        if (code != 0)
        {
            var text = Clean(dict.String("userString"), 1000) ?? Clean(dict.String("resultString"), 1000);
            throw new AppleException(text is null
                ? $"Apple's developer service refused the request (code {code})."
                : AppleErrors.Trim(text), (int)Math.Clamp(code, int.MinValue, int.MaxValue));
        }
        if ((int)reply.Status is < 200 or > 299)
            throw reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new AppleException("The Apple sign-in has expired. Sign in again.", 401)
                : new AppleException($"Apple's developer service refused the request (HTTP {(int)reply.Status}).");
        return dict;
    }

    private static PlistDict Team(string teamId) => new() { ["teamId"] = Id(teamId, "teamId") };

    private static IEnumerable<PlistDict> Items(PlistDict reply, string key)
    {
        var list = reply.Array(key) ?? throw Malformed(key);
        if (list.Count > 10_000)
            throw Malformed(key);
        foreach (var item in list)
            yield return item as PlistDict ?? throw Malformed(key);
    }

    // Ids, serials, UDIDs, bundle ids: printable ASCII without spaces, bounded.
    private static string Id(string? value, string field)
    {
        if (value is null || value.Length is 0 or > 256 || value.Any(c => c <= ' ' || c > '~'))
            throw Malformed(field);
        return value;
    }

    private static string? Clean(string? value, int max = 256)
    {
        if (value is null)
            return null;
        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length > max ? text[..max] : text;
    }

    private static string Udid(string udid)
    {
        var u = udid?.Trim() ?? "";
        if (u.Length is < 24 or > 64 || !u.All(c => char.IsAsciiHexDigit(c) || c == '-'))
            throw new ArgumentException("That isn't an iPhone UDID.", nameof(udid));
        return u;
    }

    private static string DeviceName(string name)
    {
        var n = Clean(name, 50);
        return string.IsNullOrEmpty(n) ? "iPhone" : n;
    }

    private static string BundleId(string identifier)
    {
        var id = identifier?.Trim() ?? "";
        if (id.Length is 0 or > 155 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            throw new ArgumentException("That isn't a valid bundle identifier.", nameof(identifier));
        return id;
    }

    // Apple refuses App ID names with most punctuation (error 35), so only letters and digits go through.
    internal static string AppIdName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(c);
        var n = sb.ToString();
        return n.Length == 0 ? "App" : n.Length > 50 ? n[..50] : n;
    }

    private static AppleException Malformed(string field) =>
        new($"Apple's developer service sent an incomplete reply (missing or bad '{field}').");
}
