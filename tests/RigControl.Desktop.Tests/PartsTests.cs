using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using RigControl.Core.Logging;
using RigControl.Hosting;

namespace RigControl.Desktop.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigcontrol-desktop-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Collects log lines so a test can read them.</summary>
internal sealed class ListLog : ILog
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Info(string message) => Lines.Enqueue("info " + message);
    public void Warn(string message, Exception? ex = null) => Lines.Enqueue("warn " + message);
    public void Error(string message, Exception? ex = null) => Lines.Enqueue("error " + message);
    public ILog For(string source) => this;
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(15);
        }
        return condition();
    }
}

public class StartOptionsTests
{
    [Fact]
    public void Nothing_asked_means_a_normal_start()
    {
        var o = StartOptions.Parse(Array.Empty<string>());
        Assert.False(o.Smoke);
        Assert.Null(o.Ui);
        Assert.False(o.Help);
        Assert.Null(o.Unknown);
    }

    [Theory]
    [InlineData("--ui=scheme", "scheme")]
    [InlineData("--ui=CLASSIC", "classic")]
    public void The_way_to_load_the_page_can_be_chosen(string arg, string expected)
        => Assert.Equal(expected, StartOptions.Name(StartOptions.Parse(new[] { "--smoke", arg }).Ui!.Value));

    [Fact]
    public void The_local_server_way_is_gone_and_counts_as_unknown()
    {
        // RigControl opens no network port, so there is no way of loading the page through one.
        Assert.Equal("--ui=loopback", StartOptions.Parse(new[] { "--ui=loopback" }).Unknown);
        Assert.Null(StartOptions.ParseMode("loopback"));
        Assert.Equal(new[] { UiMode.Scheme, UiMode.Classic }, Enum.GetValues<UiMode>());
        Assert.DoesNotContain("loopback", StartOptions.HelpText);
    }

    [Fact]
    public void Smoke_help_and_version_are_read()
    {
        Assert.True(StartOptions.Parse(new[] { "--smoke" }).Smoke);
        Assert.True(StartOptions.Parse(new[] { "--help" }).Help);
        Assert.True(StartOptions.Parse(new[] { "--version" }).Version);
    }

    [Fact]
    public void An_unknown_option_is_reported_and_a_wrong_way_counts_as_unknown()
    {
        Assert.Equal("--tray", StartOptions.Parse(new[] { "--tray" }).Unknown);
        Assert.Equal("--ui=ftp", StartOptions.Parse(new[] { "--ui=ftp" }).Unknown);
    }

    [Fact]
    public void The_argument_macOS_adds_when_opened_from_Finder_is_ignored()
        => Assert.Null(StartOptions.Parse(new[] { "-psn_0_12345" }).Unknown);
}

public class StartupGateTests
{
    private static string? None(string _) => null;

    [Fact]
    public void It_refuses_on_Windows_before_anything_else()
        => Assert.Equal(StartupGate.OnWindows, StartupGate.Check(isWindows: true, isLinux: false, isRoot: true, None));

    [Fact]
    public void It_refuses_as_root()
        => Assert.Equal(StartupGate.AsRoot, StartupGate.Check(false, true, isRoot: true, k => k == "DISPLAY" ? ":0" : null));

    [Fact]
    public void Linux_without_a_desktop_session_is_refused()
        => Assert.Equal(StartupGate.NoDesktop, StartupGate.Check(false, isLinux: true, false, None));

    [Theory]
    [InlineData("DISPLAY")]
    [InlineData("WAYLAND_DISPLAY")]
    public void Linux_with_a_desktop_session_starts(string variable)
        => Assert.Null(StartupGate.Check(false, isLinux: true, false, k => k == variable ? "x" : null));

    [Fact]
    public void macOS_needs_no_such_variable()
        => Assert.Null(StartupGate.Check(false, isLinux: false, false, None));

