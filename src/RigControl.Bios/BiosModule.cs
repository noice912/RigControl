using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Bios;

// ---------------------------------------------------------------- map (generated from the BIOS file's own forms)

public sealed class BiosOption
{
    public long Value { get; set; }
    public string Label { get; set; } = "";
}

public sealed class BiosSettingDef
{
    public string Name { get; set; } = "";
    public string Form { get; set; } = "";
    public int Offset { get; set; }
    public int Size { get; set; }
    public string Kind { get; set; } = "";
    public string? Help { get; set; }
    public List<BiosOption>? Options { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
}

public sealed class BiosStoreDef
{
    public string IfrName { get; set; } = "";
    public Guid Guid { get; set; }
    public int Size { get; set; }
}

public sealed class BiosMap
{
    public string Board { get; set; } = "";
    public string Bios { get; set; } = "";
    public string? Source { get; set; }
    public BiosStoreDef Store { get; set; } = new();
    public List<BiosSettingDef> Settings { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Every map shipped inside RigControl.</summary>
    public static IReadOnlyList<BiosMap> Bundled()
    {
        var asm = typeof(BiosMap).Assembly;
        var maps = new List<BiosMap>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("RigControl.Bios.Maps.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var map = JsonSerializer.Deserialize<BiosMap>(s, Options);
            if (map is not null) maps.Add(map);
        }
        return maps;
    }
}

// ---------------------------------------------------------------- contract

public sealed record BiosSettingDto(string Name, string Form, int Offset, int Size, string Kind, long Value, string? Label,
    IReadOnlyList<BiosOption>? Options, long? Min, long? Max, string? Help, bool ValueKnown);

/// <summary>Whether this PC is the exact board and BIOS version a carried map was made for. Reason is plain text when it is not.</summary>
public sealed record BiosAvailabilityDto(bool Available, string? Reason, string? Board, string? BiosVersion);

public sealed record BiosStatusDto(string? Board, string? BiosVersion, bool Elevated, bool Supported, string? Problem,
    string? VariableName, int? VariableSize, string? VariableAttributes, string? BackupPath, IReadOnlyList<BiosSettingDto> Settings,
    CpuEditsDto? Cpu = null, string? CpuProblem = null, bool? Writable = null, string? WriteProblem = null, bool RestartNeeded = false,
    string? OriginalBackup = null, IReadOnlyList<string>? LastChanges = null);

/// <summary>
/// The motherboard's own BIOS settings (CPU power limits, ratios, voltages): read straight from the UEFI variable
/// the BIOS setup menu saves to, decoded with a map made from that exact BIOS version's setup forms.
///
/// Safety rules (this stage only reads):
///  - Only a board and BIOS version with a bundled map is decoded; any other version shows nothing.
///  - The variable must have exactly the size the map expects, or nothing is decoded.
///  - A copy of the variable is saved to RigControl's data folder the first time it is read, and again whenever it
///    changes, so it can always be put back.
/// </summary>
public sealed class BiosModule : IAppModule
{
    private readonly Func<IFirmware> _firmwareFactory;
    private readonly Func<(string? Board, string? Bios)> _identity;
    private readonly IReadOnlyList<BiosMap> _maps;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private IFirmware? _firmware;
    private string? _lastBackup;
    private bool? _writable;
    private string? _writeProblem;
    private bool _restartNeeded;
    private IReadOnlyList<string>? _lastChanges;

    public BiosModule() : this(() => new WindowsFirmware(), ReadIdentity, BiosMap.Bundled()) { }

    public BiosModule(Func<IFirmware> firmwareFactory, Func<(string? Board, string? Bios)> identity, IReadOnlyList<BiosMap> maps)
    {
        _firmwareFactory = firmwareFactory;
        _identity = identity;
        _maps = maps;
    }

    /// <summary>
    /// The original settings (before RigControl changed anything), kept in HKLM\SOFTWARE\RigControl\BiosOriginal, one
    /// binary value per BIOS version. Only administrators can write there, and unlike a folder there are no links or
    /// pre-made paths a program without those rights could use to plant an "original" or redirect a write.
    /// </summary>
    public Func<string, byte[]?> ReadOriginal { get; init; } = bios =>
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RigControl\BiosOriginal");
        return k?.GetValue("Original-" + bios) as byte[];
    };

