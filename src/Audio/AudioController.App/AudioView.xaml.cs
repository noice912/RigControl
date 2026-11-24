using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioController.App.Controls;
using AudioController.App.Services;
using AudioController.App.ViewModels;
using AudioController.Core.Models;
using Microsoft.Win32;

namespace AudioController.App;

/// <summary>
/// The control panel: devices (left), boards (center), slot editor (right), routing status (bottom).
/// Code-behind only handles view concerns: dialogs, drag-and-drop, mouse hold, rename.
/// </summary>
/// <remarks>
/// RigControl: this was <c>MainWindow</c>. It is now a view inside the host's window, created and owned by
/// <see cref="Hosting.AudioHost"/>. What the window did itself is re-homed: global hotkeys and the Activated refresh
/// hang off the host window (<see cref="AttachToWindow"/>), dialogs are owned by the window the view sits in (or the
/// host window) when that is visible, window placement belongs to the host, and the shutdown that ran on Closing is
/// done by <see cref="Hosting.AudioHost.Dispose"/>.
/// </remarks>
public partial class AudioView : UserControl, IUiService
{
    private readonly MainViewModel _vm;
    private HotkeyManager? _hotkeys;
    private Window? _host;
    private SlotViewModel? _mouseHoldSlot;
    private bool _shutDown;

    public AudioView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.AttachUi(this);
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>
    /// Called once by the host after its window has a handle (SourceInitialized): registers the global hotkeys on that
    /// window's HWND, forwards its Activated event, and makes it the fallback dialog owner. Further calls are ignored.
    /// </summary>
    internal void AttachToWindow(Window host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_shutDown || _host is not null) return;

        _host = host;
        host.Activated += OnHostActivated;

