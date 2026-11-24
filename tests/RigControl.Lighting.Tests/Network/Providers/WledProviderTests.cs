using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network.Providers;

/// <summary>A made-up WLED at 172.16.0.50 that answers its two JSON pages the way a real one does.</summary>
internal sealed class FakeWled
{
    public const string Ip = "172.16.0.50";
    public const string Mac = "a1b2c3d4e5f6";

    public string Info { get; set; } = InfoJson(30);
    public string State { get; set; } = "{\"on\":true,\"bri\":128,\"lor\":0,\"seg\":[{\"id\":0,\"start\":0,\"stop\":30,\"len\":30}]}";
    public bool Answers { get; set; } = true;

    public static string InfoJson(int count, int udpPort = 21324, string extra = "", string leds = "") =>
        $"{{\"ver\":\"0.14.4\",\"name\":\"Shelf\",\"mac\":\"{Mac}\",\"arch\":\"esp32\",\"udpport\":{udpPort},\"live\":false,"
        + $"\"leds\":{{\"count\":{count},\"lc\":1{leds}}}{extra}}}";

    public FakeWled(FakeLightNetwork net, string ip = Ip)
    {
        net.OnHttp = ask =>
        {
            if (!Answers) return null;
            if (ask.Url == $"http://{ip}/json/info") return FakeLightNetwork.Json(Info);
            if (ask.Url == $"http://{ip}/json/state") return FakeLightNetwork.Json(State);
            return ask.Url.StartsWith($"http://{ip}/", StringComparison.Ordinal) ? FakeLightNetwork.Status(404) : null;
        };
    }
}

public class WledProviderTests
{
    private readonly FakeLightNetwork _net = new();
    private readonly WledProvider _provider = new();

    private static PairedLight Paired() =>
        new("wled", FakeWled.Mac, "Shelf", "WLED", FakeWled.Ip, "net|wled|" + FakeWled.Mac, new Dictionary<string, string> { ["port"] = "80" });

    private static MdnsService Announced(string ip, int port = 80) =>
        new("wled-shelf", "wled-shelf.local.", ip, port, new Dictionary<string, string> { ["mac"] = FakeWled.Mac });

    private static PairRequest Typed(string ip) => new(ip, null, new Dictionary<string, string>());

    [Fact]
    public void It_says_it_is_untested_and_needs_no_pairing_key()
    {
        Assert.Equal(("wled", "WLED"), (_provider.Brand, _provider.DisplayName));
        Assert.False(_provider.Tested);
        Assert.Contains("not yet tried on a real device", _provider.Notes);
        Assert.False(_provider.Pairing.ReturnsSecret);
        Assert.True(_provider.Pairing.ManualAddress);
        Assert.Equal(30, _provider.MaxFramesPerSecond);
        Assert.Contains(NetworkLights.BuiltIn(), p => p.Brand == "wled");
    }

