using System.Security.Principal;
using RigControl.Core.Logging;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Core.Modules;

public enum ModuleState
{
    NotStarted,
    Starting,
    /// <summary>Fully working.</summary>
    Ready,
    /// <summary>Running with reduced function (e.g. not elevated, a device missing). Message says why.</summary>
    Degraded,
    /// <summary>Could not start. Its RPC methods should throw "unavailable".</summary>
    Failed,
    Stopped,
}

public sealed record ModuleStatus(ModuleState State, string? Message = null)
{
    public static readonly ModuleStatus NotStarted = new(ModuleState.NotStarted);
}

/// <summary>Pushes events to the UI. Topic names look like "sensors.snapshot".</summary>
public interface IEventBus
{
    void Publish(string topic, object? payload);
}

/// <summary>In-process event bus. Subscribers must not block; exceptions in a subscriber are swallowed.</summary>
public sealed class EventBus : IEventBus
{
    public event Action<string, object?>? Published;

    public void Publish(string topic, object? payload)
    {
        var handlers = Published;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<string, object?>)h)(topic, payload); }
            catch { /* a bad subscriber must not break the publisher */ }
        }
    }
}

/// <summary>Facts about the running app that modules may need.</summary>
public sealed record AppEnvironment(
    string DataDirectory,
    string LogDirectory,
    bool IsElevated,
    string Version,
    string ExecutablePath)
{
    /// <summary>
    /// Windows: true when the current process token is a member of the Administrators group (i.e. elevated).
    /// Linux and macOS: true only when running as root, which RigControl never asks for.
    /// </summary>
    public static bool DetectElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            try { return System.Environment.IsPrivilegedProcess; }
            catch { return false; }
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Everything a module receives when it starts.</summary>
public sealed record ModuleContext(IEventBus Events, ILog Log, SettingsStore Settings, AppEnvironment Environment)
{
    /// <summary>
    /// What the system offers: keeping secrets, the user's running programs, opening links, starting at sign-in.
    /// The standard set for this system unless the host (or a test) gives another one.
    /// </summary>
    public IPlatformServices Platform { get; init; } = PlatformServices.Create(Environment.DataDirectory, Environment.IsElevated);
}

/// <summary>
/// One independent feature area (sensors, nvidia, cooling, lighting...). Modules must not depend on each other:
/// if one fails to start or crashes, the others keep working.
/// </summary>
public interface IAppModule : IRpcModule, IAsyncDisposable
{
    /// <summary>Human-readable name for status displays, e.g. "NVIDIA graphics".</summary>
    string DisplayName { get; }

    /// <summary>Current state. Must be safe to read from any thread.</summary>
    ModuleStatus Status { get; }

    /// <summary>
    /// Starts the module. Should return within a few seconds; long discovery should continue in the background.
    /// May throw: the host records the failure and carries on.
    /// </summary>
    Task StartAsync(ModuleContext context, CancellationToken ct);
}

public sealed record ModuleInfo(string Name, string DisplayName, ModuleState State, string? Message);

/// <summary>Starts, tracks and disposes modules, isolating failures.</summary>
public sealed class ModuleHost : IAsyncDisposable
{
    private readonly List<IAppModule> _modules = new();
    private readonly Dictionary<string, ModuleStatus> _hostStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ILog _log;

    public ModuleHost(ILog log)
    {
        _log = log;
    }

    public IReadOnlyList<IAppModule> Modules => _modules;

    public void Add(IAppModule module) => _modules.Add(module);

    /// <summary>Starts every module concurrently. Never throws; failures are recorded per module.</summary>
    public async Task StartAllAsync(Func<IAppModule, ModuleContext> contextFor, TimeSpan perModuleTimeout, CancellationToken ct)
    {
        var tasks = _modules.Select(m => StartOneAsync(m, contextFor, perModuleTimeout, ct)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task StartOneAsync(IAppModule module, Func<IAppModule, ModuleContext> contextFor, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            SetHostStatus(module.Name, new ModuleStatus(ModuleState.Starting));
            var start = Task.Run(() => module.StartAsync(contextFor(module), ct), ct);
            var finished = await Task.WhenAny(start, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != start)
            {
                _log.Warn($"Module '{module.Name}' is still starting after {timeout.TotalSeconds:0}s; continuing without waiting.");
                _ = start.ContinueWith(t =>
                {
                    if (t.IsFaulted) RecordFailure(module, t.Exception!.GetBaseException());
                    else ClearHostStatus(module.Name);
                }, TaskScheduler.Default);
                return;
            }
            await start.ConfigureAwait(false);
            ClearHostStatus(module.Name);
            _log.Info($"Module '{module.Name}' started: {module.Status.State} {module.Status.Message}");
        }
        catch (Exception ex)
        {
            RecordFailure(module, ex);
        }
    }

    private void RecordFailure(IAppModule module, Exception ex)
    {
        _log.Error($"Module '{module.Name}' failed to start.", ex);
        SetHostStatus(module.Name, new ModuleStatus(ModuleState.Failed, ex.Message));
    }

    private void SetHostStatus(string name, ModuleStatus status)
    {
        lock (_gate) _hostStatus[name] = status;
    }

    private void ClearHostStatus(string name)
    {
        lock (_gate) _hostStatus.Remove(name);
    }

    /// <summary>Status of every module. A failure recorded by the host overrides what the module reports.</summary>
    public IReadOnlyList<ModuleInfo> Describe()
    {
        var list = new List<ModuleInfo>(_modules.Count);
        foreach (var m in _modules)
        {
            ModuleStatus status;
            lock (_gate)
            {
                if (!_hostStatus.TryGetValue(m.Name, out status!))
                {
                    try { status = m.Status; }
                    catch (Exception ex) { status = new ModuleStatus(ModuleState.Failed, ex.Message); }
                }
            }
            list.Add(new ModuleInfo(m.Name, m.DisplayName, status.State, status.Message));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _modules)
        {
            try
            {
                var dispose = m.DisposeAsync().AsTask();
                if (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != dispose)
                    _log.Warn($"Module '{m.Name}' did not shut down within 5s.");
                else
                    await dispose.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Module '{m.Name}' failed while shutting down.", ex);
            }
        }
    }
}
