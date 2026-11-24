using RigControl.Lighting;
using Candidate = RigControl.Lighting.OpenRgbServer.Candidate;
using Place = RigControl.Lighting.OpenRgbServer.Place;

namespace RigControl.Lighting.Tests;

public class OpenRgbServerTests
{
    private const string Own = @"C:\Program Files\RigControl\OpenRGB\OpenRGB.exe";
    private const string Installed = @"C:\Program Files\OpenRGB\OpenRGB.exe";
    private const string UserInstall = @"C:\Users\someone\Tools\OpenRGB\OpenRGB.exe";
    private const string UserFolder = @"C:\Users\someone\AppData\Local\Programs\OpenRGB\OpenRGB.exe";

    private static string? Choose(bool elevated, IEnumerable<Candidate> candidates, Func<string, bool>? othersCanChange = null, params string[] missing) =>
        OpenRgbServer.Choose(candidates, elevated, p => !missing.Contains(p, StringComparer.OrdinalIgnoreCase), othersCanChange ?? (_ => false));

    [Fact]
    public void As_administrator_an_OpenRGB_from_the_users_own_install_list_or_folder_is_never_used()
    {
        var onlyUserPlaces = new[] { new Candidate(UserInstall, Place.UserInstall), new Candidate(UserFolder, Place.UserFolder) };
        Assert.Null(Choose(elevated: true, onlyUserPlaces));

        // Even when it is listed first, the next safe one is taken instead.
        var mixed = new[] { new Candidate(UserInstall, Place.UserInstall), new Candidate(Installed, Place.MachineInstall), new Candidate(UserFolder, Place.UserFolder) };
        Assert.Equal(Installed, Choose(elevated: true, mixed));
    }

    [Fact]
    public void Without_administrator_rights_the_users_own_install_is_still_found()
    {
        var onlyUserPlaces = new[] { new Candidate(UserInstall, Place.UserInstall), new Candidate(UserFolder, Place.UserFolder) };
        Assert.Equal(UserInstall, Choose(elevated: false, onlyUserPlaces));
    }

    [Fact]
    public void As_administrator_a_file_that_ordinary_programs_can_replace_is_skipped_wherever_it_was_listed()
    {
        // e.g. a service that somebody pointed at a folder on a data drive.
        var writable = @"D:\Tools\OpenRGB\OpenRGB.exe";
        var list = new[] { new Candidate(writable, Place.Service), new Candidate(Installed, Place.ProgramFiles) };
        Assert.Equal(Installed, Choose(elevated: true, list, p => p == writable));
        Assert.Null(Choose(elevated: true, new[] { new Candidate(writable, Place.Service) }, p => p == writable));
        // Not elevated: nothing is gained by replacing it, so it is used as before.
        Assert.Equal(writable, Choose(elevated: false, list, p => p == writable));
    }

    [Fact]
    public void RigControls_own_copy_is_the_first_candidate()
    {
        Assert.EndsWith(@"\RigControl\OpenRGB\OpenRGB.exe", OpenRgbServer.OwnCopyPath);

        var list = new[] { new Candidate(Own, Place.OwnCopy), new Candidate(Installed, Place.Service) };
        Assert.Equal(Own, Choose(elevated: true, list));
        Assert.Equal(Installed, Choose(elevated: true, list, null, Own)); // not unpacked: the installed one
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OpenRGB.exe")]
    [InlineData(@"..\OpenRGB.exe")]
    [InlineData(@"\\server\share\OpenRGB.exe")]
    [InlineData("C:\\bad\0path\\OpenRGB.exe")]
    public void Empty_relative_network_and_broken_paths_are_never_chosen(string? path)
    {
        Assert.Null(Choose(elevated: true, new[] { new Candidate(path, Place.Service) }));
        Assert.Null(Choose(elevated: false, new[] { new Candidate(path, Place.Service) }));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\RigControl\\OpenRGB\\OpenRGB.exe\"", @"C:\Program Files\RigControl\OpenRGB\OpenRGB.exe")]
    [InlineData("\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\" --server --server-port 6742", @"C:\Program Files\OpenRGB\OpenRGB.exe")]
    [InlineData(@"C:\Program Files\OpenRGB\OpenRGB.exe --server", @"C:\Program Files\OpenRGB\OpenRGB.exe")]
    [InlineData(@"  D:\Tools\OpenRGB.EXE  ", @"D:\Tools\OpenRGB.EXE")]
    public void The_program_is_read_out_of_a_service_command_line(string imagePath, string exe)
    {
        Assert.Equal(exe, OpenRgbServer.ServiceExecutable(imagePath));
    }