    [Fact]
    public void Every_sentence_is_plain_text_on_one_line()
    {
        foreach (var s in new[] { StartupGate.OnWindows, StartupGate.AsRoot, StartupGate.NoDesktop, StartupGate.AlreadyRunning, StartupGate.NoWebView, StartupGate.PageNeverConnected })
        {
            Assert.DoesNotContain('\n', s);
            Assert.EndsWith(".", s);
        }
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void The_second_start_is_turned_away_until_the_first_has_closed()
    {
        using var dir = new TempFolder();
        var first = SingleInstance.TryTake(dir.Path, out var problem);
        Assert.NotNull(first);
        Assert.Null(problem);
        Assert.True(first!.Held);

        Assert.Null(SingleInstance.TryTake(dir.Path, out _));

        first.Dispose();
        using var again = SingleInstance.TryTake(dir.Path, out _);
        Assert.NotNull(again);
        Assert.True(again!.Held);
    }

    [Fact]
    public void A_lock_file_left_by_a_crash_does_not_lock_RigControl_out()
    {
        using var dir = new TempFolder();
        File.WriteAllText(Path.Combine(dir.Path, SingleInstance.FileName), "left behind");
        using var taken = SingleInstance.TryTake(dir.Path, out _);
        Assert.NotNull(taken);
        Assert.True(taken!.Held);
    }

    [Fact]
    public void A_folder_that_is_not_there_does_not_stop_RigControl_starting()
    {
        using var dir = new TempFolder();
        using var taken = SingleInstance.TryTake(Path.Combine(dir.Path, "missing", "deeper"), out var problem);
        Assert.NotNull(taken);
        Assert.False(taken!.Held);
        Assert.NotNull(problem);
    }

    [Fact]
    public void On_Linux_and_macOS_the_lock_file_is_for_its_owner_only()
    {
        if (OperatingSystem.IsWindows()) return; // file modes are a Linux and macOS thing
        using var dir = new TempFolder();
        using var taken = SingleInstance.TryTake(dir.Path, out _);
        var mode = File.GetUnixFileMode(Path.Combine(dir.Path, SingleInstance.FileName));
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
    }
}

public class UiSiteTests
{
    private const string BuiltIndex =
        "<!doctype html>\n<html lang=\"en\">\n  <head>\n    <meta charset=\"utf-8\" />\n" +
        "    <meta http-equiv=\"Content-Security-Policy\"\n          content=\"default-src 'self'; connect-src 'self' ws://localhost:*\" />\n" +
        "    <title>RigControl</title>\n" +
        "    <script type=\"module\" crossorigin src=\"/assets/index-abc.js\"></script>\n" +
        "    <link rel=\"modulepreload\" crossorigin href=\"/assets/vendor-xyz.js\">\n" +
        "    <link rel=\"stylesheet\" crossorigin href=\"/assets/index-def.css\">\n" +
        "  </head>\n  <body><div id=\"root\"></div></body>\n</html>";

    private static UiSite Fake(string js = "const a=1;console.log(a);")
    {
        var files = new Dictionary<string, (string Text, string Type)>
        {
            ["/index.html"] = (BuiltIndex, "text/html; charset=utf-8"),
            ["/assets/index-abc.js"] = (js, "text/javascript; charset=utf-8"),
            ["/assets/index-def.css"] = ("@font-face{src:url(/assets/font.woff2)}body{color:red}", "text/css; charset=utf-8"),
            ["/assets/font.woff2"] = ("font", "font/woff2"),
        };
        return new UiSite((string path, out Stream stream, out string type) =>
        {
            if (files.TryGetValue(path, out var f))
            {
                stream = new MemoryStream(Encoding.UTF8.GetBytes(f.Text));
                type = f.Type;
                return true;
            }
            stream = Stream.Null;
            type = "text/plain";
            return false;
        });
    }

    private static string Text(UiResponse? r) => Encoding.UTF8.GetString(Assert.IsType<UiResponse>(r).Body);

