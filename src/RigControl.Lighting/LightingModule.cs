using System.Diagnostics;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Lighting;

// ---------------------------------------------------------------- contract types (docs/API.md, "lighting")

public sealed record OpenRgbDto(bool Installed, string? Path, bool Running, bool Connected, int Port, string? Version, string? Message);

public sealed record LightZoneDto(int Id, string Name, int LedCount);

public sealed record LightModeDto(int Id, string Name, int ColorCount, bool SupportsSpeed, bool SupportsBrightness,
    int? SpeedMin, int? SpeedMax, int? BrightnessMin, int? BrightnessMax);

public sealed record LightDeviceDto(int Id, string Name, string Type, string Vendor, int LedCount,
    IReadOnlyList<LightZoneDto> Zones, IReadOnlyList<LightModeDto> Modes, int ActiveModeId, string? Color, bool LeftOut = false);

public sealed record LightingStatusDto(OpenRgbDto OpenRgb, IReadOnlyList<LightDeviceDto> Devices, IReadOnlyList<string> Conflicts);

public sealed record SceneDto(string Name, string SavedAt);

/// <summary>"Lights follow the screen / a capture card" (RichPresence's light sync, as a Lighting mode).</summary>
/// <summary>One zone that can be placed on the layout (a device that is not connected now keeps its placement).</summary>
public sealed record LayoutZoneDto(string Key, string Device, int Zone, string Name, int Leds, int MatrixWidth, int MatrixHeight,
    IReadOnlyList<int>? Matrix, bool Connected, LayoutItem? Placed, IReadOnlyList<double[]>? Segments = null);

public sealed record LayoutDto(bool PerLed, IReadOnlyList<LayoutZoneDto> Zones);

/// <summary>The picture the lights follow right now, shrunk to Width x Height ("RRGGBB" per cell, row by row).</summary>
public sealed record SyncPreviewDto(int Width, int Height, string Pixels);

public sealed record SyncDto(bool Enabled, string Source, string? SourceName, int Brightness, string Status, string? Color,
    IReadOnlyList<CaptureDeviceDto> Sources, string? CaptureSize, bool Vivid, bool Bass = false, int BassStrength = 70,
    string? BassProblem = null, bool Treble = false, int TrebleStrength = 50, int AudioGain = 100, bool Mid = false, int MidStrength = 50,
    string Effect = "screen", IReadOnlyList<EffectInfo>? Effects = null, IReadOnlyDictionary<string, EffectParams>? EffectParams = null);

// ---------------------------------------------------------------- persisted settings

public sealed class SceneDevice
{
    public string Key { get; set; } = "";
    public string ModeName { get; set; } = "";
    public uint Speed { get; set; }
    public uint Brightness { get; set; }
    public List<uint> ModeColors { get; set; } = new();
    public List<uint> LedColors { get; set; } = new();
}

public sealed class Scene
{
    public string Name { get; set; } = "";
    public string SavedAt { get; set; } = "";
    public List<SceneDevice> Devices { get; set; } = new();
}

public sealed class LightingSettings
{
    public const string Section = "lighting";
    /// <summary>Start OpenRGB hidden when RigControl starts, so lighting works without opening anything else.</summary>
    public bool AutoStart { get; set; } = true;
    public List<Scene> Scenes { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    /// <summary>Where each light sits on the picture, for "Per LED".</summary>
    public LayoutSettings Layout { get; set; } = new();
    /// <summary>Devices RigControl never sends anything to (their own program runs them), by device key.</summary>
    public List<string> LeftOut { get; set; } = new();
    /// <summary>A Nanoleaf paired directly, so each panel can get its own colour (null = none).</summary>
    public NanoleafSettings? Nanoleaf { get; set; }
}

public sealed record NanoleafDto(bool Paired, string? Ip, string? Name, int Panels, bool Streaming, string? Problem, IReadOnlyList<string>? Found = null);

public sealed class SyncSettings
{
    public bool Enabled { get; set; }
    /// <summary>"" = the screen; otherwise a capture device's id.</summary>
    public string Source { get; set; } = "";
    public string SourceName { get; set; } = "";
    public int Brightness { get; set; } = 100;
    /// <summary>Make the colour bright and saturated before it is sent (on by default).</summary>
    public bool Vivid { get; set; } = true;
    /// <summary>Brightness follows the bass of what the PC plays.</summary>
    public bool Bass { get; set; }
    /// <summary>How far the lights dim between bass hits, 10 to 100%.</summary>
    public int BassStrength { get; set; } = 70;
    /// <summary>Vocals, guitars and synths turn the colour round the colour wheel.</summary>
    public bool Mid { get; set; }
    /// <summary>How far a full mid-range hit turns the colour, 10 to 100% (100% = 60 degrees).</summary>
    public int MidStrength { get; set; } = 50;
    /// <summary>Hi-hats and cymbals flash the lights toward white.</summary>
    public bool Treble { get; set; }
    /// <summary>How white a full treble hit makes the lights, 10 to 100%.</summary>
    public int TrebleStrength { get; set; } = 50;
    /// <summary>Gain on the sound before the lights react, 25 to 400% (100 = as heard).</summary>
    public int AudioGain { get; set; } = 100;
    /// <summary>Which effect paints the lights: "screen" (the screen or a capture card) or one from the effects library.</summary>
    public string Effect { get; set; } = EffectRenderer.Screen;
    /// <summary>Each effect's own settings, by effect id.</summary>
    public Dictionary<string, EffectParams> EffectParams { get; set; } = new();
    /// <summary>The lights as they were when the sync was turned on; put back when it is turned off.</summary>
    public Scene? Before { get; set; }

    public EffectParams ParamsFor(string id) => EffectParams.TryGetValue(id, out var p) ? p : new EffectParams();
}

/// <summary>
/// Lighting for every RGB device, through OpenRGB running hidden as a local server.
/// See docs/protocols/openrgb-integration.md.
///
/// Rules enforced here:
///  - OpenRGB is started automatically when installed (unless something already serves its port); RigControl never
///    installs it and never stops an OpenRGB it did not start.
///  - Nothing is ever saved into a device's own flash memory, and zones are never resized.
///  - Every mode change is built from the mode exactly as the device reported it, changing only colour, speed
///    and brightness, within the device's own limits.
/// </summary>
public sealed class LightingModule : IAppModule
{
    private const string DownloadPage = "https://openrgb.org/";

    private static readonly string[] DeviceTypes =
    {
        "Motherboard", "Memory", "Graphics card", "Cooler", "LED strip", "Keyboard", "Mouse", "Mouse mat", "Headset", "Headset stand",
        "Gamepad", "Light", "Speaker", "Virtual", "Storage", "Case", "Microphone", "Accessory", "Keypad", "Laptop", "Monitor",
    };

    /// <summary>Other programs that also drive lights and can override what RigControl sets (process name, shown name).</summary>
    private static readonly (string Process, string Name)[] Rivals =
    {
        ("LightingService", "Armoury Crate lighting"), ("ArmouryCrate", "Armoury Crate"), ("NZXT CAM", "NZXT CAM"),
        ("GCC", "GIGABYTE Control Center"), ("RazerAppEngine", "Razer Synapse"), ("Razer Synapse 3", "Razer Synapse"),
        ("iCUE", "Corsair iCUE"), ("SignalRgb", "SignalRGB"),
    };

    private readonly IOpenRgb _rgb;
    private readonly Func<string, bool> _isRunning;
    private readonly bool _background;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private ModuleContext? _ctx;
    private ILog _log = NullLog.Instance;
    private volatile ModuleStatus _status = ModuleStatus.NotStarted;
    private LightingSettings _settings = new();
    private IReadOnlyList<RgbController> _controllers = Array.Empty<RgbController>();
    private string? _message;
    private Thread? _thread;
    private int _disposed;
    private Thread? _syncThread;
    private int _syncGen;
    private string _syncStatus = "Off";
    private int? _syncColor;
    private string? _captureSize;
    private IReadOnlyList<CaptureDeviceDto> _captureDevices = Array.Empty<CaptureDeviceDto>();
    /// <summary>The newest picture the sync read (48x27 BGRA copy), for the layout page's preview.</summary>
    private volatile byte[]? _previewGrid;

    public LightingModule() : this(new OpenRgbServer(), name => Process.GetProcessesByName(name).Length > 0, background: true, new GigabyteControlCenter()) { }

    public LightingModule(IOpenRgb rgb, Func<string, bool> isProcessRunning, bool background, IGigabyteControlCenter? gcc = null)
    {
        _rgb = rgb;
        _isRunning = isProcessRunning;
        _background = background;
        _gcc = gcc ?? new NoGigabyteControlCenter();
    }

    private readonly IGigabyteControlCenter _gcc;

