using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace RigControl.Link;

public sealed class PhoneSideOptions
{
    public RelayLinkOptions Relay { get; init; } = new();
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long to wait for the PC's answer.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The phone end of RigControl Link, for the phone app (and tools/LinkProbe): pair with a code, connect to the
/// PC's room, ask for a snapshot, send an action. The phone stores the <see cref="LinkedPc"/>, its own keys
/// (protected) and <see cref="LastIn"/> between runs.
/// </summary>
public sealed class PhoneSide : IAsyncDisposable
{
    private readonly LinkedPc _pc;
    private readonly SecureChannel _channel;
    private readonly RelayLink _link;
    private readonly PhoneSideOptions _options;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();

    public PhoneSide(IRelayTransport transport, LinkedPc pc, DeviceKeys keys, long lastIn = 0, PhoneSideOptions? options = null)
    {
        _pc = pc;
        _options = options ?? new PhoneSideOptions();
        var relay = LinkEndpoints.ParseRelay(pc.Relay) ?? throw new LinkException("badrelay", "The PC's relay address is not usable.");
        _channel = SecureChannel.For(keys, pc.Id, B64Url.Decode(pc.AgreementKey), iAmPc: false, pc.Epoch, lastIn, _options.Time);
        _link = new RelayLink(transport, relay, LinkProtocol.RolePhone, pc.Id, keys, OnFrameAsync, null, _options.Relay);
        _link.StateChanged += () => StateChanged?.Invoke();
    }

    /// <summary>Pairs with the code the PC shows. <paramref name="relay"/> is a base address or a bare host (wss).</summary>
    public static Task<LinkedPc> PairAsync(IRelayTransport transport, string relay, string code, DeviceKeys keys, string phoneName,
        CancellationToken ct)
    {
        var uri = LinkEndpoints.ParseRelay(relay) ?? throw new LinkException("badrelay", "That relay address is not usable.");
        return Pairing.ClaimAsync(transport, uri, code, keys, phoneName, ct);
    }

    /// <summary>Pairs from the PC's QR code text (<c>rigcontrol://pair?relay=..&amp;code=..</c>).</summary>
    public static Task<LinkedPc> PairFromQrAsync(IRelayTransport transport, string qrText, DeviceKeys keys, string phoneName, CancellationToken ct)
    {
        if (!PairingCode.TryParseQr(qrText, out var host, out var code))
            throw new LinkException("badqr", "That QR code isn't a RigControl pairing code.");
        return PairAsync(transport, host, code, keys, phoneName, ct);
    }

    public LinkedPc Pc => _pc;

    /// <summary>The last counter accepted from the PC. Store it, and give it back next time.</summary>
    public long LastIn => _channel.LastIn;

    public LinkConnectionState State => _link.State;
    public string? LastError => _link.LastError;

    public event Action? StateChanged;

    /// <summary>An "event" from the PC.</summary>
    public event Action<JsonObject>? EventReceived;

    /// <summary>Starts the connection (it reconnects by itself) and waits until the relay accepted the hello.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _link.Start();
        await _link.WaitOnlineAsync(ct).ConfigureAwait(false);
    }

    public Task StopAsync() => _link.StopAsync();

    /// <summary>Asks the PC for its state. Throws <see cref="LinkException"/>: "offline", "pcoffline", "timeout", "refused", "stale", "busy".</summary>
    public async Task<JsonObject> RequestSnapshotAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(LinkMessages.GetSnapshot(), ct).ConfigureAwait(false);
        if (LinkMessages.Str(reply, "type") == LinkMessageTypes.Snapshot) return reply;
        // The PC's own refusals keep their code ("stale", "busy") so the app can word them; anything else is "refused".
        var code = LinkMessages.Str(reply, "code");
        throw new LinkException(code is LinkRefusals.Stale or LinkRefusals.Busy ? code : "refused",
            LinkMessages.Str(reply, "error") ?? "The PC did not send its state.");
    }

    /// <summary>Asks the PC to do one of <see cref="LinkActions.Allowed"/>. The PC checks it again and may refuse.</summary>
    public async Task<LinkActionResult> SendActionAsync(string action, JsonObject? args, CancellationToken ct)
    {
        var reply = await RequestAsync(LinkMessages.Do(action, args), ct).ConfigureAwait(false);
        var ok = reply["ok"] is JsonValue v && v.TryGetValue(out bool b) && b;
        return ok ? LinkActionResult.Success : LinkActionResult.Fail(LinkMessages.Str(reply, "error") ?? "Refused.", LinkMessages.Str(reply, "code"));
    }

    private async Task<JsonObject> RequestAsync(JsonObject message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        long n = 0;
        try
        {
            // Numbered and sent in one step (concurrent requests keep their order); registered before it goes out.
            var sent = await _channel.SendSealedAsync(message, (counter, body) =>
            {
                n = counter;
                _pending[counter] = tcs;
                return _link.SendMsgAsync(_pc.Id, body, ct);
            }, ct).ConfigureAwait(false);
            if (!sent) throw new LinkException("offline", "Not connected to the relay.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.ReplyTimeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LinkException("timeout", "The PC did not answer in time.");
            }
        }
        finally
        {
            _pending.TryRemove(n, out _);
        }
    }

    private Task OnFrameAsync(RelayFrame frame)
    {
        switch (frame)
        {
            case RelayMsg m when m.From == _pc.Id:
                JsonObject msg;
                try { msg = _channel.Open(m.Body); }
                catch (LinkException) { return Task.CompletedTask; }
                if (LinkMessages.Str(msg, "type") == LinkMessageTypes.Event)
                {
                    try { EventReceived?.Invoke(msg); }
                    catch { /* a bad listener must not stop the link */ }
                }
                else if (LinkMessages.Long(msg, "re") is { } re && _pending.TryGetValue(re, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
                break;
            case RelayGone g when g.To == _pc.Id:
                foreach (var p in _pending.Values) p.TrySetException(new LinkException("pcoffline", "The PC is not connected right now."));
                break;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _link.DisposeAsync();
}