    public Action<string, byte[]> WriteOriginal { get; init; } = (bios, data) =>
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\RigControl\BiosOriginal");
        k.SetValue("Original-" + bios, data, Microsoft.Win32.RegistryValueKind.Binary);
    };

    public string Name => "bios";
    public string DisplayName => "BIOS settings";
    public ModuleStatus Status => _status;

    /// <summary>Every "this PC can't use the BIOS page" module status starts with this, so the app can hide the page.</summary>
    public const string NotAvailablePrefix = "Not available on this PC: ";

    /// <summary>
    /// The one rule for showing the BIOS page: the motherboard and the BIOS version must both be exactly the ones a
    /// carried map was generated from. Looks at nothing but the two names; no firmware is touched.
    /// </summary>
    public static BiosAvailabilityDto CheckPc(string? board, string? bios, IReadOnlyList<BiosMap> maps)
    {
        var b = board?.Trim();
        var v = bios?.Trim();
        if (string.IsNullOrEmpty(b))
            return new BiosAvailabilityDto(false, "Windows does not say which motherboard this is.", board, bios);
        var forBoard = maps.Where(m => string.Equals(m.Board, b, StringComparison.OrdinalIgnoreCase)).ToList();
        if (forBoard.Count == 0)
            return new BiosAvailabilityDto(false, $"RigControl has no BIOS settings map for this motherboard ({b}).", board, bios);
        if (!forBoard.Any(m => string.Equals(m.Bios, v, StringComparison.OrdinalIgnoreCase)))
            return new BiosAvailabilityDto(false,
                $"RigControl's BIOS settings map is for BIOS {string.Join(" or ", forBoard.Select(m => m.Bios))}; this PC has BIOS {(string.IsNullOrEmpty(v) ? "of an unknown version" : v)}. Settings are not read from a different version.",
                board, bios);
        return new BiosAvailabilityDto(true, null, board, bios);
    }

    /// <summary>The same check for this PC, usable before (or without) starting the module, e.g. by a system scan.</summary>
    public BiosAvailabilityDto Availability()
    {
        var (board, bios) = _identity();
        return CheckPc(board, bios, _maps);
    }

    private static BiosMap? MapFor(IReadOnlyList<BiosMap> maps, string? board, string? bios) => maps.FirstOrDefault(m =>
        string.Equals(m.Board, board?.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(m.Bios, bios?.Trim(), StringComparison.OrdinalIgnoreCase));

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        try
        {
            // First of all: is this the board and BIOS the map was made for? If not, nothing else is looked at.
            var pc = Availability();
            if (!pc.Available)
            {
                _status = new ModuleStatus(ModuleState.Degraded, NotAvailablePrefix + pc.Reason);
                _log.Info("BIOS settings: " + NotAvailablePrefix.ToLowerInvariant() + pc.Reason);
                return Task.CompletedTask;
            }
            if (!context.Environment.IsElevated)
            {
                _status = new ModuleStatus(ModuleState.Degraded, "Reading BIOS settings needs administrator rights.");
                return Task.CompletedTask;
            }
            _firmware = _firmwareFactory();
            if (!_firmware.Available)
            {
                _status = new ModuleStatus(ModuleState.Degraded, _firmware.Problem);
                return Task.CompletedTask;
            }
            var s = GetStatus();
            _status = s.Supported && s.Problem is null ? new ModuleStatus(ModuleState.Ready) : new ModuleStatus(ModuleState.Degraded, s.Problem);
            _log.Info(s.Problem is null
                ? $"BIOS settings: read {s.VariableName} ({s.VariableSize} bytes, {s.VariableAttributes}), {s.Settings.Count} CPU settings decoded."
                : "BIOS settings: " + s.Problem);
            LogCpuLocks(s.BiosVersion);
        }
        catch (Exception ex)
        {
            _log.Warn("BIOS settings could not be read.", ex);
            _status = new ModuleStatus(ModuleState.Degraded, "BIOS settings could not be read: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        lock (_gate)
        {
            object? result = method switch
            {
                "getStatus" => GetStatus(),
                "getAvailability" => Availability(),
                "testWrite" => TestWrite(),
                "applyCpu" => ApplyCpu(RpcArgs.Require<CpuChange>(args)),
                "restoreOriginal" => RestoreOriginal(),
                _ => throw RpcException.UnknownMethod(Name, method),
            };
            return Task.FromResult<object?>(result);
        }
    }

    // ---------------------------------------------------------------- reading

    internal static (string? Board, string? Bios) ReadIdentity()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (key?.GetValue("BaseBoardProduct") as string, key?.GetValue("BIOSVersion") as string);
        }
        catch
        {
            return (null, null);
        }
    }

