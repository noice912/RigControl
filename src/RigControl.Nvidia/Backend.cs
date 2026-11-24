using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using NvAPIWrapper;
using NvAPIWrapper.DRS;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.General;
using RigControl.Core.Rpc;

namespace RigControl.Nvidia;

/// <param name="Chip">The chip's own name from RigControl's built-in card list (e.g. "AD104"); null when the list has none.</param>
/// <param name="Family">The card's family from the built-in list (e.g. "GeForce RTX 40 series"); null when not known.</param>
/// <param name="Architecture">The chip generation from the built-in list (e.g. "Ada Lovelace"); null when not known.</param>
public sealed record GpuInfo(int Index, string Name, int? VramMB, string? BusId,
    string? Chip = null, string? Family = null, string? Architecture = null);

public sealed record DriverInfo(bool Available, string? Version, string? Branch, IReadOnlyList<GpuInfo> Gpus, string? Problem);

public sealed record PowerReading(double? PowerW, double? LimitW, double? DefaultLimitW, double? MinLimitW, double? MaxLimitW);

/// <summary>Clock offsets in MHz as the driver reports them (NVML, performance state 0). Null when not reported.</summary>
public sealed record ClockOffsetReading(int? CoreMHz, int? CoreMin, int? CoreMax, int? MemoryMHz, int? MemoryMin, int? MemoryMax);

public sealed record ModeInfo(int Width, int Height, int RefreshHz);

public sealed record VibranceInfo(int Value, int Min, int Max, int DefaultValue);

public sealed record DisplayInfo(string Id, string Name, bool Primary, int Width, int Height, int RefreshHz,
    IReadOnlyList<ModeInfo> Modes, VibranceInfo? Vibrance);

public sealed record ProfileInfo(string Name, IReadOnlyList<string> Programs, bool IsPredefined, int ModifiedCount);

public sealed record LatestDriver(string Version, string? DetailsUrl, string? DownloadUrl = null);

/// <summary>Everything the module needs from the NVIDIA driver and Windows. Abstracted so tests never touch the real driver.</summary>
public interface INvidiaBackend : IDisposable
{
    DriverInfo Initialize();
    PowerReading GetPower();
    void SetPowerLimit(double watts);
    ClockOffsetReading GetClockOffsets();
    /// <summary>Sets the core (memory = false) or memory clock offset in MHz for performance state 0.</summary>
    void SetClockOffset(bool memory, int mhz);
    bool IsSettingKnown(uint id);
    /// <summary>The value set in the profile (null = global), or null when the profile leaves it at the driver default.</summary>
    uint? ReadSetting(string? profile, uint id);
    void WriteSetting(string? profile, uint id, uint value);
    void ResetSetting(string? profile, uint id);
    IReadOnlyList<ProfileInfo> ListProfiles(string? query, int max);
    ProfileInfo AddProgram(string exePath);
    IReadOnlyList<DisplayInfo> GetDisplays();
    /// <summary>Asks Windows whether the mode would be accepted, without changing anything.</summary>
    bool TestMode(string displayId, ModeInfo mode);
    /// <summary>Switches mode without saving it: a restart or <see cref="RevertMode"/> brings the saved mode back.</summary>
    void ApplyModeTemporarily(string displayId, ModeInfo mode);
    void KeepMode(string displayId, ModeInfo mode);
    void RevertMode(string displayId);
    void SetVibrance(string displayId, int value);
    Task<LatestDriver> GetLatestDriverAsync(CancellationToken ct);
}

/// <summary>
/// Real implementation: NvAPIWrapper (driver settings, GPU identity, digital vibrance), nvml.dll (power) and
/// Win32 (display modes). See docs/protocols/nvidia.md. All calls are serialised by the owning module.
/// </summary>
public sealed class NvidiaBackend : INvidiaBackend
{
    private readonly string _backupPath;
    private bool _nvapi;
    private IntPtr _nvmlDevice;
    private bool _nvml;
    private IReadOnlyList<string> _gpuNames = Array.Empty<string>();
    private bool? _laptop;
    private IReadOnlyList<RigControl.Core.Hardware.GpuInfo> _listed = Array.Empty<RigControl.Core.Hardware.GpuInfo>();
    private DriverLookup.Ids? _lookupIds;

