using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace RigControl.Link;

/// <summary>A linked phone as <see cref="PcSide"/> needs it.</summary>
/// <param name="Id">The phone's id.</param>
/// <param name="Name">Its name (for notices).</param>
/// <param name="AgreementKey">Its agreement public key, base64url SPKI.</param>
/// <param name="LastIn">The last counter accepted from it, as stored.</param>
/// <param name="Epoch">The epoch of its pairing (<see cref="PairedPhone.Epoch"/>).</param>
/// <param name="LastOut">The highest counter the PC sent it, as stored: the PC never sends a lower one (a clock set back).</param>
public sealed record PhoneRecord(string Id, string Name, string AgreementKey, long LastIn, string Epoch, long LastOut = 0);

/// <summary>What the PC app gives <see cref="PcSide"/>: its linked phones, its state and its actions.</summary>
public interface IPcLinkHost
{
    /// <summary>The phones linked now. Only these may talk to the PC.</summary>
    IReadOnlyList<PhoneRecord> LinkedPhones();

    /// <summary>The PC's state for a "get snapshot" (sensors, recording, lighting...). Small: it travels in one frame.</summary>
    Task<JsonObject> SnapshotAsync(PhoneRecord phone, CancellationToken ct);

    /// <summary>Runs an action that already passed <see cref="LinkActions.Check"/>.</summary>
    Task<LinkActionResult> RunAsync(PhoneRecord phone, LinkAction action, CancellationToken ct);

    /// <summary>A message from the phone was accepted; store <paramref name="counter"/> (replay protection) and the time.</summary>
    void Accepted(string phoneId, long counter);

    /// <summary>
    /// What <see cref="PcSide"/> calls: <paramref name="saveNow"/> is true for a "do", whose counter must be written
    /// before anything else happens (also when it is then answered "busy"), so it can never be replayed after a
    /// restart. Hosts that store every counter at once can leave this to the two-argument form.
    /// </summary>
    void Accepted(string phoneId, long counter, bool saveNow) => Accepted(phoneId, counter);

    /// <summary>The PC numbered a message to the phone with <paramref name="counter"/>; store the highest one (LastOut).</summary>
    void Sent(string phoneId, long counter)
    {
    }

    /// <summary>The relay says the phone came online or went away.</summary>
    void PresenceChanged(string phoneId, bool online);
}

