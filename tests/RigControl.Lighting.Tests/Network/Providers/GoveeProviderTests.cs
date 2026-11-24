using System.Text;
using System.Text.Json;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network.Providers;

/// <summary>A made-up Govee light at 172.16.0.80 that answers the way the published protocol says.</summary>
internal sealed class FakeGovee
{
    public const string Ip = "172.16.0.80";
    public const string Device = "1F:80:C5:32:32:36:72:4E";

    public string Sku { get; set; } = "H6199";
    public bool Answers { get; set; } = true;
    public bool KnowsRazer { get; set; } = true;
    /// <summary>Zero bytes some lights put after their answer.</summary>
    public int Padding { get; set; }
    public string State { get; set; } = "{\"onOff\":1,\"brightness\":40,\"color\":{\"r\":10,\"g\":20,\"b\":30},\"colorTemInKelvin\":0}";

    public FakeGovee(FakeLightNetwork net)
    {
        net.OnUdp = sent =>
        {
            if (!Answers) return null;
            // The search question (to the group or straight to the light) and questions on the control port.
            var scan = sent.Port == 4001 && (sent.Host == "239.255.255.250" || sent.Host == Ip);
            if (scan && sent.Text.Contains("\"scan\""))
                return new[] { Reply($"{{\"msg\":{{\"cmd\":\"scan\",\"data\":{{\"ip\":\"{Ip}\",\"device\":\"{Device}\",\"sku\":\"{Sku}\",\"bleVersionHard\":\"3.01.01\",\"wifiVersionSoft\":\"1.02.03\"}}}}}}") };
            if (sent.Host != Ip || sent.Port != 4003) return null;
            if (sent.Text.Contains("\"devStatus\"")) return new[] { Reply($"{{\"msg\":{{\"cmd\":\"devStatus\",\"data\":{State}}}}}") };
            if (sent.Text.Contains("\"status\"") && KnowsRazer)
                return new[] { Reply("{\"msg\":{\"cmd\":\"status\",\"data\":{\"pt\":\"" + Convert.ToBase64String(new byte[] { 0xBB, 0x00, 0x01, 0xB2, 0x01, 0x09 }) + "\"}}}") };
            return null;
        };
    }

    private UdpDatagram Reply(string json, string from = Ip) =>
        new(from, 4003, Encoding.UTF8.GetBytes(json).Concat(new byte[Padding]).ToArray());
}

/// <summary>A network where another program holds port 4002 for itself.</summary>
internal sealed class ReplyPortTaken : ILightNetwork
{
    private readonly FakeLightNetwork _inner = new();
    public IReadOnlyList<LocalNetwork> LocalNetworks() => _inner.LocalNetworks();
    public IUdpPort OpenUdp(UdpOptions? options = null) =>
        options?.LocalPort == 4002 ? throw new System.Net.Sockets.SocketException(10048) : _inner.OpenUdp(options);
    public Task<ITcpLink> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct) => _inner.ConnectTcpAsync(host, port, timeout, ct);
    public ITcpListener ListenTcp(string localAddress, int port = 0) => _inner.ListenTcp(localAddress, port);
    public Task<HttpReply> HttpAsync(HttpAsk ask, CancellationToken ct) => _inner.HttpAsync(ask, ct);
    public Task<IReadOnlyList<MdnsService>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken ct) => _inner.BrowseAsync(serviceType, wait, ct);
}

public class GoveeProviderTests
{
    private const string Scan = "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}";
    private const string AskState = "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}";
    private const string TurnOn = "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}";
    private const string TurnOff = "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}";

