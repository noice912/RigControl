using System.Collections.Concurrent;
using RigControl.Core.Logging;

namespace RigControl.Lighting.Network;

// ---------------------------------------------------------------- contract types (docs/API.md, "lighting: network lights")

/// <summary>One brand RigControl can drive over the network, with how to pair it.</summary>
/// <param name="CanSearch">False for a brand that can't be searched for: the user types the light's address.</param>
/// <param name="FollowsUnplaced">False for a brand whose lights are left alone until they are placed on the layout.</param>
public sealed record NetworkBrandDto(string Brand, string Name, bool Tested, string Notes, IReadOnlyList<string> Steps, bool ReturnsSecret,
    bool ManualAddress, IReadOnlyList<PairingOption> Options, string? NothingFound, double MaxFramesPerSecond, bool CanSearch = true,
    bool FollowsUnplaced = true);

/// <summary>A paired light. <see cref="Id"/> is also its key on the layout.</summary>
/// <param name="Controls">What it can be told directly, each with why not; null for a brand this version doesn't know.</param>
/// <param name="On">Whether it is on, as last read from the light; null when not known.</param>
/// <param name="Brightness">0-100, as last read; null when not known.</param>
/// <param name="Color">"#RRGGBB" at full strength, as last read; null for an effect, a white, or not known.</param>
/// <param name="TakenOver">True when RigControl changed it by hand and remembers what it did before ("Give back" puts that back).</param>
/// <param name="JoinsEverything">False for a brand left out of "Everything at once" and "Turn all lights off" (a Hue Bridge).</param>
/// <param name="FollowsUnplaced">False for a brand the effects leave alone until it is placed on the layout (a Hue Bridge).</param>
public sealed record NetworkLightDto(string Id, string Brand, string BrandName, string DeviceId, string Name, string Model, string Address,
    bool Tested, string Kind, int Points, string PointName, bool Placed, bool Streaming, string? Problem, string? Note,
    LightControls? Controls = null, bool? On = null, int? Brightness = null, string? Color = null, bool TakenOver = false,
    bool JoinsEverything = true, bool FollowsUnplaced = true);

/// <summary>A light the last search found.</summary>
public sealed record FoundLightDto(string Brand, string BrandName, string DeviceId, string Name, string Model, string Address, bool Paired);

/// <summary>Something that is not supported, and why, for the page's list.</summary>
public sealed record NetworkGapDto(string Brand, string Name, string Why);

/// <param name="ScanStopped">True when the last search was stopped by the user before every brand had finished.</param>
/// <param name="ScanFinished">The brands whose search ran to its end in the last search (all of the ones asked for, unless it was stopped).</param>
public sealed record NetworkLightsDto(IReadOnlyList<NetworkBrandDto> Brands, IReadOnlyList<NetworkLightDto> Paired,
    IReadOnlyList<FoundLightDto> Found, bool Scanning, IReadOnlyList<string> ScanProblems, IReadOnlyList<NetworkGapDto> NotSupported,
    bool ScanStopped = false, IReadOnlyList<string>? ScanFinished = null);

/// <summary>One step of the light sync, as the network lights need it.</summary>
/// <param name="Items">The placed boxes (empty when "per LED" is off).</param>
/// <param name="Grid">The picture (BGRA, <paramref name="GridW"/> x <paramref name="GridH"/>), or null when there is none yet.</param>
/// <param name="Shade">Turns a colour read from the picture (0xRRGGBB) into the colour to send (vivid, brightness, bass and so on).</param>
/// <param name="OneColour">The one colour every light takes that has no place of its own (0xRRGGBB, already shaded).</param>
public sealed record NetworkFrame(IReadOnlyList<LayoutItem> Items, byte[]? Grid, int GridW, int GridH, Func<int, int> Shade, int OneColour);

/// <summary>
/// The network lights: which brands exist, searching (only when the user asks), pairing, forgetting, and sending each
/// paired light its colours while the light sync runs. Owned by the lighting module.
/// </summary>
public sealed class NetworkLights : IDisposable
{
    private static readonly string[] Order = { "nanoleaf", "hue", "wled", "govee", "yeelight", "cololight", "leetdesk" };

    /// <summary>Things people ask for that can't be done locally (shown on the page, so nobody has to guess).</summary>
    public static readonly IReadOnlyList<NetworkGapDto> Gaps = new[]
    {
        new NetworkGapDto("hue", "Philips Hue: gradient strips piece by piece, and fast sync",
            "Needs Hue's Entertainment stream, which is not built yet. Each Hue light follows as one colour, a few times a second. The key for it is already saved at pairing, so nothing has to be pressed again when it is added."),
        new NetworkGapDto("hue", "Philips Hue: choosing single lights or rooms here",
            "Which Hue lights follow is chosen in the Hue app: the lights of the bridge's entertainment area (the screen one when there are several). A bridge without an entertainment area follows with every colour light it has, in every room."),
        new NetworkGapDto("govee", "Govee: lights without \"LAN Control\", and a colour per piece on every light",
            "Only Govee lights that show a LAN Control switch in the Govee Home app can be driven locally. Govee's own local protocol sets one colour for the whole light. A colour per piece uses an unofficial stream that only some lights take, and a light doesn't say how many pieces it has: you choose the number when pairing."),
        new NetworkGapDto("yeelight", "Yeelight: lights without \"LAN Control\", and strips LED by LED",
            "Some newer Yeelight lights, and ones set up through Mi Home, have no LAN Control switch. Those can only be driven through Yeelight's cloud, which RigControl doesn't use. Each Yeelight is one colour (two with a background light); strips and cubes can't be driven LED by LED."),
        new NetworkGapDto("cololight", "Cololight: a colour per panel, and searching",
            "Nobody has worked out how to colour single Cololight panels over the network, so the whole light follows as one colour. There is no dependable way to search for one either: you type its address."),
        new NetworkGapDto("leetdesk", "LeetDesk Aura: anything beyond one plain strip",
            "LeetDesk publishes nothing about how the Aura is controlled. RigControl follows a public add-on from 2024, so newer desks may behave differently. Where each light sits along the desk isn't known: place the strip on the layout and use Run backwards if it runs the wrong way."),
        new NetworkGapDto("wled", "WLED: very long strips in one go, and pairing by name",
            "A strip longer than 489 LEDs is sent in several parts, so a very long strip can change part by part. A WLED is paired by its address, not by a name such as wled.local."),
        new NetworkGapDto("openrgb", "Fan hubs and light headers OpenRGB doesn't know",
            "Corsair Commander and Lighting Node, Thermaltake Riing, Lian Li Uni hubs, Razer's light strip controller and motherboard headers come through OpenRGB, and you set how many lights hang on each header under the device on this page. Hubs OpenRGB 1.1 doesn't know (such as Lian Li Uni Fan TL) don't show up. NZXT channels count their own lights and can't be set by hand. Of these, only NZXT and Razer have been tried on real hardware."),
        new NetworkGapDto("cloud", "Lights that only work through an account (Tuya / Smart Life, Wiz cloud scenes, TP-Link cloud)",
            "RigControl controls lights on your own network only. It never signs in to a cloud account."),
    };

