using AudioController.Core.Devices;

namespace AudioController.Audio.Interop;

/// <summary>Property keys read from audio endpoint property stores.</summary>
internal static class EndpointPropertyKeys
{
    private const string DevPropDeviceFmtId = "a45c254e-df1c-4efd-8020-67d146a850e0";

    /// <summary>PKEY_Device_FriendlyName, e.g. "Headphones (Crusher ANC 2)".</summary>
    public static readonly PropertyKeyNative FriendlyName = new(DevPropDeviceFmtId, 14);

    /// <summary>PKEY_Device_DeviceDesc, e.g. "Headphones".</summary>
    public static readonly PropertyKeyNative DeviceDescription = new(DevPropDeviceFmtId, 2);

    /// <summary>DEVPKEY_Device_EnumeratorName, e.g. "BTHENUM", "USB", "HDAUDIO".</summary>
    public static readonly PropertyKeyNative EnumeratorName = new(DevPropDeviceFmtId, 24);

    /// <summary>
    /// DEVPKEY_Device_ContainerId (VT_CLSID): the physical device the endpoint belongs to. A headset's headphones
    /// and microphone share one; built-in and root devices all report {00000000-0000-0000-FFFF-FFFFFFFFFFFF}.
    /// </summary>
    public static readonly PropertyKeyNative ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);

    /// <summary>PKEY_DeviceInterface_FriendlyName (the adapter), e.g. "Crusher ANC 2".</summary>
    public static readonly PropertyKeyNative InterfaceFriendlyName = new("026e516e-b814-414b-83cd-856d6fef4822", 2);

    /// <summary>
    /// The endpoint's parent device instance id as stored by the audio endpoint builder,
    /// e.g. "{1}.BTHENUM\{0000110B-...}_VID&amp;...".
    /// </summary>
    public static readonly PropertyKeyNative ParentInstanceId = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 2);

    /// <summary>PKEY_Device_InstanceId (usually absent on endpoints; checked as a further fallback).</summary>
    public static readonly PropertyKeyNative InstanceId = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);

    /// <summary>PKEY_AudioEndpoint_Disable_SysFx: 0 = enhancements enabled, 1 = disabled.</summary>
    public static readonly PropertyKeyNative DisableSysFx = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 5);

    /// <summary>FX-store keys whose presence means the endpoint has audio processing objects (enhancements).</summary>
    public static readonly PropertyKeyNative[] EffectKeys =
    [
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 1),  // PKEY_FX_PreMixEffectClsid (legacy LFX)
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 2),  // PKEY_FX_PostMixEffectClsid (legacy GFX)
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 5),  // PKEY_FX_StreamEffectClsid (SFX)
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 6),  // PKEY_FX_ModeEffectClsid (MFX)
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 7),  // PKEY_FX_EndpointEffectClsid (EFX)
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 13), // PKEY_CompositeFX_StreamEffectClsid
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 14), // PKEY_CompositeFX_ModeEffectClsid
        new("d04e05a6-594b-4fb6-a80d-01af5eed7d1d", 15), // PKEY_CompositeFX_EndpointEffectClsid
    ];

    public const uint SysFxEnabled = 0;
    public const uint SysFxDisabled = 1;
}

/// <summary>Best-effort classification and status reads for one endpoint. Nothing here throws.</summary>
internal static class EndpointInspector
{
    /// <summary>
    /// True when the endpoint lives on a Bluetooth enumerator. Uses DEVPKEY_Device_EnumeratorName first;
    /// when that key is absent, falls back to the instance-id properties and finally to "Bluetooth"
    /// appearing in the interface friendly name.
    /// </summary>
    public static bool IsBluetooth(EndpointPropertyStore? store, string? interfaceFriendlyName)
    {
        if (store is not null)
        {
            string? enumerator = store.GetString(EndpointPropertyKeys.EnumeratorName);
            if (enumerator is not null)
            {
                return BluetoothDetector.IsBluetooth(enumerator);
            }

            if (BluetoothDetector.IsBluetooth(StripEndpointPrefix(store.GetString(EndpointPropertyKeys.ParentInstanceId)))
                || BluetoothDetector.IsBluetooth(StripEndpointPrefix(store.GetString(EndpointPropertyKeys.InstanceId))))
            {
                return true;
            }
        }

        return interfaceFriendlyName is not null
            && interfaceFriendlyName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows "Audio Enhancements" status: true/false when known, null when it cannot be read or the
    /// endpoint has no enhancements at all.
    /// </summary>
    public static bool? ReadEnhancementsEnabled(string deviceId, EndpointPropertyStore? store, PolicyConfigClient? policy)
    {
        bool anyStoreReadable = false;

        if (policy is not null)
        {
            // The Sound control panel keeps the flag in the FX store; check it first, then the endpoint store.
            foreach (bool fxStore in new[] { true, false })
            {
                var value = policy.GetProperty(deviceId, fxStore, EndpointPropertyKeys.DisableSysFx, out bool ok);
                anyStoreReadable |= ok;
                if (value.Integer is { } flag)
                {
                    return flag == EndpointPropertyKeys.SysFxEnabled;
                }
            }
        }

        if (store is not null)
        {
            anyStoreReadable = true;
            if (store.Get(EndpointPropertyKeys.DisableSysFx).Integer is { } flag)
            {
                return flag == EndpointPropertyKeys.SysFxEnabled;
            }
        }

        if (!anyStoreReadable || policy is null)
        {
            return null;
        }

        // No flag anywhere: Windows treats that as "enhancements on", provided the endpoint has any.
        return HasAudioEffects(deviceId, policy) ? true : null;
    }

    private static bool HasAudioEffects(string deviceId, PolicyConfigClient policy)
    {
        foreach (var key in EndpointPropertyKeys.EffectKeys)
        {
            var value = policy.GetProperty(deviceId, fxStore: true, key, out bool ok);
            if (!ok || value.IsEmpty)
            {
                continue;
            }

            if (value.Text is { } text && Guid.TryParse(text, out var clsid) && clsid == Guid.Empty)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>Turns "{1}.BTHENUM\..." into "BTHENUM\..." so prefix-based classification works.</summary>
    private static string? StripEndpointPrefix(string? instanceId)
    {
        if (instanceId is null || instanceId.Length == 0 || instanceId[0] != '{')
        {
            return instanceId;
        }

        int marker = instanceId.IndexOf("}.", StringComparison.Ordinal);
        return marker > 0 && marker + 2 < instanceId.Length ? instanceId[(marker + 2)..] : instanceId;
    }
}
