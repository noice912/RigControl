using System.Collections.Concurrent;
using RigControl.Core.Logging;

namespace RigControl.Desktop;

/// <summary>
/// Carries messages from RigControl to the page. Modules call <see cref="Post"/> from any thread and never wait:
/// one background thread hands the messages to the window in order. Nothing is sent before <see cref="Open"/>
/// (the page is not there yet) or after <see cref="Close"/> (the window is going away).
/// </summary>
internal sealed class MessagePump : IDisposable
{
    /// <summary>More waiting messages than this means the window has stopped taking them; new ones are dropped.</summary>
    public const int MaxWaiting = 4000;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Action<string> _send;
    private readonly Action<string>? _sent;
    private readonly ILog _log;
    private readonly Thread _thread;
    private volatile bool _open;
    private volatile bool _closed;
    private int _warned;

    /// <param name="send">Hands one message to the window. Called on the pump's own thread; may block.</param>
    /// <param name="log">Where trouble is written.</param>
    /// <param name="sent">Told after a message was handed over (the smoke check listens here).</param>
    public MessagePump(Action<string> send, ILog log, Action<string>? sent = null)
    {
        _send = send;
        _sent = sent;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "RigControl page messages" };
        _thread.Start();
    }

    /// <summary>The page is listening: messages are sent from now on.</summary>
    public void Open() => _open = true;

    public void Post(string json)
    {
        if (!_open || _closed) return;
        if (_queue.Count >= MaxWaiting)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                _log.Warn("The window is not taking messages. New ones are dropped until it does.");
            return;
        }
        try { _queue.Add(json); }
        catch (InvalidOperationException) { /* closed meanwhile */ }
    }

    private void Run()
    {
        try
        {
            foreach (var json in _queue.GetConsumingEnumerable())
            {
                if (_closed) return;
                try
                {
                    _send(json);
                    Volatile.Write(ref _warned, 0);
                    _sent?.Invoke(json);
                }
                catch (Exception ex)
                {
                    if (_closed) return;
                    _log.Warn("A message could not be handed to the window.", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // closed
        }
    }

    /// <summary>Stops sending at once. Messages still waiting are dropped. Safe to call more than once.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _queue.CompleteAdding(); } catch { /* already */ }
    }

    public void Dispose() => Close();
}
