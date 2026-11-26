namespace RigControl.Mobile.Services;

/// <summary>
/// Small settings kept on the phone (Preferences). Keys and secrets never go here: the real link keeps its keys in
/// the Keychain / Keystore (SecureStorage).
/// </summary>
public sealed class AppSettings
{
    private const string PhoneNameKey = "phone.name";
    private const string RelayKey = "link.relay";

    /// <summary>The name the PC shows for this phone in its list of linked phones.</summary>
    public string PhoneName
    {
        get => Preferences.Default.Get(PhoneNameKey, DefaultPhoneName());
        set => Preferences.Default.Set(PhoneNameKey, string.IsNullOrWhiteSpace(value) ? DefaultPhoneName() : value.Trim());
    }

    /// <summary>
    /// The relay address used last (from a QR code or typed in). Empty until the first link: there is no built-in
    /// default yet, the relay's address arrives with the pairing QR code.
    /// </summary>
    public string Relay
    {
        get => Preferences.Default.Get(RelayKey, "");
        set => Preferences.Default.Set(RelayKey, value ?? "");
    }

    private static string DefaultPhoneName() =>
        DeviceInfo.Platform == DevicePlatform.iOS ? "iPhone" : DeviceInfo.Platform == DevicePlatform.Android ? "Android phone" : "Phone";
}
