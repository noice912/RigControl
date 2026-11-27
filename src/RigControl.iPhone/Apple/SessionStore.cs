using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.iPhone.Apple;

/// <summary>What is kept between runs: the developer-services session and, if Apple sent one, the PET. Never the password.</summary>
public sealed record StoredAppleAccount(
    AppleSession Session,
    string? PasswordEquivalentToken,
    DateTimeOffset? PasswordEquivalentTokenExpires)
{
    /// <summary>The Xcode token still has at least <paramref name="margin"/> to run.</summary>
    public bool SessionUsable(TimeSpan margin) => Session.Expires - margin > DateTimeOffset.UtcNow;
}

/// <summary>
/// Keeps the signed-in session on disk so the app can be re-signed within its 7 days without asking for the
/// password and a code again. One file, written atomically, protected by <see cref="ISecretProtector"/>
/// (DPAPI for the current user in the app). A file that can't be unprotected or read is treated as absent.
/// </summary>
public sealed class SessionStore
{
    private const string FileName = "apple-session.bin";
    private const int MaxFileBytes = 256 * 1024;

    private readonly string _folder;
    private readonly ISecretProtector _protector;

    public SessionStore(string folder, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        _folder = folder;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    private string PathName => Path.Combine(_folder, FileName);

    public void Save(StoredAppleAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Directory.CreateDirectory(_folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(Saved.From(account), Json);
        byte[] protectedBytes;
        try
        {
            protectedBytes = _protector.Protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        var temp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, PathName, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>
    /// Saves the developer-services session only. The PET (a password-equivalent token) is NOT kept: nothing uses it,
    /// and any program running as this Windows user could read it back from DPAPI.
    /// </summary>
    public void Save(AppleSignIn signIn) =>
        Save(new StoredAppleAccount(signIn.Session, null, null));

    /// <summary>The saved account, or null when there is none (or it is damaged, or for another Apple ID).</summary>
    public StoredAppleAccount? Load(string? appleId = null)
    {
        var path = PathName;
        if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaxFileBytes)
            return null;
        byte[] plain;
        try
        {
            plain = _protector.Unprotect(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(plain, Json);
            var account = saved?.To();
            if (account is null)
                return null;
            if (appleId is not null && !string.Equals(account.Session.AppleId, appleId.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
            // A PET written by an earlier build is never handed out (and is gone at the next save).
            return account with { PasswordEquivalentToken = null, PasswordEquivalentTokenExpires = null };
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Forgets the saved account (sign out).</summary>
    public void Delete()
    {
        if (File.Exists(PathName))
            File.Delete(PathName);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
    };

    private sealed class Saved
    {
        public int Version { get; set; } = 1;
        public string? AppleId { get; set; }
        public string? Adsid { get; set; }
        public string? Dsid { get; set; }
        public string? XcodeToken { get; set; }
        public DateTimeOffset Expires { get; set; }
        public string? Pet { get; set; }
        public DateTimeOffset? PetExpires { get; set; }
        public string? ClientInfo { get; set; }

        public static Saved From(StoredAppleAccount a) => new()
        {
            AppleId = a.Session.AppleId,
            Adsid = a.Session.Adsid,
            Dsid = a.Session.Dsid,
            XcodeToken = a.Session.XcodeToken,
            Expires = a.Session.Expires,
            Pet = a.PasswordEquivalentToken,
            PetExpires = a.PasswordEquivalentTokenExpires,
            ClientInfo = a.Session.ClientInfo,
        };

        public StoredAppleAccount? To()
        {
            if (Version != 1 || string.IsNullOrEmpty(AppleId) || string.IsNullOrEmpty(Adsid) || string.IsNullOrEmpty(XcodeToken))
                return null;
            // The accepted client description goes into an HTTP header: printable ASCII and short only.
            var clientInfo = ClientInfo is { Length: > 0 and <= 300 } c && c.All(ch => ch is >= ' ' and <= '~') ? c : null;
            return new StoredAppleAccount(new AppleSession(AppleId, Adsid, Dsid ?? "", XcodeToken, Expires, clientInfo), Pet, PetExpires);
        }
    }
}