    public NvidiaBackend(string dataDirectory)
    {
        _backupPath = Path.Combine(dataDirectory, "nvidia-settings-before-rigcontrol.bin");
    }

    // ---------------------------------------------------------------- identity

    public DriverInfo Initialize()
    {
        try
        {
            NVIDIA.Initialize();
            _nvapi = true;
        }
        catch (Exception ex)
        {
            return new DriverInfo(false, null, null, Array.Empty<GpuInfo>(), "The NVIDIA driver did not respond: " + ex.Message);
        }

        var gpus = new List<GpuInfo>();
        var listed = new List<RigControl.Core.Hardware.GpuInfo>();
        try
        {
            var i = 0;
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                int? vram = null;
                string? bus = null;
                try { vram = (int)(gpu.MemoryInformation.DedicatedVideoMemoryInkB / 1024); } catch { /* optional */ }
                try { bus = gpu.BusInformation.BusId.ToString(CultureInfo.InvariantCulture); } catch { /* optional */ }
                // What RigControl's built-in card list says about this card (by its PCI ids). Nothing is known: nulls.
                RigControl.Core.Hardware.GpuInfo? known = null;
                try
                {
                    var pci = gpu.BusInformation.PCIIdentifiers;
                    // The driver gives the device id either alone or with the maker's id in the low half.
                    var device = pci.DeviceId > 0xFFFF ? (int)(pci.DeviceId >> 16) : (int)pci.DeviceId;
                    var found = RigControl.Core.Hardware.GpuDatabase.Lookup(pci.VendorId, device, gpu.FullName);
                    if (found.Known) known = found;
                }
                catch { /* optional */ }
                if (known is not null) listed.Add(known);
                gpus.Add(new GpuInfo(i++, gpu.FullName, vram, bus, known?.Chip, known?.Family, known?.Architecture));
                try
                {
                    // Only used to tell a laptop card from a desktop card of the same name when looking for drivers.
                    var kind = gpu.SystemType;
                    if (kind == NvAPIWrapper.Native.GPU.SystemType.Laptop) _laptop = true;
                    else if (kind == NvAPIWrapper.Native.GPU.SystemType.Desktop) _laptop ??= false;
                }
                catch { /* unknown */ }
            }
            _gpuNames = gpus.Select(g => g.Name).ToList();
            _listed = listed;
            _lookupIds = null;
        }
        catch (Exception ex)
        {
            return new DriverInfo(false, null, null, Array.Empty<GpuInfo>(), "No NVIDIA graphics card was found: " + ex.Message);
        }

        string? version = null, branch = null;
        try
        {
            var v = NVIDIA.DriverVersion; // e.g. 62124 => "621.24"
            version = $"{v / 100}.{v % 100:00}";
            branch = NVIDIA.DriverBranchVersion;
        }
        catch
        {
            // shown as unknown
        }

