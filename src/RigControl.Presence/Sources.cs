using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
#if WINDOWS
using Windows.Media.Control;
#endif

namespace RigControl.Presence;

public sealed record MusicNow(string Player, string Title, string Artist, string Album, bool Playing, double PositionSec, double DurationSec);

public sealed record ForegroundApp(string Proc, string Label, string Title, string? Icon);

public sealed record LyricLine(double T, string Text);

#if WINDOWS
/// <summary>What is playing in Apple Music or Spotify, from Windows' media controls (GSMTC).</summary>
public static class MusicSource
{
    public static async Task<MusicNow?> GetAsync(bool appleMusic, bool spotify)
    {
        if (!appleMusic && !spotify) return null;
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var cands = new List<(GlobalSystemMediaTransportControlsSession S, string Player)>();
        foreach (var s in mgr.GetSessions())
        {
            var aum = s.SourceAppUserModelId ?? "";
            if (appleMusic && Regex.IsMatch(aum, "AppleMusic|AppleInc")) cands.Add((s, "Apple Music"));
            else if (spotify && Regex.IsMatch(aum, @"^Spotify\.exe$|SpotifyAB\.SpotifyMusic", RegexOptions.IgnoreCase)) cands.Add((s, "Spotify"));
        }
        if (cands.Count == 0) return null;
        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var playing = cands.Where(c => IsPlaying(c.S)).ToList();
        var pick = cands[0];
        if (playing.Count == 1) pick = playing[0];
        else if (playing.Count > 1)
        {
            var cur = mgr.GetCurrentSession();
            pick = playing.FirstOrDefault(c => cur is not null && c.S.SourceAppUserModelId == cur.SourceAppUserModelId);
            if (pick.S is null) pick = playing.FirstOrDefault(c => c.Player == "Apple Music");
            if (pick.S is null) pick = playing[0];
        }

        var session = pick.S;
        var isPlaying = IsPlaying(session);
        var props = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var title = (props.Title ?? "").Trim();
        var artist = (props.Artist ?? "").Trim();
        var album = (props.AlbumTitle ?? "").Trim();
        // Apple Music puts "artist - album" in the artist field.
        if (pick.Player == "Apple Music" && album == "" && artist != "")
        {
            var m = Regex.Match(Regex.Replace(artist, "[‒–—―]", "-"), @"^(.+?)\s+-\s+(.+)$");
            if (m.Success) { artist = m.Groups[1].Value.Trim(); album = m.Groups[2].Value.Trim(); }
        }
        var tl = session.GetTimelineProperties();
        var duration = (tl.EndTime - tl.StartTime).TotalSeconds;
        var position = (tl.Position - tl.StartTime).TotalSeconds;
        if (isPlaying && tl.LastUpdatedTime.Year > 1) position += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
        if (duration > 0) position = Math.Max(0, Math.Min(position, duration));
        return new MusicNow(pick.Player, title, artist, album, isPlaying, position, duration);
    }
}
#endif

/// <summary>Album art (iTunes search) and synced lyrics (lrclib.net), cached per track.</summary>
public sealed class MusicExtras
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string?> _art = new();
    private readonly Dictionary<string, List<LyricLine>?> _lyrics = new();

    public MusicExtras(HttpClient http) => _http = http;

    public async Task<string?> ArtAsync(string artist, string title, string album, CancellationToken ct)
    {
        var key = $"{artist}|{album}|{title}";
        if (_art.TryGetValue(key, out var cached)) return cached;
        string? url = null;
        try
        {
            var term = Uri.EscapeDataString($"{artist} {album} {title}");
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=1", ct).ConfigureAwait(false));
            var results = doc.RootElement.GetProperty("results");
            if (results.GetArrayLength() > 0 && results[0].TryGetProperty("artworkUrl100", out var a))
                url = a.GetString()?.Replace("100x100bb", "512x512bb");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* no art */ }
        _art[key] = url;
        return url;
    }

    public async Task<List<LyricLine>?> LyricsAsync(string artist, string title, string album, double durationSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null; // a podcast or an ad: nothing to look up
        var key = $"{artist}|{title}|{album}";
        if (_lyrics.TryGetValue(key, out var cached)) return cached;
        List<LyricLine>? parsed = null;
        try
        {
            var q = $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            if (album != "") q += "&album_name=" + Uri.EscapeDataString(album);
            if (durationSec > 0) q += "&duration=" + (int)durationSec;
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://lrclib.net/api/get?" + q, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String)
                parsed = ParseLrc(sl.GetString()!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* not found */ }
        _lyrics[key] = parsed;
        return parsed;
    }

    public static List<LyricLine>? ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var line in lrc.Split('\n'))
        {
            var m = Regex.Match(line.TrimEnd('\r'), @"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");
            if (m.Success)
                lines.Add(new LyricLine(int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value.Trim()));
        }
        return lines.Count > 0 ? lines.OrderBy(l => l.T).ToList() : null;
    }

    public static string? LineAt(List<LyricLine>? lyrics, double pos)
    {
        if (lyrics is null) return null;
        string? line = null;
        foreach (var l in lyrics)
        {
            if (l.T <= pos + 0.3) line = l.Text;
            else break;
        }
        if (string.IsNullOrWhiteSpace(line)) return null;
        return line.Length > 128 ? line[..125] + "..." : line;
    }
}

