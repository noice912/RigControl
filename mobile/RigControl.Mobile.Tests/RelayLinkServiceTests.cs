using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using RigControl.Link.Tests;
using RigControl.Mobile.Core.Link;
using Lib = RigControl.Link;

namespace RigControl.Mobile.Tests;

/// <summary>A PC app as RigControl.Link's PcSide sees it, answering with the snapshot shape RigControl sends.</summary>
internal sealed class PcHost : Lib.IPcLinkHost
{
    public readonly List<Lib.PhoneRecord> Phones = [];
    public readonly ConcurrentQueue<Lib.LinkAction> Ran = new();
    public Lib.LinkActionResult NextResult = Lib.LinkActionResult.Success;

    public const string SnapshotJson = """
        {"pc":"DESK","time":"2026-11-26T12:00:00Z",
         "sensors":{"time":"x","cpu":{"name":"CPU","tempC":52.5,"loadPercent":31,"powerW":60},
                    "gpu":{"name":"GPU","tempC":44,"loadPercent":7.5},
                    "memory":{"loadPercent":40},
                    "fans":[{"name":"Pump","rpm":2400},{"name":"Rear","rpm":760.4}],
                    "temps":[{"name":"Motherboard","c":35},{"name":"Liquid","c":31.2}]},
         "recording":{"available":true,"recording":true,"since":"2026-11-26T11:58:00Z","replayRunning":true},
         "lighting":{"on":true,"effect":"wave","status":"x","color":"#ff0000","effects":[{"id":"wave","name":"Wave"},{"id":"solid","name":"Solid"}]},
         "presence":{"status":"ok","playing":null,"music":{"title":"Song\u0007","artist":"Band","player":"Spotify","playing":true},
                     "custom":[{"id":"abc","label":"Working","on":false}]}}
        """;

    public IReadOnlyList<Lib.PhoneRecord> LinkedPhones()
    {
        lock (Phones) return Phones.ToList();
    }

    public void Add(Lib.PairedPhone p)
    {
        lock (Phones) Phones.Add(new Lib.PhoneRecord(p.Id, p.Name, p.AgreementKey, 0, p.Epoch));
    }

    public void Remove(string id)
    {
        lock (Phones) Phones.RemoveAll(p => p.Id == id);
    }

    public Task<JsonObject> SnapshotAsync(Lib.PhoneRecord phone, CancellationToken ct) =>
        Task.FromResult((JsonObject)JsonNode.Parse(SnapshotJson)!);

    public Task<Lib.LinkActionResult> RunAsync(Lib.PhoneRecord phone, Lib.LinkAction action, CancellationToken ct)
    {
        Ran.Enqueue(action);
        return Task.FromResult(NextResult);
    }

    public void Accepted(string phoneId, long counter) { }

    public void PresenceChanged(string phoneId, bool online) { }
}

internal sealed class ListProgress : IProgress<string>
{
    public readonly List<string> Steps = [];

    public void Report(string value)
    {
        lock (Steps) Steps.Add(value);
    }
}

/// <summary>A network with no relay on it.</summary>
internal sealed class DeadTransport : Lib.IRelayTransport
{
    public Task<Lib.IRelayConnection> ConnectAsync(Uri uri, CancellationToken ct) =>
        Task.FromException<Lib.IRelayConnection>(new WebSocketException("Unable to connect to the remote server"));
}

/// <summary>
/// RelayLinkService against a real PcSide through RigControl.Link's in-memory relay: pairing, storage, snapshot,
/// actions, presence, reconnects and the error texts.
/// </summary>
public sealed class RelayLinkServiceTests : IAsyncLifetime
{
    private const string Host = "relay.test";
    private static readonly Uri RelayUri = new("wss://relay.test");
    private static readonly Lib.RelayLinkOptions Fast = new() { Backoff = _ => TimeSpan.FromMilliseconds(20) };

    private readonly FakeRelay _relay = new();
    private readonly PcHost _host = new();
    private readonly Lib.DeviceKeys _pcKeys = Lib.DeviceKeys.Create();
    private readonly InMemoryLinkStore _store = new();
    private readonly List<IAsyncDisposable> _cleanup = [];
    private Lib.PcSide _pc = null!;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    public async Task InitializeAsync() => _pc = await StartPc();

