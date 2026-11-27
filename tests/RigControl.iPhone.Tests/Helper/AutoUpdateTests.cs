using RigControl.iPhone.Helper;
using Xunit;

namespace RigControl.iPhone.Tests.Helper;

public class AutoUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-autoupdate-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static InstalledApp App(string source, string? release) =>
        new("udid-1", "Phone", "io.github.rigcontrol.mobile", "0.1.0", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(300), source, release);

    [Theory]
    [InlineData(InstalledApp.ImportedSource, null, true)]       // installed before releases were noted: update once
    [InlineData(InstalledApp.ImportedSource, "1.2.2-rc.17", true)]
    [InlineData(InstalledApp.ImportedSource, "1.2.2", false)]  // already this release's app
    [InlineData(InstalledApp.AppleIdSource, "1.2.1", true)]
    [InlineData(InstalledApp.PresignedSource, null, false)]    // the person's own signed app: never replaced
    public void Update_is_due_only_for_another_release_RigControl_can_sign(string source, string? release, bool due)
        => Assert.Equal(due, App(source, release).UpdateDue("1.2.2"));

    [Fact]
    public void Old_status_files_without_a_release_still_load_and_the_release_is_kept_once_set()
    {
        var paths = new InstallerPaths(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(paths.Status, """
            [{"Udid":"udid-1","DeviceName":"Phone","BundleId":"b","Version":"0.1.0","SignedAt":"2026-11-27T00:00:00+00:00","Expires":"2027-11-27T00:00:00+00:00","Source":"imported"}]
            """);
        var app = Assert.Single(IphoneInstaller.LoadStatus(paths));
        Assert.Null(app.Release);
        Assert.True(app.UpdateDue("1.2.2"));

        IphoneInstaller.SetRelease(paths, "udid-1", "1.2.2");
        IphoneInstaller.SetRelease(paths, "someone-else", "1.2.2"); // unknown iPhone: nothing added
        app = Assert.Single(IphoneInstaller.LoadStatus(paths));
        Assert.Equal("1.2.2", app.Release);
        Assert.False(app.UpdateDue("1.2.2"));
        Assert.True(app.UpdateDue("1.2.3"));
    }

    [Fact]
    public void A_try_is_remembered_on_disk_for_three_hours_per_iPhone_and_release()
    {
        var paths = new InstallerPaths(_dir);
        var at = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.False(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.3", at));
        IphoneInstaller.NoteUpdateTry(paths, "udid-1", "1.2.3", at);
        Assert.True(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.3", at.AddHours(2)));
        Assert.False(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.3", at.AddHours(3)));
        Assert.False(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.4", at.AddHours(1))); // a new release: try at once
        Assert.False(IphoneInstaller.UpdateTriedRecently(paths, "udid-2", "1.2.3", at.AddHours(1)));
        Assert.False(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.3", at.AddHours(-1))); // a time in the future isn't trusted
    }

    [Fact]
    public void A_broken_tries_file_counts_as_no_tries()
    {
        var paths = new InstallerPaths(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(paths.UpdateTries, "not json");
        Assert.False(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.3", DateTimeOffset.UtcNow));
        IphoneInstaller.NoteUpdateTry(paths, "udid-1", "1.2.3", DateTimeOffset.UtcNow);
        Assert.True(IphoneInstaller.UpdateTriedRecently(paths, "udid-1", "1.2.3", DateTimeOffset.UtcNow));
    }
}
