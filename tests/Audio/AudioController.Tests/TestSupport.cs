using System.Globalization;
using System.Security.Cryptography;

namespace AudioController.Tests;

/// <summary>
/// A unique, empty directory under the system temp folder, deleted recursively on dispose.
/// Every test that touches disk gets its own instance, so tests stay isolated and parallel-safe.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "AudioController.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a file still held open by a crashed child process must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

/// <summary>Temporarily replaces the current thread's culture; restores the previous one on dispose.</summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;

    public CultureScope(CultureInfo culture)
    {
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static CultureScope Invariant() => new(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}

internal static class TestBytes
{
    public static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Deterministic pseudo-random bytes (seeded) so failures are reproducible.</summary>
    public static byte[] Deterministic(int length, int seed = 12345)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>True when <paramref name="needle"/> occurs anywhere in <paramref name="haystack"/>.</summary>
    public static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>
    /// Walks up from the test binaries to the directory holding AudioController.sln (the repo root),
    /// or returns null when the tests are not running from inside the source tree.
    /// </summary>
    public static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "AudioController.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}

/// <summary>
/// A device list behind <see cref="AudioController.Core.Abstractions.IDeviceManager"/> (null = enumeration fails like
/// Windows would). <see cref="SetDefaultDevice"/> records every call and moves the IsDefault / IsDefaultCommunications
/// flags the way Windows does; <see cref="SetDefaultByOthers"/> does the same for the user or another program
/// (not recorded); <see cref="FailSetDefault"/> makes it throw like a refusing IPolicyConfig. Thread-safe.
/// </summary>
internal sealed class ListDeviceManager(IReadOnlyList<AudioController.Core.Models.AudioDeviceInfo>? devices)
    : AudioController.Core.Abstractions.IDeviceManager
{
    private readonly object _gate = new();
    private List<AudioController.Core.Models.AudioDeviceInfo>? _devices = devices?.ToList();

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    /// <summary>Every <see cref="SetDefaultDevice"/> call that succeeded, in order.</summary>
    public List<(string Id, AudioController.Core.Abstractions.DeviceRole Role)> DefaultChanges { get; } = new();

    /// <summary>Non-null: <see cref="SetDefaultDevice"/> throws with this message.</summary>
    public string? FailSetDefault { get; set; }

    public IReadOnlyList<AudioController.Core.Models.AudioDeviceInfo> GetDevices()
    {
        lock (_gate)
        {
            return _devices?.ToList() ?? throw new InvalidOperationException("Windows could not list the audio devices.");
        }
    }

    /// <summary>The current default playback device's id (or communications device's id), or null.</summary>
    public string? DefaultId(bool communications = false)
    {
        lock (_gate)
        {
            return _devices?.FirstOrDefault(d => d.Kind == AudioController.Core.Models.DeviceKind.Playback
                                                 && (communications ? d.IsDefaultCommunications : d.IsDefault))?.Id;
        }
    }

    public void SetDefaultDevice(string deviceId, AudioController.Core.Abstractions.DeviceRole role)
    {
        lock (_gate)
        {
            if (FailSetDefault is { } message)
            {
                throw new InvalidOperationException(message);
            }

            DefaultChanges.Add((deviceId, role));
            Move(deviceId, role);
        }
    }

    /// <summary>The user or another program changes the default (not recorded in <see cref="DefaultChanges"/>).</summary>
    public void SetDefaultByOthers(string deviceId, AudioController.Core.Abstractions.DeviceRole role = AudioController.Core.Abstractions.DeviceRole.Default)
    {
        lock (_gate)
        {
            Move(deviceId, role);
        }
    }

    /// <summary>Replaces one device (e.g. the headset goes Unplugged), keeping the default flags as given.</summary>
    public void Replace(AudioController.Core.Models.AudioDeviceInfo device)
    {
        lock (_gate)
        {
            if (_devices is null) return;
            int i = _devices.FindIndex(d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _devices[i] = device;
            else _devices.Add(device);
        }
    }

    public void SetVolume(string deviceId, float volumeScalar) => throw new NotSupportedException();

    public void SetMute(string deviceId, bool muted) => throw new NotSupportedException();

    public bool TrySetEnhancements(string deviceId, bool enabled) => false;

    public void Dispose()
    {
    }

    private void Move(string deviceId, AudioController.Core.Abstractions.DeviceRole role)
    {
        if (_devices is null) return;
        bool comms = role == AudioController.Core.Abstractions.DeviceRole.Communications;
        _devices = _devices.Select(d =>
        {
            if (d.Kind != AudioController.Core.Models.DeviceKind.Playback) return d;
            bool hit = string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase);
            return comms ? With(d, d.IsDefault, hit) : With(d, hit, d.IsDefaultCommunications);
        }).ToList();
    }

    /// <summary>A copy of <paramref name="d"/> with the given default flags (and optionally another state).</summary>
    public static AudioController.Core.Models.AudioDeviceInfo With(
        AudioController.Core.Models.AudioDeviceInfo d, bool isDefault, bool isDefaultCommunications,
        AudioController.Core.Models.DeviceActivity? state = null) => new()
    {
        Id = d.Id,
        FriendlyName = d.FriendlyName,
        Kind = d.Kind,
        State = state ?? d.State,
        IsBluetooth = d.IsBluetooth,
        IsVirtual = d.IsVirtual,
        ContainerId = d.ContainerId,
        IsDefault = isDefault,
        IsDefaultCommunications = isDefaultCommunications,
        Volume = d.Volume,
        Muted = d.Muted,
        EnhancementsEnabled = d.EnhancementsEnabled,
    };
}
