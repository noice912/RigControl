using System.Text.Json;
using RigControl.Core.Conflicts;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Health;

/// <summary>How many findings there are of each severity.</summary>
public sealed record HealthCountsDto(int Blocks, int Warn, int Info);

/// <summary>
/// The Windows settings and facts behind the findings, for pages that want to show or hide things.
/// A value is null when it could not be read.
/// </summary>
public sealed record HealthSystemDto(
    bool Elevated,
    bool? FastStartup,
    bool? MemoryIntegrity,
    bool? DriverBlocklist,
    bool? ControlledFolderAccess,
    bool? PendingRestart,
    bool? GameBarRecording,
    bool? GameBarBackgroundRecording,
    bool? GameBarLogoButton,
    bool? GameMode,
    bool? DynamicLighting,
    bool? WindowsUpdateDrivers,
    int SessionCount,
    bool OtherSessionRunsRigControl);

/// <summary>What "health.getReport" returns and what the "health.report" event carries.</summary>
public sealed record HealthReportDto(
    DateTimeOffset ScannedAt,
    IReadOnlyList<ConflictFinding> Findings,
    HealthCountsDto Counts,
    HealthSystemDto System);

/// <summary>
/// Looks for other programs and Windows settings that get in RigControl's way, and explains them.
/// It only looks: it never closes, changes or installs anything. A finding may name a button the app
/// already has ("fix"); pressing it is always the user's choice.
/// </summary>
public sealed class HealthModule : IAppModule
{
    public const string ReportTopic = "health.report";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    private readonly IConflictProbe _probe;
    private readonly ConflictScanner _scanner;
    private readonly bool _runLoop;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _scanGate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private volatile HealthReportDto? _report;
    private string? _lastSignature;      // guarded by _scanGate
    private string? _lastError;          // guarded by _scanGate
    private Timer? _timer;
    private volatile bool _disposed;

    public HealthModule() : this(CreateProbe())
    {
    }

    /// <param name="runLoop">False in tests, which call <see cref="ScanNow"/> themselves.</param>
    public HealthModule(IConflictProbe probe, IReadOnlyList<ConflictEntry>? catalog = null, bool runLoop = true,
        Func<DateTimeOffset>? now = null)
    {
        _probe = probe;
        _scanner = new ConflictScanner(catalog);
        _runLoop = runLoop;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    private static IConflictProbe CreateProbe()
        => OperatingSystem.IsWindows() ? new WindowsConflictProbe() : new NothingToCheckProbe();

    /// <summary>
    /// Set by the app: true when RigControl starts with Windows as administrator from a folder that programs without
    /// administrator rights can change. Read at every check; it only ever reads.
    /// </summary>
    public Func<bool>? StartsAsAdminFromOpenFolder { get; init; }

    public string Name => "health";
    public string DisplayName => "Health";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        _status = new ModuleStatus(ModuleState.Ready);
        // The first check runs straight away on a background thread, so starting the app never waits for it.
        if (_runLoop) _timer = new Timer(_ => ScanSafe(), null, TimeSpan.Zero, Every);
        return Task.CompletedTask;
    }

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        object? result = method switch
        {
            "getReport" => _report ?? ScanNow(),
            "rescan" => ScanNow(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };
        return Task.FromResult<object?>(result);
    }

    /// <summary>The timer's entry point. Nothing may ever escape from here.</summary>
    private void ScanSafe()
    {
        if (_disposed) return;
        if (!Monitor.TryEnter(_scanGate)) return; // the last check is still running; skip this turn
        try
        {
            ScanNow();
            _lastError = null;
        }
        catch (Exception ex)
        {
            if (_lastError != ex.Message) _log.Warn("The health check could not finish. It will try again in a minute.", ex);
            _lastError = ex.Message;
        }
        finally
        {
            Monitor.Exit(_scanGate);
        }
    }

