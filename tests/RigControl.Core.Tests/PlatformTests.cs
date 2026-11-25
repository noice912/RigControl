using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Core.Tests;

/// <summary>A throwaway folder under the system's temp folder, removed after the test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigcontrol-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

public class FileKeySecretProtectorTests
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("lights/hue/192.0.2.10");

    [Fact]
    public void What_is_protected_reads_back_and_is_not_stored_in_plain_text()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        var secret = Encoding.UTF8.GetBytes("a-pairing-key-that-must-not-leak");

        var blob = p.Protect(secret, Purpose);

        Assert.Equal(secret, p.Unprotect(blob, Purpose));
        Assert.DoesNotContain("pairing-key", Encoding.Latin1.GetString(blob));
        Assert.True(File.Exists(p.KeyPath));
        Assert.Equal(64, new FileInfo(p.KeyPath).Length);
    }

    [Fact]
    public void A_second_instance_with_the_same_folder_reads_it_back()
    {
        using var dir = new TempFolder();
        var blob = new FileKeySecretProtector(dir.Path).Protect(new byte[] { 1, 2, 3 }, Purpose);
        Assert.Equal(new byte[] { 1, 2, 3 }, new FileKeySecretProtector(dir.Path).Unprotect(blob, Purpose));
    }

    [Fact]
    public void An_empty_secret_works()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        Assert.Empty(p.Unprotect(p.Protect(Array.Empty<byte>(), Purpose), Purpose));
    }

    [Fact]
    public void The_same_secret_never_gives_the_same_bytes_twice()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        Assert.NotEqual(p.Protect(new byte[] { 9 }, Purpose), p.Protect(new byte[] { 9 }, Purpose));
    }

    [Fact]
    public void Another_purpose_cannot_read_it()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        var blob = p.Protect(new byte[] { 1, 2, 3 }, Purpose);
        Assert.Throws<CryptographicException>(() => p.Unprotect(blob, Encoding.UTF8.GetBytes("lights/hue/192.0.2.11")));
    }

    [Fact]
    public void A_changed_byte_is_noticed()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        var blob = p.Protect(Encoding.UTF8.GetBytes("secret"), Purpose);
        for (var i = 0; i < blob.Length; i++)
        {
            var copy = (byte[])blob.Clone();
            copy[i] ^= 0x01;
            Assert.Throws<CryptographicException>(() => p.Unprotect(copy, Purpose));
        }
    }

    [Fact]
    public void Another_folders_key_cannot_read_it()
    {
        using var a = new TempFolder();
        using var b = new TempFolder();
        var blob = new FileKeySecretProtector(a.Path).Protect(new byte[] { 1 }, Purpose);
        var other = new FileKeySecretProtector(b.Path);
        other.Protect(new byte[] { 1 }, Purpose); // makes b's own key
        Assert.Throws<CryptographicException>(() => other.Unprotect(blob, Purpose));
    }

    [Fact]
    public void Without_the_key_file_reading_fails_and_no_new_key_is_made()
    {
        using var a = new TempFolder();
        using var b = new TempFolder();
        var blob = new FileKeySecretProtector(a.Path).Protect(new byte[] { 1 }, Purpose);
        var other = new FileKeySecretProtector(b.Path);
        Assert.Throws<CryptographicException>(() => other.Unprotect(blob, Purpose));
        Assert.False(File.Exists(other.KeyPath));
    }

    [Fact]
    public void Garbage_is_refused()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        Assert.Throws<CryptographicException>(() => p.Unprotect(new byte[] { 1, 2, 3 }, Purpose));
        Assert.Throws<CryptographicException>(() => p.Unprotect(new byte[200], Purpose));
    }

    [Fact]
    public void Reading_with_a_damaged_key_file_is_refused_and_the_file_is_left_alone()
    {
        using var dir = new TempFolder();
        var blob = new FileKeySecretProtector(dir.Path).Protect(new byte[] { 1 }, Purpose);
        var p = new FileKeySecretProtector(dir.Path);
        File.WriteAllBytes(p.KeyPath, new byte[10]);
        Assert.Throws<CryptographicException>(() => p.Unprotect(blob, Purpose));
        Assert.Equal(10, new FileInfo(p.KeyPath).Length);
        Assert.False(File.Exists(Path.Combine(dir.Path, FileKeySecretProtector.DamagedKeyFileName)));
    }

    [Fact]
    public void A_key_file_cut_short_while_it_was_made_does_not_lock_pairing_out()
    {
        // What a crash or a full disk left behind before: the file exists but holds nothing.
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        File.WriteAllBytes(p.KeyPath, Array.Empty<byte>());
        var blob = p.Protect(new byte[] { 4, 5 }, Purpose);
        Assert.Equal(64, new FileInfo(p.KeyPath).Length);
        Assert.Equal(new byte[] { 4, 5 }, new FileKeySecretProtector(dir.Path).Unprotect(blob, Purpose));
        Assert.False(File.Exists(Path.Combine(dir.Path, FileKeySecretProtector.DamagedKeyFileName)));
    }

    [Fact]
    public void A_damaged_key_file_is_set_aside_when_something_new_is_protected()
    {
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        File.WriteAllBytes(p.KeyPath, new byte[10]);
        var blob = p.Protect(new byte[] { 1 }, Purpose);
        Assert.Equal(64, new FileInfo(p.KeyPath).Length);
        Assert.Equal(10, new FileInfo(Path.Combine(dir.Path, FileKeySecretProtector.DamagedKeyFileName)).Length);
        Assert.Equal(new byte[] { 1 }, p.Unprotect(blob, Purpose));
    }

    [Fact]
    public void Making_the_key_leaves_only_the_key_file_and_clears_half_written_ones()
    {
        using var dir = new TempFolder();
        var leftover = Path.Combine(dir.Path, FileKeySecretProtector.KeyFileName + ".0123456789abcdef.tmp");
        File.WriteAllBytes(leftover, new byte[7]);
        new FileKeySecretProtector(dir.Path).Protect(new byte[] { 1 }, Purpose);
        Assert.Equal(new[] { FileKeySecretProtector.KeyFileName }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void On_Linux_and_macOS_the_key_file_is_for_its_owner_only()
    {
        if (OperatingSystem.IsWindows()) return; // file modes are a Linux/macOS thing; this runs on the CI machines
        using var dir = new TempFolder();
        var p = new FileKeySecretProtector(dir.Path);
        p.Protect(new byte[] { 1 }, Purpose);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(p.KeyPath));

        // A key file that was opened up (copied, restored from a backup) is closed again the next time it is used.
        File.SetUnixFileMode(p.KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        new FileKeySecretProtector(dir.Path).Protect(new byte[] { 1 }, Purpose);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(p.KeyPath));
    }

    [Fact]
    public void On_Windows_the_Windows_protection_reads_back_what_it_protected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiSecretProtector();
        var blob = p.Protect(new byte[] { 4, 5, 6 }, Purpose);
        Assert.Equal(new byte[] { 4, 5, 6 }, p.Unprotect(blob, Purpose));
        CryptographicException? refused = null;
        try { p.Unprotect(blob, new byte[] { 1 }); }
        catch (CryptographicException ex) { refused = ex; }
        Assert.NotNull(refused);
    }
}

