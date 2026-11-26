using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RigControl.Core.Rpc;
using RigControl.Updates;

namespace RigControl.App.Updates;

/// <summary>Settings section "updates".</summary>
internal sealed class UpdateSettings
{
    public const string Section = "updates";

    /// <summary>Look for a new version a minute after start and every 6 hours.</summary>
    public bool AutoCheck { get; set; } = true;

    public DateTimeOffset? LastCheck { get; set; }

    /// <summary>The version the tray was last told about, so the same news is not shown again at every check.</summary>
    public string? LastNotified { get; set; }

    /// <summary>"public" (GitHub's latest release) or "test" (pre-releases too; only used for a maintainer account).</summary>
    public string Channel { get; set; } = UpdateChannels.Public;

    /// <summary>
    /// Set when the user chose "public" while running a test build: the next checks may then offer the public version
    /// although it is older ("Switch to the public version"). Cleared after an install or when "test" is chosen again.
    /// </summary>
    public bool SwitchBackPending { get; set; }
}

/// <summary>
/// The "updates" RPC module: checks GitHub's newest release (signed update information only; on the test channel, which
/// only a maintainer account gets, pre-releases count too), and on the installed copy
/// downloads it to &lt;install&gt;\updates, checks it, starts it with --update --after-pid and exits so it can replace this exe.
/// Events: "updates.status" whenever anything here changes.
/// </summary>
internal sealed class UpdatesModule : IRpcModule, IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly AppServices _services;
    private readonly App _app;
    private readonly UpdateChecker _checker;
    private readonly MaintainerCheck _maintainer;
    private readonly RigControl.Core.Logging.ILog _log;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private Timer? _timer;

    private UpdateOffer? _offer;
    private string? _latest;
    private string? _note;
    private string? _error;
    private bool _busyChecking;
    private string? _installStep;
    private double? _progress;

    public UpdatesModule(AppServices services, App app, GitHubServices github)
    {
        _services = services;
        _app = app;
        _checker = new UpdateChecker(github.Api);
        _maintainer = github.Maintainer;
        _log = services.Log.For("updates");
    }

    public string Name => "updates";

    /// <summary>Starts the timed checks (the first one after a minute, so start-up is never slowed down).</summary>
    public void Start()
    {
        _timer = new Timer(_ => _ = TimedCheckAsync(), null, FirstCheck, Every);
    }

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        switch (method)
        {
            case "getStatus":
                return Status();
            case "check":
                await CheckAsync(manual: true, ct).ConfigureAwait(false);
                return Status();
            case "setSettings":
                var a = RpcArgs.Require<SettingsArgs>(args);
                if (a.AutoCheck is { } on) _services.Settings.Update<UpdateSettings>(UpdateSettings.Section, s => s.AutoCheck = on);
                if (a.Channel is { } channel && UpdateChannels.Choose(_services.Settings, channel, _maintainer.IsMaintainer, Current))
                {
                    // What was found for the other channel no longer applies: look again for this one.
                    lock (_gate)
                    {
                        _offer = null;
                        _latest = null;
                        _note = null;
                        _error = null;
                    }
                    _log.Info("Update channel set to " + channel + ".");
                    _ = Task.Run(async () =>
                    {
                        try { await CheckAsync(manual: false, _life.Token).ConfigureAwait(false); }
                        catch (Exception ex) { _log.Warn("The update check failed: " + ex.Message); }
                    });
                }
                Publish();
                return Status();
            case "install":
                return await InstallAsync(ct).ConfigureAwait(false);
            default:
                throw RpcException.UnknownMethod(Name, method);
        }
    }

    /// <summary>This copy's version, pre-release part included ("1.1.3-rc.1" for a test build).</summary>
    private AppVersion Current => RunningVersion.Resolve(_services.Environment.Version);

    private UpdateChannel Channel => UpdateChannels.Effective(_services.Settings.Get<UpdateSettings>(UpdateSettings.Section), _maintainer.IsMaintainer);

    /// <summary>Null when this copy is not the installed one (then it never updates itself).</summary>
    private string? InstallFolder => InstalledCopy.InstallFolderFor(_services.Environment.ExecutablePath);

    private object Status()
    {
        var s = _services.Settings.Get<UpdateSettings>(UpdateSettings.Section);
        var maintainer = _maintainer.IsMaintainer;
        lock (_gate)
        {
            return new
            {
                current = Current.ToString(),
                autoCheck = s.AutoCheck,
                // The channel actually used: "test" only for a maintainer account that chose it.
                channel = UpdateChannels.Effective(s, maintainer) == UpdateChannel.Test ? UpdateChannels.Test : UpdateChannels.Public,
                // The choice is shown only to a GitHub account that may push to RigControl.
                testChannelAllowed = maintainer,
                switchBackPending = UpdateChannels.SwitchBack(s, maintainer),
                lastCheck = s.LastCheck?.ToString("o"),
                checking = _busyChecking,
                latest = _latest,
                note = _note,
                error = _error,
                available = _offer is null ? null : new
                {
                    version = _offer.Manifest.Version.ToString(),
                    size = _offer.Manifest.Size,
                    published = _offer.Manifest.Published.ToString("o"),
                    releaseUrl = _offer.ReleaseUrl,
                    notes = _offer.Notes,
                    // An OLDER public version, offered only right after leaving the test channel.
                    downgrade = _offer.IsDowngrade,
                    preRelease = _offer.Manifest.Version.IsPreRelease,
                },
                // Only the installed copy can update itself; any other copy only says that an update exists.
                canInstall = InstallFolder is not null,
                elevated = _services.Environment.IsElevated,
                installing = _installStep,
                progress = _progress,
            };
        }
    }

    private void Publish()
    {
        try { _services.Events.Publish("updates.status", Status()); }
        catch { /* the page may be closing */ }
    }

    private async Task TimedCheckAsync()
    {
        if (!_services.Settings.Get<UpdateSettings>(UpdateSettings.Section).AutoCheck) return;
        try { await CheckAsync(manual: false, _life.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn("The update check failed: " + ex.Message); }
    }

    private async Task CheckAsync(bool manual, CancellationToken ct)
    {
        if (!await _checking.WaitAsync(0, ct).ConfigureAwait(false))
        {
            if (!manual) return;
            await _checking.WaitAsync(ct).ConfigureAwait(false); // a check is running: wait for its answer
            _checking.Release();
            return;
        }
        try
        {
            lock (_gate) { _busyChecking = true; _error = null; }
            Publish();
            UpdateCheckResult result;
            var settings = _services.Settings.Get<UpdateSettings>(UpdateSettings.Section);
            var maintainer = _maintainer.IsMaintainer;
            var channel = UpdateChannels.Effective(settings, maintainer);
            var switchBack = UpdateChannels.SwitchBack(settings, maintainer);
            var current = Current;
            try
            {
                result = await _checker.CheckAsync(current, channel, switchBack, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GitHubException or UpdateException)
            {
                lock (_gate) _error = ex.Message;
                _log.Warn("Update check: " + ex.Message);
                if (manual) throw new RpcException(RpcErrorCodes.Unavailable, ex.Message, ex);
                return;
            }
            // Nothing to switch back from any more (the public version is this one or newer): the downgrade offer ends.
            var publicNotOlder = channel == UpdateChannel.Public && result.Offer is not { IsDowngrade: true }
                && AppVersion.TryParse(result.LatestVersion, out var latest) && latest >= current;
            _services.Settings.Update<UpdateSettings>(UpdateSettings.Section, s =>
            {
                s.LastCheck = DateTimeOffset.Now;
                if (publicNotOlder) s.SwitchBackPending = false;
            });
            lock (_gate)
            {
                _offer = result.Offer;
                _latest = result.LatestVersion;
                _note = result.Note;
            }
            // A switch back is the user's own doing, right in Settings: no tray news for it.
            if (result.Offer is { IsDowngrade: false } offer) Announce(offer);
        }
        finally
        {
            lock (_gate) _busyChecking = false;
            _checking.Release();
            Publish();
        }
    }

    /// <summary>Says once per version that an update is there (the page shows its own banner too).</summary>
    private void Announce(UpdateOffer offer)
    {
        var version = offer.Manifest.Version.ToString();
        var s = _services.Settings.Get<UpdateSettings>(UpdateSettings.Section);
        if (s.LastNotified == version) return;
        _services.Settings.Update<UpdateSettings>(UpdateSettings.Section, x => x.LastNotified = version);
        _log.Info("RigControl " + version + " is available.");
        var text = InstallFolder is not null
            ? "Open Settings > Updates to install it."
            : "Install RigControl with its setup to get updates automatically.";
        try { _app.Dispatcher.BeginInvoke(() => _app.ShowTrayNotice("RigControl " + version + " is available", text)); }
        catch { /* the tray may be gone */ }
    }

    private async Task<object> InstallAsync(CancellationToken ct)
    {
        UpdateOffer offer;
        lock (_gate)
        {
            if (_installStep is not null) throw RpcException.Unavailable("The update is already being installed.");
            offer = _offer ?? throw RpcException.Unavailable("There is no update to install. Check for updates first.");
        }
        // The offer was found earlier; the account or the channel may have changed since.
        var settings = _services.Settings.Get<UpdateSettings>(UpdateSettings.Section);
        var maintainer = _maintainer.IsMaintainer;
        if (offer.Manifest.Version.IsPreRelease && UpdateChannels.Effective(settings, maintainer) != UpdateChannel.Test)
            throw RpcException.Unavailable("Development builds are only installed while \"Get development builds\" is on. Check for updates again.");
        if (offer.IsDowngrade && !UpdateChannels.SwitchBack(settings, maintainer))
            throw RpcException.Unavailable("Going back to an older version is only offered right after turning development builds off. Check for updates again.");
        var install = InstallFolder
            ?? throw RpcException.Unavailable("This copy of RigControl isn't installed, so it can't update itself. Install RigControl with its setup to get updates automatically.");
        if (!_services.Environment.IsElevated)
            throw RpcException.NeedsAdmin("Installing an update needs RigControl to run as administrator. Restart it as administrator, then try again.");
        if (await RecordingAsync(ct).ConfigureAwait(false))
            throw RpcException.Unavailable("A recording is running. Stop it first, then install the update.");

        var folder = InstalledCopy.UpdatesFolder(install);
        try
        {
            SetStep("Downloading", 0);
            var path = await _checker.DownloadAsync(offer, folder, new Progress<double>(p => SetStep("Downloading", p)), ct).ConfigureAwait(false);

            SetStep("Checking", null);
            // The folder must be one only administrators can change, or the checked file could be swapped before it runs.
            if (RigControl.Bundled.FolderGuard.OthersCanChange(path))
                throw new UpdateException("The updates folder (" + folder + ") can be changed by other programs, so the update was not started.");
            if (!UpdateChecker.StillMatches(path, offer.Manifest))
                throw new UpdateException("The downloaded update changed after it was checked, so it was not started.");
            if (await RecordingAsync(ct).ConfigureAwait(false))
                throw new UpdateException("A recording started meanwhile. Stop it first, then install the update.");

            SetStep("Starting", null);
            var minimized = !_app.Dispatcher.Invoke(() => _app.Window is { IsVisible: true });
            var psi = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = folder };
            psi.ArgumentList.Add("--update");
            psi.ArgumentList.Add(App.AfterPidSwitch);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (minimized) psi.ArgumentList.Add("--minimized");
            Process.Start(psi)?.Dispose();
            _services.Settings.Update<UpdateSettings>(UpdateSettings.Section, s => s.SwitchBackPending = false);
            _log.Info(offer.IsDowngrade
                ? "Started the switch to the public version " + offer.Manifest.Version + " (older than " + Current + "; settings are kept); RigControl closes so it can be replaced."
                : "Started the update to " + offer.Manifest.Version + "; RigControl closes so it can be replaced.");
        }
        catch (Exception ex) when (ex is GitHubException or UpdateException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            SetStep(null, null, ex.Message);
            _log.Warn("Installing the update failed: " + ex.Message);
            throw new RpcException(RpcErrorCodes.Internal, ex.Message, ex);
        }
        catch
        {
            SetStep(null, null);
            throw;
        }

        // The update waits for this process to end (--after-pid), then replaces the exe and starts it again.
        _app.RequestExit();
        return new { started = true, version = offer.Manifest.Version.ToString() };
    }

    private void SetStep(string? step, double? progress, string? error = null)
    {
        lock (_gate)
        {
            // Progress arrives many times a second; only whole per cent steps are passed on.
            if (step == _installStep && progress is { } p && _progress is { } old && Math.Floor(p * 100) == Math.Floor(old * 100)) return;
            _installStep = step;
            _progress = progress;
            if (error is not null) _error = error;
        }
        Publish();
    }

    /// <summary>Asks the recording module whether it is recording. When it can't answer, nothing is being recorded.</summary>
    private async Task<bool> RecordingAsync(CancellationToken ct)
    {
        try
        {
            var answer = await _services.Dispatcher.HandleAsync("{\"id\":0,\"method\":\"capture.getStatus\"}", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(answer);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("recording", out var rec) && rec.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _life.Cancel(); } catch { /* closing */ }
        _timer?.Dispose();
    }

    private sealed class SettingsArgs
    {
        public bool? AutoCheck { get; set; }

        /// <summary>"public" or "test" (test only for a maintainer account).</summary>
        public string? Channel { get; set; }
    }
}