/// <summary>The app in front (for the "current app" card).</summary>
public static class ForegroundSource
{
#if WINDOWS
    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "lockapp", "searchhost", "shellexperiencehost", "startmenuexperiencehost", "textinputhost",
        "systemsettings", "applicationframehost", "dwm", "sihost", "richpresence", "rigcontrol",
    };

    private static readonly Dictionary<string, string> Friendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "Visual Studio Code", ["devenv"] = "Visual Studio", ["powershell"] = "PowerShell", ["pwsh"] = "PowerShell",
        ["windowsterminal"] = "Windows Terminal", ["cmd"] = "Command Prompt", ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox", ["explorer"] = "File Explorer", ["notepad"] = "Notepad", ["spotify"] = "Spotify",
        ["applemusic"] = "Apple Music", ["steam"] = "Steam", ["obs64"] = "OBS Studio", ["photoshop"] = "Photoshop",
        ["blender"] = "Blender", ["slack"] = "Slack", ["obsidian"] = "Obsidian", ["winword"] = "Word", ["excel"] = "Excel",
    };

    private static readonly Dictionary<string, string> IconDomain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["devenv"] = "visualstudio.microsoft.com", ["code"] = "code.visualstudio.com", ["chrome"] = "google.com",
        ["msedge"] = "microsoft.com", ["firefox"] = "mozilla.org", ["spotify"] = "spotify.com", ["applemusic"] = "music.apple.com",
        ["steam"] = "store.steampowered.com", ["slack"] = "slack.com", ["obsidian"] = "obsidian.md", ["obs64"] = "obsproject.com",
        ["photoshop"] = "adobe.com", ["blender"] = "blender.org", ["winword"] = "microsoft.com", ["excel"] = "microsoft.com",
    };

#endif
    /// <summary>Browser tab titles end in " - Google Chrome" etc.</summary>
    public static readonly Regex BrowserSuffix = new(@"(?i)\s[‒–—―-]\s(Google Chrome|Mozilla Firefox|Brave|Opera|Vivaldi|Microsoft\W{0,3}Edge)$");

#if WINDOWS
    public static ForegroundApp? Get(IReadOnlySet<string> gameExes)
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out var pid);
        if (pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName.ToLowerInvariant();
            if (Ignore.Contains(name) || gameExes.Contains(name)) return null;
            if (!Friendly.TryGetValue(name, out var label))
            {
                try { label = p.MainModule?.FileVersionInfo.FileDescription; } catch { /* other user / protected */ }
            }
            if (string.IsNullOrWhiteSpace(label)) label = p.ProcessName;
            var icon = IconDomain.TryGetValue(name, out var d) ? "https://www.google.com/s2/favicons?sz=128&domain=" + d : null;
            return new ForegroundApp(name, label!, Title(h), icon);
        }
        catch
        {
            return null;
        }
    }

    private static string Title(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
#endif
}

public sealed record GenshinInfo(string Nickname, int Level, int WorldLevel);

/// <summary>Genshin Impact public profile from enka.network (no login, nothing private).</summary>
public sealed class GenshinSource
{
    private readonly HttpClient _http;
    private GenshinInfo? _info;
    private DateTime _fetched = DateTime.MinValue;
    private string? _uid;

    public GenshinSource(HttpClient http) => _http = http;

    public async Task<GenshinInfo?> GetAsync(string uid, CancellationToken ct)
    {
        if (!Regex.IsMatch(uid, @"^\d{9,10}$")) return null;
        if (uid == _uid && (DateTime.UtcNow - _fetched).TotalMinutes < 10) return _info;
        _uid = uid;
        _fetched = DateTime.UtcNow;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://enka.network/api/uid/{uid}?info", ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("playerInfo", out var pi))
                _info = new GenshinInfo(pi.TryGetProperty("nickname", out var n) ? n.GetString() ?? "" : "",
                    pi.TryGetProperty("level", out var l) ? l.GetInt32() : 0,
                    pi.TryGetProperty("worldLevel", out var w) ? w.GetInt32() : 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* keep the last one */ }
        return _info;
    }
}
