using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Lighting.Network;

namespace RigControl.Lighting.Tests.Network;

/// <summary>A made-up brand whose every step the test controls.</summary>
internal sealed class FakeProvider : INetworkLightProvider
{
    public FakeProvider(string brand = "fake") => Brand = brand;

    public string Brand { get; }
    public string DisplayName => char.ToUpperInvariant(Brand[0]) + Brand[1..];
    public bool Tested => false;
    public string Notes => "Made up for tests.";
    public bool Secret { get; set; }
    public bool Manual { get; set; } = true;
    public List<PairingOption>? Options { get; set; }
    public string? NoAnswerHint { get; set; }
    public string? UnusedKeyHint { get; set; }
    public PairingInfo Pairing => new(new[] { "Switch it on.", "Press Pair." }, Secret, Manual, Options, "Check it is switched on.",
        NoAnswerHint: NoAnswerHint, UnusedKeyHint: UnusedKeyHint);
    public double MaxFramesPerSecond { get; set; } = 50;
    public string PointName => "LED";
    public bool FollowsWhenNotPlaced { get; set; } = true;
    public TimeSpan SearchTime { get; set; } = TimeSpan.FromSeconds(6);

    public List<FoundLight> Lights { get; } = new();
    public TimeSpan DiscoverDelay { get; set; }
    public Exception? DiscoverError { get; set; }
    public int Discoveries;
    public Func<PairRequest, PairResult>? OnPair { get; set; }
    public PairRequest? LastPair { get; private set; }
    public LightDescription Shape { get; set; } = LightDescription.Strip(4);
    public Exception? DescribeError { get; set; }
    public Exception? StartError { get; set; }
    public Exception? SendError { get; set; }
    public Exception? StopError { get; set; }
    public TimeSpan StopDelay { get; set; }
    public TimeSpan StopBudget { get; set; }
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(1);
    public ConcurrentQueue<int[]> Frames { get; } = new();
    public int Starts, Stops, Disposes;
    public PairedLight? Started { get; private set; }

    public async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        Interlocked.Increment(ref Discoveries);
        await Task.Delay(DiscoverDelay, ct);
        if (DiscoverError is not null) throw DiscoverError;
        return Lights.ToList();
    }

    public Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        LastPair = request;
        var result = OnPair?.Invoke(request) ?? new PairResult(request.Found?.Id ?? "id-" + request.Address, request.Found?.Name ?? "Lamp", "M1", request.Address)
        {
            Secrets = Secret ? new Dictionary<string, string> { ["token"] = "TOPSECRET-" + request.Address } : new Dictionary<string, string>(),
            Data = new Dictionary<string, string> { ["port"] = "1234" },
        };
        return Task.FromResult(result);
    }

    public Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct) =>
        DescribeError is not null ? Task.FromException<LightDescription>(DescribeError) : Task.FromResult(Shape);

    public Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        Interlocked.Increment(ref Starts);
        Started = light;
        if (StartError is not null) return Task.FromException<ILightSession>(StartError);
        return Task.FromResult<ILightSession>(new Session(this));
    }

    private sealed class Session : ILightSession
    {
        private readonly FakeProvider _p;
        public Session(FakeProvider p) => _p = p;
        public TimeSpan KeepAlive => _p.KeepAlive;
        public TimeSpan StopBudget => _p.StopBudget;

        public Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            if (_p.SendError is not null) return Task.FromException(_p.SendError);
            _p.Frames.Enqueue(colours.ToArray());
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken ct)
        {
            await Task.Delay(_p.StopDelay, ct);
            if (_p.StopError is not null) throw _p.StopError;
            Interlocked.Increment(ref _p.Stops);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _p.Disposes);
            return ValueTask.CompletedTask;
        }
    }
}

public class NetworkLightStoreTests
{
    private readonly LightingSettings _settings = new();
    private readonly object _gate = new();
    private int _saves;

    private NetworkLightStore Store() => new(() => _settings, () => _saves++, _gate, new FakeProtector());