    public async Task DisposeAsync()
    {
        foreach (var c in _cleanup)
            await c.DisposeAsync();
        await _pc.DisposeAsync();
    }

    private async Task<Lib.PcSide> StartPc(int maxRequestsPer10Seconds = 30)
    {
        var pc = new Lib.PcSide(_relay, RelayUri, _pcKeys, _host,
            new Lib.PcSideOptions { Relay = Fast, MaxRequestsPer10Seconds = maxRequestsPer10Seconds });
        pc.Start();
        await pc.WaitOnlineAsync(Ct);
        return pc;
    }

    private RelayLinkService NewService(Lib.IRelayTransport? transport = null, TimeProvider? time = null)
    {
        var s = new RelayLinkService(transport ?? _relay, _store, new RelayLinkServiceOptions
        {
            Time = time ?? TimeProvider.System,
            Relay = Fast,
            ReplyTimeout = TimeSpan.FromSeconds(3),
            ConnectWait = TimeSpan.FromSeconds(5),
            ProbeEvery = TimeSpan.FromMilliseconds(100),
            PairTimeout = TimeSpan.FromSeconds(5),
            SaveEvery = TimeSpan.Zero,
        });
        _cleanup.Add(s);
        return s;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition did not become true.");
            await Task.Delay(20);
        }
    }

    private static PairingCode Code(string text)
    {
        Assert.True(PairingCode.TryParse(text, out var code));
        return code;
    }

    private async Task<(LinkedPc Pc, Lib.PairedPhone Phone, ListProgress Progress)> Pair(RelayLinkService svc)
    {
        var code = Lib.PairingCode.New();
        var offer = _pc.PairAsync(code, "Desk PC", TimeSpan.FromMinutes(10), Ct);
        await WaitUntil(() => _relay.Frames().Any(f => f.Path == "/v1/pair"));
        var progress = new ListProgress();
        var result = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.Display(code)), Host, "Test phone"), progress, Ct);
        Assert.True(result.Ok, result.Error);
        var phone = await offer;
        _host.Add(phone);
        Assert.True(await _pc.RefreshAllowListAsync(Ct));
        await WaitUntil(() => _relay.AllowList(_pcKeys.Id).Contains(phone.Id));
        return (result.Pc!, phone, progress);
    }

    [Fact]
    public async Task Pairs_connects_reads_the_snapshot_and_runs_actions()
    {
        var svc = NewService();
        var (pc, phone, progress) = await Pair(svc);

        Assert.Equal(_pcKeys.Id, pc.Id);
        Assert.Equal("Desk PC", pc.Name);
        Assert.Equal("relay.test", pc.Relay);
        Assert.Equal("Linked to Desk PC.", progress.Steps.Last());
        Assert.Equal("Test phone", phone.Name);
        Assert.True(_store.HasKey(pc.Id));
        var listed = Assert.Single(await svc.GetPcsAsync());
        Assert.Equal(pc.Id, listed.Id);

        await svc.ConnectAsync(pc.Id, Ct);
        var s = await svc.GetSnapshotAsync(pc.Id, Ct);
        Assert.Equal(52.5, s.CpuTempC);
        Assert.Equal(44, s.GpuTempC);
        Assert.Equal(31.2, s.LiquidTempC);
        Assert.Equal(31, s.CpuLoad);
        Assert.Equal([new FanReading("Pump", 2400), new FanReading("Rear", 760)], s.Fans);
        Assert.True(s.Recording.IsRecording);
        Assert.True(s.Recording.ReplayBufferOn);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 11, 58, 0, TimeSpan.Zero), s.Recording.RecordingSince);
        Assert.True(s.Lighting.On);
        Assert.Equal("wave", s.Lighting.Effect);
        Assert.Equal(["wave", "solid"], s.Lighting.Effects.Select(e => e.Id));
        Assert.Equal("Song – Band", s.NowPlaying); // the bell character is dropped
        Assert.Equal(new CustomStatus("abc", "Working", false), Assert.Single(s.CustomStatuses));

        Assert.True((await svc.DoAsync(pc.Id, PcAction.SetEffect("solid"), Ct)).Ok);
        Assert.True((await svc.DoAsync(pc.Id, PcAction.SetCustomStatus("abc", true), Ct)).Ok);
        Assert.True((await svc.DoAsync(pc.Id, PcAction.SaveReplay(), Ct)).Ok);
        var ran = _host.Ran.ToArray();
        Assert.Equal("lighting.setEffect", ran[0].Name);
        Assert.Equal("solid", (string)ran[0].Args["effect"]!);
        Assert.Equal("presence.setCustom", ran[1].Name);
        Assert.True((bool)ran[1].Args["on"]!);
        Assert.Equal("capture.saveReplay", ran[2].Name);

        _host.NextResult = Lib.LinkActionResult.Fail("Not recording.");
        var refused = await svc.DoAsync(pc.Id, PcAction.StopRecording(), Ct);
        Assert.False(refused.Ok);
        Assert.Equal("Not recording.", refused.Error);
        await svc.DisconnectAsync(pc.Id);
    }

    /// <summary>Puts an entry for this test's PC id in the store, with other keys (or another relay) and its own phone key.</summary>
    private async Task<(byte[] OldSecret, string OldAgreement)> SeedLinkedPc(string relay = "wss://relay.test/", bool otherKeys = true)
    {
        using var other = Lib.DeviceKeys.Create();
        using var oldPhone = Lib.DeviceKeys.Create();
        var agreement = otherKeys ? other.AgreementPublicB64 : _pcKeys.AgreementPublicB64;
        var json = StoredPc.Serialize([new StoredPc
        {
            Id = _pcKeys.Id, Name = "My real PC", SigningKey = _pcKeys.SigningPublicB64, AgreementKey = agreement,
            Relay = relay, Epoch = Lib.B64Url.Encode(new byte[16]), PhoneId = oldPhone.Id, LinkedAt = DateTimeOffset.UnixEpoch,
        }]);
        await _store.SaveListAsync(json);
        var secret = oldPhone.ExportPrivate();
        await _store.SaveKeyAsync(_pcKeys.Id, secret);
        return (secret, agreement);
    }

    private async Task<PairResult> PairRaw(RelayLinkService svc)
    {
        var code = Lib.PairingCode.New();
        var offer = _pc.PairAsync(code, "Evil PC", TimeSpan.FromMinutes(10), Ct);
        await WaitUntil(() => _relay.Frames().Any(f => f.Path == "/v1/pair"));
        var result = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.Display(code)), Host, "Test phone"), null, Ct);
        await offer;
        return result;
    }

    [Fact]
    public async Task A_PC_already_linked_with_other_keys_is_kept_until_the_person_confirms()
    {
        var (oldSecret, oldAgreement) = await SeedLinkedPc();
        var svc = NewService();
        var r = await PairRaw(svc);

        Assert.False(r.Ok);
        Assert.True(r.NeedsConfirm);
        Assert.Equal("This QR claims to be My real PC, which is already linked with different keys. Replace it?", r.Error);
        // Nothing changed: the old entry and the old keys are still there, in memory and in the store.
        var listed = Assert.Single(await svc.GetPcsAsync());
        Assert.Equal("My real PC", listed.Name);
        Assert.Equal(oldSecret, await _store.LoadKeyAsync(_pcKeys.Id));
        Assert.Contains(oldAgreement, await _store.LoadListAsync());

        // Keeping the old link: the waiting keys are dropped, and the id can't be confirmed later.
        svc.DiscardReplace(r.ReplaceId!);
        Assert.False((await svc.ConfirmReplaceAsync(r.ReplaceId!, Ct)).Ok);
        Assert.Equal(oldSecret, await _store.LoadKeyAsync(_pcKeys.Id));
        Assert.Equal("My real PC", Assert.Single(await svc.GetPcsAsync()).Name);
    }

    [Fact]
    public async Task Confirming_replaces_the_linked_PC_and_its_keys()
    {
        var (oldSecret, _) = await SeedLinkedPc();
        var svc = NewService();
        var r = await PairRaw(svc);
        Assert.True(r.NeedsConfirm);

        var done = await svc.ConfirmReplaceAsync(r.ReplaceId!, Ct);
        Assert.True(done.Ok, done.Error);
        Assert.Equal("Evil PC", done.Pc!.Name);
        Assert.Equal("Evil PC", Assert.Single(await svc.GetPcsAsync()).Name);
        Assert.NotEqual(oldSecret, await _store.LoadKeyAsync(_pcKeys.Id));
        Assert.Contains(_pcKeys.AgreementPublicB64, await _store.LoadListAsync());
        // Used once.
        Assert.False((await svc.ConfirmReplaceAsync(r.ReplaceId!, Ct)).Ok);
    }

    [Fact]
    public async Task A_replace_answer_after_five_minutes_is_refused()
    {
        var (oldSecret, _) = await SeedLinkedPc();
        var clock = new StepClock();
        var svc = NewService(time: clock);
        var r = await PairRaw(svc);
        Assert.True(r.NeedsConfirm);
        clock.Add(TimeSpan.FromMinutes(6));
        Assert.False((await svc.ConfirmReplaceAsync(r.ReplaceId!, Ct)).Ok);
        Assert.Equal(oldSecret, await _store.LoadKeyAsync(_pcKeys.Id));
    }

    private sealed class StepClock : TimeProvider
    {
        private TimeSpan _offset;
        public void Add(TimeSpan by) => _offset += by;
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + _offset;
    }

    [Fact]
    public async Task A_PC_already_linked_through_another_relay_also_asks()
    {
        await SeedLinkedPc(relay: "wss://other-relay.example/", otherKeys: false);
        var svc = NewService();
        var r = await PairRaw(svc);
        Assert.True(r.NeedsConfirm);
        Assert.Equal("other-relay.example", Assert.Single(await svc.GetPcsAsync()).Relay);
    }

    [Fact]
    public async Task The_same_PC_linked_again_with_the_same_keys_asks_first()
    {
        var (oldSecret, _) = await SeedLinkedPc(otherKeys: false);
        var svc = NewService();
        var r = await PairRaw(svc);
        Assert.True(r.NeedsConfirm);
        Assert.Equal("My real PC is already linked. Link it again? Its old link on this phone is replaced.", r.Error);
        Assert.Equal(oldSecret, await _store.LoadKeyAsync(_pcKeys.Id));
        var done = await svc.ConfirmReplaceAsync(r.ReplaceId!, Ct);
        Assert.True(done.Ok, done.Error);
        Assert.NotEqual(oldSecret, await _store.LoadKeyAsync(_pcKeys.Id));
    }

    [Fact]
    public async Task A_wrong_or_expired_code_says_so()
    {
        var svc = NewService();
        var r = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.New()), Host, "Phone"), null, Ct);
        Assert.False(r.Ok);
        Assert.Contains("code didn't work", r.Error);
        Assert.Empty(await svc.GetPcsAsync());
    }

    [Fact]
    public async Task The_placeholder_relay_is_refused_before_anything_is_sent()
    {
        var svc = NewService();
        var before = _relay.Connects;
        var r = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.New()), "relay.invalid", "Phone"), null, Ct);
        Assert.False(r.Ok);
        Assert.Equal(MobileDefaults.RelayNotSetMessage, r.Error);
        Assert.Equal(before, _relay.Connects);
        Assert.True(MobileDefaults.IsPlaceholder("relay.invalid:8787"));
        Assert.False(MobileDefaults.IsPlaceholder("relay.example.com"));
        Assert.Equal("relay.invalid", MobileDefaults.HostOf("wss://relay.invalid"));
        Assert.Equal("relay.invalid", MobileDefaults.HostOf("ws://localhost:8787"));
    }

    [Fact]
    public void The_default_relay_is_the_one_the_PC_uses()
    {
        Assert.Equal("rigcontrol-relay.rigcontrol.workers.dev", MobileDefaults.RelayHost);
        Assert.Equal("wss://" + MobileDefaults.RelayHost, Lib.LinkDefaults.RelayUrl);
        Assert.True(MobileDefaults.HasRelay);
    }

    [Fact]
    public async Task A_typed_code_goes_to_the_default_relay_through_the_given_transport_only()
    {
        // The in-memory relay stands in for the deployed one: nothing here goes on the network.
        var svc = NewService();
        var r = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.New()), MobileDefaults.RelayHost, "Phone"), null, Ct);
        Assert.False(r.Ok); // no PC offered that code
        var url = Assert.Single(_relay.Urls, u => u.AbsolutePath == "/v1/pair");
        Assert.Equal("wss", url.Scheme);
        Assert.Equal("rigcontrol-relay.rigcontrol.workers.dev", url.Host);
    }

    [Theory]
    [InlineData("ws://localhost:8787")]
    [InlineData("ws://relay.example.com")]
    [InlineData("https://relay.example.com")]
    public async Task Only_wss_relays_are_used(string relay)
    {
        var svc = NewService();
        var before = _relay.Connects;
        var r = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.New()), relay, "Phone"), null, Ct);
        Assert.False(r.Ok);
        Assert.Equal("That relay address can't be used.", r.Error);
        Assert.Equal(before, _relay.Connects);
    }

    [Fact]
    public void A_stored_PC_with_a_non_wss_relay_is_dropped()
    {
        var json = StoredPc.Serialize(
        [
            new StoredPc { Id = "a", AgreementKey = "k", Epoch = "e", Relay = "wss://relay.test/" },
            new StoredPc { Id = "b", AgreementKey = "k", Epoch = "e", Relay = "ws://localhost:8787/" },
        ]);
        Assert.Equal("a", Assert.Single(StoredPc.Deserialize(json)).Id);
    }

    [Fact]
    public async Task An_unreachable_relay_says_so()
    {
        var svc = NewService(new DeadTransport());
        var r = await svc.PairAsync(new PairingRequest(Code(Lib.PairingCode.New()), "relay.example.com", "Phone"), null, Ct);
        Assert.False(r.Ok);
        Assert.StartsWith("Couldn't reach the relay at relay.example.com", r.Error);
    }

    [Fact]
    public async Task Keys_and_counters_survive_a_restart_of_the_app()
    {
        var first = NewService();
        var (pc, _, _) = await Pair(first);
        await first.ConnectAsync(pc.Id, Ct);
        await first.GetSnapshotAsync(pc.Id, Ct);
        await first.DisconnectAsync(pc.Id);
        await first.DisposeAsync();

        var stored = Assert.Single(StoredPc.Deserialize(await _store.LoadListAsync()));
        Assert.True(stored.LastIn > 0);
        Assert.NotNull(stored.LastSeen);
        Assert.DoesNotContain("PRIVATE", await _store.LoadListAsync() ?? "", StringComparison.OrdinalIgnoreCase);

        var second = NewService();
        var listed = Assert.Single(await second.GetPcsAsync());
        Assert.Equal("Desk PC", listed.Name);
        await second.ConnectAsync(pc.Id, Ct);
        Assert.Equal(52.5, (await second.GetSnapshotAsync(pc.Id, Ct)).CpuTempC);
    }

    [Fact]
    public async Task Watching_sees_the_PC_go_offline_and_come_back()
    {
        var svc = NewService();
        var (pc, _, _) = await Pair(svc);
        var presence = new ConcurrentQueue<PcPresenceChanged>();
        svc.EventReceived += (_, e) =>
        {
            if (e is PcPresenceChanged p)
                presence.Enqueue(p);
        };
        using var watch = new CancellationTokenSource();
        var watching = svc.WatchAsync(watch.Token);

        await WaitUntil(() => presence.Any(p => p.Online));
        Assert.True((await svc.GetPcsAsync())[0].Online);

        await _pc.StopAsync();
        await WaitUntil(() => presence.LastOrDefault() is { Online: false });
        Assert.False((await svc.GetPcsAsync())[0].Online);
        await svc.ConnectAsync(pc.Id, Ct);
        var ex = await Assert.ThrowsAsync<LinkUnavailableException>(() => svc.GetSnapshotAsync(pc.Id, Ct));
        Assert.Contains("Desk PC is offline", ex.Message);
        Assert.Contains("offline", (await svc.DoAsync(pc.Id, PcAction.LightsOn(), Ct)).Error);
        await svc.DisconnectAsync(pc.Id);

        await _pc.DisposeAsync();
        _pc = await StartPc();
        await WaitUntil(() => presence.LastOrDefault() is { Online: true });

        watch.Cancel();
        await watching;
    }

    [Fact]
    public async Task A_phone_clock_far_off_gets_the_clock_sentence()
    {
        var pairing = NewService();
        var (pc, _, _) = await Pair(pairing);
        await pairing.DisposeAsync();

        // The same phone data, with a clock 3 minutes behind the PC's: the PC refuses with "stale".
        var svc = NewService(time: new ManualTime(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(3)));
        await svc.ConnectAsync(pc.Id, Ct);
        var ex = await Assert.ThrowsAsync<LinkUnavailableException>(() => svc.GetSnapshotAsync(pc.Id, Ct));
        Assert.Equal("This phone's clock doesn't match the PC's. Set both to set the time automatically.", ex.Message);
        var r = await svc.DoAsync(pc.Id, PcAction.LightsOn(), Ct);
        Assert.False(r.Ok);
        Assert.Equal(LinkErrors.Stale, r.Error);
        Assert.True(_host.Ran.IsEmpty);
        await svc.DisconnectAsync(pc.Id);
    }

    [Fact]
    public async Task A_busy_PC_gets_the_busy_sentence()
    {
        await _pc.DisposeAsync();
        _pc = await StartPc(maxRequestsPer10Seconds: 1);
        var svc = NewService();
        var (pc, _, _) = await Pair(svc);
        await svc.ConnectAsync(pc.Id, Ct);
        // One request per 10 s (the connect's own check may take it): the third one is refused for sure.
        await svc.DoAsync(pc.Id, PcAction.LightsOn(), Ct);
        await svc.DoAsync(pc.Id, PcAction.LightsOn(), Ct);
        var r = await svc.DoAsync(pc.Id, PcAction.LightsOn(), Ct);
        Assert.False(r.Ok);
        Assert.Equal("The PC is busy; try again in a moment.", r.Error);
        var ex = await Assert.ThrowsAsync<LinkUnavailableException>(() => svc.GetSnapshotAsync(pc.Id, Ct));
        Assert.Equal(LinkErrors.Busy, ex.Message);
        await svc.DisconnectAsync(pc.Id);
    }

    [Theory]
    [InlineData("stale", LinkErrors.Stale)]
    [InlineData("busy", LinkErrors.Busy)]
    [InlineData("unavailable", null)]
    [InlineData(null, null)]
    public void Refusal_codes_have_fixed_wording(string? code, string? expected) => Assert.Equal(expected, LinkErrors.ForRefusal(code));
    [Fact]
    public async Task Requests_made_at_the_same_time_all_get_answers()
    {
        // Counters are taken when sealing; sent in the wrong order, the PC would refuse the older one as a replay.
        var svc = NewService();
        var (pc, _, _) = await Pair(svc);
        await svc.ConnectAsync(pc.Id, Ct);
        var all = Enumerable.Range(0, 12).Select(i => i % 3 == 0
            ? svc.DoAsync(pc.Id, PcAction.LightsOn(), Ct).ContinueWith(t => t.Result.Ok)
            : svc.GetSnapshotAsync(pc.Id, Ct).ContinueWith(t => t.Result.CpuTempC == 52.5)).ToArray();
        Assert.All(await Task.WhenAll(all), Assert.True);
        await svc.DisconnectAsync(pc.Id);
    }

    [Fact]
    public async Task Reconnects_after_the_relay_drops_everything()
    {
        var svc = NewService();
        var (pc, _, _) = await Pair(svc);
        await svc.ConnectAsync(pc.Id, Ct);
        await svc.GetSnapshotAsync(pc.Id, Ct);
        var connects = _relay.Connects;

        _relay.DropAll();
        await WaitUntil(() => _relay.Connects >= connects + 2); // the PC and the phone both came back
        PcSnapshot? s = null;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (s is null && DateTime.UtcNow < until)
        {
            try { s = await svc.GetSnapshotAsync(pc.Id, Ct); }
            catch (LinkUnavailableException) { await Task.Delay(50); }
        }
        Assert.NotNull(s);
        await svc.DisconnectAsync(pc.Id);
    }

    [Fact]
    public async Task A_phone_removed_on_the_PC_is_told_to_link_again()
    {
        var svc = NewService();
        var (pc, phone, _) = await Pair(svc);
        await svc.ConnectAsync(pc.Id, Ct);
        await svc.GetSnapshotAsync(pc.Id, Ct);

        _host.Remove(phone.Id);
        await _pc.RefreshAllowListAsync(Ct);
        await WaitUntil(() => svc.GetPcsAsync().Result[0].Problem is not null);
        Assert.Contains("link it again", (await svc.GetPcsAsync())[0].Problem);
        await svc.DisconnectAsync(pc.Id);
    }

    [Fact]
    public async Task Forgetting_deletes_the_keys_and_the_entry()
    {
        var svc = NewService();
        var (pc, _, _) = await Pair(svc);
        await svc.ConnectAsync(pc.Id, Ct);
        await svc.ForgetAsync(pc.Id);
        Assert.Empty(await svc.GetPcsAsync());
        Assert.False(_store.HasKey(pc.Id));
        Assert.Empty(StoredPc.Deserialize(await _store.LoadListAsync()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.ConnectAsync(pc.Id, Ct));
    }

    [Fact]
    public async Task Lost_keys_are_reported_not_thrown_around()
    {
        var svc = NewService();
        var (pc, _, _) = await Pair(svc);
        await _store.DeleteKeyAsync(pc.Id);
        var ex = await Assert.ThrowsAsync<LinkUnavailableException>(() => svc.ConnectAsync(pc.Id, Ct));
        Assert.Contains("keys for this PC are missing", ex.Message);
        Assert.NotNull((await svc.GetPcsAsync())[0].Problem);
    }
}

