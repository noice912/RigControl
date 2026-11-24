using RigControl.App;

namespace RigControl.App.Tests;

public class WebViewSafeTests
{
    [Fact]
    public void A_release_build_never_takes_a_dev_page_address()
    {
        Assert.Null(WebViewSafe.DevUrl("http://127.0.0.1:5173", debugBuild: false, elevated: false));
        Assert.Null(WebViewSafe.DevUrl("http://localhost:5173/", debugBuild: false, elevated: true));
    }

    [Fact]
    public void A_copy_running_as_administrator_never_takes_a_dev_page_address()
    {
        Assert.Null(WebViewSafe.DevUrl("http://127.0.0.1:5173", debugBuild: true, elevated: true));
    }

    [Fact]
    public void A_debug_build_without_administrator_rights_takes_only_an_address_on_this_PC()
    {
        Assert.Equal("http://127.0.0.1:5173", WebViewSafe.DevUrl("http://127.0.0.1:5173/some/page", true, false));
        Assert.Equal("http://localhost:5173", WebViewSafe.DevUrl(" http://localhost:5173 ", true, false));
        Assert.Null(WebViewSafe.DevUrl("http://example.com:5173", true, false));
        Assert.Null(WebViewSafe.DevUrl("file:///C:/page.html", true, false));
        Assert.Null(WebViewSafe.DevUrl("not an address", true, false));
        Assert.Null(WebViewSafe.DevUrl("", true, false));
        Assert.Null(WebViewSafe.DevUrl(null, true, false));
    }

    [Fact]
    public void WebView2_settings_from_environment_variables_are_removed_from_this_process()
    {
        // Process-only variables: nothing outside this test run is changed.
        System.Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--remote-debugging-port=9", EnvironmentVariableTarget.Process);
        System.Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", @"C:\somewhere", EnvironmentVariableTarget.Process);
        System.Environment.SetEnvironmentVariable("webview2_user_data_folder", @"C:\elsewhere", EnvironmentVariableTarget.Process);

        var cleared = WebViewSafe.ClearOverrides();

        Assert.Contains("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", cleared);
        Assert.Contains("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", cleared);
        Assert.Contains(cleared, n => n.Equals("WEBVIEW2_USER_DATA_FOLDER", StringComparison.OrdinalIgnoreCase));
        Assert.Null(System.Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"));
        Assert.Null(System.Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"));
        Assert.Null(System.Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER"));
        Assert.Empty(WebViewSafe.ClearOverrides());
    }
}
