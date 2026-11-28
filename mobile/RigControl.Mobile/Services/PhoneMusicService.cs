using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Core.Music;
#if IOS
using MediaPlayer;
using UIKit;
#endif

namespace RigControl.Mobile.Services;

/// <summary>
/// "Share my iPhone's Music app song": while the switch is on and the app is in the foreground, reads what the Music
/// app plays every few seconds and feeds it to the linked PCs (PhoneMusicFeeder: on change, else every 15 s). Going
/// to the background or switching off sends "nothing playing". iPhone only; on Android it is never available.
/// </summary>
public sealed class PhoneMusicService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    private readonly AppSettings _settings;
    private readonly PhoneMusicFeeder _feeder;
    private readonly DiscordDirect _discord;

    /// <summary>
    /// A linked PC took the song last time (its card is on Discord): the phone doesn't post its own as well, or
    /// Discord would show two cards. With no PC answering, the phone's own Discord sign-in shows it.
    /// </summary>
    private volatile bool _pcShows;
    private readonly object _gate = new();
    private CancellationTokenSource? _running;
    private Task _loop = Task.CompletedTask;
    private bool _foreground = true;

    public PhoneMusicService(ILinkService link, AppSettings settings, DiscordDirect discord)
    {
        _settings = settings;
        _feeder = new PhoneMusicFeeder(link);
        _feeder.Sent += (_, results) => _pcShows = results.Any(x => x.Result.Ok);
        _discord = discord;
    }

    /// <summary>Whether this phone can share its Music app song at all.</summary>
    public static bool IsAvailable =>
#if IOS
        true;
#else
        false;
#endif

    public bool Enabled => IsAvailable && _settings.ShareAppleMusic;

    /// <summary>
    /// The switch: turning on asks for Music access first (iOS's own prompt). Returns null when done, or a sentence
    /// when it can't be turned on (the switch should go back off).
    /// </summary>
    public async Task<string?> SetEnabledAsync(bool on)
    {
        if (!IsAvailable)
            return "Only an iPhone can share its Music app song.";
        if (on)
        {
            var problem = await AskForAccessAsync();
            if (problem is not null)
            {
                _settings.ShareAppleMusic = false;
                return problem;
            }
        }
        _settings.ShareAppleMusic = on;
        await UpdateAsync();
        return null;
    }

    /// <summary>The app came to the foreground (or started).</summary>
    public Task ResumeAsync()
    {
        _foreground = true;
        return UpdateAsync();
    }

    /// <summary>The app went to the background: the PCs are told nothing plays, then the polling stops.</summary>
    public Task PauseAsync()
    {
        _foreground = false;
        return UpdateAsync();
    }

    private async Task UpdateAsync()
    {
        var want = Enabled && _foreground;
        Task stopping;
        lock (_gate)
        {
            if (want == (_running is not null))
                return;
            if (want)
            {
                var cts = new CancellationTokenSource();
                _running = cts;
                var previous = _loop;
                _loop = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    await RunAsync(cts.Token).ConfigureAwait(false);
                });
                return;
            }
            _running!.Cancel();
            _running.Dispose();
            _running = null;
            stopping = _loop;
        }
        await stopping.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var track = await MainThread.InvokeOnMainThreadAsync(ReadNowPlaying);
                    // The PCs first; the phone's own Discord card only when no PC showed it (never two cards).
                    try { await _feeder.ReportAsync(track, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _pcShows = false; throw; }
                    finally { await _discord.ReportAsync(_pcShows ? null : track).ConfigureAwait(false); }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[music] sending the song failed: " + ex.GetType().Name);
                }
                await Task.Delay(Poll, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        await ClearAsync().ConfigureAwait(false);
    }

    /// <summary>Sends "nothing playing" (with a little background time on the iPhone so it gets out).</summary>
    private async Task ClearAsync()
    {
#if IOS
        var task = UIApplication.BackgroundTaskInvalid;
        var ended = 0;
        void End()
        {
            if (task != UIApplication.BackgroundTaskInvalid && System.Threading.Interlocked.Exchange(ref ended, 1) == 0)
                UIApplication.SharedApplication.EndBackgroundTask(task);
        }
        // iOS ending the time early must still end the task, or it kills the app.
        task = UIApplication.SharedApplication.BeginBackgroundTask("RigControl music off", End);
#endif
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _discord.ReportAsync(null).ConfigureAwait(false);
            await _feeder.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[music] clearing the song failed: " + ex.GetType().Name);
        }
#if IOS
        finally
        {
            End();
        }
#endif
    }

#if IOS
    private static async Task<string?> AskForAccessAsync()
    {
        var status = MPMediaLibrary.AuthorizationStatus;
        if (status == MPMediaLibraryAuthorizationStatus.NotDetermined)
            status = await MPMediaLibrary.RequestAuthorizationAsync();
        return status switch
        {
            MPMediaLibraryAuthorizationStatus.Authorized => null,
            MPMediaLibraryAuthorizationStatus.Restricted => "This iPhone doesn't allow apps to see the Music app (Screen Time or a profile).",
            _ => "RigControl isn't allowed to see the Music app. Turn on Media & Apple Music for RigControl in the iPhone's Settings.",
        };
    }

    /// <summary>The Music app's song (main thread). Null when nothing is loaded or access is gone.</summary>
    private static PhoneMusicTrack? ReadNowPlaying()
    {
        if (MPMediaLibrary.AuthorizationStatus != MPMediaLibraryAuthorizationStatus.Authorized)
            return null;
        var player = MPMusicPlayerController.SystemMusicPlayer;
        var item = player.NowPlayingItem;
        if (item is null)
            return null;
        var state = player.PlaybackState;
        if (state is MPMusicPlaybackState.Stopped or MPMusicPlaybackState.Interrupted)
            return null;
        return new PhoneMusicTrack(item.Title, item.Artist, item.AlbumTitle,
            state == MPMusicPlaybackState.Playing, player.CurrentPlaybackTime, item.PlaybackDuration);
    }
#else
    private static Task<string?> AskForAccessAsync() => Task.FromResult<string?>("Only an iPhone can share its Music app song.");

    private static PhoneMusicTrack? ReadNowPlaying() => null;
#endif
}