    [Fact]
    public async Task A_WLED_that_announces_itself_is_found_with_its_mac_as_its_id()
    {
        _ = new FakeWled(_net);
        _net.Mdns["_wled._tcp.local."] = new() { Announced(FakeWled.Ip) };

        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));

        Assert.Equal((FakeWled.Mac, "Shelf", FakeWled.Ip), (found.Id, found.Name, found.Address));
        Assert.Equal("WLED 0.14.4 (esp32)", found.Model);
        Assert.Equal("80", found.Data["port"]);
        Assert.Equal(new[] { "_wled._tcp.local." }, _net.Browsed);
        Assert.Equal("http://172.16.0.50/json/info", Assert.Single(_net.Asks).Url);
    }

    [Fact]
    public async Task Something_else_that_answers_the_search_is_not_listed()
    {
        _net.Mdns["_wled._tcp.local."] = new()
        {
            Announced("172.16.0.51"), // answers JSON, but not a WLED's
            Announced("172.16.0.52"), // a WLED-like answer without "leds"
            Announced("172.16.0.53"), // answers nothing
            Announced("172.16.0.54"), // not JSON at all
            Announced("203.0.113.9"), // not on the user's own network: never asked
        };
        _net.OnHttp = ask => ask.Url switch
        {
            "http://172.16.0.51/json/info" => FakeLightNetwork.Json("{\"model\":\"printer\"}"),
            "http://172.16.0.52/json/info" => FakeLightNetwork.Json($"{{\"ver\":\"0.14.4\",\"mac\":\"{FakeWled.Mac}\"}}"),
            "http://172.16.0.54/json/info" => FakeLightNetwork.Json("<html>hello</html>"),
            _ => null,
        };

        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.DoesNotContain(_net.Asks, a => a.Url.Contains("203.0.113.9"));
    }

    [Fact]
    public async Task A_typed_address_is_checked_by_reading_its_info()
    {
        _ = new FakeWled(_net);

        var r = await _provider.PairAsync(_net, Typed(FakeWled.Ip), default);

        Assert.Equal((FakeWled.Mac, "Shelf", FakeWled.Ip), (r.Id, r.Name, r.Address));
        Assert.Empty(r.Secrets);
        Assert.Equal("80", r.Data["port"]);
        var ask = Assert.Single(_net.Asks);
        Assert.Equal(("GET", "http://172.16.0.50/json/info"), (ask.Method, ask.Url));
    }

    [Fact]
    public async Task A_mac_written_with_colons_gives_the_same_id_and_a_nameless_WLED_is_called_WLED()
    {
        _ = new FakeWled(_net) { Info = "{\"ver\":\"0.13.0\",\"mac\":\"A1:B2:C3:D4:E5:F6\",\"leds\":{\"count\":8}}" };
        var r = await _provider.PairAsync(_net, Typed(FakeWled.Ip), default);
        Assert.Equal((FakeWled.Mac, "WLED", "WLED 0.13.0"), (r.Id, r.Name, r.Model));
    }

    [Fact]
    public async Task A_typed_address_that_is_not_a_WLED_is_refused_in_plain_words()
    {
        _net.OnHttp = _ => FakeLightNetwork.Json("{\"hello\":\"world\"}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Typed("172.16.0.51"), default));
        Assert.Equal("Something answered at 172.16.0.51, but it isn't a WLED. Check the address and try again.", ex.Message);

        _net.OnHttp = _ => FakeLightNetwork.Status(404);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Typed("172.16.0.51"), default));
    }

    [Fact]
    public async Task A_port_named_in_an_announcement_is_ignored_a_WLED_is_always_asked_on_its_web_port()
    {
        // An announcement can name any port; a search must not be steered to one.
        _net.Mdns["_wled._tcp.local."] = new() { Announced(FakeWled.Ip, 8500) };
        _net.OnHttp = ask => ask.Url == "http://172.16.0.50/json/info" ? FakeLightNetwork.Json(FakeWled.InfoJson(30)) : null;

        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));
        var r = await _provider.PairAsync(_net, new PairRequest(FakeWled.Ip, found, new Dictionary<string, string>()), default);

        Assert.Equal("80", r.Data["port"]);
        Assert.All(_net.Asks, a => Assert.Equal("http://172.16.0.50/json/info", a.Url));
    }

    [Fact]
    public async Task A_WLED_announced_at_an_address_on_a_network_that_was_not_searched_is_never_asked()
    {
        // A VPN adapter: private addresses, but no router, so it is not a home network.
        _net.Networks.Add(new LocalNetwork("10.0.0.2", 24, false));
        _net.Mdns["_wled._tcp.local."] = new()
        {
            Announced("10.0.0.1"),      // on the VPN
            Announced("192.168.1.100"),  // private, but on no network of this PC
        };
        _net.OnHttp = _ => FakeLightNetwork.Json(FakeWled.InfoJson(30));

        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Empty(_net.Asks);
    }

    [Fact]
    public async Task A_search_asks_at_most_64_announced_addresses()
    {
        _net.Mdns["_wled._tcp.local."] = Enumerable.Range(1, 200).Select(i => Announced("172.16.0." + i)).ToList();
        _net.OnHttp = _ => null;
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Equal(64, _net.Asks.Count);
    }

    [Fact]
    public async Task A_strip_is_described_as_one_point_per_LED()
    {
        _ = new FakeWled(_net);
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal(LightKind.Strip, d.Kind);
        Assert.Equal(30, d.Points.Count);
        Assert.Null(d.Note);
    }

    [Fact]
    public async Task A_matrix_gives_points_in_x_plus_y_times_width_order()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(8, leds: ",\"matrix\":{\"w\":4,\"h\":2}") };

        var d = await _provider.DescribeAsync(_net, Paired(), default);

        Assert.Equal(LightKind.Points, d.Kind);
        Assert.Equal(Enumerable.Range(0, 8), d.Points.Select(p => p.Id));
        Assert.Equal((0.125, 0.25), (d.Points[0].X, d.Points[0].Y));
        Assert.Equal((0.875, 0.25), (d.Points[3].X, d.Points[3].Y));
        Assert.Equal((0.375, 0.75), (d.Points[5].X, d.Points[5].Y)); // index 5 = x 1, y 1
        Assert.Equal(0.25, d.PointSize);
    }

    [Fact]
    public async Task A_matrix_that_does_not_add_up_is_treated_as_a_strip()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(10, leds: ",\"matrix\":{\"w\":4,\"h\":2}") };
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal((LightKind.Strip, 10), (d.Kind, d.Points.Count));
    }

    [Fact]
    public async Task With_main_segment_only_the_frame_is_as_long_as_that_segment()
    {
        _ = new FakeWled(_net)
        {
            Info = FakeWled.InfoJson(100, extra: ",\"liveseg\":1"),
            State = "{\"lor\":0,\"seg\":[{\"id\":0,\"start\":0,\"stop\":40},{\"id\":1,\"start\":40,\"stop\":100}]}",
        };
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal((LightKind.Strip, 60), (d.Kind, d.Points.Count));
    }

    [Fact]
    public async Task Without_main_segment_only_the_whole_strip_is_used()
    {
        _ = new FakeWled(_net)
        {
            Info = FakeWled.InfoJson(100, extra: ",\"liveseg\":-1"),
            State = "{\"lor\":0,\"seg\":[{\"id\":0,\"start\":0,\"stop\":40}]}",
        };
        Assert.Equal(100, (await _provider.DescribeAsync(_net, Paired(), default)).Points.Count);
    }

    [Fact]
    public async Task Live_override_being_on_is_told_to_the_user()
    {
        _ = new FakeWled(_net) { State = "{\"on\":true,\"lor\":1}" };
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal("Live override is on in WLED, so it ignores colours sent to it. Turn it off in WLED.", d.Note);
    }

    [Fact]
    public async Task A_strip_that_claims_to_be_enormous_is_capped()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(1_000_000) };
        Assert.Equal(4096, (await _provider.DescribeAsync(_net, Paired(), default)).Points.Count);
    }

    [Fact]
    public async Task A_paired_address_that_is_no_longer_a_WLED_says_so()
    {
        _net.OnHttp = _ => FakeLightNetwork.Json("{\"name\":\"a printer\"}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(), default));
        Assert.Contains("no longer answers like a WLED", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.StartAsync(_net, Paired(), LightDescription.Strip(3), default));
        Assert.Empty(_net.Sent);
    }

    [Fact]
    public async Task A_WLED_that_does_not_answer_can_not_be_started()
    {
        _ = new FakeWled(_net) { Answers = false };
        await Assert.ThrowsAnyAsync<Exception>(() => _provider.StartAsync(_net, Paired(), LightDescription.Strip(3), default));
        Assert.Empty(_net.Sent);
    }

    [Fact]
    public async Task A_three_LED_frame_is_one_DNRGB_packet_with_these_exact_bytes()
    {
        _ = new FakeWled(_net);
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(3), default);

        await session.SendAsync(new[] { 0xFF0000, 0x00FF00, 0x102030 }, default);

        var sent = Assert.Single(_net.Sent);
        Assert.Equal((FakeWled.Ip, 21324), (sent.Host, sent.Port));
        Assert.Equal(new byte[] { 4, 2, 0, 0, 255, 0, 0, 0, 255, 0, 0x10, 0x20, 0x30 }, sent.Data);
        Assert.Equal(TimeSpan.FromSeconds(1), session.KeepAlive);
    }

    [Fact]
    public async Task The_realtime_port_the_WLED_reports_is_the_one_used()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(3, udpPort: 30000) };
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(3), default);
        await session.SendAsync(new[] { 1, 2, 3 }, default);
        Assert.Equal(30000, Assert.Single(_net.Sent).Port);
    }

    [Fact]
    public async Task A_600_LED_strip_is_split_into_489_and_111_with_start_numbers_0_and_489()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(600) };
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(600), default);
        var frame = Enumerable.Range(0, 600).Select(i => i).ToArray(); // LED i is the colour 0x00HHLL of its own number

        await session.SendAsync(frame, default);

        var packets = _net.Sent.ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(4 + 489 * 3, packets[0].Data.Length);
        Assert.Equal(new byte[] { 4, 2, 0, 0 }, packets[0].Data[..4]);
        Assert.Equal(4 + 111 * 3, packets[1].Data.Length);
        Assert.Equal(new byte[] { 4, 2, 0x01, 0xE9 }, packets[1].Data[..4]); // 489
        Assert.Equal(new byte[] { 0, 0x01, 0xE9 }, packets[1].Data[4..7]);   // LED 489's own colour comes first
        Assert.Equal(new byte[] { 0, 0x02, 0x57 }, packets[1].Data[^3..]);   // LED 599 last
        Assert.All(packets, p => Assert.True(p.Data.Length <= 1472));
    }

    [Fact]
    public async Task Colours_beyond_the_described_strip_are_not_sent()
    {
        _ = new FakeWled(_net);
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(2), default);
        await session.SendAsync(new[] { 1, 2, 3, 4 }, default);
        Assert.Equal(4 + 2 * 3, Assert.Single(_net.Sent).Data.Length);
    }

    [Fact]
    public async Task Stopping_hands_back_at_once_with_one_timeout_zero_packet_and_frees_the_port()
    {
        _ = new FakeWled(_net);
        var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(3), default);
        await session.SendAsync(new[] { 1, 2, 3 }, default);

        await session.StopAsync(default);
        await session.DisposeAsync();

        var last = _net.Sent.Last();
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0 }, last.Data);
        Assert.Equal((FakeWled.Ip, 21324), (last.Host, last.Port));
        Assert.Equal(2, _net.Sent.Count);
    }

    [Fact]
    public async Task No_packet_ever_starts_like_a_sync_notifier_or_TPM2_and_none_has_the_sticking_timeout()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(600) };
        var session = await _provider.StartAsync(_net, Paired(), LightDescription.Strip(600), default);
        await session.SendAsync(new int[600], default);
        await session.StopAsync(default);
        await session.DisposeAsync();

        Assert.All(_net.Sent, p => Assert.Contains(p.Data[0], new byte[] { 2, 4 }));
        Assert.All(_net.Sent, p => Assert.NotEqual(255, p.Data[1]));
        Assert.All(_net.Sent, p => Assert.Equal(FakeWled.Ip, p.Host));
    }

    [Fact]
    public async Task Only_the_info_and_state_pages_are_ever_read_and_nothing_is_asked_while_frames_go_out()
    {
        _ = new FakeWled(_net) { Info = FakeWled.InfoJson(30, extra: ",\"liveseg\":0") };
        _net.Mdns["_wled._tcp.local."] = new() { Announced(FakeWled.Ip) };

        await _provider.DiscoverAsync(_net, default);
        await _provider.PairAsync(_net, Typed(FakeWled.Ip), default);
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        var session = await _provider.StartAsync(_net, Paired(), d, default);
        var before = _net.Asks.Count;
        for (var i = 0; i < 20; i++) await session.SendAsync(Enumerable.Repeat(i, d.Points.Count).ToArray(), default);
        await session.StopAsync(default);
        await session.DisposeAsync();

        Assert.Equal(before, _net.Asks.Count);
        Assert.All(_net.Asks, a => Assert.Equal("GET", a.Method));
        Assert.All(_net.Asks, a => Assert.Contains(a.Url, new[] { "http://172.16.0.50/json/info", "http://172.16.0.50/json/state" }));
        Assert.All(_net.Asks, a => Assert.Null(a.Body));
        Assert.DoesNotContain(_net.Asks, a => a.Url.Contains("cfg") || a.Url.Contains("pin"));
    }
}
