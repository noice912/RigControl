using System.Net.WebSockets;
using System.Text;

namespace RigControl.Link;

/// <summary>One open connection to the relay: JSON text frames both ways.</summary>
public interface IRelayConnection : IAsyncDisposable
{
    /// <summary>Sends one text frame. Safe to call from several threads (sends are queued).</summary>
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next text frame, or null once the connection is closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>
    /// After <see cref="ReceiveAsync"/> returned null: the reason the relay gave in its close frame (its error code,
    /// e.g. "replaced", "nopair", "paired"), or null when the connection was lost without one.
    /// </summary>
    string? CloseReason { get; }
}

/// <summary>Opens connections to the relay. The real one is <see cref="WebSocketRelayTransport"/>; tests use a fake.</summary>
public interface IRelayTransport
{
    Task<IRelayConnection> ConnectAsync(Uri uri, CancellationToken ct);
}

/// <summary>
/// The relay over <see cref="ClientWebSocket"/>. Only outgoing connections: nothing here listens for anything.
/// Frames bigger than 64 KiB, and binary frames, close the connection. No Origin header is sent (ClientWebSocket
/// sends none unless told to; keep it that way: the relay treats a browser origin as a web page).
/// </summary>
public sealed class WebSocketRelayTransport : IRelayTransport
{
    public async Task<IRelayConnection> ConnectAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != "wss" && !(uri.Scheme == "ws" && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))))
            throw new LinkException("badrelay", "The relay must be reached over wss://.");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // Lets a refused upgrade be told apart: HTTP 429 means "over the per-address limit, back off".
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            var status = ws.HttpStatusCode;
            ws.Dispose();
            throw status switch
            {
                System.Net.HttpStatusCode.TooManyRequests => new LinkException("ratelimit", "The relay asked to wait before connecting again.", ex),
                System.Net.HttpStatusCode.Forbidden => new LinkException("forbidden", "The relay refused the connection.", ex),
                0 => ex,
                _ => new LinkException("unreachable", $"The relay answered HTTP {(int)status}.", ex),
            };
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        return new Connection(ws);
    }

    private sealed class Connection : IRelayConnection
    {
        private readonly ClientWebSocket _ws;
        private readonly SemaphoreSlim _send = new(1, 1);

        public Connection(ClientWebSocket ws)
        {
            _ws = ws;
        }

        public string? CloseReason { get; private set; }

        /// <summary>
        /// One send, queued behind the others, may take at most <see cref="LinkProtocol.SendTimeout"/> (the wait in the
        /// queue included). When that runs out the socket is aborted: the receive then ends, and the link connects again
        /// instead of hanging on a connection that no longer moves.
        /// </summary>
        public async Task SendAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > LinkProtocol.MaxFrameBytes) throw new LinkException("toobig", "The message is too big for the relay.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LinkProtocol.SendTimeout);
            var queued = false;
            try
            {
                await _send.WaitAsync(cts.Token).ConfigureAwait(false);
                queued = true;
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { _ws.Abort(); } catch { /* already gone */ }
                throw new LinkException("timeout", "Sending to the relay took too long; the connection was dropped.");
            }
            catch (ObjectDisposedException) when (!queued)
            {
                // Disposed while waiting in the queue.
                throw new LinkException("closed", "The connection to the relay is closed.");
            }
            finally
            {
                if (queued)
                {
                    try { _send.Release(); } catch (ObjectDisposedException) { /* disposed meanwhile */ }
                }
            }
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult r;
                try
                {
                    r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException
                                               || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    // Aborted or lost: closed without a reason.
                    return null;
                }
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    // The relay's reason is its error code; 4000/4001 mean replaced/removed even without one.
                    CloseReason = !string.IsNullOrEmpty(r.CloseStatusDescription) ? r.CloseStatusDescription
                        : (int?)r.CloseStatus switch { 4000 => "replaced", 4001 => "removed", _ => null };
                    await CloseQuietly(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                    return null;
                }
                if (r.MessageType != WebSocketMessageType.Text)
                {
                    await CloseQuietly(WebSocketCloseStatus.InvalidMessageType).ConfigureAwait(false);
                    return null;
                }
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > LinkProtocol.MaxFrameBytes)
                {
                    await CloseQuietly(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                    return null;
                }
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        private async Task CloseQuietly(WebSocketCloseStatus status)
        {
            try
            {
                if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseOutputAsync(status, null, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Closing is best effort.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseQuietly(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
            _ws.Dispose();
            _send.Dispose();
        }
    }
}
