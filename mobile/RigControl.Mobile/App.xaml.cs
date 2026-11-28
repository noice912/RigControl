using RigControl.Mobile.Services;

namespace RigControl.Mobile;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly PhoneMusicService _music;
    private readonly DiscordDirect _discord;

    public App(AppShell shell, PhoneMusicService music, DiscordDirect discord)
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        _shell = shell;
        _music = music;
        _discord = discord;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell) { Title = "RigControl" };
        // The Music app song is shared only while the app is on screen.
        // Signed in with Discord on the phone: connected while on screen, status cleared in the background.
        window.Created += (_, _) => { _ = _discord.ResumeAsync(); _ = _music.ResumeAsync(); };
        window.Resumed += (_, _) => { _ = _discord.ResumeAsync(); _ = _music.ResumeAsync(); };
        window.Stopped += (_, _) => { _ = _music.PauseAsync(); _ = _discord.PauseAsync(); };
        return window;
    }
}
