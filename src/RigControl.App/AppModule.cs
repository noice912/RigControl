using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RigControl.Core.Rpc;

namespace RigControl.App;

/// <summary>
/// The "app" RPC module: information about the app itself, module health, app preferences,
/// restart-as-administrator, logs and exit. See docs/API.md.
/// </summary>
internal sealed class AppModule : IRpcModule
{
    private readonly AppServices _services;
    private readonly App _app;

    private readonly FirstRunService _firstRun;
    private readonly PcRestart _restart = new();
    private readonly FirstRunPc _pc;

    public AppModule(AppServices services, App app)
    {
        _services = services;
        _app = app;
        _pc = new FirstRunPc(services.Environment.ExecutablePath);
        _firstRun = new FirstRunService(services.Settings, services.Host, services.Environment.IsElevated, services.Log.For("setup"),
            () => _app.Bundled, () => _app.Dispatcher.Invoke(_app.AudioStatus), IsStartWithWindowsEnabled, SetStartWithWindows, pc: _pc);
    }

    public string Name => "app";

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        // The first-time setup waits for the modules and for installers, so it must not hold a thread while it does.
        switch (method)
        {
            case "getFirstRun":
                return await _firstRun.GetAsync(ct).ConfigureAwait(false);
            case "installHelpers":
                return await _firstRun.InstallHelpersAsync(ct).ConfigureAwait(false);
            case "completeFirstRun":
                return await _firstRun.CompleteAsync(RpcArgs.Optional<CompleteFirstRunArgs>(args).Autostart, ct).ConfigureAwait(false);
        }
        return Invoke(method, args);
    }

    private object? Invoke(string method, JsonElement? args)
    {
        object? result = method switch
        {
            "getInfo" => GetInfo(),
            "getModules" => _services.Host.Describe(),
            "getSettings" => GetSettings(),
            "setSettings" => SetSettings(RpcArgs.Require<AppSettingsDto>(args)),
            "restartAsAdmin" => RestartAsAdmin(),
            "openExternal" => OpenExternal(RpcArgs.Require<OpenExternalArgs>(args)),
            "openLogFolder" => OpenLogFolder(),
            "getLogs" => GetLogs(RpcArgs.Optional<GetLogsArgs>(args)),
            "quit" => Quit(),
            "setNativeView" => SetNativeView(RpcArgs.Optional<NativeViewArgs>(args)),
            "getAudioStatus" => GetAudioStatus(),
            "pickFile" => PickFile(RpcArgs.Optional<PickFileArgs>(args)),
            "pickFolder" => PickFolder(RpcArgs.Optional<PickFileArgs>(args)),
            "getBundled" => GetBundled(),
            "uninstallOldApp" => UninstallOldApp(RpcArgs.Require<OldAppArgs>(args)),
            "restartPc" => RestartPc(RpcArgs.Optional<RestartPcArgs>(args)),
            "cancelRestartPc" => CancelRestartPc(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return result;
    }

    /// <summary>The helper programs RigControl needs, and the old apps it replaces (which can be uninstalled).</summary>
    private object GetBundled() => new
    {
        status = _app.Bundled?.Status(),
        // Where the helper programs stand: one entry per program, whether the yes exists, what is still missing.
        helpers = _app.Bundled?.Summary(),
        oldApps = RigControl.Bundled.OldApps.List(),
        elevated = _services.Environment.IsElevated,
    };

    /// <summary>
    /// Restarts the PC after a 30-second countdown that Windows shows too. The page asks "Restart your PC now?"
    /// first; nothing else in RigControl ever calls this. While other Windows users are signed in it only goes
    /// ahead when the page says the user was told so and still said yes ("evenWithOtherUsers").
    /// </summary>
    private object RestartPc(RestartPcArgs a)
    {
        try
        {
            var others = _firstRun.OtherUsersSignedIn();
            var seconds = _restart.Schedule(others, a.EvenWithOtherUsers == true);
            _services.Log.Info(others > 0
                ? $"The user asked to restart the PC although {others} other Windows user(s) are signed in: Windows restarts in {seconds} seconds unless it is cancelled."
                : $"The user asked to restart the PC: Windows restarts in {seconds} seconds unless it is cancelled.");
            return new { scheduled = true, seconds };
        }
        catch (OtherUsersSignedInException ex)
        {
            throw RpcException.Unsafe(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new RpcException(RpcErrorCodes.Internal, "Windows would not start the restart: " + ex.Message, ex);
        }
    }

    private object CancelRestartPc()
    {
        try
        {
            var cancelled = _restart.Cancel();
            _services.Log.Info(cancelled ? "The restart was cancelled." : "There was no restart to cancel.");
            return new { cancelled };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new RpcException(RpcErrorCodes.Internal, "Windows would not cancel the restart: " + ex.Message + " You can also run \"shutdown /a\" yourself.", ex);
        }
    }

    private sealed class RestartPcArgs
    {
        public bool? EvenWithOtherUsers { get; set; }
    }

    private sealed class CompleteFirstRunArgs
    {
        public bool? Autostart { get; set; }
    }

    private object UninstallOldApp(OldAppArgs a)
    {
        if (!_services.Environment.IsElevated) throw RpcException.NeedsAdmin("Uninstalling programs needs administrator rights.");
        try
        {
            var name = RigControl.Bundled.OldApps.Uninstall(a.Id ?? "");
            _services.Log.Info($"Started {name}'s own uninstaller at the user's request.");
            return new { started = name };
        }
        catch (ArgumentException ex)
        {
            throw RpcException.InvalidArgs(ex.Message);
        }
    }

    private sealed class OldAppArgs
    {
        public string? Id { get; set; }
    }

    private object SetNativeView(NativeViewArgs a)
    {
        if (a.View is not null && !string.Equals(a.View, "audio", StringComparison.OrdinalIgnoreCase))
            throw RpcException.InvalidArgs($"Unknown view '{a.View}'.");
        var shown = _app.Dispatcher.Invoke(() =>
        {
            var window = _app.Window ?? throw RpcException.Unavailable("The window is not ready yet.");
            return window.SetNativeView(a.View, _app.AudioView);
        });
        return new { view = shown };
    }

    private object GetAudioStatus()
    {
        var (state, message) = _app.Dispatcher.Invoke(_app.AudioStatus);
        return new { state, message };
    }

    private object PickFolder(PickFileArgs a)
    {
        var path = _app.Dispatcher.Invoke(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = string.IsNullOrWhiteSpace(a.Title) ? "Choose a folder" : a.Title,
                Multiselect = false,
            };
            var ok = _app.Window is { IsVisible: true } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return ok == true ? dialog.FolderName : null;
        });
        return new { path };
    }

    private object PickFile(PickFileArgs a)
    {
        var path = _app.Dispatcher.Invoke(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(a.Title) ? "Choose a file" : a.Title,
                Filter = string.IsNullOrWhiteSpace(a.Filter) ? "All files (*.*)|*.*" : a.Filter,
                CheckFileExists = true,
                Multiselect = false,
            };
            bool? ok;
            try
            {
                ok = _app.Window is { IsVisible: true } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            }
            catch (InvalidOperationException ex)
            {
                // A malformed filter string from the UI.
                throw RpcException.InvalidArgs("The file filter is not valid: " + ex.Message);
            }
            return ok == true ? dialog.FileName : null;
        });
        return new { path };
    }

    private object GetInfo()
    {
        // The same answer as always, plus which system this is and what works on it (docs/API.md).
        return AppInfo.From(_services.Environment, RigControl.Core.Platform.HostPlatform.Windows, _services.Host.Modules,
            HostCapabilities.WindowsHost);
    }

    private AppSettingsDto GetSettings()
    {
        var s = _services.Settings.Get<AppSettings>(AppSettings.Section);
        return new AppSettingsDto
        {
            MinimizeToTray = s.MinimizeToTray,
            StartMinimized = s.StartMinimized,
            StartWithWindows = IsStartWithWindowsEnabled(),
            TemperatureUnit = s.TemperatureUnit == "C" ? "C" : "F",
        };
    }

    private AppSettingsDto SetSettings(AppSettingsDto dto)
    {
        _services.Settings.Update<AppSettings>(AppSettings.Section, s =>
        {
            if (dto.MinimizeToTray is { } m) s.MinimizeToTray = m;
            if (dto.StartMinimized is { } sm) s.StartMinimized = sm;
            if (dto.TemperatureUnit is { } u)
            {
                if (u is not ("F" or "C")) throw RpcException.InvalidArgs("Temperature unit must be F or C.");
                s.TemperatureUnit = u;
            }
        });
        if (dto.TemperatureUnit is not null) _app.ApplyTemperatureUnit(dto.TemperatureUnit);
        if (dto.StartWithWindows is { } sw) SetStartWithWindows(sw);
        return GetSettings();
    }

    /// <summary>
    /// "Start with Windows" means: a scheduled task starts RigControl elevated at sign-in (see <see cref="Autostart"/>).
    /// When Windows will not tell a non-elevated process about the task, fall back to what RigControl recorded.
    /// </summary>
    private bool IsStartWithWindowsEnabled()
    {
        try
        {
            var state = Autostart.Query();
            if (state.Known) return state.Enabled;
        }
        catch
        {
            // fall through to the recorded value
        }
        return _services.Settings.Get<AppSettings>(AppSettings.Section).AutostartRegistered;
    }

    private void SetStartWithWindows(bool enabled)
    {
        var exe = _services.Environment.ExecutablePath;
        if (string.IsNullOrEmpty(exe))
            throw RpcException.Unavailable("The program path is unknown, so the startup task cannot be created.");

        string? error;
        if (_services.Environment.IsElevated)
        {
            error = enabled ? Autostart.Install(exe, Autostart.CurrentUserSid()) : Autostart.Remove(Autostart.CurrentUserSid());
        }
        else
        {
            // One UAC prompt: a short-lived elevated copy of RigControl adds or removes the task.
            error = Autostart.RunElevatedHelper(exe, enabled);
            if (error == "cancelled")
                throw new RpcException(RpcErrorCodes.Cancelled, "Administrator permission was not granted, so nothing changed.");
        }
        if (error is not null)
            throw new RpcException(RpcErrorCodes.Internal, "Could not change the start-with-Windows setting: " + error);

        _services.Settings.Update<AppSettings>(AppSettings.Section, s => s.AutostartRegistered = enabled);
        var log = _services.Log.For("autostart");
        log.Info(enabled ? "Start with Windows (as administrator) turned on." : "Start with Windows turned off.");
        if (enabled && ProgramFolderOpen()) log.Warn(Autostart.OpenFolderWarning);
    }

    private bool ProgramFolderOpen()
    {
        try { return _pc.ProgramFolderOpen(); }
        catch { return false; }
    }

    private object RestartAsAdmin()
    {
        if (_services.Environment.IsElevated)
            return new { restarting = false, alreadyElevated = true };

        var exe = _services.Environment.ExecutablePath;
        if (string.IsNullOrEmpty(exe))
            throw RpcException.Unavailable("The program path is unknown, so it cannot restart itself.");

        // Free the single-instance lock first, otherwise the elevated copy would see us and quit. The new copy also
        // waits for this process to exit (--after-pid) before it takes over the audio engine and the hardware.
        _app.ReleaseSingleInstance();
        try
        {
            var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = App.AfterPidSwitch + " " + pid,
            };
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user declined the UAC prompt
        {
            _app.ReacquireSingleInstance();
            throw new RpcException(RpcErrorCodes.Cancelled, "Administrator permission was not granted.", ex);
        }
        catch (Exception ex)
        {
            _app.ReacquireSingleInstance();
            throw new RpcException(RpcErrorCodes.Internal, "Could not restart as administrator: " + ex.Message, ex);
        }

        _app.RequestExit();
        return new { restarting = true, alreadyElevated = false };
    }

    private object OpenExternal(OpenExternalArgs a)
    {
        bool opened;
        try
        {
            opened = ShellActions.OpenHttpsUrl(a.Url);
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
        ShellActions.OpenFolder(_services.Environment.LogDirectory);
        return new { opened = true };
    }

    private object GetLogs(GetLogsArgs a)
    {
        var max = Math.Clamp(a.Max ?? 200, 1, 500);
        var entries = _services.Log.Recent();
        return entries.Skip(Math.Max(0, entries.Count - max))
            .Select(e => new LogEntryDto(e.Time.ToString("o"), e.Level.ToString().ToLowerInvariant(), e.Source, e.Message))
            .ToArray();
    }

    private object Quit()
    {
        _app.RequestExit();
        return new { quitting = true };
    }

    private sealed record LogEntryDto(string Time, string Level, string Source, string Message);

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

    private sealed class NativeViewArgs
    {
        public string? View { get; set; }
    }

    private sealed class PickFileArgs
    {
        public string? Title { get; set; }
        public string? Filter { get; set; }
    }
}
