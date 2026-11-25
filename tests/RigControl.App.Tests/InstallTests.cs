using System.IO;
using Microsoft.Win32;
using RigControl.App.Install;

namespace RigControl.App.Tests;

/// <summary>
/// The installer's decisions, without installing anything: command lines, paths, the Installed apps entry (written to
/// a throwaway HKCU key), which start-up tasks move, and the uninstaller's last clean-up line.
/// </summary>
public class InstallTests
{
    private const string Alice = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Bob = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Root = @"C:\Program Files\RigControl";
    private const string InstalledExe = @"C:\Program Files\RigControl\app\RigControl.exe";
    private const string OldExe = @"D:\Tools\RigControl\RigControl.exe";

    private static Autostart.TaskAnswer Task(string? command, string? user, bool enabled = true) =>
        new(true, true, new Autostart.TaskInfo(enabled, command, user));

    // ---- command line ----

    [Fact]
    public void The_updaters_command_line_is_read_as_an_update_with_its_pid()
    {
        var c = InstallCommand.Parse(new[] { "--update", "--after-pid", "4321", "--minimized" });
        Assert.Equal(InstallMode.Update, c.Mode);
        Assert.Equal(4321, c.AfterPid);
        Assert.True(c.Minimized);
        Assert.Equal(StartAction.Update, c.Decide(4, runsInstalledExe: false, developmentBuild: false));
        Assert.Equal("--update --after-pid 4321 --minimized", InstallCommand.UpdateArgs(4321, minimized: true));
        Assert.Equal("--update --after-pid 7", InstallCommand.UpdateArgs(7, minimized: false));
    }

    [Fact]
    public void Each_switch_picks_its_own_mode()
    {
        Assert.Equal(InstallMode.Install, InstallCommand.Parse(new[] { "--install" }).Mode);
        Assert.Equal(InstallMode.Uninstall, InstallCommand.Parse(new[] { "--UNINSTALL", "--quiet" }).Mode);
        Assert.True(InstallCommand.Parse(new[] { "--uninstall", "--quiet" }).Quiet);
        Assert.Equal(InstallMode.Portable, InstallCommand.Parse(new[] { "--portable" }).Mode);
        Assert.Equal(InstallMode.Setup, InstallCommand.Parse(new[] { "--setup" }).Mode);
        Assert.Equal(InstallMode.None, InstallCommand.Parse(Array.Empty<string>()).Mode);
        // The start-up task's helper switches are not the installer's.
        Assert.Equal(InstallMode.None, InstallCommand.Parse(new[] { "--install-autostart", Alice }).Mode);
        // An update is never mistaken for an uninstall.
        Assert.Equal(InstallMode.Update, InstallCommand.Parse(new[] { "--uninstall", "--update" }).Mode);
    }

    [Fact]
    public void A_bad_pid_or_user_is_ignored()
    {
        Assert.Null(InstallCommand.Parse(new[] { "--update", "--after-pid", "-5" }).AfterPid);
        Assert.Null(InstallCommand.Parse(new[] { "--update", "--after-pid" }).AfterPid);
        Assert.Null(InstallCommand.Parse(new[] { "--install", "--user", "not-a-sid" }).UserSid);
        Assert.Equal(Alice, InstallCommand.Parse(new[] { "--install", "--user", Alice }).UserSid);
    }

    [Fact]
    public void The_elevated_copy_gets_the_user_and_the_reinstall_choice()
    {
        var args = InstallCommand.ElevatedInstallArgs(Alice, reinstall: true).Split(' ');
        var c = InstallCommand.Parse(args);
        Assert.Equal(InstallMode.Install, c.Mode);
        Assert.Equal(Alice, c.UserSid);
        Assert.True(c.Reinstall);
        var u = InstallCommand.Parse(InstallCommand.ElevatedUninstallArgs(Bob, quiet: true).Split(' '));
        Assert.Equal((InstallMode.Uninstall, Bob, true), (u.Mode, u.UserSid, u.Quiet));
    }

    [Fact]
    public void Without_switches_a_copy_outside_the_install_folder_is_the_setup()
    {
        var none = InstallCommand.Parse(Array.Empty<string>());
        Assert.Equal(StartAction.Setup, none.Decide(0, runsInstalledExe: false, developmentBuild: false));
        Assert.Equal(StartAction.RunApp, none.Decide(0, runsInstalledExe: true, developmentBuild: false));
        // Development builds run the app unless --setup asks for the window.
        Assert.Equal(StartAction.RunApp, none.Decide(0, runsInstalledExe: false, developmentBuild: true));
        Assert.Equal(StartAction.Setup, InstallCommand.Parse(new[] { "--setup" }).Decide(1, false, true));
    }

