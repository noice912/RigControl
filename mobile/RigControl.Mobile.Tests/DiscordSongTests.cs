using RigControl.Mobile.Core.Music;

namespace RigControl.Mobile.Tests;

public class DiscordSongTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Fact]
    public void A_playing_song_gets_start_and_end_from_its_place()
    {
        var s = DiscordSong.From(new PhoneMusicTrack("Made Up Song", "Some Band", "An Album", true, 30, 200), Now)!;
        Assert.Equal("Made Up Song", s.Title);
        Assert.Equal("Some Band", s.Artist);
        Assert.Equal(Now.ToUnixTimeMilliseconds() - 30_000, s.StartMs);
        Assert.Equal(s.StartMs + 200_000, s.EndMs);
    }

    [Fact]
    public void Paused_or_untitled_is_nothing()
    {
        Assert.Null(DiscordSong.From(new PhoneMusicTrack("Made Up Song", "Band", null, false, 3, 200), Now));
        Assert.Null(DiscordSong.From(new PhoneMusicTrack("  ", "Band", null, true, 3, 200), Now));
        Assert.Null(DiscordSong.From(null, Now));
    }

    [Fact]
    public void Unknown_length_has_no_times_and_long_text_is_cut()
    {
        var s = DiscordSong.From(new PhoneMusicTrack(new string('a', 300), "", null, true, 3, 0), Now)!;
        Assert.Null(s.StartMs);
        Assert.Null(s.EndMs);
        Assert.Equal(DiscordSong.MaxText, s.Title.Length);
        Assert.Null(s.Artist);
    }

    [Fact]
    public void The_gate_sends_changes_and_seeks_but_not_repeats_or_bursts()
    {
        var gate = new DiscordSongGate();
        var a = DiscordSong.From(new PhoneMusicTrack("Song A", "Band", null, true, 0, 200), Now);
        Assert.True(gate.ShouldSend(a, Now));
        // Same song a second later (its start time is the same): nothing.
        var a2 = DiscordSong.From(new PhoneMusicTrack("Song A", "Band", null, true, 10, 200), Now.AddSeconds(10));
        Assert.False(gate.ShouldSend(a2, Now.AddSeconds(10)));
        // Another song 1 s after the first send: too soon, then sent once 4 s have passed.
        var b = DiscordSong.From(new PhoneMusicTrack("Song B", "Band", null, true, 0, 180), Now.AddSeconds(1));
        Assert.False(gate.ShouldSend(b, Now.AddSeconds(1)));
        Assert.True(gate.ShouldSend(b, Now.AddSeconds(4)));
        // A seek of 60 s in the same song is sent.
        var seek = DiscordSong.From(new PhoneMusicTrack("Song B", "Band", null, true, 70, 180), Now.AddSeconds(10));
        Assert.True(gate.ShouldSend(seek, Now.AddSeconds(10)));
        // Nothing playing is sent once.
        Assert.True(gate.ShouldSend(null, Now.AddSeconds(20)));
        Assert.False(gate.ShouldSend(null, Now.AddSeconds(30)));
        gate.Reset();
        Assert.True(gate.ShouldSend(null, Now.AddSeconds(31)));
    }
}
