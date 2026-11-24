using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Presence;

namespace RigControl.Presence.Tests;

/// <summary>
/// The Discord application ids: encrypted into the dll by the build, never readable as text, and never needed for
/// the app to stay up. No assertion here prints an id when it fails.
/// </summary>
public class AppIdsTests
{
    private static readonly KeyValuePair<string, string>[] Pairs =
    {
        new("game", "300000000000000001"), new("music", "300000000000000002"), new("app", "30000000000000000003"),
        new("primeVideo", "30000000000000004"), new("custom1", "300000000000000005"),
    };

    private static bool Has(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    /// <summary>True when the text can be read in the bytes as plain text, the two ways a .NET file stores text.</summary>
    private static bool Readable(byte[] bytes, string text) =>
        Has(bytes, Encoding.ASCII.GetBytes(text)) || Has(bytes, Encoding.Unicode.GetBytes(text));

    private static byte[] Resource(System.Reflection.Assembly a, string name)
    {
        using var s = a.GetManifestResourceStream(name) ?? throw new InvalidOperationException("The resource " + name + " is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Ids_come_back_the_same_after_being_encrypted_and_decrypted()
    {
        var blob = AppIds.Seal(AppIds.ToText(Pairs));
        foreach (var (_, id) in Pairs) Assert.False(Readable(blob, id), "an id can be read in the encrypted bytes");
        Assert.False(Readable(blob, "music"));

        var back = AppIds.Parse(AppIds.Open(blob)!);
        Assert.Equal(Pairs.Select(p => p.Key), back.Have);
        foreach (var (name, id) in Pairs) Assert.True(back.Get(name) == id, name + " came back different");
        Assert.True(back.Game == Pairs[0].Value && back.Music == Pairs[1].Value && back.App == Pairs[2].Value);
        Assert.Equal(new[] { Pairs[4].Value }, back.Pool);
        Assert.True(back.SetUp);

        // The same ids always give the same bytes (repeatable builds); other ids give other bytes.
        Assert.Equal(blob, AppIds.Seal(AppIds.ToText(Pairs)));
        Assert.NotEqual(blob, AppIds.Seal(AppIds.ToText(Pairs.Take(4))));
    }

    [Fact]
    public void Bytes_that_were_changed_or_cut_short_are_not_opened()
    {
        var blob = AppIds.Seal(AppIds.ToText(Pairs));
        for (var i = 0; i < blob.Length; i += 7)
        {
            var bad = (byte[])blob.Clone();
            bad[i] ^= 0x01;
            Assert.Null(AppIds.Open(bad));
        }
        Assert.Null(AppIds.Open(blob[..^1]));
        Assert.Null(AppIds.Open(blob[..20]));
        Assert.Null(AppIds.Open(Array.Empty<byte>()));
        Assert.Null(AppIds.Open(Encoding.ASCII.GetBytes("{ \"game\": \"300000000000000001\" }")));
        // Whatever is in there, reading the built-in ids never throws.
        Assert.NotNull(AppIds.LoadEmbedded());
        Assert.False(AppIds.Parse("").SetUp);
        Assert.False(AppIds.Parse("nonsense\n=1\ngame=12\n").SetUp);
    }

    [Fact]
    public void The_build_step_and_the_app_use_the_same_recipe()
    {
        // The test project's build ran the real build step (DiscordIds.targets) on made-up-discord-ids.json.
        var mine = typeof(AppIdsTests).Assembly;
        var sealedByBuild = Resource(mine, "RigControl.Presence.Tests.MadeUpIds");
        using var doc = JsonDocument.Parse(Resource(mine, "RigControl.Presence.Tests.MadeUpIds.json").AsSpan().TrimStart(new byte[] { 0xEF, 0xBB, 0xBF }).ToArray());
        var pairs = doc.RootElement.EnumerateObject().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)).ToList();
        Assert.True(pairs.Count >= 5);

        var text = AppIds.Open(sealedByBuild);
        Assert.NotNull(text); // the app can open what the build made
        var ids = AppIds.Parse(text!);
        Assert.Equal(pairs.Select(p => p.Key), ids.Have);
        foreach (var (name, id) in pairs) Assert.True(ids.Get(name) == id, name + " came out different");
        // Byte for byte what the app's own code would write, ids of 17, 18 and 20 digits alike.
        Assert.Equal(AppIds.Seal(AppIds.ToText(pairs)), sealedByBuild);
        foreach (var (_, id) in pairs) Assert.False(Readable(sealedByBuild, id), "an id can be read in what the build made");
    }

    private static string? PrivateFile([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        var f = Path.Combine(root, "private", "discord-ids.json");
        return File.Exists(f) ? f : null;
    }

    [Fact]
    public void No_id_can_be_read_as_text_in_the_built_program()
    {
        var dll = typeof(AppIds).Assembly.Location;
        var bytes = File.ReadAllBytes(dll);
        // The search itself works: it finds text the dll is known to hold, in both forms.
        Assert.True(Has(bytes, Encoding.Unicode.GetBytes(AppIds.ResourceName)));        // a string in the code
        Assert.True(Has(bytes, Encoding.ASCII.GetBytes(AppIds.ResourceName)) || AppIds.LoadEmbedded().Have.Count == 0); // the resource's name
        Assert.True(Has(bytes, Encoding.ASCII.GetBytes(nameof(PresenceEngine))));

        // Every id this build carries, and (in the private repository) every id in the private file.
        var built = AppIds.LoadEmbedded();
        var ids = built.Have.Select(n => built.Get(n)!).ToHashSet();
        if (PrivateFile() is { } file)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var p in doc.RootElement.EnumerateObject()) ids.Add(p.Value.GetString()!);
        }
        var files = new List<string> { dll };
        var pdb = Path.ChangeExtension(dll, ".pdb");
        if (File.Exists(pdb)) files.Add(pdb);
        foreach (var f in files)
        {
            var b = File.ReadAllBytes(f);
            var n = 0;
            foreach (var id in ids)
            {
                n++;
                Assert.False(Has(b, Encoding.ASCII.GetBytes(id)), $"id number {n} can be read as plain text in {Path.GetFileName(f)}");
                Assert.False(Has(b, Encoding.Unicode.GetBytes(id)), $"id number {n} can be read as UTF-16 text in {Path.GetFileName(f)}");
            }
        }
        // And nothing that looks like an id at all sits in the dll's text: 17 to 20 digits in a row, either form.
        Assert.False(DigitRun(bytes, 1), "the dll holds 17 or more digits in a row as plain text");
        Assert.False(DigitRun(bytes, 2), "the dll holds 17 or more digits in a row as UTF-16 text");
    }

