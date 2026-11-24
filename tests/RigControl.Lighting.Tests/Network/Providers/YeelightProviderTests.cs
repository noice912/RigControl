using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network.Providers;

/// <summary>A made-up Yeelight at 172.16.0.80 that answers the way the specification says a real one does.</summary>
internal sealed class FakeYeelight
{
    public const string Ip = "172.16.0.80";
    public const string Id = "0x000000000015243f";
    public const string ColourSupport = "get_prop set_default set_power toggle set_bright start_cf stop_cf set_scene cron_add cron_get cron_del set_ct_abx set_rgb set_hsv set_adjust set_music set_name";
    public const string WhiteSupport = "get_prop set_default set_power toggle set_bright start_cf stop_cf set_scene cron_add cron_get cron_del set_ct_abx set_adjust set_name";
    public const string BackgroundSupport = ColourSupport + " bg_set_rgb bg_set_hsv bg_set_ct_abx bg_start_cf bg_stop_cf bg_set_scene bg_set_default bg_set_power bg_set_bright bg_set_adjust bg_toggle";

    private readonly FakeLightNetwork _net;

    public bool LanControl { get; set; } = true;
    public bool AnswersSearch { get; set; } = true;
    public bool CallsBack { get; set; } = true;
    /// <summary>Where the "music mode" connection comes from; a real light calls from its own address.</summary>
    public string CallbackFrom { get; set; } = Ip;
    /// <summary>The light sends some news of its own before every answer.</summary>
    public bool Chatty { get; set; }
    public string Support { get; set; } = ColourSupport;
    public string Model { get; set; } = "color";
    public string Name { get; set; } = "Desk lamp";
    public int Replies { get; set; } = 1;
    /// <summary>How many of the next "put it back" commands the light refuses with "client quota exceeded".</summary>
    public int QuotaRefusals { get; set; }

    public Dictionary<string, string> Props { get; } = new()
    {
        ["power"] = "on", ["bright"] = "80", ["color_mode"] = "1", ["ct"] = "4000", ["rgb"] = "255", ["hue"] = "240", ["sat"] = "100",
        ["flowing"] = "0", ["flow_params"] = "",
        ["bg_power"] = "on", ["bg_bright"] = "40", ["bg_lmode"] = "1", ["bg_rgb"] = "65280", ["bg_ct"] = "3000", ["bg_hue"] = "120", ["bg_sat"] = "100",
    };

    /// <summary>Normal connections, in the order they were made.</summary>
    public ConcurrentQueue<FakeTcpLink> Links { get; } = new();
    /// <summary>The light's ends of the connections it made back to the PC, in order.</summary>
    public ConcurrentQueue<FakeTcpLink> Music { get; } = new();
    public ConcurrentQueue<(string Host, int Port)> AskedToCall { get; } = new();

    public FakeYeelight(FakeLightNetwork net)
    {
        _net = net;
        net.OnTcp = (host, port) =>
        {
            if (!LanControl || host != Ip || port != YeelightProvider.ControlPort) return null;
            var link = new FakeTcpLink(host) { OnWrite = Answer };
            Links.Enqueue(link);
            return link;
        };
        net.OnUdp = sent =>
            AnswersSearch && sent.Port == YeelightProvider.SearchPort
                ? Enumerable.Repeat(new UdpDatagram(Ip, YeelightProvider.SearchPort, Encoding.ASCII.GetBytes(SearchReply())), Replies)
                : null;
    }

    public string SearchReply() =>
        "HTTP/1.1 200 OK\r\nCache-Control: max-age=3600\r\nDate: \r\nExt: \r\n" +
        $"Location: yeelight://{Ip}:55443\r\nServer: POSIX UPnP/1.0 YGLC/1\r\nid: {Id}\r\nmodel: {Model}\r\nfw_ver: 18\r\n" +
        $"support: {Support}\r\npower: on\r\nbright: 100\r\ncolor_mode: 2\r\nct: 4000\r\nrgb: 16711680\r\nhue: 100\r\nsat: 35\r\nname: {Name}\r\n";

