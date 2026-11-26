using System.Runtime.CompilerServices;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Device;

public enum DeviceEventKind { Connected, Disconnected }

/// <summary>An iPhone was plugged in or out (or appeared/disappeared on the network).</summary>
public sealed record DeviceEvent(DeviceEventKind Kind, string Udid, bool Usb);

/// <summary>
/// Everything the installer needs from a connected iPhone: list devices, install an .ipa (copy to PublicStaging over
/// AFC, then installation_proxy), watch plug/unplug, and read whether Developer Mode is on.
/// </summary>
public sealed class DeviceService(UsbMuxClient? mux = null, TlsWrapper? tls = null)
{
    private const string StagingFolder = "PublicStaging";

    private readonly UsbMuxClient _mux = mux ?? new UsbMuxClient();
    private readonly TlsWrapper _tls = tls ?? DeviceTls.WrapAsync;

    /// <summary>Longest time spent asking one device for its name and model while listing.</summary>
    public TimeSpan DescribeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-message wait for installd (it can go quiet while it verifies a big app).</summary>
    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Connected iPhones, one entry each (USB preferred over the same iPhone on Wi-Fi).</summary>
    public async Task<IReadOnlyList<DeviceInfo>> ListAsync(CancellationToken ct = default)
    {
        var devices = Distinct(await _mux.ListDevicesAsync(ct).ConfigureAwait(false));
        var infos = await Task.WhenAll(devices.Select(d => DescribeAsync(d, ct))).ConfigureAwait(false);
        return infos;
    }

    /// <summary>Copies the .ipa to the iPhone, installs it, and removes the copy. Progress: copy 0-50, install 50-100.</summary>
    public async Task InstallAsync(string udid, string ipaPath, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(udid);
        var device = await FindAsync(udid, ct).ConfigureAwait(false);
        var pair = await _mux.ReadPairRecordAsync(device.Udid, ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("Connecting", 0));

        await using var file = new FileStream(ipaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var total = Math.Max(1, file.Length);
        var remote = $"{StagingFolder}/{StagingName(ipaPath)}";

        await using var afc = new AfcClient(await OpenServiceAsync(device, pair, AfcClient.ServiceName, ct).ConfigureAwait(false));
        try
        {
            try
            {
                await afc.MakeDirectoryAsync(StagingFolder, ct).ConfigureAwait(false);
            }
            catch (DeviceException e) when (AfcError.Of(e) == AfcClient.ObjectExists)
            {
            }
            await afc.UploadAsync(file, remote, new SyncProgress<long>(sent =>
                progress?.Report(new InstallProgress("Copying to iPhone", (int)(sent * 50 / total)))), ct).ConfigureAwait(false);

            await using var proxy = new InstallationProxyClient(
                await OpenServiceAsync(device, pair, InstallationProxyClient.ServiceName, ct).ConfigureAwait(false), InstallTimeout);
            await proxy.InstallAsync(remote, null, new SyncProgress<InstallStatus>(s =>
                progress?.Report(new InstallProgress(InstallStage(s.Status), 50 + s.Percent / 2))), ct).ConfigureAwait(false);
        }
        finally
        {
            // installd usually removes the staged copy itself; this catches a failed or cancelled install.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await afc.RemovePathAsync(remote, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DeviceException or OperationCanceledException)
            {
            }
        }
        progress?.Report(new InstallProgress("Done", 100));
    }

    /// <summary>The installed version (CFBundleShortVersionString, else CFBundleVersion) of an app, or null.</summary>
    public async Task<string?> InstalledVersionAsync(string udid, string bundleId, CancellationToken ct = default)
    {
        var device = await FindAsync(udid, ct).ConfigureAwait(false);
        var pair = await _mux.ReadPairRecordAsync(device.Udid, ct).ConfigureAwait(false);
        await using var proxy = new InstallationProxyClient(
            await OpenServiceAsync(device, pair, InstallationProxyClient.ServiceName, ct).ConfigureAwait(false), TimeSpan.FromSeconds(30));
        var app = await proxy.LookupAsync(bundleId, null, ct).ConfigureAwait(false);
        return app?.String("CFBundleShortVersionString") ?? app?.String("CFBundleVersion");
    }

    /// <summary>True/false on iOS 16 and later; null when the iPhone doesn't report it (older iOS).</summary>
    public async Task<bool?> DeveloperModeEnabledAsync(string udid, CancellationToken ct = default)
    {
        var device = await FindAsync(udid, ct).ConfigureAwait(false);
        var pair = await _mux.ReadPairRecordAsync(device.Udid, ct).ConfigureAwait(false);
        await using var lockdown = await LockdownClient.ConnectAsync(_mux, device.DeviceId, _tls, null, ct).ConfigureAwait(false);
        await lockdown.StartSessionAsync(pair, ct).ConfigureAwait(false);
        try
        {
            return await lockdown.GetValueAsync("com.apple.security.mac.amfi", "DeveloperModeStatus", ct).ConfigureAwait(false) as bool?;
        }
        catch (DeviceException e) when (LockdownError.Of(e) is not null)
        {
            return null;
        }
    }

    /// <summary>Plug and unplug events until cancelled. iPhones already connected come first as Connected.</summary>
    public async IAsyncEnumerable<DeviceEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var known = new Dictionary<int, UsbMuxDevice>();
        await foreach (var e in _mux.ListenAsync(ct).ConfigureAwait(false))
        {
            if (e.Kind == UsbMuxEventKind.Attached && e.Device is { } d)
            {
                known[d.DeviceId] = d;
                yield return new DeviceEvent(DeviceEventKind.Connected, d.Udid, d.Usb);
            }
            else if (e.Kind == UsbMuxEventKind.Detached && known.Remove(e.DeviceId, out var gone))
            {
                yield return new DeviceEvent(DeviceEventKind.Disconnected, gone.Udid, gone.Usb);
            }
        }
    }

