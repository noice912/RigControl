using RigControl.Core.Platform;

namespace RigControl.Hosting;

/// <summary>
/// Where RigControl keeps its files for the signed-in user.
/// Windows: %LOCALAPPDATA%\RigControl, logs in its "logs" folder (as always).
/// Linux:   settings and data in $XDG_CONFIG_HOME/RigControl (normally ~/.config/RigControl),
///          logs in $XDG_STATE_HOME/RigControl/logs (normally ~/.local/state/RigControl/logs).
/// macOS:   ~/Library/Application Support/RigControl, logs in ~/Library/Logs/RigControl.
/// </summary>
public sealed record AppPaths(string DataDirectory, string LogDirectory)
{
    public const string AppFolder = "RigControl";

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>The folders for the system this process runs on.</summary>
    public static AppPaths ForThisSystem()
        => For(PlatformInfo.Current, Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The same with everything it reads passed in, so it can be tested on any system.
    /// An XDG variable that is empty or not a full path is ignored, as the XDG rules say.
    /// </summary>
    public static AppPaths For(HostPlatform platform, Func<string, string?> env, string home, string localAppData)
    {
        switch (platform)
        {
            case HostPlatform.Linux:
            {
                var config = Xdg(env("XDG_CONFIG_HOME")) ?? Join(home, ".config");
                var state = Xdg(env("XDG_STATE_HOME")) ?? Join(home, ".local", "state");
                return new AppPaths(Join(config, AppFolder), Join(state, AppFolder, "logs"));
            }
            case HostPlatform.MacOS:
                return new AppPaths(Join(home, "Library", "Application Support", AppFolder), Join(home, "Library", "Logs", AppFolder));
            default:
            {
                var data = Path.Combine(localAppData, AppFolder);
                return new AppPaths(data, Path.Combine(data, "logs"));
            }
        }
    }

    private static string? Xdg(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith('/') ? value.TrimEnd('/') : null;

    // Linux and macOS paths are joined with "/" whatever system the code (or a test) runs on.
    private static string Join(params string[] parts) => string.Join('/', parts.Select((p, i) => i == 0 ? p.TrimEnd('/') : p));

    /// <summary>
    /// Makes the folders. On Linux and macOS they are made for the owner only (0700), since they hold the settings
    /// and the key file. A folder that is already there is left as it is. Never throws.
    /// </summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDirectory, LogDirectory })
        {
            try
            {
                if (Directory.Exists(dir)) continue;
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
                else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // The log and the settings store both cope with a missing folder.
            }
        }
    }
}