    private static PairResult Result(string id = "aabbcc", string address = "172.16.0.50", string? token = "tok-1") =>
        new(id, "Desk", "M1", address)
        {
            Secrets = token is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["token"] = token },
            Data = new Dictionary<string, string> { ["port"] = "55443" },
        };

    [Fact]
    public void A_paired_light_is_saved_with_its_key_protected_and_read_back()
    {
        var store = Store();
        var light = store.Save("wled", Result());
        Assert.Equal("net|wled|aabbcc", light.LayoutKey);
        Assert.Equal(1, _saves);
        var stored = Assert.Single(_settings.NetworkLights);
        Assert.Equal("172.16.0.50", stored.Address);
        Assert.Equal("55443", stored.Data["port"]);
        // What is written to the settings file never holds the key itself.
        var json = JsonSerializer.Serialize(_settings, JsonDefaults.Options);
        Assert.DoesNotContain("tok-1", json);
        Assert.StartsWith("protected[RigControl.NetworkLight.wled.token]:", stored.ProtectedSecrets["token"]);

        var again = Assert.Single(Store().All());
        Assert.Equal("tok-1", again.RequireSecret().RevealFor("172.16.0.50"));
        Assert.Throws<SecretRuleException>(() => again.RequireSecret().RevealFor("172.16.0.51"));
        Assert.Equal("55443", again.Data["port"]);
    }

    [Fact]
    public void A_key_that_cannot_be_read_on_this_account_asks_to_pair_again()
    {
        Store().Save("hue", Result());
        _settings.NetworkLights[0].ProtectedSecrets["token"] = "from another Windows account";
        var light = Assert.Single(Store().All());
        Assert.Null(light.Secret());
        Assert.Contains("pair it again", Assert.Throws<InvalidOperationException>(() => light.RequireSecret()).Message);
    }

    [Fact]
    public void Real_windows_protection_round_trips_and_is_tied_to_its_purpose()
    {
        var p = new DpapiProtector();
        var stored = p.Protect("tok-1", "RigControl.NetworkLight.hue.token");
        Assert.DoesNotContain("tok-1", stored);
        Assert.Equal("tok-1", p.Unprotect(stored, "RigControl.NetworkLight.hue.token"));
        Assert.Null(p.Unprotect(stored, "RigControl.NetworkLight.wled.token"));
        Assert.Null(p.Unprotect("not protected data", "x"));
        Assert.Null(p.Unprotect("", "x"));
    }

    [Fact]
    public void Pairing_the_same_light_again_replaces_it_and_forgetting_removes_it()
    {
        var store = Store();
        store.Save("wled", Result(address: "172.16.0.50"));
        store.Save("wled", Result(address: "172.16.0.60"));
        store.Save("wled", Result(id: "other"));
        Assert.Equal(2, store.All().Count);
        Assert.Equal("172.16.0.60", store.Find("net|wled|aabbcc")!.Address);
        var version = store.Version;
        Assert.True(store.Forget("net|wled|aabbcc"));
        Assert.False(store.Forget("net|wled|aabbcc"));
        Assert.True(store.Version > version);
        Assert.Equal("other", Assert.Single(store.All()).Id);
    }

    [Fact]
    public void A_nanoleaf_paired_before_keeps_its_settings_format_and_its_layout_key()
    {
        var old = new NanoleafSettings { Ip = "172.16.0.70", Name = "Lines", Serial = "S1" };
        old.SetToken("nl-token");
        _settings.Nanoleaf = old;
        var light = Assert.Single(Store().All());
        Assert.Equal(("nanoleaf", "S1", "Lines", "172.16.0.70"), (light.Brand, light.Id, light.Name, light.Address));
        Assert.Equal("nanoleaf|Lines|S1", light.LayoutKey);
        Assert.Equal("nl-token", light.RequireSecret().RevealFor("172.16.0.70"));

        // A new Nanoleaf pairing is written the old way too, so an older RigControl still reads it.
        var store = Store();
        var again = store.Save("nanoleaf", new PairResult("S2", "Shapes", "", "172.16.0.71") { Secrets = new Dictionary<string, string> { ["token"] = "t2" } });
        Assert.Empty(_settings.NetworkLights);
        Assert.Equal(("172.16.0.71", "Shapes", "S2"), (_settings.Nanoleaf!.Ip, _settings.Nanoleaf.Name, _settings.Nanoleaf.Serial));
        Assert.Equal("t2", _settings.Nanoleaf.Token());
        Assert.Equal("nanoleaf|Shapes|S2", again.LayoutKey);
        Assert.True(store.Forget(again.LayoutKey));
        Assert.Null(_settings.Nanoleaf);
    }

    [Fact]
    public void Only_a_light_without_a_key_can_be_moved_to_a_new_address()
    {
        var store = Store();
        store.Save("wled", Result(id: "open", token: null));
        store.Save("hue", Result(id: "locked", token: "tok-1"));
        Assert.True(store.Readdress("net|wled|open", "172.16.0.99"));
        Assert.Equal("172.16.0.99", store.Find("net|wled|open")!.Address);
        Assert.False(store.Readdress("net|wled|open", "172.16.0.99"));  // already there
        Assert.False(store.Readdress("net|wled|open", "8.8.8.8"));       // not on the user's network
        Assert.False(store.Readdress("net|hue|locked", "172.16.0.99")); // its key stays with the address it was paired at
        Assert.Equal("172.16.0.50", store.Find("net|hue|locked")!.Address);
    }

    [Fact]
    public void An_id_can_never_break_out_of_its_layout_key()
    {
        var light = Store().Save("wled", Result(id: " a|b|c "));
        Assert.Equal("a_b_c", light.Id);
        Assert.Equal("net|wled|a_b_c", light.LayoutKey);
        Assert.Throws<ArgumentException>(() => Store().Save("wled", Result(id: "  ")));
    }
}

public class NetworkLightsTests : IDisposable
{
    private readonly LightingSettings _settings = new();
    private readonly object _gate = new();
    private readonly FakeLightNetwork _net = new();
    private readonly ListLog _log = new();
    private readonly FakeProvider _a = new("alpha");
    private readonly FakeProvider _b = new("beta");
    private readonly List<NetworkLights> _made = new();

    public void Dispose()
    {
        foreach (var m in _made) m.Dispose();
    }

    private NetworkLights Lights(TimeSpan? scanTime = null, TimeSpan? retryAfter = null)
    {
        var store = new NetworkLightStore(() => _settings, () => { }, _gate, new FakeProtector());
        var lights = new NetworkLights(_net, store, _log, new INetworkLightProvider[] { _b, _a })
        {
            ScanTime = scanTime ?? TimeSpan.FromSeconds(30),
            RetryAfter = retryAfter ?? TimeSpan.FromSeconds(15),
            StopTime = TimeSpan.FromSeconds(1),
        };
        _made.Add(lights);
        return lights;
    }

    private static readonly IReadOnlyList<LayoutItem> NoItems = Array.Empty<LayoutItem>();

