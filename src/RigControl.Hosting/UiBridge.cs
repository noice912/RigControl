using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;

namespace RigControl.Hosting;

/// <summary>The JSON texts a host sends to the UI besides answers to calls.</summary>
public static class UiMessages
{
    /// <summary>A pushed event: {"event": "sensors.snapshot", "data": ...}. Throws when the payload cannot be written as JSON.</summary>
    public static string Event(string topic, object? payload) => JsonDefaults.Serialize(new UiEvent(topic, payload));

    private sealed record UiEvent(string Event, object? Data);
}

/// <summary>
/// Connects the UI to the modules for a host whose window can only send and receive text
/// (the Linux/macOS window). Calls from the UI go to the dispatcher off the window's thread; answers and events go
/// back through <c>post</c>. The Windows app keeps its own WebView2 wiring and uses only <see cref="UiMessages"/>.
/// <c>post</c> may be called from any thread and must not throw for long; a failure is ignored (the window is closing).
/// </summary>
public sealed class UiBridge : IDisposable
{
    private readonly RpcDispatcher _dispatcher;
    private readonly EventBus _events;
    private readonly ILog _log;
    private readonly Action<string> _post;
    private int _closed;

    public UiBridge(RpcDispatcher dispatcher, EventBus events, ILog log, Action<string> post)
    {
        _dispatcher = dispatcher;
        _events = events;
        _log = log;
        _post = post;
        _events.Published += OnEvent;
    }

    /// <summary>Requests larger than this are refused unread; the UI never sends anything near it.</summary>
    public const int MaxRequestChars = 4 * 1024 * 1024;

    /// <summary>
    /// Handles one message from the UI. Returns at once; the answer is posted when the module is done.
    /// The host must only pass messages that came from its own page.
    /// </summary>
    public Task OnMessageAsync(string? requestJson)
    {
        if (Volatile.Read(ref _closed) != 0 || string.IsNullOrEmpty(requestJson) || requestJson.Length > MaxRequestChars)
            return Task.CompletedTask;
        // Off the window's thread: module code may block on hardware or the network.
        return Task.Run(async () =>
        {
            try
            {
                var response = await _dispatcher.HandleAsync(requestJson).ConfigureAwait(false);
                Post(response);
            }
            catch (Exception ex)
            {
                _log.Error("Handling a UI message failed.", ex);
            }
        });
    }

    private void OnEvent(string topic, object? payload)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        string json;
        try
        {
            json = UiMessages.Event(topic, payload);
        }
        catch (Exception ex)
        {
            _log.Error($"Event '{topic}' could not be serialised.", ex);
            return;
        }
        Post(json);
    }

    private void Post(string json)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { _post(json); }
        catch { /* the window may be closing; dropping a message is harmless */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _events.Published -= OnEvent;
    }
}
