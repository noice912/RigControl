namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// Nanoleaf panels and Lines through their local API, panel by panel. Tried on real Nanoleaf Lines.
/// Behaviour is what RigControl has always done for a paired Nanoleaf: found by trying port 16021 on the home
/// network, paired by holding the power button, about 10 frames a second, external control asked for again every
/// 20 s, and the effect that was on before comes back when the sync stops. Not placed on the layout = left alone
/// (a hub may then run it as a room light).
/// </summary>
public sealed class NanoleafProvider : INetworkLightProvider
{
    public string Brand => NetworkLightStore.NanoleafBrand;
    public string DisplayName => "Nanoleaf";
    public bool Tested => true;
    public string Notes => "Panels, Shapes and Lines. Every panel takes its own part of the picture. Tried on Nanoleaf Lines.";
    public double MaxFramesPerSecond => 10;
    public string PointName => "panel";
    public bool FollowsWhenNotPlaced => false;
    // Tries every address of a small home network, which takes a while.
    public TimeSpan SearchTime => TimeSpan.FromSeconds(25);

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Press Search for lights and wait for your Nanoleaf to show up.",
            "Hold the Nanoleaf's power button for 5 to 7 seconds, until its lights flash.",
            "Press Pair within 30 seconds.",
        },
        ReturnsSecret: true,
        NothingFound: "No Nanoleaf answered on this network. Check it's on the same Wi-Fi or router as this PC, then search again.");

    public async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        var found = await NanoleafClient.DiscoverAsync(net, ct).ConfigureAwait(false);
        // A Nanoleaf only says who it is after pairing, so until then it is known by where it is.
        return found.Select(ip => new FoundLight("at " + ip, "Nanoleaf at " + ip, "", ip)).ToList();
    }

    public async Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        var token = await NanoleafClient.PairAsync(net, request.Address, ct).ConfigureAwait(false);
        using var client = new NanoleafClient(net, request.Address, BoundSecret.For(token, request.Address));
        var (name, serial) = await client.InfoAsync(ct).ConfigureAwait(false);
        return new PairResult(serial != "" ? serial : name, name, "", request.Address)
        {
            Secrets = new Dictionary<string, string> { ["token"] = token },
        };
    }

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        var panels = await client.PanelsAsync(ct).ConfigureAwait(false);
        return new LightDescription(LightKind.Points, panels.Select(p => new LightPoint(p.Id, p.X, p.Y, p.X1, p.Y1, p.X2, p.Y2)).ToList())
        {
            DrawAsBars = true,
        };
    }

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        var client = new NanoleafClient(net, light.Address, light.RequireSecret());
        try
        {
            // Its own effect, to put back afterwards.
            var before = await client.SelectedEffectAsync(ct).ConfigureAwait(false);
            return new Session(client, description, before);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class Session : ILightSession
    {
        private readonly NanoleafClient _client;
        private readonly LightDescription _description;
        private readonly string? _before;
        private DateTime _asked = DateTime.MinValue;

        public Session(NanoleafClient client, LightDescription description, string? before)
        {
            _client = client;
            _description = description;
            _before = before;
        }

        // Every frame is sent, changed or not, as before.
        public TimeSpan KeepAlive => TimeSpan.Zero;

        public async Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            // Asked again every 20 s, in case the Nanoleaf app or a hub switched it to something else.
            if ((DateTime.UtcNow - _asked).TotalSeconds >= 20)
            {
                await _client.StartStreamAsync(ct).ConfigureAwait(false);
                _asked = DateTime.UtcNow;
            }
            var frame = new List<(int, int)>(colours.Count);
            for (var i = 0; i < colours.Count && i < _description.Points.Count; i++) frame.Add((_description.Points[i].Id, colours[i]));
            _client.Send(frame, 1);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            // Names starting with * are Nanoleaf's own temporary ones (such as the stream itself); they can't be picked again.
            if (_before is { Length: > 0 } before && !before.StartsWith("*", StringComparison.Ordinal))
                await _client.SelectEffectAsync(before, ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
