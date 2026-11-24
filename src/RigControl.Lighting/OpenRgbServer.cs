using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace RigControl.Lighting;

/// <summary>What the lighting module needs from OpenRGB. Abstracted so tests never start a process or open a socket.</summary>
public interface IOpenRgb : IDisposable
{
    /// <summary>Full path of an installed OpenRGB.exe, or null.</summary>
    string? FindExecutable();

    /// <summary>True if something already answers on the SDK port (a server the user or a service started).</summary>
    bool IsListening();

    /// <summary>Starts OpenRGB hidden as an SDK server and waits for its port. Returns false if it did not come up.</summary>
    bool StartServer(string executable);

    /// <summary>True if this object started the server (and will stop it on dispose).</summary>
    bool OwnsServer { get; }

    bool Connected { get; }
    void Connect();
    void Disconnect();
    IReadOnlyList<RgbController> ReadControllers();
    void UpdateMode(int device, int modeIndex, RgbMode mode);
    void UpdateLeds(int device, IReadOnlyList<uint> colors);
    void UpdateZoneLeds(int device, int zone, IReadOnlyList<uint> colors);

    /// <summary>
    /// Tells OpenRGB how many lights hang on a header. Only on the user's own request. OpenRGB saves the number
    /// itself; the device must be read again afterwards (its lights are numbered afresh).
    /// </summary>
    void ResizeZone(int device, int zone, int leds);

    /// <summary>True (once) if the server announced that its device list changed since the last call.</summary>
    bool DeviceListChanged();
}

/// <summary>
/// OpenRGB as a hidden local SDK server on 127.0.0.1, protocol 3..5. See docs/protocols/openrgb-integration.md.
/// The child process is tied to RigControl with a Windows job object, so it can never outlive it.
/// All calls are serialised by the owning module.
/// </summary>
public sealed class OpenRgbServer : IOpenRgb
{
    public const int Port = 6742;
    private const string Host = "127.0.0.1";

    private Process? _process;
    private IntPtr _job;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _version;
    private bool _listChanged;

    public bool OwnsServer => _process is { HasExited: false };
    public bool Connected => _tcp is { Connected: true } && _stream is not null;

    // ---------------------------------------------------------------- finding and starting

    /// <summary>
    /// Checks RigControl's own OpenRGB copy (every file against the SHA-256 recorded when RigControl was built) before
    /// it is started. Set once by the app at start; the lighting project itself does not know the recorded hashes.
    /// Gets the full path of the copy's OpenRGB.exe and returns false when it must not be started.
    /// </summary>
    public static Func<string, bool>? OwnCopyVerifier { get; set; }

    /// <summary>RigControl's own copy: C:\Program Files\RigControl\OpenRGB\OpenRGB.exe.</summary>
    public static string OwnCopyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RigControl", "OpenRGB", "OpenRGB.exe");

    private readonly Func<string, bool>? _verifyOwnCopy;
    private readonly bool _elevated;
    private readonly Func<string, bool> _othersCanChange;

    /// <param name="verifyOwnCopy">Overrides <see cref="OwnCopyVerifier"/> for this object.</param>
    public OpenRgbServer(Func<string, bool>? verifyOwnCopy = null) : this(verifyOwnCopy, IsElevated(), OrdinaryUsers.CanChange) { }

