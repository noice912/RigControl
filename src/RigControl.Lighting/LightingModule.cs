using System.Diagnostics;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Lighting.Network;

namespace RigControl.Lighting;

// ---------------------------------------------------------------- contract types (docs/API.md, "lighting")

public sealed record OpenRgbDto(bool Installed, string? Path, bool Running, bool Connected, int Port, string? Version, string? Message);

/// <summary>A zone. <c>Resizable</c>: its number of lights can be set by the user (a header with a strip or fans plugged in), within LedsMin to LedsMax.</summary>
public sealed record LightZoneDto(int Id, string Name, int LedCount, int LedsMin = 0, int LedsMax = 0, bool Resizable = false);

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
    /// <summary>Other lights paired over the network (WLED, Hue and so on). Their keys are protected for this Windows user.</summary>
    public List<StoredNetworkLight> NetworkLights { get; set; } = new();
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
///  - Nothing is ever saved into a device's own flash memory. A zone's number of lights is only changed when the
///    user sets it (setZoneSize, for a header with a strip or daisy-chained fans); OpenRGB remembers that itself.
///  - Every mode change is built from the mode exactly as the device reported it, changing only colour, speed
///    and brightness, within the device's own limits.
///
/// On Linux and macOS (the flavour built without Windows) OpenRGB is never looked for or started: RigControl only
/// talks to an OpenRGB the user runs with its SDK server on. The screen, capture cards, sound and GIGABYTE Control
/// Center are not offered there; see <see cref="LightingSystem"/>.
/// </summary>
public sealed class LightingModule : IAppModule, IModuleCapabilities
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
    /// <summary>The newest sync run that was started. A run that ends while a newer one has taken over leaves the network lights to it.</summary>
    private int _runGen;
    private string _syncStatus = "Off";
    private int? _syncColor;
    private string? _captureSize;
    private IReadOnlyList<CaptureDeviceDto> _captureDevices = Array.Empty<CaptureDeviceDto>();
    /// <summary>The newest picture the sync read (48x27 BGRA copy), for the layout page's preview.</summary>
    private volatile byte[]? _previewGrid;

#if WINDOWS
    public LightingModule() : this(new OpenRgbServer(), name => Process.GetProcessesByName(name).Length > 0, background: true, new GigabyteControlCenter()) { }
#else
    // The programs watched for (other lighting programs, streaming programs) are Windows programs: nothing is looked for.
    public LightingModule() : this(new OpenRgbClient(), _ => false, background: true, new NoGigabyteControlCenter()) { }
