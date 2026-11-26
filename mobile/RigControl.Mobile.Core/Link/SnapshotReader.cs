using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// Turns the PC's "snapshot" message into <see cref="PcSnapshot"/>. The shape is the one RigControl's LinkModule
/// sends (src/RigControl.App/Link/LinkModule.cs, SnapshotAsync):
/// <c>{"pc","time","sensors":{"cpu":{"tempC","loadPercent"},"gpu":{..},"fans":[{"name","rpm"}],"temps":[{"name","c"}]},
/// "recording":{"available","recording","since","replayRunning"},"lighting":{"on","effect","effects":[{"id","name"}]},
/// "presence":{"status","playing","music":{"title","artist","player","playing"},"custom":[{"id","label","on"}]}}</c>.
/// Everything may be missing or null; values of the wrong kind are treated as missing. Text from the PC is cut short
/// and stripped of control characters before it is shown.
/// </summary>
public static class SnapshotReader
{
    private const int MaxItems = 32;

    public static PcSnapshot Read(JsonObject o, DateTimeOffset now)
    {
        var sensors = o["sensors"] as JsonObject;
        var cpu = sensors?["cpu"] as JsonObject;
        var gpu = sensors?["gpu"] as JsonObject;
        var rec = o["recording"] as JsonObject;
        var light = o["lighting"] as JsonObject;
        var presence = o["presence"] as JsonObject;

        return new PcSnapshot
        {
            TakenAt = Date(o["time"]) ?? now,
            CpuTempC = Num(cpu?["tempC"]),
            GpuTempC = Num(gpu?["tempC"]),
            LiquidTempC = Liquid(sensors?["temps"] as JsonArray),
            CpuLoad = Clamp(Num(cpu?["loadPercent"])),
            GpuLoad = Clamp(Num(gpu?["loadPercent"])),
            Fans = Items(sensors?["fans"])
                .Select(f => (Name: Text(f["name"], 48), Rpm: Num(f["rpm"])))
                .Where(f => f.Name is not null && f.Rpm is not null)
                .Select(f => new FanReading(f.Name!, (int)Math.Clamp(Math.Round(f.Rpm!.Value), 0, 100_000)))
                .ToArray(),
            Recording = new RecordingState
            {
                IsRecording = Bool(rec?["recording"]) ?? false,
                RecordingSince = Date(rec?["since"]),
                ReplayBufferOn = Bool(rec?["replayRunning"]) ?? false,
            },
            Lighting = new LightingState
            {
                On = Bool(light?["on"]) ?? false,
                Effect = Text(light?["effect"], 32),
                Effects = Items(light?["effects"])
                    .Select(e => (Id: Text(e["id"], 32), Name: Text(e["name"], 48)))
                    .Where(e => e.Id is not null)
                    .Select(e => new LightingEffect(e.Id!, e.Name ?? e.Id!))
                    .ToArray(),
            },
            NowPlaying = NowPlaying(presence),
            CustomStatuses = Items(presence?["custom"])
                .Select(c => (Id: Text(c["id"], 64), Label: Text(c["label"], 64), On: Bool(c["on"]) ?? false))
                .Where(c => c.Id is not null)
                .Select(c => new CustomStatus(c.Id!, c.Label ?? c.Id!, c.On))
                .ToArray(),
        };
    }

    private static string? NowPlaying(JsonObject? presence)
    {
        if (presence is null)
            return null;
        if (presence["music"] is JsonObject music && Bool(music["playing"]) != false && Text(music["title"], 80) is { } title)
        {
            var artist = Text(music["artist"], 60);
            return artist is null ? title : $"{title} – {artist}";
        }
        return presence["playing"] switch
        {
            JsonObject game => Text(game["name"], 80) ?? Text(game["title"], 80),
            var p => Text(p, 80),
        };
    }

    private static double? Liquid(JsonArray? temps)
    {
        foreach (var t in Items(temps))
        {
            var name = Text(t["name"], 48);
            if (name is null)
                continue;
            if (name.Contains("liquid", StringComparison.OrdinalIgnoreCase) || name.Contains("coolant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("water", StringComparison.OrdinalIgnoreCase))
                return Num(t["c"]);
        }
        return null;
    }

    private static IEnumerable<JsonObject> Items(JsonNode? n) =>
        n is JsonArray a ? a.OfType<JsonObject>().Take(MaxItems) : [];

    private static double? Clamp(double? v) => v is null ? null : Math.Clamp(v.Value, 0, 100);

    internal static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
            return null;
        double d;
        if (v.TryGetValue(out double dd)) d = dd;
        else if (v.TryGetValue(out long l)) d = l;
        else if (v.TryGetValue(out int i)) d = i;
        else if (v.TryGetValue(out float f)) d = f;
        else if (v.TryGetValue(out decimal m)) d = (double)m;
        else return null;
        return double.IsFinite(d) ? d : null;
    }

    private static bool? Bool(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    private static DateTimeOffset? Date(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToUniversalTime()
            : null;

    /// <summary>
    /// A string value at most <paramref name="max"/> characters, without control characters and without the invisible
    /// ones (format characters such as bidi overrides and zero-width marks, line/paragraph separators), as
    /// RigControl.Link's CleanName does for names; null when empty. A surrogate pair is never split.
    /// </summary>
    internal static string? Text(JsonNode? n, int max)
    {
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
            return null;
        var sb = new StringBuilder();
        foreach (var c in v.GetValue<string>())
        {
            if (char.IsControl(c))
                continue;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            sb.Append(c);
            if (sb.Length >= max)
                break;
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1]))
            sb.Length--;
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }
}
