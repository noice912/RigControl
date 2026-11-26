using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace RigControl.Link.Tests;

/// <summary>A PC app as far as <see cref="PcSide"/> sees it.</summary>
internal sealed class TestHost : IPcLinkHost
{
    public readonly List<PhoneRecord> Phones = new();
    public readonly ConcurrentQueue<LinkAction> Ran = new();
    public readonly ConcurrentDictionary<string, long> Counters = new();
    public readonly ConcurrentQueue<(string Id, bool Online)> Presence = new();
    public LinkActionResult NextResult = LinkActionResult.Success;

    public IReadOnlyList<PhoneRecord> LinkedPhones()
    {
        lock (Phones) return Phones.ToList();
    }

    public void Add(PairedPhone p)
    {
        lock (Phones) Phones.Add(new PhoneRecord(p.Id, p.Name, p.AgreementKey, 0, p.Epoch));
    }

    public void Remove(string id)
    {
        lock (Phones) Phones.RemoveAll(p => p.Id == id);
    }

    public Task<JsonObject> SnapshotAsync(PhoneRecord phone, CancellationToken ct)
        => Task.FromResult(new JsonObject { ["pc"] = "Test PC", ["cpu"] = new JsonObject { ["tempC"] = 41.5 } });

    public Task<LinkActionResult> RunAsync(PhoneRecord phone, LinkAction action, CancellationToken ct)
    {
        Ran.Enqueue(action);
        return Task.FromResult(NextResult);
    }

    /// <summary>Every Accepted call: (counter, saveNow).</summary>
    public readonly ConcurrentQueue<(long Counter, bool SaveNow)> Saves = new();

    public void Accepted(string phoneId, long counter) => Accepted(phoneId, counter, saveNow: false);

    public void Accepted(string phoneId, long counter, bool saveNow)
    {
        Counters[phoneId] = counter;
        Saves.Enqueue((counter, saveNow));
    }

    public void PresenceChanged(string phoneId, bool online) => Presence.Enqueue((phoneId, online));

    public readonly ConcurrentDictionary<string, long> SentCounters = new();

    public void Sent(string phoneId, long counter) => SentCounters.AddOrUpdate(phoneId, counter, (_, old) => Math.Max(old, counter));
}

public class EndToEndTests : IAsyncLifetime
{
    private static readonly Uri Relay = new("wss://relay.test");
    private static readonly RelayLinkOptions Fast = new() { Backoff = _ => TimeSpan.FromMilliseconds(20) };

    private readonly FakeRelay _relay = new();
    private readonly TestHost _host = new();
    private readonly DeviceKeys _pcKeys = DeviceKeys.Create();
    private readonly DeviceKeys _phoneKeys = DeviceKeys.Create();
    private PcSide _pc = null!;
    private readonly List<IAsyncDisposable> _cleanup = new();
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    public async Task InitializeAsync()
    {
        _pc = new PcSide(_relay, Relay, _pcKeys, _host, new PcSideOptions { Relay = Fast });
        _pc.Start();
        await _pc.WaitOnlineAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        await _pc.DisposeAsync();
    }

    private async Task<(LinkedPc Pc, PairedPhone Phone)> PairAsync()
    {
        var code = PairingCode.New();
        var offer = _pc.PairAsync(code, "Test PC", TimeSpan.FromMinutes(10), Ct);
        // The phone types it the way people do.
        await WaitUntil(() => _relay.Frames().Any(f => f.Path == "/v1/pair"));
        var linked = await PhoneSide.PairAsync(_relay, Relay.ToString(), PairingCode.Display(code).ToLowerInvariant(), _phoneKeys, "Test phone", Ct);
        var phone = await offer;
        _host.Add(phone);
        Assert.True(await _pc.RefreshAllowListAsync(Ct));
        await WaitUntil(() => _relay.AllowList(_pcKeys.Id).Contains(phone.Id));
        return (linked, phone);
    }

    private async Task RestartPc(PcSideOptions options)
    {
        await _pc.DisposeAsync();
        _pc = new PcSide(_relay, Relay, _pcKeys, _host, options);
        _pc.Start();
        await _pc.WaitOnlineAsync(Ct);
    }