    [Fact]
    public void RigControls_own_copy_is_checked_against_its_recorded_files_before_it_is_started()
    {
        var asked = new List<string>();
        var bad = new OpenRgbServer(p => { asked.Add(p); return false; }, elevated: true, _ => false);
        var e = Assert.Throws<IOException>(() => bad.CheckBeforeStarting(OpenRgbServer.OwnCopyPath));
        Assert.Contains("not the ones RigControl came with", e.Message);
        Assert.Equal(new[] { OpenRgbServer.OwnCopyPath }, asked);
        // StartServer runs the same check first, so nothing is started.
        Assert.Throws<IOException>(() => bad.StartServer(OpenRgbServer.OwnCopyPath));
        Assert.False(bad.OwnsServer);

        var good = new OpenRgbServer(_ => true, elevated: true, _ => false);
        good.CheckBeforeStarting(OpenRgbServer.OwnCopyPath);
    }

    [Fact]
    public void An_own_copy_that_fails_its_check_is_passed_over_for_another_install()
    {
        var list = new[] { new Candidate(Own, Place.OwnCopy), new Candidate(Installed, Place.ProgramFiles) };
        var asked = 0;
        var ok = false;
        var server = new OpenRgbServer(_ => { asked++; return ok; }, elevated: true, _ => false);
        Assert.Equal(Installed, server.Find(list, _ => true));
        Assert.Null(server.Find(list.Take(1), _ => true));
        Assert.Equal(1, asked); // the answer is remembered for a moment: the files are not read again on every look

        // Not unpacked at all: nothing to check.
        Assert.Equal(Installed, server.Find(list, p => p != Own));
        Assert.Equal(1, asked);

        var fine = new OpenRgbServer(_ => true, elevated: true, _ => false);
        Assert.Equal(Own, fine.Find(list, _ => true));
        // No verifier set (a build that was not wired up): the copy is still used, guarded by the folder check alone.
        var saved = OpenRgbServer.OwnCopyVerifier;
        try
        {
            OpenRgbServer.OwnCopyVerifier = null;
            Assert.Equal(Own, new OpenRgbServer(null, elevated: true, _ => false).Find(list, _ => true));
            OpenRgbServer.OwnCopyVerifier = _ => false;
            Assert.Equal(Installed, new OpenRgbServer(null, elevated: true, _ => false).Find(list, _ => true));
        }
        finally
        {
            OpenRgbServer.OwnCopyVerifier = saved;
        }
    }

    [Fact]
    public void Another_OpenRGB_install_is_not_held_to_RigControls_file_list()
    {
        var asked = 0;
        var server = new OpenRgbServer(_ => { asked++; return false; }, elevated: true, _ => false);
        server.CheckBeforeStarting(Installed);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void As_administrator_nothing_is_started_from_a_place_ordinary_programs_can_change()
    {
        var server = new OpenRgbServer(_ => true, elevated: true, _ => true);
        var e = Assert.Throws<IOException>(() => server.StartServer(UserFolder));
        Assert.Contains("without administrator rights", e.Message);
        Assert.False(server.OwnsServer);
        // Not elevated, the same place passes the check (nothing is started here: only the check is called).
        new OpenRgbServer(_ => true, elevated: false, _ => true).CheckBeforeStarting(UserFolder);
    }

    [Fact]
    public void A_file_in_the_users_own_folders_counts_as_changeable_and_a_Windows_file_does_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigcontrol-openrgb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "OpenRGB.exe");
            File.WriteAllText(file, "not a program");
            Assert.True(OrdinaryUsers.CanChange(file));
            Assert.True(OrdinaryUsers.CanChange(Path.Combine(dir, "missing.exe")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        Assert.False(OrdinaryUsers.CanChange(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
        Assert.False(OrdinaryUsers.CanChange(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "not-there.exe")));
        // A folder that does not exist can't be vouched for.
        Assert.True(OrdinaryUsers.CanChange(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RigControl-not-there", "OpenRGB.exe")));
        Assert.True(OrdinaryUsers.CanChange(@"\\server\share\OpenRGB.exe"));
    }
}
