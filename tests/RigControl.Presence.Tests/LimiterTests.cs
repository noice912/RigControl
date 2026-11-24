using System.Net.Http;
using System.Text.Json.Nodes;
using RigControl.Core.Logging;
using RigControl.Presence;

namespace RigControl.Presence.Tests;

/// <summary>Discord takes 5 card updates per 20 seconds on one connection. A pretend clock, no waiting.</summary>
public class LimiterTests
{
    private DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>What reached Discord: the card's name ("" = taken down) and when.</summary>
    private readonly List<(string Name, DateTime At)> _sent = new();
    private string? _answer;

    private ActivityLimiter Limiter() => new(() => _now, a =>
    {
        _sent.Add((a?["name"]?.GetValue<string>() ?? "", _now));
        return _answer;
    });

    private static JsonObject Card(string name) => new() { ["type"] = 0, ["name"] = name };

    private static readonly TimeSpan Full = ActivityLimiter.Window + ActivityLimiter.Margin;

    private string[] Names => _sent.Select(s => s.Name).ToArray();

    [Fact]
    public void Five_changes_in_a_row_all_go_out_at_once()
    {
        var l = Limiter();
        for (var i = 1; i <= 5; i++) Assert.Equal(ActivityLimiter.Result.Sent, l.Offer(Card("c" + i)));
        Assert.Equal(new[] { "c1", "c2", "c3", "c4", "c5" }, Names);
        Assert.All(_sent, s => Assert.Equal(_now, s.At)); // not one of them waited
        Assert.False(l.Pending);
        Assert.Null(l.NextSlot);
    }

    [Fact]
    public void The_sixth_waits_and_the_seventh_takes_its_place()
    {
        var l = Limiter();
        var start = _now;
        for (var i = 1; i <= 5; i++) l.Offer(Card("c" + i));
        Assert.Equal(ActivityLimiter.Result.Waiting, l.Offer(Card("c6")));
        Assert.True(l.Pending);
        Assert.Equal(start + Full, l.NextSlot);
        _now = _now.AddSeconds(3);
        Assert.Equal(ActivityLimiter.Result.Waiting, l.Offer(Card("c7")));
        Assert.Equal(5, _sent.Count);

        // Not a moment early.
        _now = start + Full - TimeSpan.FromMilliseconds(1);
        Assert.False(l.Flush());
        Assert.Equal(5, _sent.Count);

        // The moment a place frees, the newest state goes out. The one it replaced is never sent.
        _now = start + Full;
        Assert.Equal(_now, l.NextSlot);
        Assert.True(l.Flush());
        Assert.Equal(new[] { "c1", "c2", "c3", "c4", "c5", "c7" }, Names);
        Assert.False(l.Pending);
        Assert.False(l.Flush()); // and only once
        Assert.Equal(6, _sent.Count);
    }

    [Fact]
    public void A_change_made_while_one_waits_goes_out_at_once_when_a_place_is_free_again()
    {
        var l = Limiter();
        for (var i = 1; i <= 5; i++) l.Offer(Card("c" + i));
        l.Offer(Card("c6"));
        _now += Full;
        // Nobody flushed yet; the next change itself finds the free place and is sent, not the stale one.
        Assert.Equal(ActivityLimiter.Result.Sent, l.Offer(Card("c7")));
        Assert.Equal("c7", _sent[^1].Name);
        Assert.DoesNotContain("c6", Names);
        Assert.False(l.Pending);
    }

    [Fact]
    public void Taking_a_card_down_is_never_lost()
    {
        var l = Limiter();
        for (var i = 1; i <= 5; i++) l.Offer(Card("c" + i));
        Assert.Equal(ActivityLimiter.Result.Waiting, l.Offer(Card("c6")));
        Assert.Equal(ActivityLimiter.Result.Waiting, l.Offer(null)); // the card is to come down, and no place is free
        Assert.True(l.Pending);
        _now += Full;
        Assert.True(l.Flush());
        Assert.Equal("", _sent[^1].Name); // it came down
        Assert.DoesNotContain("c6", Names);

        // Down already: asking again sends nothing.
        Assert.Equal(ActivityLimiter.Result.Same, l.Offer(null));
        Assert.Equal(6, _sent.Count);
    }