    private readonly FakeLightNetwork _net = new();
    // The real waits are 3 s and 1.5 s; the made-up light answers at once.
    private readonly GoveeProvider _provider = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1));

    private static PairedLight Paired(int segments = 1, string sku = "H6199", string address = FakeGovee.Ip) =>
        new("govee", FakeGovee.Device, "Govee " + sku, sku, address, "net|govee|" + FakeGovee.Device,
            new Dictionary<string, string> { ["sku"] = sku, ["segments"] = segments.ToString() });

    private static PairRequest Request(string segments = "1", string address = FakeGovee.Ip) =>
        new(address, null, new Dictionary<string, string> { ["segments"] = segments });

    private static string Colour(int r, int g, int b, int kelvin = 0) =>
        $"{{\"msg\":{{\"cmd\":\"colorwc\",\"data\":{{\"color\":{{\"r\":{r},\"g\":{g},\"b\":{b}}},\"colorTemInKelvin\":{kelvin}}}}}}}";

    private static string Brightness(int value) => $"{{\"msg\":{{\"cmd\":\"brightness\",\"data\":{{\"value\":{value}}}}}}}";

    /// <summary>What went to the light's control port, in order, without the state questions.</summary>
    private List<string> Commands() =>
        _net.Sent.Where(s => s.Port == 4003 && s.Text != AskState).Select(s => s.Text).ToList();

    /// <summary>The packet inside a "razer" message.</summary>
    private static byte[] RazerBytes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("msg");
        Assert.Equal("razer", msg.GetProperty("cmd").GetString());
        return Convert.FromBase64String(msg.GetProperty("data").GetProperty("pt").GetString()!);
    }

    [Fact]
    public void It_says_it_is_untested_and_needs_no_key()
    {
        var real = new GoveeProvider();
        Assert.Equal("govee", real.Brand);
        Assert.Equal("Govee", real.DisplayName);
        Assert.False(real.Tested);
        Assert.Contains("not yet tried on a real device", real.Notes);
        Assert.False(real.Pairing.ReturnsSecret);
        Assert.True(real.Pairing.ManualAddress);
        Assert.Contains(real.Pairing.Steps, s => s.Contains("LAN Control"));
        var option = Assert.Single(real.Pairing.Options!);
        Assert.Equal("segments", option.Key);
        Assert.Equal("1", option.Default);
        Assert.Equal(new[] { "1", "5", "10", "15", "20", "30" }, option.Choices.Select(c => c.Value));
        Assert.Contains(NetworkLights.BuiltIn(), p => p is GoveeProvider);
    }

    [Fact]
    public async Task The_search_question_goes_to_the_group_from_port_4002_on_every_home_network_and_never_a_VPN_adapter()
    {
        _net.Networks.Add(new LocalNetwork("172.16.5.10", 24, true));
        _net.Networks.Add(new LocalNetwork("10.0.0.2", 24, false)); // a VPN adapter: no router, never searched
        _ = new FakeGovee(_net);
        var found = Assert.Single(await _provider.DiscoverAsync(_net, default));

        Assert.Equal(2, _net.Sent.Count);
        Assert.All(_net.Sent, s =>
        {
            Assert.Equal(Encoding.UTF8.GetBytes(Scan), s.Data);
            Assert.Equal("239.255.255.250", s.Host);
            Assert.Equal(4001, s.Port);
            Assert.Equal(4002, s.LocalPort);
        });
        Assert.Equal(FakeGovee.Device, found.Id);
        Assert.Equal("H6199", found.Model);
        Assert.Equal("Govee H6199", found.Name);
        Assert.Equal(FakeGovee.Ip, found.Address);
    }

    [Fact]
    public async Task An_answer_with_zero_bytes_after_it_is_still_read_and_the_senders_address_wins()
    {
        // This light pads its answer and writes a different address inside than the one it answers from.
        var search = _provider.DiscoverAsync(_net, default);
        await Eventually.True(() => !_net.Sent.IsEmpty, "the search question");
        var json = "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"ip\":\"172.16.0.99\",\"device\":\"AA:BB:CC:DD:EE:FF:00:11\",\"sku\":\"H619A\"}}}";
        _net.Deliver(4002, new UdpDatagram("172.16.0.81", 4001, Encoding.UTF8.GetBytes(json).Concat(new byte[12]).ToArray()));
        _net.Deliver(4002, new UdpDatagram("172.16.0.82", 4001, Encoding.UTF8.GetBytes("not a Govee answer")));

        var found = Assert.Single(await search);
        Assert.Equal("AA:BB:CC:DD:EE:FF:00:11", found.Id);
        Assert.Equal("172.16.0.81", found.Address);
        Assert.Equal("H619A", found.Model);
    }

    [Fact]
    public async Task A_light_paired_by_address_on_a_network_without_a_router_can_still_be_asked()
    {
        // The search leaves such a network out, but a light the user typed the address of there still works.
        _net.Networks.Clear();
        _net.Networks.Add(new LocalNetwork("172.16.0.10", 24, false));
        _ = new FakeGovee(_net);
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Empty(_net.Sent);
        var r = await _provider.PairAsync(_net, Request(), default);
        Assert.Equal(FakeGovee.Device, r.Id);
    }

    [Fact]
    public async Task Made_up_answers_are_dropped_and_a_flood_of_them_ends_the_search_at_64_lights()
    {
        string Answer(string device, string sku) =>
            "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"device\":" + JsonSerializer.Serialize(device) + ",\"sku\":" + JsonSerializer.Serialize(sku) + "}}}";
        UdpDatagram From(string json) => new("172.16.0.66", 4003, Encoding.UTF8.GetBytes(json));
        _net.OnUdp = sent => sent.Port != 4001 ? null :
            new[]
            {
                From(Answer("AA:BB\r\n2026-11-24 21:00:00 [info] Helper programs installed", "H6199")), // a line break in the id
                From(Answer("AA:BB:CC:DD:EE:FF:11:22", new string('S', 60000))),                         // an endless model
                From(Answer(new string('D', 500), "H6199")),                                             // an endless id
            }.Concat(Enumerable.Range(0, 1000).Select(i => From(Answer($"AA:BB:CC:DD:EE:FF:{i / 256:X2}:{i % 256:X2}", "H61\n99"))));

        var found = await _provider.DiscoverAsync(_net, default);
        Assert.Equal(64, found.Count);
        Assert.All(found, f =>
        {
            Assert.DoesNotContain(f.Id + f.Name + f.Model, c => char.IsControl(c));
            Assert.True(f.Id.Length <= 64 && f.Name.Length <= 64 && f.Model.Length <= 40);
        });
    }

    [Fact]
    public async Task A_typed_address_is_asked_the_same_question_directly()
    {
        _ = new FakeGovee(_net) { Padding = 3 };
        var r = await _provider.PairAsync(_net, Request(), default);

        var sent = Assert.Single(_net.Sent);
        Assert.Equal(Scan, sent.Text);
        Assert.Equal(FakeGovee.Ip, sent.Host);
        Assert.Equal(4001, sent.Port);
        Assert.Equal(4002, sent.LocalPort);
        Assert.Equal(FakeGovee.Device, r.Id);
        Assert.Equal("Govee H6199", r.Name);
        Assert.Equal("H6199", r.Model);
        Assert.Equal(FakeGovee.Ip, r.Address);
        Assert.Empty(r.Secrets);
        Assert.Equal("H6199", r.Data["sku"]);
        Assert.Equal("1", r.Data["segments"]);
    }

    [Fact]
    public async Task No_answer_at_a_typed_address_says_to_check_LAN_Control()
    {
        _ = new FakeGovee(_net) { Answers = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request(), default));
        Assert.Equal("No Govee light answered at that address. Check LAN Control is switched on for it in the Govee Home app.", ex.Message);
    }

    [Fact]
    public async Task An_answer_from_another_address_does_not_count_for_the_typed_one()
    {
        // Some other Govee light answers while 172.16.0.90 is asked.
        _net.OnUdp = _ => new[]
        {
            new UdpDatagram(FakeGovee.Ip, 4001, Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"ip\":\"172.16.0.90\",\"device\":\"" + FakeGovee.Device + "\",\"sku\":\"H6199\"}}}")),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request(address: "172.16.0.90"), default));
    }

    [Fact]
    public async Task Pairing_with_a_colour_per_piece_checks_the_light_takes_it_and_leaves_it_switched_off_again()
    {
        _ = new FakeGovee(_net);
        var r = await _provider.PairAsync(_net, Request("10"), default);
        Assert.Equal("10", r.Data["segments"]);

        var control = _net.Sent.Where(s => s.Port == 4003).Select(s => s.Text).ToList();
        Assert.Equal(3, control.Count);
        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x01, 0x0A }, RazerBytes(control[0]));
        Assert.Equal("{\"msg\":{\"cmd\":\"status\",\"data\":{}}}", control[1]);
        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x00, 0x0B }, RazerBytes(control[2]));
    }

    [Fact]
    public async Task A_colour_per_piece_is_refused_when_the_light_does_not_answer_the_question_about_it()
    {
        _ = new FakeGovee(_net) { KnowsRazer = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request("10"), default));
        Assert.Equal("This Govee light doesn't take a colour per piece over the network. Pair it again with one colour for the whole light.", ex.Message);
        // It was told to stop again.
        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x00, 0x0B }, RazerBytes(_net.Sent.Last().Text));
    }

    [Fact]
    public async Task A_number_of_pieces_that_makes_no_sense_is_refused_in_plain_words()
    {
        _ = new FakeGovee(_net);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Request("lots"), default));
        Assert.Contains("between 1 and 255", ex.Message);
        Assert.Empty(_net.Sent);
    }

    [Fact]
    public async Task Another_program_holding_the_reply_port_is_named_as_the_problem()
    {
        var taken = new ReplyPortTaken();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DiscoverAsync(taken, default));
        Assert.Equal("Another program is using Govee's reply port (4002). Close OpenRGB's Govee support, Razer Synapse or another Govee tool and try again.", ex.Message);
    }

    [Fact]
    public async Task It_is_one_light_or_a_strip_of_the_chosen_number_and_must_answer_to_be_described()
    {
        var govee = new FakeGovee(_net);
        Assert.Equal(LightKind.Single, (await _provider.DescribeAsync(_net, Paired(), default)).Kind);
        var strip = await _provider.DescribeAsync(_net, Paired(15), default);
        Assert.Equal(LightKind.Strip, strip.Kind);
        Assert.Equal(15, strip.Points.Count);
        Assert.All(_net.Sent, s => Assert.Equal((AskState, FakeGovee.Ip, 4003, 4002), (s.Text, s.Host, s.Port, s.LocalPort)));

        govee.Answers = false;
        await Assert.ThrowsAsync<IOException>(() => _provider.DescribeAsync(_net, Paired(), default));
    }

    [Fact]
    public async Task State_answers_are_matched_by_the_address_they_come_from()
    {
        // Two lights are asked at the same time; each answer carries no id, only where it came from.
        static UdpDatagram State(string from, int brightness) => new(from, 4003, Encoding.UTF8.GetBytes(
            "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":1,\"brightness\":" + brightness + ",\"color\":{\"r\":1,\"g\":2,\"b\":3},\"colorTemInKelvin\":0}}}"));
        var a = _provider.StartAsync(_net, Paired(address: "172.16.0.80"), LightDescription.Single(), default);
        var b = _provider.StartAsync(_net, Paired(address: "172.16.0.81"), LightDescription.Single(), default);
        await Eventually.True(() => _net.Sent.Count(s => s.Text == AskState) == 2, "both questions");
        // The answers arrive the other way round; a session only gets its own if it looks at the sender.
        _net.Deliver(4002, State("172.16.0.81", 81));
        _net.Deliver(4002, State("172.16.0.80", 80));
        await using var first = await a;
        await using var second = await b;

        await first.StopAsync(default);
        await second.StopAsync(default);
        Assert.Contains(_net.Sent, s => s.Host == "172.16.0.80" && s.Text == Brightness(80));
        Assert.Contains(_net.Sent, s => s.Host == "172.16.0.81" && s.Text == Brightness(81));
        Assert.DoesNotContain(_net.Sent, s => s.Host == "172.16.0.80" && s.Text == Brightness(81));
    }

    [Fact]
    public async Task One_colour_is_sent_only_when_it_changes_at_full_brightness_set_once()
    {
        _ = new FakeGovee(_net);
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Single(), default);
        Assert.Equal(0, session.MaxFramesPerSecond); // the provider's two a second
        Assert.Equal(2, _provider.MaxFramesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(30), session.KeepAlive);
        Assert.Empty(Commands()); // nothing is changed until there is a colour to show

        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0x0080FF }, default);

        Assert.Equal(new[] { Brightness(100), Colour(255, 0, 0), Colour(0, 128, 255) }, Commands());
        Assert.Equal("{\"msg\":{\"cmd\":\"brightness\",\"data\":{\"value\":100}}}", Brightness(100));
        Assert.Equal("{\"msg\":{\"cmd\":\"colorwc\",\"data\":{\"color\":{\"r\":255,\"g\":0,\"b\":0},\"colorTemInKelvin\":0}}}", Colour(255, 0, 0));
        Assert.All(_net.Sent.Where(s => s.Text != AskState), s => Assert.Equal((FakeGovee.Ip, 4003), (s.Host, s.Port)));
    }

    [Fact]
    public async Task Black_switches_the_light_off_once_and_a_colour_switches_it_on_again()
    {
        _ = new FakeGovee(_net);
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Single(), default);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        await session.SendAsync(new[] { 0 }, default);
        await session.SendAsync(new[] { 0 }, default);
        await session.SendAsync(new[] { 0x00FF00 }, default);

        Assert.Equal(new[] { Brightness(100), Colour(0, 255, 0), TurnOff, TurnOn, Colour(0, 255, 0) }, Commands());
    }

    [Fact]
    public async Task Stopping_puts_back_the_colour_brightness_and_power_it_had()
    {
        _ = new FakeGovee(_net) { State = "{\"onOff\":0,\"brightness\":40,\"color\":{\"r\":10,\"g\":20,\"b\":30},\"colorTemInKelvin\":0}" };
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Single(), default);
        await session.SendAsync(new[] { 0xFFFFFF }, default);
        await session.StopAsync(default);

        Assert.Equal(new[] { TurnOn, Brightness(100), Colour(255, 255, 255), Colour(10, 20, 30), Brightness(40), TurnOff }, Commands());
    }

    [Fact]
    public async Task A_white_temperature_is_put_back_as_a_temperature_with_the_colour_zeroed()
    {
        _ = new FakeGovee(_net) { State = "{\"onOff\":1,\"brightness\":75,\"color\":{\"r\":255,\"g\":200,\"b\":150},\"colorTemInKelvin\":3500}" };
        await using var session = await _provider.StartAsync(_net, Paired(), LightDescription.Single(), default);
        await session.StopAsync(default);

        Assert.Equal(new[] { Colour(0, 0, 0, 3500), Brightness(75), TurnOn }, Commands());
    }

    [Fact]
    public async Task A_light_that_does_not_say_what_it_shows_is_not_taken_over()
    {
        _ = new FakeGovee(_net) { Answers = false };
        await Assert.ThrowsAsync<IOException>(() => _provider.StartAsync(_net, Paired(), LightDescription.Single(), default));
        Assert.Empty(Commands());
    }

    [Fact]
    public async Task A_colour_per_piece_goes_as_one_packet_with_its_length_high_byte_first_and_a_check_byte()
    {
        _ = new FakeGovee(_net);
        await using var session = await _provider.StartAsync(_net, Paired(3), LightDescription.Strip(3), default);
        Assert.Equal(20, session.MaxFramesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(5), session.KeepAlive);
        await session.SendAsync(new[] { 0xFF0000, 0x00FF00, 0x0000FF }, default);

        var sent = Commands();
        Assert.Equal(3, sent.Count);
        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x01, 0x0A }, RazerBytes(sent[0])); // stream on
        Assert.Equal(Brightness(100), sent[1]);
        var frame = RazerBytes(sent[2]);
        Assert.Equal(7 + 3 * 3, frame.Length);
        Assert.Equal(new byte[]
        {
            0xBB, 0x00, 0x0B, // 11 = 2 + 3 x 3
            0xB0, 0x01, 0x03, // frame, blended, three pieces
            0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF,
        }, frame[..^1]);
        Assert.Equal(frame[..^1].Aggregate((byte)0, (x, b) => (byte)(x ^ b)), frame[^1]);
        Assert.Equal(0xFD, frame[^1]);
    }

    [Fact]
    public void A_long_strip_writes_its_length_in_two_bytes()
    {
        var frame = GoveeProvider.RazerFrame(Enumerable.Repeat(0x102030, 100).ToList());
        Assert.Equal(307, frame.Length);
        Assert.Equal(new byte[] { 0xBB, 0x01, 0x2E, 0xB0, 0x01, 100, 0x10, 0x20, 0x30 }, frame[..9]); // 0x012E = 302
        Assert.Equal(frame[..^1].Aggregate((byte)0, (x, b) => (byte)(x ^ b)), frame[^1]);
        Assert.Equal(TimeSpan.FromMilliseconds(700), GoveeProvider.RazerSettleTime("H6159"));
        Assert.Equal(TimeSpan.FromMilliseconds(50), GoveeProvider.RazerSettleTime("H6199"));
    }

    [Fact]
    public async Task Stopping_a_colour_per_piece_light_switches_the_stream_off_first_then_puts_everything_back()
    {
        _ = new FakeGovee(_net);
        await using var session = await _provider.StartAsync(_net, Paired(3), LightDescription.Strip(3), default);
        await session.SendAsync(new[] { 1, 2, 3 }, default);
        var before = Commands().Count;
        await session.StopAsync(default);

        var restore = Commands().Skip(before).ToList();
        Assert.Equal(4, restore.Count);
        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x00, 0x0B }, RazerBytes(restore[0]));
        Assert.Equal(new[] { Colour(10, 20, 30), Brightness(40), TurnOn }, restore.Skip(1));
        Assert.Equal("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}", TurnOn);
        Assert.Equal("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}", TurnOff);
    }
}