    /// <summary>Everything written to the light on any connection, normal or music.</summary>
    public string Everything => string.Concat(Links.Select(l => l.WrittenText)) + string.Concat(Music.Select(l => l.WrittenText));
    public IEnumerable<string> ControlLines => Lines(string.Concat(Links.Select(l => l.WrittenText)));
    public IEnumerable<string> MusicLines => Lines(string.Concat(Music.Select(l => l.WrittenText)));

    public static IEnumerable<string> Lines(string text) => text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private string? Answer(string written)
    {
        var reply = new StringBuilder();
        foreach (var line in Lines(written))
        {
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("id").GetInt32();
            var method = doc.RootElement.GetProperty("method").GetString();
            var p = doc.RootElement.GetProperty("params");
            if (Chatty) reply.Append("{\"method\":\"props\",\"params\":{\"power\":\"on\"}}\r\n");
            if (method == "get_prop")
            {
                var values = p.EnumerateArray().Select(n => Props.GetValueOrDefault(n.GetString()!, n.GetString() == "name" ? Name : ""));
                reply.Append($"{{\"id\":{id},\"result\":{JsonSerializer.Serialize(values)}}}\r\n");
                continue;
            }
            if (method == "set_music" && p[0].GetInt32() == 1)
            {
                AskedToCall.Enqueue((p[1].GetString()!, p[2].GetInt32()));
                if (CallsBack && _net.Listeners.LastOrDefault(l => l.Port == p[2].GetInt32()) is { } listener)
                    Music.Enqueue(listener.Connect(CallbackFrom));
            }
            if (QuotaRefusals > 0 && method is "set_scene" or "bg_set_scene" or "set_power" or "bg_set_power" && QuotaRefusals > 0)
            {
                QuotaRefusals--;
                reply.Append($"{{\"id\":{id},\"error\":{{\"code\":-1,\"message\":\"client quota exceeded\"}}}}\r\n");
                continue;
            }
            reply.Append($"{{\"id\":{id},\"result\":[\"ok\"]}}\r\n");
        }
        return reply.ToString();
    }
}

public class YeelightProviderTests
{
    private readonly FakeLightNetwork _net = new();
    private readonly YeelightProvider _provider = new()
    {
        ListenTime = TimeSpan.FromMilliseconds(150),
        CallbackTime = TimeSpan.FromMilliseconds(300),
        ReplyTime = TimeSpan.FromMilliseconds(500),
    };

    private static readonly Dictionary<string, string> NoOptions = new();

    private static PairedLight Paired(string support = FakeYeelight.ColourSupport) =>
        new("yeelight", FakeYeelight.Id, "Desk lamp", "color", FakeYeelight.Ip, "net|yeelight|" + FakeYeelight.Id,
            new Dictionary<string, string> { ["port"] = "55443", ["support"] = support, ["model"] = "color" });

    private async Task<ILightSession> Started(PairedLight? light = null)
    {
        light ??= Paired();
        return await _provider.StartAsync(_net, light, await _provider.DescribeAsync(_net, light, default), default);
    }

    private static List<JsonElement> Parsed(IEnumerable<string> lines) => lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static List<JsonElement> Of(IEnumerable<string> lines, string method) =>
        Parsed(lines).Where(c => c.GetProperty("method").GetString() == method).ToList();

    // ---------------------------------------------------------------- what the page shows

    [Fact]
    public void It_says_it_is_untested_built_from_the_specification_and_needs_no_key()
    {
        Assert.Equal("yeelight", _provider.Brand);
        Assert.Equal("Yeelight", _provider.DisplayName);
        Assert.False(_provider.Tested);
        Assert.Contains("Yeelight's published specification", _provider.Notes);
        Assert.Contains("not yet tried on a real light", _provider.Notes);
        Assert.Contains("Mi Home", _provider.Notes);
        Assert.Contains("Anyone on your network", _provider.Notes);
        Assert.False(_provider.Pairing.ReturnsSecret);
        Assert.True(_provider.Pairing.ManualAddress);
        Assert.Contains(_provider.Pairing.Steps, s => s.Contains("LAN Control") && s.Contains("Developer mode"));
        Assert.Equal(15, _provider.MaxFramesPerSecond);
        Assert.True(new YeelightProvider().ListenTime < ((INetworkLightProvider)_provider).SearchTime);
    }

