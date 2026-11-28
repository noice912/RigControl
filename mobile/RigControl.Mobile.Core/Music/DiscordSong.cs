namespace RigControl.Mobile.Core.Music;

/// <summary>
/// The song the phone puts on Discord itself (signed in with Discord on the phone, no PC): title, artist, album and
/// when it started and ends, in Unix milliseconds (null when the length isn't known). Texts are cleaned like
/// presence.phoneMusic's (trimmed, at most 128 characters, which is Discord's limit for these fields).
/// </summary>
public sealed record DiscordSong(string Title, string? Artist, string? Album, long? StartMs, long? EndMs)
{
    public const int MaxText = 128;

    /// <summary>What to show for <paramref name="track"/> at <paramref name="now"/>; null when nothing plays (paused counts as nothing).</summary>
    public static DiscordSong? From(PhoneMusicTrack? track, DateTimeOffset now)
    {
        if (track is null || !track.Playing)
            return null;
        var title = Cut(PhoneMusicPayload.Clean(track.Title));
        if (title.Length < 2)
            title = title.Length == 0 ? "" : title + " "; // Discord wants at least 2 characters
        if (title.Trim().Length == 0)
            return null;
        var artist = Cut(PhoneMusicPayload.Clean(track.Artist));
        var album = Cut(PhoneMusicPayload.Clean(track.Album));
        var position = PhoneMusicPayload.Seconds(track.Position);
        var duration = PhoneMusicPayload.Seconds(track.Duration);
        long? start = null, end = null;
        if (duration > 0)
        {
            start = now.ToUnixTimeMilliseconds() - (long)(Math.Min(position, duration) * 1000);
            end = start + (long)(duration * 1000);
        }
        return new DiscordSong(title, artist.Length > 0 ? artist : null, album.Length > 0 ? album : null, start, end);
    }

    private static string Cut(string s)
    {
        if (s.Length <= MaxText)
            return s;
        var cut = s[..MaxText];
        if (char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1];
        return cut.TrimEnd();
    }
}

/// <summary>
/// When to send the song to Discord: a different song (or nothing) at once, the same song only after a seek of more
/// than 5 s, and never more often than every 4 s (Discord allows 5 status updates per 20 s). Not thread-safe.
/// </summary>
public sealed class DiscordSongGate
{
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(4);
    public const long SeekMs = 5000;

    private DiscordSong? _last;
    private bool _sentAny;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    /// <summary>True when <paramref name="song"/> should be sent now (and remembers it as sent).</summary>
    public bool ShouldSend(DiscordSong? song, DateTimeOffset now)
    {
        if (_sentAny && Same(song, _last))
            return false;
        if (_sentAny && now - _lastAt < MinGap)
            return false;
        _last = song;
        _sentAny = true;
        _lastAt = now;
        return true;
    }

    /// <summary>Forget what was sent (signed out, or Discord was reconnected): the next song goes out at once.</summary>
    public void Reset()
    {
        _last = null;
        _sentAny = false;
        _lastAt = DateTimeOffset.MinValue;
    }

    private static bool Same(DiscordSong? a, DiscordSong? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Title != b.Title || a.Artist != b.Artist || a.Album != b.Album || (a.EndMs is null) != (b.EndMs is null))
            return false;
        return a.StartMs is null || Math.Abs(a.StartMs.Value - b.StartMs!.Value) <= SeekMs;
    }
}
