namespace RigControl.iPhone.Device;

/// <summary>The plain messages shown to the person, and the mapping from Apple's error names to them.</summary>
public static class DeviceErrors
{
    public const string ServiceMissing =
        "RigControl can't reach Apple's iPhone service. Install \"Apple Devices\" from the Microsoft Store (or iTunes), " +
        "open it once, then try again.";

    public const string NotConnected =
        "The iPhone isn't connected. Plug it in with a USB cable, unlock it, then try again.";

    public const string NotTrusted =
        "This PC isn't trusted by the iPhone yet. Unlock your iPhone and tap Trust, then try again.";

    public const string TrustPending =
        "The iPhone is asking whether to trust this PC. Unlock it and tap Trust, then try again.";

    public const string TrustDenied =
        "The iPhone was told not to trust this PC. Unplug it, plug it back in, unlock it and tap Trust.";

    public const string Locked = "The iPhone is locked. Unlock it, then try again.";

    public const string NoAnswer =
        "The iPhone stopped answering. Unplug it, plug it back in, unlock it, then try again.";

    public const string ConnectionLost =
        "The connection to the iPhone was lost. Check the cable, unlock the iPhone, then try again.";

    public const string BadReply = "The iPhone sent a reply RigControl can't read. Try again.";

    public const string SecureConnectionFailed =
        "RigControl couldn't open a secure connection to the iPhone. Unplug it, plug it back in, unlock it and try " +
        "again. If it keeps failing, reset trust on the iPhone (Settings > General > Transfer or Reset iPhone > Reset > " +
        "Reset Location & Privacy) and tap Trust again.";

    public const string DeveloperModeOff =
        "Developer Mode is off on the iPhone. Turn it on in Settings > Privacy & Security > Developer Mode, let the " +
        "iPhone restart, confirm \"Turn On\", then try again.";

    public const string OutOfSpace = "The iPhone is out of storage space. Free some space, then try again.";

    /// <summary>Lockdown's "Error" values (StartSession, GetValue, StartService ...).</summary>
    public static DeviceException FromLockdown(string request, string error) => error switch
    {
        "InvalidHostID" or "InvalidPairRecord" or "PairingProhibitedOverThisConnection" or "NotPaired"
            => new DeviceException(NotTrusted),
        "PasswordProtected" => new DeviceException(Locked),
        "PairingDialogResponsePending" => new DeviceException(TrustPending),
        "UserDeniedPairing" => new DeviceException(TrustDenied),
        _ => new DeviceException($"The iPhone refused \"{request}\" ({Clip(error, 80)})."),
    };

    /// <summary>usbmuxd's "Result" numbers.</summary>
    public static DeviceException FromUsbMux(string request, long number) => number switch
    {
        2 => new DeviceException(NotConnected),
        3 => new DeviceException($"The iPhone refused the connection ({request})."),
        _ => new DeviceException($"Apple's iPhone service refused \"{request}\" (error {number})."),
    };

    /// <summary>installd's errors, as installation_proxy reports them.</summary>
    public static DeviceException FromInstall(string error, string? description, long? detail)
    {
        var d = description ?? "";
        bool Has(string text) => d.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (error.Contains("DeveloperMode", StringComparison.OrdinalIgnoreCase) || Has("Developer Mode"))
            return new DeviceException(DeveloperModeOff);

        var message = error switch
        {
            "ApplicationVerificationFailed" when Has("maximum number of installed apps") || Has("0xe8008021") =>
                "This iPhone already has the most apps a free Apple ID can sign (3). Delete one of them from the iPhone, " +
                "then try again.",
            "ApplicationVerificationFailed" when Has("0xe8008015") || Has("provisioning profile") =>
                "The app's signing doesn't cover this iPhone (no valid provisioning profile). Sign it again for this " +
                "iPhone, then try again.",
            "ApplicationVerificationFailed" when Has("0xe8008018") || Has("no longer valid") =>
                "The certificate the app was signed with is no longer valid (revoked or expired). Sign it again, " +
                "then try again.",
            "ApplicationVerificationFailed" when Has("0xe800801c") || Has("No code signature") =>
                "The app isn't signed. Sign it, then try again.",
            "ApplicationVerificationFailed" when Has("0xe8008016") || Has("entitlements") =>
                "The app asks for permissions its provisioning profile doesn't allow. Sign it again, then try again.",
            "ApplicationVerificationFailed" =>
                "The iPhone rejected the app's signature, so the app wasn't installed.",
            "MismatchedApplicationIdentifierEntitlement" =>
                "A copy of this app signed by someone else is already on the iPhone. Delete it from the iPhone, then " +
                "try again.",
            "DeviceOSVersionTooLow" => "This app needs a newer iOS version than the iPhone has. Update the iPhone first.",
            "IncorrectArchitecture" => "This app wasn't built for this iPhone.",
            "PackageInspectionFailed" or "PackageExtractionFailed" or "PackagePatchFailed" =>
                "The app file is damaged or isn't an iPhone app.",
            "InstallProhibited" =>
                "The iPhone doesn't allow installing apps right now (Screen Time or a management profile may block it).",
            "InsufficientStorage" or "NoSpace" => OutOfSpace,
            _ => $"The iPhone couldn't install the app ({Clip(error, 80)}).",
        };
        if (d.Length > 0)
            message += " Apple's message: " + Clip(d, 300);
        return new DeviceException(message);
    }

    internal static string Clip(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "...";
    }
}
