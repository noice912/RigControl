namespace RigControl.Core.Platform;

/// <summary>
/// Keeps small secrets (a light's pairing key, a token) unreadable in the settings file.
/// Windows: the user's own Windows protection (DPAPI). Linux and macOS: a key kept in a file only the user can read.
/// </summary>
public interface ISecretProtector
{
    /// <summary>One plain sentence saying how secrets are kept here; fit for the privacy note and the log.</summary>
    string Description { get; }

    /// <summary>Protects <paramref name="data"/>. The same <paramref name="purpose"/> must be given to read it back.</summary>
    byte[] Protect(byte[] data, byte[] purpose);

    /// <summary>Reads back what <see cref="Protect"/> made. Throws <see cref="System.Security.Cryptography.CryptographicException"/> when it cannot.</summary>
    byte[] Unprotect(byte[] protectedData, byte[] purpose);
}

/// <summary>A running program, as far as RigControl needs to know it.</summary>
public sealed record ProcessEntry(int Id, string Name);

/// <summary>Which programs are running. Only ever the current user's own programs.</summary>
public interface IProcessQuery
{
    /// <summary>
    /// The programs the current user is running (Windows: in this sign-in session). Names have no ".exe".
    /// Never throws; returns an empty list when the system will not say.
    /// </summary>
    IReadOnlyList<ProcessEntry> OwnProcesses();

    /// <summary>True when the current user runs a program with this name (no ".exe"; upper and lower case are the same).</summary>
    bool IsRunning(string processName);
}

/// <summary>Starting RigControl when the user signs in.</summary>
public interface IAutostart
{
    /// <summary>False where this build has no way to do it; the UI then hides the setting.</summary>
    bool Supported { get; }

    bool IsEnabled();

    /// <summary>Turns it on or off. Returns null when done, or a sentence saying what went wrong.</summary>
    string? SetEnabled(bool enabled, string executablePath);
}

/// <summary>Opening things in the user's own programs.</summary>
public interface ILinkOpener
{
    /// <summary>Opens an https link in the default browser. Anything that is not https is refused (false).</summary>
    bool OpenHttpsUrl(string? url);

    /// <summary>Shows a folder in the system's file manager. Does nothing when the folder does not exist.</summary>
    void OpenFolder(string path);
}

/// <summary>The few things RigControl asks of the system it runs on, in one place.</summary>
public interface IPlatformServices
{
    HostPlatform Platform { get; }

    /// <summary>Windows: running as administrator. Linux and macOS: running as root (RigControl never asks for that).</summary>
    bool IsElevated { get; }

    ISecretProtector Secrets { get; }
    IProcessQuery Processes { get; }
    IAutostart Autostart { get; }
    ILinkOpener Links { get; }
}

/// <summary>
/// The standard set for the system this process runs on. Hosts may swap single parts
/// (<c>PlatformServices.Create(dir) with { Autostart = ... }</c>); tests pass fakes.
/// </summary>
public sealed record PlatformServices : IPlatformServices
{
    public required HostPlatform Platform { get; init; }
    public required bool IsElevated { get; init; }
    public required ISecretProtector Secrets { get; init; }
    public required IProcessQuery Processes { get; init; }
    public required IAutostart Autostart { get; init; }
    public required ILinkOpener Links { get; init; }

    /// <summary>
    /// Builds the standard set. <paramref name="dataDirectory"/> is RigControl's own per-user folder; on Linux and
    /// macOS the key that protects secrets is kept in it. Nothing is created or read until a part is first used.
    /// </summary>
    public static PlatformServices Create(string dataDirectory)
        => Create(dataDirectory, Modules.AppEnvironment.DetectElevated());

    /// <summary>The same, for a caller that already knows whether the process is elevated.</summary>
    public static PlatformServices Create(string dataDirectory, bool isElevated)
    {
        ISecretProtector secrets = OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new FileKeySecretProtector(dataDirectory);
        return new PlatformServices
        {
            Platform = PlatformInfo.Current,
            IsElevated = isElevated,
            Secrets = secrets,
            Processes = new OwnProcessQuery(),
            Autostart = UnixAutostart.ForThisSystem(),
            Links = new SystemLinkOpener(),
        };
    }
}
