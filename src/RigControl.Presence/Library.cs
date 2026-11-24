using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RigControl.Core.Logging;

namespace RigControl.Presence;

/// <summary>One installed game. Same fields (and JSON names) as RichPresence's library.json.</summary>
public sealed class Game
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("store")] public string Store { get; set; } = "";
    [JsonPropertyName("install")] public string Install { get; set; } = "";
    /// <summary>A store link (steam://...) or the game's exe.</summary>
    [JsonPropertyName("launch")] public string? Launch { get; set; }
    /// <summary>Lower-case exe names without ".exe" that mean this game is running.</summary>
    [JsonPropertyName("exes")] public List<string> Exes { get; set; } = new();
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("art")] public string? Art { get; set; }
    [JsonPropertyName("discordId")] public string? DiscordId { get; set; }
    [JsonPropertyName("matched")] public bool Matched { get; set; }
}

public sealed class CustomGame
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
}

/// <summary>
/// Finds installed games (Steam, Epic, GOG, launcher folders, Games folders, the user's own folders and games)
/// and matches each to its official Discord application from Discord's public "detectable" list.
/// Ported from RichPresence's scanner so it finds the same games.
/// </summary>
public sealed class LibraryScanner
{
    private static readonly Regex ExcludeExe = new(
        @"^(unins.*|.*setup.*|vc_?redist.*|vcredist.*|dxwebsetup|dotnet.*|.*crash.*|.*report.*|.*prereq.*|.*install.*|.*updater?|.*helper|.*cefsubprocess.*|.*bootstrap.*|.*anticheat.*|beservice.*|.*benchmark.*|.*launcher.*|elevate|.*browser|.*editor.*|.*redist.*|.*webview.*|.*service)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] GroupNames = { "epic", "hoyoplay", "rockstar", "steam", "riot games", "origin", "ea", "ubisoft", "xboxgames", "gog galaxy", "games", "epic games", "battle.net" };
    private static readonly string[] SkipNames = { "steamlibrary", "steamapps", "launcher", "gamesave", "directxredist", "gameinputredist", "riot client", "common", "downloading", "temp" };
    private static readonly string[] Generic = { "game", "launcher", "client", "main", "start", "play", "app", "shipping", "win64", "editor", "server" };

    private readonly string _dataDir;
    private readonly ILog _log;
    private readonly HttpClient _http;
    private readonly List<Game> _games = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public LibraryScanner(string dataDir, ILog log, HttpClient http)
    {
        _dataDir = dataDir;
        _log = log;
        _http = http;
    }

    public static string Norm(string? n) => Regex.Replace((n ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    public async Task<List<Game>> ScanAsync(IReadOnlyList<string> extraFolders, IReadOnlyList<CustomGame> customGames, CancellationToken ct)
    {
        _games.Clear();
        _seen.Clear();
        Try("Steam", ScanSteam);
        Try("Epic", ScanEpic);
        Try("GOG", ScanGog);
        Try("folders", () => ScanFolders(extraFolders));
        Try("added games", () =>
        {
            foreach (var cg in customGames)
            {
                if (string.IsNullOrWhiteSpace(cg.Exe) || !File.Exists(cg.Exe)) continue;
                var fi = new FileInfo(cg.Exe);
                Add("custom:" + fi.FullName.ToLowerInvariant(), cg.Name, "Added", fi.DirectoryName!, fi.FullName, new[] { fi }, null);
            }
        });

        // An exe shared by several games (a bundled helper) can't identify any one of them.
        var count = new Dictionary<string, int>();
        foreach (var g in _games) foreach (var e in g.Exes) count[e] = count.GetValueOrDefault(e) + 1;
        foreach (var g in _games)
        {
            var unique = g.Exes.Where(e => count[e] == 1).ToList();
            if (unique.Count > 0) g.Exes = unique;
        }

        try
        {
            await MatchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Couldn't match games to Discord; they are still listed. " + ex.Message);
        }
        var sorted = _games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _log.Info($"Game library: {sorted.Count} games ({sorted.Count(g => g.Matched)} matched to Discord).");
        return sorted;
    }

    private void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { _log.Warn($"Game scan problem ({what}): {ex.Message}"); }
    }

    private static List<FileInfo> GetExes(string dir)
    {
        var all = new List<FileInfo>();
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 7, IgnoreInaccessible = true };
            all = new DirectoryInfo(dir).EnumerateFiles("*.exe", opts).ToList();
        }
        catch { /* unreadable folder */ }
        var good = all.Where(f => !ExcludeExe.IsMatch(Path.GetFileNameWithoutExtension(f.Name))).ToList();
        if (good.Count == 0) good = all;
        return good.OrderByDescending(f => f.Length).Take(8).ToList();
    }

