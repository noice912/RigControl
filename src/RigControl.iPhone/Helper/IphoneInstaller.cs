using System.Security.Cryptography.Pkcs;
using System.Text.Json;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Apple;
using RigControl.iPhone.Device;
using RigControl.iPhone.Plist;
using RigControl.iPhone.Signing;

namespace RigControl.iPhone.Helper;

/// <summary>Where the installer keeps its files for this Windows user (all under %LOCALAPPDATA%\RigControl\iphone).</summary>
public sealed record InstallerPaths(string Root)
{
    public string Account => Path.Combine(Root, "account");
    public string Keys => Path.Combine(Root, "keys");
    public string Work => Path.Combine(Root, "work");
    public string Adi => Path.Combine(Root, "adi");
    public string Status => Path.Combine(Root, "status.json");
}

/// <summary>What was installed on one iPhone and when its signature runs out. Nothing secret.</summary>
public sealed record InstalledApp(string Udid, string DeviceName, string BundleId, string Version, DateTimeOffset SignedAt, DateTimeOffset Expires);

/// <summary>
/// The whole job: sign in once (password and code typed into this helper only), then for an iPhone: register it with
/// the person's free team, get RigControl's development certificate and a profile for the app, re-sign the .ipa and
/// install it over USB. Re-signing later uses the saved session; the password is never kept.
/// </summary>
public sealed class IphoneInstaller(
    HttpClient http,
    IAnisetteProvider anisette,
    ISecretProtector protector,
    DeviceService devices,
    InstallerPaths paths)
{
    /// <summary>Renew once the signature has this little time left (free profiles last 7 days: renew on day 6).</summary>
    public static readonly TimeSpan RenewWhenLeft = TimeSpan.FromDays(1);

    private readonly SessionStore _store = new(paths.Account, protector);

    public StoredAppleAccount? SavedAccount() => _store.Load(null);

    /// <summary>True when a saved session can still talk to Apple for at least an hour.</summary>
    public bool CanWorkWithoutPassword() => SavedAccount()?.SessionUsable(TimeSpan.FromHours(1)) == true;

    public async Task SignInAsync(string appleId, char[] password, ISecondFactorPrompt prompt, CancellationToken ct)
    {
        try
        {
            var client = new AppleAccountClient(http, anisette);
            var result = await client.SignInAsync(appleId, password, prompt, ct).ConfigureAwait(false);
            _store.Save(result);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public void SignOut() => _store.Delete();

    /// <summary>
    /// Signs <paramref name="ipaPath"/> (already checked against the signed mobile.json by the caller) for the iPhone
    /// <paramref name="udid"/> and installs it. Progress is plain text for the window.
    /// </summary>
    public async Task<InstalledApp> InstallAsync(string udid, string ipaPath, IProgress<string>? progress, CancellationToken ct)
    {
        var account = SavedAccount();
        if (account is null || !account.SessionUsable(TimeSpan.FromMinutes(10)))
            throw new AppleException("Sign in with your Apple ID first.");

        progress?.Report("Looking at your iPhone...");
        var device = (await devices.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Udid == udid)
            ?? throw new DeviceException("That iPhone isn't connected any more. Plug it in and try again.");
        if (await devices.DeveloperModeEnabledAsync(udid, ct).ConfigureAwait(false) == false)
            throw new DeviceException("Turn on Developer Mode on your iPhone first: Settings > Privacy & Security > " +
                "Developer Mode, then restart the iPhone and confirm.");

        var dev = new DeveloperServicesClient(http, anisette, account.Session);
        progress?.Report("Checking your Apple developer team...");
        var teams = await dev.ListTeamsAsync(ct).ConfigureAwait(false);
        var team = teams.FirstOrDefault() ?? throw new AppleException("Your Apple ID has no developer team yet. Sign in once at developer.apple.com to accept Apple's terms, then try again.");

        progress?.Report("Registering your iPhone with your team...");
        await dev.EnsureDeviceAsync(team.Id, udid, device.Name, ct).ConfigureAwait(false);

        progress?.Report("Getting your signing certificate...");
        var cert = await dev.GetOrCreateDevelopmentCertificateAsync(team.Id, paths.Keys, protector, ct).ConfigureAwait(false);

        var bundles = IpaInspector.ListBundles(ipaPath);
        var main = bundles.FirstOrDefault(b => !b.IsExtension) ?? throw new InvalidDataException("The phone app file has no app inside.");
        // Free teams can't use an id someone else registered: the team id makes it this person's own.
        var mainId = main.OriginalBundleId + "." + team.Id;
        var profiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        DateTimeOffset expires = DateTimeOffset.MaxValue;
        foreach (var b in bundles)
        {
            var id = b.IsExtension ? mainId + b.OriginalBundleId[main.OriginalBundleId.Length..] : mainId;
            progress?.Report($"Getting a profile for {id}...");
            var appId = await dev.EnsureAppIdAsync(team.Id, id, "RigControl Mobile", ct).ConfigureAwait(false);
            var profile = await dev.DownloadTeamProvisioningProfileAsync(team.Id, appId.Id, ct).ConfigureAwait(false);
            profiles[b.OriginalBundleId] = profile;
            var e = ProfileExpiry(profile);
            if (e < expires) expires = e;
        }

        Directory.CreateDirectory(paths.Work);
        var signed = Path.Combine(paths.Work, "RigControl-Mobile-signed.ipa");
        try
        {
            progress?.Report("Signing the app for your iPhone...");
            await IpaSigner.SignAsync(new SignRequest(ipaPath, signed, cert.Certificate, mainId, profiles), progress, ct).ConfigureAwait(false);

            progress?.Report("Installing on your iPhone...");
            var install = new Progress<InstallProgress>(p => progress?.Report($"{p.Stage} ({p.Percent}%)"));
            await devices.InstallAsync(udid, signed, install, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(signed); } catch (IOException) { }
        }

        var record = new InstalledApp(udid, device.Name, mainId, ReadVersion(ipaPath), DateTimeOffset.UtcNow,
            expires == DateTimeOffset.MaxValue ? DateTimeOffset.UtcNow.AddDays(7) : expires);
        SaveStatus(paths, record);
        progress?.Report("Done. RigControl Mobile is on your iPhone.");
        return record;
    }

    /// <summary>The profile's ExpirationDate (the CMS content is the profile plist).</summary>
    public static DateTimeOffset ProfileExpiry(byte[] profile)
    {
        var cms = new SignedCms();
        cms.Decode(profile);
        var plist = PlistFile.ParseDict(cms.ContentInfo.Content);
        return plist.Date("ExpirationDate") is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : DateTimeOffset.UtcNow.AddDays(7);
    }

    private static string ReadVersion(string ipaPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(ipaPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("Payload/", StringComparison.Ordinal) && e.FullName.EndsWith(".app/Info.plist", StringComparison.Ordinal)
                && e.FullName.Count(c => c == '/') == 2);
            if (entry is null || entry.Length > 1 << 20) return "";
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return PlistFile.ParseDict(ms.ToArray()).String("CFBundleShortVersionString") ?? "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            return "";
        }
    }

    // ---- status.json: what is installed where, for the renewal check ------------------------------------------------

    public static IReadOnlyList<InstalledApp> LoadStatus(InstallerPaths paths)
    {
        try
        {
            if (!File.Exists(paths.Status) || new FileInfo(paths.Status).Length > 1 << 20) return [];
            return JsonSerializer.Deserialize<List<InstalledApp>>(File.ReadAllBytes(paths.Status)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void SaveStatus(InstallerPaths paths, InstalledApp record)
    {
        var list = LoadStatus(paths).Where(a => a.Udid != record.Udid).Append(record).ToList();
        Directory.CreateDirectory(paths.Root);
        var temp = paths.Status + ".new";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, paths.Status, overwrite: true);
    }

    /// <summary>True when the app on this iPhone should be signed again now.</summary>
    public static bool RenewalDue(InstallerPaths paths, string udid, DateTimeOffset now) =>
        LoadStatus(paths).FirstOrDefault(a => a.Udid == udid) is { } app && app.Expires - now <= RenewWhenLeft;
}
