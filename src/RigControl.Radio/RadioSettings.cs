namespace RigControl.Radio;

/// <summary>Settings section "radio". Off until switched on.</summary>
public sealed class RadioSettings
{
    public const string Section = "radio";

    public bool Enabled { get; set; }

    /// <summary>null = automatic: the capture card's sound input.</summary>
    public string? InputId { get; set; }

    /// <summary>Also recognise the station songs by their lyrics (downloads lyrics and a speech model). Off by default.</summary>
    public bool Lyrics { get; set; }
}

/// <summary>A song the detector shows now. Position and duration only when known (not for songs named by hand).</summary>
public sealed record RadioSongDto(int Id, string Title, string Artist, string Album, double? PositionSeconds, double? DurationSeconds);

/// <summary>State: "off", "listening" or "waiting" (switched on, but no capture card input; tried again every 3 s).</summary>
public sealed record RadioStatusDto(bool Enabled, string State, string? Problem, RadioSongDto? Now, bool Sound, int LearnedCount, RadioLyricsDto Lyrics);

/// <summary>Lyrics recognition: switched on, its state ("off", "preparing", "ready", "problem"), what it says, the last words it heard.</summary>
public sealed record RadioLyricsDto(bool Enabled, string State, string? Detail, string? LastHeard);

public sealed record LearnedSongDto(int Id, string Title, string Artist, string Album, double? DurationSeconds, DateTimeOffset LearnedAt);
