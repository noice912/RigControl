using System.Text;
using System.Text.Json.Nodes;

namespace RigControl.Link.Tests;

public class SecureChannelTests
{
    internal const string Epoch = "AAECAwQFBgcICQoLDA0ODw";
    internal const string OtherEpoch = "DwAODQwLCgkIBwYFBAMCAQ";
    private static (SecureChannel Pc, SecureChannel Phone, DeviceKeys PcKeys, DeviceKeys PhoneKeys) Pair(TimeProvider? time = null)
    {
        var pc = DeviceKeys.Create();
        var phone = DeviceKeys.Create();
        return (SecureChannel.For(pc, phone.Id, phone.AgreementPublic, iAmPc: true, Epoch, time: time),
            SecureChannel.For(phone, pc.Id, pc.AgreementPublic, iAmPc: false, Epoch, time: time), pc, phone);
    }

    [Fact]
    public void Round_trip_both_ways()
    {
        var (pc, phone, _, _) = Pair();
        var m = phone.Open(pc.Seal(new JsonObject { ["type"] = "event", ["kind"] = "hi" }));
        Assert.Equal("hi", (string)m["kind"]!);
        var back = pc.Open(phone.Seal(LinkMessages.GetSnapshot()));
        Assert.Equal("get", (string)back["type"]!);
        Assert.Equal("snapshot", (string)back["what"]!);
    }

    [Fact]
    public void Stale_message_is_returned_but_does_not_move_LastIn()
    {
        var (pc, phone, _, _) = Pair();
        var now = DateTimeOffset.UtcNow;
        var body = phone.Seal(LinkMessages.GetSnapshot(), out var n);
        var m = pc.Open(body, _ => false, out var stale);
        Assert.True(stale);
        Assert.Equal(n, (long)m["n"]!);
        Assert.Equal(0, pc.LastIn);
        // The window itself: 2 minutes behind, 30 s ahead.
        var us = now.ToUnixTimeMilliseconds() * 1000;
        Assert.True(LinkProtocol.IsFresh(us - 119_000_000, now));
        Assert.False(LinkProtocol.IsFresh(us - 121_000_000, now));
        Assert.True(LinkProtocol.IsFresh(us + 29_000_000, now));
        Assert.False(LinkProtocol.IsFresh(us + 31_000_000, now));
        pc.Open(body, _ => true, out stale);
        Assert.False(stale);
        Assert.Equal(n, pc.LastIn);
    }

