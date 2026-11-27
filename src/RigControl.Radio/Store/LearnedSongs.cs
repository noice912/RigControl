using System.Text;
using RigControl.Core.Logging;
using RigControl.Radio.Fingerprint;

namespace RigControl.Radio.Store;

/// <summary>A song this PC has learned: its names and when it was first learned.</summary>
public sealed record LearnedSong(int Id, string Title, string Artist, string Album, double? DurationSeconds, DateTimeOffset LearnedAt);

/// <summary>
/// The songs this PC has learned, with their audio fingerprints, kept in one file
/// (<c>%LOCALAPPDATA%\RigControl\radio\learned.rcfp</c>). The file is only data: read with size limits, and a file
/// that can't be read is treated as empty (and said so in the log), never trusted. Thread-safe.
/// </summary>
public sealed class LearnedSongs
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const int MaxSongs = 20000;
    public const int MaxLandmarksPerSong = 200_000;
    public const int MaxText = 256;

    /// <summary>All songs together: 8 bytes each in the file, so the file stays well under <see cref="MaxFileBytes"/>.</summary>
    public const int DefaultTotalLandmarkLimit = 20_000_000;

    /// <summary>Stretches of a song are counted as learned in 5-second steps.</summary>
    private const double BucketSeconds = 5;
    private static readonly byte[] Magic = "RCFP"u8.ToArray();
    private const ushort Version = 1;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _songs = new();
    private readonly Dictionary<uint, List<(int Song, int Frame)>> _index = new();
    private int _nextId = 1;
    private int _total;

    private sealed class Entry(LearnedSong song)
    {
        public LearnedSong Song { get; set; } = song;
        public List<Landmark> Landmarks { get; } = new();
        public HashSet<int> Buckets { get; } = new();
    }

    /// <summary>The most landmarks kept for all songs together (settable for tests).</summary>
    internal int TotalLandmarkLimit { get; set; } = DefaultTotalLandmarkLimit;

    internal int TotalLandmarks
    {
        get { lock (_gate) return _total; }
    }

    private LearnedSongs(string path) => _path = path;

    public IReadOnlyList<LearnedSong> Songs
    {
        get { lock (_gate) return _songs.Values.Select(e => e.Song).OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public static LearnedSongs Load(string path, ILog log)
    {
        var store = new LearnedSongs(path);
        try
        {
            if (!File.Exists(path)) return store;
            // Read only the real file in the real folder, never something a link points at.
            if (SafeFolder.ReachedThroughLink(path))
                {
                    log.Warn("In-game radio: the learned songs file is reached through a link, so it wasn't read. Starting with no learned songs.");
                    return store;
                }
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                log.Warn($"In-game radio: the learned songs file is larger than {MaxFileBytes / (1024 * 1024)} MB, so it wasn't read. Starting with no learned songs.");
                return store;
            }
            store.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            log.Warn("In-game radio: the learned songs file couldn't be read (" + ex.GetType().Name + "). Starting with no learned songs.");
            return new LearnedSongs(path);
        }
        return store;
    }

    /// <summary>
    /// Adds fingerprints for a song heard from <paramref name="startSeconds"/> into it. The same title and artist
    /// (any case) adds to the song already learned. Throws InvalidOperationException when the store is full.
    /// </summary>
    public LearnedSong Learn(string title, string artist, string album, double? duration, IReadOnlyList<Landmark> landmarks, double startSeconds)
    {
        lock (_gate)
        {
            var entry = _songs.Values.FirstOrDefault(e =>
                string.Equals(e.Song.Title, title, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Song.Artist, artist, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                if (_songs.Count >= MaxSongs || _total >= TotalLandmarkLimit)
                    throw new InvalidOperationException("RigControl's list of learned songs is full. Forget some songs first.");
                entry = new Entry(new LearnedSong(_nextId++, Cut(title), Cut(artist), Cut(album), duration, DateTimeOffset.UtcNow));
                _songs[entry.Song.Id] = entry;
            }
            else if (entry.Song.DurationSeconds is null && duration is not null)
            {
                entry.Song = entry.Song with { DurationSeconds = duration };
            }
            var offset = (int)Math.Round(Math.Max(0, startSeconds) / Fingerprinter.SecondsPerFrame);
            foreach (var l in landmarks)
            {
                if (entry.Landmarks.Count >= MaxLandmarksPerSong || _total >= TotalLandmarkLimit) break;
                var shifted = new Landmark(l.Hash, l.Frame + offset);
                Add(entry, shifted);
            }
            return entry.Song;
        }
    }

    /// <summary>Recognises <paramref name="query"/> (fingerprints of <paramref name="heardSeconds"/> of sound).</summary>
    public MatchResult? Match(IReadOnlyList<Landmark> query, double heardSeconds)
    {
        lock (_gate) return Matcher.Best(query, h => _index.TryGetValue(h, out var l) ? l : [], heardSeconds);
    }

    /// <summary>True when the whole stretch from <paramref name="startSeconds"/> (for <paramref name="seconds"/>) of song <paramref name="id"/> is already learned.</summary>
    public bool Covers(int id, double startSeconds, double seconds)
    {
        lock (_gate)
        {
            if (!_songs.TryGetValue(id, out var e)) return false;
            var first = (int)Math.Floor(Math.Max(0, startSeconds) / BucketSeconds);
            var last = (int)Math.Floor(Math.Max(0, startSeconds + seconds - 0.001) / BucketSeconds);
            for (var b = first; b <= last; b++)
                if (!e.Buckets.Contains(b)) return false;
            return true;
        }
    }

    public LearnedSong? Get(int id)
    {
        lock (_gate) return _songs.TryGetValue(id, out var e) ? e.Song : null;
    }

    public bool Rename(int id, string title, string artist, string album)
    {
        lock (_gate)
        {
            if (!_songs.TryGetValue(id, out var e)) return false;
            e.Song = e.Song with { Title = Cut(title), Artist = Cut(artist), Album = Cut(album) };
            return true;
        }
    }

    public bool Forget(int id)
    {
        lock (_gate)
        {
            if (!_songs.Remove(id, out var e)) return false;
            _total -= e.Landmarks.Count;
            foreach (var l in e.Landmarks)
                if (_index.TryGetValue(l.Hash, out var list))
                {
                    list.RemoveAll(x => x.Song == id);
                    if (list.Count == 0) _index.Remove(l.Hash);
                }
            return true;
        }
    }

    /// <summary>Writes the file (to a ".new" file first, then moved over the old one).</summary>
    public void Save()
    {
        byte[] bytes;
        lock (_gate) bytes = Write();
        SafeFolder.WriteFile(_path, bytes);
    }

    private void Add(Entry entry, Landmark l)
    {
        entry.Landmarks.Add(l);
        entry.Buckets.Add((int)Math.Floor(l.Frame * Fingerprinter.SecondsPerFrame / BucketSeconds));
        AddToIndex(entry.Song.Id, l);
        _total++;
    }

    private void AddToIndex(int song, Landmark l)
    {
        if (!_index.TryGetValue(l.Hash, out var list)) _index[l.Hash] = list = new();
        list.Add((song, l.Frame));
    }

    private static string Cut(string s) => s.Length <= MaxText ? s : s[..MaxText];

    private byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write(Version);
        w.Write(_nextId);
        w.Write(_songs.Count);
        foreach (var e in _songs.Values)
        {
            var s = e.Song;
            w.Write(s.Id);
            Text(w, s.Title);
            Text(w, s.Artist);
            Text(w, s.Album);
            w.Write(s.DurationSeconds ?? double.NaN);
            w.Write(s.LearnedAt.UtcTicks);
            w.Write(e.Landmarks.Count);
            foreach (var l in e.Landmarks)
            {
                w.Write(l.Hash);
                w.Write(l.Frame);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    private static void Text(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    private void Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadUInt16() != Version)
            throw new InvalidDataException("Not a learned songs file.");
        var nextId = r.ReadInt32();
        var count = r.ReadInt32();
        if (count < 0 || count > MaxSongs) throw new InvalidDataException("Bad song count.");
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadInt32();
            var title = ReadText(r);
            var artist = ReadText(r);
            var album = ReadText(r);
            var duration = r.ReadDouble();
            var ticks = r.ReadInt64();
            var n = r.ReadInt32();
            if (id <= 0 || _songs.ContainsKey(id) || n < 0 || n > MaxLandmarksPerSong || (long)n * 8 > bytes.Length - r.BaseStream.Position
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
                || !(double.IsNaN(duration) || duration is >= 0 and <= 86400))
                throw new InvalidDataException("Bad song entry.");
            var entry = new Entry(new LearnedSong(id, title, artist, album, double.IsNaN(duration) ? null : duration, new DateTimeOffset(ticks, TimeSpan.Zero)));
            if (_total + (long)n > DefaultTotalLandmarkLimit) throw new InvalidDataException("Too many landmarks.");
            for (var k = 0; k < n; k++) Add(entry, new Landmark(r.ReadUInt32(), r.ReadInt32()));
            _songs[id] = entry;
        }
        if (r.BaseStream.Position != bytes.Length) throw new InvalidDataException("Extra bytes at the end.");
        _nextId = Math.Max(nextId, _songs.Keys.DefaultIfEmpty(0).Max() + 1);
    }

    private static string ReadText(BinaryReader r)
    {
        var len = r.ReadUInt16();
        if (len > MaxText * 4) throw new InvalidDataException("Text too long.");
        var s = new UTF8Encoding(false, true).GetString(r.ReadBytes(len));
        if (s.Length > MaxText || s.Any(char.IsControl)) throw new InvalidDataException("Bad text.");
        return s;
    }
}
