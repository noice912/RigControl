using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RigControl.Link.Tests;

/// <summary>Two ends of an in-memory "WebSocket".</summary>
internal sealed class PipeEnd : IRelayConnection
{
    private readonly Channel<string> _in;
    private readonly Channel<string> _out;
    private int _closed;
    private PipeEnd? _peer;

    private PipeEnd(Channel<string> inbox, Channel<string> outbox)
    {
        _in = inbox;
        _out = outbox;
    }

    public static (PipeEnd Client, PipeEnd Server) Create()
    {
        var a = Channel.CreateUnbounded<string>();
        var b = Channel.CreateUnbounded<string>();
        var client = new PipeEnd(a, b);
        var server = new PipeEnd(b, a);
        client._peer = server;
        server._peer = client;
        return (client, server);
    }

    public string? CloseReason { get; private set; }

    /// <summary>Closes with a reason the other end can read (like a WebSocket close frame).</summary>
    public void Close(string reason)
    {
        if (_peer is not null) _peer.CloseReason = reason;
        Close();
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (text.Length > LinkProtocol.MaxFrameBytes) throw new LinkException("toobig", "too big");
        if (!_out.Writer.TryWrite(text)) throw new IOException("closed");
        await Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _in.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _out.Writer.TryComplete();
        _in.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An in-memory relay that follows docs/protocols/link.md: challenge/hello with signature checks, rooms with an
/// allow-list (signed by the PC), msg routing with "gone", presence to the PC, and the pairing rendezvous.
/// </summary>
internal sealed class FakeRelay : IRelayTransport
{
    private sealed class Room
    {
        public byte[] PcKey = Array.Empty<byte>();
        public HashSet<string> Allow = new(StringComparer.Ordinal);
        public PipeEnd? Pc;
        public Dictionary<string, PipeEnd> Phones = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Box, PipeEnd Pc)> _offers = new(StringComparer.Ordinal);
    private readonly List<PipeEnd> _all = new();

    /// <summary>Every frame a client sent, in order (path, text).</summary>
    private List<(string Path, string Text)> FromClients { get; } = new();

    /// <summary>A copy of every frame a client sent so far, in order.</summary>
    public List<(string Path, string Text)> Frames()
    {
        lock (_gate) return FromClients.ToList();
    }

    /// <summary>Replaces the offer box handed to the phone (a relay trying to fake the PC).</summary>
    public Func<string, string>? TamperOffer { get; set; }

    public int Connects { get; private set; }

    /// <summary>Answer unknown frames ("ping") with badmsg, as the real relay does. Off: a relay gone silent.</summary>
    public bool AnswerUnknown { get; set; }

    /// <summary>Closes the open offer for <paramref name="h"/> with "replaced" (as the relay does).</summary>
    public void ReplaceOffer(string h)
    {
        PipeEnd? pc;
        lock (_gate) pc = _offers.Remove(h, out var o) ? o.Pc : null;
        pc?.Close("replaced");
    }

    /// <summary>Every URL a client opened.</summary>
    private List<Uri> UrlList { get; } = new();

    public List<Uri> Urls
    {
        get { lock (_gate) return UrlList.ToList(); }
    }

    public Task<IRelayConnection> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var (client, server) = PipeEnd.Create();
        lock (_gate)
        {
            Connects++;
            _all.Add(server);
        }
        var path = uri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        lock (_gate) UrlList.Add(uri);
        _ = path switch
        {
            "/v1/connect" when query["room"] is { } room => Task.Run(() => ConnectLoop(server, room)),
            "/v1/pair" when query["h"] is { } h => Task.Run(() => PairLoop(server, h)),
            _ => Task.Run(server.Close),
        };
        return Task.FromResult<IRelayConnection>(client);
    }

    /// <summary>Drops every open connection (as when the relay restarts).</summary>
    public void DropAll()
    {
        List<PipeEnd> all;
        lock (_gate)
        {
            all = _all.ToList();
            _all.Clear();
        }
        foreach (var c in all) c.Close();
    }

    public IReadOnlyCollection<string> AllowList(string room)
    {
        lock (_gate) return _rooms.TryGetValue(room, out var r) ? r.Allow.ToArray() : Array.Empty<string>();
    }

    public bool PhoneConnected(string room, string phone)
    {
        lock (_gate) return _rooms.TryGetValue(room, out var r) && r.Phones.ContainsKey(phone);
    }

    /// <summary>Delivers a frame to the room's PC as if a phone had sent it (for replay tests).</summary>
    public Task InjectToPc(string room, string from, string body)
    {
        PipeEnd? pc;
        lock (_gate) pc = _rooms.TryGetValue(room, out var r) ? r.Pc : null;
        return pc?.SendAsync(new JsonObject { ["t"] = "msg", ["from"] = from, ["body"] = body }.ToJsonString(), default) ?? Task.CompletedTask;
    }

    /// <summary>Delivers any frame to the room's PC (a relay misbehaving).</summary>
    public Task InjectRawToPc(string room, JsonObject frame)
    {
        PipeEnd? pc;
        lock (_gate) pc = _rooms.TryGetValue(room, out var r) ? r.Pc : null;
        return pc?.SendAsync(frame.ToJsonString(), default) ?? Task.CompletedTask;
    }

    private void Record(string path, string text)
    {
        lock (_gate) FromClients.Add((path, text));
    }

    private static string? Str(JsonObject o, string n) => o[n] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static Task Send(PipeEnd c, JsonObject o) => c.SendAsync(o.ToJsonString(), default).ContinueWith(_ => { });

    private async Task Fail(PipeEnd c, string code)
    {
        await Send(c, new JsonObject { ["t"] = "error", ["code"] = code });
        c.Close(code);
    }

    private async Task ConnectLoop(PipeEnd c, string urlRoom)
    {
        var nonce = B64Url.Encode(RandomNumberGenerator.GetBytes(32));
        await Send(c, new JsonObject { ["t"] = "challenge", ["nonce"] = nonce });
        var helloText = await c.ReceiveAsync(default);
        if (helloText is null) return;
        Record("/v1/connect", helloText);
        var hello = JsonNode.Parse(helloText) as JsonObject;
        if (hello is null || Str(hello, "t") != "hello")
        {
            await Fail(c, "badhello");
            return;
        }
        var role = Str(hello, "role");
        var room = Str(hello, "room") ?? "";
        if (!B64Url.TryDecode(Str(hello, "pub"), out var pub) || Str(hello, "sig") is not { } sig
            || !DeviceKeys.Verify(pub, LinkProtocol.HelloSignedText(nonce, room), sig))
        {
            await Fail(c, "badsig");
            return;
        }
        var id = DeviceKeys.IdOf(pub);
        if (room != urlRoom || pub.Length != 91)
        {
            await Fail(c, room != urlRoom ? "badroom" : "badkey");
            return;
        }
        Room r;
        lock (_gate)
        {
            if (role == "pc")
            {
                if (id != room)
                {
                    r = null!;
                }
                else
                {
                    if (!_rooms.TryGetValue(room, out r!)) _rooms[room] = r = new Room { PcKey = pub };
                    r.Pc?.Close("replaced");
                    r.Pc = c;
                }
            }
            else if (role == "phone" && _rooms.TryGetValue(room, out r!) && r.Allow.Contains(id))
            {
                if (r.Phones.TryGetValue(id, out var old)) old.Close("replaced");
                r.Phones[id] = c;
            }
            else
            {
                r = null!;
            }
        }
        if (r is null)
        {
            await Fail(c, role == "pc" ? "badroom" : "denied");
            return;
        }
        await Send(c, new JsonObject { ["t"] = "ok" });
        if (role == "phone" && r.Pc is { } pcNow)
            await Send(pcNow, new JsonObject { ["t"] = "presence", ["id"] = id, ["online"] = true });

        while (true)
        {
            var text = await c.ReceiveAsync(default);
            if (text is null) break;
            Record("/v1/connect", text);
            if (JsonNode.Parse(text) is not JsonObject f) continue;
            switch (Str(f, "t"))
            {
                case "allow" when role == "pc":
                {
                    var ids = (f["phones"] as JsonArray)?.Select(x => (string)x!).ToList() ?? new List<string>();
                    if (Str(f, "sig") is not { } asig || !DeviceKeys.Verify(r.PcKey, LinkProtocol.AllowSignedText(room, ids), asig))
                    {
                        await Send(c, new JsonObject { ["t"] = "error", ["code"] = "badsig" });
                        break;
                    }
                    List<PipeEnd> drop;
                    lock (_gate)
                    {
                        r.Allow = new HashSet<string>(ids, StringComparer.Ordinal);
                        drop = r.Phones.Where(p => !r.Allow.Contains(p.Key)).Select(p => p.Value).ToList();
                        foreach (var k in r.Phones.Keys.Where(k => !r.Allow.Contains(k)).ToList()) r.Phones.Remove(k);
                    }
                    foreach (var d in drop) d.Close("removed");
                    break;
                }
                case "msg":
                {
                    var to = Str(f, "to") ?? "";
                    var body = Str(f, "body") ?? "";
                    PipeEnd? target;
                    lock (_gate)
                    {
                        if (role == "phone") target = to == room ? r.Pc : null;
                        else target = r.Allow.Contains(to) && r.Phones.TryGetValue(to, out var p) ? p : null;
                    }
                    if (target is null)
                        await Send(c, new JsonObject { ["t"] = "gone", ["to"] = to });
                    else
                        await Send(target, new JsonObject { ["t"] = "msg", ["from"] = id, ["body"] = body });
                    break;
                }
                default:
                    // The real relay answers anything it doesn't know with badmsg (and keeps the connection).
                    if (AnswerUnknown) await Send(c, new JsonObject { ["t"] = "error", ["code"] = "badmsg" });
                    break;
            }
        }

        PipeEnd? pc = null;
        lock (_gate)
        {
            if (role == "pc" && r.Pc == c) r.Pc = null;
            if (role == "phone" && r.Phones.TryGetValue(id, out var me) && me == c)
            {
                r.Phones.Remove(id);
                pc = r.Pc;
            }
        }
        if (pc is not null) await Send(pc, new JsonObject { ["t"] = "presence", ["id"] = id, ["online"] = false });
    }

    private async Task PairLoop(PipeEnd c, string urlH)
    {
        var text = await c.ReceiveAsync(default);
        if (text is null) return;
        Record("/v1/pair", text);
        if (JsonNode.Parse(text) is not JsonObject f) return;
        var h = Str(f, "h") ?? "";
        var box = Str(f, "box") ?? "";
        if (h != urlH)
        {
            await Fail(c, "badmsg");
            return;
        }
        switch (Str(f, "t"))
        {
            case "offer":
                if (Str(f, "owner") is not { Length: 43 })
                {
                    await Fail(c, "badmsg");
                    return;
                }
                PipeEnd? older = null;
                lock (_gate)
                {
                    if (_offers.TryGetValue(h, out var prev)) older = prev.Pc;
                    _offers[h] = (box, c);
                }
                older?.Close("replaced");
                await Send(c, new JsonObject { ["t"] = "ok" });
                // Stay open until the PC goes away.
                while (await c.ReceiveAsync(default) is not null) { }
                lock (_gate)
                {
                    if (_offers.TryGetValue(h, out var o) && o.Pc == c) _offers.Remove(h);
                }
                break;
            case "claim":
                (string Box, PipeEnd Pc) offer;
                lock (_gate)
                {
                    if (!_offers.Remove(h, out offer)) offer = default;
                }
                if (offer.Pc is null)
                {
                    await Fail(c, "nopair");
                    return;
                }
                var handed = TamperOffer?.Invoke(offer.Box!) ?? offer.Box!;
                await Send(c, new JsonObject { ["t"] = "offer", ["h"] = h, ["box"] = handed });
                await Send(offer.Pc, new JsonObject { ["t"] = "claim", ["h"] = h, ["box"] = box });
                c.Close("paired");
                offer.Pc.Close("paired");
                break;
        }
    }
}

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTime(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