    /// <summary>A picture whose left half is red and right half blue.</summary>
    private static byte[] RedBlue()
    {
        var g = new byte[ColorMath.GridW * ColorMath.GridH * 4];
        for (var y = 0; y < ColorMath.GridH; y++)
            for (var x = 0; x < ColorMath.GridW; x++)
            {
                var i = 4 * (y * ColorMath.GridW + x);
                if (x < ColorMath.GridW / 2) g[i + 2] = 255; else g[i] = 255;
                g[i + 3] = 255;
            }
        return g;
    }

    private static NetworkFrame Frame(IReadOnlyList<LayoutItem> items, int one = 0x101010) =>
        new(items, RedBlue(), ColorMath.GridW, ColorMath.GridH, c => c, one);

    // ---------------------------------------------------------------- searching

    [Fact]
    public async Task A_search_asks_every_brand_at_the_same_time()
    {
        _a.DiscoverDelay = _b.DiscoverDelay = TimeSpan.FromMilliseconds(400);
        _a.Lights.Add(new FoundLight("a1", "Strip", "M", "172.16.0.20"));
        _b.Lights.Add(new FoundLight("b1", "Bulb", "M", "172.16.0.21"));
        var lights = Lights();
        var clock = Stopwatch.StartNew();
        await lights.ScanAsync(null, default);
        Assert.True(clock.ElapsedMilliseconds < 750, $"took {clock.ElapsedMilliseconds} ms: the brands were asked one after another");
        var status = lights.Status(NoItems);
        Assert.Equal(new[] { "172.16.0.20", "172.16.0.21" }, status.Found.Select(f => f.Address).OrderBy(x => x));
        Assert.False(status.Scanning);
        Assert.Empty(status.ScanProblems);
        Assert.Contains(_log.Lines, l => l.Contains("found 1 Alpha, 1 Beta") || l.Contains("found 1 Beta, 1 Alpha"));
    }

    [Fact]
    public void Nothing_is_searched_until_the_user_asks()
    {
        var lights = Lights();
        lights.Status(NoItems);
        lights.Frame(Frame(NoItems));
        lights.LayoutZones(NoItems);
        Assert.Equal(0, _a.Discoveries + _b.Discoveries);
        Assert.Empty(_net.Sent);
        Assert.Empty(_net.Asks);
        Assert.Empty(_net.Browsed);
    }

    [Fact]
    public async Task A_brand_that_takes_too_long_is_stopped_and_the_others_still_report()
    {
        _a.DiscoverDelay = TimeSpan.FromSeconds(30);
        _b.Lights.Add(new FoundLight("b1", "Bulb", "M", "172.16.0.21"));
        var lights = Lights(scanTime: TimeSpan.FromMilliseconds(250));
        var clock = Stopwatch.StartNew();
        await lights.ScanAsync(null, default);
        Assert.True(clock.ElapsedMilliseconds < 3000);
        var status = lights.Status(NoItems);
        Assert.Equal("b1", Assert.Single(status.Found).DeviceId);
        Assert.Equal("Alpha: the search took too long and was stopped.", Assert.Single(status.ScanProblems));
    }

    [Fact]
    public async Task A_brand_that_cannot_search_is_reported_in_plain_words()
    {
        _a.DiscoverError = new IOException("The network is unreachable.");
        var lights = Lights();
        await lights.ScanAsync(null, default);
        Assert.Equal("Alpha: couldn't search (The network is unreachable).", Assert.Single(lights.Status(NoItems).ScanProblems));
    }

    [Fact]
    public async Task One_search_at_a_time_and_a_search_can_be_stopped()
    {
        _a.DiscoverDelay = TimeSpan.FromSeconds(30);
        var lights = Lights();
        var running = lights.ScanAsync(null, default);
        await Eventually.True(() => lights.Status(NoItems).Scanning, "the search to start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lights.ScanAsync(null, default));
        lights.CancelScan();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(lights.Status(NoItems).Scanning);
        await Assert.ThrowsAsync<ArgumentException>(() => lights.ScanAsync("nosuchbrand", default));
    }

    [Fact]
    public async Task Answers_from_outside_the_home_network_are_dropped_and_one_brand_can_be_searched_alone()
    {
        _a.Lights.Add(new FoundLight("a1", "Strip", "M", "172.16.0.20"));
        _a.Lights.Add(new FoundLight("a2", "Far away", "M", "203.0.113.9"));
        _a.Lights.Add(new FoundLight("", "No id", "M", "172.16.0.22"));
        var lights = Lights();
        await lights.ScanAsync("alpha", default);
        Assert.Equal("a1", Assert.Single(lights.Status(NoItems).Found).DeviceId);
        Assert.Equal(0, _b.Discoveries);
    }

    [Fact]
    public async Task A_search_notes_the_new_address_of_a_light_without_a_key_but_never_moves_a_key()
    {
        var lights = Lights();
        _b.Secret = true;
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        await lights.PairAsync("beta", "172.16.0.21", null, default);
        _a.Lights.Add(new FoundLight("id-172.16.0.20", "Strip", "M", "172.16.0.120"));
        _b.Lights.Add(new FoundLight("id-172.16.0.21", "Bulb", "M", "172.16.0.121"));
        await lights.ScanAsync(null, default);
        Assert.Equal("172.16.0.120", lights.Store.Find("net|alpha|id-172.16.0.20")!.Address);
        Assert.Equal("172.16.0.21", lights.Store.Find("net|beta|id-172.16.0.21")!.Address);
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp is now at 172.16.0.120.");
    }

    // ---------------------------------------------------------------- pairing

    [Fact]
    public async Task Pairing_saves_the_light_protects_its_key_and_logs_nothing_secret()
    {
        _a.Secret = true;
        _a.Lights.Add(new FoundLight("mac1", "Desk strip", "M9", "172.16.0.20") { Data = new Dictionary<string, string> { ["port"] = "80" } });
        var lights = Lights();
        await lights.ScanAsync(null, default);
        var light = await lights.PairAsync("alpha", " 172.16.0.20 ", null, default);
        Assert.Equal("net|alpha|mac1", light.LayoutKey);
        Assert.Equal("mac1", _a.LastPair!.Found!.Id); // what the search learned is handed to pairing
        Assert.Equal("80", _a.LastPair.Found.Data["port"]);

        var status = lights.Status(NoItems);
        var paired = Assert.Single(status.Paired);
        Assert.Equal(("net|alpha|mac1", "alpha", "Alpha", "Desk strip", "172.16.0.20", "strip", 4, "LED"),
            (paired.Id, paired.Brand, paired.BrandName, paired.Name, paired.Address, paired.Kind, paired.Points, paired.PointName));
        Assert.False(paired.Tested);
        Assert.False(paired.Placed);
        Assert.True(Assert.Single(status.Found).Paired);

        var json = JsonSerializer.Serialize(_settings, JsonDefaults.Options) + JsonSerializer.Serialize(status, JsonDefaults.Options);
        Assert.DoesNotContain("TOPSECRET", json);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("TOPSECRET"));
        Assert.Contains(_log.Lines, l => l == "Alpha Desk strip paired at 172.16.0.20: 4 LED(s), each can follow its own part of the effects.");
    }

