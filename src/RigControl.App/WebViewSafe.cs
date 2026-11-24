using Microsoft.Web.WebView2.Core;

namespace RigControl.App;

/// <summary>
/// The one place a WebView2 environment is made. RigControl runs as administrator, but its environment
/// variables come from the signed-in user's own settings, which any program can change. WebView2 reads
/// several of those variables (which browser to start, extra command-line switches, the profile folder),
/// so they are removed from this process first, and the options are given explicitly.
/// </summary>
internal static class WebViewSafe
{
    private const string Prefix = "WEBVIEW2_";

    /// <summary>The environment variable a debug build reads for a Vite dev server address.</summary>
    internal const string DevVariable = "RIGCONTROL_UI_DEV";

    /// <summary>Removes every WEBVIEW2_* variable from this process and returns the names that were set.</summary>
    internal static IReadOnlyList<string> ClearOverrides()
    {
        var cleared = new List<string>();
        foreach (var key in System.Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process).Keys)
        {
            if (key is string name && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                cleared.Add(name);
        }
        foreach (var name in cleared)
            System.Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        return cleared;
    }

    /// <summary>Makes the environment for a profile folder with nothing taken from environment variables.</summary>
    internal static Task<CoreWebView2Environment> CreateAsync(string userDataFolder, Action<string>? warn = null)
    {
        var cleared = ClearOverrides();
        if (cleared.Count > 0)
            warn?.Invoke("Ignored WebView2 settings from environment variables: " + string.Join(", ", cleared) + ".");
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Empty,
            AllowSingleSignOnUsingOSPrimaryAccount = false,
        };
        return CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    /// <summary>
    /// The dev server address the main window may load instead of the built-in page, or null. Only a debug
    /// build that is not running as administrator ever gets one: a page loaded this way can call everything
    /// the app can do, so a release build always shows its own page.
    /// </summary>
    internal static string? DevUrl(string? value, bool debugBuild, bool elevated)
    {
        if (!debugBuild || elevated || string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !uri.IsLoopback) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>True in a debug build only.</summary>
    internal static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
