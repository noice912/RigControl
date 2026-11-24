using System.Security.Cryptography;

namespace AudioController.App.Services;

/// <summary>
/// Small in-memory LRU of decrypted sound bytes so repeat plays do not re-read and re-decrypt the
/// vault. Bounded by entry count and total bytes. Contents live in memory only and are never
/// written anywhere. Thread-safe.
/// </summary>
public sealed class SoundCache
{
    private readonly object _sync = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly LinkedList<KeyValuePair<Guid, byte[]>> _order = new();
    private readonly Dictionary<Guid, LinkedListNode<KeyValuePair<Guid, byte[]>>> _map = new();
    private long _bytes;

    public SoundCache(int maxEntries = 32, long maxBytes = 256L * 1024 * 1024)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
    }

    public bool TryGet(Guid soundId, out byte[] data)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(soundId, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                data = node.Value.Value;
                return true;
            }
        }

        data = Array.Empty<byte>();
        return false;
    }

    public void Put(Guid soundId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            if (_map.TryGetValue(soundId, out var existing))
            {
                _order.Remove(existing);
                _bytes -= existing.Value.Value.LongLength;
                _map.Remove(soundId);
            }

            var node = new LinkedListNode<KeyValuePair<Guid, byte[]>>(new(soundId, data));
            _order.AddFirst(node);
            _map[soundId] = node;
            _bytes += data.LongLength;

            // Evict least-recently-used entries. Evicted arrays are NOT wiped: a playing sound may still
            // be reading from one; the GC reclaims them once playback releases the reference.
            while ((_map.Count > _maxEntries || _bytes > _maxBytes) && _order.Last is { } last && last != node)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Value.LongLength;
            }
        }
    }

    public void Remove(Guid soundId)
    {
        lock (_sync)
        {
            if (_map.Remove(soundId, out var node))
            {
                _order.Remove(node);
                _bytes -= node.Value.Value.LongLength;
            }
        }
    }

    /// <summary>Empties the cache and zeroes the buffers. Only call once nothing can be playing them.</summary>
    public void ClearAndWipe()
    {
        lock (_sync)
        {
            foreach (var entry in _order)
                CryptographicOperations.ZeroMemory(entry.Value);

            _order.Clear();
            _map.Clear();
            _bytes = 0;
        }
    }
}
