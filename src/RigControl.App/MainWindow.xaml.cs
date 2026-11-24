using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using RigControl.Core.Json;

namespace RigControl.App;

public partial class MainWindow : Window
{
    /// <summary>Origin the embedded UI is served from. Requests to it never touch the network.</summary>
    public const string AppOrigin = "https://app.rigcontrol";

    private readonly AppServices _services;
    private readonly App _app;
    private readonly string? _devUrl;
    private bool _forceClose;
    private bool _webViewReady;
    private bool _initStarted;

    /// <summary>True once the window has closed for good.</summary>
    public bool IsClosed { get; private set; }

    public MainWindow(AppServices services, App app)
    {
        _services = services;
        _app = app;

        // Development aid (debug builds that are not elevated only): show a running Vite dev server
        // instead of the built-in page. A release build never reads the variable.
        _devUrl = WebViewSafe.DevUrl(
            WebViewSafe.IsDebugBuild ? System.Environment.GetEnvironmentVariable(WebViewSafe.DevVariable) : null,
            WebViewSafe.IsDebugBuild, _services.Environment.IsElevated);

        InitializeComponent();
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x16, 0x1C, 0x23);
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += async (_, _) => await InitWebViewAsync();
        _services.Events.Published += OnEventPublished;
        Closed += (_, _) => IsClosed = true;
    }

    private async Task InitWebViewAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var userData = Path.Combine(_services.Environment.DataDirectory, "WebView2");
            var env = await WebViewSafe.CreateAsync(userData, m => _services.Log.Warn(m));
            await WebView.EnsureCoreWebView2Async(env);

            var core = WebView.CoreWebView2;
            var devMode = _devUrl is not null;
            core.Settings.AreDevToolsEnabled = devMode;
            core.Settings.AreDefaultContextMenusEnabled = devMode;
            core.Settings.AreBrowserAcceleratorKeysEnabled = devMode;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            core.AddWebResourceRequestedFilter(AppOrigin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NewWindowRequested += (_, e) => e.Handled = true; // the UI opens links through app.openExternal
            core.NavigationStarting += (_, e) =>
            {
                if (!IsAllowedSource(e.Uri)) e.Cancel = true;
            };
            core.ProcessFailed += (_, e) =>
            {
                _services.Log.Error("WebView2 process failed: " + e.ProcessFailedKind);
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                {
                    try { core.Reload(); } catch { /* the window may be closing */ }
                }
            };

            _webViewReady = true;
            core.Navigate(devMode ? _devUrl! : AppOrigin + "/index.html" + StartPageHash());
        }
        catch (Exception ex)
        {
            _services.Log.Error("WebView2 initialisation failed.", ex);
            WebView.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
            FallbackText.Text = ex is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is missing. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start RigControl again."
                : "Details: " + ex.Message + "\n\nThe log folder is " + _services.Environment.LogDirectory;
        }
    }

    /// <summary>"--page cooling" on the command line opens that section first (overview, cooling, graphics, capture, lighting, drivers, cpu, games, audio, health, settings).</summary>
    private static string StartPageHash()
    {
        var args = System.Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return string.Empty;
        var page = args[i + 1].ToLowerInvariant();
        return page is "overview" or "cooling" or "graphics" or "capture" or "lighting" or "drivers" or "cpu" or "games" or "audio" or "health" or "settings" ? "#" + page : string.Empty;
    }

    private bool IsAllowedSource(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (uri.StartsWith(AppOrigin + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return _devUrl is not null && uri.StartsWith(_devUrl, StringComparison.OrdinalIgnoreCase);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = WebView.CoreWebView2;
        try
        {
            var path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
            if (UiAssets.TryOpen(path, out var stream, out var contentType))
            {
                e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK",
                    "Content-Type: " + contentType + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            else
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("Serving a UI asset failed: " + e.Request.Uri, ex);
            e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", "Content-Type: text/plain");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsAllowedSource(e.Source)) return;
            var request = e.WebMessageAsJson;
            // Run off the UI thread: module code may block on hardware I/O.
            var response = await Task.Run(() => _services.Dispatcher.HandleAsync(request));
            if (_webViewReady && !_forceClose)
                WebView.CoreWebView2.PostWebMessageAsJson(response);
        }
        catch (Exception ex)
        {
            _services.Log.Error("Handling a UI message failed.", ex);
        }
    }

    private void OnEventPublished(string topic, object? payload)
    {
        if (!_webViewReady || _forceClose) return;
        string json;
        try
        {
            json = JsonDefaults.Serialize(new UiEvent(topic, payload));
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Event '{topic}' could not be serialised.", ex);
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_webViewReady && !_forceClose) WebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // The WebView may be shutting down; dropping an event is harmless.
            }
        });
    }

    private sealed record UiEvent(string Event, object? Data);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            var settings = _services.Settings.Get<AppSettings>(AppSettings.Section);
            if (settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (!settings.CloseNoticeShown)
                {
                    _services.Settings.Update<AppSettings>(AppSettings.Section, s => s.CloseNoticeShown = true);
                    _app.ShowTrayNotice("RigControl is still running",
                        "It keeps your cooling and lighting going from the tray. Right-click the tray icon to exit.");
                }
            }
            else
            {
                e.Cancel = true;
                _app.RequestExit();
            }
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Shows a native view next to the rail ("audio"), or gives the whole window back to the web UI (null).
    /// Must be called on the UI thread. Returns the view that is now shown.
    /// </summary>
    public string? SetNativeView(string? view, Func<FrameworkElement?> audioView)
    {
        if (string.Equals(view, "audio", StringComparison.OrdinalIgnoreCase))
        {
            var content = audioView();
            if (content is null) throw new InvalidOperationException("The Audio section is not ready yet.");
            if (!ReferenceEquals(NativeHost.Content, content)) NativeHost.Content = content;
            System.Windows.Controls.Grid.SetColumnSpan(WebView, 1);
            NativeHost.Visibility = Visibility.Visible;
            return "audio";
        }

        NativeHost.Visibility = Visibility.Collapsed;
        System.Windows.Controls.Grid.SetColumnSpan(WebView, 2);
        return null;
    }

    /// <summary>Closes for real (used during app exit).</summary>
    public void ForceClose()
    {
        _forceClose = true;
        _services.Events.Published -= OnEventPublished;
        try { WebView.Dispose(); } catch { /* ignore */ }
        Close();
    }

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ / Windows 11)
            _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }
        catch
        {
            // Purely cosmetic.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
