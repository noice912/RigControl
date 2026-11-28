using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Services;

namespace RigControl.Mobile.ViewModels;

/// <summary>Settings: this phone's name, the relay, music on Discord and about.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SpotifyConnection _spotify;
    private readonly PhoneMusicService _music;
    private readonly DiscordDirect _discord;
    private bool _settingSwitch;

    /// <summary>"Sign in with Discord" is offered (iPhone builds with Discord's SDK).</summary>
    public bool ShowDiscord { get; } = DiscordDirect.IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscordSignedOut))]
    public partial bool DiscordSignedIn { get; set; }

    public bool DiscordSignedOut => !DiscordSignedIn;

    [ObservableProperty] public partial string DiscordStatus { get; set; } = "";
    [ObservableProperty] public partial string? DiscordMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInDiscordCommand), nameof(SignOutDiscordCommand))]
    public partial bool DiscordBusy { get; set; }

    [ObservableProperty] public partial string PhoneName { get; set; }
    [ObservableProperty] public partial string Relay { get; set; }

    /// <summary>The iPhone Music app switch is shown (iPhone only).</summary>
    public bool ShowAppleMusic { get; } = PhoneMusicService.IsAvailable;

    [ObservableProperty] public partial bool ShareAppleMusic { get; set; }
    [ObservableProperty] public partial string? MusicMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpotifyNotConnected))]
    public partial bool SpotifyConnected { get; set; }

    public bool SpotifyNotConnected => !SpotifyConnected;

    [ObservableProperty] public partial string SpotifyStatus { get; set; } = "";
    [ObservableProperty] public partial string? SpotifyMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectSpotifyCommand), nameof(DisconnectSpotifyCommand))]
    public partial bool SpotifyBusy { get; set; }

    public string Version { get; } = $"{AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";

    /// <summary>Says plainly when the app shows sample PCs instead of real ones.</summary>
    public string LinkMode { get; }

    public SettingsViewModel(AppSettings settings, ILinkService link, SpotifyConnection spotify, PhoneMusicService music, DiscordDirect discord)
    {
        _settings = settings;
        _spotify = spotify;
        _music = music;
        _discord = discord;
        discord.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(ShowDiscordState);
        ShowDiscordState();
        PhoneName = settings.PhoneName;
        Relay = settings.Relay.Length > 0 ? settings.Relay
            : MobileDefaults.HasRelay ? MobileDefaults.RelayHost : "Set by the first PC you link";
        LinkMode = link is FakeLinkService
            ? "Sample PCs only: this build was made with the stand-in link and can't reach a real PC."
            : MobileDefaults.HasRelay
                ? "Linked through the RigControl relay, end-to-end encrypted."
                : "Linked through the relay your PC uses, end-to-end encrypted. This build has no relay address built in, so link each PC by scanning its QR code, which carries the address.";

        _settingSwitch = true;
        ShareAppleMusic = music.Enabled;
        _settingSwitch = false;
        ShowSpotify();
    }

    partial void OnPhoneNameChanged(string value) => _settings.PhoneName = value;

    partial void OnShareAppleMusicChanged(bool value)
    {
        if (_settingSwitch)
            return;
        _ = ApplyShareAppleMusic(value);
    }

    private async Task ApplyShareAppleMusic(bool on)
    {
        MusicMessage = null;
        var problem = await _music.SetEnabledAsync(on);
        if (problem is null)
            return;
        MusicMessage = problem;
        _settingSwitch = true;
        ShareAppleMusic = false;
        _settingSwitch = false;
    }

    private bool CanUseSpotify() => !SpotifyBusy;

    [RelayCommand(CanExecute = nameof(CanUseSpotify))]
    private async Task ConnectSpotify()
    {
        SpotifyBusy = true;
        SpotifyMessage = "Sign in on Spotify's page, then come back here.";
        try
        {
            var (_, message) = await _spotify.ConnectAsync();
            SpotifyMessage = message;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[spotify] connect failed: " + ex.GetType().Name);
            SpotifyMessage = "Spotify sign-in didn't work. Try again.";
        }
        finally
        {
            SpotifyBusy = false;
            ShowSpotify();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSpotify))]
    private async Task DisconnectSpotify()
    {
        SpotifyBusy = true;
        SpotifyMessage = null;
        try
        {
            var (_, message) = await _spotify.DisconnectAsync();
            SpotifyMessage = message;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[spotify] disconnect failed: " + ex.GetType().Name);
            SpotifyMessage = "Couldn't reach your PCs. Try again.";
        }
        finally
        {
            SpotifyBusy = false;
            ShowSpotify();
        }
    }

    private bool CanUseDiscord() => !DiscordBusy;

    [RelayCommand(CanExecute = nameof(CanUseDiscord))]
    private async Task SignInDiscord()
    {
        DiscordBusy = true;
        try
        {
            DiscordMessage = await _discord.SignInAsync();
            // The Music app switch has to be on for there to be a song to show.
            if (_discord.SignedIn && !ShareAppleMusic)
                ShareAppleMusic = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[discord] sign-in failed: " + ex.GetType().Name);
            DiscordMessage = "Discord sign-in didn't work. Try again.";
        }
        finally
        {
            DiscordBusy = false;
            ShowDiscordState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDiscord))]
    private async Task SignOutDiscord()
    {
        DiscordBusy = true;
        try { await _discord.SignOutAsync(); }
        finally
        {
            DiscordBusy = false;
            ShowDiscordState();
        }
    }

    private void ShowDiscordState()
    {
        DiscordSignedIn = _discord.SignedIn;
        DiscordStatus = _discord.State switch
        {
            "ready" => "Signed in",
            "connecting" => "Connecting...",
            "signing-in" => "Signing in...",
            "problem" => "Not connected",
            _ => "Not signed in",
        };
        if (_discord.Message is { } m)
            DiscordMessage = m;
    }

    private void ShowSpotify()
    {
        var names = _spotify.ConnectedTo;
        SpotifyConnected = names.Count > 0;
        SpotifyStatus = names.Count > 0 ? "Connected to " + string.Join(", ", names) : "Not connected";
    }
}