    [Fact]
    public void Stored_LastOut_keeps_counters_rising_after_the_clock_is_set_back()
    {
        using var pcKeys = DeviceKeys.Create();
        using var phoneKeys = DeviceKeys.Create();
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        var pc = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, Epoch, time: clock);
        var phone = SecureChannel.For(phoneKeys, pcKeys.Id, pcKeys.AgreementPublic, false, Epoch);
        phone.Open(pc.Seal(new JsonObject { ["type"] = "event", ["kind"] = "a" }));
        var stored = pc.LastOut;
        // Restarted with the clock an hour behind: the stored high-water mark wins, so the phone still accepts it.
        clock.Advance(TimeSpan.FromHours(-1));
        var restarted = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, Epoch, time: clock, lastOut: stored);
        restarted.Seal(new JsonObject { ["type"] = "event", ["kind"] = "b" }, out var n);
        Assert.Equal(stored + 1, n);
        var fresh = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, Epoch, time: clock);
        Assert.Equal("replay", Assert.Throws<LinkException>(() => phone.Open(fresh.Seal(new JsonObject { ["type"] = "event", ["kind"] = "c" }))).Code);
    }

    [Fact]
    public void Replay_is_rejected()
    {
        var (pc, phone, _, _) = Pair();
        var body = phone.Seal(LinkMessages.GetSnapshot());
        pc.Open(body);
        Assert.Equal("replay", Assert.Throws<LinkException>(() => pc.Open(body)).Code);
    }

    [Fact]
    public void Older_message_is_rejected_after_a_newer_one()
    {
        var (pc, phone, _, _) = Pair();
        var first = phone.Seal(LinkMessages.GetSnapshot());
        var second = phone.Seal(LinkMessages.GetSnapshot());
        pc.Open(second);
        Assert.Equal("replay", Assert.Throws<LinkException>(() => pc.Open(first)).Code);
    }

    [Fact]
    public void Stored_counter_keeps_replays_out_after_a_restart()
    {
        using var pcKeys = DeviceKeys.Create();
        using var phoneKeys = DeviceKeys.Create();
        var phone = SecureChannel.For(phoneKeys, pcKeys.Id, pcKeys.AgreementPublic, false, Epoch);
        var pc = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, Epoch);
        var body = phone.Seal(LinkMessages.GetSnapshot());
        pc.Open(body);
        var restarted = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, Epoch, lastIn: pc.LastIn);
        Assert.Equal("replay", Assert.Throws<LinkException>(() => restarted.Open(body)).Code);
    }

    [Fact]
    public void Counters_rise_strictly_even_on_a_stopped_clock()
    {
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var (pc, phone, _, _) = Pair(time);
        long last = 0;
        for (var i = 0; i < 5; i++)
        {
            phone.Seal(LinkMessages.GetSnapshot(), out var n);
            Assert.True(n > last);
            last = n;
        }
        Assert.Equal(1_800_000_000L * 1_000_000 + 4, last);
        // A clock that went back does not make counters go back.
        time.Set(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        phone.Seal(LinkMessages.GetSnapshot(), out var after);
        Assert.Equal(last + 1, after);
        _ = pc;
    }

    [Fact]
    public void Frame_from_an_earlier_pairing_is_refused_after_linking_again()
    {
        using var pcKeys = DeviceKeys.Create();
        using var phoneKeys = DeviceKeys.Create();
        var oldPhone = SecureChannel.For(phoneKeys, pcKeys.Id, pcKeys.AgreementPublic, false, Epoch);
        var recorded = oldPhone.Seal(LinkMessages.Do("capture.start", null));
        // Linked again: same keys, a new epoch, and even a counter that was reset to 0.
        var pc = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, OtherEpoch, lastIn: 0);
        Assert.Equal("sealed", Assert.Throws<LinkException>(() => pc.Open(recorded)).Code);
        var newPhone = SecureChannel.For(phoneKeys, pcKeys.Id, pcKeys.AgreementPublic, false, OtherEpoch);
        Assert.Equal("do", (string)pc.Open(newPhone.Seal(LinkMessages.Do("capture.start", null)))["type"]!);
    }

    [Fact]
    public void Tampered_body_fails()
    {
        var (pc, phone, _, _) = Pair();
        var raw = B64Url.Decode(phone.Seal(LinkMessages.Do("capture.start", null)));
        raw[^1] ^= 0x80;
        Assert.Equal("sealed", Assert.Throws<LinkException>(() => pc.Open(B64Url.Encode(raw))).Code);
        raw = B64Url.Decode(phone.Seal(LinkMessages.Do("capture.start", null)));
        raw[20] ^= 0x01;
        Assert.Equal("sealed", Assert.Throws<LinkException>(() => pc.Open(B64Url.Encode(raw))).Code);
    }

    [Fact]
    public void Changed_counter_in_the_nonce_fails()
    {
        var (pc, phone, _, _) = Pair();
        var raw = B64Url.Decode(phone.Seal(LinkMessages.GetSnapshot()));
        raw[11] ^= 0x01; // last byte of the counter
        Assert.Throws<LinkException>(() => pc.Open(B64Url.Encode(raw)));
    }

    [Fact]
    public void Own_message_reflected_back_fails()
    {
        var (pc, _, _, _) = Pair();
        var body = pc.Seal(LinkMessages.Event("x"));
        Assert.Equal("sealed", Assert.Throws<LinkException>(() => pc.Open(body)).Code);
    }

    [Fact]
    public void Another_phone_cannot_speak_for_a_linked_one()
    {
        using var pcKeys = DeviceKeys.Create();
        using var phoneKeys = DeviceKeys.Create();
        using var stranger = DeviceKeys.Create();
        var pc = SecureChannel.For(pcKeys, phoneKeys.Id, phoneKeys.AgreementPublic, true, Epoch);
        // The stranger knows the phone's id but not its private key: its session key differs.
        var fake = new SecureChannel(LinkCrypto.SessionKey(stranger, pcKeys.AgreementPublic, pcKeys.Id, Epoch), phoneKeys.Id, pcKeys.Id, false, Epoch);
        Assert.Equal("sealed", Assert.Throws<LinkException>(() => pc.Open(fake.Seal(LinkMessages.GetSnapshot()))).Code);
    }

    [Fact]
    public void Nonce_layout_is_direction_random_counter()
    {
        var (pc, phone, pcKeys, phoneKeys) = Pair();
        var body = phone.Seal(LinkMessages.GetSnapshot(), out var n);
        var raw = B64Url.Decode(body);
        Assert.Equal(SecureChannel.FromPhone, raw[0]);
        Assert.Equal((ulong)n, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(4, 8)));
        // The AAD is the documented text: decrypt by hand with it.
        var key = LinkCrypto.SessionKey(pcKeys, phoneKeys.AgreementPublic, phoneKeys.Id, Epoch);
        var plain = LinkCrypto.Open(key, raw, Encoding.UTF8.GetBytes($"rigcontrol-link-v1|{phoneKeys.Id}|{pcKeys.Id}|{n}|{Epoch}"));
        var o = JsonNode.Parse(plain)!.AsObject();
        Assert.Equal(n, (long)o["n"]!);
        _ = pc;
    }
}

