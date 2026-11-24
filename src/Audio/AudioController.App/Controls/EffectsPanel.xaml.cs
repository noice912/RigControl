using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AudioController.App.Services;
using AudioController.App.ViewModels;
using AudioController.Core.AudioMath;

namespace AudioController.App.Controls;

/// <summary>
/// The Effects panel. Code-behind only handles view concerns: the Hz boxes' Enter/Escape, the "Save as…" box, and the
/// live spectrum — a <see cref="SpectrumAnalyzer"/> on the headset that runs only while the window is visible and not
/// minimized (the capture is released otherwise) and is drawn ~30 times a second.
/// </summary>
public partial class EffectsPanel : UserControl
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private readonly SpectrumAnalyzer _analyzer = new();
    private readonly SpectrumBallistics _ballistics = new(SpectrumAnalyzer.BarCount);
    private readonly float[] _bars = new float[SpectrumAnalyzer.BarCount];
    private readonly DispatcherTimer _timer;
    private Window? _window;
    private MainViewModel? _main;
    private long _lastTick;
    private long _lastAttempt;

    public EffectsPanel()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _analyzer.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTooltip);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => AttachMain();
        IsVisibleChanged += (_, _) => UpdateSpectrumActive();
        Spectrum.ShowIdle();
    }

    /// <summary>True while the spectrum is listening (for tests and the smoke host).</summary>
    public bool SpectrumRunning => _timer.IsEnabled;

    /// <summary>Moves keyboard focus to the power button (Settings → "Open Effects").</summary>
    public void FocusPanel() => PowerButton.Focus();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
            _window.IsVisibleChanged += OnWindowVisibleChanged;
        }

        AttachMain();
        UpdateSpectrumActive();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.IsVisibleChanged -= OnWindowVisibleChanged;
            _window = null;
        }

        StopSpectrum();
        if (_main is not null) _main.PropertyChanged -= OnMainPropertyChanged;
        _main = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateSpectrumActive();

    private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateSpectrumActive();

    private void AttachMain()
    {
        var main = (DataContext as EffectsViewModel)?.Main;
        if (ReferenceEquals(main, _main)) return;
        if (_main is not null) _main.PropertyChanged -= OnMainPropertyChanged;
        _main = main;
        if (_main is not null) _main.PropertyChanged += OnMainPropertyChanged;
        UpdateSpectrumActive();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SpectrumDeviceId)) UpdateSpectrumActive();
    }

    /// <summary>Listens only while the window is on screen; otherwise releases the device.</summary>
    private void UpdateSpectrumActive()
    {
        bool onScreen = IsLoaded && IsVisible && _window is { IsVisible: true } w && w.WindowState != WindowState.Minimized;
        string? deviceId = _main?.SpectrumDeviceId;
        if (!onScreen || deviceId is null)
        {
            StopSpectrum();
            UpdateTooltip();
            return;
        }

        if (!string.Equals(_analyzer.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            _lastAttempt = Stopwatch.GetTimestamp();
            _analyzer.Start(deviceId);
        }

        if (!_timer.IsEnabled)
        {
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        }

        UpdateTooltip();
    }

    private void StopSpectrum()
    {
        _timer.Stop();
        _analyzer.Stop();
        _ballistics.Reset();
        Spectrum.ShowIdle();
    }

    private void OnTick()
    {
        double dt = Stopwatch.GetElapsedTime(_lastTick).TotalSeconds;
        _lastTick = Stopwatch.GetTimestamp();

        if (_analyzer.TryGetBars(_bars))
        {
            _ballistics.Update(_bars, dt);
            Spectrum.Update(_ballistics.Levels, _ballistics.Peaks);
            return;
        }

        if (!Spectrum.IsIdle) Spectrum.ShowIdle();

        // Busy or vanished device: try again every few seconds while the window is visible.
        if (_analyzer.Error is not null && Stopwatch.GetElapsedTime(_lastAttempt) > RetryAfter && _main?.SpectrumDeviceId is { } id)
        {
            _lastAttempt = Stopwatch.GetTimestamp();
            _analyzer.Start(id);
        }
    }

    private void UpdateTooltip()
    {
        string text;
        if (_main?.SpectrumDeviceId is null)
            text = "Spectrum: pick your headset in Setup (and connect it) to see what you hear.";
        else if (_analyzer.Error is { } error)
            text = error + " — retrying every few seconds.";
        else
            text = "Live spectrum of what you hear on your headset (after Equalizer APO), 20 Hz – 20 kHz. Display only; nothing is recorded.";
        SpectrumFrame.ToolTip = text;
    }

    private void HzBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }

    private void PresetNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not EffectsViewModel vm) return;
        if (e.Key == Key.Enter && vm.ConfirmSaveCommand.CanExecute(null))
        {
            vm.ConfirmSaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.CancelSaveCommand.CanExecute(null))
        {
            vm.CancelSaveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void PresetNameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                PresetNameBox.Focus();
                PresetNameBox.SelectAll();
            });
        }
    }
}