    [Fact]
    public void The_page_gets_this_windows_own_policy_and_only_one()
    {
        var html = Text(Fake().ForSchemeUrl("app://rigcontrol/index.html"));
        Assert.Single(Regex.Matches(html, "Content-Security-Policy"));
        Assert.Contains("connect-src 'none'", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("form-action 'none'", html);
        Assert.Contains("script-src 'self' app:;", html);
        Assert.DoesNotContain("ws://", html);
        Assert.DoesNotContain("unsafe-eval", html);
        // The script may only come from RigControl's own files: no inline script is allowed.
        Assert.DoesNotContain("script-src 'self' app: 'unsafe-inline'", html);
    }

    [Fact]
    public void This_runs_key_is_written_into_the_page_once_and_a_key_the_file_brought_is_taken_out()
    {
        const string key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string page = "<html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'\" />" +
                            "<meta name=\"rigcontrol-page-key\" content=\"planted\" /><title>x</title></head><body></body></html>";
        var html = UiSite.Index(page, classic: false, pageKey: key);
        Assert.Single(Regex.Matches(html, PageKey.MetaName));
        Assert.Contains($"<meta name=\"{PageKey.MetaName}\" content=\"{key}\" />", html);
        Assert.DoesNotContain("planted", html);
        // The same in both ways of loading the page.
        Assert.Contains(key, UiSite.Index(page, classic: true, pageKey: key));
        // Without a key none is written, and nothing but letters and digits is ever written into the page.
        Assert.DoesNotContain(PageKey.MetaName, UiSite.Index(page, false));
        Assert.DoesNotContain(PageKey.MetaName, UiSite.Index(page, false, pageKey: "a\"><script>"));
    }

    [Fact]
    public void The_usual_way_leaves_the_module_script_as_built()
    {
        var html = Text(Fake().ForSchemeUrl("app://rigcontrol/"));
        Assert.Contains("<script type=\"module\" crossorigin src=\"/assets/index-abc.js\"></script>", html);
    }

    [Fact]
    public void Content_types_are_handed_over_bare()
    {
        var site = Fake();
        Assert.Equal("text/html", site.ForSchemeUrl("app://rigcontrol/index.html")!.ContentType);
        Assert.Equal("text/javascript", site.ForSchemeUrl("app://rigcontrol/assets/index-abc.js")!.ContentType);
        Assert.Equal("text/css", site.ForSchemeUrl("app://rigcontrol/assets/index-def.css")!.ContentType);
        Assert.Equal("font/woff2", site.ForSchemeUrl("app://rigcontrol/assets/font.woff2")!.ContentType);
    }

    [Fact]
    public void Classic_turns_the_module_script_into_a_plain_deferred_one()
    {
        var site = Fake();
        var html = Text(site.ForSchemeUrl(UiSite.SchemeAddress(classic: true).ToString()));
        Assert.Contains("<script defer src=\"/assets/index-abc.js?classic=1\"></script>", html);
        Assert.DoesNotContain("type=\"module\"", html);
        Assert.DoesNotContain("modulepreload", html);
        Assert.DoesNotContain("crossorigin", html);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/assets/index-def.css\">", html);

        var js = Text(site.ForSchemeUrl("app://rigcontrol/assets/index-abc.js?classic=1"));
        Assert.StartsWith("(function(){\"use strict\";", js);
        Assert.EndsWith("})();", js);
        Assert.Contains("const a=1;console.log(a);", js);
        // Asked for the usual way, the same file is untouched.
        Assert.Equal("const a=1;console.log(a);", Text(site.ForSchemeUrl("app://rigcontrol/assets/index-abc.js")));
    }