public sealed class PcSideOptions
{
    public RelayLinkOptions Relay { get; init; } = new();
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Requests one phone may send per 10 seconds; more are answered "busy".</summary>
    public int MaxRequestsPer10Seconds { get; init; } = 30;

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The PC end of RigControl Link: stays connected to its room (the PC's id), keeps the relay's allow-list equal to
/// the linked phones, answers "get" and checks every "do" against <see cref="LinkActions"/> before the app runs it.
/// </summary>
public sealed class PcSide : IAsyncDisposable
{
    private readonly IRelayTransport _transport;
    private readonly Uri _relay;
    private readonly DeviceKeys _keys;
    private readonly IPcLinkHost _host;
    private readonly PcSideOptions _options;
    private readonly RelayLink _link;
    private readonly ConcurrentDictionary<string, (string AgreementKey, string Epoch, SecureChannel Channel)> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _online = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<long>> _rate = new(StringComparer.Ordinal);
    private readonly LogLimiter _limited;

    public PcSide(IRelayTransport transport, Uri relay, DeviceKeys keys, IPcLinkHost host, PcSideOptions? options = null)
    {
        _transport = transport;
        _relay = relay;
        _keys = keys;
        _host = host;
        _options = options ?? new PcSideOptions();
        _limited = new LogLimiter(_options.Log, _options.Time);
        _link = new RelayLink(transport, relay, LinkProtocol.RolePc, keys.Id, keys, OnFrameAsync, OnConnectedAsync, _options.Relay);
        _link.StateChanged += () =>
        {
            if (_link.State != LinkConnectionState.Online) _online.Clear();
            StateChanged?.Invoke();
        };
    }

    /// <summary>The PC's room on the relay (its id).</summary>
    public string Room => _keys.Id;

    public Uri Relay => _relay;

    public LinkConnectionState State => _link.State;
    public string? LastError => _link.LastError;
    public bool Running => _link.Running;

    /// <summary>Raised when the connection state or a phone's presence changes.</summary>
    public event Action? StateChanged;

    public IReadOnlyCollection<string> OnlinePhones => _online.Where(p => p.Value).Select(p => p.Key).ToArray();

    public void Start() => _link.Start();

    public Task StopAsync() => _link.StopAsync();

    public Task WaitOnlineAsync(CancellationToken ct) => _link.WaitOnlineAsync(ct);

    /// <summary>
    /// Sends the allow-list (the host's linked phones) now, and forgets sessions of phones no longer linked.
    /// Removed phones are refused from here on even while the PC is offline; the relay gets the new list at the
    /// latest when the PC connects again. Returns false when the PC is not connected right now.
    /// </summary>
    public Task<bool> RefreshAllowListAsync(CancellationToken ct)
    {
        var ids = _host.LinkedPhones().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        lock (_channelGate)
        {
            foreach (var id in _channels.Keys)
                if (!ids.Contains(id)) _channels.TryRemove(id, out _);
        }
        foreach (var id in _online.Keys)
            if (!ids.Contains(id)) _online.TryRemove(id, out _);
        return _link.SendAllowAsync(ids, ct);
    }

    /// <summary>Offers <paramref name="code"/> on the relay and waits for a phone (see <see cref="Pairing.OfferAsync"/>).</summary>
    public Task<PairedPhone> PairAsync(string code, string pcName, TimeSpan lifetime, CancellationToken ct)
        => Pairing.OfferAsync(_transport, _relay, code, _keys, pcName, lifetime, ct);

    /// <summary>Sends an "event" to one phone if it is connected. False when it could not be sent.</summary>
    public async Task<bool> SendEventAsync(string phoneId, string kind, JsonObject? data, CancellationToken ct)
    {
        var phone = _host.LinkedPhones().FirstOrDefault(p => p.Id == phoneId);
        if (phone is null) return false;
        return await Channel(phone).SendSealedAsync(LinkMessages.Event(kind, data), (counter, body) =>
            {
                _host.Sent(phone.Id, counter);
                return _link.SendMsgAsync(phone.Id, body, ct);
            }, ct)
            .ConfigureAwait(false);
    }

    private Task OnConnectedAsync(RelaySession session, CancellationToken ct)
        => session.SendAllowAsync(_host.LinkedPhones().Select(p => p.Id), ct);

    /// <summary>
    /// The phone's channel, made once per pairing. Made under a lock: two messages arriving at once (or a message and
    /// an event) must share one channel, or each would keep its own counters and one could accept a replay the other
    /// already saw.
    /// </summary>
    internal SecureChannel Channel(PhoneRecord phone)
    {
        lock (_channelGate)
        {
            if (_channels.TryGetValue(phone.Id, out var c) && c.AgreementKey == phone.AgreementKey && c.Epoch == phone.Epoch) return c.Channel;
            var ch = SecureChannel.For(_keys, phone.Id, B64Url.Decode(phone.AgreementKey), iAmPc: true, phone.Epoch, phone.LastIn, _options.Time,
                phone.LastOut);
            _channels[phone.Id] = (phone.AgreementKey, phone.Epoch, ch);
            return ch;
        }
    }

    private readonly object _channelGate = new();

    private async Task OnFrameAsync(RelayFrame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case RelayMsg m:
                await OnMessageAsync(m, ct).ConfigureAwait(false);
                break;
            case RelayPresence p:
                // Only a change is passed on: a relay repeating presence frames can't flood the app with updates.
                if (_host.LinkedPhones().Any(x => x.Id == p.Id) && (_online.TryGetValue(p.Id, out var was) && was) != p.Online)
                {
                    _online[p.Id] = p.Online;
                    _host.PresenceChanged(p.Id, p.Online);
                    StateChanged?.Invoke();
                }
                break;
            case RelayError e:
                _limited.Log("relay", "The relay reported: " + RelayCodes.Shown(e.Code));
                break;
        }
    }

    private async Task OnMessageAsync(RelayMsg m, CancellationToken ct)
    {
        var phone = _host.LinkedPhones().FirstOrDefault(p => p.Id == m.From);
        if (phone is null)
        {
            _limited.Log("notlinked", "A message from a device that is not linked was ignored.");
            return;
        }
        var channel = Channel(phone);
        JsonObject msg;
        bool stale;
        try
        {
            msg = channel.Open(m.Body, c => LinkProtocol.IsFresh(c, _options.Time.GetUtcNow()), out stale);
        }
        catch (LinkException ex)
        {
            _limited.Log("refused", $"A message from {phone.Name} was refused ({RelayCodes.Shown(ex.Code)}).");
            return;
        }
        catch (Exception ex)
        {
            _limited.Log("refused", $"A message from {phone.Name} could not be read ({ex.GetType().Name}).");
            return;
        }
        var n = LinkMessages.Long(msg, "n") ?? 0;
        // The accepted counter is stored before the rate check (a "do" written at once), so even a "busy" one can
        // never be replayed after a restart.
        if (!stale) _host.Accepted(phone.Id, channel.LastIn, saveNow: LinkMessages.Str(msg, "type") == LinkMessageTypes.Do);
        JsonObject reply;
        if (stale)
        {
            // Held back or delayed (or the phone's clock is far off): refused, and LastIn stays where it was.
            _limited.Log("stale", $"A message from {phone.Name} was refused (stale).");
            reply = LinkMessages.Result(n, false, LinkRefusals.StaleText, LinkRefusals.Stale);
        }
        else if (!Allow(phone.Id))
        {
            reply = LinkMessages.Result(n, false, LinkRefusals.BusyText, LinkRefusals.Busy);
        }
        else
        {
            try
            {
                reply = await AnswerAsync(phone, msg, n, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"A request from {phone.Name} failed: {ex.Message}");
                // The exception's text stays on the PC.
                reply = LinkMessages.Result(n, false, "The PC could not do that.", "internal");
            }
        }
        // The link's stop token: stopping never waits behind an answer that can't go out (and each send has its own
        // time limit, after which the connection is dropped and made again).
        bool sent;
        try
        {
            sent = await channel.SendSealedAsync(reply, (counter, body) =>
                {
                    _host.Sent(phone.Id, counter);
                    return _link.SendMsgAsync(phone.Id, body, ct);
                }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        if (!sent) _options.Log?.Invoke($"The answer to {phone.Name} could not be sent.");
    }

    private async Task<JsonObject> AnswerAsync(PhoneRecord phone, JsonObject msg, long n, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        switch (LinkMessages.Str(msg, "type"))
        {
            case LinkMessageTypes.Get:
                if (LinkMessages.Str(msg, "what") != LinkMessageTypes.WhatSnapshot) return LinkMessages.Result(n, false, "Unknown request.");
                return LinkMessages.Snapshot(n, await _host.SnapshotAsync(phone, cts.Token).ConfigureAwait(false));
            case LinkMessageTypes.Do:
                var action = LinkActions.Check(LinkMessages.Str(msg, "action"), msg["args"], out var error);
                if (action is null)
                {
                    _options.Log?.Invoke($"{phone.Name} asked for something not allowed; refused.");
                    return LinkMessages.Result(n, false, error);
                }
                var r = await _host.RunAsync(phone, action, cts.Token).ConfigureAwait(false);
                return LinkMessages.Result(n, r.Ok, r.Error, r.Code);
            default:
                return LinkMessages.Result(n, false, "Unknown message type.");
        }
    }

    private bool Allow(string phoneId)
    {
        var now = _options.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var q = _rate.GetOrAdd(phoneId, _ => new Queue<long>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > 10_000) q.Dequeue();
            if (q.Count >= _options.MaxRequestsPer10Seconds) return false;
            q.Enqueue(now);
            return true;
        }
    }

    public ValueTask DisposeAsync() => _link.DisposeAsync();
}
