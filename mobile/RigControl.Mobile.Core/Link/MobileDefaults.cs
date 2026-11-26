using RigControl.Link;

namespace RigControl.Mobile.Core.Link;

/// <summary>Values built into this build of the phone app.</summary>
public static class MobileDefaults
{
    /// <summary>
    /// The relay used for a code that is typed in (a scanned QR code brings its own relay host): the host of
    /// RigControl.Link's <see cref="LinkDefaults.RelayUrl"/>, the same relay the PC uses. A build whose RelayUrl is a
    /// ".invalid" address has no relay: typed codes then fail with <see cref="RelayNotSetMessage"/>.
    /// </summary>
    public static readonly string RelayHost = HostOf(LinkDefaults.RelayUrl);

    public const string RelayNotSetMessage =
        "The relay address is not set in this build. Scan the QR code the PC shows instead: it carries the relay address.";

    /// <summary>True for an empty host or one under the reserved ".invalid" name (the "not set" placeholder).</summary>
    public static bool IsPlaceholder(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;
        var h = host.Trim().ToLowerInvariant();
        var colon = h.LastIndexOf(':');
        if (colon >= 0)
            h = h[..colon];
        return h == "invalid" || h.EndsWith(".invalid", StringComparison.Ordinal);
    }

    /// <summary>
    /// What to say on the Link-a-PC screen about <paramref name="relayText"/> when it is a usable host other than the
    /// built-in one (so a QR code or link that points somewhere else is always visible), else null. Port 443 counts as
    /// the same host.
    /// </summary>
    public static string? OtherRelayNote(string? relayText)
    {
        if (!global::RigControl.Mobile.Core.Link.RelayHost.TryNormalize(relayText, out var host) || IsPlaceholder(host))
            return null;
        var bare = host.EndsWith(":443", StringComparison.Ordinal) ? host[..^4] : host;
        return string.Equals(bare, RelayHost, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"This links through the relay at {bare}, not the one built into the app. Only go on if you (or your PC) use that relay.";
    }

    /// <summary>True when this build has a real relay address for typed codes.</summary>
    public static bool HasRelay => !IsPlaceholder(RelayHost);

    /// <summary>The host (with a non-default port) of a wss:// relay URL; "relay.invalid" when it isn't one.</summary>
    internal static string HostOf(string relayUrl) =>
        !LinkDefaults.IsPlaceholder(relayUrl) && LinkEndpoints.ParseRelay(relayUrl) is { Scheme: "wss" } u
            ? LinkEndpoints.HostForQr(u)
            : "relay.invalid";
}
