namespace RigControl.Lighting.Network;

// The contract every Wi-Fi / network light brand implements. One class per brand under Network/Providers/.
// A provider with a parameterless constructor is found by itself (NetworkLights.BuiltIn): nothing else needs editing.

/// <summary>A choice the user makes when pairing (for example which kind of Cololight it is).</summary>
public sealed record PairingChoice(string Value, string Label);

public sealed record PairingOption(string Key, string Label, IReadOnlyList<PairingChoice> Choices, string? Default = null);

/// <summary>How a brand is paired, as data the page shows: what the user does, in order, in plain words.</summary>
/// <param name="Steps">What the user does, in order.</param>
/// <param name="ReturnsSecret">True when the device hands RigControl a key at pairing (kept protected, see <see cref="NetworkLightStore"/>).</param>
/// <param name="ManualAddress">True when the user may type the light's address instead of searching.</param>
/// <param name="Options">Choices to make when pairing; null for none.</param>
/// <param name="NothingFound">What to check when a search finds none of this brand.</param>
/// <param name="CanSearch">False for a brand that can't be searched for at all (the user types the address).</param>
/// <param name="NoAnswerHint">
/// What to check first when a paired light stops answering, as the end of the sentence "Check it is switched on ..."
/// (for example "and that LAN Control is still on for it in the Govee Home app"); null for none.
/// </param>
/// <param name="UnusedKeyHint">
/// For a brand that hands over a key: what the user can do about a key the device made in a pairing that was then
/// not kept (a full sentence); null for none.
/// </param>
public sealed record PairingInfo(IReadOnlyList<string> Steps, bool ReturnsSecret, bool ManualAddress = true,
    IReadOnlyList<PairingOption>? Options = null, string? NothingFound = null, bool CanSearch = true,
    string? NoAnswerHint = null, string? UnusedKeyHint = null);

/// <summary>Text that came from a device: never trusted as it is.</summary>
public static class DeviceText
{
    /// <summary>
    /// A name, model or id a device sent, made safe to show, save and log: no control characters (so a name can't
    /// start a line of its own in the log), no spaces around it, and no longer than <paramref name="most"/>.
    /// </summary>
    public static string Tidy(string? text, int most = 64)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = new string(text.Where(c => !char.IsControl(c) && c != (char)0x2028 && c != (char)0x2029).Take(most * 4).ToArray()).Trim();
        return clean.Length > most ? clean[..most].TrimEnd() : clean;
    }
}

/// <summary>A light a search found. <see cref="Id"/> is the device's own lasting id (never its address).</summary>
public sealed record FoundLight(string Id, string Name, string Model, string Address)
{
    /// <summary>Anything else the search learned that pairing needs (a port, a model code); never a secret.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

/// <param name="Address">The private IPv4 address to pair with (already checked to be on the user's own network).</param>
/// <param name="Found">The search result for that address, when there was one.</param>
/// <param name="Options">The user's answers to <see cref="PairingInfo.Options"/>, by key.</param>
public sealed record PairRequest(string Address, FoundLight? Found, IReadOnlyDictionary<string, string> Options);

/// <summary>What pairing produced. Secrets are protected and stored by the caller; a provider never stores anything itself.</summary>
public sealed record PairResult(string Id, string Name, string Model, string Address)
{
    /// <summary>Keys the device handed over, by name ("token" for a single one). Empty for brands without pairing.</summary>
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();
    /// <summary>Plain facts to remember (a port, the chosen type, a certificate fingerprint); never a secret.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

/// <summary>A paired light as a provider sees it. Secrets come out only for the paired address.</summary>
public sealed class PairedLight
{
    private readonly IReadOnlyDictionary<string, string> _secrets;

    public PairedLight(string brand, string id, string name, string model, string address, string layoutKey,
        IReadOnlyDictionary<string, string>? data = null, IReadOnlyDictionary<string, string>? secrets = null)
    {
        Brand = brand;
        Id = id;
        Name = name;
        Model = model;
        Address = address;
        LayoutKey = layoutKey;
        Data = data ?? new Dictionary<string, string>();
        _secrets = secrets ?? new Dictionary<string, string>();
    }

    public string Brand { get; }
    public string Id { get; }
    public string Name { get; }
    public string Model { get; }
    public string Address { get; }
    /// <summary>The light's key on the layout: "net|&lt;brand&gt;|&lt;id&gt;".</summary>
    public string LayoutKey { get; }
    public IReadOnlyDictionary<string, string> Data { get; }

    /// <summary>The stored key of that name, tied to <see cref="Address"/>; null when there is none (or it can't be read on this Windows account).</summary>
    public BoundSecret? Secret(string name = "token") =>
        _secrets.TryGetValue(name, out var v) && v.Length > 0 ? BoundSecret.For(v, Address) : null;

    /// <summary>The stored key, or a plain-words error when it is missing or can't be read on this Windows account.</summary>
    public BoundSecret RequireSecret(string name = "token") =>
        Secret(name) ?? throw new InvalidOperationException("Its pairing can't be read on this Windows account; pair it again.");