    [Theory]
    [InlineData("const a=1;", true)]
    [InlineData("  import{a}from\"./x.js\";a();", false)]
    [InlineData("import\"./x.js\";", false)]
    [InlineData("import * as x from './x.js';", false)]
    [InlineData("const u=import.meta.url;", false)]
    [InlineData("const a=1;export{a as b};", false)]
    [InlineData("const a=1;export default a;\n", false)]
    [InlineData("const p=()=>import(\"./later.js\");p();", true)]
    [InlineData("import(\"./later.js\");", true)]
    [InlineData("important();", true)]
    public void A_script_that_brings_in_or_hands_out_names_cannot_be_a_plain_script(string js, bool canBe)
    {
        Assert.Equal(canBe, UiSite.CanBeClassic(js));
        if (!canBe) Assert.Equal(js, UiSite.AsClassicScript(js));
    }

    [Fact]
    public void Each_file_says_whether_it_is_the_page_or_a_script_and_which_way_it_was_asked_for()
    {
        var site = Fake();
        var page = site.ForSchemeUrl("app://rigcontrol/index.html")!;
        Assert.Equal((UiFileKind.Page, false), (page.Kind, page.Classic));
        var classicPage = site.ForSchemeUrl("app://rigcontrol/index.html?classic=1")!;
        Assert.Equal((UiFileKind.Page, true), (classicPage.Kind, classicPage.Classic));
        var script = site.ForSchemeUrl("app://rigcontrol/assets/index-abc.js")!;
        Assert.Equal((UiFileKind.Script, false), (script.Kind, script.Classic));
        var classicScript = site.ForSchemeUrl("app://rigcontrol/assets/index-abc.js?classic=1")!;
        Assert.Equal((UiFileKind.Script, true), (classicScript.Kind, classicScript.Classic));
        Assert.Equal(UiFileKind.Other, site.ForSchemeUrl("app://rigcontrol/assets/index-def.css")!.Kind);
        Assert.Equal(UiFileKind.Other, site.ForSchemeUrl("app://rigcontrol/assets/font.woff2")!.Kind);
    }

    [Theory]
    [InlineData("https://rigcontrol/index.html")]
    [InlineData("app://elsewhere/index.html")]
    [InlineData("app://rigcontrol/nothing.html")]
    [InlineData("app://rigcontrol/assets/../../secret")]
    [InlineData("app://rigcontrol/assets/%2e%2e/%2e%2e/secret")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not an address")]
    [InlineData("")]
    public void Only_RigControls_own_files_are_answered(string url)
        => Assert.Null(Fake().ForSchemeUrl(url));

    [Fact]
    public void A_page_without_a_policy_gets_one()
    {
        var html = UiSite.Index("<html><head><title>x</title></head><body></body></html>", false);
        Assert.Contains("<head><meta http-equiv=\"Content-Security-Policy\"", html);
    }

    // ----- the interface that is really carried in RigControl.Hosting.dll -----

    private static IEnumerable<string> Addresses(string text)
        => Regex.Matches(text, "(?:src|href)=\"([^\"]+)\"|url\\(([^)\"']+)\\)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(a => !a.StartsWith("data:", StringComparison.Ordinal));

    [Fact]
    public void Every_file_the_built_page_asks_for_is_answered_on_the_windows_own_address()
    {
        if (!UiAssets.HasUi) return; // a build made with -p:SkipUiBuild=true and no built interface
        var site = new UiSite();
        foreach (var classic in new[] { false, true })
        {
            var html = Text(site.ForSchemeUrl(UiSite.SchemeAddress(classic).ToString()));
            var wanted = Addresses(html).ToList();
            Assert.Contains(wanted, a => a.Contains(".js"));
            foreach (var address in wanted)
            {
                var file = site.ForSchemeUrl("app://rigcontrol" + address);
                Assert.True(file is not null, address + " is not answered");
                if (file!.ContentType != "text/css") continue;
                foreach (var inner in Addresses(Encoding.UTF8.GetString(file.Body)))
                    Assert.True(site.ForSchemeUrl("app://rigcontrol" + inner) is not null, inner + " is not answered");
            }
        }
    }

    [Fact]
    public void The_built_page_has_one_script_and_it_can_run_as_a_plain_script()
    {
        if (!UiAssets.HasUi) return;
        var site = new UiSite();
        var html = Text(site.ForSchemeUrl("app://rigcontrol/index.html"));
        var scripts = Regex.Matches(html, "<script\\b[^>]*\\bsrc=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Single(scripts);
        Assert.DoesNotContain("<script>", html); // no inline script: the policy would refuse it
        var js = Text(site.ForSchemeUrl("app://rigcontrol" + scripts[0]));
        Assert.True(UiSite.CanBeClassic(js), "The built script uses import/export at its top, so the 'classic' way cannot work.");
        Assert.StartsWith("(function(){\"use strict\";", Text(site.ForSchemeUrl("app://rigcontrol" + scripts[0] + "?classic=1")));
        var classic = Text(site.ForSchemeUrl("app://rigcontrol/index.html?classic=1"));
        Assert.Contains(scripts[0] + "?classic=1", classic);
        Assert.DoesNotContain("type=\"module\"", classic);
    }

    [Fact]
    public void The_built_page_carries_no_fake_data()
    {
        if (!UiAssets.HasUi) return;
        // The made-up data for the developer preview must never be inside a real build.
        Assert.DoesNotContain(UiAssets.Paths, p => Regex.IsMatch(p, "(^|/)mock-", RegexOptions.IgnoreCase));
    }
}

public class MessagePumpTests
{
    [Fact]
    public async Task Nothing_is_sent_before_the_page_is_there_and_then_everything_in_order()
    {
        var got = new ConcurrentQueue<string>();
        using var pump = new MessagePump(got.Enqueue, new ListLog());
        pump.Post("early");
        pump.Open();
        for (var i = 0; i < 200; i++) pump.Post(i.ToString());
        Assert.True(await Wait.UntilAsync(() => got.Count == 200));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString()), got);
    }

    [Fact]
    public async Task Posting_never_waits_for_a_slow_window()
    {
        using var release = new ManualResetEventSlim();
        var got = new ConcurrentQueue<string>();
        using var pump = new MessagePump(m => { release.Wait(); got.Enqueue(m); }, new ListLog());
        pump.Open();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) pump.Post("m" + i);
        Assert.True(watch.ElapsedMilliseconds < 2000);
        Assert.Empty(got);
        release.Set();
        Assert.True(await Wait.UntilAsync(() => got.Count == 50));
    }

