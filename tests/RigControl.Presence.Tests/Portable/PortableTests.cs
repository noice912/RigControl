using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Presence;

namespace RigControl.Presence.Tests.Portable;

// These tests run in both flavours (Windows and neutral) and on Linux and macOS. Nothing here talks to a real
// Discord: the sockets are made in a throwaway folder and answered by the test itself.

/// <summary>A made-up file system for the socket search.</summary>
internal sealed class FakeFiles : ISocketFiles
{
    public HashSet<string> Files { get; } = new();
    /// <summary>Folders only their owner can get into.</summary>
    public HashSet<string> Private { get; } = new();
    public HashSet<string> LinkedFolders { get; } = new();
    public Dictionary<string, string> Links { get; } = new();
    public Dictionary<string, DateTime> MadeAt { get; } = new();

    public bool Exists(string path) => Files.Contains(path);
    public string? WhyNotPrivate(string folder) => Private.Contains(folder) ? null : SocketTrust.OpenToOthers;
    public bool IsLink(string path) => LinkedFolders.Contains(path);
    public string? LinkTarget(string path) => Links.GetValueOrDefault(path);
    public DateTime? Made(string path) => MadeAt.TryGetValue(path, out var t) ? t : null;
}

/// <summary>Real files in a throwaway folder that the test treats as this user's alone (so it runs on Windows too).</summary>
internal sealed class TempFiles : ISocketFiles
{
    public bool Exists(string path) => File.Exists(path);
    public string? WhyNotPrivate(string folder) => null;
    public bool IsLink(string path) => false;
    public string? LinkTarget(string path) => null;
    public DateTime? Made(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }
}

internal sealed class FakeProcesses : IProcessQuery
{
    public List<ProcessEntry> Running { get; } = new();
    public int Asked;
    public IReadOnlyList<ProcessEntry> OwnProcesses() { Asked++; return Running.ToList(); }
    public bool IsRunning(string processName) => Running.Any(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase));
}

internal sealed class Lines : ILog
{
    public List<string> All { get; } = new();
    public void Info(string message) { lock (All) All.Add(message); }
    public void Warn(string message, Exception? ex = null) { lock (All) All.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (All) All.Add("ERROR " + message); }
    public ILog For(string source) => this;
}

/// <summary>A pretend Discord that takes every card and remembers it.</summary>
internal sealed class CardBoard : IDiscordClient
{
    public Dictionary<string, string?> Cards { get; } = new();
    public DiscordLook Look() => DiscordLook.Running;
    public IDiscordConnection Open(string clientId) => new Conn(this, clientId);

    private sealed class Conn : IDiscordConnection
    {
        private readonly CardBoard _b;
        public Conn(CardBoard b, string id) { _b = b; ClientId = id; }
        public string ClientId { get; }
        public bool Connected { get; private set; }
        public DiscordUser? User => Connected ? new DiscordUser("42", "Alice") : null;
        public bool Connect() => Connected = true;
        public string? SetActivity(JsonObject? activity)
        {
            if (activity is null) _b.Cards.Remove(ClientId);
            else _b.Cards[ClientId] = activity.ToJsonString();
            return null;
        }
        public void Dispose() { Connected = false; _b.Cards.Remove(ClientId); }
    }
}

internal sealed class Offline : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
}