public class PlatformServicesTests
{
    [Fact]
    public void The_standard_set_matches_the_system_it_runs_on()
    {
        using var dir = new TempFolder();
        var s = PlatformServices.Create(dir.Path);

        Assert.Equal(PlatformInfo.Current, s.Platform);
        Assert.Equal(AppEnvironment.DetectElevated(), s.IsElevated);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<DpapiSecretProtector>(s.Secrets);
            Assert.False(s.Autostart.Supported); // the Windows app has its own startup task
        }
        else
        {
            Assert.IsType<FileKeySecretProtector>(s.Secrets);
        }
        // Nothing is written just by asking for the set.
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void A_module_context_carries_the_standard_set_unless_told_otherwise()
    {
        using var dir = new TempFolder();
        var env = new AppEnvironment(dir.Path, dir.Path, false, "1.0.0", "");
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"), NullLog.Instance);
        var context = new ModuleContext(new EventBus(), NullLog.Instance, settings, env);

        Assert.Equal(PlatformInfo.Current, context.Platform.Platform);
        Assert.False(context.Platform.IsElevated);

        var swapped = context with { Platform = PlatformServices.Create(dir.Path, isElevated: true) };
        Assert.True(swapped.Platform.IsElevated);
    }

    [Theory]
    [InlineData(HostPlatform.Windows, "windows", "Windows")]
    [InlineData(HostPlatform.Linux, "linux", "Linux")]
    [InlineData(HostPlatform.MacOS, "macos", "macOS")]
    [InlineData(HostPlatform.Other, "other", "this system")]
    public void Platform_names(HostPlatform platform, string id, string display)
    {
        Assert.Equal(id, PlatformInfo.Id(platform));
        Assert.Equal(display, PlatformInfo.DisplayName(platform));
    }

