using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace RigControl.App;

/// <summary>
/// The on-screen keyboard for typing into Discord with a controller (RichPresence's, ported). The bug that mattered
/// there: this window must never take the keyboard focus, or typed characters go nowhere. So it is a no-activate,
/// tool, topmost window by its window style (not the Topmost property, which activates), shown without activation,
/// and the focus is handed back to Discord before each key. Characters go out with SendInput as Unicode.
/// </summary>
public sealed class ControllerKeyboard : Window
{
    private static readonly Dictionary<string, string[]> Pages = new()
    {
        ["abc"] = new[] { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,.?" },
        ["sym"] = new[] { "1234567890", "-/:;()$&@\"", ".,?!'#%*+=", "_\\|~<>[]{}" },
    };
    private static readonly (string Name, double Width)[] Bottom = { ("Shift", 96), ("Symbols", 96), ("Space", 250), ("Delete", 96), ("Enter", 96), ("Close", 76) };

    private readonly Border[][] _keys = new Border[5][];
    private readonly Func<IntPtr> _target;
    private int _row = 1, _col;
    private bool _shift;
    private string _page = "abc";
    /// <summary>For tests: don't send keys. The last action is in <see cref="Did"/>.</summary>
    public bool Dry;
    public string Did = "";
    public event Action? Closed2;

    private static readonly Brush KeyBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x2C, 0x36));
    private static readonly Brush HotBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xD6));

    public ControllerKeyboard(Func<IntPtr> target)
    {
        _target = target;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Width = 760;
        Height = 268;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1A));
        Title = "RigControl keyboard";
        var canvas = new Canvas();
        for (var r = 0; r < 4; r++)
        {
            _keys[r] = new Border[10];
            for (var c = 0; c < 10; c++) _keys[r][c] = AddKey(canvas, 10 + c * 74, 10 + r * 50, 68, r, c);
        }
        _keys[4] = new Border[Bottom.Length];
        double x = 10;
        for (var c = 0; c < Bottom.Length; c++)
        {
            _keys[4][c] = AddKey(canvas, x, 210, Bottom[c].Width, 4, c);
            x += Bottom[c].Width + 6;
        }
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(h, -20);
            SetWindowLong(h, -20, ex | 0x08000000 | 0x00000080 | 0x00000008); // WS_EX_NOACTIVATE | TOOLWINDOW | TOPMOST
        };
        Refresh();
    }

    private Border AddKey(Canvas canvas, double x, double y, double w, int row, int col)
    {
        var b = new Border
        {
            Width = w,
            Height = 44,
            CornerRadius = new CornerRadius(6),
            Background = KeyBrush,
            Child = new TextBlock { Foreground = Brushes.White, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseLeftButtonUp += (_, _) => { _row = row; _col = col; Invoke(); };
        Canvas.SetLeft(b, x);
        Canvas.SetTop(b, y);
        canvas.Children.Add(b);
        return b;
    }

    public bool IsOpen => IsVisible;

    private string CharAt(int r, int c)
    {
        var ch = Pages[_page][r][c].ToString();
        return _shift && _page == "abc" ? ch.ToUpperInvariant() : ch;
    }

    private void Refresh()
    {
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 10; c++)
                ((TextBlock)_keys[r][c].Child).Text = CharAt(r, c);
        for (var c = 0; c < Bottom.Length; c++)
        {
            var name = Bottom[c].Name;
            ((TextBlock)_keys[4][c].Child).Text = name == "Symbols" ? (_page == "abc" ? "#+=" : "abc") : name == "Shift" && _shift ? "SHIFT" : name;
        }
        for (var r = 0; r < 5; r++)
            for (var c = 0; c < _keys[r].Length; c++)
                _keys[r][c].Background = r == _row && c == _col ? HotBrush : KeyBrush;
    }

    /// <summary>Shows it at the bottom of the given rectangle without taking the keyboard.</summary>
    public void ShowAt(Rect over)
    {
        Left = over.Left + (over.Width - Width) / 2;
        Top = over.Bottom - Height - 8;
        Refresh();
        Show();
        var h = new WindowInteropHelper(this).Handle;
        ShowWindow(h, 4); // SW_SHOWNOACTIVATE
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0013); // topmost, no move/size/activate
    }

    public void Close2()
    {
        Hide();
        Closed2?.Invoke();
    }

    public void Move(string dir)
    {
        int r = _row, c = _col, n = r;
        switch (dir)
        {
            case "Left": c = (c - 1 + _keys[r].Length) % _keys[r].Length; break;
            case "Right": c = (c + 1) % _keys[r].Length; break;
            case "Up": n = (r + 4) % 5; break;
            case "Down": n = (r + 1) % 5; break;
        }
        if (dir is "Up" or "Down")
        {
            if (n == 4 && r != 4)
            {
                var x = Canvas.GetLeft(_keys[r][c]) + 34;
                c = 0;
                for (var i = 0; i < _keys[4].Length; i++) if (x >= Canvas.GetLeft(_keys[4][i])) c = i;
            }
            else if (r == 4 && n != 4)
            {
                var x = Canvas.GetLeft(_keys[4][c]) + _keys[4][c].Width / 2;
                c = Math.Max(0, Math.Min(9, (int)Math.Floor((x - 10) / 74)));
            }
            r = n;
        }
        _row = r;
        _col = c;
        Refresh();
    }

    /// <summary>A controller button while the keyboard is up.</summary>
    public void Pad(string what)
    {
        switch (what)
        {
            case "Accept": Invoke(); break;
            case "Second": Target(); Press(0x08); Did = "Delete"; break;   // X / Square: delete
            case "Keyboard": Target(); Type(" "); Did = "type: "; break;   // Y / Triangle: space
            case "Enter": Target(); Press(0x0D); Did = "Enter"; break;     // Start / Options: Enter
            case "Back": Close2(); break;
            default: Move(what); break;
        }
    }

    private void Invoke()
    {
        Target();
        if (_row < 4)
        {
            var ch = CharAt(_row, _col);
            Type(ch);
            Did = "type:" + ch;
            _shift = false; // Shift is for one letter
            Refresh();
            return;
        }
        var name = Bottom[_col].Name;
        Did = name;
        switch (name)
        {
            case "Shift": _shift = !_shift; break;
            case "Symbols": _page = _page == "abc" ? "sym" : "abc"; break;
            case "Space": Type(" "); break;
            case "Delete": Press(0x08); break;
            case "Enter": Press(0x0D); break;
            case "Close": Close2(); return;
        }
        Refresh();
    }

    /// <summary>Hands the keyboard focus back to Discord if something else took it.</summary>
    private void Target()
    {
        if (Dry) return;
        var t = _target();
        if (t != IntPtr.Zero && GetForegroundWindow() != t) GamePanel.BringToFront(t);
    }

    private void Press(byte vk)
    {
        if (Dry) return;
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public IntPtr Extra;
    }

    /// <summary>Types text as characters, whatever the keyboard layout is (64-bit only).</summary>
    private void Type(string text)
    {
        if (Dry || string.IsNullOrEmpty(text) || IntPtr.Size != 8) return;
        var k = new KeyInput[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            k[2 * i] = new KeyInput { Type = 1, Scan = text[i], Flags = 4 };      // KEYEVENTF_UNICODE
            k[2 * i + 1] = new KeyInput { Type = 1, Scan = text[i], Flags = 6 };  // ...and let go
        }
        SendInput((uint)k.Length, k, 40);
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, KeyInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
}
