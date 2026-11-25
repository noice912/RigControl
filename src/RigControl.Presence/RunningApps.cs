using RigControl.Core.Platform;

namespace RigControl.Presence;

/// <summary>A program the app card can name on Linux and macOS, by the name its process goes by.</summary>
public sealed record KnownApp(string Process, string Label, string IconDomain);

/// <summary>
/// The "current app" card where there is no way to ask which window is in front without extra system libraries
/// (Linux and macOS): it names a well-known program that this user is running. The list is short and in order of
/// preference: tools people work in come before browsers, which are nearly always open. No window title is read,
/// so nothing private can show.
/// </summary>
public static class RunningApps
{
    /// <summary>Linux keeps at most 15 characters of a program's name.</summary>
    public const int LinuxNameLength = 15;

    public static readonly IReadOnlyList<KnownApp> Known = new KnownApp[]
    {
        new("blender", "Blender", "blender.org"),
        new("krita", "Krita", "krita.org"),
        new("gimp", "GIMP", "gimp.org"),
        new("gimp-2.10", "GIMP", "gimp.org"),
        new("inkscape", "Inkscape", "inkscape.org"),
        new("kdenlive", "Kdenlive", "kdenlive.org"),
        new("godot", "Godot", "godotengine.org"),
        new("xcode", "Xcode", "developer.apple.com"),
        new("code", "Visual Studio Code", "code.visualstudio.com"),
        new("codium", "VSCodium", "vscodium.com"),
        new("rider", "JetBrains Rider", "jetbrains.com"),
        new("obs", "OBS Studio", "obsproject.com"),
        new("obsidian", "Obsidian", "obsidian.md"),
        new("soffice.bin", "LibreOffice", "libreoffice.org"),
        new("slack", "Slack", "slack.com"),
        new("steam", "Steam", "store.steampowered.com"),
        new("steam_osx", "Steam", "store.steampowered.com"),
        new("spotify", "Spotify", "spotify.com"),
        new("vlc", "VLC", "videolan.org"),
        new("firefox", "Firefox", "mozilla.org"),
        new("firefox-bin", "Firefox", "mozilla.org"),
        new("google chrome", "Google Chrome", "google.com"),
        new("chrome", "Google Chrome", "google.com"),
        new("chromium", "Chromium", "chromium.org"),
        new("brave", "Brave", "brave.com"),
        new("brave browser", "Brave", "brave.com"),
        new("safari", "Safari", "apple.com"),
    };

    /// <summary>True when a running program's name is this one; a name cut to 15 characters by Linux counts too.</summary>
    public static bool Matches(string processName, string known)
    {
        if (string.Equals(processName, known, StringComparison.OrdinalIgnoreCase)) return true;
        return known.Length > LinuxNameLength && processName.Length == LinuxNameLength
               && known.StartsWith(processName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first program of the list that is running and is not one of the user's games; null when none is.</summary>
    public static ForegroundApp? Pick(IEnumerable<string> processNames, IReadOnlySet<string> gameExes)
    {
        var names = processNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        foreach (var k in Known)
        {
            if (gameExes.Contains(k.Process)) continue;
            if (!names.Any(n => Matches(n, k.Process))) continue;
            return new ForegroundApp(k.Process, k.Label, "", "https://www.google.com/s2/favicons?sz=128&domain=" + k.IconDomain);
        }
        return null;
    }
}

/// <summary>
/// What the engine reads from the PC on Linux and macOS: only the names of this user's own running programs, and
/// only through RigControl's own process list (<see cref="IProcessQuery"/>). Music, "watching" and window titles
/// need Windows and are simply never there.
/// </summary>
public sealed class PortableInputs : IPresenceInputs
{
    /// <summary>Set by the module when it starts. Until then nothing is known to run.</summary>
    public IProcessQuery? Query { get; set; }

    public PortableInputs(IProcessQuery? query = null) => Query = query;

    private IReadOnlyList<ProcessEntry> Own()
    {
        try { return Query?.OwnProcesses() ?? Array.Empty<ProcessEntry>(); }
        catch { return Array.Empty<ProcessEntry>(); }
    }

    /// <summary>Lower-case names; when each program started is not known here.</summary>
    public IReadOnlyList<RunningProcess> Processes() =>
        Own().Select(p => new RunningProcess(p.Name.ToLowerInvariant(), null)).ToList();

    public Task<MusicNow?> MusicAsync(bool appleMusic, bool spotify) => Task.FromResult<MusicNow?>(null);

    public ForegroundApp? Foreground(IReadOnlySet<string> gameExes) => RunningApps.Pick(Own().Select(p => p.Name), gameExes);

    public Task<IReadOnlyList<MediaSession>> SessionsAsync() => Task.FromResult<IReadOnlyList<MediaSession>>(Array.Empty<MediaSession>());

    public IReadOnlyList<string> BrowserTitles() => Array.Empty<string>();
}
