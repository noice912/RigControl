using System.Text.Json.Nodes;
using RigControl.Presence;
using Xunit;

namespace RigControl.Presence.Tests;

public class ActivityTextTests
{
    [Fact]
    public void A_song_whose_title_and_album_run_past_128_characters_is_shortened_not_refused()
    {
        var album = "Starting From Zero (feat. Michael McDonald) [From GTAVI: Original Score]";
        var card = new JsonObject
        {
            ["details"] = new string('d', 200),
            ["state"] = "Justice",
            ["assets"] = new JsonObject { ["large_image"] = "https://example.com/a.jpg", ["large_text"] = "Starting From Zero (feat. Michael McDonald) - " + album },
            ["buttons"] = new JsonArray(new JsonObject { ["label"] = new string('b', 40), ["url"] = "https://example.com" }),
        };
        ActivityText.FitDiscordLimits(card);
        Assert.Equal(128, card["details"]!.GetValue<string>().Length);
        Assert.EndsWith("…", card["details"]!.GetValue<string>());
        Assert.True(card["assets"]!["large_text"]!.GetValue<string>().Length <= 128);
        Assert.Equal("Justice", card["state"]!.GetValue<string>());
        Assert.Equal(32, card["buttons"]![0]!["label"]!.GetValue<string>().Length);
        Assert.Equal("https://example.com/a.jpg", card["assets"]!["large_image"]!.GetValue<string>());
    }

    [Fact]
    public void An_emoji_is_never_cut_in_half()
    {
        var text = new string('a', 126) + "😀" + "zzz"; // the emoji takes characters 127 and 128
        var cut = ActivityText.Shorten(text, 128);
        Assert.True(cut.Length <= 128);
        Assert.False(char.IsHighSurrogate(cut[^2]) && !char.IsLowSurrogate(cut[^1]));
        Assert.EndsWith("…", cut);
    }
}
