using System.Text;
using RigControl.Lighting.Network;
using RigControl.Lighting.Network.Providers;

namespace RigControl.Lighting.Tests.Network.Providers;

/// <summary>A made-up Cololight at 172.16.0.80 that answers the state question the way the captures show.</summary>
internal sealed class FakeCololight
{
    public const string Ip = "172.16.0.80";
    public const string Mac = "A1B2C3D4E5F6";

    public bool Answers { get; set; } = true;
    public bool On { get; set; } = true;
    public int Brightness { get; set; } = 60;
    /// <summary>What it shows, as four bytes; null = it doesn't say.</summary>
    public string? Shown { get; set; } = "00 10 20 30";
    /// <summary>Where its answers seem to come from.</summary>
    public string AnswersFrom { get; set; } = Ip;

    public FakeCololight(FakeLightNetwork net)
    {
        net.OnUdp = sent =>
        {
            // Only the state question (03 02 01 00) is answered; set commands never are.
            if (!Answers || sent.Host != Ip || sent.Port != 8900 || sent.Data.Length != 40 || sent.Data[36] != 0x03) return null;
            return new[] { new UdpDatagram(AnswersFrom, 8900, Reply()) };
        };
    }

    public byte[] Reply() => StateReply(On, Brightness, Shown, Mac);

    public static byte[] StateReply(bool on, int brightness, string? shown, string mac)
    {
        var reply = new byte[62];
        Encoding.ASCII.GetBytes("SZ00").CopyTo(reply, 0);
        reply[9] = 52;
        reply[36] = 0x83;
        reply[40] = (byte)(on ? 0xCF : 0xCE);
        reply[41] = (byte)brightness;
        if (shown is not null)
        {
            reply[42] = 0x02;
            reply[43] = 0xFF;
            Hex.Parse(shown).CopyTo(reply, 44);
        }
        reply[48] = 0x0D;
        reply[49] = 0x01;
        Encoding.ASCII.GetBytes(mac).CopyTo(reply, 50);
        return reply;
    }
}

public class CololightProviderTests
{
    private const string Zeros9 = "00 00 00 00 00 00 00 00 00";
    private const string Zeros16 = "00 00 00 00 00 00 00 00 | 00 00 00 00 00 00 00 00";

    private readonly FakeLightNetwork _net = new();
    private readonly CololightProvider _provider = new() { AnswerWait = TimeSpan.FromMilliseconds(60) };

    private static PairedLight Paired(string type = "hexagon") =>
        new("cololight", "a1b2c3d4e5f6", "Cololight", type, FakeCololight.Ip, "net|cololight|a1b2c3d4e5f6",
            new Dictionary<string, string> { ["type"] = type });

    private static PairRequest Ask(string type = "hexagon") =>
        new(FakeCololight.Ip, null, new Dictionary<string, string> { ["type"] = type });

    /// <summary>A whole packet: the fixed start, the length byte, the counter and the command.</summary>
    private static byte[] Packet(string length, string counter, string body) =>
        Hex.Parse($"53 5A 30 30 | 00 00 00 00 00 | {length} | {Zeros16} | {counter} | {Zeros9} | {body}");

    private byte[][] SentData() => _net.Sent.Select(s => s.Data).ToArray();

    private async Task<ILightSession> StartAsync(string type = "hexagon")
    {
        var session = await _provider.StartAsync(_net, Paired(type), LightDescription.Single(), default);
        Assert.Single(_net.Sent); // the question about what it shows now
        return session;
    }

    [Fact]
    public async Task A_search_sends_nothing_and_finds_nothing()
    {
        _ = new FakeCololight(_net);
        Assert.Empty(await _provider.DiscoverAsync(_net, default));
        Assert.Empty(_net.Sent);
        Assert.Empty(_net.Asks);
        Assert.Empty(_net.TcpTried);
        Assert.Empty(_net.Browsed);
    }