    public string Name => "lighting";
    public string DisplayName => "Lighting (OpenRGB)";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        _settings = context.Settings.Get<LightingSettings>(LightingSettings.Section);
        _status = new ModuleStatus(ModuleState.Starting);
        if (_background)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "RigControl lighting" };
            _thread.Start();
            if (_settings.Sync.Enabled) StartSync();
        }
        else
        {
            AutoStart();
        }
        return Task.CompletedTask;
    }

    private void Loop()
    {
        AutoStart();
        while (!_stop.Token.WaitHandle.WaitOne(2000))
        {
            try { Poll(); }
            catch (Exception ex) { _log.Warn("Lighting update failed.", ex); }
        }
    }

    /// <summary>Starts (or attaches to) OpenRGB at startup so the user never has to open it.</summary>
    private void AutoStart()
    {
        LightingStatusDto? connected = null;
        try
        {
            AutoStartCore();
            lock (_gate) if (_rgb.Connected) connected = BuildStatus();
        }
        finally
        {
            // Lets the rest of the app (the room lights) pick up the PC's colours once RigControl is connected.
            if (connected is not null) PublishChanged(connected);
        }
    }

    private void AutoStartCore()
    {
        lock (_gate)
        {
            try
            {
                if (_rgb.FindExecutable() is null && !_rgb.IsListening())
                {
                    SetStatus();
                    return;
                }
                if (_settings.AutoStart || _rgb.IsListening()) StartAndConnect();
            }
            catch (Exception ex)
            {
                _message = "OpenRGB could not be started: " + Plain(ex);
                _log.Warn("OpenRGB auto-start failed.", ex);
            }
            SetStatus();
        }
    }

    /// <summary>Picks up devices OpenRGB finds after it started (detection continues in the background) and hot-plugged ones.</summary>
    public void Poll()
    {
        LightingStatusDto? changed = null;
        lock (_gate)
        {
            if (!_rgb.Connected) return;
            if (_rgb.DeviceListChanged() || !_rgb.Connected)
            {
                try
                {
                    if (_rgb.Connected) _controllers = _rgb.ReadControllers();
                    else _controllers = Array.Empty<RgbController>();
                }
                catch (Exception ex)
                {
                    _rgb.Disconnect();
                    _controllers = Array.Empty<RgbController>();
                    _message = "The connection to OpenRGB was lost: " + Plain(ex);
                }
                SetStatus();
                changed = BuildStatus();
            }
        }
        if (changed is not null) PublishChanged(changed);
    }

    private static string Plain(Exception ex) => ex is RpcException ? ex.Message : ex.Message.TrimEnd('.') + ".";

    private void StartAndConnect()
    {
        if (!_rgb.IsListening())
        {
            var exe = _rgb.FindExecutable() ?? throw RpcException.Unavailable("OpenRGB is not installed.");
            _log.Info("Starting OpenRGB hidden: " + exe);
            if (!_rgb.StartServer(exe)) throw RpcException.Unavailable("OpenRGB did not start. Open it once by hand to check that it works.");
        }
        _rgb.Connect();
        _controllers = _rgb.ReadControllers();
        _message = null;
        _log.Info($"Connected to OpenRGB: {_controllers.Count} device(s).");
    }

    private void SetStatus()
    {
        if (_rgb.Connected) _status = new ModuleStatus(ModuleState.Ready);
        else if (_rgb.FindExecutable() is null) _status = new ModuleStatus(ModuleState.Degraded, "OpenRGB is not installed.");
        else _status = new ModuleStatus(ModuleState.Degraded, _message ?? "OpenRGB is not running.");
    }

    // ---------------------------------------------------------------- status

    private LightingStatusDto BuildStatus()
    {
        var exe = _rgb.FindExecutable();
        var listening = _rgb.Connected || _rgb.IsListening();
        var conflicts = Rivals.Where(r => SafeRunning(r.Process)).Select(r => r.Name).Distinct().ToList();
        var devices = _rgb.Connected ? _controllers.Select(ToDto).ToList() : new List<LightDeviceDto>();
        // While the lights follow the screen, the list shows the colour they are being sent, not the cached one.
        if (_settings.Sync.Enabled && _syncColor is { } live)
            devices = devices.Select(d => IsLeftOut(d.Id) ? d : d with { Color = ColorMath.Hex(live) }).ToList();
        devices = devices.Select(d => IsLeftOut(d.Id) ? d with { LeftOut = true } : d).ToList();
        return new LightingStatusDto(
            new OpenRgbDto(exe is not null || listening, exe, listening, _rgb.Connected, OpenRgbServer.Port, null, _message),
            devices, conflicts);
    }

    private bool SafeRunning(string process)
    {
        try { return _isRunning(process); }
        catch { return false; }
    }

    private static int ColorCount(RgbMode m)
    {
        if (m.Has(RgbMode.ModeSpecificColor)) return (int)Math.Clamp(1u, Math.Max(1u, m.ColorsMin), Math.Max(1u, m.ColorsMax));
        return m.Has(RgbMode.PerLedColor) ? 1 : 0;
    }

    private static LightDeviceDto ToDto(RgbController c)
    {
        var modes = c.Modes.Select((m, i) => new LightModeDto(i, m.Name, ColorCount(m),
            m.Has(RgbMode.HasSpeed), m.Has(RgbMode.HasBrightness),
            m.Has(RgbMode.HasSpeed) ? (int)m.SpeedMin : null, m.Has(RgbMode.HasSpeed) ? (int)m.SpeedMax : null,
            m.Has(RgbMode.HasBrightness) ? (int)m.BrightnessMin : null, m.Has(RgbMode.HasBrightness) ? (int)m.BrightnessMax : null)).ToList();
        var active = c.ActiveMode >= 0 && c.ActiveMode < c.Modes.Count ? c.Modes[c.ActiveMode] : null;
        uint? color = active is { ColorMode: 2, Colors.Count: > 0 } ? active.Colors[0]
            : c.Colors.Count > 0 ? c.Colors.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key
            : null;
        return new LightDeviceDto(c.Index, c.Name,
            c.Type >= 0 && c.Type < DeviceTypes.Length ? DeviceTypes[c.Type] : "Device",
            c.Vendor, c.Colors.Count,
            c.Zones.Select((z, i) => new LightZoneDto(i, z.Name, (int)z.LedsCount)).ToList(),
            modes, c.ActiveMode, color is { } v ? OpenRgbProtocol.ColorToHex(v) : null);
    }

    // ---------------------------------------------------------------- RPC

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        if (method == "listSyncSources") return ListSyncSourcesAsync();
        if (method == "getNanoleaf") return Task.FromResult<object?>(NanoleafStatus());
        if (method == "findNanoleaf") return FindNanoleafAsync(ct);
        if (method == "pairNanoleaf") return PairNanoleafAsync(RpcArgs.Require<NanoleafArgs>(args), ct);
        if (method == "forgetNanoleaf") return Task.FromResult<object?>(ForgetNanoleaf());
        if (method == "getSyncPreview") return Task.FromResult<object?>(Preview());
        // Changing the lights by hand ends the sync (without putting the old lights back: the hand change wins).
        if (method is "setColor" or "setZoneColor" or "setMode" or "off" or "applyScene" && _settings.Sync.Enabled) StopSync(restore: false);
        object? result;
        lock (_gate)
        {
            result = method switch
            {
                "getStatus" => BuildStatus(),
                "startOpenRgb" => StartOpenRgb(),
                "installOpenRgb" => InstallOpenRgb(),
                "setColor" => SetColor(RpcArgs.Require<ColorArgs>(args)),
                "setZoneColor" => SetZoneColor(RpcArgs.Require<ZoneColorArgs>(args)),
                "setMode" => SetMode(RpcArgs.Require<ModeArgs>(args)),
                "off" => SetColor(new ColorArgs { DeviceIds = RpcArgs.Optional<ColorArgs>(args).DeviceIds, Color = "#000000" }),
                "listScenes" => ListScenes(),
                "saveScene" => SaveScene(RpcArgs.Require<SceneArgs>(args)),
                "applyScene" => ApplyScene(RpcArgs.Require<SceneArgs>(args)),
                "deleteScene" => DeleteScene(RpcArgs.Require<SceneArgs>(args)),
                "getSync" => SyncStatus(),
                "getGcc" => _gcc.GetState(),
                "takeOverGcc" => TakeOverGcc(),
                "giveBackGcc" => GiveBackGcc(),
                "setSync" => SetSync(RpcArgs.Require<SyncArgs>(args)),
                "getLayout" => LayoutStatus(),
                "setLayout" => SetLayout(RpcArgs.Require<LayoutArgs>(args)),
                _ => throw RpcException.UnknownMethod(Name, method),
            };
        }
        return Task.FromResult<object?>(result);
    }

    private LightingStatusDto StartOpenRgb()
    {
        try
        {
            StartAndConnect();
        }
        catch (RpcException)
        {
            SetStatus();
            throw;
        }
        catch (Exception ex)
        {
            _message = "OpenRGB could not be reached: " + Plain(ex);
            SetStatus();
            throw RpcException.Unavailable(_message);
        }
        SetStatus();
        return BuildStatus();
    }

    /// <summary>
    /// RigControl does not download or install programs itself. Windows' package manager has no current OpenRGB
    /// package, so this opens OpenRGB's own download page; the user installs it and RigControl finds it.
    /// </summary>
    private object InstallOpenRgb()
    {
        if (_rgb.FindExecutable() is not null) return new { started = false, message = "OpenRGB is already installed." };
        try
        {
            Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable("The OpenRGB download page could not be opened: " + ex.Message);
        }
        return new { started = true, message = "OpenRGB's download page is open in your browser. Install the Windows 64-bit version, then press Start OpenRGB here." };
    }

    private void RequireConnected()
    {
        if (!_rgb.Connected) throw RpcException.Unavailable("OpenRGB is not running. Start it first.");
    }

    private RgbController Device(int id)
    {
        var c = _controllers.FirstOrDefault(c => c.Index == id) ?? throw RpcException.InvalidArgs("Unknown lighting device.");
        if (IsLeftOut(c)) throw RpcException.Unavailable($"RigControl leaves {c.Name} to its own program (GIGABYTE Control Center).");
        return c;
    }

    private bool IsLeftOut(RgbController c) => _settings.LeftOut.Contains(c.Key);
    private bool IsLeftOut(int index) => _controllers.FirstOrDefault(c => c.Index == index) is { } c && IsLeftOut(c);

    /// <summary>The devices RigControl may send colours to.</summary>
    private IEnumerable<RgbController> Driven => _controllers.Where(c => !IsLeftOut(c));

    private static uint ParseColor(string? hex)
    {
        try { return OpenRgbProtocol.ColorFromHex(hex ?? ""); }
        catch (FormatException ex) { throw RpcException.InvalidArgs(ex.Message); }
    }

    /// <summary>Normalises a mode before sending: the server rejects (silently) anything outside the mode's own rules.</summary>
    private static RgbMode Sanitise(RgbMode m)
    {
        if (!m.Has(RgbMode.DirectionMask)) m.Direction = 0;
        if (!m.Has(RgbMode.PerLedColor | RgbMode.ModeSpecificColor | RgbMode.RandomColor)) m.ColorMode = 0;
        if (m.ColorMode == 2 && m.Colors.Count == 0) m.Colors.Add(0); // never an empty list with mode-specific colour
        return m;
    }

    /// <summary>Sends one colour to a whole device. Returns false if the device has no mode that takes a colour.</summary>
    private bool ApplyColor(RgbController c, uint color)
    {
        var direct = c.Modes.FindIndex(m => m.Name == "Direct" && m.Has(RgbMode.PerLedColor));
        if (direct >= 0)
        {
            var m = c.Modes[direct].Clone();
            m.ColorMode = 1;
            _rgb.UpdateMode(c.Index, direct, Sanitise(m));
            if (c.Colors.Count > 0) _rgb.UpdateLeds(c.Index, Enumerable.Repeat(color, c.Colors.Count).ToList());
            return true;
        }
        var fixedMode = c.Modes.FindIndex(m => m.Name == "Static" && m.Has(RgbMode.ModeSpecificColor));
        if (fixedMode >= 0)
        {
            var m = c.Modes[fixedMode].Clone();
            var count = (int)Math.Clamp((uint)Math.Max(1, m.Colors.Count), Math.Max(1u, m.ColorsMin), Math.Max(1u, m.ColorsMax));
            m.ColorMode = 2;
            m.Colors = Enumerable.Repeat(color, count).ToList();
            _rgb.UpdateMode(c.Index, fixedMode, Sanitise(m));
            return true;
        }
        return false;
    }

    private LightingStatusDto Refresh()
    {
        // Re-reading also flushes any write still queued inside OpenRGB and shows what the devices really took.
        _controllers = _rgb.ReadControllers();
        return BuildStatus();
    }

    private T Guard<T>(Func<T> action)
    {
        RequireConnected();
        try
        {
            var result = action();
            // Any change of colour or effect is announced, so the room lights can follow the PC.
            if (result is LightingStatusDto status) PublishChanged(status);
            return result;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _rgb.Disconnect();
            _controllers = Array.Empty<RgbController>();
            _message = "The connection to OpenRGB was lost: " + Plain(ex);
            SetStatus();
            throw RpcException.Unavailable(_message);
        }
    }

    private LightingStatusDto SetColor(ColorArgs a) => Guard(() =>
    {
        var color = ParseColor(a.Color);
        var targets = a.DeviceIds is null ? Driven.ToList() : a.DeviceIds.Select(Device).ToList();
        var skipped = targets.Where(c => !ApplyColor(c, color)).Select(c => c.Name).ToList();
        if (a.DeviceIds is not null && skipped.Count > 0)
            throw RpcException.Unavailable($"{string.Join(", ", skipped)} has no mode that takes a single colour.");
        return Refresh();
    });

    private LightingStatusDto SetZoneColor(ZoneColorArgs a) => Guard(() =>
    {
        var color = ParseColor(a.Color);
        var c = Device(a.DeviceId);
        if (a.ZoneId < 0 || a.ZoneId >= c.Zones.Count) throw RpcException.InvalidArgs("Unknown zone.");
        var direct = c.Modes.FindIndex(m => m.Name == "Direct" && m.Has(RgbMode.PerLedColor));
        if (direct < 0) throw RpcException.Unavailable($"{c.Name} cannot colour its zones separately.");
        var slots = c.Zones[a.ZoneId].ColorSlots;
        if (slots <= 0) throw RpcException.Unavailable("That zone has no lights.");
        var m = c.Modes[direct].Clone();
        m.ColorMode = 1;
        _rgb.UpdateMode(c.Index, direct, Sanitise(m));
        _rgb.UpdateZoneLeds(c.Index, a.ZoneId, Enumerable.Repeat(color, slots).ToList());
        return Refresh();
    });

    private static bool InRange(uint value, uint a, uint b) => value >= Math.Min(a, b) && value <= Math.Max(a, b);

    private LightingStatusDto SetMode(ModeArgs a) => Guard(() =>
    {
        var c = Device(a.DeviceId);
        if (a.ModeId < 0 || a.ModeId >= c.Modes.Count) throw RpcException.InvalidArgs("Unknown effect.");
        var m = c.Modes[a.ModeId].Clone();

        if (a.Speed is { } speed && m.Has(RgbMode.HasSpeed))
        {
            if (speed < 0 || !InRange((uint)speed, m.SpeedMin, m.SpeedMax)) throw RpcException.InvalidArgs("That speed is outside what this effect allows.");
            m.Speed = (uint)speed;
        }
        if (a.Brightness is { } brightness && m.Has(RgbMode.HasBrightness))
        {
            if (brightness < 0 || !InRange((uint)brightness, m.BrightnessMin, m.BrightnessMax)) throw RpcException.InvalidArgs("That brightness is outside what this effect allows.");
            m.Brightness = (uint)brightness;
        }

        var colors = a.Colors?.Select(ParseColor).ToList();
        if (m.Has(RgbMode.ModeSpecificColor))
        {
            if (colors is { Count: > 0 })
            {
                var min = (int)Math.Max(1u, m.ColorsMin);
                var max = (int)Math.Max((uint)min, m.ColorsMax);
                while (colors.Count < min) colors.Add(colors[^1]);
                if (colors.Count > max) colors = colors.Take(max).ToList();
                m.Colors = colors;
            }
            m.ColorMode = 2;
        }
        else if (m.Has(RgbMode.PerLedColor))
        {
            m.ColorMode = 1;
        }
        else if (m.ColorMode is 1 or 2)
        {
            m.ColorMode = m.Has(RgbMode.RandomColor) ? 3u : 0u;
        }

        _rgb.UpdateMode(c.Index, a.ModeId, Sanitise(m));
        if (m.ColorMode == 1 && colors is { Count: > 0 } && c.Colors.Count > 0)
            _rgb.UpdateLeds(c.Index, Enumerable.Repeat(colors[0], c.Colors.Count).ToList());
        return Refresh();
    });

    // ---------------------------------------------------------------- GIGABYTE Control Center (user-triggered only)

    /// <summary>Closes GCC and turns its start-up task off, so it stops overriding the graphics card's lights.</summary>
    private object TakeOverGcc()
    {
        if (!(_ctx?.Environment.IsElevated ?? false))
            throw RpcException.NeedsAdmin("Taking over from GIGABYTE Control Center needs administrator rights.");
        _log.Info("Taking over from GIGABYTE Control Center at the user's request (closing it, turning its start-up task off).");
        try { _gcc.SetStartsWithWindows(false); }
        catch (Exception ex) { throw RpcException.Unavailable(ex.Message); }
        if (!_gcc.Close()) throw RpcException.Unavailable("GIGABYTE Control Center is still running. Close it from its tray icon.");
        if (_settings.LeftOut.Count > 0)
        {
            _settings.LeftOut.Clear();
            SaveSettings();
        }
        var status = BuildStatus();
        _ctx?.Events.Publish("lighting.changed", status);
        return new { gcc = _gcc.GetState(), status };
    }

    /// <summary>Lets GCC start with Windows again (only if RigControl turned that off). It is not started now.</summary>
    private object GiveBackGcc()
    {
        if (!(_ctx?.Environment.IsElevated ?? false))
            throw RpcException.NeedsAdmin("This needs administrator rights.");
        try { _gcc.SetStartsWithWindows(true); }
        catch (Exception ex) { throw RpcException.Unavailable(ex.Message); }
        // GCC runs Gigabyte graphics cards' lights from now on; RigControl stops sending them anything (two programs
        // writing to the card's light chip is also what locks it up).
        foreach (var c in _controllers.Where(IsGigabyteCard))
            if (!_settings.LeftOut.Contains(c.Key)) _settings.LeftOut.Add(c.Key);
        SaveSettings();
        _gcc.Start();
        _log.Info("GIGABYTE Control Center runs the graphics card's lights again (started, and starts with Windows); RigControl leaves the card alone.");
        var status = BuildStatus();
        _ctx?.Events.Publish("lighting.changed", status);
        return new { gcc = _gcc.GetState(), status };
    }

    /// <summary>Streaming programs that show the capture card themselves; while one runs, RigControl only shares the card.</summary>
    private static readonly string[] Streamers = { "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core" };

    private bool StreamerRunning() => Streamers.Any(SafeRunning);

    private static bool IsGigabyteCard(RgbController c) => c.Type == 2 && (c.Name + " " + c.Vendor).Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- lights follow the screen or a capture card

    /// <summary>
    /// The lights' colour (0xRRGGBB) for things that match them (the cooler screen): the live colour while an effect
    /// plays, otherwise the colour most of the lights show; null when there is none.
    /// </summary>
    public event Action<int?>? SyncColorChanged;

    /// <summary>Announces a status change, and (while no effect plays) the lights' main colour.</summary>
    private void PublishChanged(LightingStatusDto status)
    {
        _ctx?.Events.Publish("lighting.changed", status);
        if (_settings.Sync.Enabled) return;
        var main = MainColor(status);
        try { SyncColorChanged?.Invoke(main); } catch { /* a listener's problem stays its own */ }
    }

    /// <summary>The colour most of the lights show (counted by LEDs), ignoring off and left-out devices.</summary>
    internal static int? MainColor(LightingStatusDto status)
    {
        var hex = status.Devices.Where(d => !d.LeftOut && d.Color is { } c && !c.Equals("#000000", StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d.Color!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(d => Math.Max(1, d.LedCount))).Select(g => g.Key).FirstOrDefault();
        return hex is not null && int.TryParse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
    }

    // ---------------------------------------------------------------- room lights on the layout

    /// <summary>A light outside the PC (a room light an add-on knows) that can be placed on the layout.</summary>
    public sealed record ExternalLight(string Key, string Name);

    /// <summary>The room lights that can be placed (set by the app from an add-on; only lights that are online).</summary>
    public Func<IReadOnlyList<ExternalLight>>? ExternalLights { get; set; }

    /// <summary>A placed room light's colour (key, 0xRRGGBB), on every step of an effect; the receiver throttles.</summary>
    public event Action<string, int>? ExternalLightColor;

    private IReadOnlyList<ExternalLight> SafeExternalLights()
    {
        IReadOnlyList<ExternalLight> list;
        try { list = ExternalLights?.Invoke() ?? Array.Empty<ExternalLight>(); }
        catch { list = Array.Empty<ExternalLight>(); }
        // A Nanoleaf paired directly is placed panel by panel; its one-colour room-light entry would be a second copy.
        return _settings.Nanoleaf is null ? list : list.Where(l => !IsNanoleafName(l.Name)).ToList();
    }

    public static bool IsNanoleafName(string name) => name.Contains("nanoleaf", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Nanoleaf, panel by panel

    private volatile List<NanoleafPanel>? _nlPanels;
    private volatile string? _nlProblem;
    private int _nlLoading;
    private volatile bool _nlStreaming;

    /// <summary>True while the paired Nanoleaf takes a colour per panel from RigControl (the hub must leave it alone).</summary>
    public bool NanoleafStreaming => _nlStreaming;

    private string? NanoleafKey => _settings.Nanoleaf is { } n ? $"nanoleaf|{n.Name}|{n.Serial}" : null;

    private NanoleafDto NanoleafStatus(IReadOnlyList<string>? found = null)
    {
        var n = _settings.Nanoleaf;
        if (n is not null) EnsureNanoleafLoaded();
        return new NanoleafDto(n is not null, n?.Ip, n?.Name, _nlPanels?.Count ?? 0, _nlStreaming, n is null ? null : _nlProblem, found);
    }

    /// <summary>Reads the panels in the background; if the Nanoleaf moved to another address, finds it again.</summary>
    private void EnsureNanoleafLoaded()
    {
        var n = _settings.Nanoleaf;
        if (n is null || _nlPanels is not null || Interlocked.Exchange(ref _nlLoading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var token = n.Token() ?? throw new InvalidOperationException("Its pairing can't be read on this Windows account; pair it again.");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                // The token only ever goes to the address it was paired at: trying it on whatever else answers on
                // port 16021 would hand it to any device pretending to be a Nanoleaf. If it moved, the user pairs again.
                List<NanoleafPanel>? panels = null;
                try
                {
                    using var c = new NanoleafClient(n.Ip, token);
                    panels = await c.PanelsAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!cts.IsCancellationRequested)
                {
                    // reported below
                }
                if (panels is null)
                    throw new InvalidOperationException($"It doesn't answer at {n.Ip}. If it got a new address, press Forget, then Find my Nanoleaf and pair it again (its place on the layout is kept).");
                _nlPanels = panels;
                _nlProblem = null;
            }
            catch (Exception ex)
            {
                if (_nlProblem != ex.Message) _log.Warn($"Nanoleaf: {ex.Message}");
                _nlProblem = ex.Message;
            }
            finally
            {
                Volatile.Write(ref _nlLoading, 0);
            }
        });
    }

    private async Task<object?> FindNanoleafAsync(CancellationToken ct)
    {
        List<string> found;
        try { found = await NanoleafClient.DiscoverAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { throw RpcException.Unavailable("Couldn't look for a Nanoleaf: " + ex.Message); }
        _log.Info(found.Count == 0 ? "Nanoleaf: none found on the network." : "Nanoleaf found at " + string.Join(", ", found) + ".");
        return NanoleafStatus(found);
    }

    private async Task<object?> PairNanoleafAsync(NanoleafArgs a, CancellationToken ct)
    {
        if (a.Ip is not { } ip || !IPAddressIsLocal(ip)) throw RpcException.InvalidArgs("Pick a Nanoleaf found on your network.");
        string token;
        try { token = await NanoleafClient.PairAsync(ip, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { throw RpcException.Unavailable(ex.Message); }
        catch (Exception ex) { throw RpcException.Unavailable("The Nanoleaf didn't answer: " + ex.Message); }
        using var c = new NanoleafClient(ip, token);
        (string Name, string Serial) info;
        List<NanoleafPanel> panels;
        try
        {
            info = await c.InfoAsync(ct).ConfigureAwait(false);
            panels = await c.PanelsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable("Paired, but its panels couldn't be read: " + ex.Message);
        }
        lock (_gate)
        {
            var n = new NanoleafSettings { Ip = ip, Name = info.Name, Serial = info.Serial };
            n.SetToken(token);
            _settings.Nanoleaf = n;
            _nlPanels = panels;
            _nlProblem = null;
            // Where its one-colour room-light box was placed, its panels go now.
            foreach (var item in _settings.Layout.Items.Where(i => i.Key.StartsWith(ExternalKeyPrefix, StringComparison.Ordinal) && IsNanoleafName(i.Key)))
                item.Key = NanoleafKey!;
            _settings.Layout.Items = _settings.Layout.Items.GroupBy(i => (i.Key, i.Zone)).Select(g => g.Last()).ToList();
            SaveSettings();
        }
        _log.Info($"Nanoleaf {info.Name} paired at {ip}: {panels.Count} panel(s), each can follow its own part of the effects.");
        return NanoleafStatus();
    }

    private static bool IPAddressIsLocal(string ip) =>
        System.Net.IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && a.GetAddressBytes() is var b && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));

    private NanoleafDto ForgetNanoleaf()
    {
        lock (_gate)
        {
            // Its box stays on the layout (shown as not connected), so pairing the same Nanoleaf again puts it back in place.
            _settings.Nanoleaf = null;
            _nlPanels = null;
            _nlProblem = null;
            SaveSettings();
        }
        _log.Info("Nanoleaf forgotten; it follows as one room light through the hub again, if one is set up.");
        return NanoleafStatus();
    }

    /// <summary>A rough grid of where the panels are, so the layout page can draw them.</summary>
    private static (int W, int H, List<int> Matrix) PanelMatrix(IReadOnlyList<NanoleafPanel> panels)
    {
        const int W = 20, H = 12;
        var m = Enumerable.Repeat(-1, W * H).ToList();
        for (var i = 0; i < panels.Count; i++)
        {
            var cell = (int)Math.Round(panels[i].Y * (H - 1)) * W + (int)Math.Round(panels[i].X * (W - 1));
            if (m[cell] < 0) m[cell] = i;
        }
        return (W, H, m);
    }

    /// <summary>How often the colour is read and sent (about 15 times a second, as RichPresence).</summary>
    public int SyncIntervalMs { get; init; } = 66;

    /// <summary>The least time between two colours sent to a graphics card while the lights follow the screen.</summary>
    public int GentleIntervalMs { get; init; } = 1000;

    /// <summary>Devices whose light chip can't take fast updates: graphics cards (OpenRGB type 2).</summary>
    internal static bool IsGentle(RgbController dev) => dev.Type == 2;

    /// <summary>Keys of room lights on the layout start with this.</summary>
    public const string ExternalKeyPrefix = "homelab|";

    /// <summary>The colour the room lights should follow; at most every 2 s so the hub isn't flooded.</summary>
    private DateTime _lastRoomColor = DateTime.MinValue;

    private volatile string? _bassProblem;

    private SyncDto SyncStatus()
    {
        var s = _settings.Sync;
        var list = _captureDevices.ToList();
        if (s.Source != "" && list.All(d => d.Id != s.Source))
            list.Add(new CaptureDeviceDto(s.Source, (s.SourceName != "" ? s.SourceName : "capture card") + " (not plugged in)", false));
        return new SyncDto(s.Enabled, s.Source, s.SourceName == "" ? null : s.SourceName, s.Brightness, s.Enabled ? _syncStatus : "Off",
            _syncColor is { } c ? ColorMath.Hex(c) : null, list, _captureSize, s.Vivid, s.Bass, s.BassStrength,
            s.Enabled && (s.Bass || s.Mid || s.Treble || EffectRenderer.IsAudio(s.Effect)) ? _bassProblem : null, s.Treble, s.TrebleStrength, s.AudioGain,
            s.Mid, s.MidStrength, s.Effect, EffectRenderer.Catalog,
            EffectRenderer.Catalog.ToDictionary(e => e.Id, e => s.ParamsFor(e.Id)));
    }

    private async Task<object?> ListSyncSourcesAsync()
    {
        try { _captureDevices = await CaptureSource.ListAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn("Couldn't list capture devices: " + ex.Message); }
        lock (_gate) return SyncStatus();
    }

    private SyncDto SetSync(SyncArgs a)
    {
        var s = _settings.Sync;
        if (a.Brightness is { } b)
        {
            if (b < 1 || b > 100) throw RpcException.InvalidArgs("Brightness is 1 to 100%.");
            s.Brightness = b;
        }
        if (a.Vivid is { } vivid) s.Vivid = vivid;
        if (a.Bass is { } bass && bass != s.Bass)
        {
            s.Bass = bass;
            _log.Info(bass ? "Light sync: brightness follows the bass." : "Light sync: brightness no longer follows the bass.");
        }
        if (a.BassStrength is { } bs)
        {
            if (bs < 10 || bs > 100) throw RpcException.InvalidArgs("Bass strength is 10 to 100%.");
            s.BassStrength = bs;
        }
        if (a.Mid is { } mid && mid != s.Mid)
        {
            s.Mid = mid;
            _log.Info(mid ? "Light sync: the mid range shifts the colour." : "Light sync: the mid range no longer shifts the colour.");
        }
        if (a.MidStrength is { } ms)
        {
            if (ms < 10 || ms > 100) throw RpcException.InvalidArgs("Mid strength is 10 to 100%.");
            s.MidStrength = ms;
        }
        if (a.Treble is { } treble && treble != s.Treble)
        {
            s.Treble = treble;
            _log.Info(treble ? "Light sync: the treble flashes the lights." : "Light sync: the treble no longer flashes the lights.");
        }
        if (a.TrebleStrength is { } ts)
        {
            if (ts < 10 || ts > 100) throw RpcException.InvalidArgs("Treble strength is 10 to 100%.");
            s.TrebleStrength = ts;
        }
        if (a.AudioGain is { } g)
        {
            if (g < 25 || g > 400) throw RpcException.InvalidArgs("Gain is 25 to 400%.");
            s.AudioGain = g;
        }
        var effectSwitched = false;
        if (a.Effect is { } effect && effect != s.Effect)
        {
            effectSwitched = true;
            if (!EffectRenderer.Exists(effect)) throw RpcException.InvalidArgs("There's no effect called " + effect + ".");
            s.Effect = effect;
            _log.Info("Light effect: " + EffectRenderer.Catalog.First(e => e.Id == effect).Name + ".");
        }
        if (a.Params is { } ps)
        {
            var id = a.Effect ?? s.Effect;
            if (ps.Speed is < 1 or > 100 || ps.Size is < 1 or > 100) throw RpcException.InvalidArgs("Speed and size are 1 to 100.");
            if (ps.Direction is not ("right" or "left" or "up" or "down")) throw RpcException.InvalidArgs("Direction is right, left, up or down.");
            if (EffectRenderer.ParseHex(ps.Color1, -1) < 0 || EffectRenderer.ParseHex(ps.Color2, -1) < 0)
                throw RpcException.InvalidArgs("Colours are #RRGGBB.");
            s.EffectParams[id] = ps.Clone();
        }
        var sourceChanged = false;
        if (a.Source is { } src && src != s.Source)
        {
            if (src != "" && _captureDevices.All(d => d.Id != src)) throw RpcException.InvalidArgs("That capture device isn't plugged in.");
            s.Source = src;
            s.SourceName = src == "" ? "" : _captureDevices.First(d => d.Id == src).Name;
            sourceChanged = true;
        }
        SaveSettings();
        if (a.Enabled == true && !s.Enabled)
        {
            RequireConnected();
            // Remember the lights as they are, to put them back when the sync stops.
            _controllers = _rgb.ReadControllers();
            s.Before = Snapshot("Before light sync");
            s.Enabled = true;
            SaveSettings();
            StartSync();
            _log.Info(s.Effect == EffectRenderer.Screen ? "Lights follow " + (s.Source == "" ? "the screen" : s.SourceName) + "."
                : "Lights play the effect " + EffectRenderer.Catalog.First(e => e.Id == s.Effect).Name + ".");
        }
        else if (a.Enabled == false && s.Enabled)
        {
            StopSync(restore: true);
        }
        else if ((sourceChanged || effectSwitched) && s.Enabled)
        {
            StartSync(); // a new run with the new source
        }
        return SyncStatus();
    }

    // ---------------------------------------------------------------- per-LED layout

    private LayoutDto LayoutStatus()
    {
        var layout = _settings.Layout;
        var zones = new List<LayoutZoneDto>();
        var seen = new HashSet<(string, int)>();
        foreach (var dev in Driven)
        {
            if (dev.Colors.Count == 0) continue;
            // The whole device first (the usual way to place it), then each of its zones.
            var (whole, _) = LedLayout.WholeZone(dev);
            seen.Add((dev.Key, LedLayout.WholeDevice));
            zones.Add(new LayoutZoneDto(dev.Key, dev.Name, LedLayout.WholeDevice, "Whole device", (int)whole.LedsCount, whole.MatrixWidth,
                whole.MatrixHeight, whole.Matrix?.Select(v => v >= whole.LedsCount ? -1 : (int)v).ToList(), true,
                layout.Items.FirstOrDefault(i => i.Key == dev.Key && i.Zone == LedLayout.WholeDevice)));
            for (var z = 0; z < dev.Zones.Count; z++)
            {
                var zone = dev.Zones[z];
                if (zone.LedsCount == 0) continue;
                seen.Add((dev.Key, z));
                var placed = layout.Items.FirstOrDefault(i => i.Key == dev.Key && i.Zone == z);
                zones.Add(new LayoutZoneDto(dev.Key, dev.Name, z, zone.Name, (int)zone.LedsCount, zone.MatrixWidth, zone.MatrixHeight,
                    zone.Matrix?.Select(v => v >= zone.LedsCount ? -1 : (int)v).ToList(), true, placed));
            }
        }
        if (NanoleafKey is { } nlKey && _settings.Nanoleaf is { } nl)
        {
            EnsureNanoleafLoaded();
            var panels = _nlPanels;
            seen.Add((nlKey, LedLayout.WholeDevice));
            var (mw, mh, matrix) = panels is null ? (0, 0, (List<int>?)null) : PanelMatrix(panels);
            zones.Add(new LayoutZoneDto(nlKey, nl.Name + " (Nanoleaf)", LedLayout.WholeDevice, "Whole device", Math.Max(1, panels?.Count ?? 1), mw, mh,
                matrix, panels is not null, layout.Items.FirstOrDefault(i => i.Key == nlKey && i.Zone == LedLayout.WholeDevice),
                // Each Lines bar as drawn on the wall (0-1 inside the box), so the page can show the real shape.
                panels?.Select(p => new[] { p.X1, p.Y1, p.X2, p.Y2 }).ToList()));
        }
        // Room lights from an add-on are one box each.
        foreach (var ext in SafeExternalLights())
        {
            if (!seen.Add((ext.Key, LedLayout.WholeDevice))) continue;
            zones.Add(new LayoutZoneDto(ext.Key, ext.Name + " (room light)", LedLayout.WholeDevice, "Whole device", 1, 0, 0, null, true,
                layout.Items.FirstOrDefault(i => i.Key == ext.Key && i.Zone == LedLayout.WholeDevice)));
        }
        // Placed zones of devices that aren't connected right now stay on the layout.
        foreach (var item in layout.Items)
        {
            if (seen.Contains((item.Key, item.Zone))) continue;
            var name = item.Key.Split('|') is { Length: > 1 } parts ? parts[1] : "Device";
            zones.Add(new LayoutZoneDto(item.Key, name, item.Zone, item.Zone == LedLayout.WholeDevice ? "Whole device" : $"Zone {item.Zone + 1}", 1, 0, 0, null, false, item));
        }
        return new LayoutDto(layout.PerLed, zones);
    }

    private LayoutDto SetLayout(LayoutArgs a)
    {
        var layout = _settings.Layout;
        if (a.Items is { } items)
        {
            try { layout.Items = LedLayout.Clean(items); }
            catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        }
        if (a.PerLed is { } perLed && perLed != layout.PerLed)
        {
            layout.PerLed = perLed;
            _log.Info(perLed ? "Light sync: per LED (each placed light follows its part of the picture)." : "Light sync: one colour for every light.");
        }
        SaveSettings();
        return LayoutStatus();
    }

    private SyncPreviewDto? Preview()
    {
        var grid = _previewGrid;
        if (grid is null || !_settings.Sync.Enabled) return null;
        var sb = new System.Text.StringBuilder(ColorMath.GridW * ColorMath.GridH * 6);
        for (var i = 0; i + 3 < grid.Length; i += 4) sb.Append(grid[i + 2].ToString("X2")).Append(grid[i + 1].ToString("X2")).Append(grid[i].ToString("X2"));
        return new SyncPreviewDto(ColorMath.GridW, ColorMath.GridH, sb.ToString());
    }

    private Scene Snapshot(string name)
    {
        var scene = new Scene { Name = name, SavedAt = DateTimeOffset.Now.ToString("o") };
        foreach (var c in Driven)
        {
            if (c.ActiveMode < 0 || c.ActiveMode >= c.Modes.Count) continue;
            var m = c.Modes[c.ActiveMode];
            scene.Devices.Add(new SceneDevice { Key = c.Key, ModeName = m.Name, Speed = m.Speed, Brightness = m.Brightness,
                ModeColors = new List<uint>(m.Colors), LedColors = new List<uint>(c.Colors) });
        }
        return scene;
    }

    private void StartSync()
    {
        var gen = Interlocked.Increment(ref _syncGen);
        _syncStatus = "Starting";
        _syncThread = new Thread(() => SyncLoop(gen)) { IsBackground = true, Name = "RigControl light sync" };
        _syncThread.Start();
    }

    /// <summary>Stops the sync; with restore, the lights go back to how they were when it started.</summary>
    private void StopSync(bool restore)
    {
        Interlocked.Increment(ref _syncGen);
        var t = _syncThread;
        if (t is not null && t != Thread.CurrentThread && !Monitor.IsEntered(_gate)) t.Join(2000);
        var s = _settings.Sync;
        s.Enabled = false;
        var before = s.Before;
        s.Before = null;
        SaveSettings();
        _syncColor = null;
        _previewGrid = null;
        _syncStatus = "Off";
        try { SyncColorChanged?.Invoke(null); } catch { /* ignore */ }
        if (restore && before is not null)
        {
            lock (_gate)
            {
                try
                {
                    if (_rgb.Connected)
                    {
                        _controllers = _rgb.ReadControllers();
                        ApplySceneCore(before);
                        _controllers = _rgb.ReadControllers();
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Couldn't put the lights back after the sync.", ex);
                }
            }
        }
        _log.Info(restore ? "Light sync off; lights put back as they were." : "Light sync off (lights changed by hand).");
        _ctx?.Events.Publish("lighting.sync", SyncStatus());
        LightingStatusDto? after = null;
        lock (_gate)
        {
            try { if (_rgb.Connected) after = BuildStatus(); } catch { /* nothing to announce */ }
        }
        if (after is not null) PublishChanged(after);
    }

    private void SyncLoop(int gen)
    {
        bool Live() => Volatile.Read(ref _syncGen) == gen && _disposed == 0;
        var s = _settings.Sync;
        var source = s.Effect == EffectRenderer.Screen ? s.Source : "";
        using var screen = new ScreenSource();
        var effects = new EffectRenderer();
        var effectGrid = new byte[ColorMath.GridW * ColorMath.GridH * 4];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        float rawBass = 0, rawMid = 0, rawTreble = 0;
        NanoleafClient? nl = null;
        string? nlBefore = null;
        var nlStarted = DateTime.MinValue;
        var nlSent = DateTime.MinValue;
        var nlRetry = DateTime.MinValue;
        void StopNanoleaf()
        {
            if (nl is null) return;
            _nlStreaming = false;
            try
            {
                // Its own effect from before comes back.
                if (nlBefore is { Length: > 0 } before && !before.StartsWith("*", StringComparison.Ordinal))
                    nl.SelectEffectAsync(before, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { /* it keeps the last colours */ }
            nl.Dispose();
            nl = null;
            _log.Info("Nanoleaf: panel-by-panel colours stopped.");
        }
        CaptureSource? capture = null;
        ObsSource? obs = null;
        BassMeter? bass = null;
        var bassFactor = 1.0;
        var trebleLevel = 0f;
        var midLevel = 0f;
        // On top of the colour: the mids turn its hue, a treble hit mixes it toward white, then the bass sets the brightness.
        (double R, double G, double B) Pulse((double R, double G, double B) c)
        {
            if (midLevel > 0) c = BassFollower.Shift(c, midLevel, s.MidStrength);
            if (trebleLevel > 0) c = BassFollower.Sparkle(c, trebleLevel, s.TrebleStrength, s.Brightness);
            return bassFactor >= 0.999 ? c : (c.R * bassFactor, c.G * bassFactor, c.B * bassFactor);
        }
        string? captureProblem = null;
        (double R, double G, double B) cur = (0, 0, 0);
        uint last = uint.MaxValue;
        var sent = DateTime.MinValue;
        var direct = new HashSet<int>();
        var lastSlow = DateTime.MinValue;
        var target = 0;
        byte[]? grid = null;
        var perLed = new Dictionary<int, (double R, double G, double B)[]>();
        var gentle = new Dictionary<int, (DateTime At, List<uint> Colors)>();

        // Graphics cards take colours over the card's own I2C bus; Gigabyte's light chip locks up (until the PC
        // restarts) when written ~15 times a second. They get a colour at most once a second, and only on a real change.
        void SendLeds(RgbController dev, List<uint> colors)
        {
            if (!IsGentle(dev))
            {
                _rgb.UpdateLeds(dev.Index, colors);
                return;
            }
            var now = DateTime.UtcNow;
            if (gentle.TryGetValue(dev.Index, out var prev))
            {
                if ((now - prev.At).TotalMilliseconds < GentleIntervalMs) return;
                if ((now - prev.At).TotalSeconds < 10 && !ColorMath.Differs(prev.Colors, colors, 12)) return;
            }
            _rgb.UpdateLeds(dev.Index, colors);
            gentle[dev.Index] = (now, colors);
        }
        try
        {
            var cardName = s.SourceName != "" ? s.SourceName : "the capture card";
            var lastOpen = DateTime.MinValue;
            var lastStreamerCheck = DateTime.MinValue;
            var streamerSeen = false;
            // Opens the card itself (only while no streaming program runs: many USB cards open in one program at a time).
            void OpenCapture(bool quiet)
            {
                lastOpen = DateTime.UtcNow;
                var c = new CaptureSource();
                try
                {
                    c.StartAsync(source, allowExclusive: !streamerSeen).GetAwaiter().GetResult();
                    capture = c;
                    if (captureProblem is not null || !quiet)
                        _log.Info($"Light sync: {cardName} opened {(c.Shared ? "shared" : "for RigControl (nothing else was using it)")}.");
                    captureProblem = null;
                }
                catch (Exception ex)
                {
                    c.Dispose();
                    var problem = Inner(ex).Message;
                    if (problem != captureProblem)
                        _log.Warn($"Light sync: {cardName} can't be used ({problem}); following the screen and trying again every 10 s.");
                    captureProblem = problem;
                }
            }
            // While OBS has the card, its picture comes from OBS instead.
            void OpenObs(bool quiet)
            {
                lastOpen = DateTime.UtcNow;
                var o = new ObsSource();
                try
                {
                    o.StartAsync(s.SourceName).GetAwaiter().GetResult();
                    obs = o;
                    if (captureProblem is not null || !quiet)
                        _log.Info($"Light sync: OBS is running, so {cardName} is left to OBS; following its picture through OBS (source \"{o.SourceName}\").");
                    captureProblem = null;
                }
                catch (Exception ex)
                {
                    o.Dispose();
                    var problem = "OBS is using it and its picture can't be read from OBS: " + Inner(ex).Message;
                    if (problem != captureProblem)
                        _log.Warn($"Light sync: {cardName}: {problem}; following the screen and trying again every 10 s.");
                    captureProblem = problem;
                }
            }
            if (source != "")
            {
                streamerSeen = StreamerRunning();
                if (streamerSeen) OpenObs(quiet: false);
                else OpenCapture(quiet: false);
            }
            while (Live())
            {
                try
                {
                    if (source != "" && (DateTime.UtcNow - lastStreamerCheck).TotalSeconds >= 2)
                    {
                        lastStreamerCheck = DateTime.UtcNow;
                        var was = streamerSeen;
                        streamerSeen = StreamerRunning();
                        if (streamerSeen && !was)
                        {
                            // OBS started: let go of the card at once so OBS can open it, then read it through OBS.
                            if (capture is not null)
                            {
                                _log.Info($"Light sync: OBS started; letting go of {cardName} so OBS can use it.");
                                capture.Dispose();
                                capture = null;
                            }
                            OpenObs(quiet: false);
                        }
                        else if (!streamerSeen && was)
                        {
                            obs?.Dispose();
                            obs = null;
                            Thread.Sleep(1000); // give OBS a moment to let go of the card
                            OpenCapture(quiet: false);
                        }
                    }
                    if (source != "" && capture is null && obs is null && (DateTime.UtcNow - lastOpen).TotalSeconds >= 10)
                    {
                        if (streamerSeen) OpenObs(quiet: true);
                        else OpenCapture(quiet: true);
                    }
                    if (capture is not null)
                    {
                        try
                        {
                            if (capture.Read() is { } c)
                            {
                                target = c;
                                grid = capture.Grid;
                            }
                            if (capture.Size != _captureSize && capture.Size is not null)
                                _log.Info($"Light sync: reading {cardName} at {capture.Size}.");
                            _captureSize = capture.Size;
                        }
                        catch (Exception ex)
                        {
                            captureProblem = Inner(ex).Message;
                            capture.Dispose();
                            capture = null;
                            lastOpen = DateTime.UtcNow;
                            _log.Warn($"Light sync: {captureProblem}; following the screen and trying again every 10 s.");
                        }
                    }
                    else if (obs is not null)
                    {
                        try
                        {
                            if (obs.Read() is { } c)
                            {
                                target = c;
                                grid = obs.Grid;
                            }
                            _captureSize = "through OBS";
                        }
                        catch (Exception ex)
                        {
                            captureProblem = "OBS stopped answering: " + Inner(ex).Message;
                            obs.Dispose();
                            obs = null;
                            lastOpen = DateTime.UtcNow;
                            _log.Warn($"Light sync: {captureProblem}; following the screen and trying again every 10 s.");
                        }
                    }
                    var effect = s.Effect;
                    var isScreen = effect == EffectRenderer.Screen;
                    var picking = isScreen;
                    if (!isScreen)
                    {
                        // An effect from the library paints the picture instead of the screen.
                        target = effects.Render(effect, s.ParamsFor(effect), clock.Elapsed.TotalSeconds, rawBass, rawMid, rawTreble, effectGrid);
                        grid = effectGrid;
                    }
                    else if (capture is null && obs is null)
                    {
                        target = screen.Read();
                        grid = screen.Grid;
                    }
                    if (grid is not null) _previewGrid = (byte[])grid.Clone();
                    // Vivid is for the screen's colours; an effect's colours are already what was chosen.
                    var vivid = s.Vivid && picking;
                    var follow = picking ? 0.35 : 0.6;
                    if (vivid) target = ColorMath.Vivid(target);
                    cur = ColorMath.Smooth(cur, target, s.Brightness, follow);
                    var audioEffect = EffectRenderer.IsAudio(effect);
                    if (s.Bass || s.Mid || s.Treble || audioEffect)
                    {
                        bass ??= new BassMeter();
                        rawBass = BandFollower.WithGain(bass.Level, s.AudioGain);
                        rawMid = BandFollower.WithGain(bass.MidLevel, s.AudioGain);
                        rawTreble = BandFollower.WithGain(bass.TrebleLevel, s.AudioGain);
                        bassFactor = s.Bass ? BassFollower.Factor(BandFollower.WithGain(bass.Level, s.AudioGain), s.BassStrength) : 1;
                        trebleLevel = s.Treble ? BandFollower.WithGain(bass.TrebleLevel, s.AudioGain) : 0;
                        midLevel = s.Mid ? BandFollower.WithGain(bass.MidLevel, s.AudioGain) : 0;
                        if (bass.Problem != _bassProblem)
                        {
                            if (bass.Problem is not null) _log.Warn("Light sync: the bass can't be heard: " + bass.Problem);
                            _bassProblem = bass.Problem;
                        }
                    }
                    else if (bass is not null)
                    {
                        bass.Dispose();
                        bass = null;
                        bassFactor = 1;
                        trebleLevel = 0;
                        midLevel = 0;
                        rawBass = rawMid = rawTreble = 0;
                        _bassProblem = null;
                    }
                    var plain = ColorMath.ToOpenRgb(cur);
                    var color = ColorMath.ToOpenRgb(Pulse(cur));
                    var layout = _settings.Layout;
                    var items = layout.PerLed ? layout.Items : (IReadOnlyList<LayoutItem>)Array.Empty<LayoutItem>();
                    if (items.Count == 0) perLed.Clear();
                    // Per LED, the picture changes under the lights even when the average doesn't: send every step.
                    if (items.Count > 0 || color != last || (DateTime.UtcNow - sent).TotalSeconds > 2)
                    {
                        int devices;
                        lock (_gate)
                        {
                            if (!Live()) break;
                            if (!_rgb.Connected)
                            {
                                _syncStatus = "Waiting for OpenRGB";
                                direct.Clear();
                                goto sleep;
                            }
                            var slowDue = (DateTime.UtcNow - lastSlow).TotalMilliseconds >= 500;
                            devices = 0;
                            foreach (var dev in Driven)
                            {
                                if (dev.Colors.Count == 0) continue;
                                var di = dev.Modes.FindIndex(m => m.Name == "Direct" && m.Has(RgbMode.PerLedColor));
                                if (di >= 0)
                                {
                                    if (direct.Add(dev.Index))
                                    {
                                        var m = dev.Modes[di].Clone();
                                        m.ColorMode = 1;
                                        _rgb.UpdateMode(dev.Index, di, Sanitise(m));
                                    }
                                    var targets = items.Count > 0 ? LedLayout.Targets(dev, items, grid, ColorMath.GridW, ColorMath.GridH, target) : null;
                                    if (targets is null)
                                    {
                                        perLed.Remove(dev.Index);
                                        SendLeds(dev, Enumerable.Repeat(color, dev.Colors.Count).ToList());
                                    }
                                    else
                                    {
                                        // Each LED is smoothed on its own, the same way as the one colour.
                                        if (!perLed.TryGetValue(dev.Index, out var state) || state.Length != targets.Length)
                                            perLed[dev.Index] = state = Enumerable.Repeat(cur, targets.Length).ToArray();
                                        var colors = new List<uint>(targets.Length);
                                        for (var i = 0; i < targets.Length; i++)
                                        {
                                            if (vivid) targets[i] = ColorMath.Vivid(targets[i]);
                                            state[i] = ColorMath.Smooth(state[i], targets[i], s.Brightness, follow);
                                            colors.Add(ColorMath.ToOpenRgb(Pulse(state[i])));
                                        }
                                        SendLeds(dev, colors);
                                    }
                                    devices++;
                                }
                                else if (slowDue)
                                {
                                    // A device without Direct: a Static colour, at most twice a second (the part under it when placed).
                                    var one = color;
                                    if (items.Count > 0 && LedLayout.SingleColor(dev, items, grid, ColorMath.GridW, ColorMath.GridH) is { } c0
                                        && (vivid ? ColorMath.Vivid(c0) : c0) is var c1)
                                        one = ColorMath.ToOpenRgb(Pulse((((c1 >> 16) & 255) * s.Brightness / 100.0, ((c1 >> 8) & 255) * s.Brightness / 100.0, (c1 & 255) * s.Brightness / 100.0)));
                                    if (ApplyColor(dev, one)) devices++;
                                }
                            }
                            if (slowDue) lastSlow = DateTime.UtcNow;
                        }
                        if (items.Count > 0 && grid is not null && ExternalLightColor is not null)
                        {
                            foreach (var item in items)
                            {
                                if (!item.Key.StartsWith(ExternalKeyPrefix, StringComparison.Ordinal)) continue;
                                var rc = ColorMath.Region(grid, ColorMath.GridW, ColorMath.GridH, item.X, item.Y, item.W, item.H);
                                if (vivid) rc = ColorMath.Vivid(rc);
                                var k = s.Brightness / 100.0;
                                var pc = Pulse((((rc >> 16) & 255) * k, ((rc >> 8) & 255) * k, (rc & 255) * k));
                                var rgb = ((int)Math.Clamp(pc.R, 0, 255) << 16) | ((int)Math.Clamp(pc.G, 0, 255) << 8) | (int)Math.Clamp(pc.B, 0, 255);
                                try { ExternalLightColor(item.Key, rgb); } catch { /* the room lights' problem stays theirs */ }
                            }
                        }
                        // The paired Nanoleaf: each panel takes the part of the picture under it, about 10 times a second.
                        var nlItem = NanoleafKey is { } nk && grid is not null ? items.FirstOrDefault(i => i.Key == nk) : null;
                        var nlPanels = _nlPanels;
                        if (nlItem is null || nlPanels is null || nlPanels.Count == 0) StopNanoleaf();
                        else if ((DateTime.UtcNow - nlSent).TotalMilliseconds >= 100 && DateTime.UtcNow >= nlRetry)
                        {
                            try
                            {
                                if (nl is null)
                                {
                                    var token = _settings.Nanoleaf?.Token() ?? throw new InvalidOperationException("its pairing can't be read; pair it again");
                                    nl = new NanoleafClient(_settings.Nanoleaf!.Ip, token);
                                    nlBefore = nl.SelectedEffectAsync(CancellationToken.None).GetAwaiter().GetResult();
                                    nlStarted = DateTime.MinValue;
                                }
                                // Asked again every 20 s, in case the Nanoleaf app or the hub switched it to something else.
                                if ((DateTime.UtcNow - nlStarted).TotalSeconds >= 20)
                                {
                                    var first = nlStarted == DateTime.MinValue;
                                    nl.StartStreamAsync(CancellationToken.None).GetAwaiter().GetResult();
                                    nlStarted = DateTime.UtcNow;
                                    _nlStreaming = true;
                                    if (first) _log.Info($"Nanoleaf: {nlPanels.Count} panel(s) follow their part of the picture.");
                                }
                                var k = s.Brightness / 100.0;
                                var rw = Math.Max(nlItem.W * 0.12, 1.5 / ColorMath.GridW);
                                var rh = Math.Max(nlItem.H * 0.12, 1.5 / ColorMath.GridH);
                                var frame = new List<(int, int)>(nlPanels.Count);
                                foreach (var p in nlPanels)
                                {
                                    var px = Math.Clamp(nlItem.X + p.X * nlItem.W - rw / 2, 0, 1 - rw);
                                    var py = Math.Clamp(nlItem.Y + p.Y * nlItem.H - rh / 2, 0, 1 - rh);
                                    var rc = ColorMath.Region(grid!, ColorMath.GridW, ColorMath.GridH, px, py, rw, rh);
                                    if (vivid) rc = ColorMath.Vivid(rc);
                                    var pc = Pulse((((rc >> 16) & 255) * k, ((rc >> 8) & 255) * k, (rc & 255) * k));
                                    frame.Add((p.Id, ((int)Math.Clamp(pc.R, 0, 255) << 16) | ((int)Math.Clamp(pc.G, 0, 255) << 8) | (int)Math.Clamp(pc.B, 0, 255)));
                                }
                                nl.Send(frame, 1);
                                nlSent = DateTime.UtcNow;
                                _nlProblem = null;
                            }
                            catch (Exception ex)
                            {
                                var problem = Inner(ex).Message;
                                if (_nlProblem != problem) _log.Warn($"Nanoleaf: {problem}; trying again in 15 s.");
                                _nlProblem = problem;
                                nl?.Dispose();
                                nl = null;
                                _nlStreaming = false;
                                nlRetry = DateTime.UtcNow.AddSeconds(15);
                                _nlPanels = null; // read again (it may have moved to another address)
                            }
                        }
                        last = color;
                        sent = DateTime.UtcNow;
                        var hex = (int)(((plain & 0xFF) << 16) | (plain & 0xFF00) | ((plain >> 16) & 0xFF));
                        _syncColor = hex;
                        try { SyncColorChanged?.Invoke(hex); } catch { /* a listener's problem stays its own */ }
                        var from = obs is not null ? $"{(s.SourceName != "" ? s.SourceName : "the capture card")} through OBS"
                            : capture is not null ? (s.SourceName != "" ? s.SourceName : "the capture card")
                            : captureProblem is not null ? $"the screen ({(s.SourceName != "" ? s.SourceName : "capture card")}: {captureProblem})"
                            : "the screen";
                        _syncStatus = isScreen ? $"Following {from}" : "Playing " + EffectRenderer.Catalog.First(e => e.Id == effect).Name
                            + (items.Count == 0 ? " (one colour: place your devices on the layout to see it move across them)" : "");
                        if ((DateTime.UtcNow - _lastRoomColor).TotalSeconds >= 2)
                        {
                            _lastRoomColor = DateTime.UtcNow;
                            _ctx?.Events.Publish("lighting.syncColor", ColorMath.Hex(hex));
                            _ctx?.Events.Publish("lighting.sync", SyncStatus());
                            LightingStatusDto? status = null;
                            lock (_gate) { if (Live()) status = BuildStatus(); }
                            if (status is not null) _ctx?.Events.Publish("lighting.changed", status);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _syncStatus = "Problem: " + Inner(ex).Message;
                    _log.Warn("Light sync step failed.", ex);
                    lock (_gate)
                    {
                        try { _rgb.Disconnect(); } catch { /* reconnects below */ }
                        _controllers = Array.Empty<RgbController>();
                        direct.Clear();
                        gentle.Clear();
                    }
                    Thread.Sleep(3000);
                    lock (_gate)
                    {
                        try { if (!_rgb.Connected) StartAndConnect(); } catch { /* try again next time */ }
                    }
                }
            sleep:
                // With the bass on, twice as often (about 30 times a second) so short kicks aren't missed.
                Thread.Sleep(s.Bass || s.Mid || s.Treble || s.Effect != EffectRenderer.Screen ? Math.Min(SyncIntervalMs, 33) : SyncIntervalMs);
            }
        }
        finally
        {
            StopNanoleaf();
            capture?.Dispose();
            obs?.Dispose();
            bass?.Dispose();
            _bassProblem = null;
        }
    }

    private static Exception Inner(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    // ---------------------------------------------------------------- scenes

    private IReadOnlyList<SceneDto> ListScenes() => _settings.Scenes.Select(s => new SceneDto(s.Name, s.SavedAt)).ToList();

    private static string SceneName(SceneArgs a)
    {
        var name = (a.Name ?? "").Trim();
        if (name.Length is 0 or > 60) throw RpcException.InvalidArgs("Give the scene a name of up to 60 characters.");
        return name;
    }

    private IReadOnlyList<SceneDto> SaveScene(SceneArgs a) => Guard(() =>
    {
        var name = SceneName(a);
        _controllers = _rgb.ReadControllers();
        var scene = new Scene { Name = name, SavedAt = DateTimeOffset.Now.ToString("o") };
        foreach (var c in Driven)
        {
            if (c.ActiveMode < 0 || c.ActiveMode >= c.Modes.Count) continue;
            var m = c.Modes[c.ActiveMode];
            scene.Devices.Add(new SceneDevice { Key = c.Key, ModeName = m.Name, Speed = m.Speed, Brightness = m.Brightness,
                ModeColors = new List<uint>(m.Colors), LedColors = new List<uint>(c.Colors) });
        }
        _settings.Scenes.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.Scenes.Add(scene);
        SaveSettings();
        return ListScenes();
    });

    private LightingStatusDto ApplyScene(SceneArgs a) => Guard(() =>
    {
        var name = SceneName(a);
        var scene = _settings.Scenes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw RpcException.InvalidArgs("That scene no longer exists.");
        ApplySceneCore(scene);
        return Refresh();
    });

    private void ApplySceneCore(Scene scene)
    {
        foreach (var saved in scene.Devices)
        {
            var c = _controllers.FirstOrDefault(x => x.Key == saved.Key);
            if (c is null || IsLeftOut(c)) continue; // not connected right now, or left to its own program
            var index = c.Modes.FindIndex(m => m.Name == saved.ModeName);
            if (index < 0) continue;
            var m = c.Modes[index].Clone();
            if (m.Has(RgbMode.HasSpeed) && InRange(saved.Speed, m.SpeedMin, m.SpeedMax)) m.Speed = saved.Speed;
            if (m.Has(RgbMode.HasBrightness) && InRange(saved.Brightness, m.BrightnessMin, m.BrightnessMax)) m.Brightness = saved.Brightness;
            if (m.Has(RgbMode.ModeSpecificColor) && saved.ModeColors.Count >= Math.Max(1u, m.ColorsMin) && saved.ModeColors.Count <= Math.Max(1u, m.ColorsMax))
            {
                m.Colors = new List<uint>(saved.ModeColors);
                m.ColorMode = 2;
            }
            else if (m.Has(RgbMode.PerLedColor))
            {
                m.ColorMode = 1;
            }
            _rgb.UpdateMode(c.Index, index, Sanitise(m));
            if (m.ColorMode == 1 && saved.LedColors.Count == c.Colors.Count && c.Colors.Count > 0)
                _rgb.UpdateLeds(c.Index, saved.LedColors);
        }
    }

    private IReadOnlyList<SceneDto> DeleteScene(SceneArgs a)
    {
        var name = SceneName(a);
        _settings.Scenes.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        return ListScenes();
    }

    private void SaveSettings() => _ctx?.Settings.Set(LightingSettings.Section, _settings);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        Interlocked.Increment(ref _syncGen); // the sync keeps its setting, so it resumes at the next start
        try { _syncThread?.Join(2000); } catch { /* ignore */ }
        _stop.Cancel();
        _thread?.Join(3000);
        lock (_gate)
        {
            // Stops OpenRGB only if RigControl started it. Lights keep the last colours they were sent.
            try { _rgb.Dispose(); } catch { /* ignore */ }
        }
        _status = new ModuleStatus(ModuleState.Stopped);
        return ValueTask.CompletedTask;
    }

    private sealed class ColorArgs
    {
        public List<int>? DeviceIds { get; set; }
        public string? Color { get; set; }
    }

    private sealed class ZoneColorArgs
    {
        public int DeviceId { get; set; }
        public int ZoneId { get; set; }
        public string? Color { get; set; }
    }

    private sealed class ModeArgs
    {
        public int DeviceId { get; set; }
        public int ModeId { get; set; }
        public List<string>? Colors { get; set; }
        public int? Speed { get; set; }
        public int? Brightness { get; set; }
    }

    private sealed class SceneArgs
    {
        public string? Name { get; set; }
    }

    private sealed class NanoleafArgs
    {
        public string? Ip { get; set; }
    }

    private sealed class LayoutArgs
    {
        public bool? PerLed { get; set; }
        public List<LayoutItem>? Items { get; set; }
    }

    private sealed class SyncArgs
    {
        public bool? Enabled { get; set; }
        public string? Source { get; set; }
        public int? Brightness { get; set; }
        public bool? Vivid { get; set; }
        public bool? Bass { get; set; }
        public int? BassStrength { get; set; }
        public bool? Treble { get; set; }
        public int? TrebleStrength { get; set; }
        public bool? Mid { get; set; }
        public int? MidStrength { get; set; }
        public int? AudioGain { get; set; }
        public string? Effect { get; set; }
        public EffectParams? Params { get; set; }
    }
}
