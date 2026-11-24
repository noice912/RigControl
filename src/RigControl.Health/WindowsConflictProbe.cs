using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using RigControl.Core.Conflicts;
using RigControl.Core.Modules;

namespace RigControl.Health;

/// <summary>
/// Looks at this Windows PC once: running programs, services, drivers, a few scheduled tasks and registry values.
/// It only ever reads. Each part is on its own, so one part failing never stops the others.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConflictProbe : IConflictProbe
{
    private readonly string _tasksRoot;
    private readonly string _ownProcess;
    private readonly Func<bool> _isElevated;

    public WindowsConflictProbe()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks"), "RigControl", AppEnvironment.DetectElevated)
    {
    }

    /// <param name="tasksRoot">The folder Windows keeps its scheduled task files in.</param>
    /// <param name="ownProcess">RigControl's own process name, to spot a copy in another user's session.</param>
    internal WindowsConflictProbe(string tasksRoot, string ownProcess, Func<bool> isElevated)
    {
        _tasksRoot = tasksRoot;
        _ownProcess = ownProcess;
        _isElevated = isElevated;
    }

    public ConflictSnapshot Take(ConflictQuery query)
    {
        var failed = new List<ConflictProbeFailure>();
        var facts = new Dictionary<string, bool>();
        var counts = new Dictionary<string, int>();

        var elevated = false;
        try
        {
            elevated = _isElevated();
            facts[ConflictFacts.NotElevated] = !elevated;
        }
        catch (Exception ex) { failed.Add(new("administrator rights", ex.Message)); }

        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        // Programs in this Windows user's own session. Null when they could not be read at all.
        HashSet<string>? mine = new(StringComparer.OrdinalIgnoreCase);
        try { ReadProcesses(query.ProcessPathsFor, processes, mine, paths, facts, counts); }
        catch (Exception ex)
        {
            failed.Add(new("running programs", ex.Message));
            mine = null;
        }

        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { ReadRunning(ServiceController.GetServices(), services); }
        catch (Exception ex) { failed.Add(new("Windows services", ex.Message)); }

        var drivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { ReadRunning(ServiceController.GetDevices(), drivers); }
        catch (Exception ex) { failed.Add(new("drivers", ex.Message)); }

        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in query.Tasks)
                if (TaskIsOn(_tasksRoot, path)) tasks.Add(path);
        }
        catch (Exception ex)
        {
            // Without administrator rights Windows does not let a program look in the task folder. The
            // "not running as administrator" finding already says so; no need for a second line.
            if (elevated) failed.Add(new("scheduled tasks", ex.Message));
        }

        var registry = new Dictionary<string, RegistryReading>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var test in query.Registry)
                if (ReadRegistry(test) is { } reading) registry[test.Place] = reading;
        }
        catch (Exception ex) { failed.Add(new("Windows settings", ex.Message)); }

        try { facts[ConflictFacts.PendingRestart] = PendingRestart(); }
        catch (Exception ex) { failed.Add(new("whether Windows needs a restart", ex.Message)); }

        // Left out when Windows won't say: the finding then simply doesn't show.
        var notes = new Dictionary<string, string>();
        try
        {
            var (isPublic, names) = PublicHomeNetworks();
            if (isPublic is { } yes)
            {
                facts[ConflictFacts.PublicNetwork] = yes;
                if (yes && PublicNetworkNote(names) is { } note) notes[ConflictFacts.PublicNetwork] = note;
            }
        }
        catch
        {
            // not worth a "couldn't check" line
        }

        return new ConflictSnapshot
        {
            Processes = processes,
            SessionProcesses = mine,
            ProcessPaths = paths,
            Services = services,
            Drivers = drivers,
            Tasks = tasks,
            Registry = registry,
            Facts = facts,
            FactNotes = notes,
            Counts = counts,
            Failed = failed,
        };
    }

    // ---------------------------------------------------------------- programs

    private void ReadProcesses(IReadOnlyList<string> pathsFor, HashSet<string> names, HashSet<string> mine,
        Dictionary<string, IReadOnlyList<string>> paths, Dictionary<string, bool> facts, Dictionary<string, int> counts)
    {
        var wantPath = new HashSet<string>(pathsFor, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var desktops = new HashSet<int>();
        var otherSession = false;
        int mySession;
        using (var me = Process.GetCurrentProcess()) mySession = me.SessionId;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (string.IsNullOrEmpty(name)) continue;
                names.Add(name);

                var session = p.SessionId;
                if (session == mySession) mine.Add(name);
                if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) desktops.Add(session);
                if (name.Equals(_ownProcess, StringComparison.OrdinalIgnoreCase) && session != mySession) otherSession = true;

                if (wantPath.Contains(name) && ImagePath(p.Id) is { } path)
                {
                    if (!found.TryGetValue(name, out var list)) found[name] = list = new List<string>();
                    list.Add(path);
                }
            }
            catch
            {
                // The program ended while we were looking, or Windows won't tell us about it. Skip it.
            }
            finally
            {
                p.Dispose();
            }
        }

        foreach (var (name, list) in found) paths[name] = list;
        facts[ConflictFacts.OtherSessionRunsRigControl] = otherSession;
        counts[ConflictCounts.Sessions] = Math.Max(1, desktops.Count);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The program's file path, or null when Windows won't say. Asks for the least access that gives a path.</summary>
    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- services and drivers

    private static void ReadRunning(ServiceController[] all, HashSet<string> running)
    {
        foreach (var s in all)
        {
            try
            {
                if (s.Status == ServiceControllerStatus.Running) running.Add(s.ServiceName);
            }
            catch
            {
                // One service that can't be read doesn't matter.
            }
            finally
            {
                s.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- scheduled tasks

    /// <summary>
    /// True when the task (or, for a path ending in "\", any task in that folder) exists and is switched on.
    /// Reads the task's own file, so it works the same in every Windows language. Throws when the folder can't be read.
    /// </summary>
    internal static bool TaskIsOn(string tasksRoot, string taskPath)
    {
        var relative = taskPath.Trim('\\');
        if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal)) return false;
        var full = Path.Combine(tasksRoot, relative);

        if (taskPath.EndsWith('\\'))
        {
            if (!Directory.Exists(full)) return false;
            return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Any(TaskFileIsOn);
        }
        return File.Exists(full) && TaskFileIsOn(full);
    }

    private static bool TaskFileIsOn(string file)
    {
        try
        {
            var doc = XDocument.Load(file);
            var enabled = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
            return !string.Equals(enabled?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch
        {
            return true; // the task is there but its file is unusual: count it as on
        }
    }

    // ---------------------------------------------------------------- registry (read-only)

    /// <summary>Reads one place. Returns null when Windows refuses, so the test for it simply doesn't match.</summary>
    internal static RegistryReading? ReadRegistry(RegistryTest test)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                test.Root == RegistryRoot.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(test.Key, writable: false);
            if (key is null) return new RegistryReading(false, false, null);
            if (test.Value is null) return new RegistryReading(true, false, null);
            var value = key.GetValue(test.Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            long? number = value switch
            {
                int i => i,
                long l => l,
                string s when long.TryParse(s, out var parsed) => parsed,
                _ => null,
            };
            return new RegistryReading(true, value is not null, number);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- network type (read-only)

    /// <summary>One connected network as Windows lists it, with the adapters it runs over.</summary>
    /// <param name="Name">The name Windows shows for it (the Wi-Fi name, "Network 2", "Unidentified network").</param>
    /// <param name="CarriesHomeNetwork">
    /// True when one of its adapters is a cable or Wi-Fi adapter that is up and has a router (an IPv4 default
    /// gateway). False for VPN adapters, virtual machine switches and anything else without a router.
    /// </param>
    internal sealed record SeenNetwork(string Name, bool IsPublic, bool CarriesHomeNetwork);

    /// <summary>
    /// Is the home network set to Public? Only networks that carry the home network count: a VPN or a virtual
    /// machine's network is "Public" by default in Windows, but that doesn't stop a light on the home network from
    /// answering. True (with the names) when such a network is Public, false when none is, null when there is no
    /// such network to judge.
    /// </summary>
    internal static (bool? IsPublic, IReadOnlyList<string> Names) Judge(IEnumerable<SeenNetwork> networks)
    {
        var home = networks.Where(n => n.CarriesHomeNetwork).ToList();
        if (home.Count == 0) return (null, Array.Empty<string>());
        var names = home.Where(n => n.IsPublic).Select(n => n.Name.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (home.Any(n => n.IsPublic), names);
    }

    /// <summary>The evidence line that names the network(s), without its full stop; null when Windows gave no name.</summary>
    internal static string? PublicNetworkNote(IReadOnlyList<string> names) => names.Count switch
    {
        0 => null,
        1 => $"The network \"{names[0]}\" is set to Public",
        _ => "These networks are set to Public: " + string.Join(", ", names.Select(n => "\"" + n + "\"")),
    };

    /// <summary>
    /// True when a network that carries this PC's home network is set to "Public" in Windows, false when none is,
    /// null when there is none or Windows won't say. Asks Windows' own network list; sends nothing anywhere.
    /// </summary>
    internal static bool? OnPublicNetwork() => PublicHomeNetworks().IsPublic;

    /// <summary>The same, with the names of the networks that are Public.</summary>
    internal static (bool? IsPublic, IReadOnlyList<string> Names) PublicHomeNetworks()
    {
        const int Connected = 1, Public = 0;
        var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")); // Network List Manager
        if (type is null) return (null, Array.Empty<string>());
        var homeAdapters = HomeAdapterIds();
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(type);
            if (manager is null) return (null, Array.Empty<string>());
            var seen = new List<SeenNetwork>();
            foreach (dynamic network in ((dynamic)manager).GetNetworks(Connected))
            {
                try
                {
                    var isPublic = (int)network.GetCategory() == Public;
                    string name;
                    try { name = (string)network.GetName() ?? ""; } catch { name = ""; }
                    var home = false;
                    foreach (object connection in network.GetNetworkConnections())
                    {
                        try
                        {
                            // The adapter's id is a GUID, which the late-bound call can't hand over: the typed interface can.
                            if (connection is INetworkConnection typed && homeAdapters.Contains(typed.GetAdapterId())) home = true;
                        }
                        catch
                        {
                            // An adapter Windows won't name is not counted as carrying the home network.
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(connection);
                        }
                    }
                    // The name is Windows' own (often the Wi-Fi name); tidied before it is shown.
                    seen.Add(new SeenNetwork(new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray()), isPublic, home));
                }
                finally
                {
                    Marshal.ReleaseComObject(network);
                }
            }
            return Judge(seen);
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>Windows' own "one connection of a network" (netlistmgr.h), only as far as it is read here. Read-only.</summary>
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        bool IsConnectedToInternet { get; }
        bool IsConnected { get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }

    /// <summary>The adapters that carry a home network: cable or Wi-Fi, up, with a router. Reads only.</summary>
    private static HashSet<Guid> HomeAdapterIds()
    {
        var ids = new HashSet<Guid>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (!IsHomeAdapter(nic.NetworkInterfaceType, nic.Description,
                        nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                                       && !g.Address.Equals(System.Net.IPAddress.Any))))
                    continue;
                if (Guid.TryParse(nic.Id, out var id)) ids.Add(id);
            }
            catch
            {
                // an adapter that can't be read is not counted
            }
        }
        return ids;
    }

    /// <summary>A cable or Wi-Fi adapter with a router. Virtual machine adapters are left out even when one has a gateway.</summary>
    internal static bool IsHomeAdapter(System.Net.NetworkInformation.NetworkInterfaceType type, string? description, bool hasIpv4Gateway)
    {
        if (!hasIpv4Gateway) return false;
        if (type is not (System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
            or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
            or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT
            or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx
            or System.Net.NetworkInformation.NetworkInterfaceType.Ethernet3Megabit
            or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211))
            return false;
        var d = description ?? "";
        return !new[] { "Hyper-V", "VirtualBox", "VMware", "Virtual Ethernet", "TAP-", "WireGuard", "Wintun", "OpenVPN", "TunnelBear", "NordLynx" }
            .Any(v => d.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PendingRestart()
    {
        var places = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        };
        foreach (var place in places)
        {
            var reading = ReadRegistry(new RegistryTest(RegistryRoot.LocalMachine, place, null, RegistryCheck.KeyExists));
            if (reading is { KeyExists: true }) return true;
        }
        return false;
    }
}
