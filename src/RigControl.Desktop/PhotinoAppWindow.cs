using Photino.NET;
using RigControl.Core.Logging;

namespace RigControl.Desktop;

/// <summary>
/// The real window: Photino.NET around the system's own web view (WebKitGTK on Linux, WKWebView on macOS).
/// This is the only file that touches the window library. Nothing in it has run on the PC it was written on:
/// the "--smoke" start on a build machine is what proves it.
/// What the window library does not offer: it cannot say which page a message came from and it cannot stop the
/// page going to another address. So the page is only ever given RigControl's own files, its policy forbids
/// loading or connecting to anything else, and links are opened in the user's browser through app.openExternal.
/// And because another page could still end up in the window (a link or file dropped on it while RigControl's
/// own script is not running), every message is handed to the session as it is and the session reads only those
/// that carry this run's <see cref="PageKey"/>.
/// </summary>
internal sealed class PhotinoAppWindow : IAppWindow
{
    private readonly ILog _log;
    private volatile PhotinoWindow? _window;
    private volatile bool _created;
    private volatile bool _closing;
    private int _closeSent;

    public PhotinoAppWindow(ILog log) => _log = log;

    /// <summary>
    /// Settings for the web view on Linux (WebKitGTK). Photino.Native 4.0.22 creates the web view's settings with
    /// allow_universal_access_from_file_urls, allow_top_navigation_to_data_urls and
    /// javascript_can_open_windows_automatically switched on, then sets every name in this JSON on top
    /// (g_object_set_property, names as given). RigControl needs none of them: a page opened from a file must not be
    /// able to read RigControl's own page, a dropped data: address must not replace it, and no page may open windows.
    /// </summary>
    internal const string LinuxWebViewSettings =
        "{\"allow_universal_access_from_file_urls\":false," +
        "\"allow_file_access_from_file_urls\":false," +
        "\"allow_top_navigation_to_data_urls\":false," +
        "\"javascript_can_open_windows_automatically\":false}";

    /// <summary>
    /// Settings for the web view on macOS (WKWebView). There Photino.Native sets each name in this JSON on the web
    /// view's WKPreferences ([preferences setValue:forKey:]), so only real WKPreferences property names may be used:
    /// an unknown name would stop the program. macOS lets a page open windows on its own unless told otherwise.
    /// Universal file access is not switched on by the library on macOS, and file access is switched off through
    /// <see cref="PhotinoWindow.SetFileSystemAccessEnabled"/> (allowFileAccessFromFileURLs).
    /// </summary>
    internal const string MacWebViewSettings = "{\"javaScriptCanOpenWindowsAutomatically\":false}";

    /// <summary>Makes the window, shows the page and returns when the window has closed. Call on the main thread.</summary>
    public void Run(DesktopSession session)
    {
        var window = new PhotinoWindow();
        if (OperatingSystem.IsLinux()) window.SetBrowserControlInitParameters(LinuxWebViewSettings);
        else if (OperatingSystem.IsMacOS()) window.SetBrowserControlInitParameters(MacWebViewSettings);
        window
            // A page opened from a file (one dropped on the window) gets no access to other files.
            .SetFileSystemAccessEnabled(false)
            .SetTitle("RigControl")
            .SetUseOsDefaultSize(false)
            .SetSize(1320, 860)
            .SetMinSize(1000, 660)
            .Center()
            .SetResizable(true)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(false)
            .SetGrantBrowserPermissions(false)
            .SetLogVerbosity(0)
            // The page's files come from memory, by RigControl's own address (app://rigcontrol/...).
            .RegisterCustomSchemeHandler(UiSite.Scheme, (object _, string _, string url, out string contentType) =>
            {
                UiResponse? file = null;
                try { file = session.ServeFile(url); }
                catch (Exception ex) { _log.Warn("A page file could not be read.", ex); }
                contentType = file?.ContentType ?? "text/plain";
                return file is null ? null! : new MemoryStream(file.Body, writable: false);
            })
            .RegisterWebMessageReceivedHandler((_, message) => session.FromPage(message))
            .RegisterWindowCreatedHandler((_, _) =>
            {
                _created = true;
                session.WindowCreated();
            })
            .RegisterWindowClosingHandler((_, _) =>
            {
                _closing = true;
                session.WindowClosing();
                return false; // false = let it close
            })
            .Load(session.StartAddress);
        _window = window;
        window.WaitForClose();
        _closing = true;
    }

    public void Send(string json)
    {
        var w = _window;
        if (w is null || !_created || _closing) return;
        // On the window's own thread, as the web view requires.
        w.Invoke(() =>
        {
            if (!_closing) w.SendWebMessage(json);
        });
    }

    public void Navigate(Uri address)
    {
        var w = _window;
        if (w is null || !_created || _closing) return;
        w.Invoke(() =>
        {
            if (!_closing) w.Load(address);
        });
    }

    public void Close()
    {
        var w = _window;
        if (w is null || _closing) return;
        // Too early: the session asks again as soon as the window is there (DesktopSession.WindowCreated).
        if (!_created) return;
        // From another thread this waits in the window's queue, so it is safe before the window's loop has started.
        w.Invoke(() =>
        {
            if (!_closing && Interlocked.Exchange(ref _closeSent, 1) == 0) w.Close();
        });
    }
}