    [Fact]
    public void A_copy_that_already_starts_with_Windows_never_turns_into_a_setup_window()
    {
        // The start-up task passes --minimized; restart-as-administrator passes --after-pid.
        Assert.Equal(StartAction.RunApp, InstallCommand.Parse(new[] { "--minimized" }).Decide(1, false, false));
        Assert.Equal(StartAction.RunApp, InstallCommand.Parse(new[] { "--after-pid", "12" }).Decide(2, false, false));
        Assert.Equal(StartAction.RunApp, InstallCommand.Parse(new[] { "--portable" }).Decide(1, false, false));
        Assert.Equal(StartAction.Uninstall, InstallCommand.Parse(new[] { "--uninstall" }).Decide(1, true, false));
    }

    // ---- versions ----

    [Theory]
    [InlineData("1.1.2", "1.1.2", true)]
    [InlineData("1.2.0", "1.1.2", true)]
    [InlineData("1.1.1", "1.1.2", false)]
    [InlineData("v1.1", "1.1.0", true)]
    [InlineData("1.1.2+abc123", "1.1.2", true)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData(null, "1.1.2", false)]
    [InlineData("rubbish", "1.1.2", false)]
    public void Same_or_newer_installed_versions_are_recognised(string? installed, string mine, bool expected)
    {
        Assert.Equal(expected, InstallVersion.InstalledIsSameOrNewer(installed, mine));
    }

    // ---- paths ----

