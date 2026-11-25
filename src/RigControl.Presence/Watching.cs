using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
#if WINDOWS
using Windows.Media.Control;
#endif

namespace RigControl.Presence;

/// <summary>
/// A streaming service. Key = the name its own Discord app goes by in <see cref="AppIds"/> (card reads "Watching
/// Netflix" with its logo); null = it uses the app card. The id itself is never written here.
/// </summary>
public sealed record StreamingService(string Name, string Domain, string Mark, string App, string? Key, bool Casual = false);

/// <summary>A media session from Windows' media controls, other than music players.</summary>
public sealed record MediaSession(string Aum, bool Playing, string Title, string Artist, double DurationSec, double PositionSec);

/// <summary>What the browser extension reported for one tab.</summary>
public sealed record ExtReport(string Title, string Episode, string Image, bool Playing, double Pos, double Dur, DateTime At);

public sealed record WatchingNow(StreamingService Svc, string Show, string Sub, double Dur, double Pos, string? Image);

/// <summary>
/// "Watching" detection, ported from RichPresence: the browser extension's reports first (it reads the player), then
/// Windows' media controls, recognising the site from the session's info, its title, or the browser's window titles.
/// </summary>
public static class Watching
{
    public static readonly IReadOnlyList<StreamingService> Services = new StreamingService[]
    {
        new("Netflix", "netflix.com", "Netflix", "Netflix", "netflix"),
        new("Hulu", "hulu.com", "Hulu", "Hulu", "hulu"),
        new("Disney+", "disneyplus.com", @"Disney\+", "Disney", "disney"),
        new("Prime Video", "primevideo.com", @"Prime Video|Amazon\.com", "PrimeVideo|AmazonVideo", "primeVideo"),
        new("Max", "max.com", "HBO Max|Max", "HBOMax|WarnerBros", "max"),
        new("Crunchyroll", "crunchyroll.com", "Crunchyroll", "Crunchyroll", "crunchyroll"),
        new("Paramount+", "paramountplus.com", @"Paramount\+", "Paramount", "paramount"),
        new("Peacock", "peacocktv.com", "Peacock", "Peacock", "peacock"),
        new("Apple TV+", "tv.apple.com", @"Apple TV\+?", "AppleTV", "appleTv"),
        new("Plex", "plex.tv", "Plex", "Plex", null),
        new("YouTube", "youtube.com", "YouTube", "YouTube", null, Casual: true),
        new("Twitch", "twitch.tv", "Twitch", "Twitch", null, Casual: true),
    };

    public static readonly IReadOnlySet<string> ServiceNames = Services.Select(s => s.Name).ToHashSet();

    private const string BrowserAum = "chrome|msedge|firefox|308046B0AF4A39CB|brave|opera|vivaldi";
#if WINDOWS
    private static readonly string[] BrowserProcs = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
#endif
    private const string Dashes = "•–—";
    private const string Sep = "[|" + Dashes + ":-]";
    private static readonly char[] TrimChars = (" |:-" + Dashes).ToCharArray();
    private static readonly Regex NotAShow = new("^(?i)(watch|watching|home|browse|player|play|video|videos|movie|movies|tv|series|shows?|search|details|stream)$");

    /// <summary>"Watch The Bear | Hulu - Google Chrome" -> Hulu, "The Bear". The site's name must be its own piece of the title.</summary>
    public static (StreamingService Svc, string Clean)? FindService(string? text, bool casual)
    {
        text = (text ?? "").Trim();
        if (text == "") return null;
        foreach (var sv in Services)
        {
            if (sv.Casual && !casual) continue;
            var m = Regex.Match(text, $@"(?i)(?<=^|\s{Sep}\s*)({sv.Mark})(?=(\s+and \d+ more pages?)?(\s*{Sep}\s|\s*{Sep}?$))");
            if (!m.Success) continue;
            var before = text[..m.Index];
            var after = text[(m.Index + m.Length)..];
            var clean = before.Trim(TrimChars) != "" ? before : ForegroundSource.BrowserSuffix.Replace(after, "");
            clean = Regex.Replace(clean, @"\s+and \d+ more pages?.*$", "");
            clean = Regex.Replace(Regex.Replace(clean.Trim(TrimChars), @"(?i)^(watch|stream)\s+", ""), @"(?i)\s+online$", "");
            return (sv, clean.Trim());
        }
        return null;
    }

