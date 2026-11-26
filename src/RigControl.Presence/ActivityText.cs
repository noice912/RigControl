using System.Text.Json.Nodes;

namespace RigControl.Presence;

/// <summary>
/// Discord refuses a whole status card when one text is too long (a long song or album name, say), so every text is
/// shortened to Discord's limit just before the card is sent, ending in "…" so it is clear something was cut.
/// </summary>
public static class ActivityText
{
    /// <summary>Discord's limits, in characters: card texts 128, button labels 32.</summary>
    public const int TextLimit = 128, ButtonLimit = 32;

    private static readonly string[] TopTexts = { "details", "state", "name" };
    private static readonly string[] AssetTexts = { "large_text", "small_text" };

    /// <summary>The same card (changed in place and returned) with every text within Discord's limits.</summary>
    public static JsonObject FitDiscordLimits(JsonObject activity)
    {
        foreach (var key in TopTexts) Fit(activity, key, TextLimit);
        if (activity["assets"] is JsonObject assets)
            foreach (var key in AssetTexts) Fit(assets, key, TextLimit);
        if (activity["buttons"] is JsonArray buttons)
            foreach (var b in buttons.OfType<JsonObject>()) Fit(b, "label", ButtonLimit);
        return activity;
    }

    /// <summary>Text cut to at most <paramref name="max"/> characters, never splitting an emoji or other surrogate pair.</summary>
    public static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    private static void Fit(JsonObject o, string key, int max)
    {
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > max) o[key] = Shorten(s, max);
    }
}