    [Theory]
    [InlineData("alpha", "8.8.8.8")]
    [InlineData("alpha", "lamp.example.com")]
    [InlineData("alpha", null)]
    [InlineData("nosuchbrand", "172.16.0.20")]
    public async Task Pairing_needs_a_known_brand_and_an_address_on_the_home_network(string brand, string? address)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Lights().PairAsync(brand, address, null, default));
        Assert.Null(_a.LastPair);
    }

    [Fact]
    public async Task A_brand_that_must_be_found_first_cannot_be_paired_by_typing_an_address()
    {
        _a.Manual = false;
        var lights = Lights();
        await Assert.ThrowsAsync<ArgumentException>(() => lights.PairAsync("alpha", "172.16.0.20", null, default));
        _a.Lights.Add(new FoundLight("mac1", "Desk strip", "M9", "172.16.0.20"));
        await lights.ScanAsync(null, default);
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
    }

    [Fact]
    public async Task Pairing_choices_are_checked_and_missing_ones_take_their_default()
    {
        _a.Options = new List<PairingOption>
        {
            new("type", "Which kind", new[] { new PairingChoice("hexagon", "Hexagon"), new PairingChoice("strip", "Strip") }, "hexagon"),
        };
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        Assert.Equal("hexagon", _a.LastPair!.Options["type"]);
        await lights.PairAsync("alpha", "172.16.0.20", new Dictionary<string, string> { ["type"] = "strip", ["extra"] = "ignored" }, default);
        Assert.Equal("strip", _a.LastPair!.Options["type"]);
        Assert.False(_a.LastPair.Options.ContainsKey("extra"));
        await Assert.ThrowsAsync<ArgumentException>(() => lights.PairAsync("alpha", "172.16.0.20", new Dictionary<string, string> { ["type"] = "cube" }, default));
    }

    [Fact]
    public async Task A_key_is_never_saved_for_another_address_than_the_one_picked()
    {
        _a.Secret = true;
        _a.OnPair = r => new PairResult("mac1", "Lamp", "M", "172.16.0.99") { Secrets = new Dictionary<string, string> { ["token"] = "TOPSECRET" } };
        var lights = Lights();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => lights.PairAsync("alpha", "172.16.0.20", null, default));
        Assert.Contains("another address", ex.Message);
        Assert.Empty(_settings.NetworkLights);
    }

    [Fact]
    public async Task A_light_that_refuses_is_not_saved_and_says_why()
    {
        var lights = Lights();
        _a.OnPair = _ => throw new InvalidOperationException("Press the button on the bridge first.");
        Assert.Equal("Press the button on the bridge first.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => lights.PairAsync("alpha", "172.16.0.20", null, default))).Message);
        _a.OnPair = _ => throw new IOException("Connection refused.");
        Assert.Equal("The Alpha didn't answer: Connection refused.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => lights.PairAsync("alpha", "172.16.0.20", null, default))).Message);
        Assert.Empty(_settings.NetworkLights);
    }

    [Fact]
    public async Task A_light_that_paired_but_could_not_be_read_yet_is_kept_with_its_key_and_says_so()
    {
        // The device has already made the key: throwing the pairing away would mean pressing its button again.
        _a.Secret = true;
        _a.DescribeError = new IOException("No answer.");
        var lights = Lights();
        var light = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        Assert.Equal("TOPSECRET-172.16.0.20", light.RequireSecret().RevealFor("172.16.0.20"));
        Assert.Single(_settings.NetworkLights);
        var shown = Assert.Single(lights.Status(NoItems).Paired);
        Assert.Equal("Paired, but its LEDs couldn't be read yet (No answer). RigControl keeps trying.", shown.Problem);
        Assert.Equal(0, shown.Points);
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp paired at 172.16.0.20, but its LEDs couldn't be read yet. RigControl keeps trying.");
    }

    [Fact]
    public async Task A_device_with_nothing_to_colour_is_not_kept_and_the_message_says_not_paired()
    {
        _a.Secret = true;
        _a.UnusedKeyHint = "An unused entry stays on the bridge; it does no harm.";
        _a.DescribeError = new InvalidOperationException("This bridge has no colour lights.");
        var lights = Lights();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => lights.PairAsync("alpha", "172.16.0.20", null, default));
        Assert.Equal("Not paired: This bridge has no colour lights. An unused entry stays on the bridge; it does no harm.", ex.Message);
        Assert.Empty(_settings.NetworkLights);
        Assert.Empty(lights.Status(NoItems).Paired);
    }

    [Fact]
    public async Task What_a_device_says_about_itself_is_tidied_before_it_is_saved_shown_or_logged()
    {
        _a.OnPair = r => new PairResult("a1\r\nb2", "Lamp\r\n2026-11-24 21:00:00 [info] Helper programs installed" + new string('x', 60000), new string('M', 60000), r.Address)
        {
            Data = new Dictionary<string, string> { ["sku"] = "H6199\nforged line" + new string('y', 60000) },
        };
        var lights = Lights();
        var light = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        Assert.Equal("a1b2", light.Id);
        Assert.True(light.Name.Length <= 64 && light.Model.Length <= 64 && light.Data["sku"].Length <= 1000);
        var stored = Assert.Single(_settings.NetworkLights);
        Assert.DoesNotContain(stored.Id + stored.Name + stored.Model + stored.Data["sku"], c => char.IsControl(c));
        Assert.All(_log.Lines, l => Assert.DoesNotContain(l, c => c is '\r' or '\n'));
        Assert.All(_log.Lines, l => Assert.True(l.Length < 400, "a log line stays short"));
    }

    // ---------------------------------------------------------------- layout and colours

    [Fact]
    public async Task Each_paired_light_is_one_box_on_the_layout()
    {
        var lights = Lights();
        var strip = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        _b.Shape = new LightDescription(LightKind.Points, new[] { new LightPoint(7, 0, 0, 0, 0, 0.2, 0), new LightPoint(9, 1, 1, 1, 0.8, 1, 1) }) { DrawAsBars = true };
        var wall = await lights.PairAsync("beta", "172.16.0.21", null, default);
        var placed = new LayoutItem { Key = strip.LayoutKey, Zone = LedLayout.WholeDevice, X = 0, Y = 0, W = 1, H = 0.1 };
        var zones = lights.LayoutZones(new[] { placed });
        Assert.Equal(2, zones.Count);
        var s = zones.Single(z => z.Key == strip.LayoutKey);
        Assert.Equal(("Lamp (Alpha)", -1, 4, 0, true), (s.Device, s.Zone, s.Leds, s.MatrixWidth, s.Connected));
        Assert.Same(placed, s.Placed);
        Assert.Null(s.Segments);
        var w = zones.Single(z => z.Key == wall.LayoutKey);
        Assert.Equal((2, 20, 12), (w.Leds, w.MatrixWidth, w.MatrixHeight));
        Assert.Equal(0, w.Matrix![0]);                 // the first point sits top left
        Assert.Equal(1, w.Matrix[20 * 12 - 1]);        // the second bottom right
        Assert.Equal(new[] { 0, 0, 0.2, 0 }, w.Segments![0]);
        Assert.Null(w.Placed);
    }

    [Fact]
    public void A_strip_is_cut_into_slices_along_its_box_and_can_run_the_other_way()
    {
        var d = LightDescription.Strip(4);
        var item = new LayoutItem { Key = "k", X = 0, Y = 0.4, W = 1, H = 0.2 };
        Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF }, NetworkLights.Sample(d, item, Frame(NoItems)));
        item.Reverse = true;
        Assert.Equal(new[] { 0x0000FF, 0x0000FF, 0xFF0000, 0xFF0000 }, NetworkLights.Sample(d, item, Frame(NoItems)));
    }

    [Fact]
    public void Points_take_the_picture_under_their_own_place_and_a_single_light_takes_its_whole_box()
    {
        var points = new LightDescription(LightKind.Points, new[] { new LightPoint(1, 0.1, 0.5), new LightPoint(2, 0.9, 0.5) });
        var whole = new LayoutItem { Key = "k", X = 0, Y = 0, W = 1, H = 1 };
        Assert.Equal(new[] { 0xFF0000, 0x0000FF }, NetworkLights.Sample(points, whole, Frame(NoItems)));
        var left = new LayoutItem { Key = "k", X = 0, Y = 0, W = 0.4, H = 1 };
        Assert.Equal(new[] { 0xFF0000 }, NetworkLights.Sample(LightDescription.Single(), left, Frame(NoItems)));
        // The shade (brightness, vivid, bass...) is applied to every colour.
        var dim = new NetworkFrame(NoItems, RedBlue(), ColorMath.GridW, ColorMath.GridH, c => (c >> 1) & 0x7F7F7F, 0);
        Assert.Equal(new[] { 0x7F0000 }, NetworkLights.Sample(LightDescription.Single(), left, dim));
    }

    [Fact]
    public async Task A_placed_light_gets_its_part_of_the_picture_and_an_unplaced_one_the_one_colour()
    {
        var lights = Lights();
        var strip = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        await lights.PairAsync("beta", "172.16.0.21", null, default);
        var items = new[] { new LayoutItem { Key = strip.LayoutKey, Zone = -1, X = 0, Y = 0, W = 1, H = 0.2 } };
        lights.Frame(Frame(items, one: 0x123456));
        await Eventually.True(() => !_a.Frames.IsEmpty && !_b.Frames.IsEmpty, "both lights to get a frame");
        Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF }, _a.Frames.First());
        Assert.Equal(new[] { 0x123456, 0x123456, 0x123456, 0x123456 }, _b.Frames.First());
        Assert.Equal("172.16.0.20", _a.Started!.Address);
        var status = lights.Status(items);
        Assert.All(status.Paired, p => Assert.True(p.Streaming));
        Assert.True(status.Paired.Single(p => p.Brand == "alpha").Placed);
        Assert.True(lights.IsStreaming("alpha"));
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp: 4 LED(s) follow their part of the picture.");
    }

    [Fact]
    public async Task A_brand_that_waits_to_be_placed_is_left_alone_until_it_is_and_put_back_when_it_leaves()
    {
        _a.FollowsWhenNotPlaced = false;
        var lights = Lights();
        var strip = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        lights.Frame(Frame(NoItems));
        await Task.Delay(100);
        Assert.Equal(0, _a.Starts);

        var items = new[] { new LayoutItem { Key = strip.LayoutKey, Zone = -1, X = 0, Y = 0, W = 1, H = 0.2 } };
        lights.Frame(Frame(items));
        await Eventually.True(() => !_a.Frames.IsEmpty, "the first frame");
        lights.Frame(Frame(NoItems)); // taken off the layout
        await Eventually.True(() => _a.Stops == 1 && _a.Disposes == 1, "the light to be put back");
        Assert.False(lights.IsStreaming("alpha"));
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp: stopped following; put back as it was.");
    }

    [Fact]
    public async Task Frames_are_sent_no_faster_than_the_light_allows_and_only_the_newest_one()
    {
        _a.MaxFramesPerSecond = 10;
        _a.KeepAlive = TimeSpan.Zero; // every frame counts
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        var clock = Stopwatch.StartNew();
        var n = 0;
        while (clock.ElapsedMilliseconds < 1000)
        {
            lights.Frame(Frame(NoItems, one: ++n));
            await Task.Delay(5);
        }
        var seconds = clock.Elapsed.TotalSeconds;
        Assert.InRange(_a.Frames.Count, 3, (int)Math.Ceiling(seconds * 10) + 1);
        // Never a stale frame: each one sent is newer than the one before.
        var sent = _a.Frames.Select(f => f[0]).ToList();
        Assert.Equal(sent.OrderBy(x => x), sent);
        Assert.True(sent[^1] > n - 60);
    }

    [Fact]
    public async Task An_unchanged_frame_is_only_sent_again_to_keep_the_light_awake()
    {
        _a.KeepAlive = TimeSpan.FromMilliseconds(300);
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1000)
        {
            lights.Frame(Frame(NoItems, one: 0x00FF00));
            await Task.Delay(10);
        }
        Assert.InRange(_a.Frames.Count, 2, 5); // at 50 a second it would have been about 50
        lights.Frame(Frame(NoItems, one: 0x0000FF));
        await Eventually.True(() => _a.Frames.Last()[0] == 0x0000FF, "a changed frame to go out at once");
    }

    [Fact]
    public async Task Stopping_puts_every_light_back_once()
    {
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        await lights.PairAsync("beta", "172.16.0.21", null, default);
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => !_a.Frames.IsEmpty && !_b.Frames.IsEmpty, "both lights to start");
        lights.StopAll();
        Assert.Equal((1, 1, 1, 1), (_a.Stops, _a.Disposes, _b.Stops, _b.Disposes));
        Assert.False(lights.IsStreaming("alpha"));
        lights.StopAll(); // again: nothing more to do
        Assert.Equal(1, _a.Stops);
        // The next sync starts them afresh.
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => _a.Starts == 2, "a fresh start");
    }

    [Fact]
    public async Task A_light_that_stops_answering_is_reported_left_alone_and_tried_again_later()
    {
        var lights = Lights(retryAfter: TimeSpan.FromMilliseconds(400));
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        _a.SendError = new IOException("Connection reset.");
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => lights.Status(NoItems).Paired[0].Problem is not null, "the problem to show");
        var problem = lights.Status(NoItems).Paired[0].Problem!;
        Assert.StartsWith("It doesn't answer at 172.16.0.20.", problem);
        Assert.Contains("Search for lights", problem); // a light without a key is picked up again by a search
        Assert.False(lights.IsStreaming("alpha"));
        Assert.Equal(0, _a.Stops);                      // nothing to put back on a light that is gone
        await Eventually.True(() => _a.Disposes == 1, "the dead session to be dropped");
        Assert.Contains(_log.Lines, l => l.StartsWith("Alpha Lamp: It doesn't answer at 172.16.0.20") && l.Contains("; trying again in"));

        // Not hammered: no new start inside the waiting time.
        var startsNow = _a.Starts;
        for (var i = 0; i < 10; i++) { lights.Frame(Frame(NoItems)); await Task.Delay(10); }
        Assert.Equal(startsNow, _a.Starts);
        // Back again: picked up after the wait.
        _a.SendError = null;
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_a.Frames.IsEmpty && DateTime.UtcNow < until) { lights.Frame(Frame(NoItems)); await Task.Delay(20); }
        Assert.False(_a.Frames.IsEmpty);
        Assert.Null(lights.Status(NoItems).Paired[0].Problem);
    }

    [Fact]
    public async Task A_providers_own_plain_message_is_shown_as_it_is()
    {
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        _a.StartError = new InvalidOperationException("Switch on LAN Control for this light in its phone app.");
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => lights.Status(NoItems).Paired[0].Problem is not null, "the problem to show");
        Assert.Equal("Switch on LAN Control for this light in its phone app.", lights.Status(NoItems).Paired[0].Problem);
    }

    [Fact]
    public async Task Forgetting_a_light_puts_it_back_first_and_removes_its_key()
    {
        _a.Secret = true;
        var lights = Lights();
        var light = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => !_a.Frames.IsEmpty, "the light to start");
        Assert.True(await lights.ForgetAsync(light.LayoutKey));
        Assert.Equal(1, _a.Stops);
        Assert.Empty(_settings.NetworkLights);
        Assert.Empty(lights.Status(NoItems).Paired);
        Assert.False(await lights.ForgetAsync(light.LayoutKey));
        lights.Frame(Frame(NoItems));
        await Task.Delay(50);
        Assert.Equal(1, _a.Starts);
    }

    [Fact]
    public async Task Asking_whether_a_brand_plays_takes_no_lock_so_another_module_holding_the_settings_lock_cannot_hang_it()
    {
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => !_a.Frames.IsEmpty, "the light to start");

        // Another thread holds the lock that guards the settings (the lighting module's own) the whole time.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (_gate)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var asked = Task.Run(() => (lights.IsStreaming("alpha"), lights.IsStreaming("beta")));
            Assert.True(await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(3))) == asked, "the answer came without waiting for the lock");
            Assert.Equal((true, false), await asked);
        }
        finally
        {
            release.Set();
            await holder;
        }
        lights.StopAll();
        Assert.False(lights.IsStreaming("alpha"));
    }

    [Fact]
    public async Task A_stopped_search_keeps_what_the_finished_brands_found_and_says_which_brands_were_done()
    {
        _a.DiscoverDelay = TimeSpan.FromSeconds(30);                       // still searching when the user presses Stop
        _a.Lights.Add(new FoundLight("a1", "Strip", "M", "172.16.0.20"));
        _b.Lights.Add(new FoundLight("b1", "Bulb", "M", "172.16.0.21")); // answers at once
        var lights = Lights();
        var running = lights.ScanAsync(null, default);
        await Eventually.True(() => _b.Discoveries == 1 && _a.Discoveries == 1, "both brands to start");
        await Task.Delay(150);
        lights.CancelScan();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var status = lights.Status(NoItems);
        Assert.False(status.Scanning);
        Assert.True(status.ScanStopped);
        Assert.Equal(new[] { "beta" }, status.ScanFinished);               // alpha never finished: it doesn't count as searched
        Assert.Equal("b1", Assert.Single(status.Found).DeviceId);
        Assert.Empty(status.ScanProblems);
        Assert.Contains(_log.Lines, l => l == "Search for lights: stopped; found so far: 1 Beta.");

        // A search that runs to its end says so again.
        _a.DiscoverDelay = TimeSpan.Zero;
        await lights.ScanAsync(null, default);
        status = lights.Status(NoItems);
        Assert.False(status.ScanStopped);
        Assert.Equal(new[] { "alpha", "beta" }, status.ScanFinished!.OrderBy(b => b));
        Assert.Equal(2, status.Found.Count);
    }

    [Fact]
    public async Task A_search_lists_at_most_64_lights_of_a_brand_and_tidies_what_they_say()
    {
        _a.Lights.Add(new FoundLight("evil\r\nid", "Lamp\r\n2026-11-24 21:00:00 [info] forged" + new string('x', 60000), new string('M', 60000), "172.16.0.9")
        {
            Data = new Dictionary<string, string> { ["sku"] = new string('s', 60000) },
        });
        for (var i = 0; i < 500; i++) _a.Lights.Add(new FoundLight("id" + i, "Lamp " + i, "M", "172.16.1." + (i % 250 + 1)));
        var lights = Lights();
        await lights.ScanAsync("alpha", default);
        var found = lights.Status(NoItems).Found;
        Assert.Equal(NetworkLights.MostFoundPerBrand, found.Count);
        Assert.Equal(64, found.Count);
        Assert.All(found, f =>
        {
            Assert.DoesNotContain(f.DeviceId + f.Name + f.Model, c => char.IsControl(c));
            Assert.True(f.Name.Length <= 64 && f.Model.Length <= 64 && f.DeviceId.Length <= 200);
        });
        Assert.Equal("evilid", found[0].DeviceId);
    }

    [Fact]
    public async Task A_light_that_still_answers_at_its_old_address_is_never_moved_to_a_device_that_claims_its_id()
    {
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        // The real light answers where it always was, and something else says it is the same light.
        _a.Lights.Add(new FoundLight("id-172.16.0.20", "Strip", "M", "172.16.0.20"));
        _a.Lights.Add(new FoundLight("id-172.16.0.20", "Strip", "M", "172.16.0.66"));
        await lights.ScanAsync("alpha", default);
        Assert.Equal("172.16.0.20", lights.Store.Find("net|alpha|id-172.16.0.20")!.Address);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("is now at"));

        // The other way round in the list makes no difference.
        _a.Lights.Reverse();
        await lights.ScanAsync("alpha", default);
        Assert.Equal("172.16.0.20", lights.Store.Find("net|alpha|id-172.16.0.20")!.Address);
    }

    [Fact]
    public async Task Two_devices_that_both_claim_to_be_a_paired_light_leave_its_address_alone()
    {
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        _a.Lights.Add(new FoundLight("id-172.16.0.20", "Strip", "M", "172.16.0.66"));
        _a.Lights.Add(new FoundLight("id-172.16.0.20", "Strip", "M", "172.16.0.67"));
        await lights.ScanAsync("alpha", default);
        Assert.Equal("172.16.0.20", lights.Store.Find("net|alpha|id-172.16.0.20")!.Address);
        Assert.Contains(_log.Lines, l => l == "2 devices say they are Alpha Lamp; its address was left alone.");
    }

    [Fact]
    public async Task The_log_only_says_put_back_when_the_light_was_put_back()
    {
        _a.StopError = new IOException("the light refused: client quota exceeded");
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => !_a.Frames.IsEmpty, "the light to start");
        lights.StopAll();
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp: stopped following, but it could not be put back as it was (the light refused: client quota exceeded). It keeps its last colour until you change it.");
        Assert.DoesNotContain(_log.Lines, l => l.Contains("put back as it was."));
    }

    [Fact]
    public async Task A_light_that_needs_longer_to_be_put_back_gets_the_time_it_asks_for_and_one_that_overruns_is_reported()
    {
        // The usual stop time in these tests is one second.
        _a.StopDelay = TimeSpan.FromMilliseconds(1600);
        _a.StopBudget = TimeSpan.FromSeconds(5);
        _b.StopDelay = TimeSpan.FromMilliseconds(1600);
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        await lights.PairAsync("beta", "172.16.0.21", null, default);
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => !_a.Frames.IsEmpty && !_b.Frames.IsEmpty, "both lights to start");
        lights.StopAll(); // waits for the longest budget among the lights it stops
        Assert.Equal(1, _a.Stops);
        Assert.Equal(0, _b.Stops);
        Assert.Contains(_log.Lines, l => l == "Alpha Lamp: stopped following; put back as it was.");
        Assert.Contains(_log.Lines, l => l == "Beta Lamp: stopped following, but it could not be put back as it was (it took too long). It keeps its last colour until you change it.");
    }

    [Fact]
    public async Task A_light_that_stops_answering_names_what_to_check_before_the_new_address_advice()
    {
        _a.NoAnswerHint = "and that LAN Control is still on for it in its app";
        var lights = Lights();
        await lights.PairAsync("alpha", "172.16.0.20", null, default);
        _a.StartError = new IOException("it sent no reply");
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => lights.Status(NoItems).Paired[0].Problem is not null, "the problem to show");
        Assert.Equal("It doesn't answer at 172.16.0.20. Check it is switched on and that LAN Control is still on for it in its app. "
                     + "If it got a new address, press Search for lights and RigControl picks the new one up.",
            lights.Status(NoItems).Paired[0].Problem);
    }

    [Fact]
    public async Task A_light_that_is_no_longer_paired_but_still_playing_is_put_back_and_let_go()
    {
        var lights = Lights();
        var light = await lights.PairAsync("alpha", "172.16.0.20", null, default);
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => !_a.Frames.IsEmpty, "the light to start");
        // Its pairing is replaced behind the sender's back (as when another light takes its place in the settings).
        Assert.True(lights.Store.Forget(light.LayoutKey));
        lights.Frame(Frame(NoItems));
        await Eventually.True(() => _a.Stops == 1 && _a.Disposes == 1, "the light to be put back");
        Assert.False(lights.IsStreaming("alpha"));
        var sent = _a.Frames.Count;
        await Task.Delay(150);
        Assert.Equal(sent, _a.Frames.Count); // nothing keeps sending to it
    }

    [Fact]
    public void The_status_lists_every_brand_with_its_pairing_steps_and_what_is_not_supported()
    {
        var status = Lights().Status(NoItems);
        Assert.Equal(new[] { "alpha", "beta" }, status.Brands.Select(b => b.Brand)); // by name when the order isn't fixed
        var alpha = status.Brands[0];
        Assert.Equal(("Alpha", false, false, true, 50.0), (alpha.Name, alpha.Tested, alpha.ReturnsSecret, alpha.ManualAddress, alpha.MaxFramesPerSecond));
        Assert.Equal(new[] { "Switch it on.", "Press Pair." }, alpha.Steps);
        Assert.Equal("Check it is switched on.", alpha.NothingFound);
        Assert.NotEmpty(status.NotSupported);
        Assert.All(status.NotSupported, g => Assert.False(string.IsNullOrWhiteSpace(g.Why)));
    }

    [Fact]
    public void The_program_finds_its_own_providers_and_nanoleaf_is_one_of_them()
    {
        var builtIn = NetworkLights.BuiltIn();
        Assert.Contains(builtIn, p => p.Brand == "nanoleaf" && p.Tested);
        Assert.Equal(builtIn.Count, builtIn.Select(p => p.Brand).Distinct().Count());
        foreach (var p in builtIn)
        {
            Assert.Matches("^[a-z0-9]+$", p.Brand);
            Assert.NotEmpty(p.Pairing.Steps);
            Assert.InRange(p.MaxFramesPerSecond, 0.1, 100);
            Assert.False(string.IsNullOrWhiteSpace(p.Notes));
        }
    }
}
