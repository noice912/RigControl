using RigControl.Mobile.Core.DeepLinks;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Core.Music;

namespace RigControl.Mobile.Services;

/// <summary>
/// "Connect Spotify": signs in on Spotify's own page (PKCE, read-only scopes), hands the refresh token to every
/// linked PC (presence.spotifyConnect) and forgets it. The phone keeps only the names of the PCs that took it.
/// iPhone: an in-app Safari sign-in sheet (WebAuthenticator) that returns rigcontrol://callback directly.
/// Android: the system browser, back through rigcontrol://callback (DeepLinkRouter). Tokens and codes are never logged.
/// </summary>
public sealed class SpotifyConnection
{
    private readonly ILinkService _link;
    private readonly AppSettings _settings;
    private readonly SpotifySignIn _signIn = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    private TaskCompletionSource<IReadOnlyDictionary<string, string>>? _callback;

    public SpotifyConnection(ILinkService link, AppSettings settings, DeepLinkRouter links)
    {
        _link = link;
        _settings = settings;
        links.CallbackReceived += OnCallback;
    }

    /// <summary>The PCs that have the Spotify sign-in (names only), empty when not connected.</summary>
    public IReadOnlyList<string> ConnectedTo => _settings.SpotifyConnectedTo;

    /// <summary>Signs in and gives the sign-in to the linked PCs. Returns a sentence for the person.</summary>
    public async Task<(bool Ok, string Message)> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var pcs = await _link.GetPcsAsync(cancellationToken);
        if (pcs.Count == 0)
            return (false, "Link a PC first: the PC is what shows your Spotify song on Discord.");

        var request = _signIn.Start();
        IReadOnlyDictionary<string, string> query;
        try
        {
#if IOS
            var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = request.Url,
                CallbackUrl = new Uri(SpotifyApp.RedirectUri),
                PrefersEphemeralWebBrowserSession = false,
            });
            query = result.Properties;
#else
            var waiting = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _callback, waiting)?.TrySetCanceled();
            await Browser.Default.OpenAsync(request.Url, BrowserLaunchMode.SystemPreferred);
            using (cancellationToken.Register(() => waiting.TrySetCanceled()))
                query = await waiting.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
#endif
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or TimeoutException)
        {
            _signIn.Cancel();
            return (false, "Spotify sign-in was cancelled.");
        }

        var signedIn = await _signIn.CompleteAsync(query, cancellationToken);
        if (!signedIn.Ok)
            return (false, signedIn.Error!);

        var results = await LinkedPcs.SendToAllAsync(_link, PcAction.SpotifyConnect(signedIn.RefreshToken!), cancellationToken);
        var took = results.Where(r => r.Result.Ok).Select(r => r.Pc.Name).ToArray();
        var missed = results.Where(r => !r.Result.Ok).Select(r => r.Pc.Name).ToArray();
        if (took.Length == 0)
            return (false, "Signed in to Spotify, but no PC could take it: " + Describe(results) + " Turn the PC on and try again.");
        _settings.SpotifyConnectedTo = took.Union(_settings.SpotifyConnectedTo).Distinct().ToArray();
        return (true, missed.Length == 0
            ? $"Spotify connected to {Join(took)}."
            : $"Spotify connected to {Join(took)}. Not to {Join(missed)} (offline): connect again when it's on.");
    }

    /// <summary>Asks every linked PC to forget the Spotify sign-in.</summary>
    public async Task<(bool Ok, string Message)> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var results = await LinkedPcs.SendToAllAsync(_link, PcAction.SpotifyDisconnect(), cancellationToken);
        var linkedNames = results.Select(r => r.Pc.Name).ToHashSet(StringComparer.Ordinal);
        var stillHave = results.Where(r => !r.Result.Ok).Select(r => r.Pc.Name)
            .Intersect(_settings.SpotifyConnectedTo).ToArray();
        // PCs no longer linked can't be asked; they drop out of the list here.
        _settings.SpotifyConnectedTo = stillHave.Where(linkedNames.Contains).ToArray();
        return stillHave.Length == 0
            ? (true, "Spotify disconnected. Your PCs no longer read what Spotify plays.")
            : (false, $"{Join(stillHave)} couldn't be reached and still has the Spotify sign-in. Try again when it's on.");
    }

    private void OnCallback(object? sender, DeepLink.Callback callback)
    {
        var waiting = Interlocked.Exchange(ref _callback, null);
        if (waiting is not null)
            waiting.TrySetResult(callback.Query);
        else
            _signIn.Cancel(); // a callback nobody waits for: never complete it later
    }

    private static string Describe(IReadOnlyList<PcSendResult> results) =>
        string.Join(" ", results.Select(r => $"{r.Pc.Name}: {r.Result.Error}"));

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