    private static bool IsHost(string v) => Regex.IsMatch(v, @"^[\w-]+(\.[\w-]+)+$");

    /// <summary>Every tab reports separately: a playing tab wins over a paused one, then the newest. Older than maxAge s is ignored.</summary>
    public static ExtReport? Pick(IReadOnlyDictionary<string, ExtReport>? tabs, double maxAge, DateTime now)
    {
        ExtReport? best = null;
        if (tabs is null) return null;
        foreach (var e in tabs.Values)
        {
            if ((now - e.At).TotalSeconds >= maxAge) continue;
            if (best is null || (e.Playing && !best.Playing) || (e.Playing == best.Playing && e.At > best.At)) best = e;
        }
        return best;
    }

    public static WatchingNow? Find(IReadOnlyList<MediaSession> sessions, Func<IReadOnlyList<string>> browserTitles,
        Func<string, ExtReport?> ext, bool casual, DateTime now)
    {
        // The extension reads the player itself, so it knows best (a streaming service before YouTube/Twitch).
        foreach (var sv in Services.OrderBy(s => s.Casual))
        {
            if (sv.Casual && !casual) continue;
            var e = ext(sv.Name);
            if (e is { Playing: true })
            {
                var pos = e.Pos;
                if (e.Dur > 0) pos = Math.Min(e.Dur, pos + (now - e.At).TotalSeconds);
                return new WatchingNow(sv, e.Title, e.Episode, e.Dur, pos, e.Image == "" ? null : e.Image);
            }
        }

        IReadOnlyList<string>? titles = null;
        var found = new List<WatchingNow>();
        foreach (var s in sessions)
        {
            if (Regex.IsMatch(s.Aum, "AppleMusic|AppleInc|Spotify", RegexOptions.IgnoreCase) || !s.Playing) continue;
            StreamingService? sv = null;
            var winClean = "";
            if (!Regex.IsMatch(s.Aum, BrowserAum, RegexOptions.IgnoreCase))
            {
                sv = Services.FirstOrDefault(x => Regex.IsMatch(s.Aum, x.App, RegexOptions.IgnoreCase) && (casual || !x.Casual));
                if (sv is null) continue;
            }
            var mt = s.Title.Trim();
            var ma = s.Artist.Trim();
            var inTitle = FindService(mt, casual);
            // Without its own info the browser reports the tab title and the site ("www.netflix.com").
            sv ??= Services.FirstOrDefault(x => (casual || !x.Casual) && IsHost(ma) && Regex.IsMatch(ma, $@"(^|\.){Regex.Escape(x.Domain)}$", RegexOptions.IgnoreCase));
            if (sv is null && inTitle is { } it) sv = it.Svc;
            if (sv is null)
            {
                // Otherwise the browser's windows: one showing the same title is the best match.
                titles ??= browserTitles();
                (StreamingService Svc, string Clean)? hit = null;
                if (mt.Length >= 3)
                    foreach (var t in titles)
                        if (t.Contains(mt, StringComparison.OrdinalIgnoreCase) && (hit = FindService(t, casual)) is not null) break;
                if (hit is null)
                    foreach (var t in titles)
                        if ((hit = FindService(t, casual)) is not null) break;
                if (hit is null) continue;
                sv = hit.Value.Svc;
                winClean = hit.Value.Clean;
            }
            var show = "";
            if (inTitle is { } i2 && i2.Svc.Name == sv.Name) show = i2.Clean;
            else if (mt != "" && !IsHost(mt) && mt != sv.Name) show = mt;
            if (show == "") show = winClean;
            if (NotAShow.IsMatch(show)) show = "";
            // The extension says this service is paused: believe it over another tab's media session.
            if (ext(sv.Name) is { Playing: false }) continue;
            var sub = ma != "" && !IsHost(ma) && ma != show && ma != sv.Name ? ma : "";
            found.Add(new WatchingNow(sv, show, sub, s.DurationSec, s.PositionSec, null));
        }
        // A streaming service beats YouTube/Twitch when both play.
        return found.OrderBy(f => f.Svc.Casual).FirstOrDefault();
    }

#if WINDOWS
    // ---------------------------------------------------------------- Windows