public class SocketFolderTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Linux_looks_in_the_users_own_folder_first_then_Flatpak_then_Snap_then_the_temporary_folders()
    {
        var got = UnixSocketFolders.For(HostPlatform.Linux, Env(("XDG_RUNTIME_DIR", "/run/user/1000/"), ("TMPDIR", "/home/me/tmp")));
        Assert.Equal(new[]
        {
            "/run/user/1000",
            "/run/user/1000/app/com.discordapp.Discord",
            "/run/user/1000/snap.discord",
            "/run/user/1000/snap.discord-canary",
            "/run/user/1000/app/com.discordapp.DiscordCanary",
            "/run/user/1000/app/com.discordapp.DiscordPTB",
            "/run/user/1000/snap.discord-ptb",
            "/home/me/tmp",
            "/tmp",
        }, got.Select(f => f.Path));
        // Everything below the user's folder is judged by that folder.
        Assert.All(got.Take(7), f => Assert.Equal("/run/user/1000", f.Root));
        Assert.Equal("/tmp", got[^1].Root);
    }

    [Fact]
    public void Linux_without_its_own_folder_still_finds_tmp_and_a_folder_is_never_listed_twice()
    {
        var got = UnixSocketFolders.For(HostPlatform.Linux, Env(("TMPDIR", "/tmp/"), ("TMP", "/tmp"), ("TEMP", "relative/path")));
        Assert.Equal(new[] { "/tmp" }, got.Select(f => f.Path));
    }

    [Fact]
    public void MacOS_looks_only_in_the_users_temporary_folder()
    {
        var got = UnixSocketFolders.For(HostPlatform.MacOS, Env(("TMPDIR", "/var/folders/ab/cd/T/"), ("XDG_RUNTIME_DIR", "/run/user/501")));
        Assert.Equal(new[] { "/var/folders/ab/cd/T" }, got.Select(f => f.Path));
        Assert.Empty(UnixSocketFolders.For(HostPlatform.MacOS, Env()));
    }

    [Fact]
    public void Windows_and_unknown_systems_have_no_socket_folders()
    {
        var env = Env(("XDG_RUNTIME_DIR", "/run/user/1000"), ("TMPDIR", "/tmp"));
        Assert.Empty(UnixSocketFolders.For(HostPlatform.Windows, env));
        Assert.Empty(UnixSocketFolders.For(HostPlatform.Other, env));
    }
}

public class SocketTrustTests
{
    private const string Run = "/run/user/1000";
    private static readonly Func<string, string?> LinuxEnv = n => n == "XDG_RUNTIME_DIR" ? Run : null;

