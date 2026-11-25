using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RigControl.Updates;

/// <summary>
/// A key that lives in this PC's TPM and is used through Windows Hello (a PIN, a face or a finger). The private half
/// never leaves the chip; every signature asks the user first. Tests use a fake.
/// </summary>
public interface IDeviceKey
{
    /// <summary>False when Windows Hello is not set up on this PC (or this system has none).</summary>
    Task<bool> SupportedAsync();

    /// <summary>Makes a new key (replacing an earlier one) and returns its public half (X.509 SubjectPublicKeyInfo).</summary>
    Task<byte[]> CreateAsync();

    /// <summary>The public half of the existing key, or null when this PC has none. Never asks the user.</summary>
    Task<byte[]?> OpenAsync();

    /// <summary>Signs <paramref name="data"/> with the key (RSA, PKCS#1 v1.5, SHA-256). Windows Hello asks the user first.</summary>
    Task<byte[]> SignAsync(byte[] data);

    /// <summary>Deletes the key from this PC (nothing happens when there is none).</summary>
    Task DeleteAsync();
}

/// <summary>What a checked enrollment token says.</summary>
public sealed record DevTokenInfo(string? Subject, string? Id, string? IssuedAt, string DeviceHash, byte[] Spki);

/// <summary>
/// Dev mode is bound to one PC's TPM key:
/// <list type="number">
/// <item>"Register this PC" makes a Windows Hello key and shows a request: <c>RCREQ2.&lt;base64url SPKI&gt;</c>.</item>
/// <item>The owner turns the request into a token with the issuer key (tools/DevEnroll):
/// <c>RCDEV2.&lt;payload&gt;.&lt;signature&gt;</c>, ECDSA P-256 / SHA-256 / IEEE P1363 over the ASCII text <c>RCDEV2.&lt;payload&gt;</c>,
/// payload {"v":2,"sub","scope":["dev"],"iat":"yyyy-mm-dd","id":"&lt;16 hex&gt;","device":"&lt;base64url SHA-256 of SPKI&gt;","spki":"&lt;base64url SPKI&gt;"}.</item>
/// <item>Unlocking checks the token, that this PC's key is the one in it, and has the key sign a fresh challenge
/// (Windows Hello asks the user). A token copied to another PC unlocks nothing, and neither does a key without the token.</item>
/// </list>
/// The earlier pasted keys (RCDEV1) are refused. Dev mode only opens RigControl's dev pages: publishing to GitHub still
/// needs the owner's own GitHub sign-in.
/// </summary>
public static class DevEnrollment
{
    public const string RequestPrefix = "RCREQ2";
    public const string TokenPrefix = "RCDEV2";
    public const int MaxLength = 8192;

    /// <summary>The text the user copies to the owner: RCREQ2.&lt;base64url SPKI&gt;.</summary>
    public static string MakeRequest(byte[] spki) => RequestPrefix + "." + ToBase64Url(spki);

