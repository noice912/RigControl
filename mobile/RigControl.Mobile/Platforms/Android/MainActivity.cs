using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using RigControl.Mobile.Services;

namespace RigControl.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// rigcontrol://pair?... from a PC's QR code (opened by the phone's own camera app) and rigcontrol://callback
// (the end of a browser sign-in, Step 3). Only these two hosts open the app.
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "rigcontrol", DataHost = "pair")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "rigcontrol", DataHost = "callback")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState is null)
            Forward(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Forward(intent);
    }

    private static void Forward(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView)
            DeepLinkEntry.Open(intent.DataString);
    }
}