    private readonly ILightNetwork _net;
    private readonly NetworkLightStore _store;
    private readonly ILog _log;
    private readonly IReadOnlyList<INetworkLightProvider> _providers;
    private readonly ConcurrentDictionary<string, LightState> _states = new();
    private readonly object _scanGate = new();
    private CancellationTokenSource? _scan;
    private volatile IReadOnlyList<(INetworkLightProvider Provider, FoundLight Light)> _found = Array.Empty<(INetworkLightProvider, FoundLight)>();
    private volatile IReadOnlyList<string> _scanProblems = Array.Empty<string>();
    private volatile bool _scanStopped;
    private volatile IReadOnlyList<string> _scanFinished = Array.Empty<string>();
    /// <summary>How many lights of each brand take colours right now. Read without any lock (see <see cref="IsStreaming"/>).</summary>
    private readonly ConcurrentDictionary<string, int> _streaming = new();
    private int _disposed;

    /// <summary>The most lights of one brand a search lists. More than that is not a home, it is a device making things up.</summary>
    public const int MostFoundPerBrand = 64;
    /// <summary>The longest stop any light is given, whatever it asks for.</summary>
    private static readonly TimeSpan LongestStop = TimeSpan.FromSeconds(20);

    /// <summary>The longest any brand may search, whatever it asks for.</summary>
    public TimeSpan ScanTime { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long pairing may take (some brands wait for a button press).</summary>
    public TimeSpan PairTime { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan DescribeTime { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>After a light failed, how long before it is tried again.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How long putting a light back may take when the sync stops.</summary>
    public TimeSpan StopTime { get; init; } = TimeSpan.FromSeconds(3);

    public NetworkLights(ILightNetwork net, NetworkLightStore store, ILog log, IEnumerable<INetworkLightProvider>? providers = null)
    {
        _net = net;
        _store = store;
        _log = log;
        _providers = (providers ?? BuiltIn())
            .GroupBy(p => p.Brand).Select(g => g.First())
            .OrderBy(p => Array.IndexOf(Order, p.Brand) is var i && i < 0 ? int.MaxValue : i).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every provider in this program: each class that implements the contract and can be made without arguments.</summary>
    public static IReadOnlyList<INetworkLightProvider> BuiltIn()
    {
        var list = new List<INetworkLightProvider>();
        foreach (var type in typeof(NetworkLights).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(INetworkLightProvider).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            try { list.Add((INetworkLightProvider)Activator.CreateInstance(type)!); }
            catch { /* a provider that can't be made is left out */ }
        }
        return list;
    }

    public IReadOnlyList<INetworkLightProvider> Providers => _providers;
    public NetworkLightStore Store => _store;

    private INetworkLightProvider? Provider(string brand) => _providers.FirstOrDefault(p => p.Brand == brand);

    private sealed class LightState
    {
        public volatile LightDescription? Description;
        public volatile string? Problem;
        public int Loading;
        public DateTime LoadFailedAt = DateTime.MinValue;
        public DateTime RetryAt = DateTime.MinValue;
        public Runner? Runner;
        public Task Stopping = Task.CompletedTask;
        /// <summary>How long <see cref="Stopping"/> may take (the light's own stop budget).</summary>
        public TimeSpan StoppingNeeds = TimeSpan.Zero;
        public volatile bool Streaming;
        /// <summary>What the light last said it shows (null until read, or when it can't say).</summary>
        public volatile LightReading? Reading;
        public DateTime ReadAt = DateTime.MinValue;
        public int ReadingNow;
        /// <summary>Goes up on every change by hand, so a reading that started before it is not shown over it.</summary>
        public int Changes;
        /// <summary>One direct change at a time per light, kept the provider's gap apart.</summary>
        public readonly SemaphoreSlim Control = new(1, 1);
        public DateTime ControlAt = DateTime.MinValue;
    }

    private LightState State(string key) => _states.GetOrAdd(key, _ => new LightState());

    // ---------------------------------------------------------------- status

    public NetworkLightsDto Status(IReadOnlyList<LayoutItem> placed)
    {
        var paired = _store.All();
        var lights = new List<NetworkLightDto>();
        foreach (var l in paired)
        {
            var p = Provider(l.Brand);
            var st = State(l.LayoutKey);
            if (p is not null) EnsureDescribed(p, l, st);
            var d = st.Description;
            var controls = p is null ? null : ControlsOf(p, l);
            if (p is not null && controls!.ReadBack.Can) EnsureRead(p, l, st);
            var reading = st.Reading;
            lights.Add(new NetworkLightDto(l.LayoutKey, l.Brand, p?.DisplayName ?? l.Brand, l.Id, l.Name, l.Model, l.Address, p?.Tested ?? false,
                (d?.Kind ?? LightKind.Single).ToString().ToLowerInvariant(), d?.Points.Count ?? 0, p?.PointName ?? "light",
                placed.Any(i => i.Key == l.LayoutKey), st.Streaming,
                p is null ? "This version of RigControl doesn't know this kind of light." : st.Problem, d?.Note,
                controls, reading?.On, reading?.Brightness, reading?.Colour is { } c ? LightColour.Hex(c) : null,
                _store.Before(l.LayoutKey) is not null, p?.JoinsEverythingAtOnce ?? false, p?.FollowsWhenNotPlaced ?? false));
        }
        var found = _found.Select(f => new FoundLightDto(f.Provider.Brand, f.Provider.DisplayName, f.Light.Id, f.Light.Name, f.Light.Model, f.Light.Address,
            // By id, or by address for brands that only say who they are once paired.
            paired.Any(l => l.Brand == f.Provider.Brand && (l.Id == NetworkLightStore.TidyId(f.Light.Id) || LanAddress.Same(l.Address, f.Light.Address))))).ToList();
        var brands = _providers.Select(p => new NetworkBrandDto(p.Brand, p.DisplayName, p.Tested, p.Notes, p.Pairing.Steps, p.Pairing.ReturnsSecret,
            p.Pairing.ManualAddress, p.Pairing.Options ?? Array.Empty<PairingOption>(), p.Pairing.NothingFound, p.MaxFramesPerSecond,
            p.Pairing.CanSearch, p.FollowsWhenNotPlaced)).ToList();
        bool scanning;
        lock (_scanGate) scanning = _scan is not null;
        return new NetworkLightsDto(brands, lights, found, scanning, _scanProblems, Gaps, _scanStopped, _scanFinished);
    }

    /// <summary>
    /// True while a light of this brand takes colours from RigControl. Takes no lock and never touches the store:
    /// it is called from other modules while they hold their own locks.
    /// </summary>
    public bool IsStreaming(string brand) => _streaming.TryGetValue(brand, out var n) && n > 0;

    /// <summary>The description of a paired light once it has been read (null until then); reading starts in the background.</summary>
    public LightDescription? Description(PairedLight light)
    {
        var st = State(light.LayoutKey);
        if (Provider(light.Brand) is { } p) EnsureDescribed(p, light, st);
        return st.Description;
    }

    public string? Problem(PairedLight light) => State(light.LayoutKey).Problem;

    /// <summary>Reads what the light is made of, in the background. A light that didn't answer is asked again after ten seconds.</summary>
    private void EnsureDescribed(INetworkLightProvider p, PairedLight l, LightState st)
    {
        if (st.Description is not null || _disposed != 0) return;
        if ((DateTime.UtcNow - st.LoadFailedAt).TotalSeconds < 10) return;
        if (Interlocked.Exchange(ref st.Loading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(DescribeTime);
                st.Description = await p.DescribeAsync(_net, l, cts.Token).ConfigureAwait(false);
                // A problem with sending stays on show until the light is tried again.
                if (DateTime.UtcNow >= st.RetryAt) st.Problem = null;
            }
            catch (Exception ex)
            {
                var message = Plain(p, l, ex);
                if (st.Problem != message) _log.Warn($"{p.DisplayName} {l.Name}: {message}");
                st.Problem = message;
                st.LoadFailedAt = DateTime.UtcNow;
            }
            finally
            {
                Volatile.Write(ref st.Loading, 0);
            }
        });
    }

    /// <summary>A failure in words the user can act on. Providers' own plain messages are kept; network errors get the standard advice.</summary>
    private static string Plain(INetworkLightProvider p, PairedLight l, Exception ex)
    {
        while (ex is AggregateException { InnerException: not null } agg) ex = agg.InnerException!;
        if (ex is SecretRuleException or InvalidOperationException) return ex.Message;
        // The likely causes first (switched off, its local control switched off), the rare one (a new address) last.
        var check = "Check it is switched on" + (p.Pairing.NoAnswerHint is { Length: > 0 } hint ? " " + hint.Trim().TrimEnd('.') : "") + ".";
        return p.Pairing.ReturnsSecret
            ? $"It doesn't answer at {l.Address}. {check} If it got a new address, press Forget, then search and pair it again (its place on the layout is kept)."
            : p.Pairing.CanSearch
                ? $"It doesn't answer at {l.Address}. {check} If it got a new address, press Search for lights and RigControl picks the new one up."
                : $"It doesn't answer at {l.Address}. {check} If it got a new address, press Forget and pair it again at the new one (its place on the layout is kept).";
    }

    /// <summary>What a search found, made safe: text from the device tidied and cut to size.</summary>
    internal static FoundLight Tidy(FoundLight f) =>
        new(NetworkLightStore.TidyId(f.Id), DeviceText.Tidy(f.Name, 64), DeviceText.Tidy(f.Model, 64), f.Address) { Data = Tidy(f.Data) };

    private static IReadOnlyDictionary<string, string> Tidy(IReadOnlyDictionary<string, string> data)
    {
        var tidy = new Dictionary<string, string>();
        foreach (var (k, v) in data.Take(16)) tidy[DeviceText.Tidy(k, 40)] = DeviceText.Tidy(v, 1000);
        return tidy;
    }


    // ---------------------------------------------------------------- searching (only on the user's click)

    /// <summary>
    /// Searches for lights of every brand (or one) at the same time, each for a bounded time.
    /// Never runs by itself: only when the user asks. A light without a pairing key that moved to a new address
    /// is noted at its new address.
    /// </summary>
    public async Task ScanAsync(string? brand, CancellationToken ct)
    {
        var which = brand is null ? _providers : _providers.Where(p => p.Brand == brand).ToList();
        if (which.Count == 0) throw new ArgumentException("There's no light brand called " + brand + ".");
        CancellationTokenSource cts;
        lock (_scanGate)
        {
            if (_scan is not null) throw new InvalidOperationException("A search is already running.");
            _scan = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        try
        {
            var problems = new ConcurrentBag<string>();
            var results = await Task.WhenAll(which.Select(async p =>
            {
                using var one = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var time = p.SearchTime + TimeSpan.FromSeconds(2);
                one.CancelAfter(time < ScanTime ? time : ScanTime);
                var none = new List<(INetworkLightProvider Provider, FoundLight Light)>();
                try
                {
                    // Task.Run: a provider that blocks can't hold up the others.
                    var found = await Task.Run(() => p.DiscoverAsync(_net, one.Token), one.Token).WaitAsync(one.Token).ConfigureAwait(false);
                    // Everything a device said about itself is tidied and cut to size here, for every brand alike.
                    var kept = found.Where(f => LanAddress.IsPrivate(f.Address)).Select(Tidy).Where(f => f.Id != "")
                        .Take(MostFoundPerBrand).Select(f => (Provider: p, Light: f)).ToList();
                    return (Provider: p, Finished: true, Found: kept);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    problems.Add($"{p.DisplayName}: the search took too long and was stopped.");
                    return (Provider: p, Finished: true, Found: none);
                }
                catch (OperationCanceledException)
                {
                    // the user stopped the search before this brand was done: what was known about it before stays
                    return (Provider: p, Finished: false, Found: none);
                }
                catch (Exception ex)
                {
                    problems.Add($"{p.DisplayName}: couldn't search ({DeviceText.Tidy(ex.Message, 200).TrimEnd('.')}).");
                    return (Provider: p, Finished: true, Found: none);
                }
            })).ConfigureAwait(false);
            // A stopped search keeps what the brands that were done had found; the others keep their earlier list.
            var stopped = results.Any(r => !r.Finished);
            var found = results.Where(r => r.Finished).SelectMany(r => r.Found).ToList();
            var scanned = results.Where(r => r.Finished).Select(r => r.Provider.Brand).ToHashSet();
            _found = _found.Where(f => !scanned.Contains(f.Provider.Brand)).Concat(found).ToList();
            _scanProblems = problems.OrderBy(x => x, StringComparer.Ordinal).ToList();
            _scanFinished = which.Where(p => scanned.Contains(p.Brand)).Select(p => p.Brand).ToList();
            _scanStopped = stopped;
            await NoteNewAddressesAsync(found).ConfigureAwait(false);
            if (stopped)
                _log.Info("Search for lights: stopped" + (found.Count == 0 ? "." : "; found so far: " + Counts(found) + "."));
            else
                _log.Info(found.Count == 0 ? "Search for lights: none found on the network." : "Search for lights: found " + Counts(found) + ".");
            foreach (var problem in _scanProblems) _log.Warn("Search for lights: " + problem);
            if (stopped) throw new OperationCanceledException(cts.Token);
        }
        finally
        {
            lock (_scanGate) _scan = null;
            cts.Dispose();
        }
    }

    private static string Counts(IEnumerable<(INetworkLightProvider Provider, FoundLight Light)> found) =>
        string.Join(", ", found.GroupBy(f => f.Provider.DisplayName).Select(g => $"{g.Count()} {g.Key}"));

    /// <summary>
    /// A light without a pairing key that now answers somewhere else is noted at its new address, so it carries on
    /// after the router gave it a new one. An id is only what a device says about itself, so the address is left
    /// alone when the light still answers at its old address, or when more than one device claims to be it.
    /// </summary>
    private async Task NoteNewAddressesAsync(IReadOnlyList<(INetworkLightProvider Provider, FoundLight Light)> found)
    {
        foreach (var group in found.Where(f => !f.Provider.Pairing.ReturnsSecret).GroupBy(f => (f.Provider.Brand, f.Light.Id)).ToList())
        {
            var p = group.First().Provider;
            var key = NetworkLightStore.KeyFor(p.Brand, group.Key.Id);
            if (_store.Find(key) is not { } paired) continue;
            var addresses = group.Select(f => LanAddress.Normalise(f.Light.Address)).Distinct().ToList();
            if (addresses.Any(a => LanAddress.Same(a, paired.Address))) continue; // it still answers where it was
            if (addresses.Count > 1)
            {
                _log.Warn($"{addresses.Count} devices say they are {p.DisplayName} {paired.Name}; its address was left alone.");
                continue;
            }
            if (!_store.Readdress(key, addresses[0])) continue;
            _log.Info($"{p.DisplayName} {paired.Name} is now at {addresses[0]}.");
            await ResetAsync(key).ConfigureAwait(false);
        }
    }

    public void CancelScan()
    {
        lock (_scanGate)
        {
            try { _scan?.Cancel(); } catch (ObjectDisposedException) { /* just finished */ }
        }
    }

    // ---------------------------------------------------------------- pairing

    /// <summary>
    /// Pairs the light at <paramref name="address"/>, reads what it is made of, and saves it. Throws
    /// <see cref="ArgumentException"/> for a bad request and <see cref="InvalidOperationException"/> (plain words)
    /// when the light refuses or doesn't answer.
    /// </summary>
    public async Task<PairedLight> PairAsync(string brand, string? address, IReadOnlyDictionary<string, string>? options, CancellationToken ct)
    {
        var p = Provider(brand) ?? throw new ArgumentException("There's no light brand called " + brand + ".");
        if (address is null || !LanAddress.IsPrivate(address.Trim()))
            throw new ArgumentException("Pick a light found on your network, or type its address (for example 192.168.1.100).");
        address = LanAddress.Normalise(address);
        var found = _found.FirstOrDefault(f => f.Provider.Brand == brand && LanAddress.Same(f.Light.Address, address)).Light;
        if (found is null && !p.Pairing.ManualAddress)
            throw new ArgumentException($"Search for lights first, then pick the {p.DisplayName} it found.");
        var request = new PairRequest(address, found, CheckOptions(p, options));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PairTime);
        PairResult result;
        try
        {
            result = await p.PairAsync(_net, request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The {p.DisplayName} didn't answer: {(ex is OperationCanceledException ? "it took too long" : ex.Message.TrimEnd('.'))}.");
        }
        // A key belongs to the address the user picked; a provider can't move it somewhere else.
        if (!LanAddress.Same(result.Address, address))
            throw new InvalidOperationException($"The {p.DisplayName} answered from another address than the one picked, so it was not paired.");
        var id = NetworkLightStore.TidyId(result.Id);
        if (id == "") throw new InvalidOperationException($"The {p.DisplayName} didn't say which device it is, so it was not paired.");
        // What the device said about itself is tidied before it is saved, shown or logged. Keys are left as they are.
        result = result with
        {
            Id = id,
            Name = DeviceText.Tidy(result.Name, 64) is { Length: > 0 } tidyName ? tidyName : p.DisplayName,
            Model = DeviceText.Tidy(result.Model, 64),
            Data = Tidy(result.Data),
        };

        var key = brand == NetworkLightStore.NanoleafBrand ? $"nanoleaf|{result.Name}|{id}" : NetworkLightStore.KeyFor(brand, id);
        var trial = new PairedLight(brand, id, result.Name, result.Model, address, key, result.Data, result.Secrets);
        LightDescription? description = null;
        string? unread = null;
        try
        {
            // Its own time: a button pressed at the last second must not leave nothing for this.
            using var describe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            describe.CancelAfter(DescribeTime < TimeSpan.FromSeconds(15) ? DescribeTime : TimeSpan.FromSeconds(15));
            description = await p.DescribeAsync(_net, trial, describe.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidOperationException ex)
        {
            // The light itself says there is nothing here to control: it is not kept, and the message says so.
            throw new InvalidOperationException($"Not paired: {ex.Message.TrimEnd('.')}."
                + (p.Pairing.ReturnsSecret && p.Pairing.UnusedKeyHint is { Length: > 0 } hint ? " " + hint : ""));
        }
        catch (Exception ex)
        {
            // It paired but didn't answer just now. The pairing (and any key) is kept; it is read again by itself.
            var why = ex is OperationCanceledException or TimeoutException ? "it took too long" : DeviceText.Tidy(ex.Message, 200).TrimEnd('.');
            unread = $"Paired, but its {p.PointName}s couldn't be read yet ({why}). RigControl keeps trying.";
        }
        if (brand == NetworkLightStore.NanoleafBrand)
        {
            // One Nanoleaf at a time: the one paired before (or this one under an older name) is let go first,
            // or it would keep its last picture with nobody left to stop it.
            foreach (var old in _store.All().Where(l => l.Brand == brand && l.LayoutKey != key).ToList())
            {
                await ResetAsync(old.LayoutKey).ConfigureAwait(false);
                _states.TryRemove(old.LayoutKey, out _);
            }
        }
        await ResetAsync(key).ConfigureAwait(false);
        var saved = _store.Save(brand, result);
        var st = State(saved.LayoutKey);
        st.Description = description;
        st.Problem = unread;
        if (description is null)
        {
            st.LoadFailedAt = DateTime.UtcNow;
            _log.Warn($"{p.DisplayName} {saved.Name} paired at {address}, but its {p.PointName}s couldn't be read yet. RigControl keeps trying.");
        }
        else
        {
            _log.Info($"{p.DisplayName} {saved.Name} paired at {address}: {description.Points.Count} {p.PointName}(s)"
                      + (description.Kind == LightKind.Single ? "." : ", each can follow its own part of the effects."));
        }
        return saved;
    }

    private static IReadOnlyDictionary<string, string> CheckOptions(INetworkLightProvider p, IReadOnlyDictionary<string, string>? given)
    {
        var result = new Dictionary<string, string>();
        foreach (var o in p.Pairing.Options ?? Array.Empty<PairingOption>())
        {
            string? value = null;
            if (given is not null && given.TryGetValue(o.Key, out var v)) value = v;
            value ??= o.Default ?? (o.Choices.Count > 0 ? o.Choices[0].Value : null);
            if (value is null) continue;
            if (value.Length > 200 || (o.Choices.Count > 0 && o.Choices.All(c => c.Value != value)))
                throw new ArgumentException($"\"{o.Label}\" has no choice like that.");
            result[o.Key] = value;
        }
        return result;
    }

    /// <summary>Forgets a paired light (by its layout key). It is put back as it was first if it was following.</summary>
    public async Task<bool> ForgetAsync(string key)
    {
        var light = _store.Find(key);
        if (light is null) return false;
        await ResetAsync(key).ConfigureAwait(false);
        _store.Forget(key);
        _states.TryRemove(key, out _);
        _log.Info($"{Provider(light.Brand)?.DisplayName ?? light.Brand} {light.Name} forgotten.");
        return true;
    }

    /// <summary>Stops a light's stream (putting it back) and forgets what was read from it.</summary>
    private async Task ResetAsync(string key)
    {
        if (!_states.TryGetValue(key, out var st)) return;
        Task stopping;
        TimeSpan needs;
        lock (st)
        {
            StopRunner(st, restore: true);
            stopping = st.Stopping;
            needs = st.StoppingNeeds;
            st.Description = null;
            st.Problem = null;
            st.RetryAt = st.LoadFailedAt = DateTime.MinValue;
        }
        try { await stopping.WaitAsync(StopWait(needs)).ConfigureAwait(false); } catch { /* it keeps its last colours */ }
    }

    // ---------------------------------------------------------------- direct control (on/off, brightness, colour, give back)

    /// <summary>How long one direct change may take (a Hue Bridge with many lights needs a while).</summary>
    public TimeSpan ControlTime { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>How often a light is asked what it shows, at most, while the page asks for the list.</summary>
    public TimeSpan ReadEvery { get; init; } = TimeSpan.FromSeconds(15);

    private static LightControls ControlsOf(INetworkLightProvider p, PairedLight l)
    {
        try { return p.Controls(l); }
        catch { return LightControls.None("This light's controls couldn't be worked out."); }
    }

    /// <summary>Asks the light what it shows, in the background, at most every <see cref="ReadEvery"/>. Not while an effect drives it.</summary>
    private void EnsureRead(INetworkLightProvider p, PairedLight l, LightState st)
    {
        if (_disposed != 0 || st.Streaming || st.Runner is not null) return;
        if (DateTime.UtcNow - st.ReadAt < ReadEvery) return;
        if (Interlocked.Exchange(ref st.ReadingNow, 1) == 1) return;
        st.ReadAt = DateTime.UtcNow;
        var changes = Volatile.Read(ref st.Changes);
        _ = Task.Run(async () =>
        {
            LightReading? reading;
            try
            {
                using var cts = new CancellationTokenSource(DescribeTime < TimeSpan.FromSeconds(10) ? DescribeTime : TimeSpan.FromSeconds(10));
                reading = await p.ReadAsync(_net, l, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                reading = null; // not known; a problem with the light itself shows through its description
            }
            finally
            {
                Volatile.Write(ref st.ReadingNow, 0);
            }
            // A change by hand while it was being read wins: the reading is older than it.
            if (Volatile.Read(ref st.Changes) == changes) st.Reading = reading;
        });
    }

    /// <summary>What a paired light can be told directly; null when it isn't paired or its brand isn't known.</summary>
    public LightControls? Controls(string key) =>
        _store.Find(key) is { } l && Provider(l.Brand) is { } p ? ControlsOf(p, l) : null;

    /// <summary>
    /// Changes one light by hand. The first change remembers what it did before (for <see cref="GiveBackAsync"/>).
    /// A light an effect was driving is let go (put back) first. Throws <see cref="ArgumentException"/> for a light
    /// that isn't paired and <see cref="InvalidOperationException"/> (plain words) when it can't be done.
    /// </summary>
    public async Task SetAsync(string key, LightChange change, CancellationToken ct)
    {
        var (p, light) = Find(key);
        var can = Check(p, light, change);
        var st = State(key);
        Interlocked.Increment(ref st.Changes);
        await ControlledAsync(p, light, st, async token =>
        {
            // Taken over: what it did before is kept, once, so it can be given back later (even after a restart).
            if (can.GiveBack.Can && _store.Before(key) is null)
                _store.SetBefore(key, await p.SaveAsync(_net, light, token).ConfigureAwait(false));
            await p.SetAsync(_net, light, change, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        // What it shows now, as far as RigControl knows; it is read again later. A reading made during the change is older.
        Interlocked.Increment(ref st.Changes);
        var was = st.Reading;
        st.Reading = change.TurnsOff
            ? new LightReading(false, was?.Brightness, was?.Colour)
            : new LightReading(true, change.Brightness ?? was?.Brightness, change.Colour is { } c ? LightColour.Full(c) : was?.Colour);
        st.ReadAt = DateTime.UtcNow;
        _log.Info($"{p.DisplayName} {light.Name}: {Describe(change)} by hand.");
    }

    /// <summary>
    /// Throws unless the change can be made: <see cref="ArgumentException"/> for a light that isn't paired or an empty
    /// change, <see cref="InvalidOperationException"/> (saying why) for something the brand can't do.
    /// </summary>
    public void Check(string key, LightChange change)
    {
        var (p, light) = Find(key);
        Check(p, light, change);
    }

    private static LightControls Check(INetworkLightProvider p, PairedLight light, LightChange change)
    {
        var can = ControlsOf(p, light);
        if (change.IsEmpty) throw new ArgumentException("Say what to change: on, brightness or colour.");
        if ((change.On is not null || change.TurnsOff) && !can.Power.Can) throw new InvalidOperationException(can.Power.Why ?? "It can't be switched from here.");
        if (change.Brightness is not null && !change.TurnsOff && !can.Brightness.Can) throw new InvalidOperationException(can.Brightness.Why ?? "Its brightness can't be set from here.");
        if (change.Colour is not null && !change.TurnsOff && !can.Colour.Can) throw new InvalidOperationException(can.Colour.Why ?? "Its colour can't be set from here.");
        return can;
    }

    private static string Describe(LightChange change) =>
        change.TurnsOff ? "switched off"
        : string.Join(", ", new[]
        {
            "switched on",
            change.Brightness is { } b ? $"brightness {b}%" : null,
            change.Colour is { } c ? "colour " + LightColour.Hex(c) : null,
        }.Where(s => s is not null));

    /// <summary>Puts a light RigControl took over back to what it did before, and forgets that it took it over.</summary>
    public async Task GiveBackAsync(string key, CancellationToken ct)
    {
        var (p, light) = Find(key);
        var can = ControlsOf(p, light);
        if (!can.GiveBack.Can) throw new InvalidOperationException(can.GiveBack.Why ?? "This light can't be given back from here.");
        var saved = _store.Before(key) ?? throw new InvalidOperationException("RigControl hasn't changed this light, so there is nothing to give back.");
        var st = State(key);
        Interlocked.Increment(ref st.Changes);
        await ControlledAsync(p, light, st, token => p.GiveBackAsync(_net, light, saved, token), ct).ConfigureAwait(false);
        _store.SetBefore(key, null);
        Interlocked.Increment(ref st.Changes);
        st.Reading = null;
        st.ReadAt = DateTime.MinValue;
        _log.Info($"{p.DisplayName} {light.Name}: given back; it does what it did before RigControl changed it.");
    }

    /// <summary>
    /// "Everything at once" and "Turn all lights off" for the lights on the network: every paired light whose brand
    /// takes part and can do it. Each light is tried on its own; the ones that failed come back as plain sentences.
    /// </summary>
    /// <returns>How many lights were tried, and a sentence for each one that failed.</returns>
    public async Task<(int Tried, IReadOnlyList<string> Problems)> SetAllAsync(LightChange change, CancellationToken ct)
    {
        var jobs = new List<Task<string?>>();
        foreach (var l in _store.All())
        {
            if (Provider(l.Brand) is not { } p || !p.JoinsEverythingAtOnce) continue;
            var can = ControlsOf(p, l);
            // A brand that can't do it (a LeetDesk takes no colour) is left out rather than failing.
            if (change.TurnsOff ? !can.Power.Can : (change.Colour is not null && !can.Colour.Can) || (change.Brightness is not null && !can.Brightness.Can)) continue;
            jobs.Add(Task.Run(async () =>
            {
                try
                {
                    await SetAsync(l.LayoutKey, change, ct).ConfigureAwait(false);
                    return null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    return $"{p.DisplayName} {l.Name}: {ex.Message.TrimEnd('.')}.";
                }
            }, ct));
        }
        var results = await Task.WhenAll(jobs).ConfigureAwait(false);
        return (jobs.Count, results.Where(r => r is not null).Select(r => r!).ToList());
    }

    private (INetworkLightProvider Provider, PairedLight Light) Find(string key)
    {
        var light = _store.Find(key ?? "") ?? throw new ArgumentException("That light isn't paired.");
        var p = Provider(light.Brand) ?? throw new InvalidOperationException("This version of RigControl doesn't know this kind of light.");
        return (p, light);
    }

    /// <summary>
    /// Runs one direct change: an effect driving the light is let go first (it is put back as it was), changes to one
    /// light go one at a time and the brand's gap apart, and a failure is said in plain words (and shown on the light).
    /// </summary>
    private async Task ControlledAsync(INetworkLightProvider p, PairedLight light, LightState st, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        Task stopping;
        TimeSpan needs;
        lock (st)
        {
            StopRunner(st, restore: true);
            stopping = st.Stopping;
            needs = st.StoppingNeeds;
        }
        try { await stopping.WaitAsync(StopWait(needs), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* it keeps its last colours; the change below goes ahead */ }

        await st.Control.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var left = p.ControlGap - (DateTime.UtcNow - st.ControlAt);
            if (left > TimeSpan.Zero) await Task.Delay(left, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ControlTime);
            try
            {
                await action(cts.Token).ConfigureAwait(false);
                st.Problem = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = ex is OperationCanceledException or TimeoutException
                    ? $"It didn't answer in time at {light.Address}. Check it is switched on" + (p.Pairing.NoAnswerHint is { Length: > 0 } hint ? " " + hint.Trim().TrimEnd('.') : "") + "."
                    : Plain(p, light, ex);
                _log.Warn($"{p.DisplayName} {light.Name}: couldn't be changed: {message}");
                st.Problem = message;
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            st.ControlAt = DateTime.UtcNow;
            st.Control.Release();
        }
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Each paired light as one box for the layout page.</summary>
    public IReadOnlyList<LayoutZoneDto> LayoutZones(IReadOnlyList<LayoutItem> items)
    {
        var zones = new List<LayoutZoneDto>();
        foreach (var l in _store.All())
        {
            if (Provider(l.Brand) is not { } p) continue;
            var d = Description(l);
            var (mw, mh, matrix) = d is { Kind: LightKind.Points } ? PointMatrix(d.Points) : (0, 0, null);
            zones.Add(new LayoutZoneDto(l.LayoutKey, $"{l.Name} ({p.DisplayName})", LedLayout.WholeDevice, "Whole device", Math.Max(1, d?.Points.Count ?? 1),
                mw, mh, matrix, d is not null, items.FirstOrDefault(i => i.Key == l.LayoutKey && i.Zone == LedLayout.WholeDevice),
                // Bars as they hang on the wall (0-1 inside the box), so the page can show the real shape.
                d is { DrawAsBars: true } ? d.Points.Select(pt => new[] { pt.X1, pt.Y1, pt.X2, pt.Y2 }).ToList() : null));
        }
        return zones;
    }

    /// <summary>A rough grid of where the points are, so the layout page can draw them.</summary>
    internal static (int W, int H, List<int>? Matrix) PointMatrix(IReadOnlyList<LightPoint> points)
    {
        const int W = 20, H = 12;
        var m = Enumerable.Repeat(-1, W * H).ToList();
        for (var i = 0; i < points.Count; i++)
        {
            var cell = (int)Math.Round(Math.Clamp(points[i].Y, 0, 1) * (H - 1)) * W + (int)Math.Round(Math.Clamp(points[i].X, 0, 1) * (W - 1));
            if (m[cell] < 0) m[cell] = i;
        }
        return (W, H, m);
    }

    // ---------------------------------------------------------------- the light sync's hook

    /// <summary>
    /// One step of the light sync: works out each paired light's colours from the picture and hands them to that
    /// light's own sender. Returns at once; a slow light never holds up the others or the PC's own lights.
    /// </summary>
    public void Frame(NetworkFrame f)
    {
        if (_disposed != 0) return;
        var paired = _store.All();
        // A light that is still being sent colours but is no longer paired (it was replaced) is let go.
        foreach (var (key, orphan) in _states)
        {
            if (orphan.Runner is null || paired.Any(l => l.LayoutKey == key)) continue;
            lock (orphan) StopRunner(orphan, restore: true);
            _states.TryRemove(key, out _);
        }
        foreach (var l in paired)
        {
            if (Provider(l.Brand) is not { } p) continue;
            var st = State(l.LayoutKey);
            var item = f.Grid is null ? null : f.Items.FirstOrDefault(i => i.Key == l.LayoutKey);
            if (item is null && !p.FollowsWhenNotPlaced)
            {
                lock (st) StopRunner(st, restore: true);
                continue;
            }
            EnsureDescribed(p, l, st);
            var d = st.Description;
            if (d is null || d.Points.Count == 0)
            {
                lock (st) StopRunner(st, restore: true);
                continue;
            }
            var colours = item is null || f.Grid is null ? Enumerable.Repeat(f.OneColour, d.Points.Count).ToArray() : Sample(d, item, f);
            lock (st)
            {
                if (st.Runner is { Ended: true }) st.Runner = null;
                if (st.Runner is null)
                {
                    if (!st.Stopping.IsCompleted || DateTime.UtcNow < st.RetryAt) continue;
                    st.Runner = new Runner(this, p, l, d, st);
                }
                st.Runner.Post(colours);
            }
        }
    }

    /// <summary>The colour of each light point: the part of the picture under it, inside the light's box on the layout.</summary>
    internal static int[] Sample(LightDescription d, LayoutItem item, NetworkFrame f)
    {
        var grid = f.Grid!;
        var result = new int[d.Points.Count];
        switch (d.Kind)
        {
            case LightKind.Single:
                Array.Fill(result, f.Shade(ColorMath.Region(grid, f.GridW, f.GridH, item.X, item.Y, item.W, item.H)));
                break;
            case LightKind.Strip:
                foreach (var (led, x, y, w, h) in LedLayout.LedRects(item, new RgbZone { LedsCount = (uint)d.Points.Count }))
                    result[led] = f.Shade(ColorMath.Region(grid, f.GridW, f.GridH, x, y, w, h));
                break;
            default:
                var rw = Math.Max(item.W * d.PointSize, 1.5 / f.GridW);
                var rh = Math.Max(item.H * d.PointSize, 1.5 / f.GridH);
                for (var i = 0; i < result.Length; i++)
                {
                    var pt = d.Points[i];
                    var px = Math.Clamp(item.X + pt.X * item.W - rw / 2, 0, 1 - rw);
                    var py = Math.Clamp(item.Y + pt.Y * item.H - rh / 2, 0, 1 - rh);
                    result[i] = f.Shade(ColorMath.Region(grid, f.GridW, f.GridH, px, py, rw, rh));
                }
                break;
        }
        return result;
    }

    /// <summary>Stops every stream and puts the lights back as they were (when the light sync stops).</summary>
    public void StopAll()
    {
        var tasks = new List<Task>();
        var needs = TimeSpan.Zero;
        foreach (var st in _states.Values)
        {
            lock (st)
            {
                StopRunner(st, restore: true);
                if (st.Stopping.IsCompleted) continue;
                tasks.Add(st.Stopping);
                if (st.StoppingNeeds > needs) needs = st.StoppingNeeds;
            }
        }
        try { Task.WaitAll(tasks.ToArray(), StopWait(needs)); } catch { /* a light that didn't answer keeps its last colours */ }
    }

    /// <summary>How long a stop is waited for: the light's own budget (the usual one when it names none), and a little more.</summary>
    private TimeSpan StopWait(TimeSpan needs) => (needs > StopTime ? needs : StopTime) + TimeSpan.FromSeconds(1);

    /// <summary>Call with the state locked.</summary>
    private static void StopRunner(LightState st, bool restore)
    {
        var r = st.Runner;
        if (r is null) return;
        st.Runner = null;
        st.StoppingNeeds = r.StopNeeds;
        st.Stopping = r.StopAsync(restore);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelScan();
        StopAll();
    }

    // ---------------------------------------------------------------- one light's sender

    /// <summary>
    /// Sends one light its frames at the pace it can take: the newest frame only, no faster than the provider allows,
    /// an unchanged frame again only when the keep-alive time is up. It lives while its session is healthy; after a
    /// failure it ends and the light is tried again after <see cref="RetryAfter"/>.
    /// </summary>
    private sealed class Runner
    {
        private readonly NetworkLights _owner;
        private readonly INetworkLightProvider _provider;
        private readonly PairedLight _light;
        private readonly LightDescription _description;
        private readonly LightState _state;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private volatile int[]? _latest;
        private volatile bool _restore = true;
        private volatile bool _ended;
        private long _stopNeedsTicks;

        public Runner(NetworkLights owner, INetworkLightProvider provider, PairedLight light, LightDescription description, LightState state)
        {
            _owner = owner;
            _provider = provider;
            _light = light;
            _description = description;
            _state = state;
            _task = Task.Run(LoopAsync);
        }

        public bool Ended => _ended;

        /// <summary>How long putting this light back may take (zero until it has started).</summary>
        public TimeSpan StopNeeds => TimeSpan.FromTicks(Interlocked.Read(ref _stopNeedsTicks));

        public void Post(int[] colours) => _latest = colours;

        private TimeSpan StopBudgetOf(ILightSession session)
        {
            var asked = session.StopBudget;
            if (asked <= _owner.StopTime) return _owner.StopTime;
            return asked < LongestStop ? asked : LongestStop;
        }

        public Task StopAsync(bool restore)
        {
            _restore = restore;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already over */ }
            return _task;
        }

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            var who = $"{_provider.DisplayName} {_light.Name}";
            ILightSession? session = null;
            var counted = false;
            try
            {
                while (_latest is null) await Task.Delay(10, ct).ConfigureAwait(false);
                using (var start = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    start.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        session = await _provider.StartAsync(_owner._net, _light, _description, start.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("it didn't answer in time");
                    }
                }
                Interlocked.Exchange(ref _stopNeedsTicks, StopBudgetOf(session).Ticks);
                _state.Streaming = true;
                _owner._streaming.AddOrUpdate(_provider.Brand, 1, (_, n) => n + 1);
                counted = true;
                _state.Problem = null;
                _owner._log.Info(_description.Kind == LightKind.Single ? $"{who} follows the lights."
                    : $"{who}: {_description.Points.Count} {_provider.PointName}(s) follow their part of the picture.");

                int[]? lastSent = null;
                var sentAt = DateTime.MinValue;
                while (!ct.IsCancellationRequested)
                {
                    var fps = session.MaxFramesPerSecond > 0 ? session.MaxFramesPerSecond : _provider.MaxFramesPerSecond;
                    var gap = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 0.1, 100));
                    var now = DateTime.UtcNow;
                    if (now - sentAt < gap)
                    {
                        var left = gap - (now - sentAt);
                        await Task.Delay(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                        continue;
                    }
                    var frame = _latest!;
                    var keep = session.KeepAlive;
                    if (lastSent is not null && keep > TimeSpan.Zero && now - sentAt < keep && frame.AsSpan().SequenceEqual(lastSent))
                    {
                        await Task.Delay(10, ct).ConfigureAwait(false);
                        continue;
                    }
                    await session.SendAsync(frame, ct).ConfigureAwait(false);
                    lastSent = frame;
                    sentAt = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // stopped on purpose
            }
            catch (Exception ex)
            {
                var problem = Plain(_provider, _light, ex);
                var retry = (int)Math.Round(_owner.RetryAfter.TotalSeconds);
                if (_state.Problem != problem) _owner._log.Warn($"{who}: {problem.TrimEnd('.')}; trying again in {retry} s.");
                _state.Problem = problem;
                _state.RetryAt = DateTime.UtcNow + _owner.RetryAfter;
                _state.Description = null; // read again (it may have changed, or moved to another address)
                _restore = false;          // it isn't answering: nothing to put back
            }
            finally
            {
                _state.Streaming = false;
                if (counted) _owner._streaming.AddOrUpdate(_provider.Brand, 0, (_, n) => Math.Max(0, n - 1));
                if (session is not null)
                {
                    if (_restore)
                    {
                        // The log says "put back" only when it was.
                        try
                        {
                            using var stop = new CancellationTokenSource(StopBudgetOf(session));
                            await session.StopAsync(stop.Token).ConfigureAwait(false);
                            _owner._log.Info($"{who}: stopped following; put back as it was.");
                        }
                        catch (Exception ex)
                        {
                            var why = ex is OperationCanceledException or TimeoutException ? "it took too long"
                                : ex is InvalidOperationException or IOException ? DeviceText.Tidy(ex.Message, 200).TrimEnd('.')
                                : "it didn't answer";
                            _owner._log.Warn($"{who}: stopped following, but it could not be put back as it was ({why}). It keeps its last colour until you change it.");
                        }
                    }
                    try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
                }
                _ended = true;
                _cts.Dispose();
            }
        }
    }
}
