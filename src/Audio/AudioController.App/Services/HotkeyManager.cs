using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AudioController.App.Services;

/// <summary>
/// System-wide hotkeys via RegisterHotKey/UnregisterHotKey on the main window's HwndSource.
/// Hotkeys fire while the app is unfocused. Each owner (a slot id, or the stop-all id) holds at most
/// one gesture.
/// </summary>
/// <remarks>
/// <para><b>Hold-to-play limitation:</b> Windows sends WM_HOTKEY on key-down (and on keyboard
/// auto-repeat) but never on key-up. For registrations with an <c>onUp</c> callback the first
/// WM_HOTKEY calls <c>onDown</c>; each auto-repeat refreshes a 500 ms timer; <c>onUp</c> runs once
/// the timer lapses and the key, or any of its modifiers, is no longer physically down. So a held hotkey stops up to about
/// half a second after release.</para>
/// <para>All members must be used on the window's UI thread.</para>
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const int MaxHotkeyId = 0xBFFF; // applications must use ids 0x0000..0xBFFF; 0 is never handed out
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromMilliseconds(500);

    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Registration> _byId = new();
    private readonly Dictionary<Guid, Registration> _byOwner = new();
    private readonly DispatcherTimer _holdTimer;
    private int _nextId = 1;
    private int _suspendCount;
    private bool _disposed;

    /// <summary>Attaches to <paramref name="window"/>; call after its SourceInitialized event.</summary>
    public HotkeyManager(Window window)
        : this(HwndSource.FromHwnd(new WindowInteropHelper(window ?? throw new ArgumentNullException(nameof(window))).EnsureHandle())
               ?? throw new InvalidOperationException("The window has no HwndSource yet."))
    {
    }

    public HotkeyManager(HwndSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _hwnd = source.Handle;
        _source.AddHook(WndProc);
        _holdTimer = new DispatcherTimer(DispatcherPriority.Input, source.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _holdTimer.Tick += OnHoldTick;
    }

    /// <summary>True while hotkey capture is in progress (keys are released to the focused capture box).</summary>
    public bool IsSuspended => _suspendCount > 0;

    /// <summary>
    /// Raised by <see cref="Resume"/> with the owners whose gesture Windows refused to re-register (another
    /// program took it while hotkeys were suspended), each with a readable reason. Those registrations are
    /// dropped, since they no longer fire.
    /// </summary>
    internal event Action<IReadOnlyList<(Guid OwnerId, string Error)>>? RegistrationsLost;

    public bool TryRegister(string gesture, Guid ownerId, Action onDown, out string? error) =>
        TryRegister(gesture, ownerId, onDown, null, out error);

    /// <summary>
    /// Registers <paramref name="gesture"/> (e.g. "Ctrl+Alt+F1") for <paramref name="ownerId"/>, replacing
    /// that owner's previous gesture. Fails (with a readable <paramref name="error"/>) when the gesture is
    /// malformed, already used in this app, or owned by another program.
    /// </summary>
    /// <param name="onUp">Optional release callback (hold-to-play); see the class remarks for timing.</param>
    public bool TryRegister(string gesture, Guid ownerId, Action onDown, Action? onUp, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onDown);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister(ownerId);

        if (string.IsNullOrWhiteSpace(gesture) || !TryParse(gesture, out uint modifiers, out uint vk))
        {
            error = $"\"{gesture}\" is not a valid hotkey.";
            return false;
        }

        foreach (var existing in _byOwner.Values)
        {
            if (existing.Modifiers == modifiers && existing.Vk == vk)
            {
                error = "Already used by another hotkey in this app.";
                return false;
            }
        }

        if (!TryAllocateId(out int id))
        {
            error = $"Too many hotkeys are registered (the limit is {MaxHotkeyId}).";
            return false;
        }

        var reg = new Registration(id, ownerId, modifiers, vk, onDown, onUp);

        if (!RegisterWithWindows(reg, out error))
            return false;

        // While capturing, keys must reach the capture box: the test registration above proved the
        // gesture is free; release it until Resume().
        if (IsSuspended)
            UnregisterHotKey(_hwnd, reg.Id);

        _byId[reg.Id] = reg;
        _byOwner[ownerId] = reg;
        error = null;
        return true;
    }

    public void Unregister(Guid ownerId)
    {
        if (_byOwner.Remove(ownerId, out var reg))
        {
            _byId.Remove(reg.Id);
            if (!IsSuspended && !_disposed)
                UnregisterHotKey(_hwnd, reg.Id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var reg in _byOwner.Values.ToList())
            Unregister(reg.OwnerId);
    }

    /// <summary>Temporarily releases every hotkey to Windows (used while a capture box has focus). Nested.</summary>
    public void Suspend()
    {
        if (_disposed) return;
        if (_suspendCount++ > 0) return;

        foreach (var reg in _byId.Values)
            UnregisterHotKey(_hwnd, reg.Id);
    }

    /// <summary>
    /// Re-registers everything released by <see cref="Suspend"/>. Gestures that can no longer be registered
    /// are dropped and reported through <see cref="RegistrationsLost"/>.
    /// </summary>
    public void Resume()
    {
        if (_disposed || _suspendCount == 0) return;
        if (--_suspendCount > 0) return;

        List<(Guid OwnerId, string Error)>? lost = null;
        foreach (var reg in _byId.Values.ToList())
        {
            if (RegisterWithWindows(reg, out var error)) continue;

            _byId.Remove(reg.Id);
            _byOwner.Remove(reg.OwnerId);
            (lost ??= new()).Add((reg.OwnerId, error ?? "Windows refused this hotkey."));
        }

        if (lost is not null)
            RegistrationsLost?.Invoke(lost);
    }

    // ------------------------------------------------------------------ parsing / formatting

    /// <summary>
    /// Parses "Ctrl+Alt+D1", "Ctrl+Shift+F5", "Win+NumPad3", "Alt+End" ... into RegisterHotKey
    /// modifier flags and a virtual-key code. Exactly one non-modifier key, which comes last.
    /// </summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            uint flag = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => MOD_CONTROL,
                "alt" => MOD_ALT,
                "shift" => MOD_SHIFT,
                "win" or "windows" or "meta" => MOD_WIN,
                _ => 0,
            };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }

        if (!TryParseKey(parts[^1], out var key) || IsModifierKey(key)) return false;

        int code = KeyInterop.VirtualKeyFromKey(key);
        if (code <= 0 || code > 0xFE) return false;

        vk = (uint)code;
        return true;
    }

    /// <summary>Canonical text for a gesture ("Ctrl+Alt+Shift+Win+Key"), or null when invalid.</summary>
    public static string? Normalize(string? gesture)
    {
        if (gesture is null || !TryParse(gesture, out uint mods, out uint vk)) return null;
        return Format(ToModifierKeys(mods), KeyInterop.KeyFromVirtualKey((int)vk));
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>Keys that make sense as a hotkey without Ctrl/Alt/Win (they never type text).</summary>
    public static bool IsStandaloneKey(Key key) =>
        key is >= Key.F1 and <= Key.F24
            or >= Key.NumPad0 and <= Key.Divide
            or Key.Pause or Key.Scroll
            or >= Key.BrowserBack and <= Key.LaunchApplication2;

    private static string KeyName(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Capital => "CapsLock",
        _ => key.ToString(),
    };

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Key.D0 + (token[0] - '0');
            return true;
        }

        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
            token = token.ToUpperInvariant();

        // Reject numeric strings ("65") that Enum.TryParse would happily accept.
        if (token.All(char.IsAsciiDigit)) return false;

        if (Enum.TryParse(token, ignoreCase: true, out key) && Enum.IsDefined(key) && key != Key.None)
            return true;

        key = token.ToLowerInvariant() switch
        {
            "esc" => Key.Escape,
            "del" => Key.Delete,
            "ins" => Key.Insert,
            "pgup" => Key.PageUp,
            "pgdn" => Key.PageDown,
            _ => Key.None,
        };
        return key != Key.None;
    }

    private static ModifierKeys ToModifierKeys(uint mods)
    {
        var m = ModifierKeys.None;
        if ((mods & MOD_CONTROL) != 0) m |= ModifierKeys.Control;
        if ((mods & MOD_ALT) != 0) m |= ModifierKeys.Alt;
        if ((mods & MOD_SHIFT) != 0) m |= ModifierKeys.Shift;
        if ((mods & MOD_WIN) != 0) m |= ModifierKeys.Windows;
        return m;
    }

    // ------------------------------------------------------------------ Windows plumbing

    /// <summary>
    /// Hands out the next id in 1..<see cref="MaxHotkeyId"/>, wrapping around and skipping ids that still
    /// belong to a live registration. False only when every id is taken.
    /// </summary>
    private bool TryAllocateId(out int id)
    {
        for (int attempt = 0; attempt < MaxHotkeyId; attempt++)
        {
            id = _nextId++;
            if (_nextId > MaxHotkeyId) _nextId = 1;
            if (!_byId.ContainsKey(id)) return true;
        }

        id = 0;
        return false;
    }

    private bool RegisterWithWindows(Registration reg, out string? error)
    {
        // Hold registrations need auto-repeat WM_HOTKEYs; everything else ignores repeats.
        uint flags = reg.Modifiers | (reg.OnUp is null ? MOD_NOREPEAT : 0);
        if (RegisterHotKey(_hwnd, reg.Id, flags, reg.Vk))
        {
            error = null;
            return true;
        }

        int code = Marshal.GetLastWin32Error();
        error = code == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "Another program (or Windows) already uses this hotkey."
            : "Windows refused this hotkey: " + new Win32Exception(code).Message;
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || _disposed) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (!_byId.TryGetValue(id, out var reg)) return IntPtr.Zero;

        handled = true;
        try
        {
            if (reg.OnUp is null)
            {
                reg.OnDown();
                return IntPtr.Zero;
            }

            reg.LastSignal = Stopwatch.GetTimestamp();
            if (!reg.Held)
            {
                reg.Held = true;
                reg.OnDown();
                if (!_holdTimer.IsEnabled) _holdTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Hotkey callback failed: " + ex);
        }

        return IntPtr.Zero;
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        bool anyHeld = false;
        foreach (var reg in _byId.Values.ToList())
        {
            if (!reg.Held) continue;

            bool lapsed = Stopwatch.GetElapsedTime(reg.LastSignal) >= HoldTimeout;
            // The gesture counts as released as soon as the key OR any of its required modifiers is up.
            bool physicallyDown = IsKeyDown((int)reg.Vk) && AreModifiersDown(reg.Modifiers);
            if (lapsed && !physicallyDown)
            {
                reg.Held = false;
                try
                {
                    reg.OnUp?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Hotkey release callback failed: " + ex);
                }
            }
            else
            {
                anyHeld = true;
            }
        }

        if (!anyHeld) _holdTimer.Stop();
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True while every modifier in <paramref name="modifiers"/> (MOD_* flags) is physically held.</summary>
    private static bool AreModifiersDown(uint modifiers) =>
        ((modifiers & MOD_CONTROL) == 0 || IsKeyDown(VK_CONTROL))
        && ((modifiers & MOD_ALT) == 0 || IsKeyDown(VK_MENU))
        && ((modifiers & MOD_SHIFT) == 0 || IsKeyDown(VK_SHIFT))
        && ((modifiers & MOD_WIN) == 0 || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN));

    public void Dispose()
    {
        if (_disposed) return;

        _holdTimer.Stop();
        if (!IsSuspended)
        {
            foreach (var reg in _byId.Values)
                UnregisterHotKey(_hwnd, reg.Id);
        }

        _byId.Clear();
        _byOwner.Clear();
        _disposed = true;

        try
        {
            _source.RemoveHook(WndProc);
        }
        catch (Exception)
        {
            // The window may already be gone.
        }
    }

    private sealed class Registration(int id, Guid ownerId, uint modifiers, uint vk, Action onDown, Action? onUp)
    {
        public int Id { get; } = id;
        public Guid OwnerId { get; } = ownerId;
        public uint Modifiers { get; } = modifiers;
        public uint Vk { get; } = vk;
        public Action OnDown { get; } = onDown;
        public Action? OnUp { get; } = onUp;
        public bool Held { get; set; }
        public long LastSignal { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
