using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Presence;

namespace RigControl.Presence.Tests;

/// <summary>A pretend Discord on the other end of one pipe: answers the handshake the way it is told to.</summary>
internal sealed class FakePipe
{
    public bool Mine = true;
    /// <summary>The answer to the handshake; null = says nothing and hangs up.</summary>
    public Func<string, string?> Hello = _ => FakeTransport.ReadyFor("42", "Alice");
    public List<string> Received { get; } = new();
    public int Opens;
    /// <summary>The pipe is listed but can't be opened (Discord is starting, closing or not accepting).</summary>
    public bool Busy;
}

internal sealed class FakeTransport : IDiscordTransport
{
    public SortedDictionary<int, FakePipe> Pipes { get; } = new();

    public static string ReadyFor(string id, string? globalName, string username = "user") =>
        JsonSerializer.Serialize(new
        {
            cmd = "DISPATCH",
            evt = "READY",
            data = new { v = 1, user = new { id, username, global_name = globalName } },
        });

    public IReadOnlyList<int> List() => Pipes.Keys.ToList();

    public IDiscordPipe? Open(int index)
    {
        if (!Pipes.TryGetValue(index, out var p) || p.Busy) return null;
        p.Opens++;
        return new Link(p);
    }

    private sealed class Link : IDiscordPipe
    {
        private readonly Wire _wire;
        public Link(FakePipe p) { Mine = p.Mine; _wire = new Wire(p); }
        public Stream Stream => _wire;
        public bool Mine { get; }
        public void Dispose() => _wire.Dispose();
    }

    /// <summary>Discord's framing: [int32 opcode][int32 length][JSON], both ways.</summary>
    private sealed class Wire : Stream
    {
        private readonly FakePipe _p;
        private readonly MemoryStream _out = new();
        private bool _open = true;
        public Wire(FakePipe p) => _p = p;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_open) throw new IOException("Pipe is broken.");
            var op = BitConverter.ToInt32(buffer, offset);
            var json = Encoding.UTF8.GetString(buffer, offset + 8, BitConverter.ToInt32(buffer, offset + 4));
            _p.Received.Add(json);
            var answer = op == 0 ? _p.Hello(json) : """{"cmd":"SET_ACTIVITY","evt":null}""";
            if (answer is null) return;
            var data = Encoding.UTF8.GetBytes(answer);
            var pos = _out.Position;
            _out.Position = _out.Length;
            _out.Write(BitConverter.GetBytes(1));
            _out.Write(BitConverter.GetBytes(data.Length));
            _out.Write(data);
            _out.Position = pos;
        }

        public override int Read(byte[] buffer, int offset, int count) => _open ? _out.Read(buffer, offset, count) : 0;
        public override bool CanRead => _open;
        public override bool CanWrite => _open;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _open = false; base.Dispose(disposing); }
    }
}

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = new();
    public void Info(string message) { lock (Lines) Lines.Add(message); }
    public void Warn(string message, Exception? ex = null) { lock (Lines) Lines.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Lines) Lines.Add("ERROR " + message); }
    public ILog For(string source) => this;
}

public class PipeTests
{
    private readonly FakeTransport _t = new();
    private DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private DiscordClient Client() => new(_t, () => _now);

    [Fact]
    public void A_Discord_in_another_Windows_session_is_passed_over_and_never_spoken_to()
    {
        _t.Pipes[0] = new FakePipe { Mine = false, Hello = _ => FakeTransport.ReadyFor("7", "Someone else") };
        _t.Pipes[1] = new FakePipe { Hello = _ => FakeTransport.ReadyFor("42", "Alice") };
        var d = Client();
        using var c = d.Open(TestIds.Music);
        Assert.True(c.Connect());
        Assert.Equal(new DiscordUser("42", "Alice"), c.User);
        Assert.Empty(_t.Pipes[0].Received); // not one byte went to the other user's Discord
        Assert.Null(c.SetActivity(new System.Text.Json.Nodes.JsonObject { ["name"] = "x" }));
        Assert.Empty(_t.Pipes[0].Received);
        Assert.Equal(2, _t.Pipes[1].Received.Count);
        Assert.Equal(1, d.Chosen);
    }

