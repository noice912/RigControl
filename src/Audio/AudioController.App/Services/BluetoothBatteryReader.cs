using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AudioController.App.Services;

/// <summary>
/// Best-effort battery percentage for a connected Bluetooth device, read from the PnP device tree.
/// </summary>
/// <remarks>
/// Enumerates present device nodes with SetupAPI, keeps the Bluetooth ones (instance ids starting with
/// BTHENUM / BTHLE / BTHLEDEVICE / BTHHFENUM / BTH), matches their friendly name against the audio
/// endpoint's name, and reads DEVPKEY_Bluetooth_Battery ({104ea319-6ee2-4701-bd47-8ddbf425bbe5}, pid 2,
/// DEVPROP_TYPE_BYTE) through CfgMgr32. Windows only populates that property for devices that report
/// battery over HFP/BLE, so null is a normal answer. Never throws. It blocks while it walks the device
/// tree, so callers run it on a background thread.
/// </remarks>
public static partial class BluetoothBatteryReader
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint CR_SUCCESS = 0x00000000;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;
    private static readonly IntPtr InvalidHandle = new(-1);

    private static readonly DEVPROPKEY BatteryKey = new(new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    private static readonly DEVPROPKEY FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DEVPROPKEY DeviceDescKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DEVPROPKEY NameKey = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);

    private static readonly string[] BluetoothPrefixes = { "BTHENUM\\", "BTHLE\\", "BTHLEDEVICE\\", "BTHHFENUM\\", "BTH\\" };

    // Profile suffixes Windows appends to endpoint / service names ("Crusher ANC 2 Hands-Free AG Audio").
    private static readonly string[] NameSuffixes =
    {
        " Hands-Free AG Audio", " Hands-Free AG", " Hands-Free", " Handsfree", " Stereo", " Avrcp Transport",
        " A2DP SNK", " A2DP", " Audio",
    };

    /// <summary>
    /// Best-effort battery % (0–100) for a connected Bluetooth device, matched by friendly name — either the
    /// device's own name ("Crusher ANC 2") or an audio endpoint name ("Headphones (Crusher ANC 2)").
    /// Returns null on any failure — never throws.
    /// </summary>
    public static int? TryGetBatteryPercent(string friendlyName)
    {
        try
        {
            var keys = NameKeys(friendlyName);
            if (keys.Count == 0) return null;

            int bestScore = 0;
            int? best = null;

            foreach (var node in EnumerateBluetoothNodes())
            {
                int score = Score(node.Name, keys);
                if (score == 0 && node.ParentName is not null)
                    score = Score(node.ParentName, keys);

                if (score <= bestScore) continue;

                var battery = ReadBattery(node.DevInst);
                if (battery is null) continue;

                bestScore = score;
                best = battery;
            }

            return best;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Bluetooth battery read failed: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ matching

    private static List<string> NameKeys(string? friendlyName)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(friendlyName)) return keys;

        void Add(string candidate)
        {
            candidate = candidate.Trim();
            if (candidate.Length >= 3 && !keys.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                keys.Add(candidate);

            foreach (var suffix in NameSuffixes)
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var stripped = candidate[..^suffix.Length].Trim();
                    if (stripped.Length >= 3 && !keys.Contains(stripped, StringComparer.OrdinalIgnoreCase))
                        keys.Add(stripped);
                }
            }
        }

        // "Headphones (Crusher ANC 2)" → "Crusher ANC 2"; also keep the whole name.
        var match = ParenthesizedName().Match(friendlyName);
        if (match.Success) Add(match.Groups[1].Value);
        Add(friendlyName);
        return keys;
    }

    private static int Score(string? nodeName, List<string> keys)
    {
        if (string.IsNullOrWhiteSpace(nodeName)) return 0;

        var name = nodeName.Trim();
        int best = 0;
        foreach (var key in keys)
        {
            int s = 0;
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) s = 4;
            else if (name.StartsWith(key, StringComparison.OrdinalIgnoreCase)) s = 3;
            else if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) s = 2;
            else if (name.Length >= 3 && key.Contains(name, StringComparison.OrdinalIgnoreCase)) s = 1;
            best = Math.Max(best, s);
        }

        return best;
    }

    [GeneratedRegex(@"\(([^()]+)\)\s*$")]
    private static partial Regex ParenthesizedName();

    // ------------------------------------------------------------------ device tree

    private readonly record struct BtNode(uint DevInst, string? Name, string? ParentName);

    private static List<BtNode> EnumerateBluetoothNodes()
    {
        var nodes = new List<BtNode>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == InvalidHandle) return nodes;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var idBuffer = new char[512];

            for (uint index = 0; index < 20000 && SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                Array.Clear(idBuffer);
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuffer, idBuffer.Length, out _))
                    continue;

                int len = Array.IndexOf(idBuffer, '\0');
                if (len <= 0) continue;

                var instanceId = new string(idBuffer, 0, len);
                if (!BluetoothPrefixes.Any(p => instanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string? name = ReadName(data.DevInst);
                string? parentName = null;
                if (CM_Get_Parent(out uint parent, data.DevInst, 0) == CR_SUCCESS)
                    parentName = ReadName(parent);

                nodes.Add(new BtNode(data.DevInst, name, parentName));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return nodes;
    }

    private static string? ReadName(uint devInst) =>
        ReadString(devInst, FriendlyNameKey) ?? ReadString(devInst, NameKey) ?? ReadString(devInst, DeviceDescKey);

    private static string? ReadString(uint devInst, DEVPROPKEY key)
    {
        uint size = 0;
        var k = key;
        CM_Get_DevNode_PropertyW(devInst, ref k, out _, null, ref size, 0);
        if (size == 0 || size > 4096) return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref k, out uint type, buffer, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING)
            return null;

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static int? ReadBattery(uint devInst)
    {
        var buffer = new byte[4];
        uint size = (uint)buffer.Length;
        var k = BatteryKey;
        if (CM_Get_DevNode_PropertyW(devInst, ref k, out uint type, buffer, ref size, 0) != CR_SUCCESS)
            return null;

        if (type != DEVPROP_TYPE_BYTE || size < 1) return null;

        int value = buffer[0];
        return value is >= 0 and <= 100 ? value : null;
    }

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(Guid formatId, uint propertyId)
        {
            fmtid = formatId;
            pid = propertyId;
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        [Out] char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);
}