#endif

    /// <param name="network">How network lights are reached; tests pass a fake so nothing leaves the PC.</param>
    /// <param name="providers">The light brands; null = every provider built into the program.</param>
    public LightingModule(IOpenRgb rgb, Func<string, bool> isProcessRunning, bool background, IGigabyteControlCenter? gcc = null,
        ILightNetwork? network = null, IEnumerable<INetworkLightProvider>? providers = null, ISecretProtector? protector = null)
    {
        _rgb = rgb;
        _isRunning = isProcessRunning;
        _background = background;
        _gcc = gcc ?? new NoGigabyteControlCenter();
        _network = network ?? new RealLightNetwork();
        _providers = providers?.ToList();
        _netStore = new NetworkLightStore(() => _settings, SaveSettings, _gate, protector);
        _ownProtector = protector is null;
        _net = new NetworkLights(_network, _netStore, NullLog.Instance, _providers);
    }

    /// <summary>No protector was handed in: pairing keys are kept with the system's own, taken from the context at start.</summary>
    private readonly bool _ownProtector;

    /// <summary>What this system gives the lights to follow (a picture, the sound). Tests swap it to try the other flavour.</summary>
    internal LightingSystem Here { get; init; } = LightingSystem.ThisBuild();

    /// <summary>The parts of lighting that work on this system (sent to the interface in app.getInfo).</summary>
    public IReadOnlyCollection<string> Capabilities => Here.Capabilities(_rgb.CanStartServer, HasGcc);

    /// <summary>False with the stand-in that does nothing (Linux and macOS, and PCs set up without it).</summary>
    private bool HasGcc => _gcc is not NoGigabyteControlCenter;

    /// <summary>The effect that plays: the saved one, or a plain colour when the saved one isn't offered on this system.</summary>
    private string EffectNow(SyncSettings s) => Here.EffectOr(s.Effect);

    private readonly IGigabyteControlCenter _gcc;
    private readonly ILightNetwork _network;
    private readonly IReadOnlyList<INetworkLightProvider>? _providers;
    private readonly NetworkLightStore _netStore;
    /// <summary>The lights paired over the network (Nanoleaf, WLED, Hue...). See Network/NetworkLights.cs.</summary>
    private NetworkLights _net;

    public string Name => "lighting";
    public string DisplayName => "Lighting (OpenRGB)";
    public ModuleStatus Status => _status;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        _settings = context.Settings.Get<LightingSettings>(LightingSettings.Section);
        // Windows: the same per-user protection as always. Linux and macOS: RigControl's own key file.
        if (_ownProtector) _netStore.UseProtector(new PlatformProtector(context.Platform.Secrets));
        _net = new NetworkLights(_network, _netStore, _log, _providers);
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
        var ticks = 0;
        while (!_stop.Token.WaitHandle.WaitOne(2000))
        {
            try
            {
                // Where the user starts OpenRGB (Linux and macOS), it may be started after RigControl: look every 10 s.
                if (!_rgb.CanStartServer && ++ticks % 5 == 0) ConnectIfStarted();
                Poll();
            }
            catch (Exception ex) { _log.Warn("Lighting update failed.", ex); }
        }
    }

    /// <summary>
    /// Connects to an OpenRGB the user has started since. Only used where RigControl does not start OpenRGB itself;
    /// it never starts anything, and does nothing while connected or while nothing answers on OpenRGB's port.
    /// </summary>
    internal void ConnectIfStarted()
    {
        LightingStatusDto status;
        lock (_gate)
        {
            if (_rgb.CanStartServer || _rgb.Connected || !_rgb.IsListening()) return;
            try { StartAndConnect(); }
            catch (Exception ex) { _message = "OpenRGB could not be reached: " + Plain(ex); }
            SetStatus();
            status = BuildStatus();
        }
        PublishChanged(status);
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
            // Lets the rest of the app (the cooler screen's colour) pick up the PC's colours once RigControl is connected.
            if (connected is not null) PublishChanged(connected);
        }
    }

    private void AutoStartCore()
    {
        lock (_gate)
        {
            try
            {
                if ((!_rgb.CanStartServer || _rgb.FindExecutable() is null) && !_rgb.IsListening())
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

    /// <summary>Said where RigControl does not start OpenRGB itself (Linux and macOS).</summary>
    private const string StartItYourself = "OpenRGB is not running. Open OpenRGB, turn on its SDK server (port 6742), then press Start OpenRGB here.";

    private void StartAndConnect()
    {
        if (!_rgb.IsListening())
        {
            if (!_rgb.CanStartServer) throw RpcException.Unavailable(StartItYourself);
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
        else if (!_rgb.CanStartServer) _status = new ModuleStatus(ModuleState.Degraded, _message ?? StartItYourself);
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
            new OpenRgbDto(exe is not null || listening, exe, listening, _rgb.Connected, OpenRgbClient.Port, null, _message),
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
            c.Zones.Select((z, i) => new LightZoneDto(i, z.Name, (int)z.LedsCount, (int)Math.Min(z.LedsMin, MaxZoneLeds),
                (int)Math.Min(z.LedsMax, MaxZoneLeds), z.CanResize(c.Protocol))).ToList(),
            modes, c.ActiveMode, color is { } v ? OpenRgbProtocol.ColorToHex(v) : null);
    }

    // ---------------------------------------------------------------- RPC

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        if (method == "listSyncSources") return ListSyncSourcesAsync();
        if (method == "getNanoleaf") return Task.FromResult<object?>(NanoleafStatus());
        if (method == "findNanoleaf") return FindNanoleafAsync(ct);
        if (method == "pairNanoleaf") return PairNanoleafAsync(RpcArgs.Require<NanoleafArgs>(args), ct);
        if (method == "forgetNanoleaf") return ForgetNanoleafAsync();
        if (method == "getNetworkLights") return Task.FromResult<object?>(NetworkStatus());
        if (method == "scanNetworkLights") return ScanNetworkLightsAsync(RpcArgs.Optional<NetScanArgs>(args), ct);
        if (method == "cancelNetworkScan") { _net.CancelScan(); return Task.FromResult<object?>(NetworkStatus()); }
        if (method == "pairNetworkLight") return PairNetworkLightAsync(RpcArgs.Require<NetPairArgs>(args), ct);
        if (method == "forgetNetworkLight") return ForgetNetworkLightAsync(RpcArgs.Require<NetForgetArgs>(args));
        if (method == "setNetworkLight") return SetNetworkLightAsync(RpcArgs.Require<NetSetArgs>(args), ct);
        if (method == "giveBackNetworkLight") return GiveBackNetworkLightAsync(RpcArgs.Require<NetForgetArgs>(args), ct);
        if (method == "getSyncPreview") return Task.FromResult<object?>(Preview());
        // Changing the lights by hand ends the sync (without putting the old lights back: the hand change wins).
        if (method is "setColor" or "setZoneColor" or "setMode" or "off" or "applyScene" && _settings.Sync.Enabled) StopSync(restore: false);
        // "Everything at once" and "Turn all lights off" (no devices named) also reach the lights on the network.
        if (method is "setColor" or "off")
        {
            var everything = method == "off" ? RpcArgs.Optional<ColorArgs>(args) : RpcArgs.Require<ColorArgs>(args);
            if (everything.DeviceIds is null) return EverythingAsync(method == "off" ? "#000000" : everything.Color, ct);
        }
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
                "setZoneSize" => SetZoneSize(RpcArgs.Require<ZoneSizeArgs>(args)),
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
        if (!_rgb.CanStartServer)
        {
            // Linux and macOS: the user installs and starts OpenRGB; RigControl only shows where to get it.
            if (_rgb.Connected || _rgb.IsListening()) return new { started = false, message = "OpenRGB is already running." };
            if (!(_ctx?.Platform.Links.OpenHttpsUrl(DownloadPage) ?? false))
                throw RpcException.Unavailable("The OpenRGB download page could not be opened. Go to openrgb.org in your browser.");
            return new { started = true, message = "OpenRGB's download page is open in your browser. Install it, start it with its SDK server on, then press Start OpenRGB here." };
        }
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
            // Any change of colour or effect is announced, so the rest of the app can follow the PC.
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

    /// <summary>No header takes more lights than this; a bigger number from a device is a mistake.</summary>
    private const uint MaxZoneLeds = 2048;

    /// <summary>
    /// Sets how many lights hang on a header (a strip, or fans chained one after another). Only ever on the user's
    /// own request, and only for zones the device says can be sized, within the device's own limits. OpenRGB keeps
    /// the number itself, so it is still set after a restart.
    /// </summary>
    private LightingStatusDto SetZoneSize(ZoneSizeArgs a) => Guard(() =>
    {
        var c = Device(a.DeviceId);
        if (a.Zone < 0 || a.Zone >= c.Zones.Count) throw RpcException.InvalidArgs("Unknown zone.");
        var z = c.Zones[a.Zone];
        if (!z.CanResize(c.Protocol)) throw RpcException.Unavailable($"{z.Name} on {c.Name} has a fixed number of lights.");
        var max = Math.Min(z.LedsMax, MaxZoneLeds);
        if (a.Leds < 0 || a.Leds < z.LedsMin || a.Leds > max) throw RpcException.InvalidArgs($"{z.Name} takes {z.LedsMin} to {max} lights.");
        // Lian Li's fan hubs work out the number of fans as lights / 16; anything else confuses the hub.
        if ((c.Name + " " + c.Vendor).Contains("Lian Li", StringComparison.OrdinalIgnoreCase) && c.Name.Contains("Hub", StringComparison.OrdinalIgnoreCase) && a.Leds % 16 != 0)
            throw RpcException.InvalidArgs("This hub counts 16 lights for each fan: pick a multiple of 16.");
        _log.Info($"{c.Name}, {z.Name}: set to {a.Leds} light(s) at the user's request (was {z.LedsCount}). OpenRGB remembers this itself.");
        var before = new List<uint>(c.Colors);
        var start = c.Zones.Take(a.Zone).Sum(x => (int)x.LedsCount);
        var was = (int)z.LedsCount;
        var perLed = c.ActiveMode >= 0 && c.ActiveMode < c.Modes.Count && c.Modes[c.ActiveMode].ColorMode == 1;
        _rgb.ResizeZone(c.Index, a.Zone, a.Leds);
        // The device's lights are numbered afresh after this: read it again before anything else is sent.
        _controllers = _rgb.ReadControllers();
        // OpenRGB empties the device's colours on a resize. While the lights follow the sync it sends new ones at
        // once; otherwise the colours it showed are given back (the header's own stretched over its new length).
        if (!_settings.Sync.Enabled && perLed && before.Count > 0
            && _controllers.FirstOrDefault(x => x.Index == c.Index) is { Colors.Count: > 0 } now)
            _rgb.UpdateLeds(now.Index, FitLedColors(before, now.Colors.Count, start, was, a.Leds));
        return Refresh();
    });

    /// <summary>
    /// Saved colours for a device whose number of lights has changed since (a header was resized). The header that
    /// changed gets its colours stretched or squeezed over its new length; the other lights keep theirs.
    /// </summary>
    /// <param name="start">The first light of the header that changed.</param>
    /// <param name="was">How many lights that header had when the colours were saved.</param>
    /// <param name="now">How many it has now.</param>
    internal static List<uint> FitLedColors(IReadOnlyList<uint> saved, int want, int start, int was, int now)
    {
        if (want <= 0) return new List<uint>();
        if (saved.Count == want) return saved.ToList();
        if (saved.Count == 0) return Enumerable.Repeat(0u, want).ToList();
        if (start >= 0 && was >= 0 && now >= 0 && start + was <= saved.Count && saved.Count - was + now == want)
        {
            var fitted = new List<uint>(want);
            fitted.AddRange(saved.Take(start));
            var zone = saved.Skip(start).Take(was).ToList();
            // A header that had no lights takes the colour of the light before it (or after it).
            if (zone.Count == 0) zone.Add(saved[Math.Clamp(start - 1, 0, saved.Count - 1)]);
            for (var i = 0; i < now; i++) fitted.Add(zone[(int)((long)i * zone.Count / now)]);
            fitted.AddRange(saved.Skip(start + was));
            return fitted;
        }
        // Which header changed isn't known: the whole row is stretched over the new length.
        var all = new List<uint>(want);
        for (var i = 0; i < want; i++) all.Add(saved[(int)((long)i * saved.Count / want)]);
        return all;
    }

    /// <summary>
    /// Saved colours fitted to a device as it is now. A scene doesn't say which header was resized since; when the
    /// device has exactly one header whose size can be set, it was that one.
    /// </summary>
    internal static List<uint> FitLedColors(IReadOnlyList<uint> saved, RgbController c)
    {
        var want = c.Colors.Count;
        var sizable = c.Zones.Select((z, i) => (Zone: z, Index: i)).Where(x => x.Zone.CanResize(c.Protocol)).ToList();
        if (sizable.Count == 1 && c.Zones.Sum(z => (long)z.LedsCount) == want)
        {
            var now = (int)sizable[0].Zone.LedsCount;
            var start = c.Zones.Take(sizable[0].Index).Sum(z => (int)z.LedsCount);
            return FitLedColors(saved, want, start, saved.Count - (want - now), now);
        }
        return FitLedColors(saved, want, -1, -1, -1);
    }

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

    private const string GccIsWindows = "GIGABYTE Control Center is a Windows program.";

    /// <summary>Closes GCC and turns its start-up task off, so it stops overriding the graphics card's lights.</summary>
    private object TakeOverGcc()
    {
        if (!HasGcc) throw ModuleAvailability.Error(GccIsWindows);
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
        if (!HasGcc) throw ModuleAvailability.Error(GccIsWindows);
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

    // ---------------------------------------------------------------- network lights (Network/NetworkLights.cs)

    private NetworkLightsDto NetworkStatus()
    {
        List<LayoutItem> placed;
        lock (_gate) placed = _settings.Layout.Items.ToList();
        return _net.Status(placed);
    }

    /// <summary>Looks for lights on the network. Only ever run by this call (the user pressed search), never by itself.</summary>
    private async Task<object?> ScanNetworkLightsAsync(NetScanArgs a, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try { await _net.ScanAsync(string.IsNullOrWhiteSpace(a.Brand) ? null : a.Brand, cts.Token).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        catch (InvalidOperationException ex) { throw RpcException.Unavailable(ex.Message); }
        catch (OperationCanceledException) { /* stopped by the user: what was found before stays */ }
        return NetworkStatus();
    }

    private async Task<object?> PairNetworkLightAsync(NetPairArgs a, CancellationToken ct)
    {
        await PairCoreAsync(a.Brand ?? "", a.Address, a.Options, ct).ConfigureAwait(false);
        return NetworkStatus();
    }

    private async Task<PairedLight> PairCoreAsync(string brand, string? address, IReadOnlyDictionary<string, string>? options, CancellationToken ct)
    {
        PairedLight light;
        try { light = await _net.PairAsync(brand, address, options, ct).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        catch (InvalidOperationException ex) { throw RpcException.Unavailable(ex.Message); }
        if (light.Brand == NetworkLightStore.NanoleafBrand)
        {
            lock (_gate)
            {
                // The same Nanoleaf under an older name (it was renamed in the Nanoleaf app) keeps its place: its key
                // ends in its serial.
                foreach (var item in _settings.Layout.Items.Where(i =>
                             i.Key != light.LayoutKey && i.Key.StartsWith("nanoleaf|", StringComparison.Ordinal)
                             && i.Key.EndsWith("|" + light.Id, StringComparison.Ordinal)))
                    item.Key = light.LayoutKey;
                _settings.Layout.Items = _settings.Layout.Items.GroupBy(i => (i.Key, i.Zone)).Select(g => g.Last()).ToList();
                SaveSettings();
            }
        }
        return light;
    }

    private async Task<object?> ForgetNetworkLightAsync(NetForgetArgs a)
    {
        // Its box stays on the layout (shown as not connected), so pairing the same light again puts it back in place.
        if (!await _net.ForgetAsync(a.Id ?? "").ConfigureAwait(false)) throw RpcException.InvalidArgs("That light isn't paired.");
        return NetworkStatus();
    }

    /// <summary>"#RRGGBB" as 0xRRGGBB, or an error the page shows.</summary>
    internal static int ParseNetColour(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) throw RpcException.InvalidArgs("Colours are #RRGGBB.");
        return int.Parse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One colour (or off, for black) for every light: the PC's own through OpenRGB, and every paired light on the
    /// network whose brand takes part (a Hue Bridge doesn't: it can be a whole house). Works without OpenRGB when
    /// lights are paired on the network.
    /// </summary>
    private async Task<object?> EverythingAsync(string? color, CancellationToken ct)
    {
        var rgb = ParseNetColour(color);
        LightingStatusDto status;
        lock (_gate)
        {
            if (_rgb.Connected || !_net.Providers.Any() || _netStore.All().Count == 0) status = SetColor(new ColorArgs { Color = color });
            else status = BuildStatus();
        }
        var change = rgb == 0 ? new LightChange(On: false) : new LightChange(Colour: rgb);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var (tried, problems) = await _net.SetAllAsync(change, cts.Token).ConfigureAwait(false);
        // Without OpenRGB the network lights are all there is: when none of them took it, that is an error.
        if (!_rgb.Connected && tried > 0 && problems.Count == tried) throw RpcException.Unavailable(string.Join(" ", problems));
        return status;
    }

    private async Task<object?> SetNetworkLightAsync(NetSetArgs a, CancellationToken ct)
    {
        var id = a.Id ?? "";
        if (_netStore.Find(id) is null) throw RpcException.InvalidArgs("That light isn't paired.");
        if (a.On is null && a.Brightness is null && a.Color is null) throw RpcException.InvalidArgs("Say what to change: on, brightness or colour.");
        if (a.Brightness is { } b && (b < 0 || b > 100)) throw RpcException.InvalidArgs("Brightness is 0 to 100%.");
        var change = new LightChange(a.On, a.Brightness, a.Color is null ? null : ParseNetColour(a.Color));
        try { _net.Check(id, change); }
        catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        catch (InvalidOperationException ex) { throw RpcException.Unavailable(ex.Message); }
        // A change by hand ends the light sync, as on the PC's own lights (the hand change wins).
        if (_settings.Sync.Enabled) StopSync(restore: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try { await _net.SetAsync(id, change, cts.Token).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        catch (InvalidOperationException ex) { throw RpcException.Unavailable(ex.Message); }
        return NetworkStatus();
    }

    private async Task<object?> GiveBackNetworkLightAsync(NetForgetArgs a, CancellationToken ct)
    {
        var id = a.Id ?? "";
        if (_netStore.Find(id) is null) throw RpcException.InvalidArgs("That light isn't paired.");
        if (_net.Controls(id) is { GiveBack.Can: false } can) throw RpcException.Unavailable(can.GiveBack.Why ?? "This light can't be given back from here.");
        if (_netStore.Before(id) is null) throw RpcException.InvalidArgs("RigControl hasn't changed this light, so there is nothing to give back.");
        if (_settings.Sync.Enabled) StopSync(restore: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try { await _net.GiveBackAsync(id, cts.Token).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw RpcException.InvalidArgs(ex.Message); }
        catch (InvalidOperationException ex) { throw RpcException.Unavailable(ex.Message); }
        return NetworkStatus();
    }

    // ---------------------------------------------------------------- Nanoleaf (the older calls, kept for the page that uses them)

    /// <summary>
    /// True while the paired Nanoleaf takes a colour per panel from RigControl. Takes no lock, so it can be asked
    /// from anywhere, including while the lighting lock is held.
    /// </summary>
    public bool NanoleafStreaming => _net.IsStreaming(NetworkLightStore.NanoleafBrand);

    private PairedLight? PairedNanoleaf => _netStore.All().FirstOrDefault(l => l.Brand == NetworkLightStore.NanoleafBrand);

    private NanoleafDto NanoleafStatus(IReadOnlyList<string>? found = null)
    {
        var n = PairedNanoleaf;
        var d = n is null ? null : _net.Description(n);
        return new NanoleafDto(n is not null, n?.Address, n?.Name, d?.Points.Count ?? 0, NanoleafStreaming, n is null ? null : _net.Problem(n), found);
    }

    private async Task<object?> FindNanoleafAsync(CancellationToken ct)
    {
        try { await _net.ScanAsync(NetworkLightStore.NanoleafBrand, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw RpcException.Unavailable("Couldn't look for a Nanoleaf: " + ex.Message); }
        var status = NetworkStatus();
        if (status.ScanProblems.FirstOrDefault() is { } problem) throw RpcException.Unavailable("Couldn't look for a Nanoleaf: " + problem);
        return NanoleafStatus(status.Found.Where(f => f.Brand == NetworkLightStore.NanoleafBrand).Select(f => f.Address).ToList());
    }

    private async Task<object?> PairNanoleafAsync(NanoleafArgs a, CancellationToken ct)
    {
        if (a.Ip is not { } ip || !LanAddress.IsPrivate(ip)) throw RpcException.InvalidArgs("Pick a Nanoleaf found on your network.");
        await PairCoreAsync(NetworkLightStore.NanoleafBrand, ip, null, ct).ConfigureAwait(false);
        return NanoleafStatus();
    }

    private async Task<object?> ForgetNanoleafAsync()
    {
        if (PairedNanoleaf is { } n) await _net.ForgetAsync(n.LayoutKey).ConfigureAwait(false);
        return NanoleafStatus();
    }

    /// <summary>How often the colour is read and sent (about 15 times a second, as RichPresence).</summary>
    public int SyncIntervalMs { get; init; } = 66;

    /// <summary>The least time between two colours sent to a graphics card while the lights follow the screen.</summary>
    public int GentleIntervalMs { get; init; } = 1000;

    /// <summary>Devices whose light chip can't take fast updates: graphics cards (OpenRGB type 2).</summary>
    internal static bool IsGentle(RgbController dev) => dev.Type == 2;

    /// <summary>When the page was last told the sync's state and the lights' colours (at most every 2 s).</summary>
    private DateTime _lastStatusPush = DateTime.MinValue;

    private volatile string? _bassProblem;

    private SyncDto SyncStatus()
    {
        var s = _settings.Sync;
        // What this system doesn't offer (a capture card, the sound) is left out, whatever the settings file says.
        var cards = Here.HasCaptureCard;
        var audio = Here.Audio is not null;
        var effect = EffectNow(s);
        var list = _captureDevices.ToList();
        if (cards && s.Source != "" && list.All(d => d.Id != s.Source))
            list.Add(new CaptureDeviceDto(s.Source, (s.SourceName != "" ? s.SourceName : "capture card") + " (not plugged in)", false));
        return new SyncDto(s.Enabled, cards ? s.Source : "", !cards || s.SourceName == "" ? null : s.SourceName, s.Brightness, s.Enabled ? _syncStatus : "Off",
            _syncColor is { } c ? ColorMath.Hex(c) : null, list, _captureSize, s.Vivid, audio && s.Bass, s.BassStrength,
            audio && s.Enabled && (s.Bass || s.Mid || s.Treble || EffectRenderer.IsAudio(effect)) ? _bassProblem : null, audio && s.Treble, s.TrebleStrength, s.AudioGain,
            audio && s.Mid, s.MidStrength, effect, Here.Effects,
            Here.Effects.ToDictionary(e => e.Id, e => s.ParamsFor(e.Id)));
    }

    private async Task<object?> ListSyncSourcesAsync()
    {
        // No capture cards on this system: the answer simply has none.
        if (Here.HasCaptureCard)
        {
            try { _captureDevices = await Here.CaptureDevices!().ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("Couldn't list capture devices: " + ex.Message); }
        }
        lock (_gate) return SyncStatus();
    }

    private SyncDto SetSync(SyncArgs a)
    {
        var s = _settings.Sync;
        // Asked for something this system doesn't have: said so, and nothing is changed.
        if (Here.Audio is null && (a.Bass == true || a.Mid == true || a.Treble == true))
            throw ModuleAvailability.Error("The lights can't follow sound here in this version.");
        if (a.Effect is { } wanted && EffectRenderer.Exists(wanted) && !Here.Offers(wanted))
            throw ModuleAvailability.Error(wanted == EffectRenderer.Screen ? "The lights can't follow the screen here in this version."
                : "The effects that follow sound don't work here in this version.");
        if (!Here.HasCaptureCard && !string.IsNullOrEmpty(a.Source))
            throw ModuleAvailability.Error("The lights can't follow a capture card here in this version.");
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
            // Lights on the network need no OpenRGB: with one paired, the sync runs without it (the PC's own
            // lights join in once OpenRGB is there).
            if (_netStore.All().Count == 0) RequireConnected();
            // Remember the lights as they are, to put them back when the sync stops.
            s.Before = null;
            if (_rgb.Connected)
            {
                try
                {
                    _controllers = _rgb.ReadControllers();
                    s.Before = Snapshot("Before light sync");
                }
                catch (Exception ex) when (ex is not RpcException && _netStore.All().Count > 0)
                {
                    _rgb.Disconnect();
                    _controllers = Array.Empty<RgbController>();
                    _message = "The connection to OpenRGB was lost: " + Plain(ex);
                    SetStatus();
                }
            }
            s.Enabled = true;
            SaveSettings();
            StartSync();
            _log.Info(EffectNow(s) == EffectRenderer.Screen ? "Lights follow " + (s.Source == "" ? "the screen" : s.SourceName) + "."
                : "Lights play the effect " + EffectRenderer.Catalog.First(e => e.Id == EffectNow(s)).Name + ".");
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
        // Lights paired over the network (a Nanoleaf panel by panel, WLED, Hue...) are one box each.
        foreach (var zone in _net.LayoutZones(layout.Items))
            if (seen.Add((zone.Key, zone.Zone))) zones.Add(zone);
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
        Volatile.Write(ref _runGen, gen);
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
        // The log says "put back" only for what was put back.
        var putBack = false;
        IReadOnlyList<string> fitted = Array.Empty<string>();
        if (restore && before is not null)
        {
            lock (_gate)
            {
                try
                {
                    if (_rgb.Connected)
                    {
                        _controllers = _rgb.ReadControllers();
                        fitted = ApplySceneCore(before);
                        _controllers = _rgb.ReadControllers();
                        putBack = true;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Couldn't put the lights back after the sync.", ex);
                }
            }
        }
        _log.Info(!restore ? "Light sync off (lights changed by hand)."
            : putBack ? "Light sync off; lights put back as they were."
            : "Light sync off.");
        foreach (var name in fitted)
            _log.Info($"{name} has a different number of lights than before the sync, so its old colours were stretched to fit.");
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
        var sys = Here;
        var source = EffectNow(s) == EffectRenderer.Screen && sys.HasCaptureCard ? s.Source : "";
        using var screen = sys.Screen?.Invoke();
        var effects = new EffectRenderer();
        var effectGrid = new byte[ColorMath.GridW * ColorMath.GridH * 4];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        float rawBass = 0, rawMid = 0, rawTreble = 0;
        ICapturePicture? capture = null;
        IObsPicture? obs = null;
        IAudioMeter? bass = null;
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
        // OpenRGB going away must not stop the lights on the network: it is noted, and asked for again now and then.
        var rgbLost = false;
        var rgbRetryAt = DateTime.MinValue;
        var rgbTries = 0;

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
                var c = sys.CaptureCard!();
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
                var o = sys.Obs!();
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
                    var effect = EffectNow(s);
                    var isScreen = effect == EffectRenderer.Screen;
                    var picking = isScreen;
                    if (!isScreen)
                    {
                        // An effect from the library paints the picture instead of the screen.
                        target = effects.Render(effect, s.ParamsFor(effect), clock.Elapsed.TotalSeconds, rawBass, rawMid, rawTreble, effectGrid);
                        grid = effectGrid;
                    }
                    else if (capture is null && obs is null && screen is not null)
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
                    if (sys.Audio is not null && (s.Bass || s.Mid || s.Treble || audioEffect))
                    {
                        bass ??= sys.Audio();
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
                        var rgbUp = false;
                        lock (_gate)
                        {
                            if (!Live()) break;
                            if (!_rgb.Connected && rgbLost && DateTime.UtcNow >= rgbRetryAt)
                            {
                                // Soon after it dropped, then only now and then (OpenRGB may need the user).
                                rgbRetryAt = DateTime.UtcNow.AddSeconds(++rgbTries <= 1 ? 3 : 30);
                                try
                                {
                                    StartAndConnect();
                                    _log.Info("Light sync: OpenRGB is back; the PC's lights follow again.");
                                }
                                catch { /* tried again later */ }
                                SetStatus();
                            }
                            if (!_rgb.Connected)
                            {
                                direct.Clear();
                                gentle.Clear();
                                perLed.Clear();
                            }
                            else
                            {
                              try
                              {
                                rgbLost = false;
                                rgbTries = 0;
                                // Started without OpenRGB (only lights on the network): how the PC's lights are now is what is put back.
                                if (s.Enabled && s.Before is null)
                                {
                                    s.Before = Snapshot("Before light sync");
                                    SaveSettings();
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
                                rgbUp = true;
                              }
                              catch (Exception ex)
                              {
                                // The PC's lights wait; the lights on the network below carry on.
                                _log.Warn("Light sync: OpenRGB stopped answering. Lights on your network carry on; the PC's lights follow again once OpenRGB is back.", ex);
                                try { _rgb.Disconnect(); } catch { /* asked for again below */ }
                                _controllers = Array.Empty<RgbController>();
                                _message = "The connection to OpenRGB was lost: " + Plain(Inner(ex));
                                SetStatus();
                                direct.Clear();
                                gentle.Clear();
                                perLed.Clear();
                                rgbLost = true;
                                rgbTries = 0;
                                rgbRetryAt = DateTime.UtcNow.AddSeconds(3);
                              }
                            }
                        }
                        // Lights paired over the network: each light point takes the part of the picture under it,
                        // every light at its own pace (a Nanoleaf about 10 times a second). They are put back when
                        // they leave the layout or the sync stops.
                        {
                            var k = s.Brightness / 100.0;
                            int Shade(int rc)
                            {
                                if (vivid) rc = ColorMath.Vivid(rc);
                                var pc = Pulse((((rc >> 16) & 255) * k, ((rc >> 8) & 255) * k, (rc & 255) * k));
                                return ((int)Math.Clamp(pc.R, 0, 255) << 16) | ((int)Math.Clamp(pc.G, 0, 255) << 8) | (int)Math.Clamp(pc.B, 0, 255);
                            }
                            var one = (int)(((color & 0xFF) << 16) | (color & 0xFF00) | ((color >> 16) & 0xFF));
                            _net.Frame(new NetworkFrame(items, grid, ColorMath.GridW, ColorMath.GridH, Shade, one));
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
                        var doing = isScreen ? $"Following {from}" : "Playing " + EffectRenderer.Catalog.First(e => e.Id == effect).Name
                            + (items.Count == 0 ? " (one colour: place your devices on the layout to see it move across them)" : "");
                        _syncStatus = rgbUp ? doing
                            : _netStore.All().Count > 0 ? doing + " (the PC's own lights are waiting for OpenRGB)"
                            : "Waiting for OpenRGB";
                        if ((DateTime.UtcNow - _lastStatusPush).TotalSeconds >= 2)
                        {
                            _lastStatusPush = DateTime.UtcNow;
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
                // With the bass on, twice as often (about 30 times a second) so short kicks aren't missed.
                Thread.Sleep(s.Bass || s.Mid || s.Treble || EffectNow(s) != EffectRenderer.Screen ? Math.Min(SyncIntervalMs, 33) : SyncIntervalMs);
            }
        }
        finally
        {
            // A newer run that has already taken over keeps the network lights: handing them back here would make
            // every one of them drop out and be taken again in the middle of the new run.
            if (Volatile.Read(ref _runGen) == gen || _disposed != 0) _net.StopAll();
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
        foreach (var device in ApplySceneCore(scene))
            _log.Info($"Scene \"{scene.Name}\": {device} has a different number of lights than when it was saved, so its colours were stretched to fit.");
        return Refresh();
    });

    /// <summary>
    /// Puts a saved scene on the devices. Returns the names of devices whose number of lights has changed since it
    /// was saved (a header was resized): their colours were stretched to fit instead of being left out.
    /// </summary>
    private IReadOnlyList<string> ApplySceneCore(Scene scene)
    {
        var fitted = new List<string>();
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
            if (m.ColorMode != 1 || c.Colors.Count == 0 || saved.LedColors.Count == 0) continue;
            if (saved.LedColors.Count == c.Colors.Count)
            {
                _rgb.UpdateLeds(c.Index, saved.LedColors);
            }
            else
            {
                // A header was resized since: the saved colours are fitted, so the device isn't left on whatever it showed.
                _rgb.UpdateLeds(c.Index, FitLedColors(saved.LedColors, c));
                fitted.Add(c.Name);
            }
        }
        return fitted;
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
        try { _net.Dispose(); } catch { /* ignore */ }
        try { (_network as IDisposable)?.Dispose(); } catch { /* ignore */ }
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

    private sealed class ZoneSizeArgs
    {
        public int DeviceId { get; set; }
        public int Zone { get; set; }
        public int Leds { get; set; }
    }

    private sealed class NetScanArgs
    {
        /// <summary>One brand to search for; empty = all of them.</summary>
        public string? Brand { get; set; }
    }

    private sealed class NetPairArgs
    {
        public string? Brand { get; set; }
        public string? Address { get; set; }
        public Dictionary<string, string>? Options { get; set; }
    }

    private sealed class NetForgetArgs
    {
        /// <summary>The light's id from getNetworkLights (also its key on the layout).</summary>
        public string? Id { get; set; }
    }

    private sealed class NetSetArgs
    {
        /// <summary>The light's id from getNetworkLights.</summary>
        public string? Id { get; set; }
        public bool? On { get; set; }
        /// <summary>0-100.</summary>
        public int? Brightness { get; set; }
        /// <summary>"#RRGGBB".</summary>
        public string? Color { get; set; }
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
