using System.Text.Json.Nodes;

namespace RigControl.Presence;

/// <summary>
/// Sends one card's updates as fast as Discord allows. Discord's client takes 5 activity updates per 20 seconds on
/// one connection, so each connection gets one of these.
///
/// Rules:
///  - A change goes out at once while one of the 5 places is free.
///  - When none is free, only the newest state is kept; it goes out the moment a place frees. Older ones are never
///    queued up behind it, and the last state always arrives.
///  - Taking a card down counts as a state like any other, so it is never lost.
///  - The same state is not sent twice in a row, except as a keep-alive (the caller says so), and a keep-alive that
///    finds no free place is simply skipped.
///
/// Not thread safe: the engine calls it from its one loop.
/// </summary>
internal sealed class ActivityLimiter
{
    /// <summary>Discord's limit for one connection.</summary>
    public const int MaxSends = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    /// <summary>Discord counts by its own clock when an update arrives, so a place is given back a little late.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(500);

    public enum Result
    {
        /// <summary>Sent now.</summary>
        Sent,
        /// <summary>No free place: kept, and sent when one frees.</summary>
        Waiting,
        /// <summary>Discord already shows exactly this; nothing to do.</summary>
        Same,
        /// <summary>A keep-alive with no free place: left out.</summary>
        Skipped,
    }

    private readonly Func<DateTime> _now;
    private readonly Func<JsonObject?, string?> _send;
    private readonly Queue<DateTime> _sent = new();
    /// <summary>What Discord shows on this connection: the activity as text, "" for nothing, null when unknown.</summary>
    private string? _shown = "";
    private bool _waiting;
    private JsonObject? _pending;
    private string _pendingText = "";

    /// <param name="send">Sends SET_ACTIVITY (null takes the card down) and returns Discord's error, or null.</param>
    public ActivityLimiter(Func<DateTime> now, Func<JsonObject?, string?> send)
    {
        _now = now;
        _send = send;
    }

    /// <summary>Discord's answer to the last update that was sent: its error message, or null when accepted.</summary>
    public string? LastError { get; private set; }

    /// <summary>True while a state waits for a free place.</summary>
    public bool Pending => _waiting;

    /// <summary>When the waiting state can go out (now, if a place is already free); null when nothing waits.</summary>
    public DateTime? NextSlot
    {
        get
        {
            if (!_waiting) return null;
            var now = _now();
            return Free(now) ? now : _sent.Peek() + Window + Margin;
        }
    }

    private bool Free(DateTime now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= Window + Margin) _sent.Dequeue();
        return _sent.Count < MaxSends;
    }

    /// <summary>The newest state for this card; null takes the card down.</summary>
    public Result Offer(JsonObject? activity, bool keepAlive = false)
    {
        var text = activity?.ToJsonString() ?? "";
        if (!keepAlive && text == _shown)
        {
            // Back to what Discord already shows: whatever was waiting is out of date.
            Forget();
            return Result.Same;
        }
        if (Free(_now()))
        {
            Send(activity, text);
            return Result.Sent;
        }
        if (keepAlive && !_waiting) return Result.Skipped;
        _waiting = true;
        _pending = activity;
        _pendingText = text;
        return Result.Waiting;
    }

    /// <summary>Sends the waiting state if a place is free now. True when it was sent.</summary>
    public bool Flush()
    {
        if (!_waiting || !Free(_now())) return false;
        Send(_pending, _pendingText);
        return true;
    }

    /// <summary>
    /// The connection is about to be closed. Closing it takes the card down by itself, so the card is only cleared by
    /// hand when a place is free; nothing is kept waiting.
    /// </summary>
    public void ClearBeforeClosing()
    {
        Forget();
        if (_shown == "" || !Free(_now())) return;
        try { Send(null, ""); }
        catch { /* closing anyway */ }
    }

    /// <summary>A new connection: nothing is shown on it yet and its count starts again.</summary>
    public void Reset()
    {
        Forget();
        _sent.Clear();
        _shown = "";
        LastError = null;
    }

    private void Forget()
    {
        _waiting = false;
        _pending = null;
        _pendingText = "";
    }

    private void Send(JsonObject? activity, string text)
    {
        Forget();
        _sent.Enqueue(_now()); // counted even if it fails: it may have reached Discord
        _shown = null;
        LastError = _send(activity);
        _shown = text;
    }
}
