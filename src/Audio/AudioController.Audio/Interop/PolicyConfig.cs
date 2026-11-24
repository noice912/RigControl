using System.Runtime.InteropServices;

namespace AudioController.Audio.Interop;

/// <summary>Core Audio ERole.</summary>
internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>
/// The undocumented-but-standard IPolicyConfig interface (Windows 7 and later) used by the Sound
/// control panel to change default endpoints and endpoint/FX properties through the audio service.
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, out IntPtr format);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, ref PropertyKeyNative key, out PropVariantNative value);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, ref PropertyKeyNative key, ref PropVariantNative value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

/// <summary>Disposable wrapper over one IPolicyConfig instance. Methods never throw for COM failures.</summary>
internal sealed class PolicyConfigClient : IDisposable
{
    /// <summary>CLSID of CPolicyConfigClient.</summary>
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    /// <summary>IID of IPolicyConfig.</summary>
    private static readonly Guid IidPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    private IPolicyConfig? _policy;

    private PolicyConfigClient(IPolicyConfig policy) => _policy = policy;

    /// <summary>Creates the client, or returns null when the interface is unavailable on this Windows build.</summary>
    public static PolicyConfigClient? TryCreate()
    {
        var instance = ComHelpers.CreateUnique(ClsidPolicyConfigClient, IidPolicyConfig);
        try
        {
            if (instance is IPolicyConfig policy)
            {
                return new PolicyConfigClient(policy);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // QueryInterface failed: this Windows build does not expose the interface.
        }

        ComHelpers.Release(instance);
        return null;
    }

    /// <summary>Sets the default endpoint for one role. Returns the HRESULT.</summary>
    public int SetDefaultEndpoint(string deviceId, ERole role)
    {
        var policy = _policy;
        if (policy is null)
        {
            return HResults.EPointer;
        }

        try
        {
            return policy.SetDefaultEndpoint(deviceId, role);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return ex.HResult != 0 ? ex.HResult : HResults.EFail;
        }
    }

    /// <summary>Reads a property from the endpoint store (<paramref name="fxStore"/> false) or the FX store.</summary>
    /// <param name="succeeded">True when the audio service answered the read (even with an empty value).</param>
    public PropValue GetProperty(string deviceId, bool fxStore, PropertyKeyNative key, out bool succeeded)
    {
        succeeded = false;
        var policy = _policy;
        if (policy is null)
        {
            return PropValue.Empty;
        }

        try
        {
            var k = key;
            if (policy.GetPropertyValue(deviceId, fxStore ? 1 : 0, ref k, out var variant) < 0)
            {
                return PropValue.Empty;
            }

            succeeded = true;
            return PropValue.FromNativeAndClear(ref variant);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return PropValue.Empty;
        }
    }

    /// <summary>Writes a VT_UI4 property through the audio service. Returns true when the call succeeded.</summary>
    public bool TrySetUInt32(string deviceId, bool fxStore, PropertyKeyNative key, uint value)
    {
        var policy = _policy;
        if (policy is null)
        {
            return false;
        }

        try
        {
            var k = key;
            var variant = PropVariantNative.FromUInt32(value);
            return policy.SetPropertyValue(deviceId, fxStore ? 1 : 0, ref k, ref variant) >= 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        ComHelpers.Release(_policy);
        _policy = null;
    }
}

internal static class HResults
{
    public const int EFail = unchecked((int)0x80004005);
    public const int EPointer = unchecked((int)0x80004003);
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int ENotFound = unchecked((int)0x80070490);
    public const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntServiceNotRunning = unchecked((int)0x88890010);
    public const int AudclntResourcesInvalidated = unchecked((int)0x88890026);
}
