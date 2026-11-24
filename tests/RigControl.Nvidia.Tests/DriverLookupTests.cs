using System.Runtime.InteropServices;
using RigControl.Nvidia;

namespace RigControl.Nvidia.Tests;

/// <summary>The driver lookup must fit the card and Windows of the PC it runs on, or offer nothing. No network here.</summary>
public class DriverLookupTests
{
    // The shape of NVIDIA's public lists (lookupValueSearch.aspx, TypeID 3 = products, TypeID 2 = series), cut down.
    private const string Products = """
        <?xml version="1.0" encoding="UTF-8"?><LookupValueSearch><LookupValues>
        <LookupValue ParentID="127"><Name>NVIDIA GeForce RTX 4070 Ti</Name><Value>1001</Value></LookupValue>
        <LookupValue ParentID="127"><Name>NVIDIA GeForce RTX 4070 Ti SUPER</Name><Value>1040</Value></LookupValue>
        <LookupValue ParentID="127"><Name>NVIDIA GeForce RTX 4070</Name><Value>1015</Value></LookupValue>
        <LookupValue ParentID="129"><Name>GeForce RTX 4070 Laptop GPU</Name><Value>1006</Value></LookupValue>
        <LookupValue ParentID="120"><Name>GeForce RTX 3080</Name><Value>929</Value></LookupValue>
        <LookupValue ParentID="101"><Name>GeForce GTX 1060</Name><Value>817</Value></LookupValue>
        <LookupValue ParentID="102"><Name>GeForce GTX 1060</Name><Value>821</Value></LookupValue>
        <LookupValue ParentID="73"><Name>Quadro P2000</Name><Value>840</Value></LookupValue>
        <LookupValue><Name>No parent</Name><Value>1</Value></LookupValue>
        <LookupValue ParentID="127"><Name>No value</Name></LookupValue>
        </LookupValues></LookupValueSearch>
        """;

    private const string Series = """
        <?xml version="1.0" encoding="UTF-8"?><LookupValueSearch><LookupValues>
        <LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False"><Name>GeForce RTX 40 Series (Notebooks)</Name><Value>129</Value></LookupValue>
        <LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False"><Name>GeForce RTX 40 Series</Name><Value>127</Value></LookupValue>
        <LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False"><Name>GeForce RTX 30 Series</Name><Value>120</Value></LookupValue>
        <LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False"><Name>GeForce 10 Series</Name><Value>101</Value></LookupValue>
        <LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False"><Name>GeForce 10 Series (Notebooks)</Name><Value>102</Value></LookupValue>
        <LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False"><Name>Quadro Series</Name><Value>73</Value></LookupValue>
        </LookupValues></LookupValueSearch>
        """;

    private static readonly Version Windows11 = new(10, 0, 26200, 0);
    private static readonly Version Windows10 = new(10, 0, 19045, 0);

    private static string Answer(string version = "621.24", string[]? cards = null, string[]? systems = null, bool withCards = true,
        string download = "https://us.download.nvidia.com/Windows/621.24/621.24-desktop-win10-win11-64bit-international-dch-whql.exe")
    {
        cards ??= new[] { "NVIDIA GeForce RTX 4070 Ti", "NVIDIA GeForce RTX 4070", "GeForce RTX 3080", "GeForce GTX 1060" };
        systems ??= new[] { "Windows 10 64-bit", "Windows 11" };
        var series = "[{ \"seriesname\": \"GeForce%20RTX%2040%20Series\", \"products\": ["
                     + string.Join(",", cards.Select(c => "{ \"productName\": \"" + Uri.EscapeDataString(c) + "\" }")) + "] }]";
        var os = string.Join(",", systems.Select(s => "{ \"OSName\": \"" + Uri.EscapeDataString(s) + "\", \"OsCode\": \"10.0\" }"));
        return "{ \"Success\": \"1\", \"IDS\": [{ \"downloadInfo\": { \"Success\": \"1\", \"Version\": \"" + version + "\", "
               + "\"DetailsURL\": \"https://www.nvidia.com/en-us/drivers/details/285320/\", \"DownloadURL\": \"" + download + "\", "
               + "\"OSList\": [" + os + "]" + (withCards ? ", \"series\": " + series : "") + " } }] }";
    }

    private sealed class Web
    {
        public List<string> Asked { get; } = new();
        public Task<string> Fetch(string url, CancellationToken ct)
        {
            Asked.Add(url);
            if (url == DriverLookup.ProductsUrl) return Task.FromResult(Products);
            if (url == DriverLookup.SeriesUrl) return Task.FromResult(Series);
            throw new InvalidOperationException("unexpected request: " + url);
        }
    }

    private static Task<DriverLookup.Ids> Resolve(Web web, string card, bool? laptop = false, Version? windows = null, Architecture arch = Architecture.X64) =>
        DriverLookup.ResolveAsync(new[] { card }, laptop, windows ?? Windows11, arch, web.Fetch, CancellationToken.None);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Ti", "geforce rtx 4070 ti")]
    [InlineData("  GeForce   RTX 4070\tTi ", "geforce rtx 4070 ti")]
    [InlineData("nvidia TITAN RTX", "titan rtx")]
    [InlineData("NVIDIA", "nvidia")]
    [InlineData(null, "")]
    public void Card_names_compare_without_the_makers_name_case_or_extra_spaces(string? name, string expected)
    {
        Assert.Equal(expected, DriverLookup.Normalise(name));
    }

