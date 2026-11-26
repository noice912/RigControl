using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RigControl.Link;

/// <summary>The plaintext message types (inside a sealed body).</summary>
public static class LinkMessageTypes
{
    // phone -> PC
    public const string Get = "get";
    public const string Do = "do";

    // PC -> phone
    public const string Snapshot = "snapshot";
    public const string Result = "result";
    public const string Event = "event";

    public const string WhatSnapshot = "snapshot";
}

/// <summary>Builds the plaintext messages ("n" is added when sealing).</summary>
public static class LinkMessages
{
    public static JsonObject GetSnapshot() => new() { ["type"] = LinkMessageTypes.Get, ["what"] = LinkMessageTypes.WhatSnapshot };

    public static JsonObject Do(string action, JsonObject? args)
        => new() { ["type"] = LinkMessageTypes.Do, ["action"] = action, ["args"] = args ?? new JsonObject() };

    /// <summary>A snapshot answer: the PC's state in <paramref name="state"/> (copied in), "re" = the request's n.</summary>
    public static JsonObject Snapshot(long re, JsonObject state)
    {
        var o = new JsonObject { ["type"] = LinkMessageTypes.Snapshot, ["re"] = re };
        foreach (var (k, v) in state)
        {
            if (k is "type" or "n" or "re") continue;
            o[k] = v?.DeepClone();
        }
        return o;
    }

    /// <summary>A "result". <paramref name="code"/> is a short machine-readable reason (for example "unavailable").</summary>
    public static JsonObject Result(long re, bool ok, string? error = null, string? code = null)
    {
        var o = new JsonObject { ["type"] = LinkMessageTypes.Result, ["re"] = re, ["ok"] = ok };
        if (!ok)
        {
            o["error"] = error ?? "Refused.";
            if (code is not null) o["code"] = code;
        }
        return o;
    }

    public static JsonObject Event(string kind, JsonObject? data = null)
    {
        var o = new JsonObject { ["type"] = LinkMessageTypes.Event, ["kind"] = kind };
        if (data is not null)
        {
            foreach (var (k, v) in data)
            {
                if (k is "type" or "n" or "kind") continue;
                o[k] = v?.DeepClone();
            }
        }
        return o;
    }

    internal static string? Str(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    internal static long? Long(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue(out long l) ? l : null;
}

/// <summary>
/// The PC's own refusals, sent as a result's <c>code</c> (the phone words them itself) with a fixed sentence as
/// <c>error</c> for phones that only show the text.
/// </summary>
public static class LinkRefusals
{
    /// <summary>The message's counter is too far from the PC's clock (held back, or a clock that is off).</summary>
    public const string Stale = "stale";

    /// <summary>The phone sent more requests than the PC answers in 10 seconds.</summary>
    public const string Busy = "busy";

    public const string StaleText = "This phone's clock doesn't match the PC's. Set both to set the time automatically.";
    public const string BusyText = "The PC is busy; try again in a moment.";
}

/// <summary>The outcome of a "do" as the phone sees it.</summary>
public sealed record LinkActionResult(bool Ok, string? Error, string? Code = null)
{
    public static readonly LinkActionResult Success = new(true, null);
    public static LinkActionResult Fail(string error, string? code = null) => new(false, error, code);
}

/// <summary>A "do" that passed <see cref="LinkActions.Check"/>: the action and its cleaned arguments.</summary>
public sealed record LinkAction(string Name, JsonObject Args);

/// <summary>
/// The only things a phone may ask a PC to do (decision D1). Everything else is refused on the PC, whatever the
/// phone sends. Arguments are checked strictly: unknown fields are refused too.
///
/// Arguments (choices, the protocol document lists only the names):
/// <list type="bullet">
/// <item><c>lighting.setEffect</c>: <c>{"effect":"&lt;effect id&gt;"}</c> (an id like "wave"), or <c>{"scene":"&lt;saved scene name&gt;"}</c>.</item>
/// <item><c>lighting.off</c>, <c>lighting.on</c>, <c>capture.start</c>, <c>capture.stop</c>, <c>capture.saveReplay</c>: no arguments.</item>
/// <item><c>presence.setCustom</c>: <c>{"id":"&lt;custom status id&gt;","on":true|false}</c>: switches one of the custom
///   statuses already made on the PC on or off. A phone cannot write new status text or links.</item>
/// </list>
/// </summary>
public static class LinkActions
{
    public const string LightingSetEffect = "lighting.setEffect";
    public const string LightingOff = "lighting.off";
    public const string LightingOn = "lighting.on";
    public const string CaptureStart = "capture.start";
    public const string CaptureStop = "capture.stop";
    public const string CaptureSaveReplay = "capture.saveReplay";
    public const string PresenceSetCustom = "presence.setCustom";

    public static readonly IReadOnlyList<string> Allowed = new[]
    {
        LightingSetEffect, LightingOff, LightingOn, CaptureStart, CaptureStop, CaptureSaveReplay, PresenceSetCustom,
    };

    // \A and \z, not ^ and $: in .NET "$" also matches before a final "\n", so "abc\n" would pass.
    private static readonly Regex EffectId = new(@"\A[a-z][A-Za-z0-9]{0,31}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CustomId = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);

    /// <summary>Checks one "do". Returns the cleaned action, or null with the reason in <paramref name="error"/>.</summary>
    public static LinkAction? Check(string? action, JsonNode? args, out string? error)
    {
        error = null;
        if (action is null || !Allowed.Contains(action, StringComparer.Ordinal))
        {
            error = "That isn't something a phone may do on this PC.";
            return null;
        }
        JsonObject a;
        if (args is null) a = new JsonObject();
        else if (args is JsonObject o) a = o;
        else
        {
            error = "The action's arguments must be an object.";
            return null;
        }

        switch (action)
        {
            case LightingSetEffect:
            {
                if (!OnlyFields(a, out error, "effect", "scene")) return null;
                var effect = a["effect"];
                var scene = a["scene"];
                if ((effect is null) == (scene is null))
                {
                    error = "Give either an effect or a scene.";
                    return null;
                }
                if (effect is not null)
                {
                    if (!IsString(effect, out var e) || !EffectId.IsMatch(e))
                    {
                        error = "That isn't an effect name.";
                        return null;
                    }
                    return new LinkAction(action, new JsonObject { ["effect"] = e });
                }
                if (!IsString(scene, out var s) || s.Trim().Length is 0 or > 64 || s.Any(char.IsControl))
                {
                    error = "That isn't a scene name.";
                    return null;
                }
                return new LinkAction(action, new JsonObject { ["scene"] = s.Trim() });
            }
            case PresenceSetCustom:
            {
                if (!OnlyFields(a, out error, "id", "on")) return null;
                if (!IsString(a["id"], out var id) || !CustomId.IsMatch(id))
                {
                    error = "That isn't a custom status.";
                    return null;
                }
                if (a["on"] is not JsonValue on || on.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "Say whether to switch it on or off.";
                    return null;
                }
                return new LinkAction(action, new JsonObject { ["id"] = id, ["on"] = on.GetValue<bool>() });
            }
            default:
                if (!OnlyFields(a, out error)) return null;
                return new LinkAction(action, new JsonObject());
        }
    }

    private static bool OnlyFields(JsonObject a, out string? error, params string[] names)
    {
        foreach (var (k, _) in a)
        {
            if (!names.Contains(k, StringComparer.Ordinal))
            {
                error = $"\"{(k.Length > 32 ? k[..32] : k)}\" isn't a setting of this action.";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool IsString(JsonNode? n, out string s)
    {
        s = "";
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            s = v.GetValue<string>();
            return true;
        }
        return false;
    }
}
