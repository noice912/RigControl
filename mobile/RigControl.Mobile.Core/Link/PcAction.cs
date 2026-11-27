using System.Collections.Frozen;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// A "do" request for a PC. Only the actions in docs/protocols/link.md (decision D1) can be made; the PC checks every
/// request again, this only keeps the phone from ever sending anything else.
/// </summary>
public sealed record PcAction
{
    public const string LightingSetEffect = "lighting.setEffect";
    public const string LightingOn = "lighting.on";
    public const string LightingOff = "lighting.off";
    public const string CaptureStart = "capture.start";
    public const string CaptureStop = "capture.stop";
    public const string CaptureSaveReplay = "capture.saveReplay";
    public const string PresenceSetCustom = "presence.setCustom";
    public const string PresencePhoneMusic = "presence.phoneMusic";
    public const string PresenceSpotifyConnect = "presence.spotifyConnect";
    public const string PresenceSpotifyDisconnect = "presence.spotifyDisconnect";

    public static readonly FrozenSet<string> Allowed = new[]
    {
        LightingSetEffect, LightingOn, LightingOff, CaptureStart, CaptureStop, CaptureSaveReplay, PresenceSetCustom,
        PresencePhoneMusic, PresenceSpotifyConnect, PresenceSpotifyDisconnect,
    }.ToFrozenSet(StringComparer.Ordinal);

    public string Name { get; }

    /// <summary>
    /// The text arguments. Empty for presence.phoneMusic (typed values, see <see cref="ToJsonArgs"/>) and
    /// presence.spotifyConnect (the sign-in stays out of anything that might be printed).
    /// </summary>
    public IReadOnlyDictionary<string, string> Args { get; }

    /// <summary>Arguments that are not all text (or must not sit in <see cref="Args"/>), sent as they are.</summary>
    private readonly System.Text.Json.Nodes.JsonObject? _json;

    private PcAction(string name, IReadOnlyDictionary<string, string>? args, System.Text.Json.Nodes.JsonObject? json = null)
    {
        if (!Allowed.Contains(name))
            throw new ArgumentException($"'{name}' is not an action a phone may ask for.", nameof(name));
        Name = name;
        Args = args ?? new Dictionary<string, string>();
        _json = json;
    }

    /// <summary>Only the name: arguments may carry a sign-in.</summary>
    public override string ToString() => $"PcAction {Name}";

    public static PcAction SetEffect(string effectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        return new(LightingSetEffect, new Dictionary<string, string> { ["effect"] = effectId });
    }

    public static PcAction LightsOn() => new(LightingOn, null);
    public static PcAction LightsOff() => new(LightingOff, null);
    public static PcAction StartRecording() => new(CaptureStart, null);
    public static PcAction StopRecording() => new(CaptureStop, null);
    public static PcAction SaveReplay() => new(CaptureSaveReplay, null);

    /// <summary>Switches one of the custom statuses already made on the PC on or off.</summary>
    public static PcAction SetCustomStatus(string statusId, bool on)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusId);
        return new(PresenceSetCustom, new Dictionary<string, string> { ["id"] = statusId, ["on"] = on ? "true" : "false" });
    }

    /// <summary>
    /// What the phone's Music app plays, for the PC's Discord music card. Build the arguments with
    /// <see cref="Music.PhoneMusicPayload.Build"/> (trimmed, capped, times clamped; empty = nothing playing).
    /// </summary>
    public static PcAction PhoneMusic(System.Text.Json.Nodes.JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new(PresencePhoneMusic, null, (System.Text.Json.Nodes.JsonObject)payload.DeepClone());
    }

    /// <summary>Hands a Spotify sign-in (refresh token, read-only scopes) to the PC, which keeps it encrypted.</summary>
    public static PcAction SpotifyConnect(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return new(PresenceSpotifyConnect, null, new System.Text.Json.Nodes.JsonObject { ["refreshToken"] = refreshToken });
    }

    /// <summary>The PC forgets the Spotify sign-in.</summary>
    public static PcAction SpotifyDisconnect() => new(PresenceSpotifyDisconnect, null);

    /// <summary>
    /// The arguments as the protocol carries them (docs/protocols/link.md): strings, except presence.setCustom's
    /// "on", which is a JSON boolean.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject ToJsonArgs()
    {
        if (_json is not null)
            return (System.Text.Json.Nodes.JsonObject)_json.DeepClone();
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in Args.OrderBy(a => a.Key, StringComparer.Ordinal))
            o[k] = Name == PresenceSetCustom && k == "on" ? v == "true" : v;
        return o;
    }
}
