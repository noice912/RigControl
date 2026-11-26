using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Link;

/// <summary>
/// Sealed messages between one PC and one phone: AES-256-GCM with the session key, AAD =
/// 'rigcontrol-link-v1|' + from + '|' + to + '|' + counter, and a counter that must rise strictly in each direction.
/// A message whose counter is not larger than the last accepted one is refused (replay). Thread-safe.
/// </summary>
public sealed class SecureChannel
{
    public const byte FromPc = 1;
    public const byte FromPhone = 2;

    /// <summary>Counters stay below 2^53 so JavaScript and JSON readers keep them exact.</summary>
    public const long MaxCounter = (1L << 53) - 1;

    private readonly byte[] _key;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private long _lastIn;
    private long _lastOut;

    /// <param name="key">The session key (<see cref="LinkCrypto.SessionKey"/>).</param>
    /// <param name="myId">This device's id.</param>
    /// <param name="peerId">The other device's id.</param>
    /// <param name="iAmPc">True on the PC.</param>
    /// <param name="lastIn">The last counter accepted from the peer (stored by the caller); 0 when none.</param>
    /// <param name="time">The clock counters start from; the system clock unless a test gives one.</param>
    /// <param name="lastOut">The highest counter sent to the peer before (stored by the caller): later ones are higher
    /// even if the clock was set back.</param>
    public SecureChannel(byte[] key, string myId, string peerId, bool iAmPc, string epoch, long lastIn = 0, TimeProvider? time = null,
        long lastOut = 0)
    {
        if (!LinkProtocol.IsEpoch(epoch)) throw new ArgumentException("Not a pairing epoch.", nameof(epoch));
        Epoch = epoch;
        if (key.Length != LinkCrypto.KeySize) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        _key = key;
        MyId = myId;
        PeerId = peerId;
        IAmPc = iAmPc;
        _lastIn = Math.Max(0, lastIn);
        _lastOut = Math.Clamp(lastOut, 0, MaxCounter);
        _time = time ?? TimeProvider.System;
    }

    public string MyId { get; }

    /// <summary>The pairing's epoch (part of the key and of every AAD).</summary>
    public string Epoch { get; }
    public string PeerId { get; }
    public bool IAmPc { get; }

    /// <summary>The last counter accepted from the peer. The caller stores it so replays stay refused after a restart.</summary>
    public long LastIn
    {
        get { lock (_gate) return _lastIn; }
    }

    public long LastOut
    {
        get { lock (_gate) return _lastOut; }
    }

    /// <summary>
    /// Sets "n" on <paramref name="message"/> (which must have "type") and seals it. Returns the body for a relay
    /// "msg" frame (base64url).
    /// </summary>
    public string Seal(JsonObject message, out long counter)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message["type"] is not JsonValue t || t.GetValueKind() != JsonValueKind.String)
            throw new ArgumentException("A message needs a \"type\".", nameof(message));
        lock (_gate)
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;
            counter = Math.Max(_lastOut + 1, now);
            if (counter > MaxCounter) throw new LinkException("counter", "The message counter ran out.");
            _lastOut = counter;
        }
        message["n"] = counter;
        var plain = JsonSerializer.SerializeToUtf8Bytes(message);
        Span<byte> nonce = stackalloc byte[LinkCrypto.NonceSize];
        LinkCrypto.WriteCounter(nonce, IAmPc ? FromPc : FromPhone, (ulong)counter);
        var aad = LinkProtocol.Utf8(LinkProtocol.MessageAad(MyId, PeerId, counter, Epoch));
        return B64Url.Encode(LinkCrypto.Seal(_key, nonce, plain, aad));
    }

    public string Seal(JsonObject message) => Seal(message, out _);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Numbers, seals and sends one message as a single step for this peer: two sends at once can't overtake each other
    /// on the way (the receiver would refuse the older counter as a replay). <paramref name="send"/> gets the counter
    /// and the body and returns whether it was sent.
    /// </summary>
    public async Task<bool> SendSealedAsync(JsonObject message, Func<long, string, Task<bool>> send, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = Seal(message, out var counter);
            return await send(counter, body).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Opens a body from the peer. Throws <see cref="LinkException"/>: "sealed" (changed, wrong key or wrong
    /// sender), "replay" (counter not larger than the last accepted one), "badmsg" (not a message).
    /// On success the counter becomes the new <see cref="LastIn"/>.
    /// </summary>
    public JsonObject Open(string body) => Open(body, null, out _);

    /// <summary>
    /// Like <see cref="Open(string)"/>, and then asks <paramref name="fresh"/> about the (authentic) counter. When it
    /// says no, <paramref name="stale"/> is true, the message is returned so it can be answered, and
    /// <see cref="LastIn"/> is left as it was (a message from a clock that is far ahead must not push it forward).
    /// </summary>
    public JsonObject Open(string body, Func<long, bool>? fresh, out bool stale)
    {
        stale = false;
        if (!B64Url.TryDecode(body, out var data) || data.Length < LinkCrypto.NonceSize + LinkCrypto.TagSize)
            throw new LinkException("badmsg", "The message was not readable.");
        var (direction, raw) = LinkCrypto.ReadCounter(data);
        if (direction != (IAmPc ? FromPhone : FromPc))
            throw new LinkException("sealed", "The message was not sent by the other device.");
        if (raw == 0 || raw > MaxCounter) throw new LinkException("badmsg", "The message counter is out of range.");
        var counter = (long)raw;
        lock (_gate)
        {
            if (counter <= _lastIn) throw new LinkException("replay", "The message was already received (replay).");
        }
        var aad = LinkProtocol.Utf8(LinkProtocol.MessageAad(PeerId, MyId, counter, Epoch));
        var plain = LinkCrypto.Open(_key, data, aad);
        JsonObject msg;
        try
        {
            msg = JsonNode.Parse(plain) as JsonObject ?? throw new LinkException("badmsg", "The message was not an object.");
        }
        catch (JsonException ex)
        {
            throw new LinkException("badmsg", "The message was not JSON.", ex);
        }
        if (msg["n"] is not JsonValue nv || !nv.TryGetValue(out long n) || n != counter)
            throw new LinkException("badmsg", "The message counter did not match.");
        if (msg["type"] is not JsonValue tv || !tv.TryGetValue(out string? _))
            throw new LinkException("badmsg", "The message has no type.");
        lock (_gate)
        {
            // Two messages opened at once: only the later counter may win.
            if (counter <= _lastIn) throw new LinkException("replay", "The message was already received (replay).");
            if (fresh is not null && !fresh(counter))
            {
                stale = true;
                return msg;
            }
            _lastIn = counter;
        }
        return msg;
    }

    /// <summary>The channel between two linked devices.</summary>
    public static SecureChannel For(DeviceKeys mine, string peerId, byte[] peerAgreementSpki, bool iAmPc, string epoch, long lastIn = 0,
        TimeProvider? time = null, long lastOut = 0)
        => new(LinkCrypto.SessionKey(mine, peerAgreementSpki, peerId, epoch), mine.Id, peerId, iAmPc, epoch, lastIn, time, lastOut);
}