    private async Task<PhoneSide> ConnectPhone(LinkedPc pc, long lastIn = 0, TimeProvider? time = null)
    {
        var side = new PhoneSide(_relay, pc, _phoneKeys, lastIn,
            new PhoneSideOptions { Relay = Fast, ReplyTimeout = TimeSpan.FromSeconds(5), Time = time ?? TimeProvider.System });
        _cleanup.Add(side);
        await side.ConnectAsync(Ct);
        return side;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Pairing_exchanges_keys_and_names()
    {
        var (pc, phone) = await PairAsync();
        Assert.Equal(_pcKeys.Id, pc.Id);
        Assert.Equal("Test PC", pc.Name);
        Assert.Equal(_pcKeys.AgreementPublicB64, pc.AgreementKey);
        Assert.Equal(_phoneKeys.Id, phone.Id);
        Assert.Equal("Test phone", phone.Name);
        Assert.Equal(_phoneKeys.AgreementPublicB64, phone.AgreementKey);
        Assert.Equal(phone.Epoch, pc.Epoch);
        Assert.Equal(16, B64Url.Decode(pc.Epoch).Length);
        // The relay only saw h and sealed boxes: no name or key in clear.
        var pairFrames = _relay.Frames().Where(f => f.Path == "/v1/pair").Select(f => f.Text).ToList();
        Assert.Equal(2, pairFrames.Count);
        Assert.All(pairFrames, t => Assert.DoesNotContain("Test", t));
        Assert.All(pairFrames, t => Assert.DoesNotContain(_phoneKeys.AgreementPublicB64, t));
        var offer = JsonNode.Parse(pairFrames[0])!.AsObject();
        Assert.Equal(new[] { "t", "h", "owner", "box" }, offer.Select(p => p.Key));
        Assert.Equal(32, B64Url.Decode((string)offer["owner"]!).Length);
        Assert.Equal("offer", (string)offer["t"]!);
    }

    [Fact]
    public async Task Wrong_code_finds_no_offer()
    {
        var code = PairingCode.New();
        var offer = _pc.PairAsync(code, "Test PC", TimeSpan.FromMinutes(10), Ct);
        await WaitUntil(() => _relay.Frames().Any(f => f.Path == "/v1/pair"));
        var ex = await Assert.ThrowsAsync<LinkException>(() =>
            PhoneSide.PairAsync(_relay, Relay.ToString(), PairingCode.New(), _phoneKeys, "Test phone", Ct));
        Assert.Equal("nopair", ex.Code);
        Assert.False(offer.IsCompleted);
    }

    [Fact]
    public async Task A_relay_that_fakes_the_offer_is_caught()
    {
        var code = PairingCode.New();
        _relay.TamperOffer = _ => LinkCrypto.SealBox(PairingCode.BoxKey(PairingCode.New()), new JsonObject { ["pc"] = "x" });
        _ = _pc.PairAsync(code, "Test PC", TimeSpan.FromMinutes(10), Ct);
        await WaitUntil(() => _relay.Frames().Any(f => f.Path == "/v1/pair"));
        var ex = await Assert.ThrowsAsync<LinkException>(() => PhoneSide.PairAsync(_relay, Relay.ToString(), code, _phoneKeys, "p", Ct));
        Assert.Equal("wrongcode", ex.Code);
    }

    [Fact]
    public async Task Replaced_offer_ends_the_code_without_a_re_offer()
    {
        var code = PairingCode.New();
        var offer = _pc.PairAsync(code, "Test PC", TimeSpan.FromMinutes(10), Ct);
        await WaitUntil(() => _relay.Frames().Any(f => f.Path == "/v1/pair"));
        _relay.ReplaceOffer(PairingCode.RendezvousHash(code));
        Assert.Equal("replaced", (await Assert.ThrowsAsync<LinkException>(() => offer)).Code);
        Assert.Single(_relay.Frames(), f => f.Path == "/v1/pair");
    }

    [Fact]
    public async Task Pairing_code_runs_out()
    {
        var ex = await Assert.ThrowsAsync<LinkException>(() => _pc.PairAsync(PairingCode.New(), "Test PC", TimeSpan.FromMilliseconds(200), Ct));
        Assert.Equal("expired", ex.Code);
    }

    [Fact]
    public async Task Phone_gets_a_snapshot_and_runs_an_allowed_action()
    {
        var (pc, phone) = await PairAsync();
        var side = await ConnectPhone(pc);
        var snap = await side.RequestSnapshotAsync(Ct);
        Assert.Equal("snapshot", (string)snap["type"]!);
        Assert.Equal("Test PC", (string)snap["pc"]!);
        Assert.Equal(41.5, (double)snap["cpu"]!["tempC"]!);

        var r = await side.SendActionAsync("lighting.setEffect", new JsonObject { ["effect"] = "wave" }, Ct);
        Assert.True(r.Ok);
        Assert.True(_host.Ran.TryDequeue(out var ran));
        Assert.Equal("lighting.setEffect", ran!.Name);
        Assert.Equal("wave", (string)ran.Args["effect"]!);
        Assert.True(_host.Counters[phone.Id] > 0);
        // The PC reported the counters it numbered its answers with (stored as LastOut).
        Assert.True(_host.SentCounters[phone.Id] > 0);
        await WaitUntil(() => _pc.OnlinePhones.Contains(phone.Id));
    }

    [Fact]
    public async Task Refused_action_never_reaches_the_app()
    {
        var (pc, _) = await PairAsync();
        var side = await ConnectPhone(pc);
        var r = await side.SendActionAsync("cooling.setPumpDuty", new JsonObject { ["duty"] = 100 }, Ct);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.True(_host.Ran.IsEmpty);
    }

    [Fact]
    public async Task Action_failure_is_passed_back()
    {
        var (pc, _) = await PairAsync();
        var side = await ConnectPhone(pc);
        _host.NextResult = LinkActionResult.Fail("No recording is running.");
        var r = await side.SendActionAsync("capture.stop", null, Ct);
        Assert.False(r.Ok);
        Assert.Equal("No recording is running.", r.Error);
    }

    [Fact]
    public async Task Replayed_message_is_ignored_by_the_pc()
    {
        var (pc, phone) = await PairAsync();
        var side = await ConnectPhone(pc);
        Assert.True((await side.SendActionAsync("capture.start", null, Ct)).Ok);
        Assert.Single(_host.Ran);
        var sent = _relay.Frames().Where(f => f.Path == "/v1/connect").Select(f => JsonNode.Parse(f.Text)!.AsObject())
            .Last(o => (string?)o["t"] == "msg" && (string?)o["to"] == _pcKeys.Id);
        await _relay.InjectToPc(_pcKeys.Id, phone.Id, (string)sent["body"]!);
        await Task.Delay(200);
        Assert.Single(_host.Ran);
    }

    [Fact]
    public async Task Removed_phone_is_cut_off_and_refused()
    {
        var (pc, phone) = await PairAsync();
        var side = await ConnectPhone(pc);
        await WaitUntil(() => _relay.PhoneConnected(_pcKeys.Id, phone.Id));
        _host.Remove(phone.Id);
        Assert.True(await _pc.RefreshAllowListAsync(Ct));
        await WaitUntil(() => !_relay.PhoneConnected(_pcKeys.Id, phone.Id));
        // "removed" is final for a phone: it stops instead of being refused ("denied") again and again.
        await WaitUntil(() => side.LastError == "removed" && side.State == LinkConnectionState.Off);
        var connects = _relay.Connects;
        await Task.Delay(300);
        Assert.Equal(connects, _relay.Connects);
        Assert.Empty(_relay.AllowList(_pcKeys.Id));
    }

    [Fact]
    public async Task Both_ends_reconnect_after_the_relay_drops_them()
    {
        var (pc, phone) = await PairAsync();
        var side = await ConnectPhone(pc);
        await side.RequestSnapshotAsync(Ct);
        var before = _relay.Connects;
        _relay.DropAll();
        // Both ends connected again (two new connections), then both are online.
        await WaitUntil(() => _relay.Connects >= before + 2);
        await WaitUntil(() => _pc.State == LinkConnectionState.Online && side.State == LinkConnectionState.Online
                              && _relay.PhoneConnected(_pcKeys.Id, phone.Id));
        // The PC sent its allow-list again on the new connection.
        Assert.Contains(phone.Id, _relay.AllowList(_pcKeys.Id));
        var snap = await side.RequestSnapshotAsync(Ct);
        Assert.Equal("Test PC", (string)snap["pc"]!);
    }

    [Fact]
    public async Task Message_to_an_offline_pc_fails_fast()
    {
        var (pc, _) = await PairAsync();
        var side = await ConnectPhone(pc);
        await _pc.StopAsync();
        var ex = await Assert.ThrowsAsync<LinkException>(() => side.RequestSnapshotAsync(Ct));
        Assert.Equal("pcoffline", ex.Code);
    }

    [Fact]
    public async Task Phone_not_on_the_allow_list_is_refused()
    {
        using var stranger = DeviceKeys.Create();
        var fakePc = new LinkedPc(_pcKeys.Id, "x", _pcKeys.SigningPublicB64, _pcKeys.AgreementPublicB64, Relay.ToString(), SecureChannelTests.Epoch);
        var ex = await Assert.ThrowsAsync<LinkException>(() =>
            RelaySession.OpenAsync(_relay, Relay, "phone", fakePc.Id, stranger, Ct));
        Assert.Equal("denied", ex.Code);
    }

    [Fact]
    public async Task Urls_carry_the_room_and_h()
    {
        var (_, _) = await PairAsync();
        var urls = _relay.Urls.Select(u => u.ToString()).ToList();
        Assert.Contains("wss://relay.test/v1/connect?room=" + _pcKeys.Id, urls);
        Assert.Contains(urls, u => u.StartsWith("wss://relay.test/v1/pair?h=", StringComparison.Ordinal) && u.Length == "wss://relay.test/v1/pair?h=".Length + 43);
    }

    [Fact]
    public async Task Hello_frame_is_signed_over_the_exact_text()
    {
        // The relay checked it already (FakeRelay verifies every hello); check the frame's fields too.
        var hello = _relay.Frames().Select(f => JsonNode.Parse(f.Text)!.AsObject()).First(o => (string?)o["t"] == "hello");
        Assert.Equal(new[] { "t", "role", "room", "pub", "sig" }, hello.Select(p => p.Key));
        Assert.Equal("pc", (string)hello["role"]!);
        Assert.Equal(_pcKeys.Id, (string)hello["room"]!);
        Assert.Equal(_pcKeys.SigningPublicB64, (string)hello["pub"]!);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Allow_frame_is_sorted_and_signed()
    {
        using var a = DeviceKeys.Create();
        using var b = DeviceKeys.Create();
        _host.Phones.Add(new PhoneRecord(b.Id, "b", b.AgreementPublicB64, 0, SecureChannelTests.Epoch));
        _host.Phones.Add(new PhoneRecord(a.Id, "a", a.AgreementPublicB64, 0, SecureChannelTests.Epoch));
        Assert.True(await _pc.RefreshAllowListAsync(Ct));
        await WaitUntil(() => _relay.AllowList(_pcKeys.Id).Count == 2);
        var allow = _relay.Frames().Select(f => JsonNode.Parse(f.Text)!.AsObject()).Last(o => (string?)o["t"] == "allow");
        var ids = allow["phones"]!.AsArray().Select(x => (string)x!).ToArray();
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
        Assert.True(DeviceKeys.Verify(_pcKeys.SigningPublic, "rigcontrol-link-v1|allow|" + _pcKeys.Id + "|" + string.Join(",", ids), (string)allow["sig"]!));
    }

    [Fact]
    public async Task Held_back_messages_are_refused_as_stale()
    {
        var (pc, phone) = await PairAsync();
        var clock = new ManualTime(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(3));
        var side = await ConnectPhone(pc, time: clock);

        // Sealed 3 minutes ago (held back by the relay, or a slow clock): refused, nothing runs, LastIn stays.
        var r = await side.SendActionAsync("capture.start", null, Ct);
        Assert.False(r.Ok);
        Assert.Equal("stale", r.Code);
        Assert.Equal(LinkRefusals.StaleText, r.Error);
        Assert.True(_host.Ran.IsEmpty);
        Assert.False(_host.Counters.ContainsKey(phone.Id));
        var ex = await Assert.ThrowsAsync<LinkException>(() => side.RequestSnapshotAsync(Ct));
        Assert.Equal("stale", ex.Code);
        Assert.Equal(LinkRefusals.StaleText, ex.Message);

        // 30+ s ahead of the PC: refused too.
        clock.Set(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45));
        Assert.Equal("stale", (await side.SendActionAsync("capture.start", null, Ct)).Code);
        Assert.True(_host.Ran.IsEmpty);
        Assert.False(_host.Counters.ContainsKey(phone.Id));

        // Inside the window (100 s behind, then 20 s ahead): fine.
        await side.StopAsync();
        var side2 = await ConnectPhone(pc, time: new ManualTime(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(100)));
        Assert.True((await side2.SendActionAsync("capture.start", null, Ct)).Ok);
        await side2.StopAsync();
        var side3 = await ConnectPhone(pc, time: new ManualTime(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20)));
        Assert.True((await side3.SendActionAsync("capture.stop", null, Ct)).Ok);
        Assert.Equal(2, _host.Ran.Count);
    }