    [Fact]
    public async Task The_card_this_was_first_written_for_keeps_exactly_the_lookup_it_always_used()
    {
        var web = new Web();
        var ids = await Resolve(web, "NVIDIA GeForce RTX 4070 Ti");
        Assert.Empty(web.Asked); // known card: no list is fetched
        Assert.Equal(
            "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
            "?func=DriverManualLookup&psid=127&pfid=1001&osID=135&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1",
            DriverLookup.Url(ids));
        var latest = DriverLookup.ParseAnswer(Answer(), ids);
        Assert.Equal("621.24", latest.Version);
        Assert.Equal("https://www.nvidia.com/en-us/drivers/details/285320/", latest.DetailsUrl);
        Assert.StartsWith("https://us.download.nvidia.com/", latest.DownloadUrl);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", false, 127, 1015)]
    [InlineData("GeForce RTX 4070 Ti SUPER", null, 127, 1040)]
    [InlineData("NVIDIA GeForce RTX 3080", false, 120, 929)]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", true, 129, 1006)]
    [InlineData("GeForce GTX 1060", false, 101, 817)]
    [InlineData("GeForce GTX 1060", true, 102, 821)]
    public async Task Another_cards_ids_come_from_NVIDIAs_own_list_by_its_exact_name(string card, bool? laptop, int series, int product)
    {
        var web = new Web();
        var ids = await Resolve(web, card, laptop);
        Assert.Equal((series, product), (ids.SeriesId, ids.ProductId));
        Assert.False(ids.FromBuiltInList);
        Assert.Equal(new[] { DriverLookup.ProductsUrl, DriverLookup.SeriesUrl }, web.Asked);
        Assert.Contains($"psid={series}&pfid={product}&osID=135&", DriverLookup.Url(ids));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Ti Founders", false, "no card called exactly")] // close is not enough
    [InlineData("NVIDIA GeForce RTX 9999", false, "no card called exactly")]
    [InlineData("Quadro P2000", false, "not a GeForce card")]
    [InlineData("GeForce GTX 1060", null, "more than one card")] // desktop or laptop? unknown: no guess
    [InlineData("", false, "could not read the graphics card's name")]
    public async Task A_card_that_cannot_be_pinned_down_gets_no_driver_offered_and_a_plain_reason(string card, bool? laptop, string why)
    {
        var e = await Assert.ThrowsAsync<DriverLookupException>(() => Resolve(new Web(), card, laptop));
        Assert.Contains(why, e.Message);
        Assert.Contains("NVIDIA's driver page", e.Message);
    }

    [Fact]
    public async Task Two_different_NVIDIA_cards_in_one_PC_get_no_driver_offered()
    {
        var web = new Web();
        var e = await Assert.ThrowsAsync<DriverLookupException>(() => DriverLookup.ResolveAsync(
            new[] { "NVIDIA GeForce RTX 4070 Ti", "NVIDIA GeForce RTX 3080" }, false, Windows11, Architecture.X64, web.Fetch, CancellationToken.None));
        Assert.Contains("different NVIDIA graphics cards", e.Message);
        Assert.Empty(web.Asked);
        // Two of the same card are one answer.
        var ids = await DriverLookup.ResolveAsync(new[] { "NVIDIA GeForce RTX 4070 Ti", "GeForce RTX 4070 Ti" }, false, Windows11, Architecture.X64, web.Fetch, CancellationToken.None);
        Assert.Equal(1001, ids.ProductId);
    }

    [Fact]
    public async Task The_Windows_version_is_part_of_the_lookup()
    {
        Assert.Equal((135, "Windows 11"), DriverLookup.Os(Windows11, Architecture.X64));
        Assert.Equal((135, "Windows 11"), DriverLookup.Os(new Version(10, 0, 22000), Architecture.X64));
        Assert.Equal((57, "Windows 10 64-bit"), DriverLookup.Os(Windows10, Architecture.X64));
        var ids = await Resolve(new Web(), "NVIDIA GeForce RTX 4070 Ti", windows: Windows10);
        Assert.Contains("&osID=57&", DriverLookup.Url(ids));
    }

    [Fact]
    public void A_card_the_built_in_list_knows_is_not_a_GeForce_gets_no_driver_looked_up()
    {
        var geforce = RigControl.Core.Hardware.GpuDatabase.Lookup(0x10DE, 0x2782);
        var workstation = RigControl.Core.Hardware.GpuDatabase.Lookup(0x10DE, 0x2230);
        var unknown = RigControl.Core.Hardware.GpuDatabase.Lookup(0x10DE, 0xFFFE, "NVIDIA GeForce RTX 9090");
        Assert.True(geforce.Known);
        Assert.True(workstation.Known);
        Assert.False(unknown.Known);

        DriverLookup.RefuseOtherFamilies(Array.Empty<RigControl.Core.Hardware.GpuInfo>());
        DriverLookup.RefuseOtherFamilies(new[] { geforce, unknown });
        var e = Assert.Throws<DriverLookupException>(() => DriverLookup.RefuseOtherFamilies(new[] { geforce, workstation }));
        Assert.Contains("RTX A6000", e.Message);
        Assert.Contains("not a GeForce card", e.Message);
    }

    [Fact]
    public void Other_Windows_versions_and_processors_get_no_driver_offered()
    {
        Assert.Contains("Windows 10 and 11", Assert.Throws<DriverLookupException>(() => DriverLookup.Os(new Version(6, 3, 9600), Architecture.X64)).Message);
        Assert.Contains("64-bit", Assert.Throws<DriverLookupException>(() => DriverLookup.Os(Windows11, Architecture.Arm64)).Message);
        Assert.Contains("64-bit", Assert.Throws<DriverLookupException>(() => DriverLookup.Os(Windows10, Architecture.X86)).Message);
    }

    [Fact]
    public async Task An_answer_that_does_not_name_this_card_is_refused()
    {
        var ids = await Resolve(new Web(), "NVIDIA GeForce RTX 3080");
        Assert.Equal("621.24", DriverLookup.ParseAnswer(Answer(), ids).Version);
        var e = Assert.Throws<DriverLookupException>(() => DriverLookup.ParseAnswer(Answer(cards: new[] { "NVIDIA GeForce RTX 4070 Ti", "GeForce RTX 3080 Ti" }), ids));
        Assert.Contains("does not list the NVIDIA GeForce RTX 3080,", e.Message);
        // Even the known card is refused when NVIDIA's list leaves it out.
        var known = await Resolve(new Web(), "NVIDIA GeForce RTX 4070 Ti");
        Assert.Throws<DriverLookupException>(() => DriverLookup.ParseAnswer(Answer(cards: new[] { "GeForce RTX 3080" }), known));
    }

    [Fact]
    public async Task An_answer_for_another_Windows_is_refused()
    {
        var ids = await Resolve(new Web(), "NVIDIA GeForce RTX 3080", windows: Windows10);
        Assert.Equal("621.24", DriverLookup.ParseAnswer(Answer(), ids).Version);
        var e = Assert.Throws<DriverLookupException>(() => DriverLookup.ParseAnswer(Answer(systems: new[] { "Windows 11" }), ids));
        Assert.Contains("does not list Windows 10 64-bit", e.Message);
    }

    [Fact]
    public async Task An_answer_without_a_list_of_cards_is_only_trusted_for_the_hand_checked_ids()
    {
        var looked = await Resolve(new Web(), "NVIDIA GeForce RTX 3080");
        Assert.Contains("does not say which cards", Assert.Throws<DriverLookupException>(() => DriverLookup.ParseAnswer(Answer(withCards: false), looked)).Message);
        var known = await Resolve(new Web(), "NVIDIA GeForce RTX 4070 Ti");
        Assert.Equal("621.24", DriverLookup.ParseAnswer(Answer(withCards: false), known).Version);
    }

    [Theory]
    [InlineData("{ \"Success\": \"0\", \"IDS\": [] }")]
    [InlineData("{ \"Success\": \"0\" }")]
    [InlineData("{ \"IDS\": [{ }] }")]
    public async Task No_driver_in_the_answer_means_none_is_offered(string json)
    {
        var ids = await Resolve(new Web(), "NVIDIA GeForce RTX 4070 Ti");
        Assert.Contains("no driver listed", Assert.Throws<DriverLookupException>(() => DriverLookup.ParseAnswer(json, ids)).Message);
    }

    [Fact]
    public async Task A_download_link_that_is_not_NVIDIAs_is_dropped()
    {
        var ids = await Resolve(new Web(), "NVIDIA GeForce RTX 4070 Ti");
        var latest = DriverLookup.ParseAnswer(Answer(download: "https://example.com/621.24.exe"), ids);
        Assert.Equal("621.24", latest.Version);
        Assert.Null(latest.DownloadUrl);
    }

    [Fact]
    public async Task Without_a_detected_card_the_real_backend_asks_nobody_and_offers_nothing()
    {
        using var backend = new NvidiaBackend(Path.GetTempPath()); // Initialize() is not called: no driver is touched
        var e = await Assert.ThrowsAsync<DriverLookupException>(() => backend.GetLatestDriverAsync(CancellationToken.None));
        Assert.Contains("No NVIDIA graphics card", e.Message);
    }

    [Fact]
    public void A_list_that_is_not_NVIDIAs_format_is_an_error_not_a_guess()
    {
        Assert.Throws<InvalidDataException>(() => DriverLookup.ParseProducts("<html>blocked"));
        Assert.Empty(DriverLookup.ParseProducts("<LookupValueSearch/>"));
        Assert.Equal(8, DriverLookup.ParseProducts(Products.Trim()).Count);
        Assert.Equal(6, DriverLookup.ParseSeries(Series.Trim()).Count);
    }
}
