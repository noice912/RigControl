using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network.Providers;

/// <summary>A made-up LeetDesk Aura at 172.16.0.60: a renamed WLED that answers under /jsonaura only.</summary>
internal sealed class FakeAura
{
    public const string Ip = "172.16.0.60";
    public const string Mac = "0a1b2c3d4e5f";

    public string Name { get; set; } = "aura";
    public string Leds { get; set; } = ",\"leds\":{\"count\":512}";
    public int UdpPort { get; set; } = 21324;
    public string StateJson { get; set; } = "{\"on\":false,\"bri\":77}";
    public int PostStatus { get; set; } = 200;

    private string Info => $"{{\"ver\":\"0.13.1\",\"name\":\"{Name}\",\"mac\":\"{Mac}\",\"ip\":\"{Ip}\",\"arch\":\"esp32\",\"udpport\":{UdpPort}{Leds}}}";

    public FakeAura(FakeLightNetwork net)
    {
        net.OnHttp = ask => (ask.Method, ask.Url) switch
        {
            ("GET", "http://172.16.0.60/jsonaura/info/") => FakeLightNetwork.Json(Info),
            ("GET", "http://172.16.0.60/jsonaura/") => FakeLightNetwork.Json($"{{\"state\":{StateJson},\"info\":{Info}}}"),
            ("POST", "http://172.16.0.60/jsonaura/state/") => FakeLightNetwork.Status(PostStatus),
            _ => ask.Url.StartsWith("http://172.16.0.60/", StringComparison.Ordinal) ? FakeLightNetwork.Status(404) : null,
        };
    }
}

public class LeetDeskProviderTests
{
    private readonly FakeLightNetwork _net = new();
    private readonly LeetDeskProvider _provider = new();

    private static PairedLight Paired() =>
        new("leetdesk", FakeAura.Mac, "aura-3d4e5f", "Aura", FakeAura.Ip, "net|leetdesk|" + FakeAura.Mac, new Dictionary<string, string> { ["port"] = "80" });

    private static MdnsService Announced(string ip) => new("aura", "aura.local.", ip, 80, new Dictionary<string, string>());

    private static PairRequest Typed(string ip) => new(ip, null, new Dictionary<string, string>());

    [Fact]
    public void It_says_it_is_untested_and_where_the_protocol_comes_from()
    {
        Assert.Equal(("leetdesk", "LeetDesk Aura"), (_provider.Brand, _provider.DisplayName));
        Assert.False(_provider.Tested);
        Assert.Contains("Not yet tried on a real desk", _provider.Notes);
        Assert.Contains("third-party add-on last changed in 2024", _provider.Notes);
        Assert.Contains("may behave differently", _provider.Notes);
        Assert.False(_provider.Pairing.ReturnsSecret);
        Assert.Contains("LeetDesk suggests", _provider.Pairing.NothingFound);
        Assert.Contains(NetworkLights.BuiltIn(), p => p.Brand == "leetdesk");
    }

