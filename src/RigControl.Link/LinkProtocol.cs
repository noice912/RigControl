using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.Link;

/// <summary>
/// The fixed strings of protocol v1 (docs/protocols/link.md). Every signed or hashed text is built here and nowhere
/// else, so the PC, the phone and the tests all use exactly the same bytes.
///
/// Choices made where the protocol document leaves room (both ends use this library, so they always agree):
/// <list type="bullet">
/// <item>Public keys travel as base64url (no padding) of their SubjectPublicKeyInfo DER, in hello, offer and claim.</item>
/// <item>The pairing code is normalised (upper case, dashes and spaces removed, Crockford's O to 0 and I/L to 1)
///   before it is hashed for "h" or used as HKDF input. The PC only ever shows canonical letters.</item>
/// <item>Offer and claim boxes are sealed without additional authenticated data (the document names none); their
///   JSON shapes differ, so one can never be read as the other.</item>
/// <item>The session key is the static-static one, bound to the pairing: HKDF(ECDH(static_pc, static_phone),
///   salt = sorted ids joined by '|' + '|' + epoch, info "rigcontrol-link-v1 session"). The epoch is 16 random bytes
///   (base64url) the PC makes for each pairing and sends in the offer box ("epoch"); it is also the last part of the
///   AAD. So frames from an earlier pairing of the same phone can't be opened after it is linked again.
///   The ephemeral exchange is not in v1.</item>
/// <item>Message nonce layout: byte 0 = sender (1 PC, 2 phone), bytes 1-3 random, bytes 4-11 the counter
///   (unsigned 64-bit big-endian). The receiver needs the counter for the AAD before it can decrypt, and the
///   plaintext "n" must equal it. The direction byte keeps PC and phone nonces apart under the shared key.</item>
/// <item>Counters start at the sender's clock in microseconds since 1970 (and then go up by one), so they keep
///   rising across restarts without the sender having to store them; the receiver stores the last one it took.</item>
/// <item>"result" and "snapshot" carry "re" = the "n" of the request they answer.</item>
/// <item>The allow-list signature joins the ids in the order they are sent; the PC sends them sorted (ordinal).</item>
/// </list>
/// </summary>
public static class LinkProtocol
{
    public const string Version = "rigcontrol-link-v1";

    /// <summary>Max size of one WebSocket text frame, both ways.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>
    /// Longest one frame may take to go out. A send stuck longer (a connection that no longer moves) drops the
    /// connection, so the link connects again instead of waiting forever.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The PC refuses a phone message whose counter (microseconds since 1970) is more than this far behind its own clock
    /// (answered "stale"), so a message held back or delayed by the relay can't be used later.
    /// </summary>
    public static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(2);

    /// <summary>... or more than this far ahead of it.</summary>
    public static readonly TimeSpan MaxMessageAhead = TimeSpan.FromSeconds(30);

    /// <summary>True when a counter (microseconds since 1970) is inside the window around <paramref name="now"/>.</summary>
    public static bool IsFresh(long counter, DateTimeOffset now)
    {
        var us = now.ToUnixTimeMilliseconds() * 1000;
        return counter >= us - (long)MaxMessageAge.TotalMicroseconds && counter <= us + (long)MaxMessageAhead.TotalMicroseconds;
    }

    public const string RolePc = "pc";
    public const string RolePhone = "phone";

    /// <summary>UTF-8 'rigcontrol-link-v1|' + nonce + '|' + room: what the hello signature covers.</summary>
    public static string HelloSignedText(string nonce, string room) => Version + "|" + nonce + "|" + room;

    /// <summary>UTF-8 'rigcontrol-link-v1|allow|' + room + '|' + ids joined by ','.</summary>
    public static string AllowSignedText(string room, IEnumerable<string> phoneIds)
        => Version + "|allow|" + room + "|" + string.Join(",", phoneIds);

    /// <summary>
    /// AAD of a sealed message: 'rigcontrol-link-v1|' + from + '|' + to + '|' + counter (decimal) + '|' + epoch.
    /// The epoch is the pairing's (see <see cref="LinkCrypto.SessionKey"/>).
    /// </summary>
    public static string MessageAad(string from, string to, long counter, string epoch)
        => Version + "|" + from + "|" + to + "|" + counter.ToString(CultureInfo.InvariantCulture) + "|" + epoch;

    /// <summary>Bytes of a pairing epoch (made by the PC for every pairing, sent in the offer box).</summary>
    public const int EpochBytes = 16;

    /// <summary>True for a base64url epoch of <see cref="EpochBytes"/> bytes.</summary>
    public static bool IsEpoch(string? epoch) => B64Url.TryDecode(epoch, out var e) && e.Length == EpochBytes;

    /// <summary>Text hashed for the pairing rendezvous: 'rigcontrol-pair|' + code.</summary>
    public static string PairHashText(string normalizedCode) => "rigcontrol-pair|" + normalizedCode;

