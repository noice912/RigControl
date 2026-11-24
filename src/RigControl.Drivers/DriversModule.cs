using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Drivers;

public sealed record DriversStatusDto(
    string? Board, string? CheckedAt, bool Checking, IReadOnlyList<DriverRowDto> Rows, string? AsusProblem,
    IReadOnlyList<WindowsUpdateDriver>? WindowsUpdate, string? WindowsUpdateProblem, string? Busy, bool Elevated);

public sealed record DriverDownloadDto(string Name, double ReceivedMB, double? TotalMB, int? Percent);

public sealed record DriverActionDto(bool Started, string Message, bool RestartNeeded, DriversStatusDto Status);

/// <summary>
/// Device driver updates for this PC (what Armoury Crate's driver page did): ASUS's own downloads for the
/// motherboard's parts, compared with what Windows has installed, plus the driver updates Windows Update offers.
///
/// Rules enforced here:
///  - Nothing is downloaded or installed except when the user clicks.
///  - ASUS packages come only from ASUS's download servers over https, must match the checksum ASUS publishes, and
///    their installer must carry a signature Windows trusts from ASUS, Intel or Realtek; otherwise nothing is run.
///  - Windows Update drivers that ask for licence terms are left for the user to accept in Windows Update.
///  - The graphics card driver is handled on the Graphics page (NVIDIA), not here.
/// </summary>
public sealed class DriversModule : IAppModule
{
    private readonly IDriverSources _sources;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;

    // Guarded by _gate.
    private string? _board;
    private bool _isAsus;
    private DateTimeOffset? _checkedAt;
    private bool _checking;
    private IReadOnlyList<AsusFile> _asus = Array.Empty<AsusFile>();
    private IReadOnlyList<DriverRowDto> _rows = Array.Empty<DriverRowDto>();
    private string? _asusProblem;
    private IReadOnlyList<WindowsUpdateDriver>? _wu;
    private string? _wuProblem;
    private string? _busy;

    public DriversModule() : this(new WindowsDriverSources()) { }

    public DriversModule(IDriverSources sources) => _sources = sources;

    public string Name => "drivers";
    public string DisplayName => "Device drivers";
    public ModuleStatus Status => _status;

