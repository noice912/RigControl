using System.Globalization;
using System.Text.Json.Nodes;

namespace RigControl.Mobile.Core.Music;

/// <summary>What the phone's Music app plays right now, as read on the phone (cleaned by <see cref="PhoneMusicPayload"/>).</summary>
public sealed record PhoneMusicTrack(string? Title, string? Artist, string? Album, bool Playing, double Position, double Duration);

/// <summary>
/// Builds presence.phoneMusic's arguments (docs/protocols/link.md): plain text trimmed and capped at 256 characters,
/// control and format characters dropped, times clamped to 0-86400 s. No title = nothing playing = <c>{}</c>.
/// </summary>
public static class PhoneMusicPayload
{
    public const int MaxText = 256;
    public const double MaxSeconds = 86400;

    public static JsonObject Build(PhoneMusicTrack? track)
    {
        var title = Clean(track?.Title);
        if (track is null || title.Length == 0)
            return new JsonObject();
        var o = new JsonObject { ["title"] = title };
        var artist = Clean(track.Artist);
        if (artist.Length > 0)
            o["artist"] = artist;
        var album = Clean(track.Album);
        if (album.Length > 0)
            o["album"] = album;
        o["playing"] = track.Playing;
        o["position"] = Seconds(track.Position);
        o["duration"] = Seconds(track.Duration);
        return o;
    }

    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var kept = new System.Text.StringBuilder(Math.Min(text.Length, MaxText + 8));
        foreach (var c in text)
        {
            if (char.IsControl(c))
                kept.Append(' '); // a line break between words stays a space
            else if (char.GetUnicodeCategory(c) != UnicodeCategory.Format)
                kept.Append(c);
        }
        var s = kept.ToString().Trim();
        if (s.Length <= MaxText)
            return s;
        var cut = s[..MaxText];
        if (char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1]; // don't split an emoji in half
        return cut.TrimEnd();
    }

    internal static double Seconds(double s) =>
        double.IsNaN(s) || double.IsInfinity(s) ? 0 : Math.Round(Math.Clamp(s, 0, MaxSeconds), 1);
}