public class LinkActionTests
{
    [Theory]
    [InlineData("lighting.off", null)]
    [InlineData("lighting.on", null)]
    [InlineData("capture.start", "{}")]
    [InlineData("capture.stop", null)]
    [InlineData("capture.saveReplay", null)]
    [InlineData("lighting.setEffect", "{\"effect\":\"wave\"}")]
    [InlineData("lighting.setEffect", "{\"scene\":\"Movie night\"}")]
    [InlineData("presence.setCustom", "{\"id\":\"abc_1\",\"on\":true}")]
    public void Allowed_actions_pass(string action, string? args)
    {
        var a = LinkActions.Check(action, args is null ? null : JsonNode.Parse(args), out var error);
        Assert.Null(error);
        Assert.NotNull(a);
        Assert.Equal(action, a!.Name);
    }

    [Fact]
    public void The_list_is_exactly_D1()
        => Assert.Equal(new[] { "lighting.setEffect", "lighting.off", "lighting.on", "capture.start", "capture.stop", "capture.saveReplay", "presence.setCustom",
                                "presence.phoneMusic", "presence.spotifyConnect", "presence.spotifyDisconnect" },
            LinkActions.Allowed);

    [Theory]
    [InlineData("cooling.setPumpDuty", "{\"duty\":100}")]
    [InlineData("sensors.setFanDuty", null)]
    [InlineData("bios.apply", null)]
    [InlineData("app.quit", null)]
    [InlineData("lighting.setColor", "{\"color\":\"#ff0000\"}")]
    [InlineData("capture.openFolder", null)]
    [InlineData("presence.setCustomStatuses", "{\"items\":[]}")]
    [InlineData("LIGHTING.OFF", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Everything_else_is_refused(string? action, string? args)
    {
        Assert.Null(LinkActions.Check(action, args is null ? null : JsonNode.Parse(args), out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("lighting.off", "{\"deviceIds\":[1]}")]
    [InlineData("capture.start", "{\"file\":\"C:\\\\x\"}")]
    [InlineData("lighting.setEffect", "{}")]
    [InlineData("lighting.setEffect", "{\"effect\":\"wave\",\"scene\":\"x\"}")]
    [InlineData("lighting.setEffect", "{\"effect\":\"../x\"}")]
    [InlineData("lighting.setEffect", "{\"effect\":5}")]
    [InlineData("lighting.setEffect", "{\"effect\":\"wave\",\"params\":{}}")]
    [InlineData("presence.setCustom", "{\"id\":\"abc\"}")]
    [InlineData("presence.setCustom", "{\"id\":\"abc\",\"on\":\"yes\"}")]
    [InlineData("presence.setCustom", "{\"id\":\"a b\",\"on\":true}")]
    [InlineData("presence.setCustom", "{\"id\":\"abc\",\"on\":true,\"name\":\"x\"}")]
    [InlineData("capture.stop", "[1]")]
    public void Bad_arguments_are_refused(string action, string args)
    {
        Assert.Null(LinkActions.Check(action, JsonNode.Parse(args), out var error));
        Assert.NotNull(error);
    }
}