        InitNvml();
        return new DriverInfo(gpus.Count > 0, version, branch, gpus, gpus.Count > 0 ? null : "No NVIDIA graphics card was found.");
    }

    // ---------------------------------------------------------------- power (NVML)

    private static class Nvml
    {
        private static IntPtr _lib;

        public static bool Load()
        {
            if (_lib != IntPtr.Zero) return true;
            // Full path: never let a stray nvml.dll elsewhere on the search path be loaded.
            return NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out _lib);
        }

        private static T Fn<T>(string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int HandleByIndex(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetUInt(IntPtr device, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetTwoUInt(IntPtr device, out uint a, out uint b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetUInt(IntPtr device, uint value);

        public static int Init() => Fn<NoArgs>("nvmlInit_v2")();
        public static int Shutdown() => Fn<NoArgs>("nvmlShutdown")();
        public static int Handle(uint index, out IntPtr device) => Fn<HandleByIndex>("nvmlDeviceGetHandleByIndex_v2")(index, out device);
        public static int PowerUsage(IntPtr d, out uint mw) => Fn<GetUInt>("nvmlDeviceGetPowerUsage")(d, out mw);
        public static int PowerLimit(IntPtr d, out uint mw) => Fn<GetUInt>("nvmlDeviceGetPowerManagementLimit")(d, out mw);
        public static int DefaultLimit(IntPtr d, out uint mw) => Fn<GetUInt>("nvmlDeviceGetPowerManagementDefaultLimit")(d, out mw);
        public static int Constraints(IntPtr d, out uint min, out uint max) => Fn<GetTwoUInt>("nvmlDeviceGetPowerManagementLimitConstraints")(d, out min, out max);
        public static int SetLimit(IntPtr d, uint mw) => Fn<SetUInt>("nvmlDeviceSetPowerManagementLimit")(d, mw);

        /// <summary>nvmlClockOffset_v1_t (nvml.h): version, clock type, pstate, offset, min, max.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ClockOffset
        {
            public uint Version;
            public int Type;
            public int Pstate;
            public int OffsetMHz;
            public int MinMHz;
            public int MaxMHz;
        }

        /// <summary>NVML_STRUCT_VERSION(ClockOffset, 1) = sizeof | (1 &lt;&lt; 24).</summary>
        public static uint ClockOffsetVersion => (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24);
        public const int ClockGraphics = 0, ClockMemory = 2, Pstate0 = 0;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OffsetFn(IntPtr device, ref ClockOffset info);
        public static int GetOffsets(IntPtr d, ref ClockOffset o) => Fn<OffsetFn>("nvmlDeviceGetClockOffsets")(d, ref o);
        public static int SetOffsets(IntPtr d, ref ClockOffset o) => Fn<OffsetFn>("nvmlDeviceSetClockOffsets")(d, ref o);
    }

    private void InitNvml()
    {
        try
        {
            if (!Nvml.Load() || Nvml.Init() != 0) return;
            _nvml = Nvml.Handle(0, out _nvmlDevice) == 0;
            if (!_nvml) Nvml.Shutdown();
        }
        catch
        {
            _nvml = false;
        }
    }

    public PowerReading GetPower()
    {
        if (!_nvml) return new PowerReading(null, null, null, null, null);
        double? W(int rc, uint mw) => rc == 0 ? Math.Round(mw / 1000.0, 1) : null;
        var usage = W(Nvml.PowerUsage(_nvmlDevice, out var u), u);
        var limit = W(Nvml.PowerLimit(_nvmlDevice, out var l), l);
        var def = W(Nvml.DefaultLimit(_nvmlDevice, out var d), d);
        var rc = Nvml.Constraints(_nvmlDevice, out var min, out var max);
        return new PowerReading(usage, limit, def, W(rc, min), W(rc, max));
    }

    public void SetPowerLimit(double watts)
    {
        if (!_nvml) throw RpcException.Unavailable("The graphics driver does not report power settings.");
        var rc = Nvml.SetLimit(_nvmlDevice, (uint)Math.Round(watts * 1000));
        if (rc == 4) throw RpcException.NeedsAdmin("Changing the power limit needs administrator rights.");
        if (rc == 3) throw RpcException.Unavailable("This graphics card does not allow its power limit to be changed.");
        if (rc != 0) throw RpcException.Unavailable("The graphics driver refused the power limit (code " + rc + ").");
    }

    // ---------------------------------------------------------------- clock offsets (NVML)

    private (int? Cur, int? Min, int? Max) ReadOffset(int type)
    {
        var o = new Nvml.ClockOffset { Version = Nvml.ClockOffsetVersion, Type = type, Pstate = Nvml.Pstate0 };
        try
        {
            return Nvml.GetOffsets(_nvmlDevice, ref o) == 0 ? (o.OffsetMHz, o.MinMHz, o.MaxMHz) : (null, null, null);
        }
        catch (EntryPointNotFoundException)
        {
            return (null, null, null); // driver too old for nvmlDeviceGetClockOffsets
        }
    }

    public ClockOffsetReading GetClockOffsets()
    {
        if (!_nvml) return new ClockOffsetReading(null, null, null, null, null, null);
        var core = ReadOffset(Nvml.ClockGraphics);
        var mem = ReadOffset(Nvml.ClockMemory);
        return new ClockOffsetReading(core.Cur, core.Min, core.Max, mem.Cur, mem.Min, mem.Max);
    }

    public void SetClockOffset(bool memory, int mhz)
    {
        if (!_nvml) throw RpcException.Unavailable("The graphics driver does not report clock settings.");
        var o = new Nvml.ClockOffset
        {
            Version = Nvml.ClockOffsetVersion,
            Type = memory ? Nvml.ClockMemory : Nvml.ClockGraphics,
            Pstate = Nvml.Pstate0,
            OffsetMHz = mhz,
        };
        int rc;
        try { rc = Nvml.SetOffsets(_nvmlDevice, ref o); }
        catch (EntryPointNotFoundException) { throw RpcException.Unavailable("This graphics driver is too old for clock offsets. Update it on the Graphics page."); }
        if (rc == 4) throw RpcException.NeedsAdmin("Changing clock offsets needs administrator rights.");
        if (rc == 3) throw RpcException.Unavailable("This graphics card does not allow its clocks to be changed.");
        if (rc == 2) throw RpcException.Unsafe("The graphics driver refused that offset as out of range.");
        if (rc != 0) throw RpcException.Unavailable("The graphics driver refused the clock offset (code " + rc + ").");
    }

    // ---------------------------------------------------------------- driver settings (DRS)

    private static T Drs<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (NVIDIAApiException ex) when (ex.Status is Status.InvalidUserPrivilege or Status.AccessDenied)
        {
            throw RpcException.NeedsAdmin("The graphics driver only lets an administrator change this. Restart RigControl as administrator.");
        }
        catch (NVIDIAApiException ex)
        {
            throw RpcException.Unavailable("The graphics driver refused: " + ex.Status);
        }
    }

    private static DriverSettingsProfile Profile(DriverSettingsSession session, string? name)
    {
        if (name is null) return session.CurrentGlobalProfile ?? throw RpcException.Unavailable("The driver has no global profile.");
        try
        {
            return session.FindProfileByName(name) ?? throw RpcException.InvalidArgs($"There is no profile called \"{name}\".");
        }
        catch (NVIDIAApiException ex) when (ex.Status == Status.ProfileNotFound)
        {
            throw RpcException.InvalidArgs($"There is no profile called \"{name}\".");
        }
    }

    public bool IsSettingKnown(uint id)
    {
        if (!_nvapi) return false;
        try
        {
            var info = SettingInfo.FromId(id);
            return info is { IsAvailable: true } && !string.IsNullOrWhiteSpace(info.Name);
        }
        catch
        {
            return false;
        }
    }

    public uint? ReadSetting(string? profile, uint id) => Drs(() =>
    {
        using var session = DriverSettingsSession.CreateAndLoad();
        var setting = Profile(session, profile).GetSetting(id);
        return setting?.CurrentValue is uint value ? value : (uint?)null;
    });

    private void BackupOnce(DriverSettingsSession session)
    {
        try
        {
            if (!File.Exists(_backupPath)) session.Save(_backupPath);
        }
        catch
        {
            // A missing backup must not block the user's change; the driver's own "restore defaults" still exists.
        }
    }

    public void WriteSetting(string? profile, uint id, uint value) => Drs(() =>
    {
        using var session = DriverSettingsSession.CreateAndLoad();
        BackupOnce(session);
        Profile(session, profile).SetSetting(id, value);
        session.Save();
        return true;
    });

    public void ResetSetting(string? profile, uint id) => Drs(() =>
    {
        using var session = DriverSettingsSession.CreateAndLoad();
        BackupOnce(session);
        var p = Profile(session, profile);
        if (p.GetSetting(id) is null) return true; // already at the default
        try
        {
            p.RestoreSettingToDefault(id);
        }
        catch (NVIDIAApiException ex) when (ex.Status == Status.SettingNotFound)
        {
            return true;
        }
        session.Save();
        return true;
    });

    private static ProfileInfo Describe(DriverSettingsProfile p)
    {
        var programs = new List<string>();
        var modified = 0;
        try { programs = p.Applications.Select(a => a.ApplicationName).Where(n => !string.IsNullOrEmpty(n)).Take(5).ToList(); } catch { /* optional */ }
        try { modified = p.Settings.Count(s => !s.IsCurrentValuePredefined); } catch { /* optional */ }
        return new ProfileInfo(p.Name, programs, p.IsPredefined, modified);
    }

    public IReadOnlyList<ProfileInfo> ListProfiles(string? query, int max) => Drs(() =>
    {
        using var session = DriverSettingsSession.CreateAndLoad();
        var q = (query ?? string.Empty).Trim();
        var result = new List<ProfileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // An exact program name ("cs2.exe") finds its profile directly.
        if (q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (session.FindApplicationProfile(q) is { } byExe && seen.Add(byExe.Name)) result.Add(Describe(byExe));
            }
            catch
            {
                // not found
            }
        }
        foreach (var p in session.Profiles)
        {
            if (result.Count >= max) break;
            if (q.Length > 0 && p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (seen.Add(p.Name)) result.Add(Describe(p));
        }
        return (IReadOnlyList<ProfileInfo>)result;
    });

    public ProfileInfo AddProgram(string exePath) => Drs(() =>
    {
        using var session = DriverSettingsSession.CreateAndLoad();
        var existing = session.FindApplicationProfile(exePath);
        if (existing is not null) return Describe(existing);

        BackupOnce(session);
        var exe = Path.GetFileName(exePath).ToLowerInvariant();
        var name = "RigControl - " + Path.GetFileNameWithoutExtension(exePath);
        DriverSettingsProfile profile;
        try
        {
            profile = session.FindProfileByName(name);
        }
        catch (NVIDIAApiException)
        {
            profile = null!;
        }
        profile ??= DriverSettingsProfile.CreateProfile(session, name);
        ProfileApplication.CreateApplication(profile, exe);
        session.Save();
        return Describe(profile);
    });

    // ---------------------------------------------------------------- displays (Win32 + digital vibrance)

    private const int EnumCurrentSettings = -1;
    private const int CdsTest = 0x2;
    private const int CdsUpdateRegistry = 0x1;
    private const int DisplayAttached = 0x1;
    private const int DisplayPrimary = 0x4;
    private const int DmInterlaced = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, int index, ref DisplayDevice info, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DevMode info, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DevMode mode, IntPtr hwnd, int flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsEx")]
    private static extern int ChangeDisplaySettingsExReset(string device, IntPtr mode, IntPtr hwnd, int flags, IntPtr param);

    private static DevMode NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    private static string Gdi(string displayId) => @"\\.\" + displayId;

    private static IEnumerable<DevMode> Modes(string gdiName)
    {
        for (var i = 0; ; i++)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsEx(gdiName, i, ref dm, 0)) yield break;
            if (dm.dmBitsPerPel == 32 && (dm.dmDisplayFlags & DmInterlaced) == 0) yield return dm;
        }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        for (var i = 0; ; i++)
        {
            var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DisplayAttached) == 0) continue;

            var current = NewDevMode();
            if (!EnumDisplaySettingsEx(dd.DeviceName, EnumCurrentSettings, ref current, 0)) continue;

            var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            var name = EnumDisplayDevices(dd.DeviceName, 0, ref monitor, 0) ? monitor.DeviceString : null;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Generic", StringComparison.OrdinalIgnoreCase))
                name = "Display " + (list.Count + 1);

            var modes = Modes(dd.DeviceName)
                .Select(m => new ModeInfo(m.dmPelsWidth, m.dmPelsHeight, m.dmDisplayFrequency))
                .Distinct()
                .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.RefreshHz)
                .ToList();

            VibranceInfo? vibrance = null;
            if (_nvapi)
            {
                try
                {
                    var dvc = new NvAPIWrapper.Display.Display(dd.DeviceName).DigitalVibranceControl;
                    vibrance = new VibranceInfo(dvc.CurrentLevel, dvc.MinimumLevel, dvc.MaximumLevel, dvc.DefaultLevel);
                }
                catch
                {
                    // Not an NVIDIA output, or the driver does not offer it.
                }
            }

            list.Add(new DisplayInfo(dd.DeviceName.Replace(@"\\.\", string.Empty), name, (dd.StateFlags & DisplayPrimary) != 0,
                current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency, modes, vibrance));
        }
        return list;
    }

    private static DevMode Find(string displayId, ModeInfo mode)
    {
        foreach (var dm in Modes(Gdi(displayId)))
        {
            if (dm.dmPelsWidth == mode.Width && dm.dmPelsHeight == mode.Height && dm.dmDisplayFrequency == mode.RefreshHz) return dm;
        }
        throw RpcException.InvalidArgs("That display does not offer this mode.");
    }

    public bool TestMode(string displayId, ModeInfo mode)
    {
        var dm = Find(displayId, mode);
        return ChangeDisplaySettingsEx(Gdi(displayId), ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero) == 0;
    }

    public void ApplyModeTemporarily(string displayId, ModeInfo mode)
    {
        var dm = Find(displayId, mode);
        var rc = ChangeDisplaySettingsEx(Gdi(displayId), ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        if (rc != 0) throw RpcException.Unavailable("Windows refused the display mode (code " + rc + ").");
    }

    public void KeepMode(string displayId, ModeInfo mode)
    {
        var dm = Find(displayId, mode);
        var rc = ChangeDisplaySettingsEx(Gdi(displayId), ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (rc != 0) throw RpcException.Unavailable("Windows could not save the display mode (code " + rc + ").");
    }

    public void RevertMode(string displayId)
    {
        // A null mode tells Windows to go back to the saved (registry) settings for this display.
        ChangeDisplaySettingsExReset(Gdi(displayId), IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    public void SetVibrance(string displayId, int value)
    {
        if (!_nvapi) throw RpcException.Unavailable("The NVIDIA driver is not available.");
        Drs(() =>
        {
            var dvc = new NvAPIWrapper.Display.Display(Gdi(displayId)).DigitalVibranceControl;
            if (value < dvc.MinimumLevel || value > dvc.MaximumLevel)
                throw RpcException.InvalidArgs($"Vibrance must be between {dvc.MinimumLevel} and {dvc.MaximumLevel}.");
            dvc.CurrentLevel = value;
            return true;
        });
    }

    // ---------------------------------------------------------------- driver update check

    /// <summary>
    /// NVIDIA's newest Game Ready driver for the card and Windows of this PC (see <see cref="DriverLookup"/>).
    /// Throws <see cref="DriverLookupException"/>, with the reason in plain words, when RigControl can't be sure which
    /// driver fits: then none is offered.
    /// </summary>
    public async Task<LatestDriver> GetLatestDriverAsync(CancellationToken ct)
    {
        if (_gpuNames.Count == 0) throw new DriverLookupException("No NVIDIA graphics card was found, so there is no driver to look for.");
        // A card the built-in list knows is not a GeForce card is refused here, before anything is fetched.
        DriverLookup.RefuseOtherFamilies(_listed);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        async Task<string> Fetch(string url, CancellationToken token)
        {
            using var response = await http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        var ids = _lookupIds ??= await DriverLookup.ResolveAsync(_gpuNames, _laptop, Environment.OSVersion.Version,
            RuntimeInformation.OSArchitecture, Fetch, ct).ConfigureAwait(false);
        return DriverLookup.ParseAnswer(await Fetch(DriverLookup.Url(ids), ct).ConfigureAwait(false), ids);
    }

    public void Dispose()
    {
        try { if (_nvml) Nvml.Shutdown(); } catch { /* ignore */ }
        _nvml = false;
        try { if (_nvapi) NVIDIA.Unload(); } catch { /* ignore */ }
        _nvapi = false;
    }
}