    public static async Task<IReadOnlyList<MediaSession>> SessionsAsync()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var list = new List<MediaSession>();
        foreach (var s in mgr.GetSessions())
        {
            var aum = s.SourceAppUserModelId ?? "";
            var playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!playing || Regex.IsMatch(aum, "AppleMusic|AppleInc|Spotify", RegexOptions.IgnoreCase))
            {
                list.Add(new MediaSession(aum, playing, "", "", 0, 0));
                continue;
            }
            var props = await s.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var tl = s.GetTimelineProperties();
            var dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            var pos = (tl.Position - tl.StartTime).TotalSeconds;
            if (tl.LastUpdatedTime.Year > 1) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            list.Add(new MediaSession(aum, true, props.Title ?? "", props.Artist ?? "", dur, pos));
        }
        return list;
    }

    /// <summary>Titles of visible browser windows.</summary>
    public static IReadOnlyList<string> BrowserTitles()
    {
        // Only this Windows session's browsers.
        var pids = WinSession.PidsByName(BrowserProcs);
        if (pids.Count == 0) return Array.Empty<string>();
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var len = GetWindowTextLength(h);
            if (len <= 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            list.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
#endif
}

/// <summary>Posters (TVMaze for shows, Cinemeta for movies) and service logos, cached.</summary>
public sealed class WatchExtras
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string?> _posters = new();
    private readonly Dictionary<string, string> _logos = new();

    public WatchExtras(HttpClient http) => _http = http;

    /// <summary>Only a result whose name is the start of what's playing ("The Bear Season 2" -> "The Bear").</summary>
    public static bool SameTitle(string playing, string? found)
    {
        var np = LibraryScanner.Norm(playing);
        var nf = LibraryScanner.Norm(found);
        return np.Length >= 3 && nf.Length >= 3 && np.StartsWith(nf, StringComparison.Ordinal);
    }

    public async Task<string?> PosterAsync(string show, string sub, CancellationToken ct)
    {
        var q = show.Trim();
        if (q.Length < 2) return null;
        if (_posters.TryGetValue(q, out var cached)) return cached;
        string? url = null;
        var tries = new[] { q, Regex.Split(q, @"\s[|•–—:-]\s|:\s")[0].Trim(), sub.Trim() }.Where(t => t.Length >= 2).Distinct().ToList();
        foreach (var t in tries)
        {
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(t), ct).ConfigureAwait(false));
                var r = doc.RootElement;
                if (r.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object && SameTitle(t, r.GetProperty("name").GetString()))
                    url = (img.TryGetProperty("original", out var o) && o.ValueKind == JsonValueKind.String ? o : img.GetProperty("medium")).GetString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* not found */ }
            if (url is not null) break;
        }
        // Then movies, but never for something that's clearly an episode.
        if (url is null && !Regex.IsMatch($"{show} {sub}", @"(?i)\b(season|episode|ep\.?\s*\d|s\d+\s*:?\s*e\d+)"))
        {
            foreach (var t in tries)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://v3-cinemeta.strem.io/catalog/movie/top/search={Uri.EscapeDataString(t)}.json", ct).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("metas", out var metas))
                        foreach (var m in metas.EnumerateArray().Take(3))
                            if (m.TryGetProperty("poster", out var p) && p.ValueKind == JsonValueKind.String && SameTitle(t, m.TryGetProperty("name", out var n) ? n.GetString() : null))
                            {
                                url = p.GetString();
                                break;
                            }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* not found */ }
                if (url is not null) break;
            }
        }
        if (url is { Length: > 256 } || (url is not null && !url.StartsWith("https://", StringComparison.Ordinal))) url = null;
        _posters[q] = url;
        return url;
    }

    /// <summary>The icon uploaded to the service's Discord app (public), else the website's icon.</summary>
    /// <param name="appId">The service's own Discord app in this build, or null when it has none.</param>
    public async Task<string> LogoAsync(StreamingService sv, string? appId, CancellationToken ct)
    {
        var key = sv.Name + "|" + appId;
        if (_logos.TryGetValue(key, out var cached)) return cached;
        var logo = "https://www.google.com/s2/favicons?sz=128&domain=" + sv.Domain;
        if (AppIds.IsId(appId))
        {
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://discord.com/api/v9/applications/{appId}/rpc", ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("icon", out var ic) && ic.GetString() is { } icon && Regex.IsMatch(icon, "^[0-9a-f]{32}$"))
                    logo = $"https://cdn.discordapp.com/app-icons/{appId}/{icon}.png?size=256";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* website icon */ }
        }
        _logos[key] = logo;
        return logo;
    }
}