    private static (UnixSocketTransport T, FakeFiles F, List<string> Notes) Make()
    {
        var files = new FakeFiles();
        files.Private.Add(Run);
        var notes = new List<string>();
        var t = new UnixSocketTransport(UnixSocketFolders.For(HostPlatform.Linux, LinuxEnv), files) { Note = notes.Add };
        return (t, files, notes);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserExecute, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite, false)]
    [InlineData(UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute, false)]
    public void A_folder_counts_as_the_users_own_only_when_nobody_else_has_any_right_to_it(UnixFileMode mode, bool mine)
    {
        Assert.Equal(mine, SocketTrust.WhyNotPrivate(mode) is null);
    }

    [Fact]
    public void A_socket_in_the_users_own_folder_is_listed_lowest_number_first()
    {
        var (t, f, notes) = Make();
        f.Files.Add(Run + "/discord-ipc-3");
        f.Files.Add(Run + "/discord-ipc-0");
        f.Files.Add(Run + "/discord-ipc-10"); // not one of the ten
        f.Files.Add(Run + "/other-file");
        Assert.Equal(new[] { 0, 3 }, t.List());
        Assert.Empty(notes);
    }

    [Fact]
    public void A_Flatpak_or_Snap_socket_below_the_users_folder_is_found()
    {
        var (t, f, _) = Make();
        f.Files.Add(Run + "/app/com.discordapp.Discord/discord-ipc-0");
        f.Files.Add(Run + "/snap.discord/discord-ipc-1");
        Assert.Equal(new[] { 0, 1 }, t.List());
        Assert.Equal(new[] { Run + "/app/com.discordapp.Discord/discord-ipc-0" }, t.Usable(0, skipDead: false));
    }

    [Fact]
    public void A_socket_in_tmp_is_never_used_and_the_log_says_so_once()
    {
        var (t, f, notes) = Make();
        f.Files.Add("/tmp/discord-ipc-0");
        Assert.Empty(t.List());
        Assert.Null(t.Open(0));
        Assert.Empty(t.List());
        var note = Assert.Single(notes);
        Assert.Contains("/tmp", note);
        Assert.Contains("isn't used", note);
    }

    [Fact]
    public void The_users_folder_is_not_trusted_when_others_can_get_into_it()
    {
        var (t, f, notes) = Make();
        f.Private.Clear();
        f.Files.Add(Run + "/discord-ipc-0");
        f.Files.Add(Run + "/snap.discord/discord-ipc-0");
        Assert.Empty(t.List());
        Assert.Equal(2, notes.Count); // one per folder, not one per look
        t.List();
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void A_socket_that_is_a_link_to_somewhere_else_is_refused()
    {
        var (t, f, notes) = Make();
        f.Files.Add(Run + "/discord-ipc-0");
        f.Links[Run + "/discord-ipc-0"] = "/tmp/discord-ipc-0";
        Assert.Empty(t.List());
        Assert.Contains("link to somewhere else", Assert.Single(notes));

        // A link that can't be followed is refused as well.
        f.Links[Run + "/discord-ipc-0"] = "";
        Assert.Empty(t.List());
    }

    [Fact]
    public void A_link_to_the_Flatpak_socket_under_the_same_folder_is_fine()
    {
        var (t, f, notes) = Make();
        f.Files.Add(Run + "/discord-ipc-0");
        f.Links[Run + "/discord-ipc-0"] = Run + "/app/com.discordapp.Discord/discord-ipc-0";
        Assert.Equal(new[] { 0 }, t.List());
        Assert.Empty(notes);

        // But not to some other file in there, and not when the Flatpak folder is itself a link.
        f.Links[Run + "/discord-ipc-0"] = Run + "/app/com.discordapp.Discord/something-else";
        Assert.Empty(t.List());
        f.Links[Run + "/discord-ipc-0"] = Run + "/app/com.discordapp.Discord/discord-ipc-0";
        f.LinkedFolders.Add(Run + "/app");
        Assert.Empty(t.List());
    }

    [Fact]
    public void A_folder_on_the_way_that_is_a_link_is_refused()
    {
        var (t, f, notes) = Make();
        f.Files.Add(Run + "/snap.discord/discord-ipc-0");
        f.LinkedFolders.Add(Run + "/snap.discord");
        Assert.Empty(t.List());
        Assert.Contains("on the way", Assert.Single(notes));
    }

    [Fact]
    public void Nothing_is_said_until_somebody_listens_and_then_once()
    {
        var (t, f, notes) = Make();
        t.Note = null;
        f.Files.Add("/tmp/discord-ipc-0");
        t.List();
        t.Note = notes.Add;
        t.List();
        t.List();
        Assert.Single(notes);
    }

    [Fact]
    public void The_real_check_refuses_everything_on_Windows_and_reads_the_mode_elsewhere()
    {
        var real = new RealSocketFiles();
        var dir = Path.Combine(Path.GetTempPath(), "rc-sock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.NotNull(real.WhyNotPrivate(dir));
                return;
            }
            if (Environment.IsPrivilegedProcess)
            {
                Assert.NotNull(real.WhyNotPrivate(dir)); // root is never trusted to tell
                return;
            }
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Null(real.WhyNotPrivate(dir));
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            Assert.Equal(SocketTrust.OpenToOthers, real.WhyNotPrivate(dir));
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // A link to a private folder is still a link.
            var link = dir + "-link";
            Directory.CreateSymbolicLink(link, dir);
            try
            {
                Assert.NotNull(real.WhyNotPrivate(link));
                Assert.True(real.IsLink(link));
                Assert.False(real.IsLink(dir));
            }
            finally { Directory.Delete(link); }

            // A file that is a link says where it ends up; a plain file does not.
            var file = Path.Combine(dir, "plain");
            File.WriteAllText(file, "");
            Assert.Null(real.LinkTarget(file));
            var fileLink = Path.Combine(dir, "discord-ipc-0");
            File.CreateSymbolicLink(fileLink, file);
            Assert.Equal(file, real.LinkTarget(fileLink));
            Assert.NotNull(real.WhyNotPrivate(Path.Combine(dir, "missing")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_standard_transport_finds_nothing_on_Windows()
    {
        // The neutral flavour is not meant to run on Windows; if it does (as in these tests) it must stay quiet.
        if (!OperatingSystem.IsWindows()) return;
        var t = new UnixSocketTransport();
        Assert.Empty(t.List());
        Assert.Null(t.Open(0));
    }
}

/// <summary>A pretend Discord listening on a Unix socket in a throwaway folder.</summary>
internal sealed class SocketDiscord : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _open = new();
    public string Path { get; }
    public List<(int Op, string Json)> Received { get; } = new();
    /// <summary>The answer to the handshake; null hangs up without a word.</summary>
    public Func<string, string?> Hello { get; set; } = _ =>
        """{"cmd":"DISPATCH","evt":"READY","data":{"v":1,"user":{"id":"42","username":"alice","global_name":"Alice"}}}""";

    public SocketDiscord(string path)
    {
        Path = path;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(4);
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptAsync(_cts.Token); }
            catch { return; }
            lock (_open) _open.Add(s);
            _ = Task.Run(() => Serve(s));
        }
    }

    private void Serve(Socket s)
    {
        using var stream = new NetworkStream(s, ownsSocket: true);
        try
        {
            while (true)
            {
                var head = new byte[8];
                if (!Fill(stream, head)) return;
                var body = new byte[BitConverter.ToInt32(head, 4)];
                if (!Fill(stream, body)) return;
                var op = BitConverter.ToInt32(head, 0);
                var json = Encoding.UTF8.GetString(body);
                lock (Received) Received.Add((op, json));
                var answer = op == 0 ? Hello(json) : """{"cmd":"SET_ACTIVITY","evt":null,"data":{}}""";
                if (answer is null) return;
                var data = Encoding.UTF8.GetBytes(answer);
                stream.Write(BitConverter.GetBytes(1));
                stream.Write(BitConverter.GetBytes(data.Length));
                stream.Write(data);
            }
        }
        catch { /* the other end went away */ }
    }

    private static bool Fill(Stream s, byte[] buf)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>Stops listening. The socket file stays behind unless asked, as when Discord is stopped the hard way.</summary>
    public void Stop(bool removeFile)
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Dispose(); } catch { /* ignore */ }
        lock (_open)
        {
            foreach (var s in _open) try { s.Dispose(); } catch { /* ignore */ }
            _open.Clear();
        }
        if (removeFile) try { File.Delete(Path); } catch { /* ignore */ }
    }

    public void Dispose() => Stop(removeFile: true);
}