    /// <summary>The public key in a request, or null with a plain reason.</summary>
    public static byte[]? ReadRequest(string? request, out string? problem)
    {
        problem = null;
        var parts = (request ?? "").Trim().Split('.');
        if (parts.Length != 2 || parts[0] != RequestPrefix || !TryBase64Url(parts[1], out var spki))
        {
            problem = "That is not a RigControl registration request (it starts with RCREQ2.).";
            return null;
        }
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
        }
        catch (CryptographicException)
        {
            problem = "The registration request does not hold a usable public key.";
            return null;
        }
        return spki;
    }

    /// <summary>The device id in a token: base64url of the SHA-256 of the key's SubjectPublicKeyInfo.</summary>
    public static string DeviceHash(byte[] spki) => ToBase64Url(SHA256.HashData(spki));

    /// <summary>Makes a token for a PC (tools/DevEnroll and tests). The issuer key is only used, never written anywhere.</summary>
    public static string Issue(ECDsa issuer, byte[] spki, string subject, DateTimeOffset issued, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        id ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 2);
            w.WriteString("sub", subject);
            w.WriteStartArray("scope");
            w.WriteStringValue("dev");
            w.WriteEndArray();
            w.WriteString("iat", issued.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("id", id);
            w.WriteString("device", DeviceHash(spki));
            w.WriteString("spki", ToBase64Url(spki));
            w.WriteEndObject();
        }
        var signed = TokenPrefix + "." + ToBase64Url(buffer.ToArray());
        var sig = issuer.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signed + "." + ToBase64Url(sig);
    }

    /// <summary>
    /// Checks a token's own signature and contents (not yet that this PC holds the key: see <see cref="UnlockAsync"/>).
    /// Returns null with a plain reason in <paramref name="problem"/>.
    /// </summary>
    public static DevTokenInfo? Verify(string? token, string issuerPublicKey, out string? problem)
    {
        problem = null;
        var text = (token ?? "").Trim();
        if (text.Length == 0) { problem = "Paste the token you were sent first."; return null; }
        if (text.Length > MaxLength) { problem = "That is far too long to be a dev token."; return null; }
        if (text.StartsWith("RCDEV1.", StringComparison.Ordinal))
        {
            problem = "Dev keys of the old kind (RCDEV1) no longer unlock anything. Register this PC and ask for a token.";
            return null;
        }

        var parts = text.Split('.');
        if (parts.Length != 3 || parts[0] != TokenPrefix)
        {
            problem = "That is not a RigControl dev token (it starts with RCDEV2. and has three parts).";
            return null;
        }
        if (!TryBase64Url(parts[1], out var payload) || !TryBase64Url(parts[2], out var signature))
        {
            problem = "That dev token is damaged: part of it is not readable.";
            return null;
        }
        if (!EcSignature.Verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, issuerPublicKey))
        {
            problem = "That dev token was not issued for RigControl (its signature does not check out).";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 2)
            {
                problem = "That dev token is of a kind this version of RigControl does not know.";
                return null;
            }
            if (!root.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Array
                || !scope.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && s.GetString() == "dev"))
            {
                problem = "That dev token does not allow dev mode.";
                return null;
            }
            var device = Text(root, "device");
            var spkiText = Text(root, "spki");
            if (device is null || spkiText is null || !TryBase64Url(spkiText, out var spki) || DeviceHash(spki) != device)
            {
                problem = "That dev token does not name a PC correctly.";
                return null;
            }
            return new DevTokenInfo(Text(root, "sub"), Text(root, "id"), Text(root, "iat"), device, spki);
        }
        catch (JsonException)
        {
            problem = "That dev token is damaged: what it says cannot be read.";
            return null;
        }
    }

    /// <summary>
    /// The full check: the token is genuine, it names this PC's key, and the key signs a fresh random challenge
    /// (this is where Windows Hello asks the user). Returns null when unlocked, otherwise a plain reason.
    /// </summary>
    public static async Task<string?> UnlockAsync(string? token, IDeviceKey key, string issuerPublicKey)
    {
        var info = Verify(token, issuerPublicKey, out var problem);
        if (info is null) return problem;
        if (!await key.SupportedAsync().ConfigureAwait(false)) return "Set up Windows Hello (a PIN is enough) first.";
        var spki = await key.OpenAsync().ConfigureAwait(false);
        if (spki is null) return "This PC is not registered for dev mode (its Windows Hello key is missing). Register it again.";
        if (DeviceHash(spki) != info.DeviceHash || !CryptographicOperations.FixedTimeEquals(spki, info.Spki))
            return "The dev token was made for another PC (or for an earlier registration of this one).";

        var challenge = RandomNumberGenerator.GetBytes(32);
        byte[] signature;
        try
        {
            signature = await key.SignAsync(challenge).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "Windows Hello was cancelled, so dev mode stays locked.";
        }
        return VerifyDeviceSignature(spki, challenge, signature)
            ? null
            : "This PC's key did not sign correctly, so dev mode stays locked.";
    }

    /// <summary>RSA PKCS#1 v1.5 with SHA-256, the kind of signature a Windows Hello key makes.</summary>
    public static bool VerifyDeviceSignature(byte[] spki, byte[] data, byte[] signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) ? el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        } : null;

    public static bool TryBase64Url(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Dev mode stays open for this run of the app only, and closes after 15 minutes without use (or on exit, since
/// nothing of it is stored).
/// </summary>
public sealed class DevSession
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);

    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private DateTimeOffset? _lastUse;

    public DevSession(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Unlocked
    {
        get
        {
            lock (_gate)
            {
                if (_lastUse is { } t && _now() - t < IdleLimit) return true;
                _lastUse = null;
                return false;
            }
        }
    }

    /// <summary>When it closes if nothing else happens; null when closed.</summary>
    public DateTimeOffset? LocksAt
    {
        get { lock (_gate) return Unlocked ? _lastUse + IdleLimit : null; }
    }

    public void Open()
    {
        lock (_gate) _lastUse = _now();
    }

    /// <summary>Counts as use: the 15 minutes start again. Returns false (and does nothing) when already closed.</summary>
    public bool Touch()
    {
        lock (_gate)
        {
            if (_lastUse is not { } t || _now() - t >= IdleLimit) { _lastUse = null; return false; }
            _lastUse = _now();
            return true;
        }
    }

    public void Close()
    {
        lock (_gate) _lastUse = null;
    }
}
