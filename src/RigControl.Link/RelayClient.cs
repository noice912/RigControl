using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Link;

/// <summary>A frame from the relay after the hello, already sorted by kind.</summary>
public abstract record RelayFrame;

/// <summary><c>{"t":"msg","from":..,"body":..}</c>: a sealed message from another device in the room.</summary>
public sealed record RelayMsg(string From, string Body) : RelayFrame;

/// <summary><c>{"t":"presence","id":..,"online":..}</c>: a phone came online or went away (PC only).</summary>
public sealed record RelayPresence(string Id, bool Online) : RelayFrame;

/// <summary><c>{"t":"gone","to":..}</c>: the device a message was for is not connected; nothing was delivered.</summary>
public sealed record RelayGone(string To) : RelayFrame;

/// <summary><c>{"t":"error","code":..}</c>.</summary>
public sealed record RelayError(string Code) : RelayFrame;

/// <summary>Anything else (a newer relay's frames are ignored, not treated as errors).</summary>
public sealed record RelayOther(string Type, JsonObject Raw) : RelayFrame;

internal static class RelayJson
{
    public static JsonObject? Parse(string? text)
    {
        if (text is null) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string? Str(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static RelayFrame? ToFrame(JsonObject? o)
    {
        if (o is null) return null;
        var t = Str(o, "t") ?? "";
        switch (t)
        {
            case "msg" when Str(o, "from") is { } from && Str(o, "body") is { } body:
                return new RelayMsg(from, body);
            case "presence" when Str(o, "id") is { } id && o["online"] is JsonValue on && on.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return new RelayPresence(id, on.GetValue<bool>());
            case "gone" when Str(o, "to") is { } to:
                return new RelayGone(to);
            case "error":
                return new RelayError(RelayCodes.Clean(Str(o, "code")));
            default:
                return new RelayOther(t, o);
        }
    }
}

/// <summary>
/// One connection to <c>/v1/connect</c> after a successful hello. Made by <see cref="OpenAsync"/>; closed by disposing.
/// </summary>
public sealed class RelaySession : IAsyncDisposable
{
    private readonly IRelayConnection _conn;
    private readonly DeviceKeys _keys;
    private readonly TimeSpan _sendTimeout;
    // Cancelled when a send ran out of time: the receive then ends as if the connection was lost.
    private readonly CancellationTokenSource _broken = new();

    private RelaySession(IRelayConnection conn, DeviceKeys keys, string role, string room, TimeSpan sendTimeout)
    {
        _conn = conn;
        _keys = keys;
        _sendTimeout = sendTimeout;
        Role = role;
        Room = room;
    }

    /// <summary>True once a send ran out of time and the connection was given up.</summary>
    public bool Broken => _broken.IsCancellationRequested;

    public string Role { get; }
    public string Room { get; }

    /// <summary>
    /// Connects and does the hello: waits for <c>{"t":"challenge","nonce":..}</c> (the nonce must be 32 bytes,
    /// base64url), answers with the signed hello, and waits for <c>{"t":"ok"}</c>. Throws <see cref="LinkException"/>
    /// with the relay's error code, or "handshake" when the relay does not follow the protocol or is too slow.
    /// </summary>
    public static async Task<RelaySession> OpenAsync(IRelayTransport transport, Uri relay, string role, string room, DeviceKeys keys,
        CancellationToken ct, TimeSpan? timeout = null, TimeSpan? sendTimeout = null)
    {
        if (role is not (LinkProtocol.RolePc or LinkProtocol.RolePhone)) throw new ArgumentException("Unknown role.", nameof(role));
        if (role == LinkProtocol.RolePc && room != keys.Id) throw new ArgumentException("A PC's room is its own id.", nameof(room));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        IRelayConnection? conn = null;
        try
        {
            conn = await transport.ConnectAsync(LinkEndpoints.Connect(relay, room), cts.Token).ConfigureAwait(false);
            var challenge = RelayJson.Parse(await conn.ReceiveAsync(cts.Token).ConfigureAwait(false));
            if (challenge is null) throw conn.CloseReason is { } r1 ? RelayFailure(r1) : new LinkException("handshake", "The relay closed the connection.");
            if (RelayJson.Str(challenge, "t") == "error") throw RelayFailure(RelayJson.Str(challenge, "code"));
            var nonce = RelayJson.Str(challenge, "nonce");
            // Exactly 43 canonical base64url characters (32 bytes) before anything is signed over it.
            if (RelayJson.Str(challenge, "t") != "challenge" || nonce is not { Length: 43 } || !B64Url.TryDecode(nonce, out var raw) || raw.Length != 32)
                throw new LinkException("handshake", "The relay did not send a proper challenge.");
            var hello = new JsonObject
            {
                ["t"] = "hello",
                ["role"] = role,
                ["room"] = room,
                ["pub"] = keys.SigningPublicB64,
                ["sig"] = keys.Sign(LinkProtocol.HelloSignedText(nonce!, room)),
            };
            // WaitAsync: the handshake's time limit holds even for a send that ignores the token.
            await conn.SendAsync(hello.ToJsonString(), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            var answer = RelayJson.Parse(await conn.ReceiveAsync(cts.Token).ConfigureAwait(false));
            if (answer is null) throw conn.CloseReason is { } r2 ? RelayFailure(r2) : new LinkException("handshake", "The relay closed the connection.");
            switch (RelayJson.Str(answer, "t"))
            {
                case "ok":
                    var s = new RelaySession(conn, keys, role, room, sendTimeout ?? LinkProtocol.SendTimeout);
                    conn = null;
                    return s;
                case "error":
                    throw RelayFailure(RelayJson.Str(answer, "code"));
                default:
                    throw new LinkException("handshake", "The relay answered the hello with something unexpected.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LinkException("handshake", "The relay did not answer in time.");
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The relay's code, cleaned (<see cref="RelayCodes.Clean"/>); the message never carries relay text.</summary>
    internal static LinkException RelayFailure(string? code)
    {
        var c = RelayCodes.Clean(code);
        return new LinkException(c, "The relay refused (" + RelayCodes.Shown(c) + ").");
    }

    /// <summary>PC only: replaces the room's allow-list. The ids are sent sorted (ordinal) and signed in that order.</summary>
    public Task SendAllowAsync(IEnumerable<string> phoneIds, CancellationToken ct)
    {
        if (Role != LinkProtocol.RolePc) throw new InvalidOperationException("Only the PC sets the allow-list.");
        var ids = phoneIds.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToArray();
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(id);
        var o = new JsonObject
        {
            ["t"] = "allow",
            ["phones"] = arr,
            ["sig"] = _keys.Sign(LinkProtocol.AllowSignedText(Room, ids)),
        };
        return SendAsync(o.ToJsonString(), ct);
    }

    /// <summary><c>{"t":"ping"}</c>: something the relay answers, to tell a live connection from a dead one.</summary>
    public Task SendPingAsync(CancellationToken ct) => SendAsync("{\"t\":\"ping\"}", ct);

    /// <summary><c>{"t":"msg","to":..,"body":..}</c>.</summary>
    public Task SendMsgAsync(string to, string body, CancellationToken ct)
        => SendAsync(new JsonObject { ["t"] = "msg", ["to"] = to, ["body"] = body }.ToJsonString(), ct);

    /// <summary>
    /// One frame, at most <see cref="LinkProtocol.SendTimeout"/> (also when the connection ignores the token). When the
    /// time runs out the session is given up: <see cref="ReceiveAsync"/> ends, so the link connects again, and the
    /// caller gets <see cref="LinkException"/> "timeout".
    /// </summary>
    private async Task SendAsync(string text, CancellationToken ct)
    {
        if (Broken) throw new LinkException("dropped", "The connection to the relay was given up.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _broken.Token);
        cts.CancelAfter(_sendTimeout);
        try
        {
            await _conn.SendAsync(text, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            GiveUp();
            throw new LinkException("timeout", "Sending to the relay took too long; the connection was dropped.");
        }
        catch (LinkException ex) when (ex.Code == "timeout")
        {
            GiveUp();
            throw;
        }
    }

    private void GiveUp()
    {
        try { _broken.Cancel(); } catch (ObjectDisposedException) { /* disposed meanwhile */ }
    }

    /// <summary>The next frame, or null once the relay closed the connection (or it was given up). Frames that are not JSON are skipped.</summary>
    public async Task<RelayFrame?> ReceiveAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _broken.Token);
        while (true)
        {
            string? text;
            try
            {
                text = await _conn.ReceiveAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Broken)
            {
                return null;
            }
            if (text is null) return null;
            var f = RelayJson.ToFrame(RelayJson.Parse(text));
            if (f is not null) return f;
        }
    }

    /// <summary>After <see cref="ReceiveAsync"/> returned null: the relay's close reason ("replaced", "removed" ...), if any.</summary>
    public string? CloseReason => _conn.CloseReason is { } r ? RelayCodes.Clean(r) : null;

    /// <summary>Closes the connection; never waits more than a few seconds, even for one stuck in a send.</summary>
    public async ValueTask DisposeAsync()
    {
        GiveUp();
        try
        {
            await _conn.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Left to finish on its own.
        }
        _broken.Dispose();
    }
}

public enum LinkConnectionState
{
    /// <summary>Not started, or stopped.</summary>
    Off,
    Connecting,
    /// <summary>Hello accepted; messages can go both ways.</summary>
    Online,
    /// <summary>Lost or refused; trying again after a pause.</summary>
    Waiting,
}

/// <summary>Settings for <see cref="RelayLink"/>; tests shorten the pauses.</summary>
public sealed class RelayLinkOptions
{
    /// <summary>The pause before attempt <c>n</c> (0 = first retry). Default: 1, 2, 4 ... 60 s, with +-20% jitter.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Longest one send may take before the connection is dropped and made again.</summary>
    public TimeSpan SendTimeout { get; init; } = LinkProtocol.SendTimeout;

    /// <summary>Silence after which a ping is sent to the relay.</summary>
    public TimeSpan IdleProbeAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Silence after which the connection counts as dead and is made again.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>Shortest pause after "denied" (not on the PC's allow-list).</summary>
    public TimeSpan DeniedPause { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A connection that stayed up this long starts the pauses from the beginning again.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);

    public Action<string>? Log { get; init; }

    public static TimeSpan DefaultBackoff(int attempt)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)));
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}

/// <summary>
/// Keeps one device connected to its room: connects, does the hello, hands every frame to the owner in order,
/// and on any loss waits (backing off) and connects again until stopped.
/// </summary>
public sealed class RelayLink : IAsyncDisposable
{
    private readonly IRelayTransport _transport;
    private readonly Uri _relay;
    private readonly string _role;
    private readonly string _room;
    private readonly DeviceKeys _keys;
    private readonly Func<RelayFrame, CancellationToken, Task> _onFrame;
    private readonly Func<RelaySession, CancellationToken, Task>? _onConnected;
    private readonly RelayLinkOptions _options;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private RelaySession? _session;
    private TaskCompletionSource _online = NewTcs();
    private LinkConnectionState _state = LinkConnectionState.Off;
    private string? _lastError;

    public RelayLink(IRelayTransport transport, Uri relay, string role, string room, DeviceKeys keys,
        Func<RelayFrame, Task> onFrame, Func<RelaySession, CancellationToken, Task>? onConnected = null, RelayLinkOptions? options = null)
        : this(transport, relay, role, room, keys, (f, _) => onFrame(f), onConnected, options)
    {
    }

    /// <param name="onFrame">Gets every frame and the link's stop token (cancelled by <see cref="StopAsync"/>).</param>
    public RelayLink(IRelayTransport transport, Uri relay, string role, string room, DeviceKeys keys,
        Func<RelayFrame, CancellationToken, Task> onFrame, Func<RelaySession, CancellationToken, Task>? onConnected = null,
        RelayLinkOptions? options = null)
    {
        _transport = transport;
        _relay = relay;
        _role = role;
        _room = room;
        _keys = keys;
        _onFrame = onFrame;
        _onConnected = onConnected;
        _options = options ?? new RelayLinkOptions();
    }

    /// <summary>Raised on every state change (on a background thread).</summary>
    public event Action? StateChanged;

    public LinkConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Why the last attempt failed (the relay's code, or a short reason), or null.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public bool Running
    {
        get { lock (_gate) return _loop is not null; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            loop = _loop;
            stop = _stop;
            _loop = null;
            _stop = null;
        }
        if (loop is null) return;
        try { stop!.Cancel(); } catch { /* ignore */ }
        try { await loop.ConfigureAwait(false); } catch { /* the loop logs its own failures */ }
        stop!.Dispose();
        SetState(LinkConnectionState.Off, null);
    }

    /// <summary>Waits until the hello was accepted (throws when <paramref name="ct"/> ends first).</summary>
    public Task WaitOnlineAsync(CancellationToken ct)
    {
        Task t;
        lock (_gate) t = _online.Task;
        return t.WaitAsync(ct);
    }

    /// <summary>Sends a sealed body; false when not connected right now (nothing is queued in v1).</summary>
    public async Task<bool> SendMsgAsync(string to, string body, CancellationToken ct)
    {
        RelaySession? s;
        lock (_gate) s = _session;
        if (s is null) return false;
        try
        {
            await s.SendMsgAsync(to, body, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _options.Log?.Invoke("Sending to the relay failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>PC only: sends the allow-list now if connected; false otherwise (it is sent again on every connect).</summary>
    public async Task<bool> SendAllowAsync(IEnumerable<string> phoneIds, CancellationToken ct)
    {
        RelaySession? s;
        lock (_gate) s = _session;
        if (s is null) return false;
        try
        {
            await s.SendAllowAsync(phoneIds, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _options.Log?.Invoke("Sending the allow-list failed: " + ex.Message);
            return false;
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var attempt = 0;
        var final = false;
        while (!stop.IsCancellationRequested)
        {
            SetState(LinkConnectionState.Connecting, null);
            var connectedAt = DateTime.MinValue;
            string? error = null;
            try
            {
                var session = await RelaySession.OpenAsync(_transport, _relay, _role, _room, _keys, stop, _options.HandshakeTimeout,
                        _options.SendTimeout)
                    .ConfigureAwait(false);
                await using (session.ConfigureAwait(false))
                {
                    connectedAt = DateTime.UtcNow;
                    lock (_gate) _session = session;
                    if (_onConnected is not null) await _onConnected(session, stop).ConfigureAwait(false);
                    SetState(LinkConnectionState.Online, null);
                    error = await ReceiveLoopAsync(session, stop).ConfigureAwait(false);
                    error ??= session.CloseReason ?? "dropped";
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (LinkException ex)
            {
                error = ex.Code;
                _options.Log?.Invoke("Relay connection: " + ex.Message);
            }
            catch (Exception ex) when (connectedAt != DateTime.MinValue)
            {
                // Lost after the hello (an aborted socket, "Aborted"): a drop, not an unreachable relay.
                error = "dropped";
                _options.Log?.Invoke("The connection to the relay dropped (" + ex.GetType().Name + ").");
            }
            catch (Exception ex)
            {
                error = "unreachable";
                _options.Log?.Invoke("The relay could not be reached: " + ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _session = null;
                    if (_online.Task.IsCompleted) _online = NewTcs();
                }
            }
            if (stop.IsCancellationRequested) break;
            if (_role == LinkProtocol.RolePhone && error == "removed")
            {
                // This phone was taken off the PC's allow-list: final. Trying again would only be refused ("denied")
                // and use up the relay's per-address limit. It stays off until the app starts it again.
                _options.Log?.Invoke("This phone was removed from the PC; not connecting again.");
                SetState(LinkConnectionState.Off, "removed");
                final = true;
                break;
            }
            if (connectedAt != DateTime.MinValue && DateTime.UtcNow - connectedAt >= _options.StableAfter) attempt = 0;
            SetState(LinkConnectionState.Waiting, error);
            try
            {
                var pause = _options.Backoff(attempt);
                // Over the relay's per-address limit (HTTP 429, or closed "ratelimit"): at least 10 s, growing to a minute.
                if (error == "ratelimit")
                    pause = TimeSpan.FromSeconds(Math.Max(Math.Min(60, 10 * Math.Pow(2, Math.Min(attempt, 3))), pause.TotalSeconds));
                // Not (or no longer) on the allow-list: that changes only when the PC links this phone again.
                if (error == "denied" && pause < _options.DeniedPause) pause = _options.DeniedPause;
                await Task.Delay(pause, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            attempt++;
        }
        if (!final) return;
        // Ended by itself: forget this run so Start() can begin a new one later.
        CancellationTokenSource? mine = null;
        lock (_gate)
        {
            if (_stop is { } s && s.Token == stop)
            {
                mine = s;
                _stop = null;
                _loop = null;
            }
        }
        mine?.Dispose();
    }

    /// <summary>
    /// Hands frames to the owner until the connection ends; returns the relay's last error code, if any.
    /// .NET's WebSocket notices a dead connection only very late, so silence is checked here: after
    /// <see cref="RelayLinkOptions.IdleProbeAfter"/> without a frame a <c>{"t":"ping"}</c> is sent (today's relay answers
    /// any unknown frame with <c>{"t":"error","code":"badmsg"}</c>, which counts as hearing from it and is not passed on;
    /// a newer relay may answer "pong"), and after <see cref="RelayLinkOptions.IdleTimeout"/> the connection is dropped
    /// and made again.
    /// </summary>
    private async Task<string?> ReceiveLoopAsync(RelaySession session, CancellationToken stop)
    {
        string? error = null;
        var heard = DateTime.UtcNow;
        var pinged = false;
        Task<RelayFrame?>? pending = null;
        try
        {
            while (true)
            {
                pending ??= session.ReceiveAsync(stop);
                var deadline = heard + (pinged ? _options.IdleTimeout : _options.IdleProbeAfter);
                var wait = deadline - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    var done = await Task.WhenAny(pending, Task.Delay(wait, stop)).ConfigureAwait(false);
                    stop.ThrowIfCancellationRequested();
                    if (done != pending) continue;
                }
                else if (!pending.IsCompleted)
                {
                    if (!pinged)
                    {
                        pinged = true;
                        try { await session.SendPingAsync(stop).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { return "dropped"; }
                        continue;
                    }
                    _options.Log?.Invoke($"Nothing heard from the relay for {_options.IdleTimeout.TotalSeconds:0} s; connecting again.");
                    return "dropped";
                }
                var frame = await pending.ConfigureAwait(false);
                pending = null;
                if (frame is null) return error;
                heard = DateTime.UtcNow;
                var wasPing = pinged;
                pinged = false;
                // The relay's answer to our ping: not an error, and not for the owner.
                if (wasPing && frame is RelayError { Code: "badmsg" } or RelayOther { Type: "pong" }) continue;
                error = frame is RelayError err ? err.Code : null;
                try
                {
                    await _onFrame(frame, stop).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _options.Log?.Invoke("A relay frame could not be handled: " + ex.Message);
                }
            }
        }
        finally
        {
            // A receive still running ends when the session is closed; its outcome no longer matters.
            if (pending is { IsCompleted: false }) _ = pending.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private void SetState(LinkConnectionState state, string? error)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != state || _lastError != error;
            _state = state;
            _lastError = error;
            if (state == LinkConnectionState.Online) _online.TrySetResult();
        }
        if (!changed) return;
        try { StateChanged?.Invoke(); }
        catch { /* a bad listener must not stop the link */ }
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
