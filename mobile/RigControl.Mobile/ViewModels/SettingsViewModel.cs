using CommunityToolkit.Mvvm.ComponentModel;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Services;

namespace RigControl.Mobile.ViewModels;

/// <summary>Settings: this phone's name, the relay, the Discord status section (Step 3) and about.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty] public partial string PhoneName { get; set; }
    [ObservableProperty] public partial string Relay { get; set; }

    public string Version { get; } = $"{AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";

    /// <summary>Says plainly when the app shows sample PCs instead of real ones.</summary>
    public string LinkMode { get; }

    public SettingsViewModel(AppSettings settings, ILinkService link)
    {
        _settings = settings;
        PhoneName = settings.PhoneName;
        Relay = settings.Relay.Length > 0 ? settings.Relay
            : MobileDefaults.HasRelay ? MobileDefaults.RelayHost : "Set by the first PC you link";
        LinkMode = link is FakeLinkService
            ? "Sample PCs only: this build was made with the stand-in link and can't reach a real PC."
            : MobileDefaults.HasRelay
                ? "Linked through the RigControl relay, end-to-end encrypted."
                : "Linked through the relay your PC uses, end-to-end encrypted. This build has no relay address built in, so link each PC by scanning its QR code, which carries the address.";
    }

    partial void OnPhoneNameChanged(string value) => _settings.PhoneName = value;
}
