using RigControl.Mobile.Services;

namespace RigControl.Mobile;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly PhoneMusicService _music;

    public App(AppShell shell, PhoneMusicService music)
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        _shell = shell;
        _music = music;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell) { Title = "RigControl" };
        // The Music app song is shared only while the app is on screen.
        window.Created += (_, _) => _ = _music.ResumeAsync();
        window.Resumed += (_, _) => _ = _music.ResumeAsync();
        window.Stopped += (_, _) => _ = _music.PauseAsync();
        return window;
    }
}
