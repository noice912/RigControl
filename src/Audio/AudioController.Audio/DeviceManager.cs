using System.Runtime.InteropServices;
using AudioController.Audio.Interop;
using AudioController.Core.Abstractions;
using AudioController.Core.Devices;
using AudioController.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioController.Audio;

/// <summary>
/// Lists and controls Windows audio endpoints (playback and recording) through the Core Audio
/// MMDevice API: default devices, master volume and mute, Bluetooth classification and the
/// Windows "Audio Enhancements" flag.
/// </summary>
/// <remarks>
/// Every call opens its own short-lived enumerator, so the manager can be used from any thread.
/// <see cref="DevicesChanged"/> is raised on a thread-pool thread.
/// </remarks>
public sealed class DeviceManager : IDeviceManager
{
    private const int ChangeDebounceMs = 250;

    private readonly object _sync = new();
    private readonly Timer _debounceTimer;
    private MMDeviceEnumerator? _notificationEnumerator;
    private NotificationClient? _notificationClient;
    private int _changePending;
    private volatile bool _disposed;

    /// <summary>
    /// Creates the manager and subscribes to Windows endpoint notifications. When notifications cannot
    /// be registered, the manager still works but <see cref="DevicesChanged"/> is never raised.
    /// </summary>
    public DeviceManager()
    {
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        // Register from a thread-pool (MTA) thread so the callback registration does not depend on the
        // caller's apartment or message pump.
        var registration = Task.Run(TryRegisterNotifications).GetAwaiter().GetResult();
        _notificationEnumerator = registration.Enumerator;
        _notificationClient = registration.Client;
    }

    /// <summary>
    /// Raised (debounced, on a thread-pool thread) when a device is added, removed, enabled, disabled,
    /// plugged, unplugged, renamed, or when a default device changes.
    /// </summary>
    public event EventHandler? DevicesChanged;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Windows could not enumerate audio endpoints.</exception>
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        ThrowIfDisposed();

        using var enumerator = CreateEnumerator();
        var defaults = new DefaultIds(
            TryGetDefaultId(enumerator, DataFlow.Render, Role.Multimedia),
            TryGetDefaultId(enumerator, DataFlow.Capture, Role.Multimedia),
            TryGetDefaultId(enumerator, DataFlow.Render, Role.Communications),
            TryGetDefaultId(enumerator, DataFlow.Capture, Role.Communications));

        MMDeviceCollection collection;
        try
        {
            collection = enumerator.EnumerateAudioEndPoints(
                DataFlow.All,
                DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"Windows could not list the audio devices ({DescribeHResult(ex.HResult)}).", ex);
        }

        using var policy = PolicyConfigClient.TryCreate();

