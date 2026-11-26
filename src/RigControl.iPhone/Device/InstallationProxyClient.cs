using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Device;

/// <summary>One status message from installd: what it is doing and how far along it is (0-100).</summary>
public sealed record InstallStatus(string Status, int Percent);

/// <summary>
/// com.apple.mobile.installation_proxy: installs a package already copied to the iPhone (AFC, PublicStaging) and
/// looks up installed apps. Same framing as lockdown.
/// </summary>
public sealed class InstallationProxyClient : IAsyncDisposable, IDisposable
{
    public const string ServiceName = "com.apple.mobile.installation_proxy";

    private static readonly string[] DefaultAttributes =
        ["CFBundleIdentifier", "CFBundleShortVersionString", "CFBundleVersion", "CFBundleDisplayName", "CFBundleName"];

    private readonly Stream _stream;

    public InstallationProxyClient(Stream stream, TimeSpan? timeout = null)
    {
        _stream = stream;
        Timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Longest wait for each message. installd can go quiet for a while verifying a big app.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Installs <paramref name="packagePath"/> (relative to the AFC root) and waits for "Complete".</summary>
    public async Task InstallAsync(string packagePath, PlistDict? options = null, IProgress<InstallStatus>? progress = null,
        CancellationToken ct = default)
    {
        await SendAsync(new PlistDict
        {
            ["Command"] = "Install",
            ["PackagePath"] = packagePath,
            ["ClientOptions"] = options ?? new PlistDict(),
        }, ct).ConfigureAwait(false);
        while (true)
        {
            var reply = await ReadAsync(ct).ConfigureAwait(false);
            var status = reply.String("Status") ?? "";
            var percent = (int)Math.Clamp(reply.Long("PercentComplete") ?? (status == "Complete" ? 100 : 0), 0, 100);
            progress?.Report(new InstallStatus(status, percent));
            if (status == "Complete")
                return;
        }
    }

    /// <summary>One installed app's attributes, or null when it isn't installed.</summary>
    public async Task<PlistDict?> LookupAsync(string bundleId, IEnumerable<string>? attributes = null, CancellationToken ct = default)
    {
        await SendAsync(new PlistDict
        {
            ["Command"] = "Lookup",
            ["ClientOptions"] = new PlistDict
            {
                ["BundleIDs"] = new List<object> { bundleId },
                ["ReturnAttributes"] = (attributes ?? DefaultAttributes).Cast<object>().ToList(),
            },
        }, ct).ConfigureAwait(false);
        PlistDict? found = null;
        while (true)
        {
            var reply = await ReadAsync(ct).ConfigureAwait(false);
            if (reply.Dict("LookupResult")?.Dict(bundleId) is { } app)
                found = app;
            if (reply.String("Status") is "Complete" or null)
                return found;
        }
    }

    /// <summary>The apps the person installed (ApplicationType "User").</summary>
    public async Task<IReadOnlyList<PlistDict>> BrowseAsync(IEnumerable<string>? attributes = null, CancellationToken ct = default)
    {
        await SendAsync(new PlistDict
        {
            ["Command"] = "Browse",
            ["ClientOptions"] = new PlistDict
            {
                ["ApplicationType"] = "User",
                ["ReturnAttributes"] = (attributes ?? DefaultAttributes).Cast<object>().ToList(),
            },
        }, ct).ConfigureAwait(false);
        var apps = new List<PlistDict>();
        while (true)
        {
            var reply = await ReadAsync(ct).ConfigureAwait(false);
            foreach (var item in reply.Array("CurrentList") ?? [])
                if (item is PlistDict app)
                    apps.Add(app);
            if (reply.String("Status") is "Complete" or null)
                return apps;
        }
    }

    private Task SendAsync(PlistDict message, CancellationToken ct) =>
        PlistMessages.WriteAsync(_stream, message, TimeSpan.FromSeconds(30), ct);

    private async Task<PlistDict> ReadAsync(CancellationToken ct)
    {
        var reply = await PlistMessages.ReadAsync(_stream, Timeout, ct).ConfigureAwait(false);
        if (reply.String("Error") is { } error)
            throw DeviceErrors.FromInstall(error, reply.String("ErrorDescription"), reply.Long("ErrorDetail"));
        return reply;
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    public void Dispose() => _stream.Dispose();
}