    /// <summary>True when 17 or more digits follow each other (step 1 = ASCII, step 2 = UTF-16).</summary>
    private static bool DigitRun(byte[] b, int step)
    {
        for (var start = 0; start < step; start++)
        {
            var run = 0;
            for (var i = start; i + step - 1 < b.Length; i += step)
            {
                var digit = b[i] is >= (byte)'0' and <= (byte)'9' && (step == 1 || b[i + 1] == 0);
                run = digit ? run + 1 : 0;
                if (run >= 17) return true;
            }
        }
        return false;
    }

    [Fact]
    public void In_the_private_repository_the_build_carries_exactly_the_ids_of_the_private_file()
    {
        if (PrivateFile() is not { } file) return; // the public source has no such file, and no ids of its own
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var pairs = doc.RootElement.EnumerateObject().Select(p => (p.Name, Id: p.Value.GetString()!)).ToList();
        var built = AppIds.LoadEmbedded();
        Assert.True(built.SetUp, "private/discord-ids.json is there but this build carries no ids");
        Assert.Equal(pairs.Select(p => p.Name), built.Have);
        foreach (var (name, id) in pairs) Assert.True(built.Get(name) == id, name + " is not the id from the private file");
        Assert.Empty(built.MissingMain);
    }

    [Fact]
    public void Ids_someone_brings_themselves_are_checked()
    {
        var ids = AppIds.From(new Dictionary<string, string>
        {
            ["game"] = "400000000000000001",          // fine
            ["MUSIC"] = " 40000000000000002 ",        // fine: 17 digits, any spelling of the name, spaces around
            ["app"] = "4000000000000003",             // 16 digits
            ["netflix"] = "400000000000000000004",    // 21 digits
            ["hulu"] = "40000000000000000x",          // not digits
            ["disney"] = "400000000000000001",        // the game card's id again
            ["twitch"] = "400000000000000006",        // no such card
            ["max"] = "",                             // empty = none
            ["custom2"] = "40000000000000000007",     // fine: 20 digits
        }, out var refused);

        Assert.Equal(new[] { "game", "music", "custom2" }, ids.Have);
        Assert.True(ids.SetUp);
        Assert.Equal(new[] { "app" }, ids.MissingMain);
        Assert.Equal(new[] { "app", "netflix", "hulu", "disney", "twitch" }, refused.Select(r => r.Name));
        Assert.Contains("17 to 20 digits", refused[0].Why);
        Assert.Contains("each card needs its own", refused[3].Why);
        Assert.Contains("no card with that name", refused[4].Why);
        // The reasons never repeat an id.
        Assert.All(refused, r => Assert.DoesNotMatch(@"\d{6,}", r.Name + r.Why));

        Assert.True(AppIds.IsId("12345678901234567"));
        Assert.True(AppIds.IsId("12345678901234567890"));
        Assert.False(AppIds.IsId("1234567890123456"));
        Assert.False(AppIds.IsId("123456789012345678901"));
        Assert.False(AppIds.IsId(" 12345678901234567"));
        Assert.False(AppIds.IsId("١٢٣٤٥٦٧٨٩٠١٢٣٤٥٦٧")); // digits, but not 0 to 9
        Assert.False(AppIds.IsId(""));
        Assert.False(AppIds.IsId(null));
    }