        var result = new List<AudioDeviceInfo>();
        int count;
        try
        {
            count = collection.Count;
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"Windows could not list the audio devices ({DescribeHResult(ex.HResult)}).", ex);
        }

        for (int i = 0; i < count; i++)
        {
            MMDevice? device = null;
            try
            {
                device = collection[i];
                var info = Describe(device, policy, defaults);
                if (info is not null)
                {
                    result.Add(info);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                // The endpoint disappeared while it was being read; the next refresh will reflect that.
            }
            finally
            {
                DisposeQuietly(device);
            }
        }

        result.Sort(CompareForDisplay);
        return result.AsReadOnly();
    }

    /// <summary>
    /// Returns the first active device of <paramref name="kind"/> whose friendly name contains
    /// <paramref name="friendlyNameContains"/> (case-insensitive), or null.
    /// </summary>
    public AudioDeviceInfo? FindByName(string friendlyNameContains, DeviceKind kind)
    {
        ArgumentNullException.ThrowIfNull(friendlyNameContains);

        foreach (var device in GetDevices())
        {
            if (device.State == DeviceActivity.Active
                && device.Kind == kind
                && device.FriendlyName.Contains(friendlyNameContains, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="DeviceRole.Default"/> sets both the Console and Multimedia roles;
    /// <see cref="DeviceRole.Communications"/> sets the Communications role.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is missing or inactive, or Windows refused the change.</exception>
    public void SetDefaultDevice(string deviceId, DeviceRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();

        string name = WithActiveDevice(deviceId, device => SafeFriendlyName(device));

        var roles = role switch
        {
            DeviceRole.Default => new[] { ERole.Console, ERole.Multimedia },
            DeviceRole.Communications => new[] { ERole.Communications },
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown device role."),
        };

        using var policy = PolicyConfigClient.TryCreate()
            ?? throw new InvalidOperationException(
                "Windows did not provide the default-device service (IPolicyConfig) on this system, so the default device could not be changed.");

        foreach (var r in roles)
        {
            int hr = policy.SetDefaultEndpoint(deviceId, r);
            if (hr < 0)
            {
                string what = role == DeviceRole.Communications ? "default communications device" : "default device";
                throw new InvalidOperationException(
                    $"Windows refused to make \"{name}\" the {what} ({DescribeHResult(hr)}).",
                    Marshal.GetExceptionForHR(hr));
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="volumeScalar"/> is NaN.</exception>
    /// <exception cref="InvalidOperationException">The device is missing or inactive, or Windows refused the change.</exception>
    public void SetVolume(string deviceId, float volumeScalar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (float.IsNaN(volumeScalar))
        {
            throw new ArgumentOutOfRangeException(nameof(volumeScalar), "Volume must be a number between 0.0 and 1.0.");
        }

        ThrowIfDisposed();
        float value = Math.Clamp(volumeScalar, 0f, 1f);
        WithActiveDevice(deviceId, device =>
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = value;
            return true;
        });
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The device is missing or inactive, or Windows refused the change.</exception>
    public void SetMute(string deviceId, bool muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        WithActiveDevice(deviceId, device =>
        {
            device.AudioEndpointVolume.Mute = muted;
            return true;
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes PKEY_AudioEndpoint_Disable_SysFx through the audio service (IPolicyConfig, FX and endpoint
    /// stores), then falls back to opening the endpoint property store read-write (which normally needs
    /// administrator rights). Returns true only when re-reading the status afterwards shows the new value.
    /// Never throws.
    /// </remarks>
    public bool TrySetEnhancements(string deviceId, bool enabled)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            uint flag = enabled ? EndpointPropertyKeys.SysFxEnabled : EndpointPropertyKeys.SysFxDisabled;
            bool wrote = false;

            using (var policy = PolicyConfigClient.TryCreate())
            {
                if (policy is not null)
                {
                    wrote |= policy.TrySetUInt32(deviceId, fxStore: true, EndpointPropertyKeys.DisableSysFx, flag);
                    wrote |= policy.TrySetUInt32(deviceId, fxStore: false, EndpointPropertyKeys.DisableSysFx, flag);
                }
            }

            if (!wrote || ReadEnhancements(deviceId) != enabled)
            {
                // Fallback: the endpoint property store opened read-write (normally needs administrator rights).
                bool stored = WithAnyDevice(deviceId, device =>
                    EndpointPropertyStore.TryOpen(device, readWrite: true)?.TrySetUInt32(EndpointPropertyKeys.DisableSysFx, flag) == true);
                if (!stored)
                {
                    return false;
                }
            }

            return ReadEnhancements(deviceId) == enabled;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Unsubscribes from Windows notifications and releases native resources. Idempotent.</summary>
    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        NotificationClient? client;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            enumerator = _notificationEnumerator;
            client = _notificationClient;
            _notificationEnumerator = null;
            _notificationClient = null;
        }

        _debounceTimer.Dispose();

        if (enumerator is not null)
        {
            try
            {
                if (client is not null)
                {
                    enumerator.UnregisterEndpointNotificationCallback(client);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
            {
                // The audio service is already gone; there is nothing left to unregister from.
            }

            enumerator.Dispose();
        }
    }

    private static AudioDeviceInfo? Describe(MMDevice device, PolicyConfigClient? policy, DefaultIds defaults)
    {
        string id = device.ID;
        DataFlow flow = device.DataFlow;
        DeviceState state = device.State;
        if (flow is not (DataFlow.Render or DataFlow.Capture))
        {
            return null;
        }

        var kind = flow == DataFlow.Render ? DeviceKind.Playback : DeviceKind.Capture;

        var store = EndpointPropertyStore.TryOpen(device, readWrite: false);
        string? interfaceName = store?.GetString(EndpointPropertyKeys.InterfaceFriendlyName);
        string friendlyName = store?.GetString(EndpointPropertyKeys.FriendlyName)
            ?? store?.GetString(EndpointPropertyKeys.DeviceDescription)
            ?? SafeFriendlyName(device);

        float volume = 0f;
        bool muted = false;
        if (state == DeviceState.Active)
        {
            try
            {
                var endpointVolume = device.AudioEndpointVolume;
                volume = Math.Clamp(endpointVolume.MasterVolumeLevelScalar, 0f, 1f);
                muted = endpointVolume.Mute;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            {
                volume = 0f;
                muted = false;
            }
        }

        bool? enhancements;
        try
        {
            enhancements = EndpointInspector.ReadEnhancementsEnabled(id, store, policy);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            enhancements = null;
        }

        // Best effort: both read from the same (already indexed) store; null when absent or unreadable.
        string? enumeratorName = store?.GetString(EndpointPropertyKeys.EnumeratorName);
        string? containerId = store?.GetString(EndpointPropertyKeys.ContainerId);

        return new AudioDeviceInfo
        {
            Id = id,
            FriendlyName = friendlyName,
            Kind = kind,
            State = MapState(state),
            IsBluetooth = EndpointInspector.IsBluetooth(store, interfaceName),
            IsVirtual = VirtualDeviceDetector.IsVirtual(enumeratorName, friendlyName),
            ContainerId = containerId,
            IsDefault = string.Equals(id, kind == DeviceKind.Playback ? defaults.Render : defaults.Capture, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications = string.Equals(id, kind == DeviceKind.Playback ? defaults.RenderCommunications : defaults.CaptureCommunications, StringComparison.OrdinalIgnoreCase),
            Volume = volume,
            Muted = muted,
            EnhancementsEnabled = enhancements,
        };
    }

    private static bool? ReadEnhancements(string deviceId)
    {
        using var policy = PolicyConfigClient.TryCreate();
        return WithAnyDevice(deviceId, device =>
            EndpointInspector.ReadEnhancementsEnabled(deviceId, EndpointPropertyStore.TryOpen(device, readWrite: false), policy));
    }

    /// <inheritdoc />
    /// <remarks>Reads the shared-mode mix format (IAudioClient::GetMixFormat, no stream is opened); null on any failure.</remarks>
    public (int SampleRate, int Channels)? GetMixFormat(string deviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return WithAnyDevice<(int, int)?>(deviceId, device =>
        {
            try
            {
                if (device.State != DeviceState.Active)
                {
                    return null;
                }

                using var client = device.AudioClient;
                var mix = client.MixFormat;
                return mix.SampleRate > 0 && mix.Channels > 0 ? (mix.SampleRate, mix.Channels) : null;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or InvalidOperationException)
            {
                return null;
            }
        });
    }

    /// <summary>Runs <paramref name="read"/> on a device in any state; returns default when it cannot be opened.</summary>
    private static T? WithAnyDevice<T>(string deviceId, Func<MMDevice, T> read)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            return read(device);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or NotSupportedException)
        {
            return default;
        }
        finally
        {
            DisposeQuietly(device);
        }
    }

    private static DeviceActivity MapState(DeviceState state) => state switch
    {
        DeviceState.Active => DeviceActivity.Active,
        DeviceState.Disabled => DeviceActivity.Disabled,
        DeviceState.Unplugged => DeviceActivity.Unplugged,
        _ => DeviceActivity.NotPresent,
    };

    private static int CompareForDisplay(AudioDeviceInfo a, AudioDeviceInfo b)
    {
        int c = a.Kind.CompareTo(b.Kind);
        if (c != 0) return c;
        c = a.State.CompareTo(b.State);
        if (c != 0) return c;
        return string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        MMDevice? device = null;
        try
        {
            device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // E_NOTFOUND: there is no default device for this flow/role (e.g. no microphone at all).
            return null;
        }
        finally
        {
            DisposeQuietly(device);
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            string name = device.FriendlyName;
            return string.IsNullOrWhiteSpace(name) || name == "Unknown" ? device.ID : name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException)
        {
            return device.ID;
        }
    }

    private static MMDeviceEnumerator CreateEnumerator()
    {
        try
        {
            return new MMDeviceEnumerator();
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            throw new InvalidOperationException("Windows audio device services are not available.", ex);
        }
    }

    /// <summary>Opens an active device and runs <paramref name="action"/>, translating failures into readable errors.</summary>
    private static T WithActiveDevice<T>(string deviceId, Func<MMDevice, T> action)
    {
        using var enumerator = CreateEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new InvalidOperationException("That audio device no longer exists. It may have been unplugged or removed.", ex);
        }

        try
        {
            DeviceState state;
            try
            {
                state = device.State;
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException("That audio device is no longer available.", ex);
            }

            if (state != DeviceState.Active)
            {
                throw new InvalidOperationException(
                    $"\"{SafeFriendlyName(device)}\" is {DescribeState(state)}. Only active devices can be changed.");
            }

            try
            {
                return action(device);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Windows refused the change for \"{SafeFriendlyName(device)}\" ({DescribeHResult(ex.HResult)}).", ex);
            }
        }
        finally
        {
            DisposeQuietly(device);
        }
    }

    /// <summary>NAudio's MMDevice.Dispose can throw when the endpoint vanished; that must never mask a result.</summary>
    private static void DisposeQuietly(MMDevice? device)
    {
        if (device is null)
        {
            return;
        }

        try
        {
            device.Dispose();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
        {
            // The endpoint is already gone; nothing remains to release.
        }
    }

    internal static string DescribeState(DeviceState state) => state switch
    {
        DeviceState.Active => "active",
        DeviceState.Disabled => "disabled",
        DeviceState.Unplugged => "unplugged",
        DeviceState.NotPresent => "not present",
        _ => "unavailable",
    };

    internal static string DescribeHResult(int hr) => hr switch
    {
        HResults.EAccessDenied => "access denied",
        HResults.ENotFound => "device not found",
        HResults.AudclntDeviceInvalidated => "the device was disconnected or disabled",
        HResults.AudclntDeviceInUse => "the device is in use by another application in exclusive mode",
        HResults.AudclntServiceNotRunning => "the Windows Audio service is not running",
        HResults.AudclntResourcesInvalidated => "the device was reconfigured",
        _ => $"HRESULT 0x{hr:X8}",
    };

    private (MMDeviceEnumerator? Enumerator, NotificationClient? Client) TryRegisterNotifications()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            var client = new NotificationClient(this);
            int hr = enumerator.RegisterEndpointNotificationCallback(client);
            if (hr < 0)
            {
                enumerator.Dispose();
                return (null, null);
            }

            return (enumerator, client);
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            enumerator?.Dispose();
            return (null, null);
        }
    }

    private void QueueDevicesChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _changePending, 1) == 0)
        {
            try
            {
                _debounceTimer.Change(ChangeDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently; no further notifications are wanted.
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        Interlocked.Exchange(ref _changePending, 0);
        if (_disposed)
        {
            return;
        }

        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber failure must not take down the timer thread.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct DefaultIds(string? Render, string? Capture, string? RenderCommunications, string? CaptureCommunications);

    /// <summary>Receives Core Audio endpoint notifications on audio-service threads; only queues a refresh.</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private static readonly Guid DevicePropertiesFmtId = new("a45c254e-df1c-4efd-8020-67d146a850e0");
        private static readonly Guid InterfaceNameFmtId = new("026e516e-b814-414b-83cd-856d6fef4822");
        private static readonly Guid SysFxFmtId = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

        private readonly WeakReference<DeviceManager> _owner;

        public NotificationClient(DeviceManager owner) => _owner = new WeakReference<DeviceManager>(owner);

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Queue();

        public void OnDeviceAdded(string pwstrDeviceId) => Queue();

        public void OnDeviceRemoved(string deviceId) => Queue();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Queue();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Many drivers fire property changes constantly; only names and the enhancements flag matter here.
            bool relevant = (key.formatId == DevicePropertiesFmtId && (key.propertyId == 14 || key.propertyId == 2))
                || (key.formatId == InterfaceNameFmtId && key.propertyId == 2)
                || (key.formatId == SysFxFmtId && key.propertyId == 5);
            if (relevant)
            {
                Queue();
            }
        }

        private void Queue()
        {
            try
            {
                if (_owner.TryGetTarget(out var owner))
                {
                    owner.QueueDevicesChanged();
                }
            }
            catch (Exception)
            {
                // Never let an exception escape into the audio service's callback thread.
            }
        }
    }
}