    public override string ToString() => $"{Brand} {Name} at {Address}";
}

public enum LightKind
{
    /// <summary>One colour for the whole light.</summary>
    Single,
    /// <summary>Lights in a row whose place is not known: the user's box on the layout is cut into equal slices along its longer side.</summary>
    Strip,
    /// <summary>Lights with known places (0-1 across the device, y down).</summary>
    Points,
}

/// <summary>
/// One light point: a panel, a bulb, a LED. X and Y are its centre, 0-1 across the whole device (y down, like the
/// layout picture); ignored for a strip. X1..Y2 are the two ends of a bar-shaped light for drawing (leave 0 otherwise).
/// </summary>
public sealed record LightPoint(int Id, double X = 0.5, double Y = 0.5, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0);

/// <summary>What a paired light is made of. A frame is one colour per entry of <see cref="Points"/>, in this order.</summary>
public sealed record LightDescription(LightKind Kind, IReadOnlyList<LightPoint> Points)
{
    /// <summary>For <see cref="LightKind.Points"/>: how much of the box each point looks at (0.12 = 12% of its width and height).</summary>
    public double PointSize { get; init; } = 0.12;
    /// <summary>True when the points are bars the page should draw from their two ends.</summary>
    public bool DrawAsBars { get; init; }
    /// <summary>Something the user should know about this light right now (plain words), or null.</summary>
    public string? Note { get; init; }

    public static LightDescription Single() => new(LightKind.Single, new[] { new LightPoint(0) });

    public static LightDescription Strip(int count) =>
        new(LightKind.Strip, Enumerable.Range(0, Math.Max(1, count)).Select(i => new LightPoint(i)).ToList());
}

/// <summary>A light that is being sent colours. Made by <see cref="INetworkLightProvider.StartAsync"/>.</summary>
public interface ILightSession : IAsyncDisposable
{
    /// <summary>How often this light takes a frame right now; 0 = the provider's <see cref="INetworkLightProvider.MaxFramesPerSecond"/>.</summary>
    double MaxFramesPerSecond => 0;

    /// <summary>
    /// A frame that did not change is sent again after this long, so the light doesn't fall back to its own effect.
    /// Zero = send every frame, changed or not.
    /// </summary>
    TimeSpan KeepAlive => TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long <see cref="StopAsync"/> may need to put everything back (a bridge with many lights needs longer than
    /// one light). Zero = the usual few seconds.
    /// </summary>
    TimeSpan StopBudget => TimeSpan.Zero;

    /// <summary>One frame: a colour (0xRRGGBB) per light point, in the description's order. Called one at a time.</summary>
    Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct);

    /// <summary>
    /// Stops and puts the light back as it was before <see cref="INetworkLightProvider.StartAsync"/>. Throws (in plain
    /// words) when the light could not be put back, so the log never says it was.
    /// </summary>
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// One brand of network light. Rules for every provider:
///  - local control only: no cloud account, no sign-in page, nothing sent outside the user's own network;
///  - only <see cref="ILightNetwork"/> is used to reach the network (never a socket or HttpClient of its own);
///  - a secret is only used through <see cref="BoundSecret"/> / <see cref="HttpAsk.Secret"/>, and never logged;
///  - nothing is written to the light's permanent memory, and <see cref="ILightSession.StopAsync"/> puts back what was there;
///  - messages in exceptions are plain sentences the user can act on (they are shown as they are).
/// </summary>
public interface INetworkLightProvider
{
    /// <summary>Lower-case id used in keys and RPCs, e.g. "wled".</summary>
    string Brand { get; }
    /// <summary>The name people know, e.g. "WLED".</summary>
    string DisplayName { get; }
    /// <summary>True only when this has been tried on a real device of this brand.</summary>
    bool Tested { get; }
    /// <summary>What this brand can and can't do here, in a sentence or two.</summary>
    string Notes { get; }
    PairingInfo Pairing { get; }
    /// <summary>The most frames a second this brand should be sent.</summary>
    double MaxFramesPerSecond { get; }
    /// <summary>What one light point is called in messages ("panel", "LED", "light").</summary>
    string PointName => "light";
    /// <summary>
    /// True when a paired light that is not placed on the layout should still follow the picture's one colour.
    /// False leaves an unplaced light alone.
    /// </summary>
    bool FollowsWhenNotPlaced => true;

    /// <summary>How long a search for this brand takes at most. The search is stopped a little after that.</summary>
    TimeSpan SearchTime => TimeSpan.FromSeconds(6);

    /// <summary>
    /// Looks for this brand's lights. Only ever called after the user pressed search. Must finish by itself within
    /// <see cref="SearchTime"/> and return what it found by then.
    /// </summary>
    Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct);

    /// <summary>Pairs with the light at the request's address (or just checks that it is one, for brands without pairing).</summary>
    Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct);

    /// <summary>Reads what the light is made of. Throws when it doesn't answer.</summary>
    Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct);

    /// <summary>Remembers the light's current state and gets it ready to take frames.</summary>
    Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct);
}