    [Fact]
    public void Only_another_users_Discord_means_Discord_is_not_running_here()
    {
        _t.Pipes[0] = new FakePipe { Mine = false };
        var d = Client();
        Assert.Equal(DiscordLook.OtherUser, d.Look());
        Assert.False(((IDiscordClient)d).IsRunning());
        using var c = d.Open(TestIds.Game);
        Assert.False(c.Connect());
        Assert.Null(c.User);
        Assert.Empty(_t.Pipes[0].Received);

        _t.Pipes.Clear();
        Assert.Equal(DiscordLook.NotRunning, d.Look());
    }

    [Fact]
    public void A_Discord_pipe_that_cannot_be_opened_is_not_called_another_Windows_users()
    {
        _t.Pipes[0] = new FakePipe { Busy = true };
        var d = Client();
        Assert.Equal(DiscordLook.NoAnswer, d.Look());
        using (var c = d.Open(TestIds.Game))
            Assert.False(c.Connect()); // nothing is sent to a pipe nobody could identify
        Assert.Empty(_t.Pipes[0].Received);

        // Nothing was remembered about it: as soon as it opens, it is asked again.
        _t.Pipes[0].Busy = false;
        Assert.Equal(DiscordLook.Running, d.Look());

        // Next to a pipe that is known to be someone else's, "another user" is still not claimed.
        var t2 = new FakeTransport();
        t2.Pipes[0] = new FakePipe { Mine = false };
        t2.Pipes[1] = new FakePipe { Busy = true };
        Assert.Equal(DiscordLook.NoAnswer, new DiscordClient(t2).Look());
    }

    [Fact]
    public void Discords_pipe_never_gets_more_than_RigControls_identity()
    {
        // With anything above "Identification", a pretend Discord pipe made by another program could act with
        // RigControl's administrator rights.
        Assert.Equal(System.Security.Principal.TokenImpersonationLevel.Identification, WindowsPipeTransport.PipeImpersonation);
    }

    [Fact]
    public void A_pipe_that_does_not_answer_READY_is_passed_over()
    {
        _t.Pipes[0] = new FakePipe { Hello = _ => null };
        _t.Pipes[1] = new FakePipe { Hello = _ => """{"code":4000,"message":"Invalid Client ID"}""" };
        _t.Pipes[2] = new FakePipe();
        var d = Client();
        using var c = d.Open(TestIds.App);
        Assert.True(c.Connect());
        Assert.Equal(2, d.Chosen);
        Assert.Equal(DiscordLook.Running, d.Look());
    }

    [Fact]
    public void Every_card_uses_the_Discord_the_first_card_reached()
    {
        var silent = true;
        _t.Pipes[0] = new FakePipe { Hello = _ => silent ? null : FakeTransport.ReadyFor("9", "Second client") };
        _t.Pipes[1] = new FakePipe();
        var d = Client();
        using var game = d.Open(TestIds.Game);
        Assert.True(game.Connect());
        silent = false; // the lower-numbered Discord wakes up: later cards must not move to it
        using var music = d.Open(TestIds.Music);
        using var custom = d.Open(TestIds.Pool[0]);
        Assert.True(music.Connect());
        Assert.True(custom.Connect());
        Assert.Equal("42", music.User!.Id);
        Assert.Equal("42", custom.User!.Id);
        Assert.Single(_t.Pipes[0].Received); // only the first card's unanswered hello
        Assert.Equal(3, _t.Pipes[1].Received.Count);
    }

    [Fact]
    public void When_the_chosen_Discord_closes_and_no_card_holds_it_the_next_card_looks_again()
    {
        _t.Pipes[1] = new FakePipe();
        var d = Client();
        var first = d.Open(TestIds.Game);
        Assert.True(first.Connect());
        first.Dispose();
        _t.Pipes.Remove(1);
        _t.Pipes[0] = new FakePipe { Hello = _ => FakeTransport.ReadyFor("43", "Alice again") };
        using var next = d.Open(TestIds.Game);
        Assert.True(next.Connect());
        Assert.Equal(0, d.Chosen);
    }