    private bool Elevated => _ctx?.Environment.IsElevated ?? false;
    private string DriverFolder => Path.Combine(_ctx?.Environment.DataDirectory ?? Path.GetTempPath(), "drivers");

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        var (maker, model) = _sources.Board();
        lock (_gate)
        {
            _board = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            _isAsus = maker?.Contains("ASUS", StringComparison.OrdinalIgnoreCase) == true;
        }
        // Nothing is looked up until the user opens the page and checks.
        _status = new ModuleStatus(ModuleState.Ready);
        return Task.CompletedTask;
    }

    private DriversStatusDto BuildStatus()
        => new(_board, _checkedAt?.ToString("o"), _checking, _rows, _asusProblem, _wu, _wuProblem, _busy, Elevated);

    private void Publish()
    {
        DriversStatusDto dto;
        lock (_gate) dto = BuildStatus();
        _ctx?.Events.Publish("drivers.status", dto);
    }

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        return method switch
        {
            "getStatus" => GetStatus(),
            "check" => await CheckAsync(ct).ConfigureAwait(false),
            "installAsus" => await InstallAsusAsync(RpcArgs.Require<FileArgs>(args), ct).ConfigureAwait(false),
            "installWindowsUpdate" => await InstallWindowsUpdateAsync(RpcArgs.Require<IdsArgs>(args), ct).ConfigureAwait(false),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
    }

    private DriversStatusDto GetStatus()
    {
        lock (_gate) return BuildStatus();
    }

    // ---------------------------------------------------------------- check

    private async Task<DriversStatusDto> CheckAsync(CancellationToken ct)
    {
        string? board;
        bool isAsus;
        lock (_gate)
        {
            if (_checking) return BuildStatus();
            _checking = true;
            board = _board;
            isAsus = _isAsus;
        }
        Publish();
        try
        {
            // ASUS's list and Windows Update are asked at the same time; Windows Update is the slow one (up to a minute).
            var asusTask = Task.Run<(IReadOnlyList<AsusFile> Files, string? Problem)>(async () =>
            {
                if (board is null || !isAsus) return (Array.Empty<AsusFile>(), "This motherboard is not an ASUS board, so there is no ASUS driver list for it.");
                try
                {
                    var files = await _sources.GetAsusAsync(board, ct).ConfigureAwait(false);
                    return (files, files.Count == 0 ? "ASUS lists no drivers for this board." : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("ASUS driver list could not be fetched.", ex);
                    return (Array.Empty<AsusFile>(), "ASUS's support site did not answer. Try again later.");
                }
            }, ct);
            var wuTask = Task.Run(async () =>
            {
                try
                {
                    return (List: (IReadOnlyList<WindowsUpdateDriver>?)await _sources.SearchWindowsUpdateAsync(ct).ConfigureAwait(false), Problem: (string?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("Windows Update driver search failed.", ex);
                    return (null, "Windows Update did not answer: " + ex.Message);
                }
            }, ct);

            IReadOnlyList<InstalledDriver> installed;
            try
            {
                installed = await Task.Run(_sources.GetInstalled, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("Installed drivers could not be read.", ex);
                installed = Array.Empty<InstalledDriver>();
            }
            var asus = await asusTask.ConfigureAwait(false);
            var wu = await wuTask.ConfigureAwait(false);

            lock (_gate)
            {
                _asus = asus.Files;
                _asusProblem = asus.Problem;
                _rows = DriverPlan.Build(asus.Files, installed);
                _wu = wu.List;
                _wuProblem = wu.Problem;
                _checkedAt = DateTimeOffset.Now;
            }
            var updates = _rows.Count(r => r.State == "update") + (wu.List?.Count ?? 0);
            _log.Info($"Drivers checked: {updates} update(s) available ({string.Join(", ", _rows.Select(r => $"{r.Key} {r.State}"))}; Windows Update {wu.List?.Count.ToString() ?? "unavailable"}).");
        }
        finally
        {
            lock (_gate) _checking = false;
            Publish();
        }
        return GetStatus();
    }

    // ---------------------------------------------------------------- ASUS package

    private void RequireAdmin()
    {
        if (!Elevated) throw RpcException.NeedsAdmin("Installing drivers needs administrator rights. Restart RigControl as administrator, then try again.");
    }

    private void Claim(string what)
    {
        lock (_gate)
        {
            if (_busy is not null) throw RpcException.Unavailable($"Already busy: {_busy}. Wait for it to finish.");
            _busy = what;
        }
        Publish();
    }

    private void Release()
    {
        lock (_gate) _busy = null;
        Publish();
    }

    private async Task<DriverActionDto> InstallAsusAsync(FileArgs a, CancellationToken ct)
    {
        RequireAdmin();
        AsusFile file;
        lock (_gate)
            file = _asus.FirstOrDefault(f => f.Id == a.FileId) ?? throw RpcException.InvalidArgs("That download is not in the current list. Check for updates again.");
        if (!AsusDownloads.IsAllowedUrl(file.Url)) throw RpcException.Unsafe("That download is not on ASUS's servers.");

        Claim(file.Title.Trim());
        try
        {
            var fileName = Path.GetFileName(new Uri(file.Url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw RpcException.Unsafe("The download has an unusable file name.");
            var package = Path.Combine(DriverFolder, "asus", fileName);

            if (!File.Exists(package) || (file.Sha256 is not null && WindowsDriverSources.Sha256(package) != file.Sha256))
            {
                _log.Info($"Downloading {fileName} from ASUS.");
                try
                {
                    await _sources.DownloadAsync(file.Url, package, (received, total) =>
                        _ctx?.Events.Publish("drivers.download", new DriverDownloadDto(file.Title.Trim(),
                            Math.Round(received / 1048576.0, 1), total is null ? null : Math.Round(total.Value / 1048576.0, 1),
                            total is > 0 ? (int)(received * 100 / total.Value) : null)), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("ASUS driver download failed.", ex);
                    throw RpcException.Unavailable("The download failed: " + ex.Message);
                }
            }
            if (file.Sha256 is not null && WindowsDriverSources.Sha256(package) != file.Sha256)
            {
                TryDelete(package);
                _log.Error($"{fileName} does not match the checksum ASUS publishes; it was deleted.");
                throw RpcException.Unsafe("The downloaded file does not match what ASUS published, so it was deleted and not run.");
            }

            var installer = package;
            if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var folder = Path.Combine(DriverFolder, "asus", Path.GetFileNameWithoutExtension(fileName));
                try
                {
                    WindowsDriverSources.Unpack(package, folder);
                }
                catch (Exception ex)
                {
                    throw RpcException.Unavailable("The package could not be unpacked: " + ex.Message);
                }
                installer = AsusDownloads.FindInstaller(folder)
                            ?? throw RpcException.Unavailable($"RigControl could not tell which installer in the package to run. The files are in {folder}.");
            }
            else if (!package.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.Unavailable($"This download is not an installer RigControl can start. It was saved to {package}.");
            }

            var signer = _sources.TrustedSigner(installer);
            if (signer is null || !AsusDownloads.TrustedPublishers.Any(p => signer.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Error($"Not starting {installer}: signature {(signer ?? "missing or not trusted")}.");
                throw RpcException.Unsafe("The installer is not signed by ASUS, Intel or Realtek, so RigControl did not start it.");
            }
            try
            {
                _sources.Start(installer);
            }
            catch (Exception ex)
            {
                throw RpcException.Unavailable("The installer could not be started: " + ex.Message);
            }
            _log.Info($"Started {Path.GetFileName(installer)} from {fileName} (signed by {signer}).");
        }
        finally
        {
            Release();
        }
        return new DriverActionDto(true, "The installer has started. Follow its steps; Windows may ask to restart.", false, GetStatus());
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------- Windows Update

    private async Task<DriverActionDto> InstallWindowsUpdateAsync(IdsArgs a, CancellationToken ct)
    {
        RequireAdmin();
        var ids = (a.Ids ?? new List<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        lock (_gate)
        {
            if (ids.Count == 0) throw RpcException.InvalidArgs("Choose at least one update.");
            if (_wu is null || ids.Any(i => _wu.All(u => u.Id != i))) throw RpcException.InvalidArgs("Those updates are not in the current list. Check for updates again.");
        }
        Claim("Installing from Windows Update");
        (int Installed, bool RestartNeeded, string? Problem) result;
        try
        {
            _log.Info($"Installing {ids.Count} driver update(s) from Windows Update.");
            try
            {
                result = await _sources.InstallWindowsUpdateAsync(ids, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("Windows Update driver install failed.", ex);
                throw RpcException.Unavailable("Windows Update could not install them: " + ex.Message);
            }
            _log.Info($"Windows Update installed {result.Installed} driver(s); restart needed: {result.RestartNeeded}. {result.Problem}");
        }
        finally
        {
            Release();
        }
        // Refresh the list so installed ones disappear.
        var status = await CheckAsync(ct).ConfigureAwait(false);
        var message = $"{result.Installed} driver update(s) installed." + (result.RestartNeeded ? " Restart Windows to finish." : "") + (result.Problem is null ? "" : " " + result.Problem);
        return new DriverActionDto(result.Installed > 0, message, result.RestartNeeded, status);
    }

    public ValueTask DisposeAsync()
    {
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    private sealed class FileArgs
    {
        public string? FileId { get; set; }
    }

    private sealed class IdsArgs
    {
        public List<string>? Ids { get; set; }
    }
}
