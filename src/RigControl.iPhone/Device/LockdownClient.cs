using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Device;

/// <summary>A service lockdown started: its port on the iPhone and whether the connection must use TLS.</summary>
public sealed record LockdownService(string Name, int Port, bool Ssl);

/// <summary>
/// lockdownd on the iPhone (port 62078): device values, a session from the pair record (TLS once the iPhone asks
/// for it), and starting services such as AFC and installation_proxy.
/// </summary>
public sealed class LockdownClient : IAsyncDisposable, IDisposable
{
    public const int Port = 62078;
    private const string Label = "RigControl";

    private Stream _stream;
    private readonly TlsWrapper _tls;
    private string? _sessionId;

    internal LockdownClient(Stream stream, TlsWrapper? tls = null, TimeSpan? timeout = null)
    {
        _stream = stream;
        _tls = tls ?? DeviceTls.WrapAsync;
        Timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public TimeSpan Timeout { get; }
    public bool InSession => _sessionId is not null;

    public static async Task<LockdownClient> ConnectAsync(UsbMuxClient mux, int deviceId, TlsWrapper? tls = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var stream = await mux.ConnectAsync(deviceId, Port, ct).ConfigureAwait(false);
        return new LockdownClient(stream, tls, timeout);
    }

    public async Task<string> QueryTypeAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync("QueryType", null, ct).ConfigureAwait(false);
        return reply.String("Type") ?? throw new DeviceException(DeviceErrors.BadReply);
    }

    /// <summary>One value (domain and key), a whole domain (key null), or everything (both null). Null when missing.</summary>
    public async Task<object?> GetValueAsync(string? domain = null, string? key = null, CancellationToken ct = default)
    {
        var extra = new PlistDict();
        if (domain is not null) extra["Domain"] = domain;
        if (key is not null) extra["Key"] = key;
        try
        {
            var reply = await RequestAsync("GetValue", extra, ct).ConfigureAwait(false);
            return reply.TryGetValue("Value", out var value) ? value : null;
        }
        catch (DeviceException e) when (LockdownError.Of(e) == "MissingValue")
        {
            return null;
        }
    }

    /// <summary>Starts a session as the paired host. Switches this connection to TLS when the iPhone asks for it.</summary>
    public async Task StartSessionAsync(PairRecord pair, CancellationToken ct = default)
    {
        var reply = await RequestAsync("StartSession", new PlistDict
        {
            ["HostID"] = pair.HostId,
            ["SystemBUID"] = pair.SystemBuid,
        }, ct).ConfigureAwait(false);
        var sessionId = reply.String("SessionID") ?? throw new DeviceException(DeviceErrors.BadReply);
        if (reply.Bool("EnableSessionSSL") == true)
            _stream = await _tls(_stream, pair, ct).ConfigureAwait(false);
        _sessionId = sessionId;
    }

    public async Task<LockdownService> StartServiceAsync(string name, CancellationToken ct = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Start a session before starting a service.");
        var reply = await RequestAsync("StartService", new PlistDict { ["Service"] = name }, ct).ConfigureAwait(false);
        var port = reply.Long("Port");
        if (port is null or <= 0 or > 65535)
            throw new DeviceException(DeviceErrors.BadReply);
        return new LockdownService(name, (int)port, reply.Bool("EnableServiceSSL") == true);
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        if (_sessionId is null)
            return;
        var id = _sessionId;
        _sessionId = null;
        await RequestAsync("StopSession", new PlistDict { ["SessionID"] = id }, ct).ConfigureAwait(false);
    }

    private async Task<PlistDict> RequestAsync(string request, PlistDict? extra, CancellationToken ct)
    {
        var message = new PlistDict { ["Label"] = Label, ["Request"] = request };
        if (extra is not null)
            foreach (var (k, v) in extra)
                message[k] = v;
        await PlistMessages.WriteAsync(_stream, message, Timeout, ct).ConfigureAwait(false);
        var reply = await PlistMessages.ReadAsync(_stream, Timeout, ct).ConfigureAwait(false);
        if (reply.String("Error") is { } error)
            throw new DeviceException(DeviceErrors.FromLockdown(request, error).Message, new LockdownError(request, error));
        if (reply.String("Request") is { } echoed && echoed != request)
            throw new DeviceException(DeviceErrors.BadReply);
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await StopSessionAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DeviceException or OperationCanceledException or InvalidOperationException)
            {
                // Closing the connection ends the session anyway.
            }
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>The inner exception of a <see cref="DeviceException"/> caused by a lockdown "Error" reply.</summary>
public sealed class LockdownError(string request, string error) : Exception($"lockdown {request}: {error}")
{
    public string Request { get; } = request;
    public string Error { get; } = error;

    /// <summary>Lockdown's error name behind a DeviceException, if that is what caused it.</summary>
    public static string? Of(Exception e) => (e.InnerException as LockdownError)?.Error;
}
