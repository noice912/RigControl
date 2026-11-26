using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// Checks a relay address from a QR code or typed in: a host name (letters, digits, dashes, dots), optionally with a
/// port. No scheme, path, user name or query, so a link can't smuggle anything else into the connection URL; the app
/// always connects with wss:// to it.
/// </summary>
public static class RelayHost
{
    public static bool TryNormalize(string? text, [NotNullWhen(true)] out string? host)
    {
        host = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim().ToLowerInvariant();
        if (t.Length > 260)
            return false;

        var name = t;
        var colon = t.LastIndexOf(':');
        if (colon >= 0)
        {
            name = t[..colon];
            var port = t[(colon + 1)..];
            if (port.Length is 0 or > 5 || !port.All(char.IsAsciiDigit)
                || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                return false;
        }

        if (name.Length is 0 or > 253)
            return false;
        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
                return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return false;
        }

        host = t;
        return true;
    }

    /// <summary>The WebSocket address for one of the relay's endpoints, e.g. "connect" or "pair".</summary>
    public static Uri Endpoint(string host, string endpoint) => new($"wss://{host}/v1/{endpoint}");
}
