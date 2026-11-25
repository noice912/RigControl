namespace RigControl.Core.Platform;

/// <summary>The kind of system RigControl is running on.</summary>
public enum HostPlatform
{
    Windows,
    Linux,
    MacOS,
    /// <summary>Anything else .NET runs on. Nothing that touches the system is offered there.</summary>
    Other,
}

public static class PlatformInfo
{
    /// <summary>The system this process runs on.</summary>
    public static HostPlatform Current { get; } =
        OperatingSystem.IsWindows() ? HostPlatform.Windows :
        OperatingSystem.IsLinux() ? HostPlatform.Linux :
        OperatingSystem.IsMacOS() ? HostPlatform.MacOS :
        HostPlatform.Other;

    /// <summary>The short name sent to the UI in app.getInfo: "windows", "linux", "macos" or "other".</summary>
    public static string Id(HostPlatform platform) => platform switch
    {
        HostPlatform.Windows => "windows",
        HostPlatform.Linux => "linux",
        HostPlatform.MacOS => "macos",
        _ => "other",
    };

    /// <summary>The name to use in a sentence, e.g. "Recording works on Windows only."</summary>
    public static string DisplayName(HostPlatform platform) => platform switch
    {
        HostPlatform.Windows => "Windows",
        HostPlatform.Linux => "Linux",
        HostPlatform.MacOS => "macOS",
        _ => "this system",
    };
}
