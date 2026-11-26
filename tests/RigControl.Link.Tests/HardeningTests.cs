using System.Text.Json.Nodes;

namespace RigControl.Link.Tests;

/// <summary>What a relay (or a phone) may put in text the PC logs or shows.</summary>
public class RelayTextTests
{
    [Theory]
    [InlineData("denied", "denied")]
    [InlineData("ratelimit", "ratelimit")]
    [InlineData("abc123", "abc123")]
    [InlineData("Denied", "error")]
    [InlineData("bad code", "error")]
    [InlineData("<script>", "error")]
    [InlineData("denied\n", "error")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa", "error")] // 25 letters
    [InlineData("", "error")]
    [InlineData(null, "error")]
    public void Codes_are_cleaned(string? code, string expected) => Assert.Equal(expected, RelayCodes.Clean(code));

    [Fact]
    public void Only_known_codes_are_shown()
    {
        Assert.Equal("removed", RelayCodes.Shown("removed"));
        Assert.Equal("toobig", RelayCodes.Shown("toobig")); // the transport's own code for an oversized frame
        Assert.Equal("stale", RelayCodes.Shown(LinkRefusals.Stale));
        Assert.Equal("busy", RelayCodes.Shown(LinkRefusals.Busy));
        Assert.Equal("other", RelayCodes.Shown("visitevilsite"));
        Assert.Equal("other", RelayCodes.Shown(null));
    }

    [Fact]
    public void Error_frames_and_failures_carry_no_relay_text()
    {
        var f = Assert.IsType<RelayError>(RelayJson.ToFrame(new JsonObject { ["t"] = "error", ["code"] = "Call +1 555 0100 now" }));
        Assert.Equal("error", f.Code);
        var ex = RelaySession.RelayFailure("pleasevisitevil");
        Assert.Equal("pleasevisitevil", ex.Code); // cleaned, but not a known one
        Assert.DoesNotContain("pleasevisitevil", ex.Message);
        Assert.Contains("other", ex.Message);
        Assert.Equal("error", RelaySession.RelayFailure("Go to evil.example").Code);
    }
}

public class ChannelCreationTests
{
    [Fact]
    public async Task Messages_arriving_at_once_share_one_channel()
    {
        using var pcKeys = DeviceKeys.Create();
        using var phoneKeys = DeviceKeys.Create();
        await using var pc = new PcSide(new FakeRelay(), new Uri("wss://relay.test"), pcKeys, new TestHost());
        var phone = new PhoneRecord(phoneKeys.Id, "Phone", phoneKeys.AgreementPublicB64, 0, B64Url.Encode(new byte[16]));
        for (var round = 0; round < 20; round++)
        {
            var other = phone with { Epoch = B64Url.Encode(BitConverter.GetBytes((long)round + 1).Concat(new byte[8]).ToArray()) };
            using var start = new ManualResetEventSlim();
            var made = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { start.Wait(); return pc.Channel(other); })).ToArray();
            start.Set();
            var channels = await Task.WhenAll(made);
            Assert.All(channels, c => Assert.Same(channels[0], c));
        }
    }
}

public class NameTests
{
    [Theory]
    [InlineData("Pixel 8", "Pixel 8")]
    [InlineData("Evil\u202Egnp.exe", "Evilgnp.exe")] // right-to-left override
    [InlineData("A\u200Bdmin\u200D", "Admin")] // zero-width space / joiner
    [InlineData("Line\u2028Two\u2029", "LineTwo")] // line / paragraph separator
    [InlineData("Tab\tNew\nLine", "TabNewLine")]
    [InlineData("\u2066\u2069\uFEFF", "Phone")] // only invisible characters: the fallback
    public void Names_lose_invisible_and_control_characters(string raw, string expected)
        => Assert.Equal(expected, LinkDefaults.CleanName(raw, "Phone"));

    [Fact]
    public void Names_are_capped_without_splitting_a_pair()
    {
        Assert.Equal(LinkDefaults.MaxNameLength, LinkDefaults.CleanName(new string('a', 500), "x").Length);
        var s = LinkDefaults.CleanName(new string('a', LinkDefaults.MaxNameLength - 1) + "\U0001F600", "x");
        Assert.Equal(new string('a', LinkDefaults.MaxNameLength - 1), s);
    }
}

