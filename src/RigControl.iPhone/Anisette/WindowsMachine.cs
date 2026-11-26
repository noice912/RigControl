using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RigControl.iPhone.Anisette;

/// <summary>
/// How Apple's own Windows apps describe this PC to Apple's servers: the iTunes-style machine identifier (seven
/// hardware facts, each MD5-hashed down to 8 hex digits) and the Windows version text. Apple's CoreADI on Windows
/// keeps its provisioning under this identity, so the headers must say the same.
/// After the description in Dadoum/apple-crates (library-coreadi/src/windows.rs); our own code.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsMachine
{
    public static string DeviceIdentifier()
    {
        byte[]? Reg(string path, string name)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) switch
            {
                string s => Encoding.Latin1.GetBytes(s + "\0"),
                string[] arr => Encoding.Latin1.GetBytes(string.Join("\0", arr) + "\0\0"),
                byte[] b => b,
                _ => null,
            };
        }

        var parts = new[]
        {
            FirstMac(),
            VolumeSerial(),
            Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId"),
            Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"),
            Reg(@"HARDWARE\DESCRIPTION\System", "SystemBiosVersion"),
            ComputerName(),
            HardwareProfile(),
        };
        return Format(parts);
    }

    /// <summary>Each part MD5-hashed, first 4 bytes as upper-case hex, joined with dots; a missing part is 00000000.</summary>
    internal static string Format(IReadOnlyList<byte[]?> parts) =>
        string.Join('.', parts.Select(p => p is null ? "00000000" : Convert.ToHexString(MD5.HashData(p).AsSpan(0, 4))));

    private static byte[]? FirstMac()
    {
        // iTunes takes the first adapter Windows lists, including ones that are down.
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault();
        var mac = nic?.GetPhysicalAddress().GetAddressBytes();
        if (mac is null) return null;
        var six = new byte[6];
        mac.AsSpan(0, Math.Min(6, mac.Length)).CopyTo(six);
        return six;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, nint name, uint nameSize, out uint serial,
        out uint maxComponent, out uint flags, nint fs, uint fsSize);

    private static byte[]? VolumeSerial() =>
        GetVolumeInformationW(@"C:\", 0, 0, out var serial, out _, out _, 0, 0) ? BitConverter.GetBytes(serial) : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetComputerNameW(char[] buffer, ref uint size);

    private static byte[]? ComputerName()
    {
        var buffer = new char[16];
        uint size = (uint)buffer.Length;
        if (!GetComputerNameW(buffer, ref size)) return null;
        // iTunes quirk: it passes the character count as a byte count.
        var bytes = Encoding.Unicode.GetBytes(buffer);
        return bytes.AsSpan(0, (int)Math.Min(size, (uint)bytes.Length)).ToArray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HwProfileInfo
    {
        public uint dwDockInfo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 39)] public string szHwProfileGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szHwProfileName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetCurrentHwProfileW(out HwProfileInfo info);

    private static byte[]? HardwareProfile() =>
        GetCurrentHwProfileW(out var info) ? Encoding.Unicode.GetBytes(info.szHwProfileGuid) : null;

    /// <summary>For the X-Mme-Client-Info header, e.g. "10.0.26200/SP0.0.26200; Win11 Pro; x64".</summary>
    public static string WindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        var release = v is { Major: 10, Build: >= 22000 } ? "Win11" : v.Major == 10 ? "Win10" : $"Win{v.Major}.{v.Minor}";
        string edition;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            edition = (key?.GetValue("EditionID") as string) switch
            {
                "Professional" => "Pro",
                "Core" => "Home",
                "Enterprise" => "Enterprise",
                "Education" => "Education",
                { } other => other,
                null => "Pro",
            };
        var arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "ARM64", _ => "x64" };
        return $"{v.Major}.{v.Minor}.{v.Build}/SP0.0.{v.Build}; {release} {edition}; {arch}";
    }
}