public class UnixSocketTests : IDisposable
{
    // Kept short: a Unix socket's path may be about 100 characters at most.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _second;

    public UnixSocketTests()
    {
        _second = _dir + "/snap.discord";
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_second);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private UnixSocketTransport Transport(Func<DateTime>? now = null) =>
        new(new[] { new SocketFolder(_dir, _dir), new SocketFolder(_dir, _second) }, new TempFiles(), now);

    [Fact]
    public void A_card_goes_to_Discord_over_the_socket_in_Discords_own_framing()
    {
        using var discord = new SocketDiscord(_dir + "/discord-ipc-0");
        var client = new DiscordClient(Transport());
        Assert.Equal(DiscordLook.Running, client.Look());

        using var c = client.Open(TestIds.Game);
        Assert.True(c.Connect());
        Assert.True(c.Connected);
        Assert.Equal(new DiscordUser("42", "Alice"), c.User);
        Assert.Null(c.SetActivity(new JsonObject { ["type"] = 0, ["name"] = "Test game" }));
        Assert.Null(c.SetActivity(null));

        List<(int Op, string Json)> got;
        lock (discord.Received) got = discord.Received.ToList();
        Assert.Equal(new[] { 0, 1, 1 }, got.Select(g => g.Op));
        // The handshake: version 1 and the application id, nothing else.
        var hello = JsonNode.Parse(got[0].Json)!.AsObject();
        Assert.Equal(new[] { "client_id", "v" }, hello.Select(p => p.Key).OrderBy(k => k));
        Assert.Equal(1, (int)hello["v"]!);
        Assert.Equal(TestIds.Game, (string)hello["client_id"]!);
        // The card: SET_ACTIVITY with this process and the activity.
        var set = JsonNode.Parse(got[1].Json)!;
        Assert.Equal("SET_ACTIVITY", (string)set["cmd"]!);
        Assert.Equal(Environment.ProcessId, (int)set["args"]!["pid"]!);
        Assert.Equal("Test game", (string)set["args"]!["activity"]!["name"]!);
        Assert.False(string.IsNullOrEmpty((string)set["nonce"]!));
        // Taking it down: the same without an activity.
        Assert.Null(JsonNode.Parse(got[2].Json)!["args"]!["activity"]);
    }

