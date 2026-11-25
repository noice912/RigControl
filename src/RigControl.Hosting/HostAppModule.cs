using System.Text.Json;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Hosting;

/// <summary>What the "app" RPC module asks of the window it lives in. The Linux/macOS host implements it.</summary>
public interface IHostShell
{
    /// <summary>What this host can do besides its modules (names from <see cref="HostCapabilities"/>).</summary>
    IReadOnlyCollection<string> Features { get; }

    /// <summary>Closes RigControl in order (modules first). Returns at once.</summary>
    void RequestExit();
}

/// <summary>
/// The "app" RPC module for hosts other than the Windows app: information about the app, module health, preferences,
/// logs, opening links, exit. Methods that only make sense on Windows (restart as administrator, the helper
/// programs, the first-time setup, the Audio section) answer "Not available on this system".
/// The Windows app has its own, larger "app" module (RigControl.App/AppModule.cs); the method names and the shapes of
/// the answers are the same so the UI needs no second code path. See docs/API.md.
/// </summary>
public sealed class HostAppModule : IRpcModule
{
    private readonly AppServices _services;
    private readonly IHostShell _shell;

    public HostAppModule(AppServices services, IHostShell shell)
    {
        _services = services;
        _shell = shell;
    }

    public string Name => "app";

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getInfo" => GetInfo(),
            "getModules" => _services.Host.Describe(),
            "getSettings" => GetSettings(),
            "setSettings" => SetSettings(RpcArgs.Require<AppSettingsDto>(args)),
            "openExternal" => OpenExternal(RpcArgs.Require<OpenExternalArgs>(args)),
            "openLogFolder" => OpenLogFolder(),
            "getLogs" => AppLogs.Recent(_services, RpcArgs.Optional<GetLogsArgs>(args).Max),
            "quit" => Quit(),
            // The UI tells the host on every page change which native view to show; here there is none.
            "setNativeView" => new { view = (string?)null },
            "getAudioStatus" => new { state = "unavailable", message = ModuleAvailability.Message("the Audio section works on Windows only.") },
            "restartAsAdmin" or "pickFile" or "pickFolder" or "getBundled" or "uninstallOldApp" or "restartPc" or "cancelRestartPc"
                or "getFirstRun" or "installHelpers" or "completeFirstRun"
                => throw ModuleAvailability.Error(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult<object?>(result);
    }

    private AppInfo GetInfo()
        => AppInfo.From(_services.Environment, _services.Platform.Platform, _services.Host.Modules, _shell.Features);

    private AppSettingsDto GetSettings()
    {
        var s = _services.Settings.Get<AppSettings>(AppSettings.Section);
        var autostart = _services.Platform.Autostart;
        return new AppSettingsDto
        {
            // No tray here: closing the window always closes RigControl, whatever the stored value says.
            MinimizeToTray = false,
            StartMinimized = false,
            StartWithWindows = autostart.Supported && SafeIsEnabled(),
            TemperatureUnit = s.TemperatureUnit == "C" ? "C" : "F",
        };
    }

    private bool SafeIsEnabled()
    {
        try { return _services.Platform.Autostart.IsEnabled(); }
        catch { return false; }
    }

    private AppSettingsDto SetSettings(AppSettingsDto dto)
    {
        if (dto.TemperatureUnit is { } u)
        {
            if (u is not ("F" or "C")) throw RpcException.InvalidArgs("Temperature unit must be F or C.");
            _services.Settings.Update<AppSettings>(AppSettings.Section, s => s.TemperatureUnit = u);
        }
        if (dto.StartWithWindows is { } start)
        {
            var autostart = _services.Platform.Autostart;
            if (!autostart.Supported) throw ModuleAvailability.Error("starting at sign-in cannot be set up here.");
            var error = autostart.SetEnabled(start, _services.Environment.ExecutablePath);
            if (error is not null) throw new RpcException(RpcErrorCodes.Internal, error);
            _services.Log.For("autostart").Info(start ? "Start at sign-in turned on." : "Start at sign-in turned off.");
        }
        return GetSettings();
    }

    private object OpenExternal(OpenExternalArgs a)
    {
        bool opened;
        try
        {
            opened = _services.Platform.Links.OpenHttpsUrl(a.Url);
        }
        catch (Exception ex)
        {
            throw new RpcException(RpcErrorCodes.Internal, "The link could not be opened: " + ex.Message, ex);
        }
        if (!opened) throw RpcException.InvalidArgs("Only https links can be opened.");
        return new { opened = true };
    }

    private object OpenLogFolder()
    {
        try
        {
            _services.Platform.Links.OpenFolder(_services.Environment.LogDirectory);
        }
        catch (Exception ex)
        {
            throw new RpcException(RpcErrorCodes.Internal,
                "The log folder could not be opened: " + ex.Message + " It is " + _services.Environment.LogDirectory, ex);
        }
        return new { opened = true };
    }

    private object Quit()
    {
        _shell.RequestExit();
        return new { quitting = true };
    }

    private sealed class AppSettingsDto
    {
        public bool? MinimizeToTray { get; set; }
        public bool? StartMinimized { get; set; }
        public bool? StartWithWindows { get; set; }
        public string? TemperatureUnit { get; set; }
    }

    private sealed class OpenExternalArgs
    {
        public string? Url { get; set; }
    }

    private sealed class GetLogsArgs
    {
        public int? Max { get; set; }
    }
}

/// <summary>The recent log lines in the shape app.getLogs answers with.</summary>
public static class AppLogs
{
    public sealed record Line(string Time, string Level, string Source, string Message);

    public static Line[] Recent(AppServices services, int? max)
    {
        var take = Math.Clamp(max ?? 200, 1, 500);
        var entries = services.Log.Recent();
        return entries.Skip(Math.Max(0, entries.Count - take))
            .Select(e => new Line(e.Time.ToString("o"), e.Level.ToString().ToLowerInvariant(), e.Source, e.Message))
            .ToArray();
    }
}
