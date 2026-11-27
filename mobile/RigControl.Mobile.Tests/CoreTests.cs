using RigControl.Mobile.Core.DeepLinks;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Core.Text;

namespace RigControl.Mobile.Tests;

public class PairingCodeTests
{
    [Theory]
    [InlineData("ABCD-EFGH-JKMN-PQRS-TVWX", "ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("abcd efgh jkmn pqrs tvwx", "ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("0O1I-L234-5678-9ABC-DEFG", "0011123456789ABCDEFG")]
    public void Reads_codes_the_forgiving_Crockford_way(string typed, string expected)
    {
        Assert.True(PairingCode.TryParse(typed, out var code));
        Assert.Equal(expected, code.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD-EFGH-JKMN-PQRS-TVW")]     // 19
    [InlineData("ABCD-EFGH-JKMN-PQRS-TVWU")]    // U is not in Crockford base32
    [InlineData("ABCD-EFGH-JKMN-PQRS-TVW!")]
    public void Refuses_wrong_codes(string typed) => Assert.False(PairingCode.TryParse(typed, out _));

    [Fact]
    public void Groups_by_four()
    {
        Assert.True(PairingCode.TryParse("abcdefghjkmnpqrstvwx", out var code));
        Assert.Equal("ABCD-EFGH-JKMN-PQRS-TVWX", code.ToString());
        Assert.Equal("ABCD-EF", PairingCode.Group("ABCDEF"));
    }
}

public class RelayHostTests
{
    [Theory]
    [InlineData("rigcontrol-relay.example.workers.dev", "rigcontrol-relay.example.workers.dev")]
    [InlineData("  Relay.Example.COM ", "relay.example.com")]
    [InlineData("localhost:8787", "localhost:8787")]
    public void Accepts_host_names(string text, string expected)
    {
        Assert.True(RelayHost.TryNormalize(text, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("wss://relay.example.com")]
    [InlineData("relay.example.com/v1")]
    [InlineData("user@relay.example.com")]
    [InlineData("relay..example.com")]
    [InlineData("-relay.example.com")]
    [InlineData("relay.example.com:0")]
    [InlineData("relay.example.com:99999")]
    [InlineData("relay.example.com?x=1")]
    [InlineData("")]
    public void Refuses_anything_else(string text) => Assert.False(RelayHost.TryNormalize(text, out _));

    [Fact]
    public void Another_relay_than_the_built_in_one_is_pointed_out()
    {
        Assert.Null(MobileDefaults.OtherRelayNote(MobileDefaults.RelayHost));
        Assert.Null(MobileDefaults.OtherRelayNote(MobileDefaults.RelayHost.ToUpperInvariant()));
        Assert.Null(MobileDefaults.OtherRelayNote(MobileDefaults.RelayHost + ":443"));
        Assert.Null(MobileDefaults.OtherRelayNote("relay.invalid")); // the placeholder has its own note
        Assert.Null(MobileDefaults.OtherRelayNote("not a host"));
        Assert.Contains("relay.example.com", MobileDefaults.OtherRelayNote("Relay.Example.com"));
        Assert.Contains("relay.example.com:8443", MobileDefaults.OtherRelayNote("relay.example.com:8443"));
    }
}

public class DeepLinkTests
{
    [Fact]
    public void Reads_a_pairing_QR_code()
    {
        var link = DeepLink.Parse("rigcontrol://pair?relay=relay.example.com&code=ABCD-EFGH-JKMN-PQRS-TVWX");
        var pair = Assert.IsType<DeepLink.Pair>(link);
        Assert.Equal("relay.example.com", pair.Relay);
        Assert.Equal("ABCDEFGHJKMNPQRSTVWX", pair.Code.Value);
    }

    [Theory]
    [InlineData("rigcontrol://pair?relay=relay.example.com")]
    [InlineData("rigcontrol://pair?code=ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("rigcontrol://pair?relay=https://evil.example&code=ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("rigcontrol://pair?relay=a.example&relay=b.example&code=ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("rigcontrol://pair/extra?relay=relay.example.com&code=ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("https://pair?relay=relay.example.com&code=ABCDEFGHJKMNPQRSTVWX")]
    [InlineData("rigcontrol://unknown")]
    [InlineData("not a link")]
    public void Refuses_bad_links(string text) => Assert.Null(DeepLink.Parse(text));

    [Fact]
    public void Keeps_a_sign_in_callback_raw()
    {
        var link = Assert.IsType<DeepLink.Callback>(DeepLink.Parse("rigcontrol://callback?code=abc%20d&state=xyz"));
        Assert.Equal("abc d", link.Query["code"]);
        Assert.Equal("xyz", link.Query["state"]);
    }
}

public class PcActionTests
{
    [Fact]
    public void Only_the_D1_actions_exist()
    {
        // The phone's list is exactly the PC's (RigControl.Link LinkActions).
        Assert.Equal(global::RigControl.Link.LinkActions.Allowed.Order(StringComparer.Ordinal), PcAction.Allowed.Order(StringComparer.Ordinal));
        Assert.Equal(10, PcAction.Allowed.Count);
        Assert.Equal("lighting.setEffect", PcAction.SetEffect("wave").Name);
        Assert.Equal("wave", PcAction.SetEffect("wave").Args["effect"]);
        Assert.Throws<ArgumentException>(() => PcAction.SetEffect(" "));
    }
}

public class FakeLinkServiceTests
{
    private static FakeLinkService Make() => new(withSamples: true, delay: TimeSpan.Zero);

    [Fact]
    public async Task Has_one_online_and_one_offline_sample()
    {
        var pcs = await Make().GetPcsAsync();
        Assert.Equal(2, pcs.Count);
        Assert.Single(pcs, p => p.Online);
    }

    [Fact]
    public async Task Recording_start_and_stop_change_the_snapshot_and_raise_events()
    {
        var link = Make();
        var events = new List<LinkEvent>();
        link.EventReceived += (_, e) => events.Add(e);

        Assert.True((await link.DoAsync("sample-desk-pc", PcAction.StartRecording())).Ok);
        Assert.True((await link.GetSnapshotAsync("sample-desk-pc")).Recording.IsRecording);
        Assert.False((await link.DoAsync("sample-desk-pc", PcAction.StartRecording())).Ok);
        Assert.True((await link.DoAsync("sample-desk-pc", PcAction.StopRecording())).Ok);
        Assert.False((await link.GetSnapshotAsync("sample-desk-pc")).Recording.IsRecording);
        Assert.Contains(events, e => e is PcNotice { Text: "Recording saved" });
    }

    [Fact]
    public async Task Offline_PC_refuses_actions()
    {
        var result = await Make().DoAsync("sample-living-room", PcAction.LightsOn());
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Pairing_adds_a_PC_and_the_zero_code_fails()
    {
        var link = new FakeLinkService(withSamples: false, delay: TimeSpan.Zero);
        Assert.True(PairingCode.TryParse("00000000000000000000", out var zero));
        Assert.False((await link.PairAsync(new PairingRequest(zero, "relay.example.com", "Phone"))).Ok);

        Assert.True(PairingCode.TryParse("ABCDEFGHJKMNPQRSTVWX", out var code));
        var result = await link.PairAsync(new PairingRequest(code, "relay.example.com", "Phone"));
        Assert.True(result.Ok);
        Assert.Single(await link.GetPcsAsync());
        Assert.False((await link.PairAsync(new PairingRequest(code, "relay.example.com", "Phone"))).Ok);
    }

    [Fact]
    public async Task Unknown_effect_is_refused()
    {
        Assert.False((await Make().DoAsync("sample-desk-pc", PcAction.SetEffect("nope"))).Ok);
    }
}

public class LastSeenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Describes_times()
    {
        Assert.Equal("Online", LastSeen.Describe(true, null, Now));
        Assert.Equal("Not seen yet", LastSeen.Describe(false, null, Now));
        Assert.Equal("Seen 5 min ago", LastSeen.Describe(false, Now.AddMinutes(-5), Now));
        Assert.Equal("Seen 3 h ago", LastSeen.Describe(false, Now.AddHours(-3), Now));
        Assert.Equal("Seen yesterday", LastSeen.Describe(false, Now.AddHours(-30), Now));
        Assert.Equal("Seen 4 days ago", LastSeen.Describe(false, Now.AddDays(-4), Now));
    }
}
