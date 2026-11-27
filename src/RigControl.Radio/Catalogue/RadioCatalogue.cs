using RigControl.Radio.Lyrics;

namespace RigControl.Radio.Catalogue;

/// <summary>A song the in-game radio can play. <see cref="Key"/> identifies it (normalised title and artist).</summary>
public sealed record CatalogueSong(string Station, string Title, string Artist)
{
    public string Key { get; } = string.Join(' ', LyricsText.Words(Title)) + "|" + string.Join(' ', LyricsText.Words(Artist));
}

/// <summary>The songs on the game radio stations RigControl knows, built into the app (names only, no lyrics).</summary>
public static class RadioCatalogue
{
    private const string Resource = "RigControl.Radio.Catalogue.gta6-radio.tsv";

    public static IReadOnlyList<CatalogueSong> Load()
    {
        using var stream = typeof(RadioCatalogue).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The radio song list is missing from this build.");
        using var reader = new StreamReader(stream);
        var songs = new List<CatalogueSong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length != 3 || parts[1].Trim().Length == 0 || parts[2].Trim().Length == 0) continue;
            var song = new CatalogueSong(parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
            if (seen.Add(song.Key)) songs.Add(song);
        }
        return songs;
    }
}
