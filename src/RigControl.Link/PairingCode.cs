using System.Security.Cryptography;
using System.Text;

namespace RigControl.Link;

/// <summary>
/// The pairing code: 20 characters of Crockford base32 (100 bits), shown as 5 groups of 4, and the QR text
/// <c>rigcontrol://pair?relay=&lt;relay host&gt;&amp;code=&lt;code&gt;</c>.
/// </summary>
public static class PairingCode
{
    /// <summary>Crockford's base32 alphabet (no I, L, O, U).</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Length = 20;
    public const int GroupSize = 4;

    /// <summary>A new random code (20 independent uniform symbols = 100 bits), canonical form, no dashes.</summary>
    public static string New()
    {
        Span<char> c = stackalloc char[Length];
        for (var i = 0; i < Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c);
    }

    /// <summary>
    /// The code as both sides hash it: upper case, dashes and spaces removed, Crockford's look-alikes read as the
    /// digit they stand for (O as 0, I and L as 1). Null when the result is not 20 Crockford symbols.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;
        var sb = new StringBuilder(Length);
        foreach (var raw in input)
        {
            if (raw is '-' or ' ' or '\t') continue;
            var ch = char.ToUpperInvariant(raw);
            ch = ch switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => ch,
            };
            if (Alphabet.IndexOf(ch) < 0) return null;
            sb.Append(ch);
            if (sb.Length > Length) return null;
        }
        return sb.Length == Length ? sb.ToString() : null;
    }

    /// <summary>The 5 groups of 4 for display.</summary>
    public static IReadOnlyList<string> Groups(string code)
    {
        var n = Normalize(code) ?? throw new ArgumentException("Not a pairing code.", nameof(code));
        var list = new string[Length / GroupSize];
        for (var i = 0; i < list.Length; i++) list[i] = n.Substring(i * GroupSize, GroupSize);
        return list;
    }

    /// <summary>"ABCD-EFGH-JKMN-PQRS-TVWX".</summary>
    public static string Display(string code) => string.Join("-", Groups(code));

    /// <summary>The QR code's text. The code is given without dashes.</summary>
    public static string QrText(string relayHost, string code)
        => "rigcontrol://pair?relay=" + relayHost + "&code=" + (Normalize(code) ?? throw new ArgumentException("Not a pairing code.", nameof(code)));

    /// <summary>Reads a scanned QR text. The relay host must be a plain host name (optionally with a port).</summary>
    public static bool TryParseQr(string? text, out string relayHost, out string code)
    {
        relayHost = "";
        code = "";
        if (text is null || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "rigcontrol" || !u.Host.Equals("pair", StringComparison.OrdinalIgnoreCase)) return false;
        string? relay = null, c = null;
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            if (key == "relay") relay = value;
            else if (key == "code") c = value;
        }
        var n = Normalize(c);
        if (n is null || relay is null || !IsHost(relay)) return false;
        relayHost = relay;
        code = n;
        return true;
    }

    private static bool IsHost(string s)
    {
        if (s.Length is 0 or > 253 + 6) return false;
        var colon = s.LastIndexOf(':');
        var host = colon >= 0 ? s[..colon] : s;
        if (colon >= 0 && (!int.TryParse(s[(colon + 1)..], out var port) || port is < 1 or > 65535)) return false;
        if (host.Length == 0) return false;
        foreach (var ch in host)
            if (!(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-')) return false;
        return true;
    }

    /// <summary>"h" for the relay: base64url(SHA-256('rigcontrol-pair|' + code)).</summary>
    public static string RendezvousHash(string code)
    {
        var n = Normalize(code) ?? throw new ArgumentException("Not a pairing code.", nameof(code));
        return B64Url.Encode(SHA256.HashData(LinkProtocol.Utf8(LinkProtocol.PairHashText(n))));
    }

    /// <summary>K = HKDF-SHA256(ikm = UTF-8 code, salt = "rigcontrol-pair-v1", info = "box", 32 bytes).</summary>
    public static byte[] BoxKey(string code)
    {
        var n = Normalize(code) ?? throw new ArgumentException("Not a pairing code.", nameof(code));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, LinkProtocol.Utf8(n), 32,
            LinkProtocol.Utf8(LinkProtocol.PairSalt), LinkProtocol.Utf8(LinkProtocol.PairInfo));
    }
}
