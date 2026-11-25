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

/// <summary>One thing a light can or can't be told directly. <see cref="Why"/> says, in plain words, why not.</summary>
public sealed record LightAbility(bool Can, string? Why = null)
{
    public static readonly LightAbility Yes = new(true);
    public static LightAbility No(string why) => new(false, why);
}

/// <summary>
/// What a paired light can be told directly, without an effect playing: switched on or off, its brightness, one
/// colour, read back (what it shows now), and given back (put back to what it did before RigControl changed it).
/// </summary>
public sealed record LightControls(LightAbility Power, LightAbility Brightness, LightAbility Colour, LightAbility ReadBack, LightAbility GiveBack)
{
    public static LightControls None(string why) =>
        new(LightAbility.No(why), LightAbility.No(why), LightAbility.No(why), LightAbility.No(why), LightAbility.No(why));
}

/// <summary>What a light shows now, as far as it says. Null = it doesn't say (or can't be asked).</summary>
/// <param name="Brightness">0-100.</param>
/// <param name="Colour">0xRRGGBB at full strength (brightness is separate); null for an effect or a white.</param>
public sealed record LightReading(bool? On, int? Brightness, int? Colour);

/// <summary>
/// One direct change. Null fields stay as they are. A colour or a brightness without <see cref="On"/> also switches
/// the light on (you want to see what you picked). A colour of black switches it off.
/// </summary>
/// <param name="Brightness">0-100 (a brand without a 0 takes its lowest).</param>
/// <param name="Colour">0xRRGGBB.</param>
public sealed record LightChange(bool? On = null, int? Brightness = null, int? Colour = null)
{
    public bool IsEmpty => On is null && Brightness is null && Colour is null;

    /// <summary>True when the light ends up off.</summary>
    public bool TurnsOff => On == false || (Colour is { } c && (c & 0xFFFFFF) == 0);
}

/// <summary>Colour sums every provider needs for direct control.</summary>
public static class LightColour
{
    /// <summary>Hue 0-359, saturation 0-100, value 0-100.</summary>
    public static (int Hue, int Sat, int Value) ToHsv(int rgb)
    {
        double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        var hue = (int)Math.Round(h) % 360;
        var sat = max <= 1e-9 ? 0 : (int)Math.Round(d / max * 100);
        return (hue, sat, (int)Math.Round(max * 100));
    }

    /// <summary>0xRRGGBB from hue 0-359, saturation 0-100, value 0-100.</summary>
    public static int FromHsv(double hue, double sat, double value)
    {
        var h = ((hue % 360) + 360) % 360;
        var s = Math.Clamp(sat, 0, 100) / 100.0;
        var v = Math.Clamp(value, 0, 100) / 100.0;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        int To(double v2) => (int)Math.Round(Math.Clamp(v2 + m, 0, 1) * 255);
        return (To(r) << 16) | (To(g) << 8) | To(b);
    }

    /// <summary>The same colour at full strength (its brightest part at 255), so brightness can be told apart.</summary>
    public static int Full(int rgb)
    {
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        var max = Math.Max(r, Math.Max(g, b));
        if (max == 0) return 0;
        return ((int)Math.Round(r * 255.0 / max) << 16) | ((int)Math.Round(g * 255.0 / max) << 8) | (int)Math.Round(b * 255.0 / max);
    }

    public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6");
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

    // ---- direct control (no effect needed). Each call is one short exchange with the light; nothing is kept open.

    /// <summary>What this light can be told directly. A brand that can't do something says why, rather than faking it.</summary>
    LightControls Controls(PairedLight light) => LightControls.None("This version of RigControl can't control this kind of light directly.");

    /// <summary>
    /// True when "Everything at once" and "Turn all lights off" also reach this brand. False for a brand where one
    /// paired entry can be a whole house of lights (a Hue Bridge): it is only changed from its own controls.
    /// </summary>
    bool JoinsEverythingAtOnce => true;

    /// <summary>The least time between two direct changes to one light (some lights only take so many commands a minute).</summary>
    TimeSpan ControlGap => TimeSpan.FromMilliseconds(250);

    /// <summary>What the light shows now. Only called when <see cref="Controls"/> says it can be read back.</summary>
    Task<LightReading> ReadAsync(ILightNetwork net, PairedLight light, CancellationToken ct) =>
        throw new InvalidOperationException("This light can't say what it shows.");

    /// <summary>
    /// What the light is doing now, as text <see cref="GiveBackAsync"/> understands later. Called once, just before
    /// RigControl first changes the light; the text is kept with the pairing (never a secret).
    /// </summary>
    Task<string> SaveAsync(ILightNetwork net, PairedLight light, CancellationToken ct) =>
        throw new InvalidOperationException("RigControl can't read what this light was doing, so it can't give it back.");

    /// <summary>Switches it on or off, sets its brightness or one colour.</summary>
    Task SetAsync(ILightNetwork net, PairedLight light, LightChange change, CancellationToken ct) =>
        throw new InvalidOperationException("This version of RigControl can't control this kind of light directly.");

    /// <summary>Puts the light back to what <paramref name="saved"/> (from <see cref="SaveAsync"/>) says it did.</summary>
    Task GiveBackAsync(ILightNetwork net, PairedLight light, string saved, CancellationToken ct) =>
        throw new InvalidOperationException("RigControl can't give this light back.");
}