    [Fact]
    public void A_name_that_is_really_an_id_is_not_repeated_in_the_reason()
    {
        AppIds.From(new Dictionary<string, string> { ["400000000000000001"] = "400000000000000002" }, out var refused);
        Assert.Equal("(id)", Assert.Single(refused).Name);
    }

    [Fact]
    public void Your_own_ids_win_over_the_built_in_ones_and_an_id_sits_under_one_name_only()
    {
        var own = AppIds.From(new Dictionary<string, string> { ["music"] = "400000000000000009", ["custom1"] = TestIds.App }, out _);
        var both = TestIds.Ids.With(own);
        Assert.True(both.Music == "400000000000000009");
        Assert.True(both.Game == TestIds.Game);
        Assert.Null(both.App);                       // its id was given to custom1
        Assert.True(both.Pool[0] == TestIds.App);
        Assert.False(both.Contains(TestIds.Music));  // the built-in music id is no longer in use
    }

    [Fact]
    public void Ids_are_taken_out_of_text_before_it_is_logged()
    {
        Assert.Equal("Invalid Client ID (id).", AppIds.Scrub("Invalid Client ID 123456789012345678."));
        Assert.Equal("code 4000, pid 1234", AppIds.Scrub("code 4000, pid 1234"));
        Assert.Equal("", AppIds.Scrub(null));
    }
}

