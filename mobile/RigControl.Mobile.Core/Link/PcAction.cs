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

    public static readonly FrozenSet<string> Allowed = new[]
    {
        LightingSetEffect, LightingOn, LightingOff, CaptureStart, CaptureStop, CaptureSaveReplay, PresenceSetCustom,
    }.ToFrozenSet(StringComparer.Ordinal);

    public string Name { get; }
    public IReadOnlyDictionary<string, string> Args { get; }

    private PcAction(string name, IReadOnlyDictionary<string, string>? args)
    {
        if (!Allowed.Contains(name))
            throw new ArgumentException($"'{name}' is not an action a phone may ask for.", nameof(name));
        Name = name;
        Args = args ?? new Dictionary<string, string>();
    }

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
    /// The arguments as the protocol carries them (docs/protocols/link.md): strings, except presence.setCustom's
    /// "on", which is a JSON boolean.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject ToJsonArgs()
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in Args.OrderBy(a => a.Key, StringComparer.Ordinal))
            o[k] = Name == PresenceSetCustom && k == "on" ? v == "true" : v;
        return o;
    }
}