    [Fact]
    public void A_refused_app_id_gives_Discords_reason_and_a_silent_Discord_is_reported_as_not_answering()
    {
        _t.Pipes[0] = new FakePipe { Hello = _ => """{"code":4000,"message":"Invalid Client ID"}""" };
        var d = Client();
        var c = (DiscordConnection)d.Open("123456789012345678");
        Assert.False(c.Connect());
        Assert.Equal("Invalid Client ID", c.Refused);
        Assert.Equal(DiscordLook.Running, d.Look()); // it answered, just not with yes

        _t.Pipes[0].Hello = _ => null;
        Assert.False(c.Connect());
        Assert.Null(c.Refused);
        Assert.Equal(DiscordLook.NoAnswer, d.Look());
        Assert.True(((IDiscordClient)d).IsRunning());
        c.Dispose();
    }

    [Fact]
    public void Who_owns_a_pipe_is_asked_again_only_after_a_while()
    {
        _t.Pipes[0] = new FakePipe { Mine = false };
        var d = Client();
        for (var i = 0; i < 5; i++) d.Look();
        Assert.Equal(1, _t.Pipes[0].Opens);
        _now = _now.AddSeconds(31);
        d.Look();
        Assert.Equal(2, _t.Pipes[0].Opens);
    }

    [Theory]
    [InlineData("Alice A.", "alice", "Alice A.")]
    [InlineData(null, "alice", "alice")]
    [InlineData("", "alice", "alice")]
    public void READY_gives_the_account_id_and_the_name_Discord_shows(string? globalName, string username, string shown)
    {
        Assert.True(DiscordConnection.Ready(FakeTransport.ReadyFor("123456789012345678", globalName, username), out var u));
        Assert.Equal(new DiscordUser("123456789012345678", shown), u);
    }

    [Fact]
    public void READY_without_a_usable_account_still_connects_and_other_answers_are_not_READY()
    {
        Assert.True(DiscordConnection.Ready("""{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}""", out var none));
        Assert.Null(none);
        Assert.True(DiscordConnection.Ready("""{"evt":"READY","data":{"user":{"id":"not-a-number","username":"x"}}}""", out var odd));
        Assert.Null(odd);
        Assert.True(DiscordConnection.Ready("{\"evt\":\"READY\",\"data\":{\"user\":{\"id\":\"5\",\"username\":\"line\\nbreak\"}}}", out var clean));
        Assert.Equal("linebreak", clean!.Name);
        Assert.False(DiscordConnection.Ready("""{"code":4000,"message":"READY"}""", out _));
        Assert.False(DiscordConnection.Ready("not json", out _));
    }
}

public class AttachTests
{
    private readonly FakeDiscord _discord = new() { User = new DiscordUser("1", "Alice") };
    private readonly FakeInputs _inputs = new();
    private readonly ListLog _log = new();
    private DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly PresenceSettings _s = new() { Enabled = true, ShowCurrentApp = false, ShowLyrics = false, ShowAlbumArt = false, ShowWatching = false };

    private PresenceEngine Engine() => new(_discord, _inputs, _log, new HttpClient(new NoNetwork()), () => _now);

    [Fact]
    public async Task The_attached_account_is_named_once_in_the_log()
    {
        var e = Engine();
        _inputs.Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100);
        _s.CustomStatuses.Add(new CustomStatus { Name = "Studying", Enabled = true });
        for (var i = 0; i < 3; i++)
        {
            await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
            _now = _now.AddSeconds(3);
        }
        Assert.Equal(new DiscordUser("1", "Alice"), e.Attached);
        Assert.Single(_log.Lines, l => l == "Attached to Alice's Discord.");
        Assert.Null(e.AttachNote);
    }

    [Fact]
    public async Task When_Discord_switches_account_the_old_cards_are_cleared_and_it_carries_on_with_the_new_one()
    {
        var e = Engine();
        _inputs.Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100);
        _s.CustomStatuses.Add(new CustomStatus { Name = "Studying", Enabled = true });
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        Assert.Equal(2, _discord.Cards.Count);

        // Discord signs in to another account. The music card's link drops and comes back on the new account;
        // the custom card's link is still the old one.
        _discord.User = new DiscordUser("2", "Bob");
        _discord.Opened.Single(c => c.ClientId == TestIds.Music).Cut();
        _now = _now.AddSeconds(3);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);

        Assert.Contains((TestIds.Pool[0], "1"), _discord.Cleared); // taken down on the old account's connection
        Assert.All(_discord.Opened, c => Assert.Equal("2", c.Account));  // every card is on the new account now
        Assert.Equal(2, _discord.Cards.Count);
        Assert.Equal("Bob", e.Attached!.Name);
        Assert.Contains("Bob", e.AttachNote);
        Assert.Contains("Discord switched to another account. Cards sent to the old one were cleared.", _log.Lines);
        Assert.Single(_log.Lines, l => l == "Attached to Bob's Discord.");

        _now = _now.AddMinutes(6);
        Assert.Null(e.AttachNote); // the note goes away by itself
    }

    [Fact]
    public async Task Nothing_is_attached_once_Discord_is_closed()
    {
        var e = Engine();
        _inputs.Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100);
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        Assert.NotNull(e.Attached);
        _discord.Running = false;
        await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
        Assert.Null(e.Attached);
    }

    [Fact]
    public void Asking_who_is_signed_in_shows_no_card()
    {
        var e = Engine();
        e.Probe();
        Assert.Equal("Alice", e.Attached!.Name);
        Assert.Empty(_discord.Cards);
        Assert.Empty(_discord.Opened); // the one connection was closed again
    }
}