/// <summary>A build without ids (the public source built as it is) and what it does.</summary>
public class NotSetUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-presence-tests-" + Guid.NewGuid().ToString("N"));
    public NotSetUpTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<(PresenceModule Module, SettingsStore Store)> Start(FakeDiscord discord, FakeInputs inputs, ILog log, AppIds builtIn, PresenceSettings settings)
    {
        var m = new PresenceModule(discord, inputs, new HttpClient(new NoNetwork()), () => false)
        {
            TickInterval = TimeSpan.FromMilliseconds(20), StartBridge = false, BuiltInIds = builtIn,
        };
        var store = new SettingsStore(Path.Combine(_dir, "s.json"), NullLog.Instance);
        settings.ImportedFromRichPresence = DateTimeOffset.Now;
        store.Set(PresenceSettings.Section, settings);
        Directory.CreateDirectory(Path.Combine(_dir, "presence"));
        File.WriteAllText(Path.Combine(_dir, "presence", "library.json"), "[]");
        await m.StartAsync(new ModuleContext(new EventBus(), log, store, new AppEnvironment(_dir, _dir, true, "1", "x.exe")), CancellationToken.None);
        return (m, store);
    }

    private static async Task<JsonElement> Status(PresenceModule m, Func<JsonElement, bool>? until = null)
    {
        JsonElement st = default;
        for (var i = 0; i < 100; i++)
        {
            st = JsonSerializer.SerializeToElement(await m.InvokeAsync("getStatus", null, CancellationToken.None), Web);
            if (until is null || until(st)) break;
            await Task.Delay(20);
        }
        return st;
    }

    private static PresenceSettings On() => new() { Enabled = true, ShowLyrics = false, ShowAlbumArt = false, ShowCurrentApp = true };

    [Fact]
    public async Task Without_ids_the_engine_opens_nothing_sends_nothing_and_says_so()
    {
        var discord = new FakeDiscord();
        var inputs = new FakeInputs
        {
            Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100),
            App = new ForegroundApp("notepad", "Notepad", "x", null),
        };
        inputs.Procs.Add(new RunningProcess("indie", DateTime.UtcNow.AddMinutes(-5)));
        inputs.Sessions.Add(new MediaSession("msedge", true, "Stranger Things", "www.netflix.com", 3000, 60));
        var s = On();
        s.CustomStatuses.Add(new CustomStatus { Id = "a", Name = "Studying", Enabled = true });
        var e = new PresenceEngine(discord, inputs, NullLog.Instance, new HttpClient(new NoNetwork()), ids: () => AppIds.Empty);
        var game = new Game { Id = "folder:x", Name = "Indie Game", Exes = { "indie" } };

        e.Probe();
        for (var i = 0; i < 3; i++) await e.TickAsync(s, new[] { game }, CancellationToken.None);

        Assert.Equal(0, discord.OpenCount); // not even a connection
        Assert.Empty(discord.Sent);
        Assert.Empty(discord.Cards);
        Assert.Equal(new[] { "Studying" }, e.Now.CustomSkipped); // no card to borrow
        Assert.Null(e.NextSendIn);
    }

    [Fact]
    public async Task Without_ids_and_nothing_to_show_the_status_reads_isn_t_set_up_in_this_build()
    {
        var discord = new FakeDiscord();
        var e = new PresenceEngine(discord, new FakeInputs(), NullLog.Instance, new HttpClient(new NoNetwork()), ids: () => AppIds.Empty);
        await e.TickAsync(On(), Array.Empty<Game>(), CancellationToken.None);
        Assert.Equal("Discord status isn't set up in this build", e.Now.Status);
        Assert.Equal(0, discord.OpenCount);

        // With ids the same quiet moment reads as before.
        var withIds = new PresenceEngine(discord, new FakeInputs(), NullLog.Instance, new HttpClient(new NoNetwork()));
        await withIds.TickAsync(On(), Array.Empty<Game>(), CancellationToken.None);
        Assert.Equal("Watching for games", withIds.Now.Status);
    }

    [Fact]
    public async Task A_game_Discord_knows_and_a_custom_status_with_its_own_id_still_show_in_a_build_without_ids()
    {
        var discord = new FakeDiscord();
        var inputs = new FakeInputs();
        inputs.Procs.Add(new RunningProcess("official", DateTime.UtcNow.AddMinutes(-5)));
        var s = On();
        s.CustomStatuses.Add(new CustomStatus { Id = "a", Name = "Mine", Enabled = true, AppId = "500000000000000001" });
        var e = new PresenceEngine(discord, inputs, NullLog.Instance, new HttpClient(new NoNetwork()), ids: () => AppIds.Empty);
        var game = new Game { Id = "steam:1", Name = "Official Game", Exes = { "official" }, DiscordId = "500000000000000002" };
        await e.TickAsync(s, new[] { game }, CancellationToken.None);
        Assert.Equal(new[] { "500000000000000001", "500000000000000002" }, discord.Cards.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task A_library_entry_with_a_broken_Discord_id_never_reaches_Discord()
    {
        var discord = new FakeDiscord();
        var inputs = new FakeInputs();
        inputs.Procs.Add(new RunningProcess("official", DateTime.UtcNow.AddMinutes(-5)));
        var e = new PresenceEngine(discord, inputs, NullLog.Instance, new HttpClient(new NoNetwork()), ids: () => AppIds.Empty);
        foreach (var bad in new[] { "", " ", "12345", "not-an-id" })
        {
            var game = new Game { Id = "steam:1", Name = "Official Game", Exes = { "official" }, DiscordId = bad };
            await e.TickAsync(On(), new[] { game }, CancellationToken.None);
        }
        Assert.Equal(0, discord.OpenCount);
    }

    [Fact]
    public void A_connection_without_a_real_id_opens_no_pipe_and_sends_nothing()
    {
        var t = new FakeTransport();
        t.Pipes[0] = new FakePipe();
        var d = new DiscordClient(t, () => DateTime.UtcNow);
        foreach (var bad in new[] { "", "0", "abc", "1234567890123456" })
        {
            using var c = d.Open(bad);
            Assert.False(c.Connect());
            Assert.False(c.Connected);
            Assert.Throws<IOException>(() => c.SetActivity(new System.Text.Json.Nodes.JsonObject { ["name"] = "x" }));
        }
        Assert.Equal(0, t.Pipes[0].Opens);
        Assert.Empty(t.Pipes[0].Received);
    }

    [Fact]
    public async Task The_page_is_told_that_the_status_isn_t_set_up_and_what_to_do()
    {
        var log = new ListLog();
        var discord = new FakeDiscord { User = new DiscordUser("1", "Alice") };
        var inputs = new FakeInputs { Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100) };
        var (m, _) = await Start(discord, inputs, log, AppIds.Empty, On());
        var st = await Status(m, s => s.GetProperty("status").GetString() == PresenceEngine.NotSetUp);

        Assert.Equal("Discord status isn't set up in this build", st.GetProperty("status").GetString());
        Assert.False(st.GetProperty("setUp").GetBoolean());
        Assert.StartsWith("Discord status isn't set up in this build.", st.GetProperty("setupNote").GetString());
        Assert.Contains("presence > appIds", st.GetProperty("setupNote").GetString());
        Assert.Empty(st.GetProperty("ownAppIds").EnumerateArray());
        await Task.Delay(100);
        await m.DisposeAsync();
        Assert.Equal(0, discord.OpenCount);
        Assert.Empty(discord.Sent);
        lock (log.Lines) Assert.Single(log.Lines, l => l.Contains("isn't set up in this build")); // said once
    }

    [Fact]
    public async Task A_build_with_ids_reports_that_it_is_set_up()
    {
        var (m, _) = await Start(new FakeDiscord(), new FakeInputs(), NullLog.Instance, TestIds.Ids, new PresenceSettings());
        var st = await Status(m);
        Assert.True(st.GetProperty("setUp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("setupNote").ValueKind);
        await m.DisposeAsync();
    }

    [Fact]
    public async Task Your_own_ids_in_the_settings_file_make_a_build_without_ids_work_and_wrong_ones_are_left_out()
    {
        const string music = "600000000000000001", game = "60000000000000000002";
        var log = new ListLog();
        var discord = new FakeDiscord();
        var inputs = new FakeInputs { Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100), App = new ForegroundApp("notepad", "Notepad", "x", null) };
        var s = On();
        s.AppIds = new Dictionary<string, string> { ["Music"] = music, ["game"] = game, ["app"] = "12345", ["nonsense"] = "600000000000000003" };
        var (m, _) = await Start(discord, inputs, log, AppIds.Empty, s);
        for (var i = 0; i < 100 && !discord.Cards.ContainsKey(music); i++) await Task.Delay(20);

        Assert.Contains("Song", discord.Cards[music]);
        var st = await Status(m);
        Assert.True(st.GetProperty("setUp").GetBoolean());
        Assert.Equal(new[] { "game", "music" }, st.GetProperty("ownAppIds").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("no Discord application id for the app card", st.GetProperty("setupNote").GetString());
        // The ids themselves are never handed back to the page.
        Assert.DoesNotContain(music, st.GetRawText());
        Assert.DoesNotContain(game, st.GetRawText());
        await Task.Delay(100);
        await m.DisposeAsync();

        Assert.All(discord.Sent, x => Assert.True(x.ClientId == music)); // the app card had no valid id: never opened
        lock (log.Lines)
        {
            Assert.Single(log.Lines, l => l.Contains("\"app\"") && l.Contains("17 to 20 digits"));
            Assert.Single(log.Lines, l => l.Contains("\"nonsense\"") && l.Contains("no card with that name"));
            // Not one line of the log holds an id.
            Assert.All(log.Lines, l => Assert.DoesNotMatch(@"\d{17,20}", l));
        }
    }

    [Fact]
    public async Task Ids_saved_from_the_app_are_checked_first_and_take_effect_at_once()
    {
        const string music = "700000000000000001";
        var log = new ListLog();
        var discord = new FakeDiscord();
        var inputs = new FakeInputs { Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100) };
        var (m, store) = await Start(discord, inputs, log, AppIds.Empty, On());

        foreach (var (bad, why) in new (object Ids, string Why)[]
        {
            (new { music = "1234" }, "17 to 20 digits"),
            (new { music = "70000000000000000x" }, "17 to 20 digits"),
            (new { twitch = music }, "no card called"),
            (new { music, game = music }, "Each card needs its own"),
        })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("setAppIds", JsonSerializer.SerializeToElement(new { ids = bad }), CancellationToken.None));
            Assert.Equal(RpcErrorCodes.InvalidArgs, ex.Code);
            Assert.Contains(why, ex.Message);
            Assert.DoesNotMatch(@"\d{17,20}", ex.Message);
        }
        await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("setAppIds", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Empty(store.Get<PresenceSettings>(PresenceSettings.Section).AppIds); // nothing was saved
        Assert.Equal(0, discord.OpenCount);

        var st = JsonSerializer.SerializeToElement(await m.InvokeAsync("setAppIds", JsonSerializer.SerializeToElement(new { ids = new { MUSIC = music, app = "" } }), CancellationToken.None), Web);
        Assert.True(st.GetProperty("setUp").GetBoolean());
        Assert.Equal(new[] { "music" }, st.GetProperty("ownAppIds").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(music, st.GetRawText());
        var saved = store.Get<PresenceSettings>(PresenceSettings.Section).AppIds;
        Assert.True(saved.Count == 1 && saved["music"] == music);
        for (var i = 0; i < 100 && !discord.Cards.ContainsKey(music); i++) await Task.Delay(20);
        Assert.True(discord.Cards.ContainsKey(music)); // no restart needed

        // Taking them away again takes the card down.
        st = JsonSerializer.SerializeToElement(await m.InvokeAsync("setAppIds", JsonSerializer.SerializeToElement(new { ids = new { } }), CancellationToken.None), Web);
        Assert.False(st.GetProperty("setUp").GetBoolean());
        for (var i = 0; i < 100 && discord.Cards.Count > 0; i++) await Task.Delay(20);
        Assert.Empty(discord.Cards);
        await m.DisposeAsync();
        lock (log.Lines) Assert.All(log.Lines, l => Assert.DoesNotMatch(@"\d{17,20}", l));
    }

    [Fact]
    public async Task Discord_s_own_error_text_is_logged_without_the_id()
    {
        var log = new ListLog();
        var discord = new FakeDiscord { Answer = id => "Unknown application " + id };
        var inputs = new FakeInputs { Music = new MusicNow("Spotify", "Song", "Band", "", true, 1, 100) };
        var e = new PresenceEngine(discord, inputs, log, new HttpClient(new NoNetwork()));
        await e.TickAsync(On(), Array.Empty<Game>(), CancellationToken.None);
        lock (log.Lines)
        {
            Assert.Contains(log.Lines, l => l.Contains("Discord didn't accept the music card: Unknown application (id)"));
            Assert.All(log.Lines, l => Assert.DoesNotContain(TestIds.Music, l));
        }
    }
}
