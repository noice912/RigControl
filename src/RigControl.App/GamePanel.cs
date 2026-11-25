using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RigControl.Core.Json;
using RigControl.Core.Logging;

namespace RigControl.App;

/// <summary>Settings for the in-game panel (brought over from RichPresence's overlay).</summary>
public sealed class GamePanelSettings
{
    public const string Section = "gamePanel";
    public bool Enabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+G";
    /// <summary>The controller's logo button opens the panel, and the controller moves around it.</summary>
    public bool Controller { get; set; } = true;
    /// <summary>Stop Windows giving the logo button to Xbox Game Bar (a Windows setting, put back when unticked).</summary>
    public bool GameBarButtonOff { get; set; }
    /// <summary>What that Windows setting was before RigControl changed it ("" = not changed, "unset" = there was none).</summary>
    public string GameBarButtonBefore { get; set; } = "";
    /// <summary>Pause PS Remote Play while the panel is open, so the PlayStation doesn't get the controller.</summary>
    public bool PauseRemotePlay { get; set; }
    /// <summary>Remote Play processes paused right now (un-paused at the next start if RigControl was ended in between).</summary>
    public List<int> PausedPids { get; set; } = new();
    public DateTimeOffset? ImportedFromRichPresence { get; set; }
}

/// <summary>
/// The panel over the game (RichPresence's overlay, rebuilt): a dimmed screen with RigControl's own "#overlay" page on
/// top, and Discord's web app over the widgets when asked. Ordinary always-on-top windows: nothing is injected into
/// games, so it shows over windowed and borderless games, not over exclusive full screen.
/// </summary>
public sealed class GamePanel
{
    public const int BaseWidth = 1172, WideWidth = 1568, PanelHeight = 700, MaxPanelHeight = 1000, TopBar = 56;

    private readonly AppServices _services;
    private readonly ILog _log;
    private readonly Window _dim;
    private readonly Window _panel;
    private readonly WebView2 _view;
    private readonly DiscordWindow _discord;
    private HwndSource? _hotkeyWindow;
    private bool _ready;
    private bool _starting;
    private IntPtr _previous;
    private bool _discordOpen;
    private readonly Pad? _pad;
    private readonly ControllerKeyboard _keyboard;
    private readonly System.Windows.Threading.DispatcherTimer _pauseTimer;
    private readonly System.Windows.Threading.DispatcherTimer _keepAlive;