    [Fact]
    public void Without_READY_nothing_is_connected_and_a_refused_id_is_remembered()
    {
        using var discord = new SocketDiscord(_dir + "/discord-ipc-0") { Hello = _ => """{"code":4000,"message":"Invalid Client ID"}""" };
        var client = new DiscordClient(Transport());
        using var c = client.Open(TestIds.Game);
        Assert.False(c.Connect());
        Assert.False(c.Connected);
        Assert.Equal("Invalid Client ID", ((DiscordConnection)c).Refused);
        Assert.Throws<IOException>(() => c.SetActivity(new JsonObject()));
    }

    [Fact]
    public void A_made_up_id_never_opens_the_socket()
    {
        using var discord = new SocketDiscord(_dir + "/discord-ipc-0");
        var client = new DiscordClient(Transport());
        using var c = client.Open("not-an-id");
        Assert.False(c.Connect());
        lock (discord.Received) Assert.Empty(discord.Received);
    }

    [Fact]
    public void Every_card_uses_the_same_socket_and_the_second_folder_is_used_when_the_first_has_none()
    {
        using var discord = new SocketDiscord(_second + "/discord-ipc-1");
        var t = Transport();
        Assert.Equal(new[] { 1 }, t.List());
        var client = new DiscordClient(t);
        using var a = client.Open(TestIds.Game);
        using var b = client.Open(TestIds.Music);
        Assert.True(a.Connect());
        Assert.True(b.Connect());
        Assert.Equal(1, client.Chosen);
        lock (discord.Received) Assert.Equal(2, discord.Received.Count(r => r.Op == 0));
    }

    [Fact]
    public void A_socket_file_nobody_listens_on_is_left_alone_until_Discord_makes_it_anew()
    {
        var path = _dir + "/discord-ipc-0";
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = new SocketDiscord(path);
        old.Stop(removeFile: false);
        // Linux and macOS leave the socket file behind. Windows takes it away with the listener, so a plain file
        // stands in for it there: nobody listens on that either.
        if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
        var t = Transport(() => now);
        var client = new DiscordClient(t, () => now);
        Assert.Equal(new[] { 0 }, t.List());
        Assert.Equal(DiscordLook.NoAnswer, client.Look()); // it was there but nobody answered
        Assert.Empty(t.List());                             // so it is not asked again
        Assert.Equal(DiscordLook.NotRunning, client.Look());

        // After a while it is tried once more, in case Discord was only busy.
        now = now.AddMinutes(6);
        Assert.Equal(new[] { 0 }, t.List());
        Assert.Null(t.Open(0));
        Assert.Empty(t.List());

        // Discord starts: it removes the old file and listens on a new one.
        File.Delete(path);
        Thread.Sleep(50);
        using var fresh = new SocketDiscord(path);
        Assert.Equal(new[] { 0 }, t.List());
        using var c = client.Open(TestIds.Game);
        Assert.True(c.Connect());
    }

    [Fact]
    public void When_Discord_goes_away_the_card_is_no_longer_connected()
    {
        var discord = new SocketDiscord(_dir + "/discord-ipc-0");
        var client = new DiscordClient(Transport());
        using var c = client.Open(TestIds.Game);
        Assert.True(c.Connect());
        discord.Dispose();
        // The next update finds out: it fails, and the connection reads as gone afterwards.
        Assert.ThrowsAny<Exception>(() =>
        {
            for (var i = 0; i < 3; i++) c.SetActivity(new JsonObject { ["name"] = "x" });
        });
        Assert.False(c.Connected);
        Assert.Equal(DiscordLook.NotRunning, client.Look());
    }
}

public class ProtocolTests
{
    [Fact]
    public void READY_names_the_account_and_never_needs_more_than_id_and_name()
    {
        Assert.True(DiscordConnection.Ready("""{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"123456","username":"bob","global_name":null}}}""", out var u));
        Assert.Equal(new DiscordUser("123456", "bob"), u);
        Assert.True(DiscordConnection.Ready("""{"evt":"READY","data":{}}""", out u));
        Assert.Null(u);
        Assert.False(DiscordConnection.Ready("""{"evt":"ERROR","data":{"message":"no"}}""", out _));
        Assert.False(DiscordConnection.Ready("not json", out _));
    }
}

