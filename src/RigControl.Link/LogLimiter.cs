namespace RigControl.Link;

/// <summary>
/// Keeps a flood of the same kind of log line (messages from strangers, refused frames, relay errors) from filling the
/// log: the first <c>perMinute</c> lines of a kind in a minute are written, then one "counted, not logged" line, and
/// when the next minute starts a line says how many were left out. Thread-safe.
/// </summary>
internal sealed class LogLimiter
{
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly int _perMinute;
    private readonly Dictionary<string, (long Start, int Count)> _kinds = new(StringComparer.Ordinal);

    public LogLimiter(Action<string>? log, TimeProvider time, int perMinute = 5)
    {
        _log = log;
        _time = time;
        _perMinute = perMinute;
    }

    public void Log(string kind, string text)
    {
        if (_log is null) return;
        var lines = new List<string>(2);
        lock (_kinds)
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            if (!_kinds.TryGetValue(kind, out var e) || now - e.Start >= 60_000)
            {
                if (e.Count > _perMinute) lines.Add($"{e.Count - _perMinute} more \"{kind}\" lines in the last minute were not logged.");
                e = (now, 0);
            }
            e.Count++;
            _kinds[kind] = e;
            if (e.Count <= _perMinute) lines.Add(text);
            else if (e.Count == _perMinute + 1) lines.Add($"More \"{kind}\" lines this minute are counted, not logged.");
        }
        foreach (var l in lines) _log(l);
    }
}
