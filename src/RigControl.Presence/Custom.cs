using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RigControl.Presence;

/// <summary>A card of your own: text, pictures, buttons. Same fields (and JSON names) as RichPresence's custom_statuses.</summary>
public sealed class CustomStatus
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>0 Playing, 2 Listening to, 3 Watching, 5 Competing in.</summary>
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("details")] public string Details { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("large_image")] public string LargeImage { get; set; } = "";
    [JsonPropertyName("large_text")] public string LargeText { get; set; } = "";
    [JsonPropertyName("small_image")] public string SmallImage { get; set; } = "";
    [JsonPropertyName("small_text")] public string SmallText { get; set; } = "";
    [JsonPropertyName("button1_label")] public string Button1Label { get; set; } = "";
    [JsonPropertyName("button1_url")] public string Button1Url { get; set; } = "";
    [JsonPropertyName("button2_label")] public string Button2Label { get; set; } = "";
    [JsonPropertyName("button2_url")] public string Button2Url { get; set; } = "";
    [JsonPropertyName("elapsed")] public bool Elapsed { get; set; } = true;
    /// <summary>The user's own Discord app id (optional). Without one, a built-in app is borrowed (only a few exist).</summary>
    [JsonPropertyName("app_id")] public string AppId { get; set; } = "";
}

public sealed record CustomCard(string AppId, string Key, string Label, bool Elapsed, JsonObject Activity);

/// <summary>Custom status cards, ported from RichPresence (New-CustomActivity, Merge-CustomStatuses, Get-CustomCards).</summary>
public static class CustomCards
{
    private const string Dot = " · ";
    public static bool IsAppId(string? v) => AppIds.IsId((v ?? "").Trim());

    /// <summary>
    /// True for an app id RigControl uses for its own cards (game, music, current app, the streaming services and
    /// the borrowed ones). A custom status may not claim one: two cards on one id would overwrite each other.
    /// </summary>
    public static bool IsBuiltIn(string? v, AppIds ids) => ids.Contains((v ?? "").Trim());

    /// <summary>True when the status brings its own Discord app id that it may use.</summary>
    private static bool HasOwnApp(CustomStatus s, AppIds ids) => IsAppId(s.AppId) && !IsBuiltIn(s.AppId, ids);

    private static string Limit(string? t, int max)
    {
        t = (t ?? "").Trim();
        return t.Length > max ? t[..(max - 3)] + "..." : t;
    }

    public static string Label(CustomStatus s)
    {
        foreach (var v in new[] { s.Name, s.Details, s.State })
            if (v.Trim() != "") return v.Trim();
        return "Custom status";
    }

    /// <summary>A picture is an https link, or the name of a picture uploaded to the user's own Discord app.</summary>
    public static string? Image(string? v)
    {
        v = (v ?? "").Trim();
        if (Regex.IsMatch(v, @"^https://\S+$") && v.Length <= 256) return v;
        if (Regex.IsMatch(v, @"^[\w.-]{1,128}$")) return v;
        return null;
    }

    public static JsonObject Activity(CustomStatus s)
    {
        var a = new JsonObject { ["type"] = s.Type is 2 or 3 or 5 ? s.Type : 0 };
        void Text(string key, string v) { v = Limit(v, 128); if (v.Length >= 2) a[key] = v; }
        Text("name", s.Name);
        Text("details", s.Details);
        Text("state", s.State);
        var assets = new JsonObject();
        if (Image(s.LargeImage) is { } li)
        {
            assets["large_image"] = li;
            var t = Limit(s.LargeText, 128);
            if (t.Length >= 2) assets["large_text"] = t;
        }
        if (Image(s.SmallImage) is { } si)
        {
            assets["small_image"] = si;
            var t = Limit(s.SmallText, 128);
            if (t.Length >= 2) assets["small_text"] = t;
        }
        if (assets.Count > 0) a["assets"] = assets;
        var buttons = new JsonArray();
        foreach (var (l, u) in new[] { (s.Button1Label, s.Button1Url), (s.Button2Label, s.Button2Url) })
        {
            var label = Limit(l, 32);
            var url = (u ?? "").Trim();
            if (label != "" && Regex.IsMatch(url, @"^https?://\S+$") && url.Length <= 512) buttons.Add(new JsonObject { ["label"] = label, ["url"] = url });
        }
        if (buttons.Count > 0) a["buttons"] = buttons;
        return a;
    }

    /// <summary>
    /// Several ticked statuses as one card: the first one's title, type and app id; every line joined; the first
    /// picture big and the next one small; the first two buttons found.
    /// </summary>
    public static CustomStatus Merge(IReadOnlyList<CustomStatus> list)
    {
        var first = list[0];
        var m = new CustomStatus
        {
            Id = "merged", Enabled = true, Type = first.Type, Name = first.Name, AppId = first.AppId,
            Elapsed = list.Any(x => x.Elapsed),
            Details = string.Join(Dot, list.Select(x => x.Details.Trim()).Where(x => x != "")),
            State = string.Join(Dot, list.Select(x => x.State.Trim()).Where(x => x != "")),
            SmallImage = first.SmallImage, SmallText = first.SmallText,
        };
        var pics = list.Where(x => Image(x.LargeImage) is not null).Select(x => (x.LargeImage, x.LargeText)).ToList();
        if (pics.Count > 0) (m.LargeImage, m.LargeText) = pics[0];
        if (Image(m.SmallImage) is null && pics.Count > 1) (m.SmallImage, m.SmallText) = pics[1];
        var btns = list.SelectMany(x => new[] { (x.Button1Label.Trim(), x.Button1Url.Trim()), (x.Button2Label.Trim(), x.Button2Url.Trim()) })
            .Where(b => b.Item1 != "" && Regex.IsMatch(b.Item2, "^https?://")).Take(2).ToList();
        if (btns.Count > 0) (m.Button1Label, m.Button1Url) = btns[0];
        if (btns.Count > 1) (m.Button2Label, m.Button2Url) = btns[1];
        return m;
    }

    /// <summary>The cards to show, and the labels of ticked statuses that got no free app.</summary>
    /// <param name="ids">The app ids in use: its pool is what statuses without their own id borrow (it can be empty).</param>
    public static (List<CustomCard> Cards, List<string> Skipped) Build(IReadOnlyList<CustomStatus> items, string mode, AppIds ids)
    {
        var pool = ids.Pool;
        var on = items.Where(x => x.Enabled).ToList();
        if (mode == "merge" && on.Count > 1) on = new List<CustomStatus> { Merge(on) };
        // Statuses with their own Discord app claim it first; the rest share the built-in ones.
        // A built-in id typed in by hand (kept from an older version) counts as no id at all.
        var ordered = on.Where(x => HasOwnApp(x, ids)).Concat(on.Where(x => !HasOwnApp(x, ids)));
        var cards = new List<CustomCard>();
        var used = new HashSet<string>();
        var skipped = new List<string>();
        var pi = 0;
        foreach (var st in ordered)
        {
            var cid = st.AppId.Trim();
            if (!HasOwnApp(st, ids))
            {
                cid = null!;
                while (pi < pool.Count && cid is null)
                {
                    if (!used.Contains(pool[pi])) cid = pool[pi];
                    pi++;
                }
            }
            if (cid is null || used.Contains(cid))
            {
                skipped.Add(Label(st));
                continue;
            }
            used.Add(cid);
            cards.Add(new CustomCard(cid, st.Id, Label(st), st.Elapsed, Activity(st)));
        }
        return (cards, skipped);
    }
}