    [Fact]
    public void Only_https_links_are_opened()
    {
        var links = new SystemLinkOpener();
        // These are refused before anything is started.
        Assert.False(links.OpenHttpsUrl(null));
        Assert.False(links.OpenHttpsUrl(""));
        Assert.False(links.OpenHttpsUrl("http://example.com"));
        Assert.False(links.OpenHttpsUrl("file:///etc/passwd"));
        Assert.False(links.OpenHttpsUrl("javascript:alert(1)"));
        Assert.False(links.OpenHttpsUrl("example.com"));
    }

    [Fact]
    public void Attaching_a_helper_program_never_throws()
    {
        // On Linux and macOS there are no job objects: the answer is simply "not attached".
        if (OperatingSystem.IsWindows()) return;
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(RigControl.Core.Util.ChildProcesses.Attach(me));
    }
}

public class OwnProcessQueryTests
{
    [Fact]
    public void This_test_run_is_among_the_users_own_programs()
    {
        var query = new OwnProcessQuery();
        var mine = query.OwnProcesses();
        Assert.Contains(mine, p => p.Id == Environment.ProcessId);
        Assert.False(query.IsRunning(""));
        Assert.False(query.IsRunning("rigcontrol-no-such-program-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Ps_output_is_read_with_paths_that_hold_spaces()
    {
        var list = OwnProcessQuery.ParsePs(
            "  501 /Applications/Discord.app/Contents/MacOS/Discord\n" +
            "  777 /Applications/Some Game.app/Contents/MacOS/Some Game\n" +
            "12 login\n" +
            "\n" +
            "garbage line\n");

        Assert.Equal(new[] { new ProcessEntry(501, "Discord"), new ProcessEntry(777, "Some Game"), new ProcessEntry(12, "login") }, list);
    }

    [Fact]
    public void A_proc_folder_gives_only_this_users_programs()
    {
        using var dir = new TempFolder();
        void Proc(string id, string uid, string comm)
        {
            var d = Directory.CreateDirectory(Path.Combine(dir.Path, id)).FullName;
            File.WriteAllText(Path.Combine(d, "status"), "Name:\t" + comm + "\nUid:\t" + uid + "\t" + uid + "\t" + uid + "\t" + uid + "\nGid:\t5\n");
            File.WriteAllText(Path.Combine(d, "comm"), comm + "\n");
        }
        Proc("self", "1000", "RigControl");
        Proc("41", "1000", "Discord");
        Proc("42", "0", "systemd");
        Proc("43", "1001", "Discord"); // another person's Discord
        Directory.CreateDirectory(Path.Combine(dir.Path, "sys")); // not a process
        Directory.CreateDirectory(Path.Combine(dir.Path, "44")); // ended while we looked: no files

        var list = OwnProcessQuery.Linux(dir.Path);

        Assert.Equal(new[] { new ProcessEntry(41, "Discord") }, list);
    }

    [Fact]
    public void Without_knowing_who_we_are_nothing_is_listed()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "41"));
        Assert.Empty(OwnProcessQuery.Linux(dir.Path));
        Assert.Null(OwnProcessQuery.UidFromStatus(Path.Combine(dir.Path, "missing")));
    }
}

public class UnixAutostartTests
{
    [Fact]
    public void Linux_turning_it_on_writes_one_desktop_file_and_off_removes_it()
    {
        using var dir = new TempFolder();
        var folder = Path.Combine(dir.Path, "autostart");
        var a = new UnixAutostart(UnixAutostart.Kind.LinuxDesktopFile, folder);
        var exe = OperatingSystem.IsWindows() ? @"C:\Apps\RigControl" : "/opt/RigControl/RigControl";

        Assert.True(a.Supported);
        Assert.False(a.IsEnabled());
        Assert.Null(a.SetEnabled(true, exe));
        Assert.True(a.IsEnabled());
        Assert.Equal(new[] { Path.Combine(folder, "rigcontrol.desktop") }, Directory.GetFiles(folder));
        var text = File.ReadAllText(a.FilePath);
        Assert.StartsWith("[Desktop Entry]\n", text);
        Assert.Contains("\nType=Application\n", text);
        Assert.Contains("\nExec=\"", text);

        Assert.Null(a.SetEnabled(false, exe));
        Assert.False(a.IsEnabled());
        Assert.Null(a.SetEnabled(false, exe)); // already off: nothing to do, no error
    }