        try
        {
            _hotkeys = new HotkeyManager(host);
            HotkeyCaptureBox.CaptureStarted += OnCaptureStarted;
            HotkeyCaptureBox.CaptureEnded += OnCaptureEnded;
            _vm.AttachHotkeys(_hotkeys);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Hotkeys unavailable: " + ex);
            _vm.ShowStatus("Global hotkeys are unavailable: " + ex.Message, isError: true);
        }
    }

    private void OnHostActivated(object? sender, EventArgs e) => _vm.OnWindowActivated();

    private void OnCaptureStarted(object? sender, EventArgs e) => _hotkeys?.Suspend();

    private void OnCaptureEnded(object? sender, EventArgs e) => _hotkeys?.Resume();

    /// <summary>
    /// What MainWindow's Closing did before <c>_vm.Shutdown()</c>, minus saving the window placement: unhooks the host
    /// window and the capture events. The host then shuts the view model down (which releases the hotkeys). Idempotent.
    /// </summary>
    internal void Detach()
    {
        if (_shutDown) return;
        _shutDown = true;

        if (_host is not null) _host.Activated -= OnHostActivated;
        HotkeyCaptureBox.CaptureStarted -= OnCaptureStarted;
        HotkeyCaptureBox.CaptureEnded -= OnCaptureEnded;
    }

    /// <summary>
    /// The window dialogs should belong to: the window this view is in, else the host window, whichever is visible;
    /// null (an ownerless dialog, centered on screen) when neither is, e.g. while the host sits in the tray.
    /// </summary>
    private Window? DialogOwner()
    {
        var inTree = Window.GetWindow(this);
        if (inTree is { IsVisible: true }) return inTree;
        return _host is { IsVisible: true } ? _host : null;
    }

    // ------------------------------------------------------------------ toolbar

    /// <summary>Settings → "Open Effects": brings the Effects panel into view and focuses its power button.</summary>
    public void ShowEffects()
    {
        if ((Window.GetWindow(this) ?? _host) is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        EffectsPanel.BringIntoView();
        EffectsPanel.FocusPanel();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_vm.CreateSettingsViewModel());
        if (DialogOwner() is { } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        if (window.OpenEffectsRequested) ShowEffects();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import sounds (up to 30 seconds each)",
            Multiselect = true,
            Filter = ImportService.FileDialogFilter,
            CheckFileExists = true,
        };

        var owner = DialogOwner();
        bool? picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (picked == true && dialog.FileNames.Length > 0)
            _ = RunImportAsync(dialog.FileNames);
    }

    private async Task RunImportAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _vm.ImportAsync(paths);
        }
        catch (Exception ex)
        {
            ShowError("Import failed", ex.Message);
        }
    }

    // ------------------------------------------------------------------ drag and drop

    private void Boards_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files && _vm.CanImport ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (files)
        {
            DropOverlayText.Text = _vm.CanImport
                ? "Drop to import into this board"
                : _vm.FfmpegMissing ? "Import is disabled (FFmpeg not found)" : "An import is already running";
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Boards_PreviewDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Boards_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        string[]? paths = null;
        try
        {
            paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Drop data unreadable: " + ex.Message);
        }

        if (paths is null || paths.Length == 0) return;

        if (!_vm.CanImport)
        {
            if (_vm.FfmpegMissing) ShowError("Import unavailable", MainViewModel.FfmpegMissingText);
            return;
        }

        // Return from the drop right away so the drag source (Explorer) isn't kept waiting.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = RunImportAsync(paths));
    }

    // ------------------------------------------------------------------ slot tiles

    private static SlotViewModel? SlotOf(object sender) => (sender as FrameworkElement)?.DataContext as SlotViewModel;

    private void Slot_Click(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { } slot) return;
        _vm.SelectedSlot = slot;

        // Hold slots are driven by press/release below, not by the click that follows the release.
        if (slot.PlayMode != PlayMode.HoldToPlay)
            _vm.ActivateSlot(slot);
    }

    private void Slot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SlotOf(sender) is not { } slot) return;
        _vm.SelectedSlot = slot;

        if (slot.PlayMode == PlayMode.HoldToPlay)
        {
            EndMouseHold();
            _mouseHoldSlot = slot;
            _vm.BeginHold(slot);
        }
    }

    private void Slot_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndMouseHold();

    private void Slot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // The button captures the mouse while pressed, so MouseLeave can't fire; check the bounds instead.
        if (_mouseHoldSlot is null || sender is not FrameworkElement element) return;
        var p = e.GetPosition(element);
        if (p.X < 0 || p.Y < 0 || p.X > element.ActualWidth || p.Y > element.ActualHeight)
            EndMouseHold();
    }

    private void Slot_MouseLeave(object sender, MouseEventArgs e) => EndMouseHold();

    private void Slot_LostMouseCapture(object sender, MouseEventArgs e) => EndMouseHold();

    private void EndMouseHold()
    {
        if (_mouseHoldSlot is not { } slot) return;
        _mouseHoldSlot = null;
        _vm.EndHold(slot);
    }

    // ------------------------------------------------------------------ board rename

    private void BoardHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is BoardViewModel board && !board.IsEditing)
        {
            board.BeginRename();
            e.Handled = true;
        }
    }

    private void BoardName_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BoardViewModel board) return;

        if (e.Key == Key.Enter)
        {
            board.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            board.CancelRename();
            e.Handled = true;
        }
    }

    private void BoardName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BoardViewModel board)
            board.CommitRename();
    }

    private void BoardName_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            });
        }
    }

    // ------------------------------------------------------------------ IUiService

    public bool Confirm(string title, string message) =>
        ShowMessage(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message) =>
        ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowReport(string title, string summary, IReadOnlyList<ReportLine> lines)
    {
        var window = new ReportWindow(title, summary, lines);
        if (DialogOwner() is { } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    private MessageBoxResult ShowMessage(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        DialogOwner() is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image, defaultResult)
            : MessageBox.Show(message, title, buttons, image, defaultResult);
}
