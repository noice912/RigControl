namespace RigControl.Mobile.Services;

/// <summary>Where the platform code (Android intent, iPhone URL) hands in a rigcontrol:// link.</summary>
public static class DeepLinkEntry
{
    public static void Open(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        IPlatformApplication.Current?.Services.GetService<DeepLinkRouter>()?.Open(uri);
    }
}