    // ---------------------------------------------------------------- searching

    [Fact]
    public async Task The_search_question_is_exactly_the_one_in_the_specification_and_goes_to_port_1982()
    {
        _ = new FakeYeelight(_net);
        await _provider.DiscoverAsync(_net, default);
        var sent = Assert.Single(_net.Sent);
        Assert.Equal("239.255.255.250", sent.Host);
        Assert.Equal(1982, sent.Port);
        Assert.Equal(
            Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n"),
            sent.Data);
    }

    [Fact]
    public async Task Only_home_networks_are_searched_one_question_each()
    {
        _net.Networks.Add(new LocalNetwork("172.16.5.10", 24, true));
        _net.Networks.Add(new LocalNetwork("172.20.0.2", 24, false)); // a VPN adapter: no router, never searched
        _ = new FakeYeelight(_net) { AnswersSearch = false };
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Equal(2, _net.Sent.Count);
        Assert.Empty(_net.TcpTried);
    }

    [Fact]
    public async Task An_answer_gives_the_lights_id_name_model_address_port_and_what_it_can_do()
    {
        _ = new FakeYeelight(_net);
        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));
        Assert.Equal(FakeYeelight.Id, found.Id);
        Assert.Equal("Desk lamp", found.Name);
        Assert.Equal("color", found.Model);
        Assert.Equal(FakeYeelight.Ip, found.Address);
        Assert.Equal("55443", found.Data["port"]);
        Assert.Equal(FakeYeelight.ColourSupport, found.Data["support"]);
    }

    [Fact]
    public async Task A_light_that_answers_twice_is_listed_once()
    {
        _ = new FakeYeelight(_net) { Replies = 3 };
        Assert.Single(await _provider.DiscoverAsync(_net, default));
    }

    [Fact]
    public async Task A_light_without_a_name_is_called_by_its_model()
    {
        _ = new FakeYeelight(_net) { Name = "", Model = "stripe" };
        Assert.Equal("Yeelight stripe", Assert.Single(await _provider.DiscoverAsync(_net, default)).Name);
    }

    [Fact]
    public void Adverts_and_answers_that_name_another_address_are_ignored()
    {
        var light = new FakeYeelight(_net);
        var advert = light.SearchReply().Replace("HTTP/1.1 200 OK", "NOTIFY * HTTP/1.1");
        Assert.Null(YeelightProvider.ParseReply(new UdpDatagram(FakeYeelight.Ip, 1982, Encoding.ASCII.GetBytes(advert))));
        Assert.Null(YeelightProvider.ParseReply(new UdpDatagram("172.16.0.99", 1982, Encoding.ASCII.GetBytes(light.SearchReply()))));
        Assert.Null(YeelightProvider.ParseReply(new UdpDatagram(FakeYeelight.Ip, 1982, new byte[] { 1, 2, 3 })));
        // Header names in any case, and another port.
        var odd = light.SearchReply().Replace("Location:", "LOCATION:").Replace(":55443", ":55444").Replace("id:", "ID:");
        var found = YeelightProvider.ParseReply(new UdpDatagram(FakeYeelight.Ip, 1982, Encoding.ASCII.GetBytes(odd)));
        Assert.Equal("55444", found!.Data["port"]);
        Assert.Equal(FakeYeelight.Id, found.Id);
    }

    // ---------------------------------------------------------------- pairing

    [Fact]
    public async Task A_typed_address_with_LAN_Control_off_says_what_to_switch_on()
    {
        _ = new FakeYeelight(_net) { LanControl = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.PairAsync(_net, new PairRequest(FakeYeelight.Ip, null, NoOptions), default));
        Assert.Equal("No Yeelight answered at that address. Check LAN Control is switched on for it in the Yeelight app.", ex.Message);
    }

    [Fact]
    public async Task A_typed_address_is_checked_and_the_light_is_asked_directly_which_one_it_is()
    {
        var light = new FakeYeelight(_net);
        var r = await _provider.PairAsync(_net, new PairRequest(FakeYeelight.Ip, null, NoOptions), default);
        Assert.Equal(FakeYeelight.Id, r.Id);
        Assert.Equal("Desk lamp", r.Name);
        Assert.Equal("color", r.Model);
        Assert.Equal(FakeYeelight.Ip, r.Address);
        Assert.Empty(r.Secrets);
        Assert.Equal("55443", r.Data["port"]);
        Assert.Equal(FakeYeelight.ColourSupport, r.Data["support"]);
        // The question went to that one address only, never to the whole network.
        var sent = Assert.Single(_net.Sent);
        Assert.Equal((FakeYeelight.Ip, 1982), (sent.Host, sent.Port));
        Assert.Equal("{\"id\":1,\"method\":\"get_prop\",\"params\":[\"name\"]}\r\n", Assert.Single(light.Links).WrittenText);
    }

    [Fact]
    public async Task A_typed_address_that_does_not_say_which_light_it_is_asks_for_a_search_instead()
    {
        _ = new FakeYeelight(_net) { AnswersSearch = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.PairAsync(_net, new PairRequest(FakeYeelight.Ip, null, NoOptions), default));
        Assert.Contains("Press Search for lights", ex.Message);
    }

    [Fact]
    public async Task A_found_light_is_paired_without_another_search_question()
    {
        _ = new FakeYeelight(_net);
        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));
        var r = await _provider.PairAsync(_net, new PairRequest(found.Address, found, NoOptions), default);
        Assert.Equal(FakeYeelight.Id, r.Id);
        Assert.Single(_net.Sent); // only the search itself
    }

    [Fact]
    public async Task A_white_only_light_is_listed_but_pairing_says_it_has_no_colour()
    {
        _ = new FakeYeelight(_net) { Support = FakeYeelight.WhiteSupport, Model = "mono" };
        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.PairAsync(_net, new PairRequest(found.Address, found, NoOptions), default));
        Assert.Equal("This Yeelight is white-only, so it can't follow colours.", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(FakeYeelight.WhiteSupport), default));
    }

    // ---------------------------------------------------------------- describing

    [Fact]
    public async Task A_plain_bulb_is_one_light_and_one_with_a_background_light_is_two()
    {
        _ = new FakeYeelight(_net);
        var one = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal(LightKind.Single, one.Kind);
        Assert.Single(one.Points);

        var two = await _provider.DescribeAsync(_net, Paired(FakeYeelight.BackgroundSupport), default);
        Assert.Equal(LightKind.Strip, two.Kind);
        Assert.Equal(2, two.Points.Count);
        Assert.Contains("background light", two.Note);
    }

    [Fact]
    public async Task Describing_a_light_that_is_gone_fails_so_it_is_asked_again_later()
    {
        _ = new FakeYeelight(_net) { LanControl = false };
        await Assert.ThrowsAsync<IOException>(() => _provider.DescribeAsync(_net, Paired(), default));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public async Task Every_command_is_one_JSON_object_and_a_line_end_with_rising_ids()
    {
        var light = new FakeYeelight(_net);
        await using var session = await Started();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        await session.StopAsync(default);

        Assert.All(light.Links.Concat(light.Music), l => Assert.All(l.Written, bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            Assert.EndsWith("\r\n", text);
            var body = text[..^2];
            Assert.DoesNotContain("\n", body);
            var command = JsonDocument.Parse(body).RootElement;
            Assert.Equal(JsonValueKind.Number, command.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, command.GetProperty("method").ValueKind);
            Assert.Equal(JsonValueKind.Array, command.GetProperty("params").ValueKind);
        }));
        foreach (var link in light.Links.Skip(1).Concat(light.Music))
        {
            var ids = Parsed(FakeYeelight.Lines(link.WrittenText)).Select(c => c.GetProperty("id").GetInt32()).ToList();
            Assert.True(ids.Count > 0);
            Assert.Equal(ids.OrderBy(i => i).Distinct().ToList(), ids);
        }
    }

    [Fact]
    public async Task News_the_light_sends_by_itself_is_skipped_when_waiting_for_an_answer()
    {
        var light = new FakeYeelight(_net) { Chatty = true };
        light.Props["power"] = "off";
        await using var session = await Started();
        await session.StopAsync(default);
        // The state read at the start was understood despite the extra lines: the light was off, so it is switched off again.
        var power = Assert.Single(Of(light.ControlLines, "set_power"));
        Assert.Equal("[\"off\",\"smooth\",300]", power.GetProperty("params").GetRawText());
    }

    [Fact]
    public async Task An_answer_that_arrives_in_pieces_is_put_together()
    {
        _net.OnTcp = (host, _) =>
        {
            var link = new FakeTcpLink(host);
            link.OnWrite = _ =>
            {
                link.Say("{\"method\":\"props\",\"params\":{\"bright\":\"5\"}}\r\n{\"id\":1,\"res");
                link.Say("ult\":[\"Shelf\"]}\r");
                link.Say("\n");
                return null;
            };
            return link;
        };
        var found = new FoundLight(FakeYeelight.Id, "", "color", FakeYeelight.Ip)
        {
            Data = new Dictionary<string, string> { ["port"] = "55443", ["support"] = FakeYeelight.ColourSupport },
        };
        var r = await _provider.PairAsync(_net, new PairRequest(FakeYeelight.Ip, found, NoOptions), default);
        Assert.Equal("Shelf", r.Name);
    }

    // ---------------------------------------------------------------- music mode

    [Fact]
    public async Task The_light_is_asked_to_call_back_at_the_PCs_address_on_that_connection_and_the_port_being_listened_on()
    {
        var light = new FakeYeelight(_net);
        await using var session = await Started();
        var listener = Assert.Single(_net.Listeners);
        Assert.Equal("172.16.0.10", listener.LocalAddress);
        Assert.Equal(("172.16.0.10", listener.Port), Assert.Single(light.AskedToCall));
        Assert.True(listener.Disposed); // nobody else can connect afterwards
        Assert.Equal(15, session.MaxFramesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(60), session.KeepAlive);
    }

    [Fact]
    public async Task Frames_go_over_the_connection_the_light_made_and_no_answer_is_waited_for()
    {
        var light = new FakeYeelight(_net);
        await using var session = await Started();
        var music = Assert.Single(light.Music);
        var before = light.ControlLines.Count();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await session.SendAsync(new[] { 0x800000 }, default);
        await session.SendAsync(new[] { 0x0000FF }, default);
        Assert.True(watch.ElapsedMilliseconds < 400, "nothing answers in music mode, so nothing may be waited for");

        var frames = Of(light.MusicLines, "set_scene");
        Assert.Equal(2, frames.Count);
        // Half-bright red: the colour at full strength, brightness 50.
        Assert.Equal("[\"color\",16711680,50]", frames[0].GetProperty("params").GetRawText());
        Assert.Equal("[\"color\",255,100]", frames[1].GetProperty("params").GetRawText());
        Assert.Equal(before, light.ControlLines.Count());
        Assert.False(music.Disposed);
    }

    [Fact]
    public async Task A_call_back_from_another_address_is_turned_away()
    {
        var light = new FakeYeelight(_net) { CallbackFrom = "172.16.0.99" };
        await using var session = await Started();
        var stranger = Assert.Single(light.Music);
        Assert.True(stranger.Disposed);
        Assert.Equal(0.75, session.MaxFramesPerSecond);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        Assert.Empty(stranger.Written);
        Assert.Single(Of(light.ControlLines, "set_scene"));
    }

    [Fact]
    public async Task With_no_call_back_it_falls_back_to_45_commands_a_minute_on_the_normal_connection()
    {
        var light = new FakeYeelight(_net) { CallsBack = false };
        await using var session = await Started();
        // The light refuses more than 60 commands a minute; 45 leaves room for the start and for putting it back.
        Assert.Equal(0.75, session.MaxFramesPerSecond);
        Assert.True(session.MaxFramesPerSecond * 60 <= 45);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        Assert.Equal(2, Of(light.ControlLines, "set_scene").Count);
        // It is not asked to call back again for every frame.
        Assert.Single(light.AskedToCall);
        await session.StopAsync(default);
        Assert.DoesNotContain(Of(light.ControlLines, "set_music"), c => c.GetProperty("params")[0].GetInt32() == 0);
    }

    [Fact]
    public async Task A_light_that_does_not_list_music_mode_is_never_asked_for_it()
    {
        var light = new FakeYeelight(_net);
        await using var session = await Started(Paired(FakeYeelight.ColourSupport.Replace(" set_music", "")));
        Assert.Empty(light.AskedToCall);
        Assert.Empty(_net.Listeners);
        Assert.Equal(0.75, session.MaxFramesPerSecond);
    }

    [Fact]
    public async Task When_the_fast_connection_drops_it_is_asked_for_once_more_and_then_it_falls_back()
    {
        var light = new FakeYeelight(_net);
        await using var session = await Started();
        light.Music.First().HangUp();
        await Eventually.True(() => session.MaxFramesPerSecond == 0.75, "the drop to be noticed");

        await session.SendAsync(new[] { 0xFF0000 }, default);
        Assert.Equal(2, light.AskedToCall.Count);
        Assert.Equal(2, light.Music.Count);
        Assert.Equal(15, session.MaxFramesPerSecond);
        Assert.Single(Of(FakeYeelight.Lines(light.Music.Last().WrittenText), "set_scene"));

        light.Music.Last().HangUp();
        await Eventually.True(() => session.MaxFramesPerSecond == 0.75, "the second drop to be noticed");
        await session.SendAsync(new[] { 0x00FF00 }, default);
        Assert.Equal(2, light.AskedToCall.Count);
        Assert.Single(Of(light.ControlLines, "set_scene"));
    }

    [Fact]
    public async Task A_normal_connection_that_went_away_is_opened_again()
    {
        var light = new FakeYeelight(_net) { CallsBack = false };
        await using var session = await Started();
        var count = light.Links.Count;
        light.Links.Last().HangUp();
        await Task.Delay(100);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        Assert.Equal(count + 1, light.Links.Count);
        Assert.Single(Of(FakeYeelight.Lines(light.Links.Last().WrittenText), "set_scene"));
    }

    // ---------------------------------------------------------------- colours

    [Fact]
    public async Task Near_black_is_sent_as_the_dimmest_brightness_and_never_as_colour_0_or_off()
    {
        var light = new FakeYeelight(_net);
        await using var session = await Started();
        await session.SendAsync(new[] { 0x000000 }, default);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        await session.SendAsync(new[] { 0x000000 }, default);
        await session.SendAsync(new[] { 0x010000 }, default);

        var frames = Of(light.MusicLines, "set_scene").Select(f => f.GetProperty("params")).ToList();
        Assert.Equal(4, frames.Count);
        Assert.All(frames, p => Assert.True(p[1].GetInt32() > 0));
        Assert.All(frames, p => Assert.InRange(p[2].GetInt32(), 1, 100));
        Assert.Equal(1, frames[0][2].GetInt32());
        // Dark after green stays green, as dim as it goes.
        Assert.Equal("[\"color\",65280,1]", frames[2].GetRawText());
        Assert.Equal("[\"color\",16711680,1]", frames[3].GetRawText());
        Assert.Empty(Of(light.MusicLines, "set_power"));
    }

    [Fact]
    public async Task A_colour_the_light_already_shows_is_not_sent_again()
    {
        var light = new FakeYeelight(_net) { Support = FakeYeelight.BackgroundSupport };
        await using var session = await Started(Paired(FakeYeelight.BackgroundSupport));
        await session.SendAsync(new[] { 0xFF0000, 0x0000FF }, default);
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00 }, default);
        Assert.Single(Of(light.MusicLines, "set_scene"));
        Assert.Equal(2, Of(light.MusicLines, "bg_set_scene").Count);
    }

    // ---------------------------------------------------------------- putting it back

    [Theory]
    [InlineData("1", "[\"color\",255,80]")]
    [InlineData("2", "[\"ct\",4000,80]")]
    [InlineData("3", "[\"hsv\",240,100,80]")]
    public async Task Stopping_leaves_music_mode_and_puts_the_light_back_by_the_mode_it_was_in(string mode, string expected)
    {
        var light = new FakeYeelight(_net);
        light.Props["color_mode"] = mode;
        light.Props["flowing"] = "1"; // a running colour flow comes back as its plain colour
        await using var session = await Started();
        await session.SendAsync(new[] { 0x123456 }, default);
        await session.StopAsync(default);

        var read = Assert.Single(Of(light.ControlLines, "get_prop"), c => c.GetProperty("params").GetArrayLength() > 1);
        Assert.Equal("[\"power\",\"bright\",\"color_mode\",\"ct\",\"rgb\",\"hue\",\"sat\",\"flowing\",\"flow_params\"]", read.GetProperty("params").GetRawText());

        var control = Parsed(light.ControlLines).Select(c => c.GetProperty("method").GetString() + c.GetProperty("params").GetRawText()).ToList();
        var leave = control.IndexOf("set_music[0]");
        var restore = control.IndexOf("set_scene" + expected);
        Assert.True(leave >= 0, "music mode is left on a normal connection");
        Assert.True(restore > leave, "the old state is put back after music mode is left");
        Assert.True(light.Music.First().Disposed);
        Assert.Empty(Of(light.ControlLines, "set_power"));
    }

    [Fact]
    public async Task A_put_back_the_light_refuses_is_asked_once_more_on_a_fresh_connection()
    {
        // The light counts commands for each connection: a new connection has a fresh count.
        var light = new FakeYeelight(_net) { QuotaRefusals = 1 };
        await using var session = await Started();
        await session.SendAsync(new[] { 0x123456 }, default);
        await session.StopAsync(default);
        Assert.Equal(3, light.Links.Count); // one to describe it, one for the session, and the fresh one
        Assert.Equal(2, Of(light.ControlLines, "set_scene").Count);
        Assert.Single(Of(FakeYeelight.Lines(light.Links.Last().WrittenText), "set_scene"));
    }

    [Fact]
    public async Task A_put_back_the_light_keeps_refusing_is_reported_and_not_called_done()
    {
        var light = new FakeYeelight(_net) { QuotaRefusals = 10 };
        await using var session = await Started();
        await session.SendAsync(new[] { 0x123456 }, default);
        var ex = await Assert.ThrowsAsync<IOException>(() => session.StopAsync(default));
        Assert.Equal("the light refused: client quota exceeded", ex.Message);
        Assert.Equal(2, Of(light.ControlLines, "set_scene").Count); // asked twice, not for ever
    }

    [Fact]
    public async Task A_search_lists_at_most_64_lights()
    {
        _net.OnUdp = sent => sent.Port == YeelightProvider.SearchPort
            ? Enumerable.Range(0, 500).Select(i => new UdpDatagram("172.16.0.60", YeelightProvider.SearchPort, Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nLocation: yeelight://172.16.0.60:55443\r\nid: 0x{i:x8}\r\nmodel: color\r\nsupport: set_rgb\r\nname: \r\n")))
            : null;
        Assert.Equal(64, (await _provider.DiscoverAsync(_net, default)).Count);
    }

    [Fact]
    public async Task A_light_that_was_off_is_switched_off_again()
    {
        var light = new FakeYeelight(_net);
        light.Props["power"] = "off";
        await using var session = await Started();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.StopAsync(default);
        var power = Assert.Single(Of(light.ControlLines, "set_power"));
        Assert.Equal("[\"off\",\"smooth\",300]", power.GetProperty("params").GetRawText());
        Assert.Empty(Of(light.ControlLines, "set_scene"));
    }

    [Fact]
    public async Task A_light_with_a_background_light_gets_two_colours_and_both_are_put_back()
    {
        var light = new FakeYeelight(_net) { Support = FakeYeelight.BackgroundSupport, CallsBack = false };
        light.Props["bg_power"] = "off";
        var paired = Paired(FakeYeelight.BackgroundSupport);
        await using var session = await Started(paired);
        Assert.Equal(0.375, session.MaxFramesPerSecond); // two commands a frame, 45 commands a minute

        var read = Assert.Single(Of(light.ControlLines, "get_prop"), c => c.GetProperty("params").GetArrayLength() > 1);
        Assert.EndsWith("\"bg_power\",\"bg_bright\",\"bg_lmode\",\"bg_rgb\",\"bg_ct\",\"bg_hue\",\"bg_sat\"]", read.GetProperty("params").GetRawText());

        await session.SendAsync(new[] { 0xFF0000, 0x000080 }, default);
        Assert.Equal("[\"color\",16711680,100]", Assert.Single(Of(light.ControlLines, "set_scene")).GetProperty("params").GetRawText());
        Assert.Equal("[\"color\",255,50]", Assert.Single(Of(light.ControlLines, "bg_set_scene")).GetProperty("params").GetRawText());

        await session.StopAsync(default);
        Assert.Equal("[\"color\",255,80]", Of(light.ControlLines, "set_scene").Last().GetProperty("params").GetRawText());
        Assert.Equal("[\"off\",\"smooth\",300]", Assert.Single(Of(light.ControlLines, "bg_set_power")).GetProperty("params").GetRawText());
    }

    [Fact]
    public async Task A_ceiling_light_with_only_a_coloured_background_light_has_just_that_one_driven()
    {
        var support = FakeYeelight.BackgroundSupport.Replace(" set_rgb", "").Replace(" set_hsv", "");
        var light = new FakeYeelight(_net) { Support = support };
        var paired = Paired(support);
        var description = await _provider.DescribeAsync(_net, paired, default);
        Assert.Equal(LightKind.Single, description.Kind);
        Assert.Contains("main light is left alone", description.Note);

        await using var session = await _provider.StartAsync(_net, paired, description, default);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        await session.StopAsync(default);
        Assert.Single(Of(light.MusicLines, "bg_set_scene"));
        Assert.Empty(Of(light.MusicLines, "set_scene"));
        Assert.Empty(Of(light.ControlLines, "set_scene"));
        Assert.Empty(Of(light.ControlLines, "set_power"));
        Assert.Equal("[\"color\",65280,40]", Assert.Single(Of(light.ControlLines, "bg_set_scene")).GetProperty("params").GetRawText());
    }

    [Fact]
    public async Task Nothing_is_ever_written_to_the_lights_permanent_memory()
    {
        foreach (var support in new[] { FakeYeelight.ColourSupport, FakeYeelight.BackgroundSupport })
        foreach (var callsBack in new[] { true, false })
        {
            var net = new FakeLightNetwork();
            var light = new FakeYeelight(net) { Support = support, CallsBack = callsBack };
            var found = Assert.Single(await _provider.DiscoverAsync(net, default));
            var r = await _provider.PairAsync(net, new PairRequest(found.Address, found, NoOptions), default);
            var paired = new PairedLight("yeelight", r.Id, r.Name, r.Model, r.Address, "net|yeelight|" + r.Id, r.Data);
            var session = await _provider.StartAsync(net, paired, await _provider.DescribeAsync(net, paired, default), default);
            await session.SendAsync(new[] { 0xFF8800, 0x0088FF }, default);
            await session.SendAsync(new[] { 0x000000, 0x000000 }, default);
            await session.StopAsync(default);
            await session.DisposeAsync();

            Assert.DoesNotContain("set_default", light.Everything);
            Assert.DoesNotContain("set_name", light.Everything);
            Assert.DoesNotContain("cron_", light.Everything);
            var methods = Parsed(light.ControlLines.Concat(light.MusicLines)).Select(c => c.GetProperty("method").GetString()).Distinct();
            Assert.All(methods, m => Assert.Contains(m, new[] { "get_prop", "set_music", "set_scene", "set_power", "bg_set_scene", "bg_set_power" }));
            Assert.All(net.Sent, s => Assert.Equal(1982, s.Port));
        }
    }

    [Fact]
    public async Task Everything_is_closed_when_the_session_ends()
    {
        var light = new FakeYeelight(_net);
        var session = await Started();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.StopAsync(default);
        await session.DisposeAsync();
        Assert.All(light.Links, l => Assert.True(l.Disposed));
        Assert.All(light.Music, l => Assert.True(l.Disposed));
        Assert.All(_net.Listeners, l => Assert.True(l.Disposed));
    }
}