    private void Add(string id, string? name, string store, string? install, string? launch, IReadOnlyList<FileInfo>? exes, string? art)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) return;
        var key = install.TrimEnd('\\').ToLowerInvariant();
        if (_seen.Contains(key)) return;
        exes ??= GetExes(install);
        if (exes.Count == 0) return;
        _seen.Add(key);
        _games.Add(new Game
        {
            Id = id, Name = name, Store = store, Install = install, Launch = launch, Art = art, Icon = exes[0].FullName,
            Exes = exes.Select(f => Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant()).Distinct().ToList(),
        });
    }

    private void ScanSteam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        if (string.IsNullOrEmpty(steam) || !Directory.Exists(steam)) return;
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var t = File.ReadAllText(acf);
                var appid = Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                var name = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                var dir = Regex.Match(t, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (appid == "" || name == "" || Regex.IsMatch(name, "Steamworks|Proton|Steam Linux|Redistributable|Runtime|Wallpaper Engine|Dedicated Server|SDK")) continue;
                string? art = null;
                var cache = Path.Combine(steam, @"appcache\librarycache", appid);
                foreach (var f in new[] { "header.jpg", "library_600x900.jpg" })
                    if (art is null && File.Exists(Path.Combine(cache, f))) art = Path.Combine(cache, f);
                Add("steam:" + appid, name, "Steam", Path.Combine(apps, "common", dir), "steam://rungameid/" + appid, null, art);
            }
        }
    }

    private void ScanEpic()
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            string? S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = S("DisplayName");
            var loc = S("InstallLocation");
            var app = S("AppName");
            if (name is null || loc is null || app is null || (S("LaunchExecutable") ?? "").Contains("Editor") || name.Contains("Editor")) continue;
            // It goes into a launch link: anything but a plain id is not used.
            if (!Regex.IsMatch(app, "^[A-Za-z0-9_-]+$")) continue;
            Add("epic:" + app, name, "Epic", loc, $"com.epicgames.launcher://apps/{app}?action=launch&silent=true", null, null);
        }
    }

    private void ScanGog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root is null) return;
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            var name = k?.GetValue("gameName") as string;
            var path = k?.GetValue("path") as string;
            if (name is not null && path is not null) Add("gog:" + sub, name, "GOG", path, k?.GetValue("exe") as string, null, null);
        }
    }

    private void ScanFolders(IReadOnlyList<string> extra)
    {
        var roots = new List<string> { @"C:\Riot Games", @"C:\XboxGames", @"C:\Program Files\EA Games", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games", @"C:\Program Files\Epic Games" };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)) roots.Add(Path.Combine(d.Name, "Games"));
        roots.AddRange(extra);
        foreach (var r in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) ScanFolder(r, 0);
    }

    private void ScanFolder(string dir, int depth)
    {
        string[] children;
        try { children = Directory.GetDirectories(dir); } catch { return; }
        foreach (var c in children)
        {
            var n = Path.GetFileName(c).ToLowerInvariant();
            if (SkipNames.Contains(n)) continue;
            if (GroupNames.Contains(n) && depth < 2) { ScanFolder(c, depth + 1); continue; }
            var exes = GetExes(c);
            if (exes.Count == 0) continue;
            var name = Regex.Replace(Path.GetFileName(c), @"\s+[Gg]ames?$", "");
            try
            {
                var pn = FileVersionInfo.GetVersionInfo(exes[0].FullName).ProductName;
                if (pn is { Length: > 2 } && !Regex.IsMatch(pn, "Unreal|Unity")) name = pn;
            }
            catch { /* keep the folder name */ }
            Add("folder:" + c.ToLowerInvariant(), name, "Folder", c, exes[0].FullName, exes, null);
        }
    }

    // ---------------------------------------------------------------- Discord's detectable list

    private sealed record DetApp(string Id, string Name);

    private async Task<JsonDocument> LoadDetectableAsync(CancellationToken ct)
    {
        var cache = Path.Combine(_dataDir, "detectable.json");
        if (!File.Exists(cache) || (DateTime.Now - File.GetLastWriteTime(cache)).TotalDays > 7)
        {
            try
            {
                _log.Info("Downloading Discord's list of known games.");
                var text = await _http.GetStringAsync("https://discord.com/api/v9/applications/detectable", ct).ConfigureAwait(false);
                JsonDocument.Parse(text).Dispose(); // only keep it if it is valid JSON
                Directory.CreateDirectory(_dataDir);
                await File.WriteAllTextAsync(cache, text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(cache))
            {
                _log.Warn("Couldn't refresh Discord's list of known games; using the saved one. " + ex.Message);
            }
        }
        return JsonDocument.Parse(await File.ReadAllTextAsync(cache, ct).ConfigureAwait(false));
    }

    private async Task MatchAsync(CancellationToken ct)
    {
        using var doc = await LoadDetectableAsync(ct).ConfigureAwait(false);
        var byExe = new Dictionary<string, List<DetApp>>();
        var byName = new Dictionary<string, DetApp>();
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            var app = new DetApp(a.GetProperty("id").ToString().Trim('"'), a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "");
            var an = Norm(app.Name);
            if (an != "") byName.TryAdd(an, app);
            if (!a.TryGetProperty("executables", out var exes) || exes.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in exes.EnumerateArray())
            {
                if (!e.TryGetProperty("os", out var os) || os.GetString() != "win32") continue;
                if (e.TryGetProperty("is_launcher", out var il) && il.ValueKind == JsonValueKind.True) continue;
                var raw = e.TryGetProperty("name", out var en) ? en.GetString() ?? "" : "";
                var b = Regex.Replace(raw.Split('\\', '/').Last().ToLowerInvariant(), "^>", "");
                b = Regex.Replace(b, @"\.exe$", "");
                if (b == "") continue;
                if (!byExe.TryGetValue(b, out var list)) byExe[b] = list = new List<DetApp>();
                list.Add(app);
            }
        }

        foreach (var g in _games)
        {
            var gn = Norm(g.Name);
            var score = new Dictionary<string, int>();
            var apps = new Dictionary<string, DetApp>();
            foreach (var e in g.Exes)
            {
                if (!byExe.TryGetValue(e, out var list)) continue;
                foreach (var a in list)
                {
                    apps[a.Id] = a;
                    var an = Norm(a.Name);
                    var pts = 1;
                    if (an == gn) pts += 10;
                    else if (gn.Length >= 4 && an.Length >= 4 && (an.Contains(gn) || gn.Contains(an))) pts += 5;
                    else if (e.Length >= 8 && !Generic.Contains(e)) pts += 3;
                    score[a.Id] = score.GetValueOrDefault(a.Id) + pts;
                }
            }
            DetApp? hit = null;
            var best = score.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best.Key is not null && best.Value >= 4)
            {
                hit = apps[best.Key];
                // Trust Discord's own executable list for this game over our guesses.
                var official = g.Exes.Where(e => byExe.TryGetValue(e, out var l) && l.Any(a => a.Id == best.Key)).ToList();
                if (official.Count > 0) g.Exes = official;
            }
            else if (byName.TryGetValue(gn, out var byN))
            {
                hit = byN;
            }
            if (hit is null) continue;
            g.DiscordId = hit.Id;
            g.Matched = true;
            if (g.Store is "Folder" or "Added") g.Name = hit.Name;
        }
    }
}
