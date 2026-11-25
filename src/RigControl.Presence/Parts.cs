using System.Net.Http;
using RigControl.Core.Logging;
using RigControl.Core.Modules;

namespace RigControl.Presence;

/// <summary>Finds the games installed on this PC.</summary>
public interface IGameLibrary
{
    Task<List<Game>> ScanAsync(string dataDirectory, ILog log, HttpClient http, IReadOnlyList<string> extraFolders,
        IReadOnlyList<CustomGame> customGames, CancellationToken ct);
}

/// <summary>Starts a game, or shows a folder, as the signed-in user would.</summary>
public interface IGameLauncher
{
    /// <summary>A store link (steam://...) or the game's own file.</summary>
    void Start(string target);
    void OpenFolder(string folder);
}

/// <summary>The local link the browser extension reports to.</summary>
public interface IExtensionBridge : IDisposable
{
    bool Listening { get; }
    /// <summary>The port it is listening on, or null while it isn't.</summary>
    int? Port { get; }
    /// <summary>The newest report for a streaming service, if a fresh one is there.</summary>
    ExtReport? Get(string service);
    void Start();
}

/// <summary>
/// The parts of Discord status that are not the same on every system. The Windows flavour has all of them; the
/// flavour for Linux and macOS has none, and the module then answers "Not available on this system" for what is
/// missing. What the engine reads from the PC (music, the app in front, running programs) is the separate
/// <see cref="IPresenceInputs"/>.
/// </summary>
public sealed class PresenceParts
{
    /// <summary>The names every system has.</summary>
    public static readonly IReadOnlyList<string> EverywhereNames = new[] { "presence.custom", "presence.apps" };

    /// <summary>The names only the Windows flavour has.</summary>
    public static readonly IReadOnlyList<string> WindowsNames = new[]
    {
        "presence.media", "presence.games", "presence.launcher", "presence.watching", "presence.extension",
    };

    /// <summary>The game list. Null: there is none here.</summary>
    public IGameLibrary? Library { get; init; }

    /// <summary>Starting a game and showing a folder. Null: not here.</summary>
    public IGameLauncher? Launcher { get; init; }

    /// <summary>Makes the browser extension link (log, RigControl's version, this user's secret). Null: not here.</summary>
    public Func<ILog, string, Func<string?>, IExtensionBridge>? Bridge { get; init; }

    /// <summary>Copy the settings of the standalone RichPresence app once, when it is installed (Windows).</summary>
    public bool ImportFromRichPresence { get; init; }

    /// <summary>What the module says it can do here (see <see cref="IModuleCapabilities"/>).</summary>
    public IReadOnlyCollection<string> Capabilities { get; init; } = EverywhereNames;

    /// <summary>Custom statuses and a card for a running program: all that works without Windows.</summary>
    public static PresenceParts Portable { get; } = new();

    /// <summary>The parts this build of RigControl.Presence has.</summary>
    public static PresenceParts ForThisBuild() =>
#if WINDOWS
        WindowsParts.Create();
#else
        Portable;
#endif

    internal static Exception NoGames() => ModuleAvailability.Error("the game list works on Windows only.");
    internal static Exception NoExtension() => ModuleAvailability.Error("the browser extension link works on Windows only.");
}
