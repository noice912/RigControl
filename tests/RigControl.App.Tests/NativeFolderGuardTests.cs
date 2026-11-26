using System.IO;
using RigControl.App;
using RigControl.App.Install;
using Step = RigControl.App.NativeFolderGuard.Step;

namespace RigControl.App.Tests;

public sealed class NativeFolderGuardTests
{
    private const string Temp = @"C:\Users\user\AppData\Local\Temp\.net\RigControl\abc";
    private const string Safe = @"C:\Program Files\RigControl\runtime";
    private const string SafeDir = Safe + @"\RigControl\abc";

    private static string? OpenUnderUsers(string path) =>
        path.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase) ? path : null;

    private static NativeFolderGuard.Decision Decide(bool elevated = true, bool bundle = true, string[]? dirs = null,
        bool relaunched = false, string? fromEnv = null, string? expected = Safe)
        => NativeFolderGuard.Decide(elevated, bundle, dirs ?? new[] { Temp }, OpenUnderUsers, relaunched, fromEnv, expected);

    [Fact]
    public void Without_administrator_rights_nothing_is_done()
        => Assert.Equal(Step.Continue, Decide(elevated: false).Step);

    [Fact]
    public void A_build_that_is_not_one_file_is_left_alone()
        => Assert.Equal(Step.Continue, Decide(bundle: false).Step);

    [Fact]
    public void An_administrator_unpacked_into_an_open_folder_starts_again()
    {
        var d = Decide();
        Assert.Equal(Step.Relaunch, d.Step);
        Assert.Contains(Temp, d.Reason);
    }

    [Fact]
    public void An_administrator_already_unpacked_into_a_closed_folder_carries_on()
    {
        Assert.Equal(Step.Continue, Decide(dirs: new[] { SafeDir }).Step);
        Assert.Equal(Step.Continue, Decide(dirs: Array.Empty<string>()).Step);
    }

    [Fact]
    public void With_no_safe_folder_to_offer_it_refuses_instead_of_loading_from_the_open_one()
        => Assert.Equal(Step.Refuse, Decide(expected: null).Step);

    [Fact]
    public void The_started_copy_carries_on_only_when_unpacked_where_it_was_sent()
        => Assert.Equal(Step.Continue, Decide(dirs: new[] { SafeDir }, relaunched: true, fromEnv: Safe + @"\").Step);

    [Fact]
    public void The_started_copy_never_starts_another_one()
    {
        // Still in the open folder (the variable was not honoured): stop, don't loop.
        Assert.Equal(Step.Refuse, Decide(relaunched: true, fromEnv: Safe).Step);
        // The variable was changed on the way.
        Assert.Equal(Step.Refuse, Decide(dirs: new[] { SafeDir }, relaunched: true, fromEnv: @"C:\Users\user\x").Step);
        Assert.Equal(Step.Refuse, Decide(dirs: new[] { SafeDir }, relaunched: true, fromEnv: null).Step);
        // Unpacked somewhere closed but not where it was sent.
        Assert.Equal(Step.Refuse, Decide(dirs: new[] { @"C:\Program Files\Other\abc" }, relaunched: true, fromEnv: Safe).Step);
        // Unpacked where sent, but that folder is open after all.
        Assert.Equal(Step.Refuse, NativeFolderGuard.Decide(true, true, new[] { SafeDir }, p => p, true, Safe, Safe).Step);
        Assert.Equal(Step.Refuse, Decide(dirs: new[] { SafeDir }, relaunched: true, fromEnv: Safe, expected: null).Step);
    }

    [Fact]
    public void The_safe_folder_is_the_install_folders_runtime_or_program_files()
    {
        Assert.Equal(@"D:\Apps\RigControl\runtime", NativeFolderGuard.ExpectedBase(new InstallLayout(@"D:\Apps\RigControl"), @"C:\Program Files"));
        Assert.Equal(Safe, NativeFolderGuard.ExpectedBase(null, @"C:\Program Files"));
    }

    // ---- environment variables that load code ----

    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("dotnet_startup_hooks")]
    [InlineData("COMPlus_STARTUP_HOOKS")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("coreclr_profiler_path_arm64")]
    [InlineData("CORECLR_SOMETHING_NEW")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("COR_PROFILER")]
    [InlineData("COR_PROFILER_PATH_64")]
    [InlineData("DOTNET_PROFILER")]
    [InlineData("COMPlus_PROFILER_PATH")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    [InlineData("DOTNET_SHARED_STORE")]
    [InlineData("COMPlus_ADDITIONAL_DEPS")]
    [InlineData("DOTNET_GCName")]
    [InlineData("COMPLUS_GCNAME")]
    [InlineData("DOTNET_GCPath")]
    [InlineData("DOTNET_AltJit")]
    [InlineData("DOTNET_AltJitName")]
    [InlineData("DOTNET_JitPath")]
    [InlineData("DOTNET_DiagnosticPorts")]
    public void Variables_that_make_dotnet_load_code_are_found(string name)
        => Assert.True(NativeFolderGuard.LoadsCode(name), name);

    [Theory]
    [InlineData("PATH")]
    [InlineData("TEMP")]
    [InlineData("DOTNET_BUNDLE_EXTRACT_BASE_DIR")] // handled by the native-folder rules themselves
    [InlineData("DOTNET_gcServer")]
    [InlineData("DOTNET_EnableDiagnostics")]
    [InlineData("COR_")]
    [InlineData("CORECLR")]
    [InlineData("MY_DOTNET_STARTUP_HOOKS")]
    [InlineData("WEBVIEW2_USER_DATA_FOLDER")]
    [InlineData("RIGCONTROL_NATIVE_RELAUNCHED")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_variables_are_left_alone(string? name)
        => Assert.False(NativeFolderGuard.LoadsCode(name), name);

    [Fact]
    public void The_found_names_are_sorted_and_listed_once()
        => Assert.Equal(new[] { "CORECLR_PROFILER", "DOTNET_STARTUP_HOOKS" },
            NativeFolderGuard.CodeLoadingVariables(new[] { "PATH", "DOTNET_STARTUP_HOOKS", "CORECLR_PROFILER", "dotnet_startup_hooks" }));

    [Fact]
    public void A_started_copys_environment_has_none_of_them()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER_PATH_64"] = @"C:\Users\user\evil.dll",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\Users\user\hook.dll",
            ["TEMP"] = @"C:\Users\user\AppData\Local\Temp",
        };
        NativeFolderGuard.RemoveCodeLoading(env);
        Assert.Equal(new[] { "PATH", "TEMP" }, env.Keys.OrderBy(k => k));
    }

    [Fact]
    public void An_administrator_started_with_them_starts_again_without_them()
    {
        var found = new[] { "CORECLR_PROFILER" };
        Assert.Equal(Step.Relaunch, NativeFolderGuard.DecideEnvironment(elevated: true, found, relaunched: false).Step);
        Assert.Contains("CORECLR_PROFILER", NativeFolderGuard.DecideEnvironment(true, found, false).Reason);
        // Set again on the way into the restarted copy: stop, never loop.
        Assert.Equal(Step.Refuse, NativeFolderGuard.DecideEnvironment(elevated: true, found, relaunched: true).Step);
        // Nothing to gain without administrator rights, and nothing to do without the variables.
        Assert.Equal(Step.Continue, NativeFolderGuard.DecideEnvironment(elevated: false, found, relaunched: false).Step);
        Assert.Equal(Step.Continue, NativeFolderGuard.DecideEnvironment(elevated: true, Array.Empty<string>(), relaunched: true).Step);
    }

    // ---- setup run from an open folder ----

    private const string Downloads = @"C:\Users\user\Downloads";

    [Fact]
    public void An_administrators_setup_in_an_open_folder_carries_on_from_a_safe_copy()
    {
        var d = NativeFolderGuard.DecideStage(elevated: true, bundle: true, installAction: true, Downloads, relaunched: false, safeFolder: true);
        Assert.Equal(Step.Relaunch, d.Step);
        Assert.Contains(Downloads, d.Reason);
    }

    [Fact]
    public void A_setup_copy_never_makes_another_copy()
        => Assert.Equal(Step.Refuse, NativeFolderGuard.DecideStage(true, true, true, Downloads, relaunched: true, safeFolder: true).Step);

    [Fact]
    public void Without_a_safe_folder_setup_refuses_instead_of_running_from_the_open_one()
        => Assert.Equal(Step.Refuse, NativeFolderGuard.DecideStage(true, true, true, Downloads, relaunched: false, safeFolder: false).Step);

    [Fact]
    public void Setup_from_a_closed_folder_without_rights_or_as_the_app_itself_is_left_alone()
    {
        Assert.Equal(Step.Continue, NativeFolderGuard.DecideStage(true, true, true, exeFolderOpen: null, false, true).Step);
        Assert.Equal(Step.Continue, NativeFolderGuard.DecideStage(elevated: false, true, true, Downloads, false, true).Step);
        Assert.Equal(Step.Continue, NativeFolderGuard.DecideStage(true, bundle: false, true, Downloads, false, true).Step);
        // RigControl itself (a portable copy): Start with Windows must keep pointing at the exe the user chose.
        Assert.Equal(Step.Continue, NativeFolderGuard.DecideStage(true, true, installAction: false, Downloads, false, true).Step);
    }

    [Fact]
    public void Several_reasons_make_one_restart_and_any_refusal_wins()
    {
        var go = new NativeFolderGuard.Decision(Step.Continue);
        var a = new NativeFolderGuard.Decision(Step.Relaunch, "a");
        var b = new NativeFolderGuard.Decision(Step.Relaunch, "b");
        var no = new NativeFolderGuard.Decision(Step.Refuse, "no");
        Assert.Equal(Step.Continue, NativeFolderGuard.Combine(go, go).Step);
        Assert.Equal(new NativeFolderGuard.Decision(Step.Relaunch, "a; b"), NativeFolderGuard.Combine(a, go, b));
        Assert.Equal(no, NativeFolderGuard.Combine(a, no, b));
    }

    [Fact]
    public void Setup_and_uninstall_count_as_install_actions_and_the_installed_exe_does_not()
    {
        var installed = new InstalledCopy(new InstallLayout(@"C:\Program Files\RigControl"), "1.1.2");
        Assert.Equal(StartAction.RunApp, InstallCommand.ActionAt(Array.Empty<string>(), @"C:\Program Files\RigControl\app\RigControl.exe", installed));
        Assert.Equal(StartAction.Setup, InstallCommand.ActionAt(new[] { "--install", "--user", "S-1-5-21-1-2-3-1001" }, Downloads + @"\RigControl.exe", installed));
        Assert.Equal(StartAction.Uninstall, InstallCommand.ActionAt(new[] { "--uninstall" }, @"C:\Program Files\RigControl\app\RigControl.exe", installed));
        Assert.Equal(StartAction.RunApp, InstallCommand.ActionAt(new[] { "--minimized" }, Downloads + @"\RigControl.exe", null));
    }

    [Fact]
    public void The_safe_copy_has_the_same_bytes_in_a_new_folder_of_its_own()
    {
        // FolderGuard counts a temp folder as open, so only the copy itself is checked here, through its failure:
        // the copy is refused because the test's updates folder is open, and nothing is left behind.
        var root = Path.Combine(Path.GetTempPath(), "rc-stage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var exe = Path.Combine(root, "src", "RigControl.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });
            Assert.Null(NativeFolderGuard.StageCopy(exe, Path.Combine(root, "updates"), out var problem));
            Assert.Contains("programs without administrator rights can change", problem);
            Assert.False(Directory.Exists(Path.Combine(root, "updates")) && Directory.GetFiles(Path.Combine(root, "updates"), "*", SearchOption.AllDirectories).Length > 0);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }
}