    private BiosStatusDto GetStatus()
    {
        var (board, bios) = _identity();
        var elevated = _ctx?.Environment.IsElevated ?? false;
        BiosStatusDto Fail(string problem, bool supported = false) =>
            new(board, bios, elevated, supported, problem, null, null, null, _lastBackup, Array.Empty<BiosSettingDto>());

        var pc = CheckPc(board, bios, _maps);
        var map = MapFor(_maps, board, bios);
        if (!pc.Available || map is null) return Fail(NotAvailablePrefix + (pc.Reason ?? "RigControl has no BIOS settings map for it."));
        if (!elevated) return Fail("Reading BIOS settings needs administrator rights.", true);
        if (_firmware is null || !_firmware.Available) return Fail(_firmware?.Problem ?? "BIOS settings are not available.", true);

        var candidates = _firmware.List().Where(v => v.Vendor == map.Store.Guid).ToList();
        if (candidates.Count == 0) return Fail("The BIOS does not show its CPU settings to Windows.", true);
        (FirmwareVariable Var, byte[] Data, uint Attrs)? found = null;
        foreach (var c in candidates)
        {
            var r = _firmware.Read(c.Name, c.Vendor);
            if (r is { } got && got.Data.Length == map.Store.Size)
            {
                if (found is not null) return Fail("More than one BIOS variable matches; nothing is decoded to stay safe.", true);
                found = (c, got.Data, got.Attributes);
            }
        }
        if (found is not { } f)
            return Fail($"No BIOS variable has the expected size ({map.Store.Size} bytes): " +
                        string.Join(", ", candidates.Select(c => c.Name)), true);

        Backup(f.Var.Name, bios ?? "unknown", f.Data);
        if (_writable != true && _lastChanges is null) SaveOriginal(bios ?? "unknown", f.Data); // before RigControl has written anything
        var settings = map.Settings.Select(d => Decode(d, f.Data)).ToList();
        var edits = CpuEdits.For(map, out var cpuProblem);
        return new BiosStatusDto(board, bios, elevated, true, null, f.Var.Name, f.Data.Length, DescribeAttributes(f.Attrs),
            _lastBackup, settings, edits?.Read(f.Data), cpuProblem, _writable, _writeProblem, _restartNeeded, OriginalBackup(bios ?? "unknown"),
            _lastChanges);
    }

    // ---------------------------------------------------------------- writing

    private sealed record Current(BiosMap Map, FirmwareVariable Var, byte[] Data, uint Attrs, string Bios);

    /// <summary>The variable as it is now, only when everything checks out (same rules as reading).</summary>
    private Current Load()
    {
        var s = GetStatus();
        if (s.Problem is not null || s.VariableName is null) throw RpcException.Unavailable(s.Problem ?? "BIOS settings can't be read.");
        var map = MapFor(_maps, s.Board, s.BiosVersion) ?? throw RpcException.Unavailable(NotAvailablePrefix + "RigControl has no BIOS settings map for it.");
        var v = _firmware!.List().First(x => x.Name == s.VariableName && x.Vendor == map.Store.Guid);
        var r = _firmware.Read(v.Name, v.Vendor) ?? throw RpcException.Unavailable("The BIOS settings disappeared.");
        if (r.Data.Length != map.Store.Size) throw RpcException.Unsafe("The BIOS settings changed size; nothing is written.");
        return new Current(map, v, r.Data, r.Attributes, s.BiosVersion ?? "unknown");
    }

