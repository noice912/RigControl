using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RigControl.Core.Logging;

namespace RigControl.App;

/// <summary>
/// Discord's own web app in a browser window (brought over from RichPresence's overlay): the real client, with the
/// full DM list, groups and calls. Rules kept from RichPresence:
///  - Only https discord.com, discordapp.com and discord.gg load here; any other link opens in the normal browser.
///  - The page may not reach this PC's own addresses (127.0.0.1 / localhost): otherwise it finds the Discord desktop
///    app, hands over to it and shows "Discord App Launched" instead of the chat. It starts at /login for the same reason.
///  - Its own sign-in profile (cookies) in RigControl's data folder, copied once from RichPresence's.
///  - It is a separate, non-transparent window: WebView2 is unreliable inside a see-through one.
/// </summary>
public sealed class DiscordWindow : Window
{
    public const string Home = "https://discord.com/login";
    private readonly string _profile;
    private readonly ILog _log;
    private WebView2? _view;
    private bool _starting;

    public DiscordWindow(string dataDirectory, ILog log)
    {
        _profile = Path.Combine(dataDirectory, "discord-web");
        _log = log;
        Title = "RigControl - Discord";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x23));
        Closing += (_, e) =>
        {
            if (_reallyClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    private bool _reallyClose;

    /// <summary>Why Discord couldn't start, if it couldn't.</summary>
    public string? Problem { get; private set; }

    public bool SignedInProfileExists => Directory.Exists(_profile);

    public static bool IsAllowed(string? url)
    {
        if (url == "about:blank") return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var h = u.Host.ToLowerInvariant();
        return h is "discord.com" or "discordapp.com" or "discord.gg"
               || h.EndsWith(".discord.com", StringComparison.Ordinal) || h.EndsWith(".discordapp.com", StringComparison.Ordinal)
               || h.EndsWith(".discord.gg", StringComparison.Ordinal);
    }

    private void OpenOutside(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { RigControl.Presence.Launcher.Start(u.AbsoluteUri); }
        catch (Exception ex) { _log.Warn("Couldn't open a link from Discord: " + ex.Message); }
    }

    /// <summary>
    /// Copies RichPresence's Discord sign-in once (only when RigControl has none yet and RichPresence isn't running,
    /// because a profile in use can't be copied cleanly). RichPresence's own folder is never changed.
    /// </summary>
    public string? CopyRichPresenceProfile()
    {
        // Only ever once: after "Sign out and forget" (or once RigControl has its own sign-in) RichPresence's is
        // never copied again, or signing out would undo itself the next time Discord opens.
        if (File.Exists(CopiedMarker)) return null;
        if (Directory.Exists(_profile))
        {
            MarkCopied();
            return null;
        }
        var source = Path.Combine(RigControl.Presence.PresenceSettings.RichPresenceDataDir, "discord-web");
        if (!Directory.Exists(source)) return null;
        if (RigControl.Presence.PresenceModule.IsRichPresenceRunning()) return "RichPresence is running, so its Discord sign-in wasn't copied. Close it and open Discord again, or sign in here.";
        try
        {
            CopyDir(source, _profile);
            MarkCopied();
            _log.Info("Discord sign-in copied from RichPresence.");
            return null;
        }
        catch (Exception ex)
        {
            try { Directory.Delete(_profile, true); } catch { /* half a copy is worse than none */ }
            return "Couldn't copy RichPresence's Discord sign-in: " + ex.Message;
        }
    }

    private string CopiedMarker => _profile + ".copied-from-richpresence";

    private void MarkCopied()
    {
        try { File.WriteAllText(CopiedMarker, DateTimeOffset.Now.ToString("o")); }
        catch (Exception ex) { _log.Warn("Couldn't note that the Discord sign-in was copied: " + ex.Message); }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(f);
            if (name is "lockfile" or "LOCK" or "SingletonLock" or "SingletonCookie" or "SingletonSocket") continue;
            File.Copy(f, Path.Combine(to, name), overwrite: false);
        }
        foreach (var d in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(d);
            // Caches are rebuilt by the browser; leaving them out keeps the copy small.
            if (name is "Cache" or "Code Cache" or "GPUCache" or "Crashpad" or "ShaderCache" or "GrShaderCache" or "DawnCache" or "DawnGraphiteCache" or "DawnWebGPUCache") continue;
            CopyDir(d, Path.Combine(to, name));
        }
    }

    /// <summary>Shows Discord in the given screen rectangle (device-independent pixels), starting the browser on first use.</summary>
    public async Task ShowAtAsync(Rect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (!IsVisible) Show();
        Activate();
        if (_view is not null || _starting) return;
        _starting = true;
        try
        {
            Problem = CopyRichPresenceProfile();
            Directory.CreateDirectory(_profile);
            var env = await WebViewSafe.CreateAsync(_profile);
            _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x16, 0x1C, 0x23) };
            Content = _view;
            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenOutside(e.Uri);
            };
            foreach (var local in new[] { "http://127.0.0.1:*", "http://localhost:*", "https://127.0.0.1:*", "https://localhost:*", "http://127.0.0.1/*", "http://localhost/*", "https://127.0.0.1/*", "https://localhost/*", "ws://127.0.0.1:*", "ws://localhost:*" })
                core.AddWebResourceRequestedFilter(local, CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                try { e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", ""); } catch { /* closing */ }
            };
            core.NavigationStarting += (_, e) =>
            {
                if (IsAllowed(e.Uri)) return;
                e.Cancel = true;
                OpenOutside(e.Uri);
            };
            core.ProcessFailed += (_, e) => _log.Warn("Discord browser process failed: " + e.ProcessFailedKind);
            core.Navigate(Home);
            _log.Info("Discord opened in RigControl.");
        }
        catch (Exception ex)
        {
            Problem = "Discord couldn't start here: " + ex.Message;
            _log.Warn(Problem);
            try { _view?.Dispose(); } catch { /* ignore */ }
            _view = null;
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Closes the browser and deletes its profile (the Discord sign-in, cookies and cache kept by RigControl).</summary>
    public async Task SignOutAndForgetAsync()
    {
        MarkCopied(); // and never bring RichPresence's sign-in back after this
        Hide();
        try { _view?.Dispose(); } catch { /* ignore */ }
        _view = null;
        Content = null;
        // The browser needs a moment to let go of its files.
        for (var i = 0; i < 10 && Directory.Exists(_profile); i++)
        {
            await Task.Delay(500);
            try { Directory.Delete(_profile, true); } catch { /* still in use */ }
        }
        _log.Info(Directory.Exists(_profile) ? "Discord sign-in could not be fully deleted yet." : "Discord sign-in forgotten.");
    }

    public void CloseForGood()
    {
        _reallyClose = true;
        try { _view?.Dispose(); } catch { /* ignore */ }
        Close();
    }
}
