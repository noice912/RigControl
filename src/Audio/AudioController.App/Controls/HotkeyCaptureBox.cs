using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioController.App.Services;

namespace AudioController.App.Controls;

/// <summary>
/// Read-only text box that records a key chord: press the keys to set a gesture such as
/// "Ctrl+Alt+D1"; Esc (or Backspace/Delete) clears it. Letters, digits and other typing keys need
/// Ctrl, Alt or Win so a hotkey can never swallow normal typing; F-keys, numpad and media keys may
/// stand alone. While a box has keyboard focus, global hotkeys are suspended (see
/// <see cref="CaptureStarted"/>) so the keys reach the box instead of firing sounds.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnGestureChanged));

    /// <summary>When true the gesture is shown marked "(conflict)" (it could not be registered).</summary>
    public static readonly DependencyProperty HasConflictProperty = DependencyProperty.Register(
        nameof(HasConflict), typeof(bool), typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata(false, OnGestureChanged));

    /// <summary>Raised when any capture box gains keyboard focus (suspend global hotkeys).</summary>
    public static event EventHandler? CaptureStarted;

    /// <summary>Raised when a capture box loses keyboard focus (resume global hotkeys).</summary>
    public static event EventHandler? CaptureEnded;

    private bool _capturing;

    public HotkeyCaptureBox()
    {
        // Implicit styles only match the exact type; reuse the themed TextBox style.
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ContextMenu = null;
        InputMethod.SetIsInputMethodEnabled(this, false);
        ToolTip = "Click, then press the key combination. Esc clears.";
        UpdateText(null);
    }

    public string? Gesture
    {
        get => (string?)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public bool HasConflict
    {
        get => (bool)GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyCaptureBox)d).UpdateText(null);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_capturing) return;

        _capturing = true;
        CaptureStarted?.Invoke(this, EventArgs.Empty);
        UpdateText(null);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!_capturing) return;

        _capturing = false;
        CaptureEnded?.Invoke(this, EventArgs.Empty);
        UpdateText(null);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers;

        // Plain Tab keeps normal focus navigation.
        if (key == Key.Tab && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        if (modifiers == ModifierKeys.None && key is Key.Escape or Key.Back or Key.Delete)
        {
            Gesture = null;
            UpdateText(null);
            return;
        }

        if (HotkeyManager.IsModifierKey(key) || key == Key.None)
        {
            UpdateText(HotkeyManager.Format(modifiers, Key.None).Replace("+None", "+…").Replace("None", "…"));
            return;
        }

        bool hasStrongModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        if (!hasStrongModifier && !HotkeyManager.IsStandaloneKey(key))
        {
            UpdateText("Add Ctrl, Alt or Win to that key");
            return;
        }

        var gesture = HotkeyManager.Format(modifiers, key);
        if (!HotkeyManager.TryParse(gesture, out _, out _))
        {
            UpdateText("That key can't be used as a hotkey");
            return;
        }

        Gesture = gesture;
        UpdateText(null);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (Keyboard.Modifiers == ModifierKeys.None)
            UpdateText(null);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e) => e.Handled = true;

    private void UpdateText(string? transient)
    {
        if (transient is not null)
        {
            Text = transient;
            return;
        }

        Text = string.IsNullOrWhiteSpace(Gesture)
            ? (_capturing ? "Press a key combination…" : "None")
            : HasConflict && !_capturing ? Gesture + " (conflict)" : Gesture!;
    }
}