    /// <summary>Writes, then reads back: the BIOS must now hold exactly what was written.</summary>
    private void WriteVerified(Current c, byte[] data)
    {
        var err = _firmware!.Write(c.Var.Name, c.Var.Vendor, data, c.Attrs);
        if (err is not null)
        {
            _writable = false;
            _writeProblem = err;
            throw RpcException.Unavailable("The BIOS didn't take it: " + err + ".");
        }
        var back = _firmware.Read(c.Var.Name, c.Var.Vendor);
        if (back is null || !back.Value.Data.AsSpan().SequenceEqual(data))
        {
            _writable = false;
            _writeProblem = "the BIOS accepted the write but reads back something else";
            throw RpcException.Unavailable("The BIOS accepted the change but reads back something different. Restart into the BIOS and check the CPU settings, or press Put back the original settings.");
        }
        _writable = true;
        _writeProblem = null;
    }

    /// <summary>Writes the settings back unchanged: proves the BIOS lets Windows change them, without changing anything.</summary>
    private BiosStatusDto TestWrite()
    {
        var c = Load();
        WriteVerified(c, c.Data);
        _log.Info("BIOS settings: a write of the unchanged settings was accepted and reads back the same.");
        return GetStatus();
    }

    private BiosStatusDto ApplyCpu(CpuChange change)
    {
        var c = Load();
        var edits = CpuEdits.For(c.Map, out var problem) ?? throw RpcException.Unavailable(problem!);
        if (OriginalBackup(c.Bios) is null)
            throw RpcException.Unavailable("RigControl couldn't keep a safe copy of the original settings, so it won't change them.");
        var lines = new List<string>();
        byte[] next;
        try { next = edits.Apply(c.Data, change, lines); }
        catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        if (lines.Count == 0) return GetStatus();
        // Only the bytes these settings own may differ from what is there now.
        var allowed = edits.TouchedBytes().ToHashSet();
        for (var i = 0; i < next.Length; i++)
            if (next[i] != c.Data[i] && !allowed.Contains(i)) throw RpcException.Unsafe("A change touched a part of the BIOS settings it shouldn't; nothing was written.");
        Backup(c.Var.Name, c.Bios, c.Data);
        WriteVerified(c, next);
        _restartNeeded = true;
        _lastChanges = lines;
        _log.Info("BIOS CPU settings changed (applies at the next restart): " + string.Join("; ", lines) + ".");
        Backup(c.Var.Name, c.Bios, next);
        return GetStatus();
    }

    /// <summary>
    /// The BIOS's locks that decide what Windows tools (Intel XTU) may change, read from Intel's CpuSetup variable
    /// (BIOS 2301 offsets, from the same setup forms). Read only, logged once at start.
    /// </summary>
    private void LogCpuLocks(string? bios)
    {
        try
        {
            if (bios?.Trim() != "2301" || _firmware is null || !_firmware.Available) return;
            var guid = new Guid("b08f97ff-e6e8-4193-a997-5e9e9b0adb32");
            var v = _firmware.List().FirstOrDefault(x => x.Vendor == guid && x.Name == "CpuSetup");
            if (v is null) return;
            var r = _firmware.Read(v.Name, v.Vendor);
            if (r is not { } got || got.Data.Length != 962) return;
            string On(int o) => got.Data[o] switch { 0 => "Disabled", 1 => "Enabled", var x => "value " + x };
            _log.Info($"BIOS CPU locks: UnderVolt Protection {On(898)}, Overclocking Lock {On(271)}, CFG Lock {On(68)}, OverClocking Feature {On(474)}.");
        }
        catch (Exception ex)
        {
            _log.Warn("Couldn't read the BIOS's CPU locks: " + ex.Message);
        }
    }

    private static string BiosKey(string bios) => string.Concat(bios.Where(char.IsLetterOrDigit));

    /// <summary>Where the original is shown as kept, when there is one.</summary>
    private string? OriginalBackup(string bios) => TrustedOriginal(bios) is null ? null : @"HKLM\SOFTWARE\RigControl\BiosOriginal\Original-" + BiosKey(bios);

    private byte[]? TrustedOriginal(string bios)
    {
        try { return ReadOriginal(BiosKey(bios)); }
        catch { return null; }
    }