public class SnapshotReaderTests
{
    [Theory]
    [InlineData("Evil\u202Egnp.exe", "Evilgnp.exe")] // right-to-left override
    [InlineData("A\u200Bdmin\u200D", "Admin")] // zero-width space / joiner
    [InlineData("Line\u2028Two\u2029", "LineTwo")] // line / paragraph separator
    [InlineData("\u2066\u2069\uFEFF", null)] // only invisible characters
    [InlineData("Tab\tNew\nLine", "TabNewLine")]
    public void Text_from_the_PC_loses_invisible_and_control_characters(string raw, string? expected) =>
        Assert.Equal(expected, SnapshotReader.Text(JsonValue.Create(raw), 80));

    [Fact]
    public void Text_is_cut_without_splitting_a_pair() =>
        Assert.Equal("abc", SnapshotReader.Text(JsonValue.Create("abc\U0001F600"), 4));

    [Fact]
    public void Missing_and_wrong_values_are_left_empty()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = SnapshotReader.Read((JsonObject)JsonNode.Parse("""
            {"sensors":{"cpu":{"tempC":"hot","loadPercent":250},"fans":[{"name":"A"},{"rpm":5},{"name":"B","rpm":900}]},
             "lighting":{"on":"yes","effects":[{"name":"no id"},{"id":"rain"}]},"presence":{"playing":"Game\n"}}
            """)!, now);
        Assert.Equal(now, s.TakenAt);
        Assert.Null(s.CpuTempC);
        Assert.Equal(100, s.CpuLoad);
        Assert.Equal([new FanReading("B", 900)], s.Fans);
        Assert.False(s.Lighting.On);
        Assert.Equal(new LightingEffect("rain", "rain"), Assert.Single(s.Lighting.Effects));
        Assert.Equal("Game", s.NowPlaying);
        Assert.False(s.Recording.IsRecording);
    }

    [Fact]
    public void Custom_status_args_carry_a_boolean()
    {
        var args = PcAction.SetCustomStatus("abc", false).ToJsonArgs();
        Assert.Equal("""{"id":"abc","on":false}""", args.ToJsonString());
        Assert.Equal("""{"effect":"wave"}""", PcAction.SetEffect("wave").ToJsonArgs().ToJsonString());
    }
}