    [Fact]
    public void The_layout_puts_each_part_in_its_own_folder()
    {
        var l = new InstallLayout(Root + @"\");
        Assert.Equal(Root, l.Root);
        Assert.Equal(InstalledExe, l.AppExe);
        Assert.Equal(Root + @"\ffmpeg", l.FfmpegDir);
        Assert.Equal(Root + @"\licenses", l.LicensesDir);
        Assert.Equal(Root + @"\updates", l.UpdatesDir);
        Assert.Equal(4, l.OwnFolders.Count);
        Assert.DoesNotContain(l.OwnFolders, f => f.EndsWith("OpenRGB", StringComparison.OrdinalIgnoreCase));
        Assert.True(l.IsAppExe(@"c:\program files\rigcontrol\APP\rigcontrol.exe"));
        Assert.False(l.IsAppExe(OldExe));
        Assert.True(l.Contains("\"" + InstalledExe + "\""));
        Assert.False(l.Contains(@"C:\Program Files\RigControlOther\app\RigControl.exe"));
        Assert.Equal(Root, InstallLayout.DefaultRoot(@"C:\Program Files"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\RigControl", true)]
    [InlineData(@"D:\Apps\RigControl", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:", false)]
    [InlineData(@"C:\Windows\RigControl", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"\\server\share\RigControl", false)]
    [InlineData(@"relative\RigControl", false)]
    [InlineData(@"C:\Apps\100%\RigControl", false)]
    [InlineData(@"C:\Apps\""x""", false)]
    [InlineData("", false)]
    public void Only_an_ordinary_folder_of_its_own_can_be_the_install_folder(string path, bool ok)
    {
        var problem = InstallLayout.RootProblem(path, @"C:\Windows", new[] { @"C:\Program Files", @"C:\Users" });
        Assert.Equal(ok, problem is null);
    }

    // ---- Installed apps entry ----

    [Fact]
    public void The_installed_apps_entry_has_the_values_the_design_lists()
    {
        var values = UninstallEntry.Values(new InstallLayout(Root), "1.1.2", 12345).ToDictionary(v => v.Name, v => v.Value);
        Assert.Equal("RigControl", values["DisplayName"]);
        Assert.Equal("1.1.2", values["DisplayVersion"]);
        Assert.Equal("RigControl", values["Publisher"]);
        Assert.Equal(Root, values["InstallLocation"]);
        Assert.Equal(InstalledExe, values["DisplayIcon"]);
        Assert.Equal("\"" + InstalledExe + "\" --uninstall", values["UninstallString"]);
        Assert.Equal("\"" + InstalledExe + "\" --uninstall --quiet", values["QuietUninstallString"]);
        Assert.Equal(12345, values["EstimatedSize"]);
        Assert.Equal(1, values["NoModify"]);
        Assert.Equal(1, values["NoRepair"]);
        Assert.Equal("https://github.com/noice912/RigControl", values["URLInfoAbout"]);
    }

    [Fact]
    public void The_entry_written_is_read_back_as_the_installed_copy()
    {
        var key = @"Software\RigControl-Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(UninstallEntry.Read(Registry.CurrentUser, key));
            UninstallEntry.Write(Registry.CurrentUser, key, new InstallLayout(@"D:\Apps\RigControl"), "1.1.2", 2048);
            var copy = UninstallEntry.Read(Registry.CurrentUser, key);
            Assert.NotNull(copy);
            Assert.Equal(@"D:\Apps\RigControl\app\RigControl.exe", copy!.Layout.AppExe);
            Assert.Equal("1.1.2", copy.Version);
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal(RegistryValueKind.DWord, k!.GetValueKind("EstimatedSize"));

            // A location that could make the uninstaller delete the wrong things is not used.
            using (var k = Registry.CurrentUser.OpenSubKey(key, writable: true)) k!.SetValue("InstallLocation", @"C:\");
            Assert.Null(UninstallEntry.Read(Registry.CurrentUser, key));

            UninstallEntry.Delete(Registry.CurrentUser, key);
            Assert.Null(UninstallEntry.Read(Registry.CurrentUser, key));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    // ---- start-up tasks ----

    [Fact]
    public void An_existing_users_own_task_is_pointed_at_the_installed_exe()
    {
        var changes = TaskPlan.ForInstall(Alice, Task(OldExe, Alice), Autostart.TaskAnswer.Missing, InstalledExe, startWithWindows: true);
        var c = Assert.Single(changes);
        Assert.Equal((Autostart.TaskNameFor(Alice), TaskChangeKind.Repoint, OldExe), (c.TaskName, c.Kind, c.OldCommand));
    }

    [Fact]
    public void The_old_shared_task_moves_only_when_it_is_this_users()
    {
        var mine = TaskPlan.ForInstall(Alice, Autostart.TaskAnswer.Missing, Task(OldExe, Alice), InstalledExe, null);
        Assert.Equal(Autostart.TaskName, Assert.Single(mine).TaskName);

        var theirs = TaskPlan.ForInstall(Alice, Autostart.TaskAnswer.Missing, Task(OldExe, Bob), InstalledExe, null);
        Assert.Empty(theirs);
    }

    [Fact]
    public void A_task_that_already_starts_the_installed_exe_is_left_alone()
    {
        Assert.Empty(TaskPlan.ForInstall(Alice, Task(InstalledExe, Alice), Autostart.TaskAnswer.Missing, InstalledExe, true));
        Assert.Empty(TaskPlan.ForInstall(Alice, Task("\"" + InstalledExe.ToUpperInvariant() + "\"", Alice), Autostart.TaskAnswer.Missing, InstalledExe, null));
    }

    [Fact]
    public void The_tick_makes_a_task_when_there_is_none_and_an_update_never_does()
    {
        var created = Assert.Single(TaskPlan.ForInstall(Alice, Autostart.TaskAnswer.Missing, Autostart.TaskAnswer.Missing, InstalledExe, true));
        Assert.Equal((Autostart.TaskNameFor(Alice), TaskChangeKind.Create), (created.TaskName, created.Kind));
        Assert.Empty(TaskPlan.ForInstall(Alice, Autostart.TaskAnswer.Missing, Autostart.TaskAnswer.Missing, InstalledExe, null));
    }

    [Fact]
    public void Unticking_removes_this_users_tasks_only()
    {
        var changes = TaskPlan.ForInstall(Alice, Task(OldExe, Alice), Task(OldExe, Bob), InstalledExe, false);
        var c = Assert.Single(changes);
        Assert.Equal((Autostart.TaskNameFor(Alice), TaskChangeKind.Remove), (c.TaskName, c.Kind));
    }

    [Fact]
    public void An_update_leaves_a_task_switched_off_by_hand_as_it_is()
    {
        Assert.Empty(TaskPlan.ForInstall(Alice, Task(OldExe, Alice, enabled: false), Autostart.TaskAnswer.Missing, InstalledExe, null));
        // Ticking the box in the setup window switches it back on.
        Assert.Single(TaskPlan.ForInstall(Alice, Task(InstalledExe, Alice, enabled: false), Autostart.TaskAnswer.Missing, InstalledExe, true));
    }

    [Fact]
    public void The_finish_screen_names_old_copies_that_are_still_on_disk()
    {
        var layout = new InstallLayout(Root);
        var changes = new[]
        {
            new TaskChange("a", TaskChangeKind.Repoint, "\"" + OldExe + "\""),
            new TaskChange("b", TaskChangeKind.Repoint, OldExe.ToLowerInvariant()),
            new TaskChange("c", TaskChangeKind.Repoint, @"E:\Gone\RigControl.exe"),
            new TaskChange("d", TaskChangeKind.Repoint, InstalledExe),
            new TaskChange("e", TaskChangeKind.Create, null),
        };
        var old = TaskPlan.OldCopies(changes, layout, p => !p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { OldExe }, old);
    }

    [Fact]
    public void Uninstalling_removes_this_users_task_and_a_shared_one_inside_the_install_folder()
    {
        var layout = new InstallLayout(Root);
        var inside = TaskPlan.ForUninstall(Alice, Task(InstalledExe, Alice), Task(InstalledExe, Bob), layout);
        Assert.Equal(new[] { Autostart.TaskNameFor(Alice), Autostart.TaskName }, inside.Select(c => c.TaskName));
        Assert.All(inside, c => Assert.Equal(TaskChangeKind.Remove, c.Kind));

        // A shared task that starts a copy somewhere else is not this install's to remove.
        var outside = TaskPlan.ForUninstall(Alice, Autostart.TaskAnswer.Missing, Task(OldExe, Bob), layout);
        Assert.Empty(outside);
    }

    // ---- licences ----

    [Fact]
    public void Licence_resource_names_map_to_files_under_licenses()
    {
        Assert.Equal("LICENSE", LicenceFiles.RelativeName("licenses/LICENSE"));
        Assert.Equal("LICENSES/MIT.txt", LicenceFiles.RelativeName("licenses/LICENSES/MIT.txt"));
        Assert.Null(LicenceFiles.RelativeName("licenses/../evil.txt"));
        Assert.Null(LicenceFiles.RelativeName("licenses/"));
        Assert.Null(LicenceFiles.RelativeName("RigControl.App.Something.resources"));
    }

    [Fact]
    public void The_exe_carries_the_terms_licences_and_notices_terms_first()
    {
        var names = LicenceFiles.Names();
        Assert.Equal(LicenceFiles.Terms, names[0]);
        foreach (var n in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md", "PRIVACY.md", "SECURITY.md", "README.md", "LICENSES/MIT.txt", "LICENSES/GPL-2.0.txt" })
            Assert.Contains(n, names);
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", LicenceFiles.Read("LICENSE"));
    }

    [Fact]
    public void Licences_are_written_into_their_folders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigcontrol-licences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var count = LicenceFiles.WriteTo(dir);
            Assert.Equal(LicenceFiles.Names().Count, count);
            Assert.True(File.Exists(Path.Combine(dir, "TERMS-OF-USE.md")));
            Assert.True(File.Exists(Path.Combine(dir, "LICENSES", "MIT.txt")));
            Assert.Empty(Directory.GetFiles(dir, "*.new", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp folder */ }
        }
    }

    [Fact]
    public void The_terms_read_as_plain_text()
    {
        var plain = TermsText.Plain("# Title\r\n\r\nSee **this** and [LICENSE](LICENSE).\n| A | B |\n|---|---|\n| **x** | `y` |\n");
        Assert.Equal("Title\n\nSee this and LICENSE.\n• A — B\n• x — y", plain);
    }

    // ---- uninstall clean-up ----

    [Fact]
    public void The_cleanup_line_deletes_the_folders_and_the_root_only_when_empty()
    {
        var script = CleanupScript.Build(new[] { InstalledExe[..InstalledExe.LastIndexOf('\\')] }, Root, null)!;
        Assert.StartsWith("/d /c \"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + Root + "\\app\" 2>nul", script);
        Assert.EndsWith("& rmdir \"" + Root + "\" 2>nul\"", script);
        Assert.DoesNotContain("rmdir /s /q \"" + Root + "\" ", script);
        Assert.Null(CleanupScript.Build(Array.Empty<string>(), null, null));
        Assert.Throws<ArgumentException>(() => CleanupScript.Build(new[] { @"C:\Apps\%TEMP%" }, null, null));
    }
}