    /// <summary>Saves the original once per BIOS version, before any change is written.</summary>
    private void SaveOriginal(string bios, byte[] data)
    {
        try
        {
            if (TrustedOriginal(bios) is not null) return;
            WriteOriginal(BiosKey(bios), data);
            _log.Info(@"BIOS settings: original saved (administrators only) in HKLM\SOFTWARE\RigControl\BiosOriginal.");
        }
        catch (Exception ex)
        {
            _log.Warn("Could not save the original BIOS settings where only administrators can write; changing them stays off.", ex);
        }
    }

    private BiosStatusDto RestoreOriginal()
    {
        var c = Load();
        var original = TrustedOriginal(c.Bios) ?? throw RpcException.Unavailable("There is no saved copy of the original settings; nothing was written.");
        if (original.Length != c.Map.Store.Size) throw RpcException.Unsafe("The saved copy has the wrong size; nothing was written.");
        var edits = CpuEdits.For(c.Map, out var problem) ?? throw RpcException.Unavailable(problem!);
        // Only the four CPU settings come back, and through the same limits as any change: the copy can't put anything
        // else into the BIOS.
        var was = edits.Read(original);
        var lines = new List<string>();
        byte[] next;
        try
        {
            next = edits.Apply(c.Data, new CpuChange { Profile = was.Profile, Pl1 = was.Pl1, Pl2 = was.Pl2,
                UndervoltMv = Math.Max(0, was.UndervoltMv), AllCoreRatio = was.AllCoreRatio }, lines);
        }
        catch (ArgumentException ex)
        {
            throw RpcException.Unsafe("The saved original is outside RigControl's limits (" + ex.Message + "); nothing was written.");
        }
        var allowed = edits.TouchedBytes().ToHashSet();
        for (var i = 0; i < next.Length; i++)
            if (next[i] != c.Data[i] && !allowed.Contains(i)) throw RpcException.Unsafe("Putting back touched a part of the BIOS settings it shouldn't; nothing was written.");
        if (lines.Count == 0) return GetStatus();
        WriteVerified(c, next);
        _restartNeeded = true;
        _lastChanges = new[] { "put back the original CPU settings: " + string.Join("; ", lines) };
        _log.Info("BIOS CPU settings put back to the original (applies at the next restart): " + string.Join("; ", lines) + ".");
        return GetStatus();
    }

    internal static long ReadValue(byte[] data, int offset, int size)
    {
        long v = 0;
        for (var i = size - 1; i >= 0; i--) v = (v << 8) | data[offset + i];
        return v;
    }

    internal static BiosSettingDto Decode(BiosSettingDef d, byte[] data)
    {
        var value = ReadValue(data, d.Offset, d.Size);
        string? label = null;
        var known = true;
        if (d.Options is { Count: > 0 })
        {
            label = d.Options.FirstOrDefault(o => o.Value == value)?.Label;
            known = label is not null;
        }
        else if (d.Min is { } min && d.Max is { } max)
        {
            known = value >= min && value <= max;
        }
        return new BiosSettingDto(d.Name, d.Form, d.Offset, d.Size, d.Kind, value, label, d.Options, d.Min, d.Max, d.Help, known);
    }

    internal static string DescribeAttributes(uint a)
    {
        var parts = new List<string>();
        if ((a & 1) != 0) parts.Add("non-volatile");
        if ((a & 2) != 0) parts.Add("boot");
        if ((a & 4) != 0) parts.Add("runtime");
        if ((a & 0x20) != 0) parts.Add("authenticated");
        return string.Join(", ", parts) + $" (0x{a:X})";
    }

    private void Backup(string name, string bios, byte[] data)
    {
        // Same content already saved: keep the older file (the original stays the first one).
        if (_ctx is null) return;
        try
        {
            var dir = Path.Combine(_ctx.Environment.DataDirectory, "bios-backups");
            Directory.CreateDirectory(dir);
            var hash = Convert.ToHexString(SHA256.HashData(data))[..12];
            var existing = Directory.GetFiles(dir, $"*-{hash}.bin").FirstOrDefault();
            if (existing is not null)
            {
                _lastBackup = existing;
                return;
            }
            var safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
            var path = Path.Combine(dir, $"{safe}-bios{bios}-{DateTime.Now:yyyyMMdd-HHmmss}-{hash}.bin");
            File.WriteAllBytes(path, data);
            _lastBackup = path;
            _log.Info("BIOS settings copy saved: " + path);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not save a copy of the BIOS settings.", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }
}
