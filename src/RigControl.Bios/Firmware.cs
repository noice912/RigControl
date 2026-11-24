using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RigControl.Bios;

public sealed record FirmwareVariable(string Name, Guid Vendor);

/// <summary>UEFI variable access. Abstracted so tests never touch the real firmware.</summary>
public interface IFirmware
{
    /// <summary>False when Windows was not started in UEFI mode or the privilege could not be enabled.</summary>
    bool Available { get; }
    string? Problem { get; }
    IReadOnlyList<FirmwareVariable> List();
    /// <summary>The variable's bytes and attributes, or null when it does not exist.</summary>
    (byte[] Data, uint Attributes)? Read(string name, Guid vendor);
    /// <summary>Writes the whole variable. Returns null when Windows accepted it, otherwise why not.</summary>
    string? Write(string name, Guid vendor, byte[] data, uint attributes);
}

/// <summary>
/// Real UEFI variables through Windows (GetFirmwareEnvironmentVariableEx, NtEnumerateSystemEnvironmentValuesEx).
/// Needs administrator rights and SeSystemEnvironmentPrivilege, which this enables for the process.
/// This class only reads.
/// </summary>
public sealed class WindowsFirmware : IFirmware
{
    public bool Available { get; }
    public string? Problem { get; }

    public WindowsFirmware()
    {
        try
        {
            if (!EnablePrivilege("SeSystemEnvironmentPrivilege"))
            {
                Problem = "Windows did not give RigControl access to the BIOS settings. Run RigControl as administrator.";
                return;
            }
            // A legacy (non-UEFI) boot answers every read with ERROR_INVALID_FUNCTION.
            GetFirmwareEnvironmentVariableExW("", "{00000000-0000-0000-0000-000000000000}", IntPtr.Zero, 0, out _);
            if (Marshal.GetLastWin32Error() == 1)
            {
                Problem = "Windows was not started in UEFI mode, so BIOS settings cannot be read from Windows.";
                return;
            }
            Available = true;
        }
        catch (Exception ex)
        {
            Problem = "BIOS settings cannot be read: " + ex.Message;
        }
    }

    public IReadOnlyList<FirmwareVariable> List()
    {
        uint length = 0;
        NtEnumerateSystemEnvironmentValuesEx(1, IntPtr.Zero, ref length);
        if (length == 0) return Array.Empty<FirmwareVariable>();
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            var status = NtEnumerateSystemEnvironmentValuesEx(1, buffer, ref length);
            if (status != 0) throw new Win32Exception($"Listing BIOS variables failed (NTSTATUS 0x{status:X8}).");
            var list = new List<FirmwareVariable>();
            var offset = 0;
            while (true)
            {
                var entry = buffer + offset;
                var next = Marshal.ReadInt32(entry);
                var guid = Marshal.PtrToStructure<Guid>(entry + 4);
                var name = Marshal.PtrToStringUni(entry + 20) ?? "";
                list.Add(new FirmwareVariable(name, guid));
                if (next == 0) break;
                offset += next;
                if (offset >= length) break;
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public (byte[] Data, uint Attributes)? Read(string name, Guid vendor)
    {
        var size = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var n = GetFirmwareEnvironmentVariableExW(name, vendor.ToString("B"), buffer, (uint)size, out var attrs);
            if (n == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == 203) return null; // ERROR_ENVVAR_NOT_FOUND
                throw new Win32Exception(err);
            }
            var data = new byte[n];
            Marshal.Copy(buffer, data, 0, (int)n);
            return (data, attrs);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public string? Write(string name, Guid vendor, byte[] data, uint attributes)
    {
        var buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            if (SetFirmwareEnvironmentVariableExW(name, vendor.ToString("B"), buffer, (uint)data.Length, attributes)) return null;
            var err = Marshal.GetLastWin32Error();
            return err switch
            {
                5 => "the BIOS refused the change (access denied)",
                19 => "the BIOS keeps these settings write-protected from Windows",
                1314 => "Windows did not give RigControl the right to change BIOS settings",
                _ => new Win32Exception(err).Message + $" (error {err})",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------------------------------------------------------- native

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFirmwareEnvironmentVariableExW(string lpName, string lpGuid, IntPtr pValue, uint nSize, uint dwAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableExW(string lpName, string lpGuid, IntPtr pBuffer, uint nSize, out uint pdwAttributes);

    [DllImport("ntdll.dll")]
    private static extern int NtEnumerateSystemEnvironmentValuesEx(uint informationClass, IntPtr buffer, ref uint bufferLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint length, IntPtr prev, IntPtr prevLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED = 1300
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
