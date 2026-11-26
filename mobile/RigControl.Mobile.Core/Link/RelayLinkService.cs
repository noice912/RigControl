using System.Text.Json.Nodes;
using Lib = global::RigControl.Link;

namespace RigControl.Mobile.Core.Link;

/// <summary>Settings for <see cref="RelayLinkService"/>; tests shorten the times.</summary>
public sealed class RelayLinkServiceOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Reconnect pauses and handshake time of each relay connection (RigControl.Link's defaults: 1 s doubling to 60 s).</summary>
    public Lib.RelayLinkOptions Relay { get; init; } = new();

    /// <summary>How long to wait for a PC's answer.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long <see cref="RelayLinkService.ConnectAsync"/> waits for the relay before returning anyway.</summary>
    public TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How often <see cref="RelayLinkService.WatchAsync"/> asks each PC whether it is there. The relay tells only the
    /// PC about presence, so the phone learns that a PC is online by asking it (a snapshot); a PC that answered
    /// recently is not asked again.
    /// </summary>
    public TimeSpan ProbeEvery { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan PairTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Counters and last-seen times are written at most this often while connected (and always on disconnect).</summary>
    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(30);

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The real link to the PCs, on top of RigControl.Link (docs/protocols/link.md): pairing on /v1/pair, one relay
/// connection per PC (PhoneSide, which reconnects by itself with growing pauses), sealed snapshot and action requests,
/// and online / last-seen tracking. The phone makes a new key pair for every PC it links, so the relay can't tell that
/// two PCs belong to the same phone; the private keys go to <see cref="ILinkStore.SaveKeyAsync"/> (Keychain /
/// Keystore), the rest of the list (public keys, epoch, relay, counters, last seen) to ordinary storage.
/// </summary>
public sealed class RelayLinkService : ILinkService, IAsyncDisposable
{
    private readonly Lib.IRelayTransport _transport;
    private readonly ILinkStore _store;
    private readonly RelayLinkServiceOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<string, Conn> _conns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);
    private List<StoredPc>? _pcs;
    private bool _dirty;
    private DateTimeOffset _lastSave;

    public event EventHandler<LinkEvent>? EventReceived;

    public RelayLinkService(Lib.IRelayTransport transport, ILinkStore store, RelayLinkServiceOptions? options = null)
    {
        _transport = transport;
        _store = store;
        _options = options ?? new RelayLinkServiceOptions();
    }

    /// <summary>One shared relay connection to a PC.</summary>
    private sealed class Conn
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required Lib.PhoneSide Side { get; init; }
        public required Lib.DeviceKeys Keys { get; init; }
        public CancellationTokenSource Life { get; } = new();
        /// <summary>
        /// One request at a time: each is sealed with the next counter before it is sent, so two at once could reach
        /// the PC in the other order, and the PC refuses the older counter as a replay (it would never answer).
        /// </summary>
        public SemaphoreSlim Sending { get; } = new(1, 1);
        public int Users;
        public bool RelayOnline;
        public bool Answering;
        /// <summary>The online state last raised as <see cref="PcPresenceChanged"/>.</summary>
        public bool Reported;
        public DateTimeOffset? LastAnswer;
        /// <summary>"denied" / "removed" from the relay, kept until it accepts this phone again.</summary>
        public string? Refused;
        public int Probing;
        public bool Closed;
    }

    private DateTimeOffset Now => _options.Time.GetUtcNow();

    // ---------------------------------------------------------------- pairing

    public async Task<PairResult> PairAsync(PairingRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Relay?.Trim() ?? "";
        if (MobileDefaults.IsPlaceholder(host))
            return PairResult.Fail(MobileDefaults.RelayNotSetMessage);
        // Only wss:// relays: a bare host (as QR codes carry it) means wss; ws:// (even to this device) is refused.
        var relay = Lib.LinkEndpoints.ParseRelay(host);
        if (relay is not { Scheme: "wss" })
            return PairResult.Fail(LinkErrors.ForPairing("badrelay", host));

        progress?.Report($"Contacting the relay at {host} and exchanging keys with the PC...");
        using var keys = Lib.DeviceKeys.Create();
        Lib.LinkedPc pc;
        try
        {
            pc = await Lib.Pairing.ClaimAsync(_transport, relay, request.Code.Value, keys, request.PhoneName, cancellationToken, _options.PairTimeout)
                .ConfigureAwait(false);
        }
        catch (Lib.LinkException ex)
        {
            Log($"Pairing failed: {ex.Code}");
            return PairResult.Fail(LinkErrors.ForPairing(ex.Code, host));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (LinkErrors.IsNetwork(ex))
        {
            Log("Pairing: relay unreachable: " + ex.Message);
            return PairResult.Fail(LinkErrors.Unreachable(host));
        }

        var now = Now;
        var stored = new StoredPc
        {
            Id = pc.Id,
            Name = pc.Name,
            SigningKey = pc.SigningKey,
            AgreementKey = pc.AgreementKey,
            Relay = pc.Relay,
            Epoch = pc.Epoch,
            PhoneId = keys.Id,
            LastIn = 0,
            LinkedAt = now,
            LastSeen = now,
        };
        var secret = keys.ExportPrivate();
        var existing = FindConflict(await PcsAsync().ConfigureAwait(false), stored);
        if (existing is not null)
        {
            // Same PC id as a linked PC. With other keys or another relay it is a QR code (or relay) claiming to be
            // that PC; with the same keys it could still change the stored name, epoch and phone id (and break a
            // working link), so it is always asked. Nothing changes until the person confirms; the new keys wait in
            // memory only, for at most PendingLifetime.
            var id = Lib.B64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            Pending? dropped;
            lock (_gate)
            {
                dropped = _pending;
                _pending = new Pending(id, stored, secret, now);
            }
            dropped?.Clear();
            Log(existing.Value.SameKeys
                ? "Pairing: this PC is already linked; waiting for the person to confirm linking it again."
                : "Pairing: the PC's id is already linked with different keys; waiting for the person to confirm.");
            progress?.Report("Waiting for your answer...");
            return PairResult.NeedsReplace(id, existing.Value.SameKeys
                ? $"{existing.Value.Name} is already linked. Link it again? Its old link on this phone is replaced."
                : $"This QR claims to be {existing.Value.Name}, which is already linked with different keys. Replace it?");
        }
        return await StoreLinkAsync(stored, secret, progress).ConfigureAwait(false);
    }

    /// <summary>A pairing that would replace a linked PC's keys, waiting for the person's answer.</summary>
    private sealed class Pending(string id, StoredPc pc, byte[] secret, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public StoredPc Pc { get; } = pc;
        public byte[] Secret { get; } = secret;

        public void Clear() => Array.Clear(Secret);
    }

    private Pending? _pending;

    /// <summary>How long a pairing waits for the person's answer.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The linked PC <paramref name="stored"/> would replace (its name, and whether keys and relay are the same), else
    /// null for a new PC.
    /// </summary>
    private (string Name, bool SameKeys)? FindConflict(List<StoredPc> list, StoredPc stored)
    {
        lock (_gate)
        {
            var old = list.FirstOrDefault(p => p.Id == stored.Id);
            if (old is null)
                return null;
            var same = old.AgreementKey == stored.AgreementKey && old.SigningKey == stored.SigningKey
                && string.Equals(old.RelayHost, stored.RelayHost, StringComparison.OrdinalIgnoreCase);
            return (old.Name, same);
        }
    }

    public async Task<PairResult> ConfirmReplaceAsync(string replaceId, CancellationToken cancellationToken = default)
    {
        Pending? p;
        lock (_gate)
        {
            p = _pending is { } x && x.Id == replaceId ? x : null;
            if (p is not null)
                _pending = null;
        }
        if (p is not null && Now - p.CreatedAt > PendingLifetime)
        {
            p.Clear();
            p = null;
        }
        if (p is null)
            return PairResult.Fail("Nothing is waiting to be replaced any more. Show a new code on the PC and try again.");
        Log("Pairing: the person chose to replace the linked PC's keys.");
        return await StoreLinkAsync(p.Pc, p.Secret, null).ConfigureAwait(false);
    }

    public void DiscardReplace(string replaceId)
    {
        Pending? p;
        lock (_gate)
        {
            p = _pending is { } x && x.Id == replaceId ? x : null;
            if (p is not null)
                _pending = null;
        }
        p?.Clear();
    }

    /// <summary>Stores the keys and puts <paramref name="stored"/> in the list (replacing an entry with its id). Clears <paramref name="secret"/>.</summary>
    private async Task<PairResult> StoreLinkAsync(StoredPc stored, byte[] secret, IProgress<string>? progress)
    {
        progress?.Report($"Saving the keys for {stored.Name} on this phone...");
        try
        {
            await _store.SaveKeyAsync(stored.Id, secret).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("Pairing: storing the keys failed: " + ex.Message);
            return PairResult.Fail("The PC answered, but this phone couldn't store the keys safely. Show a new code on the PC and try again.");
        }
        finally
        {
            Array.Clear(secret);
        }

        await PcsAsync().ConfigureAwait(false);
        Conn? stale;
        lock (_gate)
        {
            // Linking the same PC again (it was removed on the PC, say, or the person confirmed new keys) replaces the
            // old link and its keys.
            _pcs!.RemoveAll(p => p.Id == stored.Id);
            _pcs.Add(stored);
            if (_conns.Remove(stored.Id, out stale))
                stale.Closed = true;
            _problems.Remove(stored.Id);
            _dirty = true;
        }
        if (stale is not null)
            await CloseAsync(stale, save: false).ConfigureAwait(false);
        await SaveAsync().ConfigureAwait(false);
        progress?.Report($"Linked to {stored.Name}.");
        lock (_gate)
            return PairResult.Success(ToLinkedPc(stored));
    }
    // ---------------------------------------------------------------- the list

    public async Task<IReadOnlyList<LinkedPc>> GetPcsAsync(CancellationToken cancellationToken = default)
    {
        var pcs = await PcsAsync().ConfigureAwait(false);
        lock (_gate)
            return pcs.Select(ToLinkedPc).ToArray();
    }

    /// <summary>Call with <see cref="_gate"/> held.</summary>
    private LinkedPc ToLinkedPc(StoredPc p)
    {
        _conns.TryGetValue(p.Id, out var c);
        var problem = _problems.GetValueOrDefault(p.Id);
        if (problem is null && c?.Refused is { } refused)
            problem = LinkErrors.ForConnection(refused);
        return new LinkedPc(p.Id, p.Name, p.RelayHost, c?.Reported ?? false, p.LastSeen, problem);
    }

    private async Task<List<StoredPc>> PcsAsync()
    {
        lock (_gate)
        {
            if (_pcs is not null)
                return _pcs;
        }
        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_pcs is not null)
                    return _pcs;
            }
            string? json = null;
            try
            {
                json = await _store.LoadListAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Reading the list of PCs failed: " + ex.Message);
            }
            var list = StoredPc.Deserialize(json);
            lock (_gate)
                return _pcs ??= list;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string json;
            lock (_gate)
            {
                if (_pcs is null)
                    return;
                json = StoredPc.Serialize(_pcs);
                _dirty = false;
                _lastSave = Now;
            }
            await _store.SaveListAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("Saving the list of PCs failed: " + ex.Message);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private Task MaybeSaveAsync()
    {
        lock (_gate)
        {
            if (!_dirty || Now - _lastSave < _options.SaveEvery)
                return Task.CompletedTask;
        }
        return SaveAsync();
    }

    public async Task ForgetAsync(string pcId)
    {
        await PcsAsync().ConfigureAwait(false);
        Conn? c;
        lock (_gate)
        {
            _pcs!.RemoveAll(p => p.Id == pcId);
            if (_conns.Remove(pcId, out c))
                c.Closed = true;
            _problems.Remove(pcId);
            _dirty = true;
        }
        if (c is not null)
            await CloseAsync(c, save: false).ConfigureAwait(false);
        try
        {
            await _store.DeleteKeyAsync(pcId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("Deleting the keys failed: " + ex.Message);
        }
        await SaveAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- connections

    public async Task ConnectAsync(string pcId, CancellationToken cancellationToken = default)
    {
        var c = await AttachAsync(pcId).ConfigureAwait(false);
        if (c is null)
        {
            string? problem;
            lock (_gate)
                problem = _problems.GetValueOrDefault(pcId);
            throw new LinkUnavailableException(problem ?? "This PC can't be reached from this phone.");
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_options.ConnectWait);
        try
        {
            await c.Side.ConnectAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The relay isn't reachable yet: the connection keeps trying; requests say why they fail meanwhile.
        }
        catch
        {
            // Cancelled by the caller: give the connection back, so a cancelled connect needs no disconnect.
            await DetachAsync(c).ConfigureAwait(false);
            throw;
        }
    }

    public Task DisconnectAsync(string pcId)
    {
        Conn? c;
        lock (_gate)
            _conns.TryGetValue(pcId, out c);
        return c is null ? Task.CompletedTask : DetachAsync(c);
    }

    /// <summary>Takes a share of the PC's connection, making and starting it when there is none. Null when it can't be made.</summary>
    private async Task<Conn?> AttachAsync(string pcId)
    {
        var pcs = await PcsAsync().ConfigureAwait(false);
        StoredPc pc;
        lock (_gate)
        {
            if (_conns.TryGetValue(pcId, out var existing))
            {
                existing.Users++;
                return existing;
            }
            pc = pcs.FirstOrDefault(p => p.Id == pcId)?.Copy() ?? throw new KeyNotFoundException("That PC is not linked to this phone.");
        }

        Lib.DeviceKeys keys;
        try
        {
            var secret = await _store.LoadKeyAsync(pcId).ConfigureAwait(false) ?? throw new Lib.LinkException("badkey", "No keys stored.");
            try
            {
                keys = Lib.DeviceKeys.ImportPrivate(secret);
            }
            finally
            {
                Array.Clear(secret);
            }
        }
        catch (Exception ex)
        {
            Log($"The keys for a PC could not be read: {ex.Message}");
            lock (_gate)
                _problems[pcId] = "This phone's keys for this PC are missing (for example after the phone was restored from a backup). Forget the PC here and link it again.";
            return null;
        }

        Conn created;
        lock (_gate)
        {
            if (_conns.TryGetValue(pcId, out var raced))
            {
                raced.Users++;
                keys.Dispose();
                return raced;
            }
            if (!_pcs!.Any(p => p.Id == pcId))
            {
                keys.Dispose();
                throw new KeyNotFoundException("That PC is not linked to this phone.");
            }
            Lib.PhoneSide side;
            try
            {
                side = new Lib.PhoneSide(_transport, pc.ToLibrary(), keys, pc.LastIn, new Lib.PhoneSideOptions
                {
                    Relay = _options.Relay,
                    Time = _options.Time,
                    ReplyTimeout = _options.ReplyTimeout,
                });
            }
            catch (Exception ex)
            {
                keys.Dispose();
                Log("A stored PC could not be used: " + ex.Message);
                _problems[pcId] = "The link to this PC is damaged on this phone. Forget the PC here and link it again.";
                return null;
            }
            _problems.Remove(pcId);
            created = new Conn { Id = pc.Id, Name = pc.Name, Side = side, Keys = keys, Users = 1 };
            _conns[pcId] = created;
        }
        created.Side.StateChanged += () => OnStateChanged(created);
        created.Side.EventReceived += m => OnPcEvent(created, m);
        // Starts the connection loop (it reconnects by itself) and waits in the background until it is up.
        _ = created.Side.ConnectAsync(created.Life.Token).ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return created;
    }

    private async Task DetachAsync(Conn c)
    {
        bool close;
        lock (_gate)
        {
            close = !c.Closed && _conns.TryGetValue(c.Id, out var current) && current == c && --c.Users <= 0;
            if (close)
            {
                _conns.Remove(c.Id);
                c.Closed = true;
            }
        }
        if (close)
            await CloseAsync(c, save: true).ConfigureAwait(false);
    }

    private async Task CloseAsync(Conn c, bool save)
    {
        try { c.Life.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            await c.Side.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("Closing a connection failed: " + ex.Message);
        }
        KeepCounters(c);
        c.Keys.Dispose();
        c.Life.Dispose();
        if (save)
            await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Copies the connection's last accepted counter into the stored list.</summary>
    private void KeepCounters(Conn c)
    {
        lock (_gate)
        {
            var p = _pcs?.FirstOrDefault(x => x.Id == c.Id);
            var lastIn = c.Side.LastIn;
            if (p is not null && lastIn > p.LastIn)
            {
                p.LastIn = lastIn;
                _dirty = true;
            }
        }
    }

    // ---------------------------------------------------------------- online state

    private void OnStateChanged(Conn c)
    {
        var online = c.Side.State == Lib.LinkConnectionState.Online;
        var error = c.Side.LastError;
        bool probe;
        lock (_gate)
        {
            if (c.Closed)
                return;
            if (online)
                c.Refused = null;
            else if (error is "denied" or "removed")
                c.Refused = error;
            probe = online && !c.RelayOnline;
            c.RelayOnline = online;
            if (!online)
                c.Answering = false;
        }
        RaisePresenceIfChanged(c);
        // The relay says nothing to a phone about its PC, so ask the PC whether it is there.
        if (probe)
            _ = ProbeAsync(c, force: true, CancellationToken.None);
    }

    private void OnPcEvent(Conn c, JsonObject message)
    {
        MarkAnswered(c);
        var kind = SnapshotReader.Text(message["kind"], 48) ?? "event";
        var text = SnapshotReader.Text(message["text"], 200) ?? kind;
        Raise(new PcNotice(c.Id, kind, text));
    }

    private void MarkAnswered(Conn c)
    {
        var now = Now;
        lock (_gate)
        {
            if (c.Closed)
                return;
            c.RelayOnline = c.Side.State == Lib.LinkConnectionState.Online;
            c.Answering = true;
            c.LastAnswer = now;
            var p = _pcs?.FirstOrDefault(x => x.Id == c.Id);
            if (p is not null)
            {
                p.LastSeen = now;
                _dirty = true;
            }
        }
        KeepCounters(c);
        RaisePresenceIfChanged(c);
    }

    private void MarkNotAnswering(Conn c)
    {
        lock (_gate)
        {
            if (c.Closed)
                return;
            c.Answering = false;
        }
        RaisePresenceIfChanged(c);
    }

    private void RaisePresenceIfChanged(Conn c)
    {
        PcPresenceChanged? e = null;
        lock (_gate)
        {
            if (c.Closed)
                return;
            var online = c.RelayOnline && c.Answering;
            if (online != c.Reported)
            {
                c.Reported = online;
                e = new PcPresenceChanged(c.Id, online, online ? Now : c.LastAnswer ?? Now);
            }
        }
        if (e is not null)
            Raise(e);
    }

    /// <summary>Asks the PC for a snapshot to learn whether it is there (raises <see cref="PcSnapshotReceived"/> when it answers).</summary>
    private async Task ProbeAsync(Conn c, bool force, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref c.Probing, 1) == 1)
            return;
        try
        {
            CancellationToken life;
            lock (_gate)
            {
                if (c.Closed)
                    return;
                if (!force && c.Answering && c.LastAnswer is { } last && Now - last < _options.ProbeEvery / 2)
                    return;
                life = c.Life.Token;
            }
            if (c.Side.State != Lib.LinkConnectionState.Online)
                return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, life);
            if (!await c.Sending.WaitAsync(0, cts.Token).ConfigureAwait(false))
                return; // a request is running anyway, and its answer says the same
            JsonObject o;
            try
            {
                o = await c.Side.RequestSnapshotAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                c.Sending.Release();
            }
            MarkAnswered(c);
            Raise(new PcSnapshotReceived(c.Id, SnapshotReader.Read(o, Now)));
        }
        catch (Lib.LinkException ex)
        {
            if (ex.Code is "refused" or "stale" or "busy")
                MarkAnswered(c);
            else if (ex.Code is "pcoffline" or "timeout")
                MarkNotAnswering(c);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log("Checking a PC failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref c.Probing, 0);
        }
    }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        var mine = new Dictionary<string, Conn>(StringComparer.Ordinal);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var (id, c) in mine.ToList())
                {
                    if (c.Closed)
                        mine.Remove(id); // forgotten or linked again: the new link is picked up below
                }
                var pcs = await PcsAsync().ConfigureAwait(false);
                string[] ids;
                lock (_gate)
                    ids = pcs.Select(p => p.Id).ToArray();
                foreach (var id in ids)
                {
                    if (mine.ContainsKey(id))
                        continue;
                    try
                    {
                        if (await AttachAsync(id).ConfigureAwait(false) is { } c)
                            mine[id] = c;
                    }
                    catch (KeyNotFoundException)
                    {
                    }
                }
                await Task.WhenAll(mine.Values.Select(c => ProbeAsync(c, force: false, cancellationToken))).ConfigureAwait(false);
                await MaybeSaveAsync().ConfigureAwait(false);
                await Task.Delay(_options.ProbeEvery, _options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var c in mine.Values)
                await DetachAsync(c).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- requests

    public async Task<PcSnapshot> GetSnapshotAsync(string pcId, CancellationToken cancellationToken = default)
    {
        var o = await RequestAsync(pcId, static (side, ct) => side.RequestSnapshotAsync(ct), cancellationToken).ConfigureAwait(false);
        return SnapshotReader.Read(o, Now);
    }

    public async Task<ActionResult> DoAsync(string pcId, PcAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            var r = await RequestAsync(pcId, (side, ct) => side.SendActionAsync(action.Name, action.ToJsonArgs(), ct), cancellationToken)
                .ConfigureAwait(false);
            return r.Ok ? ActionResult.Success : ActionResult.Fail(LinkErrors.ForRefusal(r.Code) ?? r.Error ?? "The PC refused.");
        }
        catch (LinkUnavailableException ex)
        {
            return ActionResult.Fail(ex.Message);
        }
    }

    private async Task<T> RequestAsync<T>(string pcId, Func<Lib.PhoneSide, CancellationToken, Task<T>> send, CancellationToken ct)
    {
        Conn? c;
        lock (_gate)
        {
            if (!_conns.TryGetValue(pcId, out c))
                throw new LinkUnavailableException(_problems.GetValueOrDefault(pcId) ?? "Not connected to that PC.");
        }
        if (c.Side.State != Lib.LinkConnectionState.Online)
            throw new LinkUnavailableException(LinkErrors.ForConnection(c.Refused ?? c.Side.LastError) ?? "Connecting to the relay...");
        try
        {
            T result;
            await c.Sending.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                result = await send(c.Side, ct).ConfigureAwait(false);
            }
            finally
            {
                c.Sending.Release();
            }
            MarkAnswered(c);
            await MaybeSaveAsync().ConfigureAwait(false);
            return result;
        }
        catch (Lib.LinkException ex)
        {
            if (ex.Code is "refused" or "stale" or "busy")
                MarkAnswered(c); // the PC answered, if only to refuse
            else if (ex.Code is "pcoffline" or "timeout")
                MarkNotAnswering(c);
            var text = ex.Code == "offline" ? LinkErrors.ForConnection(c.Side.LastError) ?? LinkErrors.ForRequest(ex.Code, c.Name)
                : LinkErrors.ForRequest(ex.Code, c.Name, ex.Code == "refused" ? ex.Message : null);
            throw new LinkUnavailableException(text, ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new LinkUnavailableException("The connection to the PC was closed.", ex);
        }
    }

    // ---------------------------------------------------------------- the rest

    private void Raise(LinkEvent e)
    {
        try
        {
            EventReceived?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            Log("A link event listener failed: " + ex.Message);
        }
    }

    private void Log(string text) => _options.Log?.Invoke(text);

    public async ValueTask DisposeAsync()
    {
        List<Conn> all;
        lock (_gate)
        {
            _pending?.Clear();
            _pending = null;
            all = _conns.Values.ToList();
            foreach (var c in all)
                c.Closed = true;
            _conns.Clear();
        }
        foreach (var c in all)
            await CloseAsync(c, save: false).ConfigureAwait(false);
        await SaveAsync().ConfigureAwait(false);
    }
}