    [Fact]
    public async Task A_busy_action_still_has_its_counter_saved_first()
    {
        await RestartPc(new PcSideOptions { Relay = Fast, MaxRequestsPer10Seconds = 1 });
        var (pc, phone) = await PairAsync();
        var side = await ConnectPhone(pc);
        Assert.True((await side.SendActionAsync("capture.start", null, Ct)).Ok);
        var busy = await side.SendActionAsync("capture.stop", null, Ct);
        Assert.Equal("busy", busy.Code);
        Assert.Equal(LinkRefusals.BusyText, busy.Error);
        Assert.Single(_host.Ran);
        var saves = _host.Saves.ToArray();
        Assert.Equal(2, saves.Length);
        Assert.All(saves, s => Assert.True(s.SaveNow));
        Assert.Equal(saves[1].Counter, _host.Counters[phone.Id]);
        Assert.True(saves[1].Counter > saves[0].Counter);
    }

    [Fact]
    public async Task Repeated_presence_is_passed_on_only_when_it_changes()
    {
        var (_, phone) = await PairAsync();
        while (_host.Presence.TryDequeue(out _)) { }
        for (var i = 0; i < 10; i++)
            await _relay.InjectRawToPc(_pcKeys.Id, new JsonObject { ["t"] = "presence", ["id"] = phone.Id, ["online"] = true });
        await _relay.InjectRawToPc(_pcKeys.Id, new JsonObject { ["t"] = "presence", ["id"] = phone.Id, ["online"] = false });
        for (var i = 0; i < 10; i++)
            await _relay.InjectRawToPc(_pcKeys.Id, new JsonObject { ["t"] = "presence", ["id"] = phone.Id, ["online"] = false });
        await WaitUntil(() => _host.Presence.Count >= 2);
        await Task.Delay(200);
        Assert.Equal(new[] { (phone.Id, true), (phone.Id, false) }, _host.Presence.ToArray());
    }

