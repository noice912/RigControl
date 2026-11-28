using RigControl.Radio.Catalogue;
using RigControl.Radio.Lyrics;

namespace RigControl.Radio.Tests;

public class CatalogueTests
{
    [Fact]
    public void Loads_the_embedded_station_lists()
    {
        var songs = RadioCatalogue.Load();
        Assert.InRange(songs.Count, 780, 840);
        Assert.All(songs, s => Assert.False(string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Artist)));
        Assert.Contains(songs, s => s.Title == "Electric Feel" && s.Artist == "MGMT");
        Assert.Contains(songs, s => s.Title == "In the Air Tonight" && s.Station == "Emotion 98.3");
    }

    [Fact]
    public void The_same_song_on_two_stations_is_listed_once()
    {
        var songs = RadioCatalogue.Load();
        Assert.Single(songs, s => s.Title == "Skrilla" && s.Artist == "Kodak Black");
        Assert.Equal(songs.Count, songs.Select(s => s.Key).Distinct().Count());
    }
}

public class LyricsTextTests
{
    [Fact]
    public void Normalises_to_plain_words()
    {
        Assert.Equal(new[] { "dont", "stop", "me", "now", "cafe" }, LyricsText.Words("Don't STOP me... now! Café"));
        Assert.Empty(LyricsText.Words("♪ ♪ (instrumental)").Where(w => w != "instrumental"));
    }

    [Fact]
    public void Reads_timed_lines()
    {
        var lines = LyricsText.ParseSynced("[00:12.34] First line\n[01:02.5]Second line\nnot a line\n[xx:yy] bad\n[00:20.00]");
        Assert.Equal(2, lines.Count);
        Assert.Equal(12.34, lines[0].Seconds, 2);
        Assert.Equal("Second line", lines[1].Text);
        Assert.Equal(62.5, lines[1].Seconds, 2);
    }
}

public class LyricsIndexTests
{
    private static LyricsIndex Index()
    {
        // A realistic library: 200 other songs around the four these tests are about.
        var i = FakeCorpus.Build(200, 11);
        i.Add("a", [new(10, "the copper kettle hums along the quiet harbour"), new(14, "seven paper lanterns drifting past the mill"),
                    new(18, "we painted every fencepost blue before the summer rain came")], 230);
        i.Add("b", [new(5, "this is the way that we roll down by the river"), new(9, "and the night keeps calling my name")], 200);
        i.Add("c", [new(3, "i love you baby i love you"), new(7, "oh oh oh oh"), new(11, "the city lights are burning slow tonight"),
                    new(15, "while the old guitars keep humming down the empty street")], 180);
        i.Add("d", [new(3, "i love you baby i love you"), new(7, "you know i love you"), new(11, "underneath the purple paper moon")], 190);
        return i;
    }

    [Fact]
    public void Finds_the_song_and_its_place_from_misheard_words()
    {
        // Two lines heard, with a couple of words wrong, as speech recognition gets them over game sound.
        var m = Index().Match("seven paper lanterns drifting past the meal we painted every fence post blue before the summer rain came");
        Assert.NotNull(m);
        Assert.Equal("a", m!.SongKey);
        Assert.InRange(m.PositionSeconds!.Value, 14, 26);
    }

    [Fact]
    public void Talking_that_isnt_a_song_finds_nothing()
    {
        Assert.Null(Index().Match("you're listening to vice city fm the hottest hits all day long stay tuned"));
        Assert.Null(Index().Match(""));
    }

    [Fact]
    public void A_phrase_many_songs_share_is_not_enough()
    {
        Assert.Null(Index().Match("i love you baby i love you"));
    }

    [Fact]
    public void A_rare_line_beats_a_common_one()
    {
        var m = Index().Match("i love you baby i love you the city lights are burning slow tonight while the old guitars keep humming down the empty street");
        Assert.Equal("c", m!.SongKey);
    }
}
