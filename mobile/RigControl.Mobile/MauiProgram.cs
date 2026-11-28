using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Services;
using RigControl.Mobile.ViewModels;
using RigControl.Mobile.Views;
using ZXing.Net.Maui.Controls;

namespace RigControl.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if IOS
        // Before anything is stored: Keychain items stay on this iPhone (not in backups or transfers).
        MauiLinkStore.KeepKeysOnThisDevice();
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureLifecycleEvents(events =>
            {
#if IOS
                // rigcontrol:// links on the iPhone: a running app gets them through its scene, a cold start in the
                // scene's connection options. (Android: MainActivity.)
                events.AddiOS(ios => ios
                    .SceneWillConnect((_, _, options) =>
                    {
                        // A normal launch (not from a link) has no URL contexts: iOS passes null, not an empty set.
                        if (options?.UrlContexts is not { } contexts)
                            return;
                        foreach (var context in contexts)
                            if (context?.Url?.AbsoluteString is { } url)
                                DeepLinkEntry.Open(url);
                    })
                    .SceneOpenUrl((_, contexts) =>
                    {
                        if (contexts is null)
                            return false;
                        foreach (var context in contexts)
                            if (context?.Url?.AbsoluteString is { } url)
                                DeepLinkEntry.Open(url);
                        return true;
                    })
                    .OpenUrl((_, url, _) =>
                    {
                        if (url?.AbsoluteString is not { } link)
                            return false;
                        DeepLinkEntry.Open(link);
                        return true;
                    }));
#endif
            });

        // The link to the PCs: the real one through the relay, or (built with -p:UseFakeLink=true) the stand-in with
        // two sample PCs. The screens only see ILinkService.
#if FAKE_LINK
        builder.Services.AddSingleton<ILinkService>(_ => new FakeLinkService());
#else
        builder.Services.AddSingleton<ILinkStore, MauiLinkStore>();
        builder.Services.AddSingleton<ILinkService>(sp => new RelayLinkService(
            new global::RigControl.Link.WebSocketRelayTransport(),
            sp.GetRequiredService<ILinkStore>(),
            new RelayLinkServiceOptions { Log = m => System.Diagnostics.Debug.WriteLine("[link] " + m) }));
#endif

        builder.Services.AddSingleton<AppSettings>();
        builder.Services.AddSingleton<DeepLinkRouter>();
        builder.Services.AddSingleton<SpotifyConnection>();
        builder.Services.AddSingleton<DiscordDirect>();
        builder.Services.AddSingleton<PhoneMusicService>();
        builder.Services.AddSingleton<AppShell>();

        builder.Services.AddTransient<PcsViewModel>();
        builder.Services.AddTransient<PcsPage>();
        builder.Services.AddTransient<PcDetailViewModel>();
        builder.Services.AddTransient<PcDetailPage>();
        builder.Services.AddTransient<LinkPcViewModel>();
        builder.Services.AddTransient<LinkPcPage>();
        builder.Services.AddTransient<ScanPage>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<SettingsPage>();

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        return builder.Build();
    }
}
