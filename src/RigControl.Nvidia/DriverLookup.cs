using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace RigControl.Nvidia;

/// <summary>
/// RigControl can't tell which driver is right for this PC, so it offers none. The message is plain text for the user
/// (what is in the way and what to do instead).
/// </summary>
public sealed class DriverLookupException : Exception
{
    public DriverLookupException(string message) : base(message) { }
}

/// <summary>
/// Finds NVIDIA's newest driver for the graphics card and Windows version of THIS PC, the same way NVIDIA's own
/// download page does: the card's name is looked up in NVIDIA's public product list to get its ids, and the answer is
/// only accepted if NVIDIA's own list of supported cards and Windows versions in it names this card and this Windows.
/// When any step is unsure, no driver is offered (<see cref="DriverLookupException"/> says why). Only NVIDIA's public
/// lists are fetched; nothing about this PC is sent except the ids of the card model and Windows version.
/// </summary>
internal static class DriverLookup
{
    public const string ProductsUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    public const string SeriesUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2";
    private const string LookupUrl = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";
    private const string UseNvidiaPage = " Use NVIDIA's driver page to pick the right one.";

    /// <summary>NVIDIA's product type 1 is "GeForce": the only family the Game Ready driver lookup below is for.</summary>
    private const int GeForceType = 1;

    /// <summary>A card on NVIDIA's download page: the series it is listed under and its own id.</summary>
    internal sealed record Product(int SeriesId, int ProductId, string Name);

    internal sealed record Series(int Id, int TypeId, string Name);

    /// <param name="FromBuiltInList">The ids come from the checked list below, not from a name search.</param>
    /// <param name="Card">The card's name as <see cref="Normalise"/> gives it (for comparing).</param>
    /// <param name="CardName">The card's name as the driver reports it (for messages).</param>
    internal sealed record Ids(int SeriesId, int ProductId, int OsId, string OsName, string Card, bool FromBuiltInList, string CardName);

    /// <summary>
    /// Ids checked by hand against NVIDIA's list (docs/protocols/nvidia.md 6.2), so these cards need no extra request.
    /// Key: the card's name as <see cref="Normalise"/> gives it.
    /// </summary>
    private static readonly Dictionary<string, (int SeriesId, int ProductId)> Known = new(StringComparer.Ordinal)
    {
        ["geforce rtx 4070 ti"] = (127, 1001),
    };

