using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.App.Link;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Link;

namespace RigControl.App.Tests;

public class LinkModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-link-" + Guid.NewGuid().ToString("N"));

    public LinkModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    /// <summary>Records every call; answers presence.getStatus with two custom statuses.</summary>
    private sealed class FakeModule : IRpcModule
    {
        public FakeModule(string name) => Name = name;
        public string Name { get; }
        public List<(string Method, string? Args)> Calls { get; } = new();
        public bool LongText { get; set; }
        public string? FailWith { get; set; }

        public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
        {
            Calls.Add((method, args?.GetRawText()));
            if (FailWith is { } why) throw RpcException.Unavailable(why);
            if (Name == "presence" && method == "getStatus")
            {
                var o = JsonNode.Parse("""
                    {"status":"Off","settings":{"customStatuses":[{"id":"a","enabled":false,"name":"Coding"},{"id":"b","enabled":true,"name":"AFK"}]}}
                    """)!.AsObject();
                if (LongText)
                {
                    var big = new string('x', 5000);
                    o["status"] = big;
                    o["playing"] = big;
                    o["music"] = new JsonObject { ["title"] = big, ["artist"] = big, ["player"] = big, ["playing"] = new JsonObject { ["x"] = big } };
                }
                return Task.FromResult<object?>(o);
            }
            return Task.FromResult<object?>(null);
        }
    }

    // Tests never talk to the real relay: they run as a build without a relay address.
    private (LinkModule Module, Dictionary<string, FakeModule> Fakes, List<string> Notices, SettingsStore Settings) Make(
        LinkSettings? seed = null, string relayUrl = "wss://relay.invalid")
    {
        var dispatcher = new RpcDispatcher(NullLog.Instance);
        var fakes = new[] { "lighting", "capture", "presence", "cooling" }.ToDictionary(n => n, n => new FakeModule(n));
        foreach (var f in fakes.Values) dispatcher.Register(f);
        var notices = new List<string>();
        var bus = new EventBus();
        Bus = bus;
        var m = new LinkModule(dispatcher, bus, (t, s) => notices.Add(s), buildRelayUrl: relayUrl);
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance);
        seed ??= new LinkSettings();
        seed.RelayUrl = relayUrl;
        settings.Set(LinkSettings.Section, seed);
        var env = new AppEnvironment(_dir, _dir, false, "test", "");
        m.StartAsync(new ModuleContext(bus, NullLog.Instance, settings, env), default).GetAwaiter().GetResult();
        return (m, fakes, notices, settings);
    }

    private EventBus Bus = null!;

    private static PhoneRecord Phone => new("id", "Pixel", "", 0, "AAECAwQFBgcICQoLDA0ODw");

    [Theory]
    [InlineData("lighting.setEffect", """{"effect":"wave"}""", "lighting", "setSync", """{"enabled":true,"effect":"wave"}""")]
    [InlineData("lighting.setEffect", """{"scene":"Movie"}""", "lighting", "applyScene", """{"name":"Movie"}""")]
    [InlineData("lighting.on", null, "lighting", "setSync", """{"enabled":true}""")]
    [InlineData("lighting.off", null, "lighting", "off", null)]
    [InlineData("capture.start", null, "capture", "startRecording", null)]
    [InlineData("capture.stop", null, "capture", "stopRecording", null)]
    [InlineData("capture.saveReplay", null, "capture", "saveReplay", null)]
    public async Task Allowed_actions_run_through_the_dispatcher_and_show_a_notice(string action, string? args, string module, string method, string? rpcArgs)
    {
        var (m, fakes, notices, _) = Make();
        var checkedAction = LinkActions.Check(action, args is null ? null : JsonNode.Parse(args), out _)!;
        var r = await m.RunAsync(Phone, checkedAction, default);
        Assert.True(r.Ok, r.Error);
        var call = Assert.Single(fakes[module].Calls);
        Assert.Equal(method, call.Method);
        Assert.Equal(rpcArgs, call.Args);
        Assert.Single(notices);
        Assert.StartsWith("Pixel ", notices[0]);
        Assert.Empty(fakes["cooling"].Calls);
    }

    [Fact]
    public async Task Set_custom_switches_only_that_status()
    {
        var (m, fakes, notices, _) = Make();
        var a = LinkActions.Check("presence.setCustom", JsonNode.Parse("""{"id":"a","on":true}"""), out _)!;
        Assert.True((await m.RunAsync(Phone, a, default)).Ok);
        var set = fakes["presence"].Calls.Single(c => c.Method == "setCustomStatuses");
        var items = JsonNode.Parse(set.Args!)!["items"]!.AsArray();
        Assert.True((bool)items[0]!["enabled"]!);
        Assert.True((bool)items[1]!["enabled"]!);
        Assert.Equal("Coding", (string)items[0]!["name"]!);
        Assert.Single(notices);

        var missing = LinkActions.Check("presence.setCustom", JsonNode.Parse("""{"id":"zzz","on":true}"""), out _)!;
        Assert.False((await m.RunAsync(Phone, missing, default)).Ok);
    }

    [Fact]
    public async Task Without_a_relay_nothing_connects_and_pairing_says_why()
    {
        var (m, _, _, settings) = Make();
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        var status = (LinkStatusDto)(await m.InvokeAsync("getStatus", null, default))!;
        Assert.False(status.Enabled);
        Assert.False(status.RelaySet);
        Assert.Equal("off", status.Connection);
        Assert.Contains("not set in this build", status.Message);
        var ex = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("startPairing", null, default));
        Assert.Equal(RpcErrorCodes.Unavailable, ex.Code);
        // No keys were made either: nothing about the link is written before it is used.
        Assert.Equal("", settings.Get<LinkSettings>(LinkSettings.Section).DeviceKey);
        // The release build's own address is a real one.
        Assert.False(LinkDefaults.IsPlaceholder(new LinkSettings().RelayUrl));
    }

    [Fact]
    public async Task Snapshot_has_the_expected_parts()
    {
        var (m, _, _, _) = Make();
        var snap = await m.SnapshotAsync(Phone, default);
        Assert.Equal(new[] { "pc", "time", "sensors", "recording", "lighting", "presence" }, snap.Select(p => p.Key));
        var custom = snap["presence"]!["custom"]!.AsArray();
        Assert.Equal(2, custom.Count);
        Assert.Equal("Coding", (string)custom[0]!["label"]!);
        Assert.True(Encoding(snap).Length < LinkProtocol.MaxFrameBytes);
    }

    private static string Encoding(JsonObject o) => o.ToJsonString();

    [Fact]
    public async Task Snapshot_strings_from_other_modules_are_cut()
    {
        var (m, fakes, _, _) = Make();
        fakes["presence"].LongText = true;
        var snap = await m.SnapshotAsync(Phone, default);
        var p = snap["presence"]!;
        Assert.Equal(64, ((string)p["status"]!).Length);
        Assert.Equal(96, ((string)p["playing"]!).Length);
        Assert.Equal(96, ((string)p["music"]!["title"]!).Length);
        Assert.Equal(96, ((string)p["music"]!["artist"]!).Length);
        Assert.Equal(48, ((string)p["music"]!["player"]!).Length);
        Assert.Null(p["music"]!["playing"]); // not a boolean: left out
        Assert.True(Encoding(snap).Length < 4096);
    }

    [Fact]
    public void Cut_never_splits_a_surrogate_pair()
    {
        var s = new string('a', 47) + "😀";
        Assert.Equal(new string('a', 47), LinkModule.Cut(s, 48));
        Assert.Equal(s, LinkModule.Cut(s, 49));
    }

    [Fact]
    public async Task Status_events_are_folded_to_a_few_per_second()
    {
        var (m, _, _, _) = Make();
        var count = 0;
        Bus.Published += (topic, _) =>
        {
            if (topic == "link.status") Interlocked.Increment(ref count);
        };
        await Task.Delay(LinkModule.StatusGap * 2);
        for (var i = 0; i < 200; i++) m.PublishStatus();
        Assert.Equal(1, Volatile.Read(ref count));
        await Task.Delay(LinkModule.StatusGap * 3);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    private static LinkedPhoneSettings RealPhone(DeviceKeys k) => new()
    {
        Id = k.Id, Name = "Pixel", SigningKey = k.SigningPublicB64, AgreementKey = k.AgreementPublicB64,
        Added = DateTime.UtcNow, LastIn = 5, Epoch = "AAECAwQFBgcICQoLDA0ODw",
    };

    [Fact]
    public async Task Unreadable_keys_keep_the_phones_until_the_user_confirms_new_keys()
    {
        using var phoneKeys = DeviceKeys.Create();
        var (m, _, _, settings) = Make(new LinkSettings
        {
            DeviceKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(80)),
            Phones = { RealPhone(phoneKeys) },
        });
        Assert.Equal(ModuleState.Degraded, m.Status.State);
        Assert.Equal(LinkModule.KeysUnreadableMessage, m.Status.Message);
        var status = (LinkStatusDto)(await m.InvokeAsync("getStatus", null, default))!;
        Assert.True(status.KeysUnreadable);
        Assert.Single(status.Phones);
        Assert.Equal(LinkModule.KeysUnreadableMessage, status.Message);
        // Nothing was replaced or removed on disk.
        var stored = settings.Get<LinkSettings>(LinkSettings.Section);
        Assert.Single(stored.Phones);
        Assert.NotEqual("", stored.DeviceKey);
        await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("startPairing", null, default));

        // Without the confirmation nothing happens.
        var no = await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("resetKeys", JsonDocument.Parse("""{"confirm":false}""").RootElement, default));
        Assert.Equal(RpcErrorCodes.InvalidArgs, no.Code);
        Assert.Single(settings.Get<LinkSettings>(LinkSettings.Section).Phones);

        var after = (LinkStatusDto)(await m.InvokeAsync("resetKeys", JsonDocument.Parse("""{"confirm":true}""").RootElement, default))!;
        Assert.False(after.KeysUnreadable);
        Assert.Empty(after.Phones);
        stored = settings.Get<LinkSettings>(LinkSettings.Section);
        Assert.Empty(stored.Phones);
        Assert.Equal("", stored.DeviceKey);
        // Once the keys are fine, a reset is refused.
        await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("resetKeys", JsonDocument.Parse("""{"confirm":true}""").RootElement, default));
    }

    [Fact]
    public void Linking_again_with_a_new_epoch_starts_the_counter_at_zero()
    {
        using var k = DeviceKeys.Create();
        var old = RealPhone(k);
        old.LastIn = 1_900_000_000_000_000;
        var phones = new List<LinkedPhoneSettings> { old };
        LinkModule.Relinked(phones, new PairedPhone(k.Id, "Pixel 9", k.SigningPublicB64, k.AgreementPublicB64, "DwAODQwLCgkIBwYFBAMCAQ"), DateTime.UtcNow);
        var p = Assert.Single(phones);
        Assert.Equal(0, p.LastIn);
        Assert.Equal("DwAODQwLCgkIBwYFBAMCAQ", p.Epoch);
        Assert.Equal("Pixel 9", p.Name);
    }

    [Fact]
    public async Task Rpc_errors_reach_the_phone_as_a_fixed_message_and_a_code()
    {
        var (m, fakes, notices, _) = Make();
        fakes["capture"].FailWith = @"secret detail in C:\Users\someone\file.txt";
        var r = await m.RunAsync(Phone, LinkActions.Check("capture.stop", null, out _)!, default);
        Assert.False(r.Ok);
        Assert.Equal("unavailable", r.Code);
        Assert.Equal("That isn't available on the PC right now.", r.Error);
        Assert.DoesNotContain("secret", r.Error);
        Assert.Empty(notices);
        Assert.Equal("internal", LinkModule.RpcFailure("somethingElse").Code);
        Assert.Equal("internal", LinkModule.RpcFailure(null).Code);
    }

    [Fact]
    public async Task Removing_an_unknown_phone_is_refused()
    {
        var (m, _, _, _) = Make();
        await Assert.ThrowsAsync<RpcException>(() => m.InvokeAsync("removePhone", JsonDocument.Parse("""{"id":"x"}""").RootElement, default));
    }
}

public class LinkStatusWordingTests
{
    [Theory]
    [InlineData("ratelimit", "asked to wait")]
    [InlineData("dropped", "dropped")]
    [InlineData("somethingnew", "can't be reached")]
    [InlineData(null, "can't be reached")]
    public void Waiting_message_uses_fixed_words(string? code, string expected)
    {
        var text = LinkModule.WaitingMessage(code);
        Assert.Contains(expected, text);
        if (code is not null && code != "dropped") Assert.DoesNotContain(code, text);
    }
}
