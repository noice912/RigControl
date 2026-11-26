using System.Collections.Concurrent;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// Where <see cref="RelayLinkService"/> keeps what it must remember. The list of linked PCs (ids, names, public keys,
/// epochs, relay, counters, last seen) holds nothing secret and goes to ordinary app storage; the phone's private keys
/// for each PC go to the platform's protected store (Keychain on iPhone, Keystore-backed storage on Android).
/// </summary>
public interface ILinkStore
{
    /// <summary>The saved list (JSON made by the service), or null when there is none.</summary>
    Task<string?> LoadListAsync();

    Task SaveListAsync(string json);

    /// <summary>The phone's private keys for one PC, or null when missing or unreadable.</summary>
    Task<byte[]?> LoadKeyAsync(string pcId);

    Task SaveKeyAsync(string pcId, byte[] secret);

    Task DeleteKeyAsync(string pcId);
}

/// <summary>Keeps everything in memory (tests, and a second service on the same data in tests).</summary>
public sealed class InMemoryLinkStore : ILinkStore
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private volatile string? _list;

    public int ListSaves { get; private set; }

    public bool HasKey(string pcId) => _keys.ContainsKey(pcId);

    public Task<string?> LoadListAsync() => Task.FromResult(_list);

    public Task SaveListAsync(string json)
    {
        _list = json;
        ListSaves++;
        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadKeyAsync(string pcId) =>
        Task.FromResult(_keys.TryGetValue(pcId, out var k) ? (byte[]?)k.ToArray() : null);

    public Task SaveKeyAsync(string pcId, byte[] secret)
    {
        _keys[pcId] = secret.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteKeyAsync(string pcId)
    {
        _keys.TryRemove(pcId, out _);
        return Task.CompletedTask;
    }
}