    private async Task<DeviceInfo> DescribeAsync(UsbMuxDevice device, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DescribeTimeout);
        PlistDict? values = null;
        try
        {
            PairRecord? pair = null;
            try
            {
                pair = await _mux.ReadPairRecordAsync(device.Udid, cts.Token).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // Not trusted yet: the values lockdown gives without a session still name the model.
            }
            if (pair is not null)
                values = await ReadValuesAsync(device, pair, cts.Token).ConfigureAwait(false);
            values ??= await ReadValuesAsync(device, null, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DeviceException or OperationCanceledException && !ct.IsCancellationRequested)
        {
        }
        return new DeviceInfo(
            device.Udid,
            values?.String("DeviceName") ?? "iPhone",
            values?.String("ProductType") ?? "",
            values?.String("ProductVersion") ?? "",
            device.Usb);
    }

    /// <summary>All of lockdown's top-level values, in a session when a pair record is given. Null on refusal.</summary>
    private async Task<PlistDict?> ReadValuesAsync(UsbMuxDevice device, PairRecord? pair, CancellationToken ct)
    {
        try
        {
            await using var lockdown = await LockdownClient.ConnectAsync(_mux, device.DeviceId, _tls, null, ct).ConfigureAwait(false);
            if (pair is not null)
                await lockdown.StartSessionAsync(pair, ct).ConfigureAwait(false);
            return await lockdown.GetValueAsync(null, null, ct).ConfigureAwait(false) as PlistDict;
        }
        catch (DeviceException) when (pair is not null)
        {
            return null;
        }
    }

    /// <summary>A lockdown session, a started service, and a pipe to it (TLS when the service wants it).</summary>
    private async Task<Stream> OpenServiceAsync(UsbMuxDevice device, PairRecord pair, string name, CancellationToken ct)
    {
        LockdownService service;
        await using (var lockdown = await LockdownClient.ConnectAsync(_mux, device.DeviceId, _tls, null, ct).ConfigureAwait(false))
        {
            await lockdown.StartSessionAsync(pair, ct).ConfigureAwait(false);
            service = await lockdown.StartServiceAsync(name, ct).ConfigureAwait(false);
        }
        var stream = await _mux.ConnectAsync(device.DeviceId, service.Port, ct).ConfigureAwait(false);
        if (!service.Ssl)
            return stream;
        try
        {
            return await _tls(stream, pair, ct).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<UsbMuxDevice> FindAsync(string udid, CancellationToken ct)
    {
        var devices = Distinct(await _mux.ListDevicesAsync(ct).ConfigureAwait(false));
        return devices.FirstOrDefault(d => string.Equals(d.Udid, udid, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeviceException(DeviceErrors.NotConnected);
    }

    private static List<UsbMuxDevice> Distinct(IEnumerable<UsbMuxDevice> devices) =>
        devices.GroupBy(d => d.Udid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.Usb).First())
            .ToList();

    /// <summary>A safe file name for the staged copy: letters, digits, '-', '_' and '.', always ending in .ipa.</summary>
    internal static string StagingName(string ipaPath)
    {
        var name = Path.GetFileNameWithoutExtension(ipaPath);
        var clean = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        if (clean.Length == 0)
            clean = "app";
        if (clean.Length > 64)
            clean = clean[..64];
        return clean + ".ipa";
    }

    private static string InstallStage(string status) => status switch
    {
        "Complete" => "Installed",
        "" => "Installing",
        _ => "Installing (" + DeviceErrors.Clip(status, 60) + ")",
    };

    /// <summary>Reports on the calling thread (unlike Progress&lt;T&gt;, which posts and can arrive out of order).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