    [Fact]
    public void It_says_it_is_untested_and_where_the_packets_come_from()
    {
        Assert.Equal("cololight", _provider.Brand);
        Assert.Equal("Cololight", _provider.DisplayName);
        Assert.False(_provider.Tested);
        Assert.Contains("Not yet tried on a real light", _provider.Notes);
        Assert.Contains("pycololight", _provider.Notes);
        Assert.Contains("one colour", _provider.Notes);
        Assert.Equal(2, _provider.MaxFramesPerSecond);
        Assert.False(_provider.Pairing.ReturnsSecret);
        Assert.True(_provider.Pairing.ManualAddress);
        var option = Assert.Single(_provider.Pairing.Options!);
        Assert.Equal("type", option.Key);
        Assert.Equal("hexagon", option.Default);
        Assert.Equal(new[] { "hexagon", "strip", "mix" }, option.Choices.Select(c => c.Value));
        Assert.Contains(NetworkLights.BuiltIn(), p => p is CololightProvider);
    }

    [Fact]
    public async Task The_state_question_is_exactly_the_captured_packet()
    {
        _ = new FakeCololight(_net);
        await _provider.DescribeAsync(_net, Paired(), default);
        var sent = Assert.Single(_net.Sent);
        Assert.Equal(FakeCololight.Ip, sent.Host);
        Assert.Equal(8900, sent.Port);
        Assert.Equal(40, sent.Data.Length);
        Assert.Equal(Hex.Parse("""
            53 5A 30 30 00 00 00 00 00 1E
            00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
            01
            00 00 00 00 00 00 00 00 00
            03 02 01 00
            """), sent.Data);
    }

    [Fact]
    public void Power_and_colour_packets_are_exactly_the_captured_ones()
    {
        // Power on at brightness 100, 42 bytes, length byte 20.
        var on = CololightProvider.Frame(1, CololightProvider.Power(true, 100));
        Assert.Equal(42, on.Length);
        Assert.Equal(Packet("20", "01", "04 01 03 01 CF 64"), on);
        // Power off keeps a brightness too.
        Assert.Equal(Packet("20", "02", "04 01 03 01 CE 3C"), CololightProvider.Frame(2, CololightProvider.Power(false, 60)));
        // A brightness outside 1-100 is never sent.
        Assert.Equal(Hex.Parse("04 01 03 01 CF 01"), CololightProvider.Power(true, 0));
        Assert.Equal(Hex.Parse("04 01 03 01 CF 64"), CololightProvider.Power(true, 255));

        // A colour, 45 bytes, length byte 23: first byte 04 for Hexagon and MIX, 07 for Strip.
        var hexagon = CololightProvider.Frame(1, CololightProvider.Colour(false, 0xFF8001));
        Assert.Equal(45, hexagon.Length);
        Assert.Equal(Packet("23", "01", "04 01 06 02 FF 00 FF 80 01"), hexagon);
        Assert.Equal(Packet("23", "02", "07 01 06 02 FF 00 FF 80 01"), CololightProvider.Frame(2, CololightProvider.Colour(true, 0xFF8001)));
    }

    [Fact]
    public void A_state_answer_gives_power_brightness_colour_and_the_hardware_address()
    {
        var on = CololightProvider.ParseState(FakeCololight.StateReply(true, 60, "00 10 20 30", "A1B2C3D4E5F6"))!;
        Assert.True(on.On);
        Assert.Equal(60, on.Brightness);
        Assert.Equal(0x102030, on.Colour);
        Assert.Equal("a1b2c3d4e5f6", on.Mac);

        var off = CololightProvider.ParseState(FakeCololight.StateReply(false, 35, "01 84 20 00", "a1b2c3d4e5f6"))!;
        Assert.False(off.On);
        Assert.Equal(35, off.Brightness);
        Assert.Null(off.Colour); // an effect, kept as its four bytes
        Assert.Equal(Hex.Parse("01 84 20 00"), off.Shown);

        Assert.Null(CololightProvider.ParseState(FakeCololight.StateReply(true, 60, null, "A1B2C3D4E5F6"))!.Shown);
    }