    public const string PairSalt = "rigcontrol-pair-v1";
    public const string PairInfo = "box";
    public const string SessionInfo = "rigcontrol-link-v1 session";

    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>Defaults shared by the PC and the phone app.</summary>
public static class LinkDefaults
{
    /// <summary>
    /// The relay this build talks to (RigControl's own Cloudflare Worker, see relay/README.md). A build that sets this to
    /// a ".invalid" address has no relay: the PC then says so and never connects.
    /// </summary>
    public const string RelayUrl = "wss://rigcontrol-relay.rigcontrol.workers.dev";

    /// <summary>True when <paramref name="relayUrl"/> is the "not set" placeholder (or empty).</summary>
    public static bool IsPlaceholder(string? relayUrl)
        => string.IsNullOrWhiteSpace(relayUrl)
           || (Uri.TryCreate(relayUrl.Trim(), UriKind.Absolute, out var u)
               && (u.Host.Equals("invalid", StringComparison.OrdinalIgnoreCase)
                   || u.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)));

    /// <summary>How long a pairing code works.</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Longest device name kept or shown.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Cuts a device name to something safe to show: no control characters, no invisible format characters (bidi
    /// overrides and isolates, zero-width characters: Unicode category Cf), no line or paragraph separators, and at
    /// most <see cref="MaxNameLength"/> (never ending in half a surrogate pair).
    /// </summary>
    public static string CleanName(string? name, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
        {
            if (char.IsControl(c)) continue;
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            sb.Append(c);
            if (sb.Length >= MaxNameLength) break;
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        var s = sb.ToString().Trim();
        return s == "" ? fallback : s;
    }
}

/// <summary>The two relay endpoints for a relay base address, with the address rules.</summary>
public static class LinkEndpoints
{
    /// <summary>
    /// Checks a relay base address: wss:// anywhere, or ws:// only to this computer (a local relay for testing).
    /// A bare host ("relay.example.com", as a QR code carries it) means wss. No user info, path, query or fragment.
    /// Returns null when it is not acceptable.
    /// </summary>
    public static Uri? ParseRelay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (!t.Contains("://", StringComparison.Ordinal)) t = "wss://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return null;
        if (u.UserInfo != "" || u.Query != "" || u.Fragment != "") return null;
        if (u.AbsolutePath != "/") return null;
        if (u.Scheme == "wss") return u;
        if (u.Scheme == "ws" && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))) return u;
        return null;
    }

    /// <summary><c>wss://&lt;relay&gt;/v1/connect?room=&lt;room&gt;</c> (the relay routes by the URL's room; it must equal hello's).</summary>
    public static Uri Connect(Uri relay, string room) => new(relay, "/v1/connect?room=" + Uri.EscapeDataString(room));

    /// <summary><c>wss://&lt;relay&gt;/v1/pair?h=&lt;h&gt;</c> (it must equal the offer's or claim's h).</summary>
    public static Uri Pair(Uri relay, string h) => new(relay, "/v1/pair?h=" + Uri.EscapeDataString(h));

    /// <summary>What goes in the QR code's relay= field: the host, with the port when it is not the default.</summary>
    public static string HostForQr(Uri relay) => relay.IsDefaultPort ? relay.Host : relay.Authority;
}

/// <summary>
/// Error codes and close reasons come from the relay, so they are text a relay chooses. <see cref="Clean"/> keeps only
/// <c>[a-z0-9]{1,24}</c> (else "error") and <see cref="Shown"/> lets only the codes listed here reach logs and the UI
/// (anything else is "other"); the UI words each known code itself.
/// </summary>
public static class RelayCodes
{
    private static readonly Regex Safe = new(@"\A[a-z0-9]{1,24}\z", RegexOptions.CultureInvariant);

    /// <summary>The relay's codes (relay/src/protocol.ts) and this library's own.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        // relay
        "badmsg", "badkey", "badsig", "badroom", "denied", "notallowed", "removed", "replaced", "timeout", "toolarge",
        "ratelimit", "nopair", "expired", "paired",
        // client side
        "error", "handshake", "unreachable", "dropped", "closed", "forbidden", "badrelay", "interrupted", "wrongcode",
        "badbox", "badcode", "badqr", "offline", "pcoffline", "refused", "stale", "busy", "toobig",
    };

    /// <summary>The code if it is <c>[a-z0-9]{1,24}</c>, else "error".</summary>
    public static string Clean(string? code) => code is not null && Safe.IsMatch(code) ? code : "error";

    /// <summary>The code if it is one of <see cref="Known"/>, else "other": the only form logged or shown.</summary>
    public static string Shown(string? code) => code is not null && Known.Contains(code) ? code : "other";
}

/// <summary>A protocol failure with a short machine-readable code (the relay's codes are passed on as they are).</summary>
public sealed class LinkException : Exception
{
    public string Code { get; }

    public LinkException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>base64url without padding (RFC 4648 section 5).</summary>
public static class B64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes; throws <see cref="FormatException"/> on anything that is not base64url without padding.</summary>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                throw new FormatException("Not base64url.");
        }
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Not base64url.");
        }
        var data = Convert.FromBase64String(s);
        // Strict, as the relay is: the unused low bits of the last character must be zero (one text per value).
        if (Encode(data) != text) throw new FormatException("Not canonical base64url.");
        return data;
    }

    public static bool TryDecode(string? text, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (text is null) return false;
        try
        {
            data = Decode(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