    /// <summary>Takes one snapshot of the PC, builds the report, and tells the UI when the findings changed.</summary>
    internal HealthReportDto ScanNow()
    {
        lock (_scanGate)
        {
            var scan = _scanner.Scan(new WithAppFacts(_probe, StartsAsAdminFromOpenFolder));
            var report = BuildReport(scan);
            _report = report;

            var signature = string.Join("|", report.Findings.Select(f => $"{f.Id}:{f.Severity}:{f.Evidence}"));
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                _log.Info(Describe(report));
                _ctx?.Events.Publish(ReportTopic, report);
            }
            return report;
        }
    }

    private HealthReportDto BuildReport(ConflictScan scan)
    {
        var findings = scan.Findings;
        var s = scan.Snapshot;
        var counts = new HealthCountsDto(
            findings.Count(f => f.Severity == ConflictSeverity.Blocks),
            findings.Count(f => f.Severity == ConflictSeverity.Warn),
            findings.Count(f => f.Severity == ConflictSeverity.Info));

        var elevated = s.Facts.TryGetValue(ConflictFacts.NotElevated, out var notElevated)
            ? !notElevated
            : _ctx?.Environment.IsElevated ?? false;

        var system = new HealthSystemDto(
            Elevated: elevated,
            FastStartup: Setting("system.fast-startup", s),
            MemoryIntegrity: Setting("system.memory-integrity", s),
            DriverBlocklist: Setting("system.driver-blocklist", s),
            ControlledFolderAccess: Setting("system.controlled-folder-access", s),
            PendingRestart: s.Facts.TryGetValue(ConflictFacts.PendingRestart, out var pending) ? pending : null,
            GameBarRecording: Setting("capture.game-bar", s),
            GameBarBackgroundRecording: Setting("capture.game-bar-background", s),
            GameBarLogoButton: Setting("overlay.game-bar-logo-button", s),
            GameMode: Setting("system.game-mode", s),
            DynamicLighting: Setting("lighting.dynamic-lighting", s),
            WindowsUpdateDrivers: Setting("drivers.windows-update", s),
            SessionCount: s.Counts.TryGetValue(ConflictCounts.Sessions, out var sessions) ? sessions : 1,
            OtherSessionRunsRigControl: s.Facts.TryGetValue(ConflictFacts.OtherSessionRunsRigControl, out var other) && other);

        return new HealthReportDto(_now(), findings, counts, system);
    }

    /// <summary>
    /// Whether the Windows setting behind a catalog entry is on. Null when the entry is not in the catalog or
    /// any registry place it needs could not be read.
    /// </summary>
    private bool? Setting(string entryId, ConflictSnapshot snapshot)
    {
        var entry = _scanner.Catalog.FirstOrDefault(e => e.Id == entryId);
        if (entry is null || entry.Detect.Registry.Count == 0) return null;
        if (entry.Detect.Registry.Any(t => !snapshot.Registry.ContainsKey(t.Place))) return null;
        return _scanner.IsPresent(entryId, snapshot);
    }

    private static string Describe(HealthReportDto report)
    {
        var serious = report.Findings.Where(f => f.Severity != ConflictSeverity.Info).ToList();
        if (serious.Count == 0)
            return report.Findings.Count == 0
                ? "Health check: nothing is getting in the way."
                : $"Health check: nothing is getting in the way ({report.Findings.Count} things worth knowing).";
        var names = string.Join(", ", serious.Take(8).Select(f => f.Name));
        var more = serious.Count > 8 ? $" and {serious.Count - 8} more" : "";
        return $"Health check: {serious.Count} thing(s) can get in the way ({report.Counts.Blocks} stopping a feature): {names}{more}.";
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds what only the app itself knows to the probe's look at the PC.</summary>
    private sealed class WithAppFacts : IConflictProbe
    {
        private readonly IConflictProbe _inner;
        private readonly Func<bool>? _openFolder;

        public WithAppFacts(IConflictProbe inner, Func<bool>? openFolder)
        {
            _inner = inner;
            _openFolder = openFolder;
        }

        public ConflictSnapshot Take(ConflictQuery query)
        {
            var snapshot = _inner.Take(query);
            if (_openFolder is null) return snapshot;
            bool open;
            try { open = _openFolder(); }
            catch { return snapshot; } // could not be worked out: say nothing
            return snapshot with { Facts = new Dictionary<string, bool>(snapshot.Facts) { [ConflictFacts.ProgramFolderOpen] = open } };
        }
    }

    /// <summary>Used where there is nothing to look at (not Windows): every check comes back empty.</summary>
    private sealed class NothingToCheckProbe : IConflictProbe
    {
        public ConflictSnapshot Take(ConflictQuery query) => new();
    }
}