    [Fact]
    public void Anything_that_is_not_a_state_answer_is_ignored()
    {
        var good = FakeCololight.StateReply(true, 60, "00 10 20 30", "A1B2C3D4E5F6");
        Assert.NotNull(CololightProvider.ParseState(good));

        Assert.Null(CololightProvider.ParseState(good[..55]));
        Assert.Null(CololightProvider.ParseState(Array.Empty<byte>()));
        byte[] Changed(int at, byte to) { var b = (byte[])good.Clone(); b[at] = to; return b; }
        Assert.Null(CololightProvider.ParseState(Changed(0, 0x00)));    // not "SZ00"
        Assert.Null(CololightProvider.ParseState(Changed(36, 0x82)));   // not a state answer
        Assert.Null(CololightProvider.ParseState(Changed(40, 0x00)));   // neither on nor off
        Assert.Null(CololightProvider.ParseState(Changed(41, 101)));    // no such brightness
        Assert.Null(CololightProvider.ParseState(Changed(48, 0x00)));   // no address marker
        Assert.Null(CololightProvider.ParseState(Changed(55, (byte)'Z'))); // not a hardware address
    }

    [Theory]
    [InlineData("hexagon", "Cololight Hexagon", "Hexagon")]
    [InlineData("strip", "Cololight Strip", "Strip")]
    [InlineData("mix", "Cololight MIX", "MIX")]
    public async Task Pairing_asks_the_light_who_it_is_and_remembers_the_kind(string type, string name, string model)
    {
        _ = new FakeCololight(_net);
        var r = await _provider.PairAsync(_net, Ask(type), default);
        Assert.Equal("a1b2c3d4e5f6", r.Id);
        Assert.Equal(name, r.Name);
        Assert.Equal(model, r.Model);
        Assert.Equal(FakeCololight.Ip, r.Address);
        Assert.Equal(type, r.Data["type"]);
        Assert.Empty(r.Secrets);
        Assert.Single(_net.Sent);
    }

    [Fact]
    public async Task Pairing_without_a_kind_takes_the_hexagon()
    {
        _ = new FakeCololight(_net);
        var r = await _provider.PairAsync(_net, new PairRequest(FakeCololight.Ip, null, new Dictionary<string, string>()), default);
        Assert.Equal("hexagon", r.Data["type"]);
    }