    [Fact]
    public void A_new_connection_shows_nothing_so_taking_the_card_down_sends_nothing()
    {
        var l = Limiter();
        Assert.Equal(ActivityLimiter.Result.Same, l.Offer(null));
        Assert.Empty(_sent);
    }

    [Fact]
    public void The_same_card_is_not_sent_twice_in_a_row()
    {
        var l = Limiter();
        Assert.Equal(ActivityLimiter.Result.Sent, l.Offer(Card("a")));
        Assert.Equal(ActivityLimiter.Result.Same, l.Offer(Card("a")));
        Assert.Equal(ActivityLimiter.Result.Same, l.Offer(Card("a")));
        Assert.Single(_sent);

        // Only a keep-alive repeats it, and it counts towards the limit like any update.
        Assert.Equal(ActivityLimiter.Result.Sent, l.Offer(Card("a"), keepAlive: true));
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void A_keep_alive_never_waits_and_never_takes_a_change_s_place()
    {
        var l = Limiter();
        for (var i = 1; i <= 5; i++) l.Offer(Card("c" + i));
        Assert.Equal(ActivityLimiter.Result.Skipped, l.Offer(Card("c5"), keepAlive: true));
        Assert.False(l.Pending);
        _now += Full;
        Assert.False(l.Flush());
        Assert.Equal(5, _sent.Count);
    }

    [Fact]
    public void Going_back_to_what_Discord_already_shows_forgets_what_was_waiting()
    {
        var l = Limiter();
        for (var i = 1; i <= 5; i++) l.Offer(Card("c" + i));
        l.Offer(Card("c6"));
        Assert.Equal(ActivityLimiter.Result.Same, l.Offer(Card("c5"))); // Discord shows c5 already
        Assert.False(l.Pending);
        _now += Full;
        Assert.False(l.Flush());
        Assert.Equal(5, _sent.Count);
    }

    [Fact]
    public void Discord_s_answer_to_the_last_update_is_kept()
    {
        var l = Limiter();
        _answer = "Invalid image";
        l.Offer(Card("a"));
        Assert.Equal("Invalid image", l.LastError);
        _answer = null;
        l.Offer(Card("b"));
        Assert.Null(l.LastError);
    }

    [Fact]
    public void A_new_connection_starts_counting_again()
    {
        var l = Limiter();
        for (var i = 1; i <= 5; i++) l.Offer(Card("c" + i));
        l.Offer(Card("c6"));
        l.Reset();
        Assert.False(l.Pending);
        Assert.Equal(ActivityLimiter.Result.Sent, l.Offer(Card("c6")));
    }

    [Fact]
    public void Closing_takes_the_card_down_only_when_a_place_is_free_and_keeps_nothing_waiting()
    {
        var l = Limiter();
        l.Offer(Card("a"));
        l.ClearBeforeClosing();
        Assert.Equal(new[] { "a", "" }, Names);

        var full = Limiter();
        _sent.Clear();
        for (var i = 1; i <= 5; i++) full.Offer(Card("c" + i));
        full.Offer(Card("c6"));
        full.ClearBeforeClosing(); // no place free: closing the connection takes the card down by itself
        Assert.Equal(5, _sent.Count);
        Assert.False(full.Pending);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(2026)]
    public void However_fast_the_changes_come_no_more_than_five_go_out_in_any_twenty_seconds_and_the_last_one_arrives(int seed)
    {
        var rnd = new Random(seed);
        var l = Limiter();
        var last = "";
        var n = 0;
        for (var step = 0; step < 4000; step++)
        {
            _now = _now.AddMilliseconds(rnd.Next(1, 900));
            l.Flush();
            var roll = rnd.Next(10);
            if (roll < 6)
            {
                last = "s" + ++n;
                l.Offer(Card(last));
            }
            else if (roll == 6)
            {
                last = "";
                l.Offer(null);
            }
            else if (roll == 7 && last != "") l.Offer(Card(last), keepAlive: true);
            // A quiet spell now and then, so the waiting state gets its turn.
            if (rnd.Next(40) == 0)
            {
                _now += Full;
                l.Flush();
                Assert.False(l.Pending);
                Assert.Equal(last, _sent[^1].Name); // the newest state is what Discord shows
            }
        }
        _now += Full;
        l.Flush();
        Assert.Equal(last, _sent[^1].Name);
        Assert.True(_sent.Count > 500); // it really was sending as fast as allowed

        // Any update and the 5th one after it are more than 20 seconds apart.
        for (var i = 0; i + ActivityLimiter.MaxSends < _sent.Count; i++)
            Assert.True(_sent[i + ActivityLimiter.MaxSends].At - _sent[i].At >= ActivityLimiter.Window,
                $"6 updates within 20 s at update {i}");
        // And it never sends the same card twice running without being asked to keep it alive (checked above by
        // The_same_card_is_not_sent_twice_in_a_row); here: a waiting state that was replaced never shows up late.
        var states = _sent.Where(s => s.Name.StartsWith('s')).Select(s => int.Parse(s.Name[1..])).ToList();
        Assert.Equal(states.OrderBy(x => x), states); // never an older state after a newer one
    }
}

/// <summary>The same rules seen from the engine: real cards, a pretend Discord and a pretend clock.</summary>
public class FastUpdateTests
{
    private readonly FakeDiscord _discord = new();
    private DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly PresenceSettings _s = new() { Enabled = true, ShowMusic = false, ShowSpotify = false, ShowCurrentApp = false, ShowWatching = false };