    [Fact]
    public async Task A_desk_is_found_by_its_own_service_name_and_its_own_info_page()
    {
        _ = new FakeAura(_net);
        _net.Mdns["_aura._tcp.local."] = new() { Announced(FakeAura.Ip) };
        _net.Mdns["_wled._tcp.local."] = new() { Announced("172.16.0.61") }; // an ordinary WLED: not this brand's search

        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));

        Assert.Equal((FakeAura.Mac, FakeAura.Ip), (found.Id, found.Address));
        Assert.Equal("Aura 0.13.1 (esp32)", found.Model);
        Assert.Equal(new[] { "_aura._tcp.local." }, _net.Browsed);
        Assert.Equal("http://172.16.0.60/jsonaura/info/", Assert.Single(_net.Asks).Url);
    }

    [Fact]
    public async Task A_desk_still_called_aura_is_shown_with_the_end_of_its_id()
    {
        var desk = new FakeAura(_net);
        Assert.Equal("aura-3d4e5f", (await _provider.PairAsync(_net, Typed(FakeAura.Ip), default)).Name);

        desk.Name = "Desk upstairs";
        Assert.Equal("Desk upstairs", (await _provider.PairAsync(_net, Typed(FakeAura.Ip), default)).Name);

        desk.Name = "";
        Assert.Equal("LeetDesk Aura", (await _provider.PairAsync(_net, Typed(FakeAura.Ip), default)).Name);
    }

    [Fact]
    public async Task An_ordinary_WLED_that_answers_json_but_not_jsonaura_is_not_a_LeetDesk()
    {
        _ = new FakeWled(_net, "172.16.0.61");
        _net.Mdns["_aura._tcp.local."] = new() { Announced("172.16.0.61") };

        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Typed("172.16.0.61"), default));
        Assert.Equal("Something answered at 172.16.0.61, but it isn't a LeetDesk Aura. Check the address and try again.", ex.Message);
        Assert.All(_net.Asks, a => Assert.Equal("http://172.16.0.61/jsonaura/info/", a.Url));
    }

    [Fact]
    public async Task The_desk_is_one_strip_as_long_as_it_says_or_512_when_it_does_not_say()
    {
        var desk = new FakeAura(_net) { Leds = ",\"leds\":{\"count\":300}" };
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal((LightKind.Strip, 300), (d.Kind, d.Points.Count));
        Assert.Equal("The desk is one strip of 300 lights. Use Run backwards on the layout if it runs the wrong way.", d.Note);

        desk.Leds = "";
        Assert.Equal(512, (await _provider.DescribeAsync(_net, Paired(), default)).Points.Count);
    }

    [Fact]
    public async Task Starting_remembers_on_and_brightness_then_turns_the_desk_on_at_full()
    {
        _ = new FakeAura(_net);
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(512), default);

        Assert.Equal(new[] { "GET http://172.16.0.60/jsonaura/info/", "GET http://172.16.0.60/jsonaura/", "POST http://172.16.0.60/jsonaura/state/" },
            _net.Asks.Select(a => a.Method + " " + a.Url));
        Assert.Equal("{\"on\":true,\"bri\":255,\"live\":false}", _net.Asks.Last().Body);
        Assert.Empty(_net.Sent);
    }

    [Fact]
    public async Task A_512_light_frame_is_split_into_485_and_27_with_these_header_bytes()
    {
        _ = new FakeAura(_net) { UdpPort = 21999 };
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(512), default);
        var asked = _net.Asks.Count;

        await session.SendAsync(Enumerable.Repeat(0x0A0B0C, 512).ToArray(), default);

        var packets = _net.Sent.ToArray();
        Assert.Equal(2, packets.Length);
        Assert.All(packets, p => Assert.Equal((FakeAura.Ip, 21999), (p.Host, p.Port)));
        Assert.Equal(new byte[] { 0x04, 0x02, 0x00, 0x00 }, packets[0].Data[..4]);
        Assert.Equal(4 + 485 * 3, packets[0].Data.Length);
        Assert.Equal(new byte[] { 0x04, 0x02, 0x01, 0xE5 }, packets[1].Data[..4]); // 485
        Assert.Equal(4 + 27 * 3, packets[1].Data.Length);
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, packets[1].Data[^3..]);
        Assert.Equal(asked, _net.Asks.Count); // nothing is asked over the web while frames go out
        Assert.Equal(TimeSpan.FromSeconds(1), session.KeepAlive);
        Assert.Equal(30, _provider.MaxFramesPerSecond);
    }

    [Fact]
    public async Task Stopping_sends_one_dark_frame_and_then_puts_on_and_brightness_back()
    {
        _ = new FakeAura(_net);
        var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(512), default);
        await session.SendAsync(Enumerable.Repeat(0xFFFFFF, 512).ToArray(), default);
        var before = _net.Sent.Count;

        await session.StopAsync(default);
        await session.DisposeAsync();

        var dark = _net.Sent.Skip(before).ToArray();
        Assert.Equal(2, dark.Length);
        Assert.Equal(new byte[] { 4, 2, 0, 0 }, dark[0].Data[..4]);
        Assert.Equal(new byte[] { 4, 2, 0x01, 0xE5 }, dark[1].Data[..4]);
        Assert.All(dark, p => Assert.All(p.Data[4..], b => Assert.Equal(0, b)));
        var last = _net.Asks.Last();
        Assert.Equal(("POST", "http://172.16.0.60/jsonaura/state/"), (last.Method, last.Url));
        Assert.Equal("{\"on\":false,\"bri\":77,\"live\":false}", last.Body);
    }

    [Fact]
    public async Task A_desk_that_does_not_say_its_state_is_not_switched_and_nothing_is_put_back()
    {
        _ = new FakeAura(_net) { StateJson = "{}" };
        var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(10), default);
        await session.SendAsync(new int[10], default);
        await session.StopAsync(default);
        await session.DisposeAsync();

        Assert.DoesNotContain(_net.Asks, a => a.Method != "GET");
        Assert.Equal(2, _net.Sent.Count); // the frame, then the dark one
    }

    [Fact]
    public async Task The_update_page_and_the_plain_WLED_pages_are_never_touched()
    {
        _ = new FakeAura(_net);
        _net.Mdns["_aura._tcp.local."] = new() { Announced(FakeAura.Ip) };

        await _provider.DiscoverAsync(_net, default);
        await _provider.PairAsync(_net, Typed(FakeAura.Ip), default);
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        var session = await _provider.StartAsync(_net, Paired(), d, default);
        await session.SendAsync(new int[d.Points.Count], default);
        await session.StopAsync(default);
        await session.DisposeAsync();

        Assert.All(_net.Asks, a => Assert.StartsWith("http://172.16.0.60/jsonaura/", a.Url));
        Assert.DoesNotContain(_net.Asks, a => a.Url.Contains("update") || a.Url.Contains("cfg"));
        Assert.All(_net.Sent, p => Assert.Equal((byte)4, p.Data[0]));
    }
}
