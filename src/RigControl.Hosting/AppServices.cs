using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Hosting;

/// <summary>Everything the window, tray icon and "app" RPC module share.</summary>
public sealed record AppServices(
    AppEnvironment Environment,
    SettingsStore Settings,
    EventBus Events,
    RpcDispatcher Dispatcher,
    ModuleHost Host,
    FileLog Log)
{
    /// <summary>What this system offers (secrets, links, start at sign-in). The standard set unless a host swaps it.</summary>
    public IPlatformServices Platform { get; init; } = PlatformServices.Create(Environment.DataDirectory, Environment.IsElevated);
}

/// <summary>App-level preferences stored in settings.json under "app".</summary>
public sealed class AppSettings
{
    public const string Section = "app";

    /// <summary>Closing the window hides it to the tray instead of exiting (so software fan curves keep running).</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool CloseNoticeShown { get; set; }

    /// <summary>True once the "start with Windows as administrator" scheduled task was registered by RigControl.</summary>
    public bool AutostartRegistered { get; set; }

    /// <summary>"F" or "C": how temperatures are shown. Sensors, curves and devices always work in °C inside.</summary>
    public string TemperatureUnit { get; set; } = "F";
}
