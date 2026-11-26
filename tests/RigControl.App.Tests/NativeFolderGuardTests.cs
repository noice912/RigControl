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
}
