using RigControl.Mobile.Core.Link;

namespace RigControl.Mobile.Core.DeepLinks;

/// <summary>A rigcontrol:// link the phone was opened with.</summary>
public abstract record DeepLink
{
    public const string Scheme = "rigcontrol";

    /// <summary>
    /// rigcontrol://pair?relay=&lt;host&gt;&amp;code=&lt;code&gt; from a PC's QR code. Opening it only fills in the
    /// Link a PC screen; the person still taps Link.
    /// </summary>
    public sealed record Pair(string Relay, PairingCode Code) : DeepLink;

    /// <summary>
    /// rigcontrol://callback?... : the end of a sign-in in the browser (Spotify, PKCE). Kept raw; the sign-in code
    /// that started it checks state and reads the code (Step 3).
    /// </summary>
    public sealed record Callback(IReadOnlyDictionary<string, string> Query) : DeepLink;

    /// <summary>Reads a link. Null when it isn't one of ours or is malformed.</summary>
    public static DeepLink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
            return null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            return null;
        return Parse(uri);
    }

    public static DeepLink? Parse(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length != 0)
            return null;

        var query = ParseQuery(uri.Query);
        if (query is null)
            return null;

        switch (uri.Host.ToLowerInvariant())
        {
            case "pair":
                if (!query.TryGetValue("relay", out var relay) || !RelayHost.TryNormalize(relay, out var host))
                    return null;
                if (!query.TryGetValue("code", out var codeText) || !PairingCode.TryParse(codeText, out var code))
                    return null;
                return new Pair(host, code);
            case "callback":
                return new Callback(query);
            default:
                return null;
        }
    }

    /// <summary>Query string to a dictionary; null when a key repeats (ambiguous, so refused).</summary>
    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = query.StartsWith('?') ? query[1..] : query;
        if (q.Length == 0)
            return result;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            if (!result.TryAdd(key, value))
                return null;
        }
        return result;
    }
}
