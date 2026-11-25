using System.Reflection;
using System.Runtime.InteropServices;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Hosting;

namespace RigControl.Desktop;

/// <summary>RigControl for Linux and macOS. The Windows app is RigControl.App.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // This program builds on Windows only so the solution is one build. It never runs there.
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(StartupGate.OnWindows);
            return 1;
        }

        var options = StartOptions.Parse(args);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        if (options.Help)
        {
            Console.WriteLine(StartOptions.HelpText);
            return 0;
        }
        if (options.Version)
        {
            Console.WriteLine("RigControl " + version);
            return 0;
        }
        if (options.Unknown is { } unknown)
        {
            Console.Error.WriteLine($"RigControl does not know the option \"{unknown}\". Use --help to see the options.");
            return 1;
        }

        var refusal = StartupGate.Check(isWindows: false, OperatingSystem.IsLinux(), Environment.IsPrivilegedProcess,
            Environment.GetEnvironmentVariable);
        if (refusal is not null)
        {
            Console.Error.WriteLine(refusal);
            return 1;
        }

        return options.Smoke ? RunSmoke(options, version) : RunApp(options, version);
    }

    /// <summary>The modules this app really has. Everything else in the catalogue says "Not available on this system".</summary>
    internal static IReadOnlyList<KeyValuePair<string, Func<IAppModule>>> Makers() => new KeyValuePair<string, Func<IAppModule>>[]
    {
        new("lighting", () => new RigControl.Lighting.LightingModule()),
        new("presence", () => new RigControl.Presence.PresenceModule()),
    };

    private static int RunApp(StartOptions options, string version)
    {
        var paths = AppPaths.ForThisSystem();
        paths.EnsureCreated();
        using var instance = SingleInstance.TryTake(paths.DataDirectory, out var lockProblem);
        if (instance is null)
        {
            Console.Error.WriteLine(StartupGate.AlreadyRunning);
            return 0;
        }
        return Run(paths, options, version, lockProblem);
    }

    private static int RunSmoke(StartOptions options, string version)
    {
        // Its own throwaway folder: the user's settings, lights and Discord are never touched by a smoke check.
        var folder = Path.Combine(Path.GetTempPath(), "rigcontrol-smoke-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(folder, Path.Combine(folder, "logs"));
        try
        {
            return Run(paths, options, version, null);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* the system clears its temporary folder */ }
        }
    }

    private static int Run(AppPaths paths, StartOptions options, string version, string? lockProblem)
    {
        var runtime = HostRuntime.Create(paths, version, Environment.ProcessPath ?? "");
        var log = runtime.Services.Log;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unhandled exception (fatal=" + e.IsTerminating + ").", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
        log.Info($"RigControl {version} starting on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})" +
                 (options.Smoke ? ", smoke check." : "."));
        if (lockProblem is not null)
            log.Warn("The lock file could not be made, so a second RigControl would not be noticed: " + lockProblem);

        var window = new PhotinoAppWindow(log.For("window"));
        var session = new DesktopSession(runtime, options, window, Makers());
        var code = 0;
        // Ctrl+C, a sign-out or "kill": close in order, so the lights are handed back.
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; session.RequestExit(); });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; session.RequestExit(); });
        using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; session.RequestExit(); });
        try
        {
            session.StartModules();
            if (options.Smoke) _ = WatchSmokeAsync(session, log);
            window.Run(session);
            log.Info("The window closed.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            log.Error("The window could not be opened: the system's web view, another system library or the window library is missing.", ex);
            Console.Error.WriteLine(StartupGate.NoWebView);
            // The system's own words name the file that is missing, which may be neither of the two above.
            Console.Error.WriteLine("What the system said: " + ex.Message + " The log in " + paths.LogDirectory + " has the details.");
            code = 1;
        }
        catch (Exception ex)
        {
            log.Error("The window stopped with an error.", ex);
            Console.Error.WriteLine("RigControl's window stopped with an error: " + ex.Message + " The log in " + paths.LogDirectory + " has the details.");
            code = 1;
        }

        // Modules first and in full (lights are handed back), then the last log line, then out.
        try
        {
            if (!session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(20)))
                log.Warn("Stopping took longer than 20 seconds. Closing anyway.");
        }
        catch (Exception ex)
        {
            log.Error("Stopping failed.", ex);
        }
        if (code == 0) code = session.ExitCode;
        if (session.PageNeverConnected)
            Console.Error.WriteLine(StartupGate.PageNeverConnected + " The log in " + paths.LogDirectory + " says what was tried.");
        log.Info("RigControl closed" + (code == 0 ? "." : " with exit code " + code + "."));
        if (options.Smoke) PrintLog(log);
        return code;
    }

    /// <summary>A smoke check that cannot close its window must still end: the build machine is waiting.</summary>
    private static async Task WatchSmokeAsync(DesktopSession session, FileLog log)
    {
        await session.RunSmokeCheckAsync(DesktopSession.SmokeLimit).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        log.Error("The window did not close after the smoke check. Ending the process.");
        PrintLog(log);
        Environment.Exit(session.ExitCode == 0 ? 3 : session.ExitCode);
    }

    private static void PrintLog(FileLog log)
    {
        foreach (var line in log.Recent())
            Console.WriteLine($"{line.Time:HH:mm:ss} {line.Level.ToString().ToLowerInvariant(),-5} [{line.Source}] {line.Message}");
    }
}
