using System.Text.Json.Nodes;
using RigControl.Link;

namespace RigControl.Link.Tests;

public class MusicActionTests
{
    private static LinkAction? Check(string action, string json, out string? error) =>
        LinkActions.Check(action, JsonNode.Parse(json), out error);

    [Fact]
    public void Phone_music_is_cleaned_and_kept()
    {
        var a = Check("presence.phoneMusic",
            "{\"title\":\"  Song  \",\"artist\":\"Band\",\"album\":\"LP\",\"playing\":true,\"position\":12.34,\"duration\":200}", out var e);
        Assert.Null(e);
        Assert.Equal("Song", a!.Args["title"]!.GetValue<string>());
        Assert.Equal(12.3, a.Args["position"]!.GetValue<double>());
        Assert.True(a.Args["playing"]!.GetValue<bool>());
    }

    [Fact]
    public void Nothing_playing_is_an_empty_object()
    {
        var a = Check("presence.phoneMusic", "{}", out var e);
        Assert.Null(e);
        Assert.Empty(a!.Args);
    }

    [Theory]
    [InlineData("{\"title\":\"\",\"playing\":true}")]                       // empty title
    [InlineData("{\"title\":\"x\",\"playing\":\"yes\"}")]                    // not a bool
    [InlineData("{\"title\":\"x\",\"playing\":true,\"position\":-1}")]       // negative time
    [InlineData("{\"title\":\"x\",\"playing\":true,\"duration\":90000}")]    // over a day
    [InlineData("{\"title\":\"x\",\"playing\":true,\"url\":\"https://x\"}")] // unknown field (no links)
    public void Bad_phone_music_is_refused(string json)
    {
        Assert.Null(Check("presence.phoneMusic", json, out var e));
        Assert.NotNull(e);
    }

    [Theory]
    [InlineData("ab")]   // control character
    [InlineData("a‎b")]   // invisible format character
    public void Hidden_characters_in_music_text_are_refused(string title)
    {
        var json = new JsonObject { ["title"] = title, ["playing"] = true }.ToJsonString();
        Assert.Null(Check("presence.phoneMusic", json, out var e));
        Assert.NotNull(e);
    }

    [Fact]
    public void Overlong_music_text_is_refused()
    {
        var json = new JsonObject { ["title"] = new string('a', LinkActions.MaxMusicText + 1), ["playing"] = true }.ToJsonString();
        Assert.Null(Check("presence.phoneMusic", json, out _));
    }

    [Fact]
    public void Spotify_connect_takes_only_a_token_shaped_value()
    {
        Assert.NotNull(Check("presence.spotifyConnect", "{\"refreshToken\":\"AQC_abc-DEF.ghi~1234567890\"}", out _));
        Assert.Null(Check("presence.spotifyConnect", "{\"refreshToken\":\"short\"}", out _));
        Assert.Null(Check("presence.spotifyConnect", "{\"refreshToken\":\"has space in it 1234567890\"}", out _));
        Assert.Null(Check("presence.spotifyConnect", "{\"refreshToken\":\"AQC_abc-DEF.ghi~1234567890\",\"clientId\":\"x\"}", out _));
        Assert.NotNull(Check("presence.spotifyDisconnect", "{}", out _));
        Assert.Null(Check("presence.spotifyDisconnect", "{\"x\":1}", out _));
    }
}
