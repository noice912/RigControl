using LibreHardwareMonitor.Hardware;

namespace RigControl.Sensors;

/// <summary>One reading, flattened out of the monitoring library so the module logic can be tested without hardware.</summary>
/// <param name="Id">Stable identifier, e.g. "/intelcpu/0/temperature/32".</param>
/// <param name="HardwareKind">"cpu", "gpu", "memory", "board" or "superio".</param>
/// <param name="Kind">"temperature", "load", "power", "clock", "fan", "control", "data" or "smallData".</param>
public sealed record RawSensor(string Id, string Hardware, string HardwareKind, string Kind, string Name, int Index, double? Value);

public sealed record HardwareNames(string? Cpu, string? Gpu, string? Board, string? SuperIo);

/// <summary>Where sensor readings and motherboard fan control come from.</summary>
public interface IHardwareSource : IDisposable
{
    /// <summary>True when the kernel driver needed for processor temperatures and motherboard fans is installed.</summary>
    bool DriverInstalled { get; }

    /// <summary>Starts monitoring. <paramref name="lowLevel"/> = also talk to the motherboard's sensor chip (needs administrator + driver).</summary>
    void Open(bool lowLevel);

    HardwareNames Names { get; }

    /// <summary>Refreshes and returns every reading.</summary>
    IReadOnlyList<RawSensor> Read();

    /// <summary>Sets a motherboard fan control to a percentage, or hands it back to the BIOS when null. Returns false if unknown.</summary>
    bool SetControl(string controlId, double? percent);
}

/// <summary>
/// LibreHardwareMonitorLib 0.9.6. See docs/protocols/sensors-and-fans.md. Notes that matter:
///  - Its controller group is never enabled: that group opens NZXT and Razer devices and writes to them.
///  - Processor temperatures and the motherboard chip go through the separately installed PawnIO driver.
///  - Only motherboard (Super I/O) fan controls are ever used; the graphics card's fan control is never touched.
///  - Closing restores every motherboard fan this process changed to its BIOS setting.
/// All calls are serialised by the owning module.
/// </summary>
public sealed class LibreHardwareSource : IHardwareSource
{
    private Computer? _computer;
    private readonly Dictionary<string, IControl> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _lastWritten = new(StringComparer.Ordinal);

    public bool DriverInstalled
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    public HardwareNames Names { get; private set; } = new(null, null, null, null);

    public void Open(bool lowLevel)
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = lowLevel,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false, // never: it would open and write to the NZXT and Razer devices
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };
        computer.Open();
        _computer = computer;
    }

    public IReadOnlyList<RawSensor> Read()
    {
        var computer = _computer ?? throw new InvalidOperationException("Not open.");
        var list = new List<RawSensor>(128);
        string? cpu = null, gpu = null, board = null, superIo = null;
        _controls.Clear();

        foreach (var hw in computer.Hardware)
        {
            var kind = hw.HardwareType switch
            {
                HardwareType.Cpu => "cpu",
                HardwareType.GpuNvidia => "gpu",
                HardwareType.Memory => "memory",
                HardwareType.Motherboard => "board",
                _ => null,
            };
            if (kind is null) continue;
            hw.Update();
            if (kind == "cpu") cpu ??= hw.Name;
            if (kind == "gpu") gpu ??= hw.Name;
            if (kind == "board") board ??= hw.Name;
            Collect(hw, kind, list);
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                var subKind = sub.HardwareType == HardwareType.SuperIO ? "superio" : kind;
                if (subKind == "superio") superIo ??= sub.Name;
                Collect(sub, subKind, list);
            }
        }
        // In basic mode the library does not name the motherboard; Windows knows it without any driver.
        if (string.IsNullOrWhiteSpace(board)) board = BoardNameFromWindows();
        Names = new HardwareNames(cpu, gpu, board, superIo);
        return list;
    }

    private static string? _boardName;

    private static string? BoardNameFromWindows()
    {
        if (_boardName is not null) return _boardName.Length == 0 ? null : _boardName;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = key?.GetValue("BaseBoardManufacturer") as string;
            var product = key?.GetValue("BaseBoardProduct") as string;
            if (maker is not null && maker.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase)) maker = "ASUS";
            _boardName = string.IsNullOrWhiteSpace(product) ? string.Empty : (maker + " " + product).Trim();
        }
        catch
        {
            _boardName = string.Empty;
        }
        return _boardName.Length == 0 ? null : _boardName;
    }

    private void Collect(IHardware hw, string hardwareKind, List<RawSensor> list)
    {
        foreach (var s in hw.Sensors)
        {
            var kind = s.SensorType switch
            {
                SensorType.Temperature => "temperature",
                SensorType.Load => "load",
                SensorType.Power => "power",
                SensorType.Clock => "clock",
                SensorType.Fan => "fan",
                SensorType.Control => "control",
                SensorType.Data => "data",
                SensorType.SmallData => "smallData",
                _ => null,
            };
            if (kind is null) continue;
            var id = s.Identifier.ToString();
            double? value = s.Value is { } v && !float.IsNaN(v) && !float.IsInfinity(v) ? v : null;
            list.Add(new RawSensor(id, hw.Name, hardwareKind, kind, s.Name, s.Index, value));
            // Controls that may be driven: the motherboard chip's headers and the graphics card's own fans.
            if (kind == "control" && hardwareKind is "superio" or "gpu" && s.Control is { } control) _controls[id] = control;
        }
    }

    public bool SetControl(string controlId, double? percent)
    {
        if (!_controls.TryGetValue(controlId, out var control)) return false;
        if (percent is null)
        {
            control.SetDefault();
            _lastWritten.Remove(controlId);
            return true;
        }
        var value = (float)Math.Clamp(percent.Value, 0, 100);
        // The library ignores a repeated identical value, so nudge it (same byte on the chip) to force a real write.
        if (_lastWritten.TryGetValue(controlId, out var last) && last == value) value = Math.Min(100f, value + 0.01f);
        control.SetSoftware(value);
        _lastWritten[controlId] = value;
        return true;
    }

    public void Dispose()
    {
        try { _computer?.Close(); } // restores every motherboard fan this process changed
        finally { _computer = null; }
    }
}