    internal OpenRgbServer(Func<string, bool>? verifyOwnCopy, bool elevated, Func<string, bool> othersCanChange)
    {
        _verifyOwnCopy = verifyOwnCopy;
        _elevated = elevated;
        _othersCanChange = othersCanChange;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return true; // unknown: use the strict rules
        }
    }

    /// <summary>Where an OpenRGB.exe was heard of. The two "User" places can be written by any program of the user.</summary>
    internal enum Place { OwnCopy, Service, MachineInstall, ProgramFiles, UserInstall, UserFolder }

    internal sealed record Candidate(string? Path, Place Place);

    public string? FindExecutable() => Find(Candidates(), File.Exists);

    internal string? Find(IEnumerable<Candidate> candidates, Func<string, bool> exists) =>
        Choose(candidates.Where(c => c.Place != Place.OwnCopy || !exists(c.Path ?? "") || OwnCopyLooksRight(c.Path!)), _elevated, exists, _othersCanChange);

    private (DateTime Written, bool Ok, long At)? _ownCopyChecked;

    /// <summary>
    /// RigControl's own copy is only offered when it passes the verifier (if one is set), so a damaged or half-written
    /// copy falls back to another install. The answer is remembered while the file is unchanged; a "no" only for
    /// half a minute, because RigControl repairs its copy at start. It is checked again, fresh, right before starting.
    /// </summary>
    private bool OwnCopyLooksRight(string path)
    {
        var verify = _verifyOwnCopy ?? OwnCopyVerifier;
        if (verify is null) return true;
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (_ownCopyChecked is { } last && last.Written == written && (last.Ok || Environment.TickCount64 - last.At < 30_000)) return last.Ok;
            var ok = verify(Path.GetFullPath(path));
            _ownCopyChecked = (written, ok, Environment.TickCount64);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The first candidate that exists. When RigControl runs as administrator, a file that a program without
    /// administrator rights could have put there or could replace is never chosen: that program would get its own
    /// code run as administrator.
    /// </summary>
    internal static string? Choose(IEnumerable<Candidate> candidates, bool elevated, Func<string, bool> exists, Func<string, bool> othersCanChange)
    {
        foreach (var c in candidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(c.Path)) continue;
                if (elevated && c.Place is Place.UserInstall or Place.UserFolder) continue;
                if (!Path.IsPathFullyQualified(c.Path)) continue;
                var full = Path.GetFullPath(c.Path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal) || !exists(full)) continue;
                if (elevated && othersCanChange(full)) continue;
                return full;
            }
            catch
            {
                // malformed path
            }
        }
        return null;
    }

    /// <summary>RigControl's own copy first, then the installs Windows knows about, then the usual install places.</summary>
    internal static List<Candidate> Candidates()
    {
        // RigControl's own copy (unpacked from its exe into its Program Files folder).
        var candidates = new List<Candidate> { new(OwnCopyPath, Place.OwnCopy) };
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\OpenRGB");
            if (service?.GetValue("ImagePath") is string image) candidates.Add(new(ServiceExecutable(image), Place.Service));
        }
        catch
        {
            // not installed as a service
        }
        foreach (var (root, place) in new[] { (Registry.LocalMachine, Place.MachineInstall), (Registry.CurrentUser, Place.UserInstall) })
        {
            foreach (var view in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = root.OpenSubKey(view);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var entry = key.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string display || !display.StartsWith("OpenRGB", StringComparison.OrdinalIgnoreCase)) continue;
                        if (entry.GetValue("InstallLocation") is string location && location.Length > 0)
                            candidates.Add(new(Path.Combine(location, "OpenRGB.exe"), place));
                    }
                }
                catch
                {
                    // unreadable hive: skip
                }
            }
        }
        candidates.Add(new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"), Place.ProgramFiles));
        candidates.Add(new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"), Place.UserFolder));
        return candidates;
    }

    /// <summary>The program in a service's command line: "C:\a b\OpenRGB.exe" --server  ->  C:\a b\OpenRGB.exe.</summary>
    internal static string ServiceExecutable(string imagePath)
    {
        var image = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (image.StartsWith('"'))
        {
            var end = image.IndexOf('"', 1);
            if (end > 0) return image[1..end];
        }
        var i = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? image : image[..(i + 4)];
    }

    public bool IsListening()
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(Host, Port).Wait(400) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    public bool StartServer(string executable)
    {
        if (OwnsServer) return true;
        CheckBeforeStarting(executable);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--server");
        // Only this PC may talk to it: the server has no password, so it must not listen on the network.
        psi.ArgumentList.Add("--server-host");
        psi.ArgumentList.Add(Host);
        psi.ArgumentList.Add("--server-port");
        psi.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _process = Process.Start(psi);
        if (_process is null) return false;
        TieToThisProcess(_process);

        // The port opens before device detection finishes; detection results arrive as "device list changed".
        for (var i = 0; i < 60; i++)
        {
            if (_process.HasExited) return false;
            if (IsListening()) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>The last look at the file before it is run. Throws (in plain words) when it must not be started.</summary>
    internal void CheckBeforeStarting(string executable)
    {
        var full = Path.GetFullPath(executable);
        if (_elevated && _othersCanChange(full))
            throw new IOException("the OpenRGB in " + Path.GetDirectoryName(full) + " sits in a folder that programs without administrator rights can change, so RigControl (which runs as administrator) will not start it. Install OpenRGB for all users, or let RigControl set up its own copy.");
        if (!string.Equals(full, Path.GetFullPath(OwnCopyPath), StringComparison.OrdinalIgnoreCase)) return;
        var verify = _verifyOwnCopy ?? OwnCopyVerifier;
        if (verify is not null && !verify(full))
            throw new IOException("RigControl's own OpenRGB files are not the ones RigControl came with, so they were not started. Restart RigControl to put them back.");
    }

    private void TieToThisProcess(Process child)
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;
            var info = new JobExtendedLimitInformation { BasicLimitInformation = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            var size = Marshal.SizeOf<JobExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ptr, (uint)size))
                    AssignProcessToJobObject(_job, child.Handle);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // Without the job object the child is still stopped in Dispose; only a crash of RigControl would leave it.
        }
    }

    // ---------------------------------------------------------------- SDK client

    public void Connect()
    {
        Disconnect();
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
        if (!tcp.ConnectAsync(Host, Port).Wait(1500))
        {
            tcp.Dispose();
            throw new IOException("OpenRGB is not answering.");
        }
        _tcp = tcp;
        _stream = tcp.GetStream();

        Send(OpenRgbProtocol.ProtocolVersionRequest(OpenRgbProtocol.MaxVersion));
        var reply = WaitFor(OpenRgbProtocol.RequestProtocolVersion, null);
        if (reply.Payload.Length < 4) throw new IOException("OpenRGB sent an unexpected answer.");
        var serverMax = (int)BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload);
        _version = Math.Min(OpenRgbProtocol.MaxVersion, serverMax);
        if (_version < OpenRgbProtocol.MinVersion)
        {
            Disconnect();
            throw new IOException("This OpenRGB is too old. Install OpenRGB 1.0 or newer.");
        }
        Send(OpenRgbProtocol.ClientName("RigControl"));
    }

    public void Disconnect()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
    }

    private NetworkStream Stream => _stream ?? throw new IOException("Not connected to OpenRGB.");

    private void Send(byte[] packet) => Stream.Write(packet, 0, packet.Length);

    private void ReadExactly(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = Stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new IOException("OpenRGB closed the connection.");
            read += n;
        }
    }

    private OpenRgbPacket ReadPacket()
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        ReadExactly(header);
        if (!OpenRgbProtocol.TryParseHeader(header, out var device, out var id, out var size))
            throw new IOException("OpenRGB sent data that is not its protocol.");
        var payload = new byte[size];
        if (size > 0) ReadExactly(payload);
        if (id == OpenRgbProtocol.DeviceListUpdated) _listChanged = true;
        return new OpenRgbPacket(device, id, payload);
    }

    /// <summary>Reads until the reply with this id (and device, when given) arrives; anything else is skipped.</summary>
    private OpenRgbPacket WaitFor(uint id, uint? device)
    {
        for (var i = 0; i < 64; i++)
        {
            var p = ReadPacket();
            if (p.Id == id && (device is null || p.DeviceId == device)) return p;
        }
        throw new IOException("OpenRGB did not answer the request.");
    }

    public IReadOnlyList<RgbController> ReadControllers()
    {
        Send(OpenRgbProtocol.ControllerCountRequest());
        var countReply = WaitFor(OpenRgbProtocol.RequestControllerCount, null);
        var count = countReply.Payload.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(countReply.Payload) : 0;
        if (count is < 0 or > 256) throw new IOException("OpenRGB reported an implausible number of devices.");
        var list = new List<RgbController>(count);
        for (var i = 0; i < count; i++)
        {
            Send(OpenRgbProtocol.ControllerDataRequest(i, _version));
            var reply = WaitFor(OpenRgbProtocol.RequestControllerData, (uint)i);
            list.Add(OpenRgbProtocol.ParseController(reply.Payload, _version, i));
        }
        return list;
    }

    public void UpdateMode(int device, int modeIndex, RgbMode mode) => Send(OpenRgbProtocol.UpdateModePacket(device, modeIndex, mode, _version));

    public void UpdateLeds(int device, IReadOnlyList<uint> colors) => Send(OpenRgbProtocol.UpdateLedsPacket(device, colors));

    public void UpdateZoneLeds(int device, int zone, IReadOnlyList<uint> colors) => Send(OpenRgbProtocol.UpdateZoneLedsPacket(device, zone, colors));

    public void ResizeZone(int device, int zone, int leds) => Send(OpenRgbProtocol.ResizeZonePacket(device, zone, leds));

    public bool DeviceListChanged()
    {
        try
        {
            while (Connected && Stream.DataAvailable) ReadPacket();
        }
        catch
        {
            Disconnect();
        }
        var changed = _listChanged;
        _listChanged = false;
        return changed;
    }

    public void Dispose()
    {
        Disconnect();
        try
        {
            if (_process is { HasExited: false })
            {
                // There is no "please stop" in the SDK; lights keep the last colours they were sent.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // already gone
        }
        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Answers one question about a file: could a program WITHOUT administrator rights replace it, or the folders it sits
/// in? Looks at who owns each of them and who Windows lets write there. Anything that can't be read counts as "yes".
/// </summary>
internal static class OrdinaryUsers
{
    // Write, append, delete, change permissions, take ownership (and the "generic" forms of the same).
    private const int ChangeRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;
    // On the drive's top folder only "delete what is inside" and the permission rights matter.
    private const int RootRights = 0x40 | 0x40000 | 0x80000 | 0x10000000;

    private static readonly SecurityIdentifier[] Trusted =
    {
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    };

    public static bool CanChange(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            // Only NTFS and ReFS keep permissions at all.
            var format = new DriveInfo(root).DriveFormat;
            if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !format.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) return true;

            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || Open(info.GetAccessControl(), ChangeRights, checkOwner: true)) return true;
            }
            for (var dir = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full).Directory; dir is not null; dir = dir.Parent)
            {
                var top = dir.Parent is null;
                // A link could point the whole path somewhere else.
                if (!top && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                if (Open(dir.GetAccessControl(), top ? RootRights : ChangeRights, checkOwner: !top)) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when the owner, or anyone Windows allows one of these rights, is not SYSTEM, Administrators or TrustedInstaller.</summary>
    internal static bool Open(FileSystemSecurity security, int rights, bool checkOwner)
    {
        if (checkOwner && (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner))) return true;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // only for things created inside later
            if (((int)rule.FileSystemRights & rights) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Trusted.Contains(sid)) continue;
            return true;
        }
        return false;
    }
}
