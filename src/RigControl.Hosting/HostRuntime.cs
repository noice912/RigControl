using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Hosting;

/// <summary>The steps of starting modules that every host does the same way.</summary>
public static class HostModules
{
    /// <summary>How long one module may take to start before the others stop waiting for it.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Adds each module to the host (so it is started and stopped) and to the dispatcher (so the UI can call it).</summary>
    public static void Register(IEnumerable<IAppModule> modules, ModuleHost host, RpcDispatcher dispatcher)
    {
        foreach (var m in modules)
        {
            host.Add(m);
            dispatcher.Register(m);
        }
    }

    /// <summary>
    /// Starts every module, each with its own log, then tells the UI the list changed ("app.modulesChanged").
    /// Never throws: one module failing is recorded for that module only.
    /// </summary>
    public static async Task StartAllAsync(ModuleHost host, AppEnvironment env, SettingsStore settings, EventBus bus, ILog log,
        CancellationToken ct, IPlatformServices? platform = null)
    {
        try
        {
            await host.StartAllAsync(
                m => platform is null
                    ? new ModuleContext(bus, log.For(m.Name), settings, env)
                    : new ModuleContext(bus, log.For(m.Name), settings, env) { Platform = platform },
                StartTimeout,
                ct);
        }
        catch (Exception ex)
        {
            log.Error("Module startup failed unexpectedly.", ex);
        }
        bus.Publish("app.modulesChanged", host.Describe());
    }
}

/// <summary>
/// The whole non-window part of a host, for hosts that have nothing to add in between (the Linux/macOS host):
/// folders, log, settings, event bus, dispatcher and module host, made in the same order the Windows app makes them.
/// </summary>
public sealed class HostRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _stopped;

    private HostRuntime(AppServices services, AppPaths paths)
    {
        Services = services;
        Paths = paths;
    }

    public AppServices Services { get; }
    public AppPaths Paths { get; }

    /// <summary>Cancelled when the host is closing.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>Makes the folders, opens the log and the settings. Nothing is started and no device is touched.</summary>
    public static HostRuntime Create(AppPaths paths, string version, string executablePath, IPlatformServices? platform = null)
    {
        paths.EnsureCreated();
        var log = new FileLog(paths.LogDirectory);
        var isElevated = platform?.IsElevated ?? AppEnvironment.DetectElevated();
        var env = new AppEnvironment(paths.DataDirectory, paths.LogDirectory, isElevated, version, executablePath);
        var settings = new SettingsStore(paths.SettingsFile, log.For("settings"));
        var bus = new EventBus();
        var dispatcher = new RpcDispatcher(log.For("rpc"));
        var host = new ModuleHost(log.For("modules"));
        var services = new AppServices(env, settings, bus, dispatcher, host, log);
        if (platform is not null) services = services with { Platform = platform };
        return new HostRuntime(services, paths);
    }

    /// <summary>Adds the modules (see <see cref="ModuleCatalog.Build"/>) so they can be started and called.</summary>
    public void AddModules(IEnumerable<IAppModule> modules)
        => HostModules.Register(modules, Services.Host, Services.Dispatcher);

    public Task StartModulesAsync()
        => HostModules.StartAllAsync(Services.Host, Services.Environment, Services.Settings, Services.Events, Services.Log,
            _lifetime.Token, Services.Platform);

    /// <summary>Stops the modules (each gets five seconds). Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { _lifetime.Cancel(); } catch { /* ignore */ }
        try { await Services.Host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Services.Log.Error("Stopping modules failed.", ex); }
        // The token source is left undisposed on purpose: Lifetime stays readable after closing.
    }
}