    [Fact]
    public async Task After_closing_nothing_more_is_sent()
    {
        var got = new ConcurrentQueue<string>();
        var pump = new MessagePump(got.Enqueue, new ListLog());
        pump.Open();
        pump.Post("one");
        Assert.True(await Wait.UntilAsync(() => got.Count == 1));
        pump.Close();
        pump.Post("two");
        pump.Close();
        await Task.Delay(100);
        Assert.Single(got);
    }

    [Fact]
    public async Task A_window_that_refuses_one_message_still_gets_the_next()
    {
        var log = new ListLog();
        var got = new ConcurrentQueue<string>();
        var sent = new ConcurrentQueue<string>();
        using var pump = new MessagePump(m =>
        {
            if (m == "bad") throw new InvalidOperationException("no");
            got.Enqueue(m);
        }, log, sent.Enqueue);
        pump.Open();
        pump.Post("bad");
        pump.Post("good");
        Assert.True(await Wait.UntilAsync(() => got.Count == 1));
        Assert.Equal(new[] { "good" }, sent);
        Assert.Contains(log.Lines, l => l.StartsWith("warn"));
    }

    [Fact]
    public async Task A_window_that_stopped_taking_messages_does_not_fill_the_memory()
    {
        using var release = new ManualResetEventSlim();
        var log = new ListLog();
        var count = 0;
        using var pump = new MessagePump(_ => { release.Wait(); Interlocked.Increment(ref count); }, log);
        pump.Open();
        for (var i = 0; i < MessagePump.MaxWaiting + 500; i++) pump.Post("x");
        Assert.Single(log.Lines, l => l.Contains("not taking messages"));
        release.Set();
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref count) >= MessagePump.MaxWaiting));
        await Task.Delay(100);
        Assert.InRange(Volatile.Read(ref count), MessagePump.MaxWaiting, MessagePump.MaxWaiting + 1);
    }
}

