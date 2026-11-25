using System.Text.Json;

namespace RigControl.Desktop;

/// <summary>
/// Watches the message channel for proof that the window works in both directions:
///  1. the page itself asked for "app.getInfo";
///  2. RigControl's answer was handed to the window;
///  3. the page said that this answer arrived (a short note it sends once it has it, see bridge.ts).
/// Only the third step shows that messages from RigControl really reach the page, so only then does it pass.
/// </summary>
internal sealed class SmokeProbe
{
    /// <summary>The note the page sends when an answer to app.getInfo has arrived: {"note":"answered","id":1}.</summary>
    public const string AnsweredNote = "answered";

    private readonly object _gate = new();
    private readonly HashSet<long> _asked = new();
    private readonly HashSet<long> _handed = new();
    private readonly HashSet<long> _arrived = new();
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _anyMessage;
    private bool _askedInfo;
    private string? _refusal;

    /// <summary>Completes when the page asked for app.getInfo, the answer was handed over and the page said it arrived.</summary>
    public Task Passed => _done.Task;

    /// <summary>
    /// A message arrived from RigControl's own page. True when it was a note for this class and not a call:
    /// a note is never passed on to the modules.
    /// </summary>
    public bool FromPage(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        lock (_gate) _anyMessage = true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("note", out var note))
            {
                if (note.ValueKind == JsonValueKind.String && note.GetString() == AnsweredNote &&
                    root.TryGetProperty("id", out var noteId) && noteId.ValueKind == JsonValueKind.Number && noteId.TryGetInt64(out var arrived))
                {
                    lock (_gate)
                    {
                        // Only an answer the page really asked for counts.
                        if (_asked.Contains(arrived)) _arrived.Add(arrived);
                    }
                    Settle(arrived);
                }
                return true;
            }
            if (!root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String || m.GetString() != "app.getInfo") return false;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var n)) return false;
            lock (_gate)
            {
                _askedInfo = true;
                _asked.Add(n);
            }
        }
        catch (JsonException)
        {
            // not ours
        }
        return false;
    }

    /// <summary>A message was handed to the window.</summary>
    public void ToPage(string? json)
    {
        if (string.IsNullOrEmpty(json) || _done.Task.IsCompleted) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var n)) return;
            bool ours;
            lock (_gate) ours = _asked.Contains(n);
            if (!ours) return;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                lock (_gate) _handed.Add(n);
                Settle(n);
                return;
            }
            lock (_gate) _refusal = root.TryGetProperty("error", out var e) ? e.ToString() : "no details";
        }
        catch (JsonException)
        {
            // not ours
        }
    }

    /// <summary>
    /// Passes once the same answer was both handed over and reported as arrived. The page's note can overtake
    /// the "handed over" report (they come from two threads), so either order counts.
    /// </summary>
    private void Settle(long id)
    {
        bool both;
        lock (_gate) both = _handed.Contains(id) && _arrived.Contains(id);
        if (both) _done.TrySetResult(true);
    }

    /// <summary>One sentence saying how far it got, for the log when time runs out.</summary>
    public string WhereItStopped(bool windowCreated)
    {
        lock (_gate)
        {
            if (_done.Task.IsCompleted) return "The page asked for app.getInfo and said the answer arrived.";
            if (!windowCreated) return "The window was never created.";
            if (!_anyMessage) return "The window opened but the page never sent a message: the page did not load, its script did not run, or the message channel does not work.";
            if (!_askedInfo) return "The page sent messages but never asked for app.getInfo.";
            if (_refusal is not null) return "The page asked for app.getInfo and RigControl answered with an error: " + _refusal;
            if (_handed.Count == 0) return "The page asked for app.getInfo but the answer was never handed to the window.";
            return "The answer to app.getInfo was handed to the window, but the page never said it arrived: messages from RigControl do not reach the page.";
        }
    }
}