public class RunningAppTests
{
    private static readonly IReadOnlySet<string> NoGames = new HashSet<string>();

    [Fact]
    public void A_known_program_is_named_and_a_tool_wins_over_a_browser()
    {
        var app = RunningApps.Pick(new[] { "bash", "firefox", "Blender", "systemd" }, NoGames);
        Assert.NotNull(app);
        Assert.Equal("Blender", app!.Label);
        Assert.Equal("", app.Title); // no window title is ever read
        Assert.StartsWith("https://", app.Icon);
        Assert.Equal("Firefox", RunningApps.Pick(new[] { "bash", "firefox" }, NoGames)!.Label);
        Assert.Null(RunningApps.Pick(new[] { "bash", "systemd", "" }, NoGames));
    }

    [Fact]
    public void A_name_cut_to_15_characters_by_Linux_still_counts()
    {
        Assert.True(RunningApps.Matches("gnome-terminal-", "gnome-terminal-server"));
        Assert.False(RunningApps.Matches("gnome-term", "gnome-terminal-server")); // only a name of exactly 15 is a cut one
        Assert.False(RunningApps.Matches("gnome-terminal-", "gnome-terminal"));
        Assert.True(RunningApps.Matches("Code", "code"));
        Assert.False(RunningApps.Matches("codex", "code"));
        Assert.All(RunningApps.Known, k => Assert.Equal(k.Process, k.Process.ToLowerInvariant()));
    }

    [Fact]
    public void A_program_that_is_one_of_the_users_games_is_not_named_as_an_app()
    {
        Assert.Null(RunningApps.Pick(new[] { "steam" }, new HashSet<string> { "steam" }));
    }

    [Fact]
    public async Task Only_the_users_own_program_list_is_asked_and_nothing_else_is_ever_there()
    {
        var q = new FakeProcesses();
        q.Running.Add(new ProcessEntry(10, "Obsidian"));
        q.Running.Add(new ProcessEntry(11, "MyGame"));
        var inputs = new PortableInputs(q);
        Assert.Equal(new[] { "obsidian", "mygame" }, inputs.Processes().Select(p => p.Name));
        Assert.All(inputs.Processes(), p => Assert.Null(p.StartUtc));
        Assert.Equal("Obsidian", inputs.Foreground(NoGames)!.Label);
        Assert.True(q.Asked >= 3);
        Assert.Null(await inputs.MusicAsync(true, true));
        Assert.Empty(await inputs.SessionsAsync());
        Assert.Empty(inputs.BrowserTitles());

        // Before the module hands it the list, nothing is known to run.
        var early = new PortableInputs();
        Assert.Empty(early.Processes());
        Assert.Null(early.Foreground(NoGames));
    }

    [Fact]
    public async Task The_app_card_shows_a_running_program_and_goes_when_it_closes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var q = new FakeProcesses();
        q.Running.Add(new ProcessEntry(10, "blender"));
        var board = new CardBoard();
        var engine = new PresenceEngine(board, new PortableInputs(q), NullLog.Instance, new HttpClient(new Offline()), () => now, ids: () => TestIds.Ids);
        var s = new PresenceSettings { Enabled = true, ShowCurrentApp = true };
        await engine.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
        now = now.AddSeconds(4);
        await engine.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
        var card = JsonNode.Parse(board.Cards[TestIds.App]!)!;
        Assert.Equal("Blender", (string)card["name"]!);
        Assert.Null(card["details"]);
        Assert.Contains("App: Blender", engine.Now.Status);
        // Nothing but that card: no music, no watching, no game.
        Assert.Equal(new[] { TestIds.App }, board.Cards.Keys);
    }
}

