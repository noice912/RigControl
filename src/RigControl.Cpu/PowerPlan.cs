using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RigControl.Cpu;

/// <summary>Windows refused or failed a power plan call. <see cref="AccessDenied"/> means it wants administrator rights.</summary>
public sealed class PowerPlanException : Exception
{
    public uint Code { get; }
    public bool AccessDenied => Code == 5;

    public PowerPlanException(string what, uint code)
        : base($"{what}: {new Win32Exception((int)code).Message} (Windows error {code})")
    {
        Code = code;
    }
}

/// <summary>
/// Windows' power plans, as far as the CPU page needs them. Values are the plain numbers Windows stores (a percentage,
/// MHz, or the number of a choice). Behind an interface so the logic is tested with a fake: tests never touch the real
/// power plan.
/// </summary>
public interface IPowerPlan
{
    /// <summary>The plan in use now.</summary>
    Guid ActiveScheme();

    /// <summary>Every plan on this PC.</summary>
    IReadOnlyList<Guid> Schemes();

    /// <summary>The plan's name as Windows shows it ("Balanced"), or null.</summary>
    string? SchemeName(Guid scheme);

    /// <summary>True when this PC has a battery, so the "on battery" values matter.</summary>
    bool HasBattery();

    /// <summary>The value used while plugged in. Null when Windows on this PC does not have the setting.</summary>
    uint? ReadAc(Guid scheme, Guid setting);

    /// <summary>The value used on battery. Null when Windows on this PC does not have the setting.</summary>
    uint? ReadDc(Guid scheme, Guid setting);

    void WriteAc(Guid scheme, Guid setting, uint value);
    void WriteDc(Guid scheme, Guid setting, uint value);

    /// <summary>Makes Windows use the plan's values now (needed after a write to the plan in use).</summary>
    void Apply(Guid scheme);
}

/// <summary>
/// The real thing, through Windows' documented power functions (powrprof.dll). No command output is parsed, so it
/// works the same in every Windows language. Only processor settings are ever read or written.
/// </summary>
public sealed class WindowsPowerPlan : IPowerPlan
{
    /// <summary>"Processor power management", the group all the settings used here belong to.</summary>
    public static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");

    private const uint Success = 0;
    private const uint FileNotFound = 2;
    private const uint MoreData = 234;
    private const uint NoMoreItems = 259;
    private const int AccessScheme = 16;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting, [Out] byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroup, int accessFlags, uint index, [Out] byte[] buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public Guid ActiveScheme()
    {
        var code = PowerGetActiveScheme(IntPtr.Zero, out var p);
        if (code != Success || p == IntPtr.Zero) throw new PowerPlanException("Windows did not say which power plan is in use", code);
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public IReadOnlyList<Guid> Schemes()
    {
        var list = new List<Guid>();
        var buffer = new byte[16];
        for (uint i = 0; i < 256; i++)
        {
            uint size = 16;
            var code = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size);
            if (code == NoMoreItems) break;
            if (code != Success) throw new PowerPlanException("Windows did not list its power plans", code);
            list.Add(new Guid(buffer));
        }
        return list;
    }

    public string? SchemeName(Guid scheme)
    {
        uint size = 0;
        var code = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if ((code != Success && code != MoreData) || size == 0 || size > 4096) return null;
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != Success) return null;
        var name = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
        var end = name.IndexOf('\0');
        if (end >= 0) name = name[..end];
        return name.Length == 0 ? null : name;
    }

    public bool HasBattery()
    {
        // 128 = "no system battery", 255 = unknown: both count as no battery.
        return GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255;
    }

    public uint? ReadAc(Guid scheme, Guid setting)
    {
        var sub = ProcessorSubgroup;
        var code = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value);
        if (code == Success) return value;
        if (code == FileNotFound) return null;
        throw new PowerPlanException("Windows did not give a power plan value", code);
    }

    public uint? ReadDc(Guid scheme, Guid setting)
    {
        var sub = ProcessorSubgroup;
        var code = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value);
        if (code == Success) return value;
        if (code == FileNotFound) return null;
        throw new PowerPlanException("Windows did not give a power plan value", code);
    }

    public void WriteAc(Guid scheme, Guid setting, uint value)
    {
        var sub = ProcessorSubgroup;
        var code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (code != Success) throw new PowerPlanException("Windows did not save the power plan value", code);
    }

    public void WriteDc(Guid scheme, Guid setting, uint value)
    {
        var sub = ProcessorSubgroup;
        var code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (code != Success) throw new PowerPlanException("Windows did not save the power plan value", code);
    }

    public void Apply(Guid scheme)
    {
        var code = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (code != Success) throw new PowerPlanException("Windows did not apply the power plan", code);
    }
}