/// <summary>Reconnect rules: "denied" waits long, a silent relay is noticed, an aborted socket is a drop.</summary>
public class ReconnectTests
{
    private const string Ping = "{\"t\":\"ping\"}";
    private static readonly Uri Relay = new("wss://relay.test");
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Denied_waits_at_least_the_long_pause()
    {
        var relay = new FakeRelay();
        using var pcKeys = DeviceKeys.Create();
        using var stranger = DeviceKeys.Create();
        await using var pc = new PcSide(relay, Relay, pcKeys, new TestHost(), new PcSideOptions { Relay = new() { Backoff = _ => TimeSpan.FromMilliseconds(10) } });
        pc.Start();
        await pc.WaitOnlineAsync(Ct);
        var before = relay.Connects;
        await using var link = new RelayLink(relay, Relay, LinkProtocol.RolePhone, pcKeys.Id, stranger, _ => Task.CompletedTask, null,
            new RelayLinkOptions { Backoff = _ => TimeSpan.FromMilliseconds(10) });
        link.Start();
        await WaitUntil(() => link.LastError == "denied");
        await Task.Delay(400);
        Assert.Equal(before + 1, relay.Connects);
        Assert.Equal(LinkConnectionState.Waiting, link.State);
    }

    [Fact]
    public async Task A_silent_relay_is_dropped_and_connected_again()
    {
        var relay = new FakeRelay { AnswerUnknown = false };
        using var keys = DeviceKeys.Create();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var link = new RelayLink(relay, Relay, LinkProtocol.RolePc, keys.Id, keys, _ => Task.CompletedTask, null, new RelayLinkOptions
        {
            Backoff = _ => TimeSpan.FromMilliseconds(10),
            IdleProbeAfter = TimeSpan.FromMilliseconds(100),
            IdleTimeout = TimeSpan.FromMilliseconds(300),
            Log = log.Enqueue,
        });
        link.Start();
        await WaitUntil(() => relay.Connects >= 2);
        Assert.Contains(log, l => l.StartsWith("Nothing heard", StringComparison.Ordinal));
        Assert.Contains(relay.Frames(), f => f.Text == Ping);
    }

    [Fact]
    public async Task A_relay_that_answers_pings_keeps_the_connection_and_nothing_is_reported()
    {
        var relay = new FakeRelay { AnswerUnknown = true };
        using var keys = DeviceKeys.Create();
        var frames = new System.Collections.Concurrent.ConcurrentQueue<RelayFrame>();
        await using var link = new RelayLink(relay, Relay, LinkProtocol.RolePc, keys.Id, keys, f => { frames.Enqueue(f); return Task.CompletedTask; }, null,
            new RelayLinkOptions
            {
                Backoff = _ => TimeSpan.FromMilliseconds(10),
                IdleProbeAfter = TimeSpan.FromMilliseconds(100),
                IdleTimeout = TimeSpan.FromMilliseconds(300),
            });
        link.Start();
        await link.WaitOnlineAsync(Ct);
        await Task.Delay(1000);
        Assert.Equal(1, relay.Connects);
        Assert.True(relay.Frames().Count(f => f.Text == Ping) >= 3);
        Assert.Empty(frames); // the badmsg answers to pings never reach the owner
        Assert.Equal(LinkConnectionState.Online, link.State);
    }

    /// <summary>Wraps a transport; <see cref="Abort"/> makes the open connection's receive throw like an aborted socket.</summary>
    private sealed class AbortingTransport : IRelayTransport
    {
        private readonly IRelayTransport _inner;
        private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AbortingTransport(IRelayTransport inner) => _inner = inner;

        public void Abort() => _abort.TrySetResult();

        public async Task<IRelayConnection> ConnectAsync(Uri uri, CancellationToken ct) => new Conn(await _inner.ConnectAsync(uri, ct), this);

        private sealed class Conn : IRelayConnection
        {
            private readonly IRelayConnection _c;
            private readonly AbortingTransport _t;

            public Conn(IRelayConnection c, AbortingTransport t)
            {
                _c = c;
                _t = t;
            }

            public string? CloseReason => _c.CloseReason;

            public Task SendAsync(string text, CancellationToken ct) => _c.SendAsync(text, ct);

            public async Task<string?> ReceiveAsync(CancellationToken ct)
            {
                var r = _c.ReceiveAsync(ct);
                if (await Task.WhenAny(r, _t._abort.Task) == r) return await r;
                throw new InvalidOperationException("The WebSocket is in an invalid state ('Aborted') for this operation.");
            }

            public ValueTask DisposeAsync() => _c.DisposeAsync();
        }
    }