    /// <summary>"NVIDIA GeForce RTX 4070  Ti" and "GeForce RTX 4070 Ti" are the same card: lower case, one space, no maker.</summary>
    public static string Normalise(string? name)
    {
        var words = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 0 && words[0].Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) && words.Count > 1) words.RemoveAt(0);
        return string.Join(' ', words).ToLowerInvariant();
    }

    /// <summary>NVIDIA's id and name for this Windows. Only 64-bit (x64) Windows 10 and 11 get the current drivers.</summary>
    public static (int Id, string Name) Os(Version windows, Architecture architecture)
    {
        if (architecture != Architecture.X64)
            throw new DriverLookupException("RigControl only checks NVIDIA drivers for 64-bit (x64) Windows." + UseNvidiaPage);
        if (windows.Major == 10 && windows.Build >= 22000) return (135, "Windows 11");
        if (windows.Major == 10) return (57, "Windows 10 64-bit");
        throw new DriverLookupException($"RigControl only checks NVIDIA drivers for Windows 10 and 11; this is Windows {windows.Major}.{windows.Minor}." + UseNvidiaPage);
    }

    /// <summary>
    /// A guard before any request: when RigControl's built-in card list knows a card of this PC and says it is not a
    /// GeForce card (a Quadro, an RTX A-series, a Tesla, a TITAN), no driver is looked for. A card the list does not
    /// know, or knows without a family, passes: the checks against NVIDIA's own lists still apply to it.
    /// </summary>
    public static void RefuseOtherFamilies(IReadOnlyList<RigControl.Core.Hardware.GpuInfo> cards)
    {
        foreach (var card in cards)
        {
            if (!card.Known || card.VendorId != RigControl.Core.Hardware.GpuDatabase.NvidiaVendorId) continue;
            if (string.IsNullOrEmpty(card.Family) || card.Family.StartsWith("GeForce", StringComparison.Ordinal)) continue;
            throw new DriverLookupException($"The {card.Name} is not a GeForce card, and RigControl only checks GeForce drivers." + UseNvidiaPage);
        }
    }

    /// <summary>The one card name of this PC. Different NVIDIA cards in one PC may need different drivers, so that is refused.</summary>
    public static string OneCard(IReadOnlyList<string> gpuNames)
    {
        var names = gpuNames.Select(Normalise).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) throw new DriverLookupException("RigControl could not read the graphics card's name, so it can't tell which driver fits." + UseNvidiaPage);
        if (names.Count > 1)
            throw new DriverLookupException("This PC has different NVIDIA graphics cards (" + string.Join(", ", gpuNames.Distinct()) + "); RigControl can't tell which driver fits all of them." + UseNvidiaPage);
        return names[0];
    }

    public static IReadOnlyList<Product> ParseProducts(string xml) => Values(xml)
        .Where(v => v.Parent is not null).Select(v => new Product(v.Parent!.Value, v.Value, v.Name)).ToList();

    public static IReadOnlyList<Series> ParseSeries(string xml) => Values(xml)
        .Where(v => v.Parent is not null).Select(v => new Series(v.Value, v.Parent!.Value, v.Name)).ToList();

    private static IEnumerable<(int? Parent, int Value, string Name)> Values(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new InvalidDataException("NVIDIA's product list could not be read: " + ex.Message); }
        foreach (var e in doc.Descendants("LookupValue"))
        {
            var name = e.Element("Name")?.Value.Trim();
            if (string.IsNullOrEmpty(name) || !int.TryParse(e.Element("Value")?.Value, out var value)) continue;
            yield return (int.TryParse(e.Attribute("ParentID")?.Value, out var parent) ? parent : null, value, name);
        }
    }

    /// <summary>
    /// The card's ids in NVIDIA's list: an exact name match in a GeForce series. A desktop card and a laptop card with
    /// the same name are told apart by what the driver says this PC is; if that is unknown, nothing is chosen.
    /// </summary>
    public static (int SeriesId, int ProductId) Find(string card, bool? laptop, IReadOnlyList<Product> products, IReadOnlyList<Series> series, string? cardName = null)
    {
        cardName ??= card;
        var geforce = series.Where(s => s.TypeId == GeForceType).ToDictionary(s => s.Id);
        var named = products.Where(p => Normalise(p.Name) == card).ToList();
        var matches = named.Where(p => geforce.ContainsKey(p.SeriesId)).ToList();
        if (matches.Count == 0)
            throw new DriverLookupException(named.Count > 0
                ? $"The {cardName} is not a GeForce card, and RigControl only checks GeForce drivers." + UseNvidiaPage
                : $"NVIDIA's list has no card called exactly \"{cardName}\", so RigControl can't tell which driver fits." + UseNvidiaPage);
        if (matches.Count > 1 && laptop is { } isLaptop)
            matches = matches.Where(p => geforce[p.SeriesId].Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) == isLaptop).ToList();
        if (matches.Count != 1)
            throw new DriverLookupException($"NVIDIA lists more than one card called \"{cardName}\" (desktop and laptop versions), and RigControl can't tell which one this is." + UseNvidiaPage);
        return (matches[0].SeriesId, matches[0].ProductId);
    }

    /// <summary>Works out the ids for this PC. <paramref name="fetch"/> is only used when the card is not in the built-in list.</summary>
    public static async Task<Ids> ResolveAsync(IReadOnlyList<string> gpuNames, bool? laptop, Version windows, Architecture architecture,
        Func<string, CancellationToken, Task<string>> fetch, CancellationToken ct)
    {
        var card = OneCard(gpuNames);
        var cardName = gpuNames.First(n => Normalise(n) == card).Trim();
        var (osId, osName) = Os(windows, architecture);
        if (Known.TryGetValue(card, out var known)) return new Ids(known.SeriesId, known.ProductId, osId, osName, card, true, cardName);
        var products = ParseProducts(await fetch(ProductsUrl, ct).ConfigureAwait(false));
        var series = ParseSeries(await fetch(SeriesUrl, ct).ConfigureAwait(false));
        var (seriesId, productId) = Find(card, laptop, products, series, cardName);
        return new Ids(seriesId, productId, osId, osName, card, false, cardName);
    }

    /// <summary>The same lookup NVIDIA's own download page uses: newest Game Ready, WHQL, DCH driver for that card and Windows.</summary>
    public static string Url(Ids ids) => FormattableString.Invariant(
        $"{LookupUrl}?func=DriverManualLookup&psid={ids.SeriesId}&pfid={ids.ProductId}&osID={ids.OsId}&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1");

    /// <summary>
    /// Reads NVIDIA's answer and only accepts it when NVIDIA's own lists in it name this card and this Windows.
    /// </summary>
    public static LatestDriver ParseAnswer(string json, Ids ids)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0
            || !list[0].TryGetProperty("downloadInfo", out var info))
            throw new DriverLookupException($"NVIDIA has no driver listed for the {ids.CardName} on {ids.OsName} right now." + UseNvidiaPage);
        var version = Text(info, "Version");
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("No version in NVIDIA's answer.");

        var systems = Names(info, "OSList", "OSName");
        if (systems is not null && !systems.Contains(ids.OsName, StringComparer.OrdinalIgnoreCase))
            throw new DriverLookupException($"NVIDIA's newest driver ({version}) does not list {ids.OsName}, so RigControl does not offer it." + UseNvidiaPage);

        var cards = SupportedCards(info);
        if (cards is null)
        {
            // Without NVIDIA's list of supported cards only the hand-checked ids are trusted.
            if (!ids.FromBuiltInList)
                throw new DriverLookupException($"NVIDIA's answer does not say which cards driver {version} is for, so RigControl does not offer it." + UseNvidiaPage);
        }
        else if (!cards.Contains(ids.Card, StringComparer.Ordinal))
        {
            throw new DriverLookupException($"NVIDIA's newest driver ({version}) does not list the {ids.CardName}, so RigControl does not offer it." + UseNvidiaPage);
        }

        var details = Text(info, "DetailsURL");
        if (details is not null && !details.StartsWith("https://www.nvidia.com/", StringComparison.OrdinalIgnoreCase)) details = null;
        var download = Text(info, "DownloadURL");
        if (!DriverInstaller.IsAllowedUrl(download)) download = null;
        return new LatestDriver(version, details, download);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    private static List<string>? Names(JsonElement info, string list, string field)
    {
        if (!info.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) return null;
        return items.EnumerateArray().Select(i => Text(i, field)).Where(n => n is not null).Select(n => Decode(n!).Trim()).ToList();
    }

    /// <summary>Every card NVIDIA says the driver is for (normalised), or null when the answer has no such list.</summary>
    private static HashSet<string>? SupportedCards(JsonElement info)
    {
        if (!info.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Array) return null;
        var cards = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in series.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in products.EnumerateArray())
                if (Text(p, "productName") is { } name) cards.Add(Normalise(Decode(name)));
        }
        return cards.Count == 0 ? null : cards;
    }
}
