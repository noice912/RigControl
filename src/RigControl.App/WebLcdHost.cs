using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RigControl.Core.Logging;

namespace RigControl.App;

/// <summary>
/// The hidden browser behind the cooler screen's "web page" mode. It opens one page in a 640x640 window placed off
/// the visible desktop and takes a picture of it a few times a second for the cooler module to send to the screen.
/// The page gets its own browser profile, cannot open windows, download files or ask for permissions, and has no
/// connection to RigControl itself.
/// </summary>
public sealed class WebLcdHost
{
    private readonly Dispatcher _dispatcher;
    private readonly string _profile;
    private readonly ILog _log;
    private Window? _window;
    private WebView2? _view;
    private DispatcherTimer? _timer;
    private bool _capturing;
    private volatile string? _wanted;
    private volatile byte[]? _latest;

    public WebLcdHost(Dispatcher dispatcher, string dataDirectory, ILog log)
    {
        _dispatcher = dispatcher;
        _profile = Path.Combine(dataDirectory, "WebView2-screen");
        _log = log;
    }

    /// <summary>
    /// Safe from any thread. Given an address: makes sure that page is open and returns its newest picture as PNG
    /// bytes (null until the first one is ready). Given null: closes the page.
    /// </summary>
    public byte[]? Latest(string? url)
    {
        if (url is null)
        {
            if (_wanted is not null)
            {
                _wanted = null;
                _latest = null;
                _dispatcher.BeginInvoke(Close);
            }
            return null;
        }
        if (url != _wanted)
        {
            _wanted = url;
            _latest = null;
            _dispatcher.BeginInvoke(() => _ = OpenAsync(url));
        }
        return _latest;
    }

    private async Task OpenAsync(string url)
    {
        try
        {
            if (!(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")) return;
            if (_view is null)
            {
                _view = new WebView2();
                _window = new Window
                {
                    Title = "RigControl cooler screen page",
                    Width = 640,
                    Height = 640,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Focusable = false,
                    Left = -20000,
                    Top = -20000,
                    Content = _view,
                };
                _window.Show();
                var env = await WebViewSafe.CreateAsync(_profile);
                await _view.EnsureCoreWebView2Async(env);
                var core = _view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.IsMuted = true;
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _timer.Tick += async (_, _) => await CaptureAsync();
                _timer.Start();
            }
            if (_wanted != url) return; // changed again while the browser was starting
            _view.CoreWebView2.Navigate(uri.AbsoluteUri);
            _log.Info("Cooler screen: showing web page " + uri.GetLeftPart(UriPartial.Path));
        }
        catch (Exception ex)
        {
            _log.Warn("The web page for the cooler screen could not be opened.", ex);
            Close();
        }
    }

    private async Task CaptureAsync()
    {
        if (_capturing || _view?.CoreWebView2 is null || _wanted is null) return;
        _capturing = true;
        try
        {
            using var ms = new MemoryStream();
            await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            if (_wanted is not null && ms.Length > 0) _latest = ms.ToArray();
        }
        catch
        {
            // the page is navigating or the browser is closing; the next tick tries again
        }
        finally
        {
            _capturing = false;
        }
    }

    private void Close()
    {
        if (_wanted is not null) return; // a new page was asked for meanwhile
        try { _timer?.Stop(); } catch { /* ignore */ }
        _timer = null;
        try { _view?.Dispose(); } catch { /* ignore */ }
        _view = null;
        try { _window?.Close(); } catch { /* ignore */ }
        _window = null;
    }
}
