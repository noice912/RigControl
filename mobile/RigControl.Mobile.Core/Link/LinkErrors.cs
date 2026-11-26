using System.Net.Sockets;
using System.Net.WebSockets;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// A PC can't be asked right now (offline, relay unreachable, no answer). The message is written for people and can
/// be shown as it is.
/// </summary>
public sealed class LinkUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns RigControl.Link's short error codes into sentences for the screens.</summary>
public static class LinkErrors
{
    /// <summary>Why pairing failed. <paramref name="code"/> is a LinkException code (the relay's codes pass through).</summary>
    public static string ForPairing(string code, string relayHost) => code switch
    {
        "nopair" => "That code didn't work: it may be mistyped, older than 10 minutes, or already used. Check it, or show a new code on the PC.",
        "wrongcode" or "badbox" => "The answer didn't match the code, so nothing was linked. Show a new code on the PC and try again.",
        "timeout" => $"The relay at {relayHost} didn't answer in time. Check this phone's internet connection and try again.",
        "interrupted" => "The connection dropped while linking. Show a new code on the PC and try again.",
        "ratelimit" => "Too many tries in a short time. Wait a minute, then try again.",
        "badrelay" => "That relay address can't be used.",
        "badcode" => "That isn't a pairing code (20 letters and digits).",
        "unreachable" or "forbidden" or "handshake" => Unreachable(relayHost),
        _ => $"Linking didn't work ({code}). Show a new code on the PC and try again.",
    };

    public static string Unreachable(string relayHost) =>
        $"Couldn't reach the relay at {relayHost}. Check this phone's internet connection and the relay address, then try again.";

    /// <summary>True for the exceptions a failed network connection gives (DNS, refused, TLS, WebSocket upgrade).</summary>
    public static bool IsNetwork(Exception ex) =>
        ex is WebSocketException or SocketException or HttpRequestException or IOException
        || (ex.InnerException is { } inner && IsNetwork(inner));

    /// <summary>What to show for a relay connection that is not up (RelayLink.LastError), or null when there's nothing to say.</summary>
    public static string? ForConnection(string? lastError) => lastError switch
    {
        null => null,
        "denied" => "This PC doesn't list this phone any more (it was removed on the PC). Forget it here and link it again.",
        "removed" => "The PC removed this phone. Forget it here and link it again.",
        "ratelimit" => "The relay asked this phone to wait a little. Trying again soon.",
        _ => "Can't reach the relay right now. Trying again.",
    };

    public const string Stale = "This phone's clock doesn't match the PC's. Set both to set the time automatically.";
    public const string Busy = "The PC is busy; try again in a moment.";

    /// <summary>
    /// The phone's own words for a refusal code the PC sends ("stale", "busy"), or null for any other code (then the
    /// PC's fixed sentence is shown).
    /// </summary>
    public static string? ForRefusal(string? code) => code switch
    {
        "stale" => Stale,
        "busy" => Busy,
        _ => null,
    };

    /// <summary>Why a request to the PC failed (PhoneSide's codes).</summary>
    public static string ForRequest(string code, string pcName, string? detail = null) => code switch
    {
        "pcoffline" => $"{pcName} is offline (RigControl isn't running there, or the PC is asleep).",
        "timeout" => $"{pcName} didn't answer in time.",
        "offline" => "Not connected to the relay right now. Trying again.",
        "stale" => Stale,
        "busy" => Busy,
        "refused" => detail ?? $"{pcName} refused.",
        _ => detail ?? $"Couldn't reach {pcName} ({code}).",
    };
}
