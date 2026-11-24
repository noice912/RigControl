using System.Runtime.InteropServices;

namespace RigControl.Capture;

/// <summary>System-wide keyboard shortcuts. Abstracted so tests never register real ones.</summary>
public interface IHotkeys : IDisposable
{
    /// <summary>Replaces all shortcuts. Returns the names of those Windows refused (already taken by another program).</summary>
    IReadOnlyList<string> Set(IReadOnlyDictionary<string, string> shortcuts, Action<string> pressed);
}

public sealed class NoHotkeys : IHotkeys
{
    public IReadOnlyList<string> Set(IReadOnlyDictionary<string, string> shortcuts, Action<string> pressed) => Array.Empty<string>();
    public void Dispose() { }
}

/// <summary>A shortcut such as "Alt+F9" turned into what Windows needs.</summary>
public sealed record HotkeyGesture(uint Modifiers, uint Key)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    /// <summary>Parses "Ctrl+Shift+R", "Alt+F10" and the like. Null if it is empty or not understood. At least one modifier is required.</summary>
    public static HotkeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "ALT": mods |= Alt; continue;
                case "CTRL" or "CONTROL": mods |= Ctrl; continue;
                case "SHIFT": mods |= Shift; continue;
                case "WIN": mods |= Win; continue;
            }
            if (key != 0) return null;
            if (part.Length == 1 && (char.IsAsciiLetterUpper(part[0]) || char.IsAsciiDigit(part[0]))) key = part[0];
            else if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part.AsSpan(1), out var f) && f is >= 1 and <= 24) key = (uint)(0x70 + f - 1);
            else return null;
        }
        return mods != 0 && key != 0 ? new HotkeyGesture(mods, key) : null;
    }
}

/// <summary>Real shortcuts: a small background thread registers them with Windows and waits for presses.</summary>
public sealed class GlobalHotkeys : IHotkeys
{
    private const uint WmHotkey = 0x0312, WmApp = 0x8000, WmQuit = 0x0012, NoRepeat = 0x4000;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _applied = new();
    private uint _threadId;
    private List<(string Name, HotkeyGesture Gesture)> _wanted = new();
    private List<string> _failed = new();
    private readonly List<string> _registered = new(); // index = hotkey id - 1; touched only by the thread
    private Action<string>? _pressed;

    public GlobalHotkeys()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RigControl hotkeys" };
        _thread.Start();
        _ready.Wait(3000);
    }

    public IReadOnlyList<string> Set(IReadOnlyDictionary<string, string> shortcuts, Action<string> pressed)
    {
        var failed = new List<string>();
        var wanted = new List<(string, HotkeyGesture)>();
        foreach (var (name, text) in shortcuts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var g = HotkeyGesture.Parse(text);
            if (g is null) failed.Add(name);
            else wanted.Add((name, g));
        }
        lock (_gate)
        {
            _wanted = wanted;
            _pressed = pressed;
            _applied.Reset();
        }
        if (_threadId == 0 || !PostThreadMessage(_threadId, WmApp, IntPtr.Zero, IntPtr.Zero)) return failed;
        _applied.Wait(3000);
        lock (_gate) failed.AddRange(_failed);
        return failed;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // makes Windows create this thread's message queue
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmApp)
            {
                Apply();
            }
            else if (msg.message == WmHotkey)
            {
                var id = (int)msg.wParam;
                Action<string>? pressed;
                lock (_gate) pressed = _pressed;
                if (id >= 1 && id <= _registered.Count)
                {
                    var name = _registered[id - 1];
                    try { pressed?.Invoke(name); } catch { /* a handler must not kill the listener */ }
                }
            }
        }
        for (var i = 0; i < _registered.Count; i++) UnregisterHotKey(IntPtr.Zero, i + 1);
    }

    private void Apply()
    {
        for (var i = 0; i < _registered.Count; i++) UnregisterHotKey(IntPtr.Zero, i + 1);
        _registered.Clear();
        List<(string Name, HotkeyGesture Gesture)> wanted;
        lock (_gate) wanted = _wanted.ToList();
        var failed = new List<string>();
        foreach (var (name, g) in wanted)
        {
            var id = _registered.Count + 1;
            if (RegisterHotKey(IntPtr.Zero, id, g.Modifiers | NoRepeat, g.Key)) _registered.Add(name);
            else failed.Add(name);
        }
        lock (_gate) _failed = failed;
        _applied.Set();
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
