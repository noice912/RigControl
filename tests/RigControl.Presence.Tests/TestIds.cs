using System.Runtime.CompilerServices;
using RigControl.Presence;

namespace RigControl.Presence.Tests;

/// <summary>
/// Made-up Discord application ids for every test. They stand in for the built-in ones for the whole test run, so
/// no test depends on (or shows) the real ids, and the tests pass the same with or without private/discord-ids.json.
/// </summary>
internal static class TestIds
{
    public const string Game = "100000000000000001";
    public const string Music = "100000000000000002";
    public const string App = "100000000000000003";
    public const string Netflix = "100000000000000011";

    public static readonly AppIds Ids = AppIds.From(new Dictionary<string, string>
    {
        ["game"] = Game,
        ["music"] = Music,
        ["app"] = App,
        ["netflix"] = Netflix,
        ["hulu"] = "100000000000000012",
        ["disney"] = "100000000000000013",
        ["primeVideo"] = "100000000000000014",
        ["max"] = "100000000000000015",
        ["crunchyroll"] = "100000000000000016",
        ["paramount"] = "100000000000000017",
        ["peacock"] = "100000000000000018",
        ["appleTv"] = "100000000000000019",
        ["custom1"] = "100000000000000021",
        ["custom2"] = "100000000000000022",
    }, out _);

    public static IReadOnlyList<string> Pool => Ids.Pool;

    /// <summary>The ids of the streaming services' own cards.</summary>
    public static IEnumerable<string> Services => AppIds.ServiceNames.Select(n => Ids.Get(n)!);

    [ModuleInitializer]
    internal static void Install() => AppIds.UseForTests(Ids);
}
