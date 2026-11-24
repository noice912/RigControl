using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RigControl.App;

/// <summary>
/// The controller (RichPresence's RPPad, ported): its logo button opens the panel; while the panel is open the d-pad
/// or left stick moves around it, A / Cross picks and B / Circle closes. While Discord shows (a web page, nothing to
/// move a frame between) the left stick is a mouse pointer instead. Xbox controllers through XInput (the logo button
/// only through the undocumented XInputGetStateEx, ordinal 100), a DualSense or DualShock 4 from its own HID reports.
/// Works while a game has the keyboard. Nothing is hidden from the game: it still gets the same presses.
/// </summary>
public sealed class Pad : IDisposable
{
    // What a controller is doing right now, packed into one number.
    public const int LOGO = 1, ACCEPT = 2, BACK = 4, UP = 16, DOWN = 32, LEFT = 64, RIGHT = 128, DIRS = 240;
    public const int SECOND = 256, KEYS = 512, ENTER = 1024; // X / Square, Y / Triangle, Start / Options

    /// <summary>The logo button went down.</summary>
    public event Action? Pressed;
    /// <summary>A move, pick or back while Nav or Keys is on: "Up", "Down", "Left", "Right", "Accept", "Back", "Second", "Keyboard", "Enter".</summary>
    public event Action<string>? Moved;

    /// <summary>The panel is open: report moves.</summary>
    public bool Nav;
    /// <summary>Discord is showing: left stick = pointer, A = click (hold to drag), X = right-click, right stick = scroll,
    /// d-pad = arrow keys, Start = Enter. Y and B come through Moved as "Keyboard" and "Back".</summary>
    public bool Pointer;
    /// <summary>The controller keyboard is up: everything comes through Moved and the pointer rests.</summary>
    public bool Keys;
    /// <summary>For tests: work everything out but don't touch the mouse or keys. <see cref="Did"/> says what it would have done.</summary>
    public bool Dry;
    public string Did = "";
    public float LX, LY, RX, RY;
    public string Seen { get; private set; } = "";
    /// <summary>When any controller last had anything pressed, pulled or pushed (UTC). Used to let PS Remote Play run only while the controller is still.</summary>
    public DateTime LastBusy { get; private set; } = DateTime.MinValue;

    private readonly DispatcherTimer _timer;
    private HwndSource? _window;
    private readonly int[] _xSkip = new int[4];
    private bool _xinput = true, _on;
    private int _was;
    private DateTime _repeatAt = DateTime.MaxValue, _firedAt = DateTime.MinValue;
    private double _moveX, _moveY, _wheel;
    private bool _leftHeld, _rightHeld;
    private readonly Dictionary<IntPtr, int> _kinds = new();      // 0 = not ours, 1 = DualSense, 2 = DualShock 4
    private readonly Dictionary<IntPtr, int> _psState = new();
    private readonly Dictionary<IntPtr, float[]> _psSticks = new();
    private readonly Dictionary<IntPtr, bool> _psBusy = new();

