using RigControl.Core.Modules;
using RigControl.Core.Platform;

namespace RigControl.Hosting;

/// <summary>One feature area as the UI knows it: its RPC name, its title, and the systems it has a version for.</summary>
/// <param name="Name">The module's RPC name, e.g. "lighting".</param>
/// <param name="DisplayName">The title shown in status lists.</param>
/// <param name="Platforms">The systems that have a real version of it.</param>
/// <param name="Parts">
/// Its parts by capability name, each with the systems it works on. Used when the module itself does not say
/// (<see cref="IModuleCapabilities"/>).
/// </param>
public sealed record ModuleDescriptor(
    string Name,
    string DisplayName,
    IReadOnlyCollection<HostPlatform> Platforms,
    IReadOnlyDictionary<string, IReadOnlyCollection<HostPlatform>> Parts)
{
    public bool AvailableOn(HostPlatform platform) => Platforms.Contains(platform);
}

/// <summary>
/// Every feature area RigControl has, and where each one can work. This is the single list both hosts build their
/// modules from, and the source of the "capabilities" the UI gets in app.getInfo.
/// </summary>
public static class ModuleCatalog
{
    private static readonly HostPlatform[] WindowsOnly = { HostPlatform.Windows };
    private static readonly HostPlatform[] Desktop = { HostPlatform.Windows, HostPlatform.Linux, HostPlatform.MacOS };

    private static ModuleDescriptor Module(string name, string displayName, HostPlatform[] platforms,
        params (string Part, HostPlatform[] Platforms)[] parts)
        => new(name, displayName, platforms,
            parts.ToDictionary(p => p.Part, p => (IReadOnlyCollection<HostPlatform>)p.Platforms, StringComparer.Ordinal));

    /// <summary>In the order the Windows app starts them.</summary>
    public static IReadOnlyList<ModuleDescriptor> All { get; } = new[]
    {
        Module("sensors", "Sensors and motherboard fans", WindowsOnly),
        Module("nvidia", "NVIDIA graphics", WindowsOnly),
        Module("cooling", "NZXT cooling", WindowsOnly),
        Module("lighting", "Lighting (OpenRGB)", Desktop,
            // Works wherever OpenRGB and the network lights do.
            ("lighting.openrgb", Desktop),
            ("lighting.network", Desktop),
            ("lighting.effects", Desktop),
            ("lighting.layout", Desktop),
            ("lighting.scenes", Desktop),
            // Windows only in this version.
            ("lighting.screen", WindowsOnly),
            ("lighting.captureCard", WindowsOnly),
            ("lighting.audio", WindowsOnly),
            ("lighting.openrgbServer", WindowsOnly),
            ("lighting.gcc", WindowsOnly)),
        Module("capture", "Recording and instant replay", WindowsOnly),
        Module("listen", "Capture card sound", WindowsOnly),
        Module("drivers", "Device drivers", WindowsOnly),
        Module("bios", "BIOS settings", WindowsOnly),
        Module("cpu", "Processor", WindowsOnly),
        Module("presence", "Discord status", Desktop,
            ("presence.custom", Desktop),
            ("presence.apps", Desktop),
            // Windows only in this version.
            ("presence.media", WindowsOnly),
            ("presence.games", WindowsOnly),
            ("presence.launcher", WindowsOnly),
            ("presence.watching", WindowsOnly),
            ("presence.extension", WindowsOnly)),
        Module("health", "Health", WindowsOnly),
        // RigControl Link: linked phones through the relay (Settings > Phone).
        Module("link", "Phone link", WindowsOnly),
    };

    public static ModuleDescriptor? Find(string name)
        => All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The module list for one system: for every catalogue entry the host has a maker for, the real module; for the
    /// rest a stand-in that says "Not available on this system". The order is the catalogue's. Makers for names the
    /// catalogue does not know are added at the end, in the order given.
    /// A maker is only called for a system the catalogue lists for that module.
    /// </summary>
    public static List<IAppModule> Build(HostPlatform platform, IReadOnlyList<KeyValuePair<string, Func<IAppModule>>> makers)
    {
        var byName = new Dictionary<string, Func<IAppModule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in makers) byName[m.Key] = m.Value;

