using System.Net.Http;
using RigControl.Core.Logging;

namespace RigControl.Presence;

/// <summary>
/// Windows: the parts are the classes RigControl has always used (the registry and folder scan, the start through
/// Explorer, the extension link on 127.0.0.1). This file is not part of the Linux and macOS flavour.
/// </summary>
internal static class WindowsParts
{
    public static PresenceParts Create() => new()
    {
        Library = new Scanner(),
        Launcher = new ShellLauncher(),
        Bridge = (log, version, linkToken) => new ExtensionBridge(log, version, linkToken: linkToken),
        ImportFromRichPresence = true,
        Capabilities = PresenceParts.EverywhereNames.Concat(PresenceParts.WindowsNames).ToArray(),
    };

    private sealed class Scanner : IGameLibrary
    {
        public Task<List<Game>> ScanAsync(string dataDirectory, ILog log, HttpClient http, IReadOnlyList<string> extraFolders,
            IReadOnlyList<CustomGame> customGames, CancellationToken ct) =>
            new LibraryScanner(dataDirectory, log, http).ScanAsync(extraFolders, customGames, ct);
    }

    private sealed class ShellLauncher : IGameLauncher
    {
        public void Start(string target) => Launcher.Start(target);
        public void OpenFolder(string folder) => Launcher.StartFolder(folder);
    }
}