    [Fact]
    public async Task Pairing_with_an_unknown_kind_is_refused_before_anything_is_sent()
    {
        _ = new FakeCololight(_net);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Ask("cube"), default));
        Assert.Contains("which kind of Cololight", ex.Message);
        Assert.Empty(_net.Sent);
    }

    [Fact]
    public async Task Pairing_with_nothing_there_asks_twice_and_then_says_so_plainly()
    {
        _ = new FakeCololight(_net) { Answers = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Ask(), default));
        Assert.Equal("No Cololight answered at that address. Check the address and that the light is switched on.", ex.Message);
        // Asked twice, and the counter goes 1 then 2.
        Assert.Equal(new[] { Packet("1E", "01", "03 02 01 00"), Packet("1E", "02", "03 02 01 00") }, SentData());
    }

    [Fact]
    public async Task An_answer_from_another_address_does_not_count()
    {
        _ = new FakeCololight(_net) { AnswersFrom = "172.16.0.81" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.PairAsync(_net, Ask(), default));
    }

    [Fact]
    public async Task A_light_that_does_not_answer_cannot_be_described_or_started()
    {
        _ = new FakeCololight(_net) { Answers = false };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DescribeAsync(_net, Paired(), default));
        Assert.Contains("doesn't answer", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.StartAsync(_net, Paired(), LightDescription.Single(), default));
    }

    [Fact]
    public async Task It_is_one_light_with_one_colour()
    {
        _ = new FakeCololight(_net);
        var d = await _provider.DescribeAsync(_net, Paired(), default);
        Assert.Equal(LightKind.Single, d.Kind);
        Assert.Single(d.Points);
    }

    [Fact]
    public async Task The_first_colour_sets_full_brightness_and_then_the_colour_with_the_counter_alternating()
    {
        _ = new FakeCololight(_net) { Brightness = 60 };
        await using var session = await StartAsync();
        await session.SendAsync(new[] { 0xFF8001 }, default);
        Assert.Equal(new[]
        {
            Packet("1E", "01", "03 02 01 00"),
            Packet("20", "02", "04 01 03 01 CF 64"),
            Packet("23", "01", "04 01 06 02 FF 00 FF 80 01"),
        }, SentData());
    }

    [Fact]
    public async Task A_strip_gets_its_own_first_byte_and_a_light_already_at_full_gets_just_the_colour()
    {
        _ = new FakeCololight(_net) { Brightness = 100 };
        await using var session = await StartAsync("strip");
        await session.SendAsync(new[] { 0x0A0B0C }, default);
        Assert.Equal(new[]
        {
            Packet("1E", "01", "03 02 01 00"),
            Packet("23", "02", "07 01 06 02 FF 00 0A 0B 0C"),
        }, SentData());
    }

    [Fact]
    public async Task A_mix_is_sent_the_same_colour_packet_as_a_hexagon()
    {
        _ = new FakeCololight(_net) { Brightness = 100 };
        await using var session = await StartAsync("mix");
        await session.SendAsync(new[] { 0x0A0B0C }, default);
        Assert.Equal(Packet("23", "02", "04 01 06 02 FF 00 0A 0B 0C"), SentData()[1]);
    }

    [Fact]
    public async Task A_colour_is_only_sent_when_it_changes()
    {
        _ = new FakeCololight(_net) { Brightness = 100 };
        await using var session = await StartAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), session.KeepAlive);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0xFF0000 }, default);
        Assert.Equal(2, _net.Sent.Count);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        Assert.Equal(3, _net.Sent.Count);
        Assert.Equal(Hex.Parse("04 01 06 02 FF 00 00 FF 00"), SentData()[2][36..]);
    }

    [Fact]
    public async Task Black_switches_it_off_once_and_colour_switches_it_on_again()
    {
        _ = new FakeCololight(_net) { Brightness = 100 };
        await using var session = await StartAsync();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        await session.SendAsync(new[] { 0x000000 }, default);
        await session.SendAsync(new[] { 0x030201 }, default); // still as good as black
        await session.SendAsync(new[] { 0x000000 }, default);
        await session.SendAsync(new[] { 0xFF0000 }, default); // the same colour as before: only switched on
        await session.SendAsync(new[] { 0x0000FF }, default);
        Assert.Equal(new[]
        {
            "03 02 01 00",
            "04 01 06 02 FF 00 FF 00 00",
            "04 01 03 01 CE 64",
            "04 01 03 01 CF 64",
            "04 01 06 02 FF 00 00 00 FF",
        }.Select(Hex.Parse), SentData().Select(d => d[36..]));
        // The counter alternates over everything sent.
        Assert.Equal(new byte[] { 1, 2, 1, 2, 1 }, SentData().Select(d => d[26]));
    }

    [Fact]
    public async Task A_light_that_was_off_stays_off_while_the_picture_is_black()
    {
        _ = new FakeCololight(_net) { On = false };
        await using var session = await StartAsync();
        await session.SendAsync(new[] { 0x000000 }, default);
        Assert.Single(_net.Sent);
        await session.SendAsync(new[] { 0x102030 }, default);
        Assert.Equal(Hex.Parse("04 01 03 01 CF 64"), SentData()[1][36..]);
        Assert.Equal(Hex.Parse("04 01 06 02 FF 00 10 20 30"), SentData()[2][36..]);
    }

    [Fact]
    public async Task Stopping_puts_back_the_plain_colour_and_brightness_it_had()
    {
        _ = new FakeCololight(_net) { Brightness = 60, Shown = "00 10 20 30" };
        await using var session = await StartAsync();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        var before = _net.Sent.Count;
        await session.StopAsync(default);
        Assert.Equal(new[] { "04 01 06 02 FF 00 10 20 30", "04 01 03 01 CF 3C" }.Select(Hex.Parse),
            SentData().Skip(before).Select(d => d[36..]));
        // Stopping twice, or a late frame, sends nothing more.
        await session.StopAsync(default);
        await session.SendAsync(new[] { 0x00FF00 }, default);
        Assert.Equal(before + 2, _net.Sent.Count);
    }

    [Fact]
    public async Task Stopping_puts_back_the_effect_it_was_playing()
    {
        _ = new FakeCololight(_net) { Brightness = 80, Shown = "01 84 20 00" };
        await using var session = await StartAsync("strip");
        await session.SendAsync(new[] { 0xFF0000 }, default);
        var before = _net.Sent.Count;
        await session.StopAsync(default);
        Assert.Equal(new[] { "07 01 06 02 FF 01 84 20 00", "04 01 03 01 CF 50" }.Select(Hex.Parse),
            SentData().Skip(before).Select(d => d[36..]));
    }

    [Fact]
    public async Task Stopping_switches_a_light_that_was_off_back_off_at_its_old_brightness()
    {
        _ = new FakeCololight(_net) { On = false, Brightness = 35, Shown = "00 10 20 30" };
        await using var session = await StartAsync();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        var before = _net.Sent.Count;
        await session.StopAsync(default);
        Assert.Equal(new[] { "04 01 06 02 FF 00 10 20 30", "04 01 03 01 CE 23" }.Select(Hex.Parse),
            SentData().Skip(before).Select(d => d[36..]));
    }

    [Fact]
    public async Task A_light_that_did_not_say_what_it_showed_only_gets_its_power_put_back()
    {
        _ = new FakeCololight(_net) { On = false, Brightness = 0, Shown = null };
        await using var session = await StartAsync();
        await session.SendAsync(new[] { 0xFF0000 }, default);
        var before = _net.Sent.Count;
        await session.StopAsync(default);
        // No brightness known: it will come on at full next time.
        Assert.Equal(new[] { "04 01 03 01 CE 64" }.Select(Hex.Parse), SentData().Skip(before).Select(d => d[36..]));
    }

    [Fact]
    public async Task A_light_that_was_never_sent_a_colour_is_left_alone_on_stop()
    {
        _ = new FakeCololight(_net);
        await using var session = await StartAsync();
        await session.StopAsync(default);
        Assert.Single(_net.Sent);
    }

    [Fact]
    public async Task Everything_goes_to_port_8900_of_the_paired_address_and_nowhere_else()
    {
        _ = new FakeCololight(_net) { Brightness = 60 };
        await _provider.DiscoverAsync(_net, default);
        await _provider.PairAsync(_net, Ask(), default);
        await _provider.DescribeAsync(_net, Paired(), default);
        await using (var session = await _provider.StartAsync(_net, Paired(), LightDescription.Single(), default))
        {
            await session.SendAsync(new[] { 0xFF0000 }, default);
            await session.SendAsync(new[] { 0x000000 }, default);
            await session.SendAsync(new[] { 0x00FF00 }, default);
            await session.StopAsync(default);
        }
        Assert.True(_net.Sent.Count >= 9);
        Assert.All(_net.Sent, s => Assert.Equal(FakeCololight.Ip, s.Host));
        Assert.All(_net.Sent, s => Assert.Equal(8900, s.Port));
        Assert.All(_net.Sent, s => Assert.Equal(new byte[] { 0x53, 0x5A, 0x30, 0x30 }, s.Data[..4]));
        Assert.All(_net.Sent, s => Assert.Equal(s.Data.Length - 10, s.Data[9]));
        Assert.Empty(_net.Asks);
        Assert.Empty(_net.TcpTried);
        Assert.Empty(_net.Browsed);
        Assert.Empty(_net.Listeners);
    }

    [Fact]
    public void It_says_it_cannot_be_searched_for_so_the_page_offers_no_search_button()
    {
        var provider = new CololightProvider();
        Assert.False(provider.Pairing.CanSearch);
        Assert.True(provider.Pairing.ManualAddress);
        Assert.Contains("can't be searched for", provider.Pairing.NothingFound);
        Assert.All(NetworkLights.BuiltIn().Where(p => p is not CololightProvider), p => Assert.True(p.Pairing.CanSearch));
    }
}