        var list = new List<IAppModule>();
        foreach (var d in All)
        {
            if (d.AvailableOn(platform) && byName.TryGetValue(d.Name, out var make)) list.Add(make());
            else list.Add(new UnavailableModule(d.Name, d.DisplayName, WhyNot(d, platform)));
        }
        foreach (var m in makers)
        {
            if (Find(m.Key) is null) list.Add(m.Value());
        }
        return list;
    }

    /// <summary>The short reason shown after "Not available on this system: ".</summary>
    internal static string WhyNot(ModuleDescriptor d, HostPlatform platform)
    {
        if (!d.AvailableOn(platform))
        {
            return d.Platforms.Count == 1 && d.Platforms.Contains(HostPlatform.Windows)
                ? "it works on Windows only."
                : "there is no version of it for " + PlatformInfo.DisplayName(platform) + " yet.";
        }
        return "this build was made without it.";
    }
}

/// <summary>The capability names the UI gets in app.getInfo: which modules and which of their parts work here.</summary>
public static class HostCapabilities
{
    // What the host itself can do (not a module).
    public const string Tray = "host.tray";
    public const string Elevation = "host.elevation";
    public const string Autostart = "host.autostart";
    public const string Overlay = "host.overlay";
    public const string AudioSection = "host.audio";
    public const string FilePicker = "host.filePicker";
    public const string FirstRun = "host.firstRun";
    public const string Helpers = "host.helpers";
    public const string RestartPc = "host.restartPc";
    /// <summary>The "updates", "github" and "dev" calls: auto-update, GitHub sign-in and reviews, dev mode.</summary>
    public const string Updates = "host.updates";

    /// <summary>Everything the Windows app (WPF) offers besides its modules.</summary>
    public static readonly IReadOnlyList<string> WindowsHost = new[]
    {
        Tray, Elevation, Autostart, Overlay, AudioSection, FilePicker, FirstRun, Helpers, RestartPc, Updates,
    };

    /// <summary>
    /// The sorted list for this run: one name per module that is really there (a stand-in does not count), each
    /// module's parts (what it says itself, or else what the catalogue lists for this system), and the host's own.
    /// </summary>
    public static IReadOnlyList<string> For(HostPlatform platform, IEnumerable<IAppModule> modules, IEnumerable<string> hostFeatures)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var m in modules)
        {
            if (m is UnavailableModule) continue;
            set.Add(m.Name);
            if (m is IModuleCapabilities own)
            {
                IReadOnlyCollection<string> parts;
                try { parts = own.Capabilities; }
                catch { continue; }
                foreach (var p in parts)
                    if (p.StartsWith(m.Name + ".", StringComparison.Ordinal)) set.Add(p);
            }
            else if (ModuleCatalog.Find(m.Name) is { } d)
            {
                foreach (var (part, where) in d.Parts)
                    if (where.Contains(platform)) set.Add(part);
            }
        }
        foreach (var f in hostFeatures) set.Add(f);
        return set.ToArray();
    }
}

/// <summary>What app.getInfo answers. The UI reads "platform" and "capabilities" to hide or explain pages.</summary>
public sealed record AppInfo(
    string Version,
    bool IsElevated,
    string DataDirectory,
    string LogDirectory,
    string MachineName,
    string Platform,
    IReadOnlyList<string> Capabilities)
{
    public static AppInfo From(AppEnvironment env, HostPlatform platform, IEnumerable<IAppModule> modules, IEnumerable<string> hostFeatures)
        => new(env.Version, env.IsElevated, env.DataDirectory, env.LogDirectory, Environment.MachineName,
            PlatformInfo.Id(platform), HostCapabilities.For(platform, modules, hostFeatures));
}