    public FastUpdateTests() => _discord.Clock = () => _now;

    private PresenceEngine Engine() => new(_discord, new FakeInputs(), NullLog.Instance, new HttpClient(new NoNetwork()), () => _now);

    private void Say(string text)
    {
        _s.CustomStatuses.Clear();
        _s.CustomStatuses.Add(new CustomStatus { Id = "a", Name = text, Enabled = true, Elapsed = false });
    }

    private string[] SentNames => _discord.Sent.Select(x => x.Activity is null ? "" : JsonNode.Parse(x.Activity)!["name"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task A_change_reaches_Discord_in_the_same_pass_and_a_burst_keeps_only_the_newest()
    {
        var e = Engine();
        var start = _now;
        for (var i = 1; i <= 7; i++)
        {
            Say("Step " + i);
            await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
            if (i <= 5) Assert.Equal("Step " + i, SentNames[^1]); // sent in that very pass
            _now = _now.AddSeconds(1);
        }
        Assert.Equal(5, _discord.Sent.Count);
        Assert.Equal(1, e.Now.CustomShown);

        // The engine says exactly how long the newest state has to wait.
        var wait = e.NextSendIn;
        Assert.NotNull(wait);
        Assert.Equal(start + ActivityLimiter.Window + ActivityLimiter.Margin, _now + wait!.Value);
        e.Flush();
        Assert.Equal(5, _discord.Sent.Count); // not early

        _now += wait.Value;
        Assert.Equal(TimeSpan.Zero, e.NextSendIn);
        e.Flush();
        Assert.Equal(new[] { "Step 1", "Step 2", "Step 3", "Step 4", "Step 5", "Step 7" }, SentNames);
        Assert.Null(e.NextSendIn);
        Assert.Contains("Step 7", _discord.Cards[TestIds.Pool[0]]);
    }

    [Fact]
    public async Task An_unchanged_card_is_not_sent_again_pass_after_pass()
    {
        var e = Engine();
        Say("Studying");
        for (var i = 0; i < 10; i++)
        {
            await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
            _now = _now.AddSeconds(3);
        }
        Assert.Single(_discord.Sent);
    }

    [Fact]
    public async Task No_card_gets_more_than_five_updates_in_twenty_seconds_and_the_last_text_always_arrives()
    {
        var e = Engine();
        for (var i = 1; i <= 200; i++)
        {
            Say("Step " + i);
            await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
            _now = _now.AddMilliseconds(700);
        }
        // Quiet now: whatever still waits goes out when it may.
        while (e.NextSendIn is { } wait)
        {
            _now += wait;
            e.Flush();
        }
        Assert.Equal("Step 200", SentNames[^1]);
        var times = _discord.Sent.Select(x => x.At).ToList();
        Assert.True(times.Count > 30);
        for (var i = 0; i + 5 < times.Count; i++) Assert.True(times[i + 5] - times[i] >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Each_card_has_its_own_five_places()
    {
        var e = Engine();
        for (var i = 1; i <= 5; i++)
        {
            _s.CustomStatuses.Clear();
            _s.CustomStatuses.Add(new CustomStatus { Id = "a", Name = "A" + i, Enabled = true, Elapsed = false });
            _s.CustomStatuses.Add(new CustomStatus { Id = "b", Name = "B" + i, Enabled = true, Elapsed = false });
            await e.TickAsync(_s, Array.Empty<Game>(), CancellationToken.None);
            _now = _now.AddSeconds(1);
        }
        Assert.Equal(10, _discord.Sent.Count); // 5 each, none held back by the other card
        Assert.Equal(5, _discord.Sent.Count(x => x.ClientId == TestIds.Pool[0]));
        Assert.Equal(5, _discord.Sent.Count(x => x.ClientId == TestIds.Pool[1]));
    }

    [Fact]
    public async Task A_card_that_has_to_come_down_while_the_limit_is_reached_still_comes_down()
    {
        // The app card keeps its connection while a film takes its place, so its "take down" waits like any update.
        var inputs = new FakeInputs();
        var s = new PresenceSettings { Enabled = true, ShowMusic = false, ShowSpotify = false, ShowCurrentApp = true };
        var e = new PresenceEngine(_discord, inputs, NullLog.Instance, new HttpClient(new NoNetwork()), () => _now);
        inputs.App = new ForegroundApp("notepad", "Notepad", "one", null);
        await e.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
        _now = _now.AddSeconds(4);
        for (var i = 0; i < 6; i++)
        {
            inputs.App = inputs.App! with { Title = "file " + i };
            await e.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
            _now = _now.AddMilliseconds(200);
        }
        Assert.Equal(5, _discord.Sent.Count);
        Assert.Contains("file 3", _discord.Cards[TestIds.App]); // "file 5" is waiting

        inputs.Sessions.Add(new MediaSession("msedge", true, "Stranger Things", "www.netflix.com", 3000, 60));
        await e.TickAsync(s, Array.Empty<Game>(), CancellationToken.None);
        Assert.True(_discord.Cards.ContainsKey(TestIds.Netflix)); // its own card, its own places: up at once
        Assert.True(_discord.Cards.ContainsKey(TestIds.App));     // no place free yet for the app card
        while (e.NextSendIn is { } wait)
        {
            _now += wait;
            e.Flush();
        }
        Assert.False(_discord.Cards.ContainsKey(TestIds.App));
        var app = _discord.Sent.Where(x => x.ClientId == TestIds.App).ToList();
        Assert.Equal(6, app.Count);
        Assert.Null(app[^1].Activity);                              // it came down
        Assert.DoesNotContain(app, x => x.Activity?.Contains("file 5") == true); // and the stale title was never sent
        Assert.True(_discord.Cards.ContainsKey(TestIds.Netflix));
    }
}
