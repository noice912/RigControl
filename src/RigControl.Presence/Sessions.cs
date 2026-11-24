using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RigControl.Presence;

/// <summary>
/// "This user" on a PC that several people may be signed in to at once: the Windows session RigControl runs in, and
/// the accounts that count as its user. Everything that feeds a Discord card is limited to this.
/// </summary>
internal static class WinSession
{
    private static readonly Lazy<int> Mine = new(() =>
    {
        using var p = Process.GetCurrentProcess();
        return p.SessionId;
    });

    /// <summary>The Windows session RigControl runs in.</summary>
    public static int Current => Mine.Value;

    /// <summary>The session a process runs in, or null when Windows won't say.</summary>
    public static int? OfProcess(int pid)
    {
        try { return pid > 0 && ProcessIdToSessionId((uint)pid, out var s) ? (int)s : null; }
        catch { return null; }
    }

    /// <summary>The account a process runs as (its SID), or null when it can't be read.</summary>
    public static string? UserOfProcess(int pid)
    {
        if (pid <= 0) return null;
        var proc = IntPtr.Zero;
        var token = IntPtr.Zero;
        var buf = IntPtr.Zero;
        try
        {
            proc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (proc == IntPtr.Zero) return null;
            if (!OpenProcessToken(proc, TokenQuery, out token)) return null;
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var need);
            if (need <= 0 || need > 4096) return null;
            buf = Marshal.AllocHGlobal(need);
            if (!GetTokenInformation(token, TokenUser, buf, need, out _)) return null;
            // TOKEN_USER starts with a pointer to the SID.
            return new SecurityIdentifier(Marshal.ReadIntPtr(buf)).Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (proc != IntPtr.Zero) CloseHandle(proc);
        }
    }

    /// <summary>
    /// The accounts that are "this user": the one RigControl runs as, and the one signed in to this desktop (they
    /// differ only when RigControl was started as administrator with another account's password).
    /// </summary>
    public static IReadOnlySet<string> MyUsers()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is { } sid) set.Add(sid.Value);
        }
        catch { /* leaves the set smaller: more is refused, never less */ }
        try
        {
            var shell = GetShellWindow();
            if (shell != IntPtr.Zero && GetWindowThreadProcessId(shell, out var pid) != 0 && OfProcess((int)pid) == Current
                && UserOfProcess((int)pid) is { } desk) set.Add(desk);
        }
        catch { /* same */ }
        return set;
    }

    /// <summary>Names (lower case, no .exe) and start times of the processes in this session only.</summary>
    public static IReadOnlyList<RunningProcess> Processes()
    {
        var all = new List<(string Name, int Session, DateTime? Start)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var session = p.SessionId;
                    if (session != Current) continue;
                    DateTime? start = null;
                    try { start = p.StartTime.ToUniversalTime(); } catch { /* protected process */ }
                    all.Add((p.ProcessName, session, start));
                }
                catch { /* gone already */ }
            }
        }
        return InSession(all, Current);
    }

    /// <summary>The filter itself, separate so it can be tested: only processes of one session are kept.</summary>
    internal static IReadOnlyList<RunningProcess> InSession(IEnumerable<(string Name, int Session, DateTime? Start)> all, int session) =>
        all.Where(p => p.Session == session).Select(p => new RunningProcess(p.Name.ToLowerInvariant(), p.Start)).ToList();

    /// <summary>Process ids with one of these names, in this session only.</summary>
    public static IReadOnlySet<int> PidsByName(IEnumerable<string> names)
    {
        var pids = new HashSet<int>();
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { if (p.SessionId == Current) pids.Add(p.Id); }
                    catch { /* gone already */ }
                }
            }
        }
        return pids;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int needed);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}

/// <summary>What other parts of RigControl may ask about the Windows account it runs as.</summary>
public static class DesktopUser
{
    /// <summary>
    /// True when RigControl runs as a different Windows account than the one signed in to this desktop (it was
    /// started as administrator with another account's password). Its settings folder is then that account's.
    /// </summary>
    public static bool IsAnotherAccount()
    {
        try { return WinSession.MyUsers().Count > 1; }
        catch { return false; }
    }
}

/// <summary>The program on the other end of a local connection.</summary>
public sealed record PeerInfo(int Pid, int SessionId, string UserSid);

/// <summary>Finds which program opened a connection to the browser extension link. Abstracted so tests can fake it.</summary>
public interface IPeerLookup
{
    /// <summary>The program whose side of the connection is 127.0.0.1:<paramref name="peerPort"/>, or null when it can't be told.</summary>
    PeerInfo? Find(int peerPort, int listenPort);
}

/// <summary>
/// Decides whether a local connection comes from this user: the program must run in RigControl's own Windows session
/// and as this user. Anything that can't be identified is refused.
/// </summary>
public sealed class PeerGuard
{
    private readonly IPeerLookup _lookup;
    private readonly int _session;
    private readonly IReadOnlySet<string> _users;

    public PeerGuard(IPeerLookup lookup, int session, IReadOnlySet<string> users)
    {
        _lookup = lookup;
        _session = session;
        _users = users;
    }

    public static PeerGuard ForThisUser() => new(new WinPeerLookup(), WinSession.Current, WinSession.MyUsers());

    public bool Allows(int peerPort, int listenPort)
    {
        PeerInfo? peer;
        try { peer = _lookup.Find(peerPort, listenPort); }
        catch { peer = null; }
        return Allows(peer);
    }

    public bool Allows(PeerInfo? peer) =>
        peer is not null && peer.SessionId == _session && !string.IsNullOrEmpty(peer.UserSid) && _users.Contains(peer.UserSid);
}

/// <summary>Windows' own table of TCP connections with the process that owns each end.</summary>
public sealed class WinPeerLookup : IPeerLookup
{
    public PeerInfo? Find(int peerPort, int listenPort)
    {
        var pid = OwnerPid(peerPort, listenPort);
        if (pid is null) return null;
        var session = WinSession.OfProcess(pid.Value);
        var user = WinSession.UserOfProcess(pid.Value);
        return session is null || user is null ? null : new PeerInfo(pid.Value, session.Value, user);
    }

    /// <summary>The process that owns 127.0.0.1:peerPort connected to 127.0.0.1:listenPort.</summary>
    private static int? OwnerPid(int peerPort, int listenPort)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size <= 0 || size > 64 << 20) return null;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var err = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
                if (err == ErrorInsufficientBuffer) continue; // the table grew in between: once more with the new size
                if (err != 0) return null;
                var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
                var count = Marshal.ReadInt32(buf);
                // Each row: state, local address, local port, remote address, remote port, owning pid (4 bytes each).
                for (var i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * 24;
                    if ((uint)Marshal.ReadInt32(row, 4) != loopback || (uint)Marshal.ReadInt32(row, 12) != loopback) continue;
                    if (Port(Marshal.ReadInt32(row, 8)) != peerPort || Port(Marshal.ReadInt32(row, 16)) != listenPort) continue;
                    return Marshal.ReadInt32(row, 20);
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return null;
    }

    /// <summary>Ports are stored in network byte order in the low two bytes.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
