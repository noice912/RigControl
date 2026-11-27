using System.Text.Json.Nodes;
using RigControl.Mobile.Core.Link;

namespace RigControl.Mobile.Core.Music;

/// <summary>The answer of one PC to an action sent to every linked PC.</summary>
public sealed record PcSendResult(LinkedPc Pc, ActionResult Result);

/// <summary>Sends one action to every PC this phone is linked to (connecting to each for the request).</summary>
public static class LinkedPcs
{
    public static async Task<IReadOnlyList<PcSendResult>> SendToAllAsync(ILinkService link, PcAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(action);
        var pcs = await link.GetPcsAsync(cancellationToken).ConfigureAwait(false);
        var tasks = pcs.Where(p => p.Problem is null).Select(async pc =>
        {
            try
            {
                await link.ConnectAsync(pc.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new PcSendResult(pc, ActionResult.Fail("Couldn't connect to the PC."));
            }
            try
            {
                return new PcSendResult(pc, await link.DoAsync(pc.Id, action, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new PcSendResult(pc, ActionResult.Fail("The PC didn't answer."));
            }
            finally
            {
                await link.DisconnectAsync(pc.Id).ConfigureAwait(false);
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

/// <summary>
/// Feeds presence.phoneMusic to every linked PC while sharing is on: <see cref="ReportAsync"/> is called with what
/// the Music app plays (every few seconds); a change is sent at once, an unchanged song every 15 s so the PC knows
/// the phone is still there, and nothing playing is sent once as <c>{}</c>. <see cref="StopAsync"/> (switch off, app
/// to the background) sends <c>{}</c> and lets go of the connections it held.
/// </summary>
public sealed class PhoneMusicFeeder : IAsyncDisposable
{
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(15);

    /// <summary>A seek of more than this (against where the song should be by now) counts as a change.</summary>
    public const double SeekSeconds = 5;

    private readonly ILinkService _link;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly HashSet<string> _connected = new(StringComparer.Ordinal);

    private JsonObject? _last; // null = nothing sent yet
    private DateTimeOffset _lastAt;

    public PhoneMusicFeeder(ILinkService link, TimeProvider? time = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after each send with the PCs' answers (for a status line); never carries the song.</summary>
    public event EventHandler<IReadOnlyList<PcSendResult>>? Sent;

    /// <summary>What the Music app plays now (null = nothing). Sends when needed; returns whether it sent.</summary>
    public async Task<bool> ReportAsync(PhoneMusicTrack? track, CancellationToken cancellationToken = default)
    {
        var payload = PhoneMusicPayload.Build(track);
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!ShouldSend(payload, now))
                return false;
            await SendAsync(payload, now, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Sharing stopped: tells the PCs nothing plays (if they were told something did) and disconnects.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last is { Count: > 0 })
                await SendAsync(new JsonObject(), _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _last = null;
            foreach (var id in _connected.ToArray())
                await Release(id).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    private bool ShouldSend(JsonObject payload, DateTimeOffset now)
    {
        if (_last is null)
            return true;
        if (payload.Count == 0)
            return _last.Count != 0; // "nothing" is said once
        if (_last.Count == 0 || !SameSong(payload, _last))
            return true;
        if (now - _lastAt >= Refresh)
            return true;
        // A seek: compare with where the song should be by now.
        var elapsed = (now - _lastAt).TotalSeconds;
        var expected = Num(_last, "position") + (_last["playing"]?.GetValue<bool>() == true ? elapsed : 0);
        return Math.Abs(Num(payload, "position") - expected) > SeekSeconds;
    }

    private static bool SameSong(JsonObject a, JsonObject b) =>
        Text(a, "title") == Text(b, "title") && Text(a, "artist") == Text(b, "artist") && Text(a, "album") == Text(b, "album")
        && a["playing"]?.GetValue<bool>() == b["playing"]?.GetValue<bool>() && Num(a, "duration") == Num(b, "duration");

    private static string? Text(JsonObject o, string key) => o[key]?.GetValue<string>();
    private static double Num(JsonObject o, string key) => o[key]?.GetValue<double>() ?? 0;

    private async Task SendAsync(JsonObject payload, DateTimeOffset now, CancellationToken ct)
    {
        var pcs = await _link.GetPcsAsync(ct).ConfigureAwait(false);
        var usable = pcs.Where(p => p.Problem is null).ToArray();
        foreach (var gone in _connected.Where(id => usable.All(p => p.Id != id)).ToArray())
            await Release(gone).ConfigureAwait(false);

        // Connections are held while sharing is on (one ConnectAsync each, given back in StopAsync).
        var fresh = usable.Where(p => !_connected.Contains(p.Id)).ToArray();
        var connected = await Task.WhenAll(fresh.Select(async pc =>
        {
            try
            {
                await _link.ConnectAsync(pc.Id, ct).ConfigureAwait(false);
                return pc.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        })).ConfigureAwait(false);
        foreach (var id in connected)
            if (id is not null)
                _connected.Add(id);

        var action = PcAction.PhoneMusic(payload);
        var results = await Task.WhenAll(usable.Select(async pc =>
        {
            try
            {
                if (!_connected.Contains(pc.Id))
                    return new PcSendResult(pc, ActionResult.Fail("Couldn't connect to the PC."));
                return new PcSendResult(pc, await _link.DoAsync(pc.Id, action, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new PcSendResult(pc, ActionResult.Fail("The PC didn't answer."));
            }
        })).ConfigureAwait(false);

        _last = payload;
        _lastAt = now;
        Sent?.Invoke(this, results);
    }

    private async Task Release(string pcId)
    {
        _connected.Remove(pcId);
        try
        {
            // Bounded: a stalled relay must not hold up going to the background.
            await _link.DisconnectAsync(pcId).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // already gone
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _turn.Dispose();
    }
}