    /// <summary>
    /// Wraps a transport. Once <see cref="Stall"/> is set, sends never complete (and ignore the token), like a socket
    /// whose peer stopped reading. <see cref="Inject"/> hands a frame to the open connection's receive.
    /// </summary>
    private sealed class StallingTransport : IRelayTransport
    {
        private readonly IRelayTransport _inner;
        private readonly System.Threading.Channels.Channel<string> _injected = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public StallingTransport(IRelayTransport inner) => _inner = inner;

        public volatile bool Stall;
        public int StalledSends;

        public void Inject(string frame) => _injected.Writer.TryWrite(frame);

        public async Task<IRelayConnection> ConnectAsync(Uri uri, CancellationToken ct) => new Conn(await _inner.ConnectAsync(uri, ct), this);

        private sealed class Conn : IRelayConnection
        {
            private readonly IRelayConnection _c;
            private readonly StallingTransport _t;

            public Conn(IRelayConnection c, StallingTransport t)
            {
                _c = c;
                _t = t;
            }

            public string? CloseReason => _c.CloseReason;

            public Task SendAsync(string text, CancellationToken ct)
            {
                if (!_t.Stall) return _c.SendAsync(text, ct);
                Interlocked.Increment(ref _t.StalledSends);
                return new TaskCompletionSource().Task; // never completes, whatever the token says
            }

            public async Task<string?> ReceiveAsync(CancellationToken ct)
            {
                var r = _c.ReceiveAsync(ct);
                var i = _t._injected.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(r, i) == r) return await r;
                if (_t._injected.Reader.TryRead(out var text)) return text;
                return await r;
            }

            public ValueTask DisposeAsync() => _c.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_send_that_never_completes_drops_the_connection_within_the_timeout()
    {
        var transport = new StallingTransport(new FakeRelay());
        using var keys = DeviceKeys.Create();
        await using var link = new RelayLink(transport, Relay, LinkProtocol.RolePc, keys.Id, keys, _ => Task.CompletedTask, null,
            new RelayLinkOptions
            {
                Backoff = _ => TimeSpan.FromMilliseconds(10),
                SendTimeout = TimeSpan.FromMilliseconds(300),
                HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            });
        link.Start();
        await link.WaitOnlineAsync(Ct);
        transport.Stall = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await link.SendMsgAsync("someone", "body", Ct));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"the send took {sw.Elapsed}");
        // The receive ended and the link is connecting again (the handshake's own send now stalls too).
        await WaitUntil(() => link.State != LinkConnectionState.Online);
        transport.Stall = false;
        await link.WaitOnlineAsync(Ct);
        Assert.Equal(LinkConnectionState.Online, link.State);
    }

    [Fact]
    public async Task Stopping_does_not_wait_for_a_stalled_send_in_a_frame_handler()
    {
        var transport = new StallingTransport(new FakeRelay());
        using var keys = DeviceKeys.Create();
        RelayLink link = null!;
        var handling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link = new RelayLink(transport, Relay, LinkProtocol.RolePc, keys.Id, keys, async (_, ct) =>
            {
                handling.TrySetResult();
                await link.SendMsgAsync("someone", "body", ct);
            }, null,
            new RelayLinkOptions { Backoff = _ => TimeSpan.FromMilliseconds(10), SendTimeout = TimeSpan.FromMinutes(5) });
        link.Start();
        await link.WaitOnlineAsync(Ct);
        transport.Stall = true;
        transport.Inject("{\"t\":\"presence\",\"id\":\"p\",\"online\":true}");
        await handling.Task.WaitAsync(Ct);
        await WaitUntil(() => Volatile.Read(ref transport.StalledSends) > 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await link.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"stopping took {sw.Elapsed}");
        Assert.Equal(LinkConnectionState.Off, link.State);
    }

    [Fact]
    public async Task An_aborted_receive_is_a_drop_not_unreachable()
    {
        var transport = new AbortingTransport(new FakeRelay());
        using var keys = DeviceKeys.Create();
        await using var link = new RelayLink(transport, Relay, LinkProtocol.RolePc, keys.Id, keys, _ => Task.CompletedTask, null,
            new RelayLinkOptions { Backoff = _ => TimeSpan.FromSeconds(30) });
        link.Start();
        await link.WaitOnlineAsync(Ct);
        transport.Abort();
        await WaitUntil(() => link.State == LinkConnectionState.Waiting);
        Assert.Equal("dropped", link.LastError);
    }
}