public class FirstRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-presence-tests-" + Guid.NewGuid().ToString("N"));
    public FirstRunTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private async Task<(PresenceModule Module, SettingsStore Store)> Start(FakeDiscord discord, ILog? log = null)
    {
        var m = new PresenceModule(discord, new FakeInputs(), new HttpClient(new NoNetwork()), () => false) { TickInterval = TimeSpan.FromMilliseconds(20), StartBridge = false };
        var store = new SettingsStore(Path.Combine(_dir, "s.json"), NullLog.Instance);
        store.Set(PresenceSettings.Section, new PresenceSettings { ImportedFromRichPresence = DateTimeOffset.Now });
        Directory.CreateDirectory(Path.Combine(_dir, "presence"));
        File.WriteAllText(Path.Combine(_dir, "presence", "library.json"), "[]");
        await m.StartAsync(new ModuleContext(new EventBus(), log ?? NullLog.Instance, store, new AppEnvironment(_dir, _dir, true, "1", "x.exe")), CancellationToken.None);
        return (m, store);
    }

    private static async Task<JsonElement> Status(PresenceModule m, Func<JsonElement, bool> until)
    {
        JsonElement st = default;
        for (var i = 0; i < 100; i++)
        {
            st = JsonSerializer.SerializeToElement(await m.InvokeAsync("getStatus", null, CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (until(st)) break;
            await Task.Delay(20);
        }
        return st;
    }

    [Fact]
    public async Task With_the_status_still_off_the_page_already_knows_whose_Discord_it_would_use()
    {
        var discord = new FakeDiscord { User = new DiscordUser("1", "Alice") };
        var (m, _) = await Start(discord);
        var st = await Status(m, s => s.GetProperty("discordState").GetString() == "attached");
        Assert.False(st.GetProperty("enabled").GetBoolean()); // still off: nothing is shown without a yes
        Assert.True(st.GetProperty("discordRunning").GetBoolean());
        Assert.Equal("attached", st.GetProperty("discordState").GetString());
        Assert.Equal("1", st.GetProperty("attachedUser").GetProperty("id").GetString());
        Assert.Equal("Alice", st.GetProperty("attachedUser").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("discordReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, st.GetProperty("extensionPort").ValueKind);
        Assert.Empty(discord.Cards);
        await m.DisposeAsync();
    }

    [Theory]
    [InlineData(DiscordLook.NotRunning, "notRunning", "Discord isn't running", false)]
    [InlineData(DiscordLook.OtherUser, "otherUser", "Discord is running under another Windows user", false)]
    [InlineData(DiscordLook.NoAnswer, "noAnswer", "Discord didn't answer", true)]
    public async Task The_page_is_told_in_plain_words_why_nothing_is_attached(DiscordLook seen, string state, string reason, bool running)
    {
        var discord = new FakeDiscord { Seen = seen, Running = false };
        var log = new ListLog();
        var (m, _) = await Start(discord, log);
        var st = await Status(m, s => s.GetProperty("discordState").GetString() == state && s.GetProperty("discordReason").ValueKind == JsonValueKind.String);
        Assert.Equal(state, st.GetProperty("discordState").GetString());
        Assert.StartsWith(reason, st.GetProperty("discordReason").GetString());
        Assert.Equal(running, st.GetProperty("discordRunning").GetBoolean());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("attachedUser").ValueKind);
        await m.DisposeAsync();
        lock (log.Lines) Assert.Single(log.Lines, l => l.StartsWith("Discord status: " + reason, StringComparison.Ordinal)); // said once, not every pass
    }

    [Fact]
    public async Task A_custom_status_may_not_use_one_of_the_built_in_app_ids()
    {
        var (m, store) = await Start(new FakeDiscord());
        var builtIn = new[] { TestIds.Game, TestIds.Music, TestIds.App, TestIds.Pool[1] }
            .Concat(TestIds.Services);
        foreach (var id in builtIn)
        {
            var args = JsonSerializer.SerializeToElement(new { items = new[] { new { id = "a", name = "Mine", enabled = true, app_id = id } } });
            var ex = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("setCustomStatuses", args, CancellationToken.None));
            Assert.Contains("already uses", ex.Message);
        }
        Assert.Empty(store.Get<PresenceSettings>(PresenceSettings.Section).CustomStatuses);
        var own = JsonSerializer.SerializeToElement(new { items = new[] { new { id = "a", name = "Mine", enabled = true, app_id = "123456789012345678" } } });
        await m.InvokeAsync("setCustomStatuses", own, CancellationToken.None);
        Assert.Equal("123456789012345678", store.Get<PresenceSettings>(PresenceSettings.Section).CustomStatuses.Single().AppId);
        await m.DisposeAsync();
    }

    [Fact]
    public void A_built_in_id_saved_by_an_older_version_is_treated_as_no_id()
    {
        var items = new[]
        {
            new CustomStatus { Name = "Old", Enabled = true, AppId = TestIds.Music },
            new CustomStatus { Name = "Own", Enabled = true, AppId = "123456789012345678" },
        };
        var (cards, skipped) = CustomCards.Build(items, "separate", TestIds.Ids);
        Assert.Empty(skipped);
        Assert.Equal(new[] { "123456789012345678", TestIds.Pool[0] }, cards.Select(c => c.AppId));
    }
}

public class SessionTests
{
    [Fact]
    public void Only_this_sessions_processes_count()
    {
        var all = new (string, int, DateTime?)[] { ("MyGame", 2, null), ("OtherUsersGame", 3, null), ("services", 0, null) };
        Assert.Equal(new[] { "mygame" }, WinSession.InSession(all, 2).Select(p => p.Name));
    }

    [Fact]
    public void The_real_process_list_has_this_program_and_nothing_from_another_session()
    {
        using var me = Process.GetCurrentProcess();
        var names = new PcInputs().Processes().Select(p => p.Name).ToHashSet();
        Assert.Contains(me.ProcessName.ToLowerInvariant(), names);
        Assert.Equal(me.SessionId, WinSession.Current);
        Assert.Equal(me.SessionId, WinSession.OfProcess(me.Id));
        // Windows' own session-0 programs must not be listed for a signed-in user.
        if (me.SessionId != 0) Assert.DoesNotContain("smss", names);
        Assert.Contains(me.Id, WinSession.PidsByName(new[] { me.ProcessName }));
        Assert.Contains(WinSession.UserOfProcess(me.Id)!, WinSession.MyUsers());
    }
}

internal sealed class FakePeers : IPeerLookup
{
    public PeerInfo? Peer;
    public PeerInfo? Find(int peerPort, int listenPort) => Peer;
}

public class BridgePeerTests
{
    private static readonly IReadOnlySet<string> Me = new HashSet<string> { "S-1-5-21-1-2-3-1001" };

    [Fact]
    public void Only_this_users_programs_in_this_session_may_use_the_link()
    {
        var peers = new FakePeers();
        var guard = new PeerGuard(peers, 2, Me);
        peers.Peer = new PeerInfo(100, 2, "S-1-5-21-1-2-3-1001");
        Assert.True(guard.Allows(50000, 47610));
        peers.Peer = new PeerInfo(100, 3, "S-1-5-21-1-2-3-1001"); // same account, another session (remote desktop)
        Assert.False(guard.Allows(50000, 47610));
        peers.Peer = new PeerInfo(100, 2, "S-1-5-21-1-2-3-1002"); // another account in this session
        Assert.False(guard.Allows(50000, 47610));
        peers.Peer = new PeerInfo(100, 3, "S-1-5-21-1-2-3-1002"); // another signed-in user
        Assert.False(guard.Allows(50000, 47610));
        peers.Peer = null;                                         // can't be told: refused
        Assert.False(guard.Allows(50000, 47610));
    }

    /// <summary>
    /// A free port with room for ten after it, away from the real 47610-47619. Picked at random below the range
    /// Windows hands out by itself: asking Windows for "any free port" fails for a while whenever its counter is
    /// near the top of that range.
    /// </summary>
    private static int FreeRange()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(20000, 45000);
            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.ExclusiveAddressUse = true;
                probe.Start();
                return port;
            }
            catch (SocketException)
            {
                // in use: try another
            }
            finally
            {
                probe.Stop();
            }
        }
        throw new InvalidOperationException("No free ports.");
    }

    private static async Task<int> Listening(ExtensionBridge b)
    {
        for (var i = 0; i < 200 && b.Port is null; i++) await Task.Delay(10);
        return b.Port ?? throw new InvalidOperationException("The link never started.");
    }

    private static async Task<HttpResponseMessage> Ask(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        req.Headers.TryAddWithoutValidation("Origin", "chrome-extension://abcdefghijklmnop");
        req.Headers.TryAddWithoutValidation("X-RichPresence", "1");
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task This_users_own_program_gets_the_greeting_and_a_website_is_still_refused()
    {
        // The real lookup: this test is "the browser", running as this user in this session.
        using var b = new ExtensionBridge(NullLog.Instance, "9.9.9", firstPort: FreeRange());
        b.Start();
        var port = await Listening(b);
        using var ok = await Ask(port);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal("RigControl", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal("9.9.9", doc.RootElement.GetProperty("version").GetString());

        using var http = new HttpClient();
        var site = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        site.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        site.Headers.TryAddWithoutValidation("X-RichPresence", "1");
        using var refused = await http.SendAsync(site);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var noOrigin = await http.GetAsync($"http://127.0.0.1:{port}/status");
        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
    }

    [Theory]
    [InlineData(true, false)]   // another Windows session
    [InlineData(false, true)]   // another account
    [InlineData(false, false)]  // unknown program
    public async Task Another_users_browser_is_refused_and_can_set_nothing(bool otherSession, bool otherUser)
    {
        var peers = new FakePeers();
        if (otherSession) peers.Peer = new PeerInfo(1, 99, Me.First());
        if (otherUser) peers.Peer = new PeerInfo(1, 2, "S-1-5-21-9-9-9-1001");
        var log = new ListLog();
        using var b = new ExtensionBridge(log, "1", guard: new PeerGuard(peers, 2, Me), firstPort: FreeRange());
        b.Start();
        var port = await Listening(b);
        using var status = await Ask(port);
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);

        using var http = new HttpClient();
        var post = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/watching")
        {
            Content = new StringContent("""{"service":"Netflix","title":"Not yours","playing":true}""", Encoding.UTF8, "application/json"),
        };
        post.Headers.TryAddWithoutValidation("Origin", "chrome-extension://abcdefghijklmnop");
        post.Headers.TryAddWithoutValidation("X-RichPresence", "1");
        using var posted = await http.SendAsync(post);
        Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);
        Assert.Null(b.Get("Netflix"));
        lock (log.Lines) Assert.Single(log.Lines, l => l.Contains("didn't come from this Windows user"));
    }

    [Fact]
    public async Task A_second_copy_takes_the_next_port_instead_of_waiting_for_the_first()
    {
        var first = FreeRange();
        using var a = new ExtensionBridge(NullLog.Instance, "1", firstPort: first);
        using var b = new ExtensionBridge(NullLog.Instance, "1", firstPort: first);
        a.Start();
        var portA = await Listening(a);
        b.Start();
        var portB = await Listening(b);
        Assert.Equal(first, portA);
        Assert.Equal(first + 1, portB);
        Assert.True(a.Listening && b.Listening);
    }
}
