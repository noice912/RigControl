using RigControl.Core.Logging;
using RigControl.Radio.Fingerprint;
using RigControl.Radio.Store;

namespace RigControl.Radio.Tests;

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = new();
    public void Info(string message) { lock (Lines) Lines.Add("INFO " + message); }
    public void Warn(string message, Exception? ex = null) { lock (Lines) Lines.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Lines) Lines.Add("ERROR " + message); }
    public ILog For(string source) => this;
}

public class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-radio-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "learned.rcfp");
    public StoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static IReadOnlyList<Landmark> Prints(int seed, double from, double to) =>
        Fingerprinter.Compute(TestAudio.Slice(TestAudio.Song(seed, to + 1), from, to));

    [Fact]
    public void Learned_song_survives_save_and_load()
    {
        var log = new ListLog();
        var store = LearnedSongs.Load(File_, log);
        var song = store.Learn("Song", "Band", "Album", 200, Prints(1, 0, 30), 0);
        store.Save();
        var again = LearnedSongs.Load(File_, log);
        var s = Assert.Single(again.Songs);
        Assert.Equal(("Song", "Band", "Album", 200d), (s.Title, s.Artist, s.Album, s.DurationSeconds!.Value));
        var m = again.Match(Prints(1, 10, 20), 10);
        Assert.Equal(song.Id, m!.SongId);
        Assert.InRange(m.PositionSeconds, 19.5, 20.5);
    }

    [Fact]
    public void Learning_the_same_song_again_adds_to_it()
    {
        var store = LearnedSongs.Load(File_, new ListLog());
        var a = store.Learn("Song", "Band", "", null, Prints(2, 0, 20), 0);
        var b = store.Learn("song", "BAND", "", null, Prints(2, 20, 40), 20); // more of it, later in the song
        Assert.Equal(a.Id, b.Id);
        Assert.Single(store.Songs);
        var m = store.Match(Prints(2, 25, 35), 10);
        Assert.Equal(a.Id, m!.SongId);
        Assert.InRange(m.PositionSeconds, 34.5, 35.5);
    }

    [Fact]
    public void Forget_removes_its_fingerprints()
    {
        var store = LearnedSongs.Load(File_, new ListLog());
        var s = store.Learn("Song", "Band", "", null, Prints(3, 0, 30), 0);
        Assert.True(store.Forget(s.Id));
        Assert.Null(store.Match(Prints(3, 5, 15), 10));
        Assert.Empty(store.Songs);
        Assert.False(store.Forget(s.Id));
    }

    [Fact]
    public void Rename_changes_the_names_only()
    {
        var store = LearnedSongs.Load(File_, new ListLog());
        var s = store.Learn("Sonng", "Band", "", null, Prints(4, 0, 30), 0);
        Assert.True(store.Rename(s.Id, "Song", "Band", "Album"));
        Assert.Equal("Song", store.Songs.Single().Title);
        Assert.Equal(s.Id, store.Match(Prints(4, 5, 15), 10)!.SongId);
    }

    [Fact]
    public void Damaged_file_loads_empty()
    {
        var bytes = new byte[5000];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(File_, bytes);
        var log = new ListLog();
        var store = LearnedSongs.Load(File_, log);
        Assert.Empty(store.Songs);
        Assert.Contains(log.Lines, l => l.StartsWith("WARN"));
        // A valid header followed by nonsense counts as damaged too.
        File.WriteAllBytes(File_, "RCFP"u8.ToArray().Concat(new byte[] { 1, 0, 255, 255, 255, 127 }).ToArray());
        Assert.Empty(LearnedSongs.Load(File_, log).Songs);
    }

    [Fact]
    public void Oversized_file_is_refused()
    {
        using (var f = File.Create(File_)) f.SetLength(LearnedSongs.MaxFileBytes + 1);
        var log = new ListLog();
        Assert.Empty(LearnedSongs.Load(File_, log).Songs);
        Assert.Contains(log.Lines, l => l.StartsWith("WARN"));
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_file()
    {
        var store = LearnedSongs.Load(File_, new ListLog());
        store.Learn("Song", "Band", "", null, Prints(5, 0, 10), 0);
        store.Save();
        Assert.True(File.Exists(File_));
        Assert.False(File.Exists(File_ + ".new"));
    }

    [Fact]
    public void Missing_file_is_an_empty_store_without_warnings()
    {
        var log = new ListLog();
        Assert.Empty(LearnedSongs.Load(File_, log).Songs);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("WARN"));
    }

    [Fact]
    public void Knows_which_stretches_of_a_song_are_already_learned()
    {
        var store = LearnedSongs.Load(File_, new ListLog());
        var s = store.Learn("Song", "Band", "", 200, Prints(6, 0, 15), 30);
        Assert.True(store.Covers(s.Id, 30, 15));
        Assert.False(store.Covers(s.Id, 60, 15));
        Assert.False(store.Covers(s.Id + 1, 30, 15));
    }

    [Fact]
    public void Stops_learning_at_the_total_limit_and_stays_loadable()
    {
        var store = LearnedSongs.Load(File_, new ListLog());
        store.TotalLandmarkLimit = 1000;
        store.Learn("A", "Band", "", null, Prints(7, 0, 30), 0);
        Assert.Throws<InvalidOperationException>(() => store.Learn("B", "Band", "", null, Prints(8, 0, 30), 0));
        Assert.True(store.TotalLandmarks <= 1000);
        store.Save();
        Assert.Single(LearnedSongs.Load(File_, new ListLog()).Songs);
    }

    [Fact]
    public void Never_saves_through_a_folder_link()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir, "radio");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link));
        var store = LearnedSongs.Load(Path.Combine(link, "learned.rcfp"), new ListLog());
        store.Learn("Song", "Band", "", null, Prints(9, 0, 10), 0);
        Assert.Throws<IOException>(() => store.Save());
        Assert.Empty(Directory.GetFiles(target));
    }
}