public class PageKeyTests
{
    [Fact]
    public void Only_a_message_that_starts_with_the_whole_key_is_opened()
    {
        var key = new PageKey();
        Assert.Equal(PageKey.Length, key.Value.Length);
        Assert.True(key.TryOpen(key.Value + "{\"id\":1}", out var json));
        Assert.Equal("{\"id\":1}", json);

        foreach (var bad in new[]
                 {
                     null, "", "{\"id\":1}", key.Value, key.Value[1..] + "{\"id\":1}", key.Value.ToUpperInvariant() + "{}",
                     new string('0', PageKey.Length) + "{}", " " + key.Value + "{}", new string('\u00e9', PageKey.Length) + "{}",
                 })
        {
            Assert.False(key.TryOpen(bad, out var rest), bad);
            Assert.Equal("", rest);
        }
    }

    [Fact]
    public void Two_keys_are_never_the_same()
        => Assert.NotEqual(new PageKey().Value, new PageKey().Value);
}

public class SmokeProbeTests
{
    [Fact]
    public void It_passes_only_when_the_page_says_the_getInfo_answer_arrived()
    {
        var probe = new SmokeProbe();
        Assert.Equal("The window was never created.", probe.WhereItStopped(windowCreated: false));
        Assert.Contains("never sent a message", probe.WhereItStopped(true));

        probe.FromPage("{\"id\":2,\"method\":\"app.getModules\",\"params\":null}");
        probe.ToPage("{\"id\":2,\"ok\":true,\"result\":[]}");
        Assert.False(probe.Passed.IsCompleted);
        Assert.Contains("never asked for app.getInfo", probe.WhereItStopped(true));

        probe.FromPage("{\"id\":1,\"method\":\"app.getInfo\",\"params\":null}");
        Assert.Contains("never handed to the window", probe.WhereItStopped(true));
        probe.ToPage("{\"event\":\"app.modulesChanged\",\"data\":[]}");
        Assert.False(probe.Passed.IsCompleted);

        // Handing the answer to the window proves nothing about the page having received it.
        probe.ToPage("{\"id\":1,\"ok\":true,\"result\":{\"version\":\"1.0.0\"}}");
        Assert.False(probe.Passed.IsCompleted);
        Assert.Contains("do not reach the page", probe.WhereItStopped(true));

        // A note about some other answer does not count; the note about this one does.
        Assert.True(probe.FromPage("{\"note\":\"answered\",\"id\":2}"));
        Assert.False(probe.Passed.IsCompleted);
        Assert.True(probe.FromPage("{\"note\":\"answered\",\"id\":1}"));
        Assert.True(probe.Passed.IsCompletedSuccessfully);
        Assert.Equal("The page asked for app.getInfo and said the answer arrived.", probe.WhereItStopped(true));
    }

