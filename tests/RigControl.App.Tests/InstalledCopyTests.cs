using RigControl.App.Updates;

namespace RigControl.App.Tests;

public sealed class InstalledCopyTests
{
    [Theory]
    [InlineData(@"C:\Program Files\RigControl\app\RigControl.exe", @"C:\Program Files\RigControl", true)]
    [InlineData(@"C:\Program Files\RigControl\app\RigControl.exe", @"C:\Program Files\RigControl\", true)]
    [InlineData(@"c:\program files\rigcontrol\APP\rigcontrol.exe", @"C:\Program Files\RigControl", true)]
    [InlineData(@"C:\Program Files\RigControl\app\RigControl.exe", "\"C:\\Program Files\\RigControl\"", true)]
    [InlineData(@"C:\Users\Someone\Downloads\RigControl.exe", @"C:\Program Files\RigControl", false)]
    [InlineData(@"C:\Program Files\RigControl\RigControl.exe", @"C:\Program Files\RigControl", false)]
    [InlineData(@"C:\Program Files\RigControl\app\RigControl.exe", null, false)]
    [InlineData(@"C:\Program Files\RigControl\app\RigControl.exe", "", false)]
    public void Only_the_exe_in_the_install_folders_app_folder_counts(string exe, string? location, bool expected)
        => Assert.Equal(expected, InstalledCopy.Matches(exe, location));

    [Fact]
    public void Updates_go_into_the_install_folder()
        => Assert.Equal(@"C:\Program Files\RigControl\updates", InstalledCopy.UpdatesFolder(@"C:\Program Files\RigControl"));
}