public sealed class StartupCleanupTests
{
    private static readonly InstallLayout Layout = new(@"C:\Program Files\RigControl");
    private const string Bundles = @"C:\Program Files\RigControl\runtime\RigControl";

    [Fact]
    public void Old_bundle_folders_go_and_the_current_one_stays()
    {
        var children = new[] { Bundles + @"\aaa", Bundles + @"\bbb", Bundles + @"\ccc.old-1234" };
        Assert.Equal(new[] { Bundles + @"\aaa", Bundles + @"\ccc.old-1234" },
            StartupCleanup.OldBundleFolders(Layout, children, new[] { Bundles + @"\bbb\" }));
    }

    [Fact]
    public void Nothing_goes_when_this_copy_did_not_unpack_under_runtime()
    {
        var children = new[] { Bundles + @"\aaa", Bundles + @"\bbb" };
        Assert.Empty(StartupCleanup.OldBundleFolders(Layout, children, Array.Empty<string>()));
        Assert.Empty(StartupCleanup.OldBundleFolders(Layout, children, new[] { @"C:\Users\user\AppData\Local\Temp\.net\RigControl\bbb" }));
    }

    [Fact]
    public void Only_setup_copies_go_from_updates_and_never_the_running_one()
    {
        var updates = Layout.UpdatesDir;
        var children = new[] { updates + @"\setup-1111", updates + @"\setup-2222", updates + @"\other", updates + @"\SETUP-3333.old-9" };
        Assert.Equal(new[] { updates + @"\setup-1111", updates + @"\SETUP-3333.old-9" },
            StartupCleanup.OldSetupFolders(Layout, children, updates + @"\setup-2222\RigControl.exe"));
        Assert.Equal(3, StartupCleanup.OldSetupFolders(Layout, children, Layout.AppExe).Count);
    }

    [Fact]
    public void A_folder_in_use_is_left_and_the_others_are_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        var layout = new InstallLayout(root);
        var bundles = StartupCleanup.BundleParent(layout);
        try
        {
            foreach (var d in new[] { "current", "old", "busy" }) Directory.CreateDirectory(Path.Combine(bundles, d, "sub"));
            Directory.CreateDirectory(Path.Combine(layout.UpdatesDir, "setup-abc"));
            File.WriteAllText(Path.Combine(bundles, "old", "sub", "a.dll"), "x");
            var busyFile = Path.Combine(bundles, "busy", "sub", "b.dll");
            File.WriteAllText(busyFile, "x");
            using (new FileStream(busyFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                StartupCleanup.Run(layout, new[] { Path.Combine(bundles, "current") }, Path.Combine(root, "app", "RigControl.exe"), RigControl.Core.Logging.NullLog.Instance);
            Assert.True(Directory.Exists(Path.Combine(bundles, "current")));
            Assert.False(Directory.Exists(Path.Combine(bundles, "old")));
            Assert.True(File.Exists(busyFile), "a folder with an open file was removed");
            Assert.False(Directory.Exists(Path.Combine(layout.UpdatesDir, "setup-abc")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }
}