    [Fact]
    public async Task Floods_of_refused_frames_are_logged_a_few_times_then_counted()
    {
        var log = new ConcurrentQueue<string>();
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await RestartPc(new PcSideOptions { Relay = Fast, Log = log.Enqueue, Time = clock });
        using var stranger = DeviceKeys.Create();
        for (var i = 0; i < 20; i++) await _relay.InjectToPc(_pcKeys.Id, stranger.Id, "AAAA");
        for (var i = 0; i < 20; i++)
            await _relay.InjectRawToPc(_pcKeys.Id, new JsonObject { ["t"] = "error", ["code"] = "badmsg" });
        await WaitUntil(() => log.Count(l => l.Contains("counted")) == 2);
        await Task.Delay(100);
        Assert.Equal(5, log.Count(l => l.Contains("not linked")));
        Assert.Equal(5, log.Count(l => l.StartsWith("The relay reported", StringComparison.Ordinal)));
        clock.Advance(TimeSpan.FromSeconds(61));
        await _relay.InjectToPc(_pcKeys.Id, stranger.Id, "AAAA");
        await WaitUntil(() => log.Any(l => l.StartsWith("15 more", StringComparison.Ordinal)));
        Assert.Equal(6, log.Count(l => l.Contains("not linked")));
    }

    [Fact]
    public async Task Concurrent_sends_to_one_peer_all_arrive()
    {
        var (pc, phone) = await PairAsync();
        var side = await ConnectPhone(pc);
        // 12 requests at once from the phone: none overtakes another, so none is refused as a replay.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => side.SendActionAsync("capture.saveReplay", null, Ct))));
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(12, _host.Ran.Count);

        // 12 events at once from the PC: the phone opens every one.
        var events = new ConcurrentQueue<JsonObject>();
        side.EventReceived += events.Enqueue;
        await WaitUntil(() => _pc.OnlinePhones.Contains(phone.Id));
        var sent = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => _pc.SendEventAsync(phone.Id, "test", new JsonObject { ["i"] = i }, Ct))));
        Assert.All(sent, Assert.True);
        await WaitUntil(() => events.Count == 12);
    }
}