    public GamePanel(AppServices services)
    {
        _services = services;
        _log = services.Log.For("panel");
        _dim = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Title = "RigControl panel background",
        };
        _dim.MouseLeftButtonDown += (_, _) => Hide();
        _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x16, 0x1C, 0x23) };
        _panel = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            Title = "RigControl panel",
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x23)),
            Content = _view,
        };
        _panel.Closing += (_, e) => { e.Cancel = !_closing; if (!_closing) Hide(); };
        _discord = new DiscordWindow(services.Environment.DataDirectory, _log);
        _discord.Deactivated += (_, _) => { };
        _keyboard = new ControllerKeyboard(() => new WindowInteropHelper(_discord).Handle);
        _keyboard.Closed2 += UpdatePadModes;
        _pauseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _pauseTimer.Tick += (_, _) => { _pauseTimer.Stop(); PauseRemotePlay(); };
        _keepAlive = new System.Windows.Threading.DispatcherTimer { Interval = KeepAliveCheck };
        _keepAlive.Tick += (_, _) => KeepAliveTick();
        _services.Events.Published += OnEventPublished;
        ImportRichPresenceSettings();
        ResumeRemotePlay(); // paused by a RigControl that was ended while the panel was open
        try
        {
            _pad = new Pad();
            _pad.Pressed += () => { if (_services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section).Controller) Toggle(); };
            _pad.Moved += OnPad;
        }
        catch (Exception ex)
        {
            _log.Warn("Controller support could not start: " + ex.Message);
        }
    }

    /// <summary>Copies the overlay settings from RichPresence once, including the Game Bar change it made (RigControl now puts it back).</summary>
    private void ImportRichPresenceSettings()
    {
        var s = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section);
        if (s.ImportedFromRichPresence is not null) return;
        try
        {
            var path = Path.Combine(RigControl.Presence.PresenceSettings.RichPresenceDataDir, "settings.json");
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
                var r = doc.RootElement;
                bool B(string k, bool d) => r.TryGetProperty(k, out var v) ? v.ValueKind == System.Text.Json.JsonValueKind.True || (v.ValueKind != System.Text.Json.JsonValueKind.False && d) : d;
                string S(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : d;
                s.Enabled = B("overlay_enabled", true);
                var hk = S("overlay_hotkey", "Ctrl+Alt+G");
                if (TryParseHotkey(hk, out _, out _)) s.Hotkey = hk;
                s.Controller = B("overlay_controller", true);
                s.GameBarButtonOff = B("gamebar_button_off", false);
                s.GameBarButtonBefore = S("gamebar_button_before", "");
                s.PauseRemotePlay = B("pause_remote_play", false);
                _log.Info($"Panel settings copied from RichPresence (controller {s.Controller}, Game Bar button off {s.GameBarButtonOff}, pause Remote Play {s.PauseRemotePlay}).");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Couldn't copy RichPresence's overlay settings: " + ex.Message);
        }
        s.ImportedFromRichPresence = DateTimeOffset.Now;
        _services.Settings.Set(GamePanelSettings.Section, s);
    }

    private bool _closing;
    public bool IsOpen => _panel.IsVisible;
    public bool DiscordOpen => _discordOpen && _discord.IsVisible;
    public DiscordWindow Discord => _discord;

    // ---------------------------------------------------------------- hotkey

    /// <summary>Registers the shortcut. Returns a problem to show, or null.</summary>
    public string? ApplySettings()
    {
        var s = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section);
        if (_hotkeyWindow is null)
        {
            _hotkeyWindow = new HwndSource(new HwndSourceParameters("RigControl panel hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
            _hotkeyWindow.AddHook(HotkeyHook);
        }
        UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        try { _pad?.Enable(s.Enabled && s.Controller); } catch (Exception ex) { _log.Warn("Controller: " + ex.Message); }
        ApplyGameBar(s);
        if (!s.Enabled) return null;
        if (!TryParseHotkey(s.Hotkey, out var mods, out var vk)) return $"\"{s.Hotkey}\" isn't a shortcut RigControl understands.";
        if (!RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | 0x4000 /* NOREPEAT */, vk))
            return $"{s.Hotkey} is already used by another program.";
        _log.Info($"Panel shortcut: {s.Hotkey}.");
        return null;
    }

    private const int HotkeyId = 0x4752; // "GR"

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId) // WM_HOTKEY
        {
            handled = true;
            Toggle();
        }
        return IntPtr.Zero;
    }

    public static bool TryParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default: return false;
            }
        }
        if (mods == 0) return false;
        if (!Enum.TryParse<Keys>(parts[^1], true, out var key) || key == Keys.None) return false;
        vk = (uint)key;
        return true;
    }

    // ---------------------------------------------------------------- showing

    public void Toggle()
    {
        if (IsOpen) Hide();
        else _ = ShowAsync();
    }

    public async Task ShowAsync()
    {
        var s = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section);
        if (!s.Enabled) return;
        _previous = GetForegroundWindow();
        // On the screen the mouse is on, sized from that screen's own pixels and scale (see PanelPlacement).
        var (screen, scale) = PanelPlacement.MonitorUnderMouse();
        var place = PanelPlacement.For(screen, scale, BaseWidth, WideWidth, PanelHeight, MaxPanelHeight);
        var width = place.PanelWidthDip;
        // A first guess in WPF units (avoids a flash at the old place); the pixel placement below is what counts.
        _dim.Left = screen.X / scale; _dim.Top = screen.Y / scale; _dim.Width = screen.Width / scale; _dim.Height = screen.Height / scale;
        _panel.Left = place.Panel.X / scale; _panel.Top = place.Panel.Y / scale;
        _panel.Width = place.PanelWidthDip; _panel.Height = place.PanelHeightDip;
        _dim.Show();
        _panel.Show();
        // Placed in pixels once both windows exist, so the result doesn't depend on which screen WPF thought they were on.
        PanelPlacement.Apply(new WindowInteropHelper(_dim).Handle, place.Screen);
        PanelPlacement.Apply(new WindowInteropHelper(_panel).Handle, place.Panel);
        BringToFront(new WindowInteropHelper(_panel).Handle);
        _panel.Activate();
        _view.Focus();
        await EnsureViewAsync();
        Post("overlay.shown", new { wide = width >= WideWidth });
        if (s.PauseRemotePlay) _pauseTimer.Start(); // after the logo button is let go, so the PlayStation doesn't see it held
        if (_discordOpen) await OpenDiscordAsync();
        UpdatePadModes();
    }

    public void Hide()
    {
        _pauseTimer.Stop();
        _keyboard.Hide();
        _discord.Hide();
        _panel.Hide();
        _dim.Hide();
        // Give the active window back to whatever had it, unless the person has moved on since.
        var fg = GetForegroundWindow();
        if (_previous != IntPtr.Zero && (fg == IntPtr.Zero || fg == new WindowInteropHelper(_panel).Handle || fg == new WindowInteropHelper(_dim).Handle))
            BringToFront(_previous);
        _previous = IntPtr.Zero;
        UpdatePadModes();
        ResumeRemotePlay();
    }

    public async Task OpenDiscordAsync()
    {
        if (!IsOpen) return;
        _discordOpen = true;
        _discord.Owner = _panel;
        var wasPointer = _pad?.Pointer ?? false;
        await _discord.ShowAtAsync(new Rect(_panel.Left, _panel.Top + TopBar, _panel.Width, _panel.Height - TopBar));
        Post("overlay.discord", new { open = true, problem = _discord.Problem });
        UpdatePadModes();
        // Inside Discord the controller is a mouse: the pointer starts in the middle of it.
        if (_pad is { Pointer: true } && !wasPointer && _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section).Controller)
        {
            var src = PresentationSource.FromVisual(_discord);
            var m = src?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
            SetCursorPos((int)((_discord.Left + _discord.Width / 2) * m.M11), (int)((_discord.Top + _discord.Height / 2) * m.M22));
        }
    }

    public void CloseDiscord()
    {
        _discordOpen = false;
        _keyboard.Hide();
        _discord.Hide();
        if (IsOpen) _panel.Activate();
        Post("overlay.discord", new { open = false, problem = (string?)null });
        UpdatePadModes();
    }

    // ---------------------------------------------------------------- controller

    private void UpdatePadModes()
    {
        if (_pad is null) return;
        _pad.Nav = IsOpen;
        _pad.Pointer = IsOpen && DiscordOpen;
        _pad.Keys = _keyboard.IsVisible;
        Post("overlay.mode", new { mode = _pad.Keys ? "keyboard" : _pad.Pointer ? "pointer" : IsOpen ? "widgets" : "closed" });
    }

    /// <summary>Three modes, in this order: the keyboard is up; Discord is showing (pointer); the widgets.</summary>
    private void OnPad(string what)
    {
        if (!IsOpen) return;
        if (_keyboard.IsVisible)
        {
            _keyboard.Pad(what);
            return;
        }
        if (DiscordOpen)
        {
            if (what == "Back") CloseDiscord();
            else if (what == "Keyboard")
            {
                _keyboard.Owner = _discord;
                _keyboard.ShowAt(new Rect(_discord.Left, _discord.Top, _discord.Width, _discord.Height));
                UpdatePadModes();
            }
            return;
        }
        if (what == "Back") Hide();
        else Post("overlay.pad", new { what });
    }

    // ---------------------------------------------------------------- PS Remote Play

    /// <summary>
    /// PS Remote Play reads the controller whether or not its window is active, so it is paused (every thread, with
    /// only the suspend/resume right) while the panel is open, and un-paused on close, on quit and at the next start.
    /// </summary>
    private void PauseRemotePlay()
    {
        var s = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section);
        if (!s.PauseRemotePlay || !IsOpen || s.PausedPids.Count > 0) return;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("RemotePlay"))
        {
            using (p)
            {
                if (SuspendResume(p.Id, true)) s.PausedPids.Add(p.Id);
            }
        }
        if (s.PausedPids.Count > 0)
        {
            _services.Settings.Set(GamePanelSettings.Section, s);
            _lastRun = DateTime.UtcNow;
            _keepAlive.Start();
            _log.Info("PS Remote Play paused while the panel is open.");
        }
    }

    /// <summary>
    /// Frozen for good, Remote Play loses its link to the PlayStation (a 29 s pause dropped it on 2026-11-22). So while
    /// it is paused it runs for a moment about every 1.5 s to keep the link alive. A 0.1 s run every 1.5 s still let
    /// presses through (it reads the controller then, plus the last reports Windows kept for it), so it only runs once
    /// the controller has been completely still for a moment; if it never is, it runs anyway after 8 s.
    /// </summary>
    public static readonly TimeSpan KeepAliveEvery = TimeSpan.FromMilliseconds(1500), KeepAliveRun = TimeSpan.FromMilliseconds(50),
        KeepAliveStill = TimeSpan.FromMilliseconds(300), KeepAliveLatest = TimeSpan.FromSeconds(8), KeepAliveCheck = TimeSpan.FromMilliseconds(100);

    private DateTime _lastRun = DateTime.MinValue;

    /// <summary>Whether to let Remote Play run now: due, and the controller still for a moment (or overdue).</summary>
    public static bool ShouldRun(DateTime now, DateTime lastRun, DateTime lastBusy) =>
        now - lastRun >= KeepAliveLatest || (now - lastRun >= KeepAliveEvery && now - lastBusy >= KeepAliveStill);

    private async void KeepAliveTick()
    {
        var pids = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section).PausedPids.ToList();
        if (pids.Count == 0 || !IsOpen)
        {
            _keepAlive.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        var lastBusy = _pad?.LastBusy ?? DateTime.MinValue;
        if (!ShouldRun(now, _lastRun, lastBusy)) return;
        if (now - lastBusy < KeepAliveStill)
            _log.Info($"PS Remote Play ran while the controller was in use (still busy after {(now - _lastRun).TotalSeconds:0} s; {(DiscordOpen ? "Discord" : "widgets")}).");
        _lastRun = now;
        foreach (var pid in pids) SuspendResume(pid, false);
        await Task.Delay(KeepAliveRun);
        // Closed (and un-paused for good) in the meantime: leave it running.
        var still = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section).PausedPids;
        if (!IsOpen || still.Count == 0) return;
        foreach (var pid in pids.Where(still.Contains)) SuspendResume(pid, true);
    }

    private void ResumeRemotePlay()
    {
        _keepAlive?.Stop();
        var s = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section);
        if (s.PausedPids.Count == 0) return;
        foreach (var pid in s.PausedPids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (string.Equals(p.ProcessName, "RemotePlay", StringComparison.OrdinalIgnoreCase)) SuspendResume(pid, false);
            }
            catch { /* gone */ }
        }
        s.PausedPids.Clear();
        _services.Settings.Set(GamePanelSettings.Section, s);
        _log.Info("PS Remote Play un-paused.");
    }

    private static bool SuspendResume(int pid, bool pause)
    {
        var h = OpenProcess(0x0800, false, pid); // PROCESS_SUSPEND_RESUME only
        if (h == IntPtr.Zero) return false;
        try { return (pause ? NtSuspendProcess(h) : NtResumeProcess(h)) == 0; }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- Xbox Game Bar and the logo button

    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameBarValue = "UseNexusForGameBarEnabled";

    /// <summary>
    /// Windows gives the logo button to Xbox Game Bar unless UseNexusForGameBarEnabled is 0. Written only when the user
    /// ticks the box (and the panel and controller are on); the old value is remembered and put back otherwise.
    /// </summary>
    private void ApplyGameBar(GamePanelSettings s)
    {
        var want = s.GameBarButtonOff && s.Enabled && s.Controller;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(GameBarKey);
            if (want && s.GameBarButtonBefore == "")
            {
                var old = key.GetValue(GameBarValue);
                s.GameBarButtonBefore = old is int i ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unset";
                key.SetValue(GameBarValue, 0, Microsoft.Win32.RegistryValueKind.DWord);
                _services.Settings.Set(GamePanelSettings.Section, s);
                _log.Info("The controller's logo button no longer opens Xbox Game Bar.");
            }
            else if (want)
            {
                if (key.GetValue(GameBarValue) is not int v || v != 0) key.SetValue(GameBarValue, 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            else if (s.GameBarButtonBefore != "")
            {
                if (s.GameBarButtonBefore == "unset") key.DeleteValue(GameBarValue, false);
                else if (int.TryParse(s.GameBarButtonBefore, out var before)) key.SetValue(GameBarValue, before, Microsoft.Win32.RegistryValueKind.DWord);
                s.GameBarButtonBefore = "";
                _services.Settings.Set(GamePanelSettings.Section, s);
                _log.Info("Xbox Game Bar's logo button setting put back.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Couldn't change the Xbox Game Bar setting: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- the page

    private async Task EnsureViewAsync()
    {
        if (_ready || _starting) return;
        _starting = true;
        try
        {
            var userData = Path.Combine(_services.Environment.DataDirectory, "WebView2");
            var env = await WebViewSafe.CreateAsync(userData, m => _log.Warn(m));
            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.AddWebResourceRequestedFilter(MainWindow.AppOrigin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                try
                {
                    var path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
                    e.Response = UiAssets.TryOpen(path, out var stream, out var type)
                        ? core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff")
                        : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                }
                catch (Exception ex)
                {
                    _log.Error("Serving a panel asset failed.", ex);
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(MainWindow.AppOrigin + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.WebMessageReceived += async (_, e) =>
            {
                if (!e.Source.StartsWith(MainWindow.AppOrigin + "/", StringComparison.OrdinalIgnoreCase)) return;
                var request = e.WebMessageAsJson;
                var response = await Task.Run(() => _services.Dispatcher.HandleAsync(request));
                if (_ready && !_closing) core.PostWebMessageAsJson(response);
            };
            _ready = true;
            core.Navigate(MainWindow.AppOrigin + "/index.html#overlay");
        }
        catch (Exception ex)
        {
            _log.Error("The panel's page could not start.", ex);
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnEventPublished(string topic, object? payload) => Post(topic, payload);

    private void Post(string topic, object? payload)
    {
        if (!_ready || _closing) return;
        string json;
        try { json = JsonDefaults.Serialize(new { Event = topic, Data = payload }); }
        catch { return; }
        _panel.Dispatcher.BeginInvoke(() =>
        {
            try { if (_ready && !_closing) _view.CoreWebView2?.PostWebMessageAsJson(json); }
            catch { /* closing */ }
        });
    }

    public void Close()
    {
        _closing = true;
        try { ResumeRemotePlay(); } catch { /* ignore */ }
        try { _pad?.Dispose(); } catch { /* ignore */ }
        try { _keyboard.Close(); } catch { /* ignore */ }
        _services.Events.Published -= OnEventPublished;
        if (_hotkeyWindow is not null)
        {
            UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
            _hotkeyWindow.Dispose();
        }
        try { _discord.CloseForGood(); } catch { /* ignore */ }
        try { _view.Dispose(); } catch { /* ignore */ }
        try { _panel.Close(); } catch { /* ignore */ }
        try { _dim.Close(); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------- Win32

    /// <summary>
    /// Makes a window the active one. Windows refuses that unless this process just got a key or click (a hotkey counts,
    /// a controller button doesn't), so join the active window's input queue for a moment, with an Alt tap as fallback.
    /// </summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            if (!SetForegroundWindow(hwnd))
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero);       // Alt down
                keybd_event(0x12, 0, 0x2, UIntPtr.Zero);     // Alt up
                SetForegroundWindow(hwnd);
            }
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
