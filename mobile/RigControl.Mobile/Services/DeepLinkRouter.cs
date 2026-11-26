using RigControl.Mobile.Core.DeepLinks;

namespace RigControl.Mobile.Services;

/// <summary>
/// Takes rigcontrol:// links from the platform code and sends them to the right screen. A pairing link only fills in
/// the Link a PC screen (the person still taps Link); sign-in callbacks are handed to whoever waits for one (Step 3).
/// A link that arrives while the app is still starting is kept until the shell is on screen.
/// </summary>
public sealed class DeepLinkRouter
{
    private readonly object _gate = new();
    private DeepLink? _pending;
    private Shell? _shell;

    /// <summary>Raised on the main thread for rigcontrol://callback links (Spotify sign-in, Step 3).</summary>
    public event EventHandler<DeepLink.Callback>? CallbackReceived;

    public void Open(Uri uri)
    {
        var link = DeepLink.Parse(uri);
        if (link is null)
            return;
        lock (_gate)
        {
            if (_shell is null)
            {
                _pending = link;
                return;
            }
        }
        MainThread.BeginInvokeOnMainThread(() => _ = Route(link));
    }

    /// <summary>Called by the shell once it is on screen.</summary>
    public void Attach(Shell shell)
    {
        DeepLink? pending;
        lock (_gate)
        {
            _shell = shell;
            pending = _pending;
            _pending = null;
        }
        if (pending is not null)
            MainThread.BeginInvokeOnMainThread(() => _ = Route(pending));
    }

    private async Task Route(DeepLink link)
    {
        switch (link)
        {
            case DeepLink.Pair pair:
                await Shell.Current.GoToAsync(AppShell.LinkRoute, new Dictionary<string, object>
                {
                    ["relay"] = pair.Relay,
                    ["code"] = pair.Code.Value,
                });
                break;
            case DeepLink.Callback callback:
                CallbackReceived?.Invoke(this, callback);
                break;
        }
    }
}