    [Fact]
    public void The_note_may_arrive_before_the_answer_is_reported_as_handed_over()
    {
        // The two reports come from different threads, so either order must do.
        var probe = new SmokeProbe();
        Assert.False(probe.FromPage("{\"id\":4,\"method\":\"app.getInfo\",\"params\":null}"));
        Assert.True(probe.FromPage("{\"note\":\"answered\",\"id\":4}"));
        Assert.False(probe.Passed.IsCompleted);
        probe.ToPage("{\"id\":4,\"ok\":true,\"result\":{}}");
        Assert.True(probe.Passed.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_note_alone_proves_nothing_and_every_note_is_kept_from_the_modules()
    {
        var probe = new SmokeProbe();
        Assert.True(probe.FromPage("{\"note\":\"answered\",\"id\":1}"));
        Assert.True(probe.FromPage("{\"note\":\"something else\"}"));
        Assert.True(probe.FromPage("{\"note\":5,\"method\":\"app.quit\",\"id\":3}"));
        probe.ToPage("{\"id\":1,\"ok\":true}");
        Assert.False(probe.Passed.IsCompleted);
        // Calls are not notes.
        Assert.False(probe.FromPage("{\"id\":2,\"method\":\"app.getModules\",\"params\":null}"));
        Assert.False(probe.FromPage("not json"));
    }

    [Fact]
    public void An_error_answer_does_not_pass_and_is_named()
    {
        var probe = new SmokeProbe();
        probe.FromPage("{\"id\":7,\"method\":\"app.getInfo\"}");
        probe.ToPage("{\"id\":7,\"ok\":false,\"error\":{\"code\":\"internal\",\"message\":\"boom\"}}");
        Assert.False(probe.Passed.IsCompleted);
        Assert.Contains("boom", probe.WhereItStopped(true));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"id\":\"1\",\"method\":\"app.getInfo\"}")]
    [InlineData("{\"id\":1,\"method\":5}")]
    [InlineData("")]
    public void Odd_messages_are_ignored(string message)
    {
        var probe = new SmokeProbe();
        probe.FromPage(message);
        probe.ToPage(message);
        probe.ToPage("{\"id\":1,\"ok\":true}");
        Assert.False(probe.Passed.IsCompleted);
    }
}

public class PageLoaderTests
{
    [Fact]
    public void The_order_is_what_was_asked_or_the_remembered_way_first()
    {
        Assert.Equal(new[] { UiMode.Scheme, UiMode.Classic }, PageLoader.Order(null, null));
        Assert.Equal(new[] { UiMode.Classic }, PageLoader.Order(UiMode.Classic, UiMode.Scheme));
        Assert.Equal(new[] { UiMode.Classic, UiMode.Scheme }, PageLoader.Order(null, UiMode.Classic));
        Assert.Equal(new[] { UiMode.Scheme, UiMode.Classic }, PageLoader.Order(null, UiMode.Scheme));
    }

    [Fact]
    public async Task A_page_whose_files_the_window_took_is_slow_not_refused_and_gets_more_time()
    {
        var loads = new ConcurrentQueue<UiMode>();
        var worked = new ConcurrentQueue<UiMode>();
        var log = new ListLog();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(80), loads.Enqueue, log, worked.Enqueue,
            slowLimit: TimeSpan.FromSeconds(10));
        loader.Begin();
        loader.Served(UiMode.Scheme, UiFileKind.Page);
        loader.Served(UiMode.Scheme, UiFileKind.Script);
        // Several first waits go by: the usual way is kept, never swapped for the next one.
        await Task.Delay(500);
        Assert.Empty(loads);
        Assert.Single(log.Lines, l => l.Contains("loading slowly (scheme)"));
        loader.PageSpoke();
        Assert.Equal(new[] { UiMode.Scheme }, worked);
    }