public class PortableModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-presence-portable-" + Guid.NewGuid().ToString("N"));
    public PortableModuleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<(PresenceModule M, CardBoard Board, PortableInputs Inputs, FakeProcesses Q)> Start(PresenceSettings? settings = null)
    {
        var board = new CardBoard();
        var inputs = new PortableInputs();
        var q = new FakeProcesses();
        var m = new PresenceModule(board, inputs, new HttpClient(new Offline()), () => false)
        {
            TickInterval = TimeSpan.FromMilliseconds(20), StartBridge = true, BuiltInIds = TestIds.Ids, Parts = PresenceParts.Portable,
        };
        var store = new SettingsStore(Path.Combine(_dir, "s.json"), NullLog.Instance);
        if (settings is not null) store.Set(PresenceSettings.Section, settings);
        var env = new AppEnvironment(_dir, _dir, false, "1", "x");
        var ctx = new ModuleContext(new EventBus(), NullLog.Instance, store, env)
        {
            Platform = PlatformServices.Create(_dir, false) with { Processes = q },
        };
        await m.StartAsync(ctx, CancellationToken.None);
        return (m, board, inputs, q);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o, Web);

    [Fact]
    public void The_module_says_exactly_what_works_in_this_flavour()
    {
        var everywhere = new[] { "presence.apps", "presence.custom" };
        var windows = new[] { "presence.extension", "presence.games", "presence.launcher", "presence.media", "presence.watching" };
        Assert.Equal(everywhere, PresenceParts.Portable.Capabilities.OrderBy(c => c));
        var m = new PresenceModule(new CardBoard(), new PortableInputs(), new HttpClient(new Offline()), () => false);
        Assert.IsAssignableFrom<IModuleCapabilities>(m);
#if WINDOWS
        Assert.Equal(everywhere.Concat(windows).OrderBy(c => c), m.Capabilities.OrderBy(c => c));
        Assert.NotNull(m.Parts.Library);
        Assert.NotNull(m.Parts.Launcher);
        Assert.NotNull(m.Parts.Bridge);
#else
        Assert.Equal(everywhere, m.Capabilities.OrderBy(c => c));
        Assert.Null(m.Parts.Library);
        Assert.Null(m.Parts.Launcher);
        Assert.Null(m.Parts.Bridge);
        Assert.DoesNotContain(windows, c => m.Capabilities.Contains(c));
#endif
    }

    [Theory]
    [InlineData("rescan", null)]
    [InlineData("play", "steam:1")]
    [InlineData("getArt", "steam:1")]
    [InlineData("setGameEnabled", "steam:1")]
    [InlineData("getExtension", null)]
    public async Task What_needs_Windows_answers_not_available_on_this_system(string method, string? id)
    {
        var (m, _, _, _) = await Start();
        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync(method, id is null ? null : Args(new { id, enabled = false }), CancellationToken.None));
            Assert.Equal(RpcErrorCodes.Unavailable, ex.Code);
            Assert.StartsWith(ModuleAvailability.NotOnThisSystem, ex.Message);
        }
        finally { await m.DisposeAsync(); }
    }

    [Fact]
    public async Task Status_and_custom_statuses_work_without_any_Windows_part()
    {
        // A saved game list from somewhere else is not read, and nothing is scanned.
        Directory.CreateDirectory(Path.Combine(_dir, "presence"));
        File.WriteAllText(Path.Combine(_dir, "presence", "library.json"), """[{"id":"steam:1","name":"X","exes":["x"]}]""");
        var (m, board, inputs, q) = await Start(new PresenceSettings { Enabled = true });
        try
        {
            Assert.Same(q, inputs.Query); // the running programs come from RigControl's own list
            var st = JsonSerializer.SerializeToElement(await m.InvokeAsync("getStatus", null, CancellationToken.None), Web);
            Assert.Equal(0, st.GetProperty("games").GetArrayLength());
            Assert.False(st.GetProperty("scanning").GetBoolean());
            Assert.False(st.GetProperty("extensionLinked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, st.GetProperty("extensionPort").ValueKind);
            Assert.Equal(JsonValueKind.Null, st.GetProperty("importedFromRichPresence").ValueKind);

            await m.InvokeAsync("setCustomStatuses", Args(new { items = new[] { new { id = "one", enabled = true, name = "Reading", details = "A book" } }, mode = "separate" }), CancellationToken.None);
            for (var i = 0; i < 100 && board.Cards.Count == 0; i++) await Task.Delay(20);
            var card = Assert.Single(board.Cards);
            Assert.Contains("Reading", card.Value);
            Assert.False(File.Exists(Path.Combine(_dir, "presence", "extension-link.key")));
        }
        finally { await m.DisposeAsync(); }
    }
}