    [Fact]
    public void The_program_path_is_quoted_the_way_desktop_files_ask_for()
    {
        Assert.Contains("\nExec=\"/opt/Rig Control/RigControl\"\n", UnixAutostart.DesktopFile("/opt/Rig Control/RigControl"));
        // $ ` " get a backslash (written twice in the file), a backslash becomes four, a percent sign two.
        Assert.Contains("\nExec=\"/a\\\\$b\\\\`c\\\\\"d\\\\\\\\e%%f\"\n", UnixAutostart.DesktopFile("/a$b`c\"d\\e%f"));
    }

    [Fact]
    public void MacOS_writes_a_launch_agent_with_the_path_escaped()
    {
        using var dir = new TempFolder();
        var a = new UnixAutostart(UnixAutostart.Kind.MacLaunchAgent, dir.Path);
        var exe = OperatingSystem.IsWindows() ? @"C:\Apps\A&B <x>\RigControl" : "/Applications/A&B <x>/RigControl";

        Assert.Null(a.SetEnabled(true, exe));

        Assert.EndsWith("io.github.rigcontrol.plist", a.FilePath);
        var text = File.ReadAllText(a.FilePath);
        Assert.Contains("A&amp;B &lt;x&gt;", text);
        Assert.Contains("<key>RunAtLoad</key>\n  <true/>", text);
        // It is well-formed XML.
        var doc = new System.Xml.XmlDocument { XmlResolver = null };
        doc.LoadXml(text.Substring(text.IndexOf("<plist", StringComparison.Ordinal)));
    }

    [Fact]
    public void Paths_that_cannot_be_written_safely_are_refused()
    {
        using var dir = new TempFolder();
        var a = new UnixAutostart(UnixAutostart.Kind.LinuxDesktopFile, dir.Path);
        Assert.NotNull(a.SetEnabled(true, ""));
        Assert.NotNull(a.SetEnabled(true, "RigControl")); // not a full path
        var root = OperatingSystem.IsWindows() ? @"C:\x" : "/x";
        Assert.NotNull(a.SetEnabled(true, root + "\nExec=evil"));
        Assert.False(a.IsEnabled());
    }

    [Fact]
    public void Where_there_is_no_way_to_do_it_it_says_so()
    {
        var a = new UnixAutostart(UnixAutostart.Kind.None, "");
        Assert.False(a.Supported);
        Assert.False(a.IsEnabled());
        Assert.NotNull(a.SetEnabled(true, "/x"));
    }
}

public class ModuleAvailabilityTests
{
    [Fact]
    public void The_message_always_starts_the_same_way()
    {
        Assert.Equal("Not available on this system", ModuleAvailability.Message());
        Assert.Equal("Not available on this system: it works on Windows only.", ModuleAvailability.Message(" it works on Windows only. "));
        Assert.True(ModuleAvailability.IsNotOnThisSystem(ModuleAvailability.Status("x")));
        Assert.True(ModuleAvailability.IsNotOnThisSystem(new ModuleStatus(ModuleState.Degraded, "Not available on this system")));
        Assert.False(ModuleAvailability.IsNotOnThisSystem(new ModuleStatus(ModuleState.Failed, "Not available on this PC: no NVIDIA card.")));
        Assert.False(ModuleAvailability.IsNotOnThisSystem(new ModuleStatus(ModuleState.Ready)));
        Assert.Equal(ModuleState.Failed, ModuleAvailability.Status().State);
        Assert.Equal(RpcErrorCodes.Unavailable, ModuleAvailability.Error().Code);
        Assert.Equal("Not available on this system.", ModuleAvailability.Error().Message);
    }

    [Fact]
    public async Task A_stand_in_module_starts_reports_and_answers_every_call_the_same_way()
    {
        var log = NullLog.Instance;
        var host = new ModuleHost(log);
        var dispatcher = new RpcDispatcher(log);
        var module = new UnavailableModule("capture", "Recording and instant replay", "it works on Windows only.");
        host.Add(module);
        dispatcher.Register(module);
        using var dir = new TempFolder();
        var env = new AppEnvironment(dir.Path, dir.Path, false, "1.0.0", "");
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"), log);

        await host.StartAllAsync(m => new ModuleContext(new EventBus(), log, settings, env), TimeSpan.FromSeconds(5), CancellationToken.None);

        var info = Assert.Single(host.Describe());
        Assert.Equal(new ModuleInfo("capture", "Recording and instant replay", ModuleState.Failed,
            "Not available on this system: it works on Windows only."), info);

        var answer = JsonDocument.Parse(await dispatcher.HandleAsync("{\"id\":1,\"method\":\"capture.getStatus\"}")).RootElement;
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("unavailable", answer.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Not available on this system: it works on Windows only.", answer.GetProperty("error").GetProperty("message").GetString());

        await host.DisposeAsync();
    }
}