    [Fact]
    public async Task A_slow_page_is_given_up_on_at_the_slow_limit()
    {
        var loads = new ConcurrentQueue<UiMode>();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(50), loads.Enqueue, new ListLog(),
            slowLimit: TimeSpan.FromMilliseconds(300));
        loader.Begin();
        loader.Served(UiMode.Scheme, UiFileKind.Page);
        loader.Served(UiMode.Scheme, UiFileKind.Script);
        await Task.Delay(120);
        Assert.Empty(loads);
        Assert.True(await Wait.UntilAsync(() => loads.Count == 1, 3000));
        Assert.Equal(new[] { UiMode.Classic }, loads);
    }

    [Fact]
    public async Task Only_the_page_alone_or_files_for_another_way_do_not_count_as_loading()
    {
        var loads = new ConcurrentQueue<UiMode>();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(80), loads.Enqueue, new ListLog(),
            slowLimit: TimeSpan.FromSeconds(10));
        loader.Begin();
        // The page but not its script (the web view would not run it), and a script for the other way.
        loader.Served(UiMode.Scheme, UiFileKind.Page);
        loader.Served(UiMode.Classic, UiFileKind.Script);
        loader.Served(UiMode.Scheme, UiFileKind.Other);
        Assert.True(await Wait.UntilAsync(() => loads.Count == 1, 2000));
        // The next way starts with a clean slate: what was taken for the first way does not carry over.
        loader.Served(UiMode.Scheme, UiFileKind.Script);
        await Task.Delay(300);
        Assert.Equal(new[] { UiMode.Classic }, loads);
        Assert.False(loader.Connected);
    }

    [Fact]
    public async Task A_page_that_connects_in_time_is_left_alone()
    {
        var loads = new ConcurrentQueue<UiMode>();
        var worked = new ConcurrentQueue<UiMode>();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(150), loads.Enqueue, new ListLog(), worked.Enqueue);
        Assert.Null(loader.Current);
        loader.Begin();
        Assert.Equal(UiMode.Scheme, loader.Current);
        loader.PageSpoke();
        loader.PageSpoke();
        await Task.Delay(400);
        Assert.Empty(loads);
        Assert.Equal(new[] { UiMode.Scheme }, worked);
        Assert.True(loader.Connected);
    }

    [Fact]
    public async Task A_page_that_stays_silent_is_tried_the_next_way_and_the_way_that_worked_is_reported()
    {
        var loads = new ConcurrentQueue<UiMode>();
        var worked = new ConcurrentQueue<UiMode>();
        var log = new ListLog();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(100), loads.Enqueue, log, worked.Enqueue);
        loader.Begin();
        Assert.True(await Wait.UntilAsync(() => loads.Count == 1));
        Assert.Equal(new[] { UiMode.Classic }, loads);
        loader.PageSpoke();
        await Task.Delay(300);
        Assert.Equal(new[] { UiMode.Classic }, loads);
        Assert.Equal(new[] { UiMode.Classic }, worked);
        Assert.Contains(log.Lines, l => l.Contains("Trying classic"));
    }

    [Fact]
    public async Task When_no_way_works_it_says_so_once_and_stops()
    {
        var loads = new ConcurrentQueue<UiMode>();
        var log = new ListLog();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(60), loads.Enqueue, log);
        loader.Begin();
        Assert.True(await Wait.UntilAsync(() => log.Lines.Any(l => l.StartsWith("error"))));
        await Task.Delay(200);
        Assert.Equal(new[] { UiMode.Classic }, loads);
        Assert.Single(log.Lines, l => l.Contains("did not connect (classic was the last way tried)"));
        Assert.False(loader.Connected);
    }

    [Fact]
    public async Task When_no_way_works_the_host_is_told_once_and_a_late_message_no_longer_counts()
    {
        var gaveUp = 0;
        var worked = new ConcurrentQueue<UiMode>();
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(40), _ => { }, new ListLog(),
            worked.Enqueue, () => Interlocked.Increment(ref gaveUp));
        loader.Begin();
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref gaveUp) == 1));
        loader.PageSpoke();
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref gaveUp));
        Assert.Empty(worked);
        Assert.False(loader.Connected);
    }

    [Fact]
    public async Task A_page_that_connects_is_never_given_up_on()
    {
        var gaveUp = 0;
        using var loader = new PageLoader(PageLoader.DefaultOrder, TimeSpan.FromMilliseconds(40), _ => { }, new ListLog(),
            gaveUp: () => Interlocked.Increment(ref gaveUp));
        loader.Begin();
        loader.PageSpoke();
        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref gaveUp));
    }

    [Fact]
    public async Task One_way_asked_for_is_never_swapped_for_another()
    {
        var loads = new ConcurrentQueue<UiMode>();
        var log = new ListLog();
        using var loader = new PageLoader(PageLoader.Order(UiMode.Scheme, null), TimeSpan.FromMilliseconds(60), loads.Enqueue, log);
        loader.Begin();
        Assert.True(await Wait.UntilAsync(() => log.Lines.Any(l => l.StartsWith("error"))));
        Assert.Empty(loads);
    }
}
