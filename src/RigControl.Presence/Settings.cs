using System.Text.Json;

namespace RigControl.Presence;

/// <summary>Discord status settings. Names follow RichPresence's settings.json so they can be imported once.</summary>
public sealed class PresenceSettings
{
    public const string Section = "presence";

    /// <summary>Show my status on Discord. Off until the user turns it on (standalone RichPresence may still be running).</summary>
    public bool Enabled { get; set; }
    public string GenshinUid { get; set; } = "";
    public bool ShowMusic { get; set; } = true;
    public bool ShowSpotify { get; set; } = true;
    public bool ShowLyrics { get; set; } = true;
    public bool ShowAlbumArt { get; set; } = true;
    /// <summary>The app in front, with its window or tab title. Off unless the user turns it on: titles can be private.</summary>
    public bool ShowCurrentApp { get; set; }
    public List<string> DisabledGames { get; set; } = new();
    public List<string> ExtraFolders { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();
    public bool ShowWatching { get; set; } = true;
    public bool ShowWatchTitle { get; set; } = true;
    public bool ShowWatchPoster { get; set; } = true;
    /// <summary>Also YouTube and Twitch.</summary>
    public bool ShowWatchCasual { get; set; } = true;
    public List<CustomStatus> CustomStatuses { get; set; } = new();
    /// <summary>"separate" = one card each, "merge" = the ticked ones become one card.</summary>
    public string CustomMode { get; set; } = "separate";
    public bool OfficialToDiscord { get; set; } = true;
    public double OfficialDelayMinutes { get; set; } = 2;
    /// <summary>When RichPresence's settings were copied in (null = never).</summary>
    public DateTimeOffset? ImportedFromRichPresence { get; set; }
    /// <summary>
    /// Your own Discord application ids, by card name (settings.json: presence > appIds). Only needed when you built
    /// RigControl yourself, since such a build carries no ids; an entry here also wins over a built-in one.
    /// Names: game, music, app, netflix, hulu, disney, primeVideo, max, crunchyroll, paramount, peacock, appleTv,
    /// custom1 to custom9. Each id is 17 to 20 digits and may be used for one card only; anything else is ignored.
    /// </summary>
    public Dictionary<string, string> AppIds { get; set; } = new();
    /// <summary>
    /// The Spotify sign-in a linked phone handed over (its refresh token), protected for this Windows user
    /// (<c>ISecretProtector</c>, base64). Empty = not connected. Never in plain text, never logged.
    /// </summary>
    public string SpotifySignIn { get; set; } = "";

    public static string RichPresenceDataDir =>
        Environment.GetEnvironmentVariable("RICHPRESENCE_DATA") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichPresence");

    /// <summary>
    /// Copies the Stage 1 settings from RichPresence's settings.json. Discord app ids are never read from it.
    /// Returns false when there is nothing to import.
    /// </summary>
    public bool ImportFromRichPresence(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath).TrimStart('﻿'));
        var r = doc.RootElement;
        bool B(string k, bool def) => r.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && def) : def;
        List<string> L(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
        GenshinUid = r.TryGetProperty("genshin_uid", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : "";
        ShowMusic = B("show_music", true);
        ShowSpotify = B("show_spotify", true);
        ShowLyrics = B("show_lyrics", true);
        ShowAlbumArt = B("show_album_art", true);
        ShowCurrentApp = B("show_current_app", false);
        OfficialToDiscord = B("official_to_discord", true);
        ShowWatching = B("show_watching", true);
        ShowWatchTitle = B("show_watch_title", true);
        ShowWatchPoster = B("show_watch_poster", true);
        ShowWatchCasual = B("show_watch_casual", true);
        CustomMode = r.TryGetProperty("custom_mode", out var cm) && cm.ValueKind == JsonValueKind.String && cm.GetString() == "merge" ? "merge" : "separate";
        CustomStatuses = new List<CustomStatus>();
        if (r.TryGetProperty("custom_statuses", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var x in cs.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && x.Deserialize<CustomStatus>() is { } st) CustomStatuses.Add(st);
        if (r.TryGetProperty("official_delay_minutes", out var d) && d.ValueKind == JsonValueKind.Number) OfficialDelayMinutes = Math.Clamp(d.GetDouble(), 0, 60);
        DisabledGames = L("disabled_games");
        ExtraFolders = L("extra_folders");
        CustomGames = new List<CustomGame>();
        if (r.TryGetProperty("custom_games", out var cg) && cg.ValueKind == JsonValueKind.Array)
            foreach (var x in cg.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("exe", out var e) && e.ValueKind == JsonValueKind.String)
                    CustomGames.Add(new CustomGame { Exe = e.GetString()!, Name = x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "" });
        ImportedFromRichPresence = DateTimeOffset.Now;
        return true;
    }
}
