using System.Text.Json;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// The LeetDesk Aura desk's light strip. Its controller runs a renamed WLED: the same realtime colour packets, with
/// its pages under /jsonaura and its own name on the network. What is known about it comes from a third-party add-on
/// (last changed in 2024), not from LeetDesk; it has not been tried on a real desk and current firmware may differ.
/// The desk's on/off and brightness are remembered when colours start and put back when they stop. Its update page
/// is never touched.
/// </summary>
public sealed class LeetDeskProvider : INetworkLightProvider
{
    internal static readonly WledDialect Dialect = new(
        What: "a LeetDesk Aura",
        MdnsType: "_aura._tcp.local.",
        InfoPath: "/jsonaura/info/",
        StatePath: "/jsonaura/state/",
        LedsPerPacket: 485,
        NeedsVersionAndLeds: false,
        DefaultName: "LeetDesk Aura",
        ModelName: "Aura");

    private const string AllPath = "/jsonaura/";
    /// <summary>The desk's strip when it doesn't say how long it is.</summary>
    internal const int DefaultLights = 512;

    public string Brand => "leetdesk";
    public string DisplayName => "LeetDesk Aura";
    public bool Tested => false;
    public string Notes =>
        "The light strip around a LeetDesk Aura desk, as one strip. Not yet tried on a real desk. "
        + "How it is controlled comes from a third-party add-on last changed in 2024, not from LeetDesk, so current desks may behave differently.";
    public double MaxFramesPerSecond => 30;

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Set the desk up once in the LeetDesk phone app, so it is on your Wi-Fi.",
            "This PC must be on the same network as the desk.",
            "Press Search for lights, or type the desk's address.",
        },
        ReturnsSecret: false,
        NothingFound: "No LeetDesk Aura answered the search. Type the desk's address instead. "
                      + "For a desk that can't be found, LeetDesk suggests holding the small button on the Aura controller for 25 seconds.");

    public Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct) =>
        WledWire.DiscoverAsync(net, Dialect, ct, NameOf);

    public Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct) =>
        WledWire.PairAsync(net, Dialect, request, ct, NameOf);

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        var info = await WledWire.RequireInfoAsync(net, Dialect, light, ct).ConfigureAwait(false);
        var count = Math.Min(info.Count > 0 ? info.Count : DefaultLights, WledWire.MostLeds);
        // Which end the strip starts at is not known, so it is left to the user.
        return LightDescription.Strip(count) with
        {
            Note = $"The desk is one strip of {count} lights. Use Run backwards on the layout if it runs the wrong way.",
        };
    }

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        var info = await WledWire.RequireInfoAsync(net, Dialect, light, ct).ConfigureAwait(false);
        var port = WledWire.PortOf(light.Data);
        var stateUrl = WledWire.Url(light.Address, port, Dialect.StatePath);

        // On/off and brightness as they are now, to put back afterwards. When the desk doesn't say, they are left alone.
        var all = await net.HttpAsync(HttpAsk.Get(WledWire.Url(light.Address, port, AllPath)), ct).ConfigureAwait(false);
        string? restore = null;
        if (all.Ok && WledWire.Object(all.Body) is { } root && root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object
            && state.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
            && state.TryGetProperty("bri", out var bri) && bri.ValueKind == JsonValueKind.Number && bri.TryGetInt32(out var brightness))
        {
            var answer = await net.HttpAsync(HttpAsk.Post(stateUrl, "{\"on\":true,\"bri\":255,\"live\":false}"), ct).ConfigureAwait(false);
            if (answer.Ok)
                restore = $"{{\"on\":{(on.GetBoolean() ? "true" : "false")},\"bri\":{Math.Clamp(brightness, 0, 255)},\"live\":false}}";
        }

        var count = Math.Min(description.Points.Count, WledWire.MostLeds);
        return new WledSession(net.OpenUdp(), light.Address, info.UdpPort, Dialect.LedsPerPacket, count, async (session, stop) =>
        {
            session.SendBlack();
            if (restore is not null)
                await net.HttpAsync(HttpAsk.Post(stateUrl, restore) with { Timeout = TimeSpan.FromSeconds(2) }, stop).ConfigureAwait(false);
        });
    }

    /// <summary>A desk still called just "aura" is told apart by the end of its id.</summary>
    private static string NameOf(WledInfo info) =>
        info.Name == "aura" ? "aura-" + info.Mac[^6..]
        : info.Name != "" ? info.Name
        : Dialect.DefaultName;
}