    /// <summary>Creates the controller reader. The timer only runs after <see cref="Enable"/>; tests drive <see cref="Step"/>.</summary>
    public Pad(bool createWindow = true)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Poll();
        if (createWindow)
        {
            _window = new HwndSource(new HwndSourceParameters("RigControl controller") { Width = 0, Height = 0, WindowStyle = 0 });
            _window.AddHook(Hook);
        }
    }

    public void Enable(bool want)
    {
        if (want == _on) return;
        _on = want;
        if (_window is not null)
        {
            // Gamepads and joysticks; 0x100 = keep sending while another window is in front, 1 = stop.
            var d = new RawDevice[2];
            for (var i = 0; i < 2; i++)
                d[i] = new RawDevice { UsagePage = 1, Usage = (ushort)(i == 0 ? 5 : 4), Flags = want ? 0x100u : 1u, Target = want ? _window.Handle : IntPtr.Zero };
            RegisterRawInputDevices(d, 2, (uint)Marshal.SizeOf<RawDevice>());
        }
        _psState.Clear();
        _psSticks.Clear();
        _psBusy.Clear();
        _was = 0;
        if (want) _timer.Start(); else _timer.Stop();
    }

    // ---------------------------------------------------------------- reading

    private static int XButtons(XState s)
    {
        var v = 0;
        if ((s.Buttons & 0x0400) != 0) v |= LOGO;
        if ((s.Buttons & 0x1000) != 0) v |= ACCEPT;
        if ((s.Buttons & 0x2000) != 0) v |= BACK;
        if ((s.Buttons & 0x4000) != 0) v |= SECOND;
        if ((s.Buttons & 0x8000) != 0) v |= KEYS;
        if ((s.Buttons & 0x0010) != 0) v |= ENTER;
        if ((s.Buttons & 1) != 0 || s.LY > 20000) v |= UP;
        if ((s.Buttons & 2) != 0 || s.LY < -20000) v |= DOWN;
        if ((s.Buttons & 4) != 0 || s.LX < -20000) v |= LEFT;
        if ((s.Buttons & 8) != 0 || s.LX > 20000) v |= RIGHT;
        return v;
    }

    /// <summary>A PlayStation controller's report as the packed state; -1 = a report without the buttons. kind 1 = DualSense, 2 = DualShock 4.</summary>
    public static int Parse(int kind, byte[]? r)
    {
        if (r is null || r.Length < 8) return -1;
        int ps, stick = 1;
        if (kind == 1)
        {
            if (r[0] == 0x31) { ps = 11; stick = 2; }          // Bluetooth, full report
            else if (r[0] == 0x01) ps = r.Length >= 64 ? 10 : 7; // USB : Bluetooth, short report
            else return -1;
        }
        else if (kind == 2)
        {
            if (r[0] == 0x11) { ps = 9; stick = 3; }           // Bluetooth
            else if (r[0] == 0x01) ps = 7;                     // USB
            else return -1;
        }
        else return -1;
        if (ps >= r.Length) return -1;
        int v = 0, b = r[ps - 2], hat = b & 15;
        if ((r[ps] & 1) != 0) v |= LOGO;
        if ((b & 0x20) != 0) v |= ACCEPT;  // Cross
        if ((b & 0x40) != 0) v |= BACK;    // Circle
        if ((b & 0x10) != 0) v |= SECOND;  // Square
        if ((b & 0x80) != 0) v |= KEYS;    // Triangle
        if ((r[ps - 1] & 0x20) != 0) v |= ENTER; // Options
        if (hat is 7 or 0 or 1 || r[stick + 1] < 50) v |= UP;
        if (hat is 3 or 4 or 5 || r[stick + 1] > 205) v |= DOWN;
        if (hat is 5 or 6 or 7 || r[stick] < 50) v |= LEFT;
        if (hat is 1 or 2 or 3 || r[stick] > 205) v |= RIGHT;
        return v;
    }

    /// <summary>
    /// True when anything at all is in use on a PlayStation controller: any button (including shoulders, triggers,
    /// Share/Options, stick clicks, touchpad, mute), the d-pad, a trigger pulled, or a stick off centre.
    /// </summary>
    public static bool Busy(int kind, byte[]? r)
    {
        if (r is null || r.Length < 8) return false;
        int ps, stick, trig;
        if (kind == 1)
        {
            if (r[0] == 0x31) { ps = 11; stick = 2; trig = 6; }
            else if (r[0] == 0x01) { ps = r.Length >= 64 ? 10 : 7; stick = 1; trig = r.Length >= 64 ? 5 : 8; }
            else return false;
        }
        else if (kind == 2)
        {
            if (r[0] == 0x11) { ps = 9; stick = 3; trig = 10; }
            else if (r[0] == 0x01) { ps = 7; stick = 1; trig = 8; }
            else return false;
        }
        else return false;
        if (ps >= r.Length || trig + 1 >= r.Length) return false;
        var b = r[ps - 2];
        if ((b & 0x0F) != 8 || (b & 0xF0) != 0) return true;     // d-pad, face buttons
        if (r[ps - 1] != 0) return true;                          // L1 R1 L2 R2 Share Options L3 R3
        if ((r[ps] & (kind == 1 ? 0x07 : 0x03)) != 0) return true; // PS, touchpad click (+ mute on a DualSense; a DS4 keeps a counter above)
        if (r[trig] > 30 || r[trig + 1] > 30) return true;        // triggers
        for (var i = 0; i < 4; i++)
            if (Math.Abs(r[stick + i] - 128) > 16) return true;   // sticks: below the pointer's own dead zone (0.18), above resting drift
        return false;
    }

    /// <summary>Left x, left y, right x, right y (-1..1, up and right positive), or null when this report doesn't carry them.</summary>
    public static float[]? Sticks(int kind, byte[]? r)
    {
        if (r is null || r.Length < 8) return null;
        int at;
        if (kind == 1) { if (r[0] == 0x31) at = 2; else if (r[0] == 0x01) at = 1; else return null; }
        else if (kind == 2) { if (r[0] == 0x11) at = 3; else if (r[0] == 0x01) at = 1; else return null; }
        else return null;
        if (at + 3 >= r.Length) return null;
        return new[] { (r[at] - 128) / 127f, -(r[at + 1] - 128) / 127f, (r[at + 2] - 128) / 127f, -(r[at + 3] - 128) / 127f };
    }

    /// <summary>A stick's push to how hard to move: nothing near the middle, gentle at first, fast at the edge.</summary>
    public static float Curve(float v)
    {
        var a = Math.Abs(v);
        if (a < 0.18f) return 0f;
        var t = Math.Min(1f, (a - 0.18f) / 0.82f);
        return (v < 0 ? -1f : 1f) * t * t;
    }

    private static float Bigger(float a, float b) => Math.Abs(b) > Math.Abs(a) ? b : a;

    private void Poll()
    {
        var now = 0;
        float lx = 0, ly = 0, rx = 0, ry = 0;
        if (_xinput)
        {
            for (var i = 0; i < 4; i++)
            {
                if (_xSkip[i] > 0) { _xSkip[i]--; continue; } // an empty slot is slow to ask: look again in 2 s
                int r;
                XState s;
                try { r = XInputGetStateEx(i, out s); }
                catch { _xinput = false; break; }
                if (r != 0) { _xSkip[i] = 2000 / (int)_timer.Interval.TotalMilliseconds; continue; }
                now |= XButtons(s);
                if (s.Buttons != 0 || s.LT > 30 || s.RT > 30 || Math.Abs((int)s.LX) > 4000 || Math.Abs((int)s.LY) > 4000
                    || Math.Abs((int)s.RX) > 4000 || Math.Abs((int)s.RY) > 4000) LastBusy = DateTime.UtcNow;
                lx = Bigger(lx, s.LX / 32767f); ly = Bigger(ly, s.LY / 32767f); rx = Bigger(rx, s.RX / 32767f); ry = Bigger(ry, s.RY / 32767f);
            }
        }
        foreach (var v in _psState.Values) now |= v;
        if (_psBusy.Values.Any(b => b)) LastBusy = DateTime.UtcNow;
        foreach (var f in _psSticks.Values) { lx = Bigger(lx, f[0]); ly = Bigger(ly, f[1]); rx = Bigger(rx, f[2]); ry = Bigger(ry, f[3]); }
        LX = lx; LY = ly; RX = rx; RY = ry;
        var want = TimeSpan.FromMilliseconds(Pointer ? 16 : 40); // a pointer has to be smooth
        if (_timer.Interval != want) _timer.Interval = want;
        Step(now, DateTime.UtcNow);
    }

    private static string DirName(int d) =>
        // One direction at a time: up and down win over left and right on a diagonal.
        (d & UP) != 0 ? "Up" : (d & DOWN) != 0 ? "Down" : (d & LEFT) != 0 ? "Left" : (d & RIGHT) != 0 ? "Right" : "";

    private void Mouse(uint flags, int data) { if (!Dry) mouse_event(flags, 0, 0, data, UIntPtr.Zero); }
    private void Key(byte vk) { if (!Dry) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); } }

    /// <summary>Turns "what is held now" into presses. Public so it can be driven without a controller.</summary>
    public void Step(int now, DateTime at)
    {
        var went = now & ~_was;
        // A controller that some program also shows as an Xbox one reports the logo twice: once is enough.
        if ((went & LOGO) != 0 && (at - _firedAt).TotalMilliseconds > 400)
        {
            _firedAt = at;
            Pressed?.Invoke();
        }
        var gone = _was & ~now;
        if (Pointer && !Keys)
        {
            Did = "";
            float ax = Curve(LX), ay = Curve(LY);
            if (ax != 0 || ay != 0)
            {
                _moveX += ax * 26; _moveY -= ay * 26; // up on the stick is up on the screen
                int dx = (int)_moveX, dy = (int)_moveY;
                if (dx != 0 || dy != 0)
                {
                    _moveX -= dx; _moveY -= dy;
                    Did += $"move({dx},{dy}) ";
                    if (!Dry && GetCursorPos(out var p)) SetCursorPos(p.X + dx, p.Y + dy);
                }
            }
            else { _moveX = 0; _moveY = 0; }
            var sy = Curve(RY);
            if (sy != 0)
            {
                _wheel += sy * 55;
                var w = (int)_wheel;
                if (w != 0) { _wheel -= w; Did += $"wheel({w}) "; Mouse(0x0800, w); }
            }
            else _wheel = 0;
            if ((went & ACCEPT) != 0) { _leftHeld = true; Did += "leftdown "; Mouse(0x0002, 0); }
            if ((gone & ACCEPT) != 0 && _leftHeld) { _leftHeld = false; Did += "leftup "; Mouse(0x0004, 0); }
            if ((went & SECOND) != 0) { _rightHeld = true; Did += "rightdown "; Mouse(0x0008, 0); }
            if ((gone & SECOND) != 0 && _rightHeld) { _rightHeld = false; Did += "rightup "; Mouse(0x0010, 0); }
            if ((went & ENTER) != 0) { Did += "enter "; Key(0x0D); }
            if ((went & KEYS) != 0) Moved?.Invoke("Keyboard");
            if ((went & BACK) != 0) Moved?.Invoke("Back");
            // The d-pad is the arrow keys; the stick's own directions don't count while it steers the pointer.
            var steering = Math.Abs(LX) > 0.3f || Math.Abs(LY) > 0.3f;
            string arrow = steering ? "" : DirName(now & DIRS), before = DirName(_was & DIRS);
            var press = false;
            if (arrow == "") _repeatAt = DateTime.MaxValue;
            else if (arrow != before || _repeatAt == DateTime.MaxValue) { press = true; _repeatAt = at.AddMilliseconds(380); }
            else if (at >= _repeatAt) { press = true; _repeatAt = at.AddMilliseconds(90); }
            if (press)
            {
                Did += $"key({arrow}) ";
                Key((byte)(arrow == "Up" ? 0x26 : arrow == "Down" ? 0x28 : arrow == "Left" ? 0x25 : 0x27));
            }
        }
        else
        {
            // Leaving Discord with a button still held: let go of it.
            if (_leftHeld) { _leftHeld = false; Mouse(0x0004, 0); }
            if (_rightHeld) { _rightHeld = false; Mouse(0x0010, 0); }
            if (Nav || Keys)
            {
                if (Keys)
                {
                    if ((went & SECOND) != 0) Moved?.Invoke("Second");
                    if ((went & KEYS) != 0) Moved?.Invoke("Keyboard");
                    if ((went & ENTER) != 0) Moved?.Invoke("Enter");
                }
                if ((went & ACCEPT) != 0) Moved?.Invoke("Accept");
                if ((went & BACK) != 0) Moved?.Invoke("Back");
                string dir = DirName(now & DIRS), before = DirName(_was & DIRS);
                if (dir == "") _repeatAt = DateTime.MaxValue;
                else if (dir != before) { Moved?.Invoke(dir); _repeatAt = at.AddMilliseconds(380); }
                else if (at >= _repeatAt) { Moved?.Invoke(dir); _repeatAt = at.AddMilliseconds(120); } // held down
            }
            else _repeatAt = DateTime.MaxValue;
        }
        _was = now;
    }

    private int Kind(IntPtr dev)
    {
        if (_kinds.TryGetValue(dev, out var k)) return k;
        k = 0;
        try
        {
            uint n = 0;
            GetRawInputDeviceInfo(dev, 0x20000007, null, ref n);
            var sb = new StringBuilder((int)n + 2);
            GetRawInputDeviceInfo(dev, 0x20000007, sb, ref n);
            var m = Regex.Match(sb.ToString(), "054c.{0,4}pid[_&]([0-9a-f]{4})", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var pid = m.Groups[1].Value.ToLowerInvariant();
                if (pid is "0ce6" or "0df2") k = 1;
                else if (pid is "05c4" or "09cc" or "0ba0") k = 2;
                if (k != 0) Seen = (Seen + " " + (k == 1 ? "DualSense" : "DualShock 4")).Trim();
            }
        }
        catch { /* not ours */ }
        _kinds[dev] = k;
        return k;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x00FF || !_on) return IntPtr.Zero; // WM_INPUT
        try
        {
            uint header = (uint)(8 + 2 * IntPtr.Size), size = 0;
            GetRawInputData(lParam, 0x10000003, IntPtr.Zero, ref size, header);
            if (size <= header + 8 || size >= 4096) return IntPtr.Zero;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, 0x10000003, buf, ref size, header) != size || Marshal.ReadInt32(buf) != 2) return IntPtr.Zero; // RIM_TYPEHID
                var dev = Marshal.ReadIntPtr(buf, 8);
                var kind = Kind(dev);
                int each = Marshal.ReadInt32(buf, (int)header), count = Marshal.ReadInt32(buf, (int)header + 4);
                if (kind == 0 || each <= 0 || count <= 0 || header + 8 + each > size) return IntPtr.Zero;
                var rep = new byte[each];
                Marshal.Copy(buf + (int)header + 8, rep, 0, each);
                var v = Parse(kind, rep);
                if (v >= 0) _psState[dev] = v;
                var st = Sticks(kind, rep);
                if (st is not null) _psSticks[dev] = st;
                if (v >= 0)
                {
                    var busy = Busy(kind, rep);
                    _psBusy[dev] = busy;
                    if (busy) LastBusy = DateTime.UtcNow;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch { /* a bad report is skipped */ }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try { Enable(false); } catch { /* closing */ }
        _timer.Stop();
        _window?.Dispose();
    }

    // ---------------------------------------------------------------- native

    [StructLayout(LayoutKind.Sequential)] private struct XState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern int XInputGetStateEx(int index, out XState state);
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawDevice[] devs, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr input, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, StringBuilder? data, ref uint size);
    [StructLayout(LayoutKind.Sequential)] private struct MousePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out MousePoint p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
