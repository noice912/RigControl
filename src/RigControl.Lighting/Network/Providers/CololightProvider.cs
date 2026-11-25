using System.Text;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// LifeSmart Cololight Hexagon (Pro), Strip and MIX over their local UDP port 8900. Not yet tried on a real light.
/// The maker publishes no protocol: the packets here follow what other people captured from the phone app
/// (pycololight by BazaJayGee66, MIT). Only what those captures show is used: a question for the light's state,
/// power on / off with a brightness, and one colour for the whole light. There is no pairing and no key.
///
/// A packet: "SZ00", five zero bytes, one byte with the length of everything after it, sixteen zero bytes, a counter
/// that goes 1, 2, 1, 2, nine zero bytes, then the command. Set commands are not answered.
/// </summary>
public sealed class CololightProvider : INetworkLightProvider
{
    internal const int Port = 8900;
    /// <summary>The brightness kept during a sync; the level is carried by the colour itself, so a frame is one packet.</summary>
    internal const int SyncBrightness = 100;
    /// <summary>A colour whose brightest part is below this counts as black: the light is switched off.</summary>
    internal const int NearBlack = 8;

    private static readonly PairingChoice[] Types =
    {
        new("hexagon", "Hexagon (Pro)"),
        new("strip", "Strip"),
        new("mix", "MIX"),
    };

    public string Brand => "cololight";
    public string DisplayName => "Cololight";
    public bool Tested => false;
    public string Notes =>
        "Cololight Hexagon (Pro), Strip and MIX. Not yet tried on a real light. The maker has no documentation for this: " +
        "it is built from other people's recordings of what the phone app sends (pycololight, MIT). " +
        "The whole light shows one colour. When the sync stops, what the light showed before is put back; " +
        "some Strip effects may not come back exactly.";
    // A small Wi-Fi chip with no known safe rate, so it is sent to gently.
    public double MaxFramesPerSecond => 2;
    // Nothing is sent, so there is nothing to wait for.
    public TimeSpan SearchTime => TimeSpan.FromSeconds(1);

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Set the light up once in the Cololight or LifeSmart phone app, so it joins your Wi-Fi.",
            "Find its address in your router's list of devices. Giving it a fixed address there is a good idea.",
            "Type the address here and choose which kind of Cololight it is.",
        },
        ReturnsSecret: false,
        ManualAddress: true,
        Options: new[] { new PairingOption("type", "Which Cololight", Types, "hexagon") },
        NothingFound: "Cololight lights can't be searched for. Type the light's address.",
        CanSearch: false);

    /// <summary>How long a light gets to answer a question (asked twice before giving up).</summary>
    internal TimeSpan AnswerWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>There is no dependable way to find these lights, so nothing is sent and nothing is found.</summary>
    public Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FoundLight>>(Array.Empty<FoundLight>());

    public async Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        var type = request.Options.TryGetValue("type", out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim().ToLowerInvariant() : "hexagon";
        if (Types.All(c => c.Value != type))
            throw new InvalidOperationException("Choose which kind of Cololight it is: Hexagon, Strip or MIX.");

        using var link = new Link(net, request.Address);
        var state = await link.AskStateAsync(AnswerWait, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No Cololight answered at that address. Check the address and that the light is switched on.");
        var model = type switch { "strip" => "Strip", "mix" => "MIX", _ => "Hexagon" };
        return new PairResult(state.Mac, "Cololight " + model, model, request.Address)
        {
            Data = new Dictionary<string, string> { ["type"] = type },
        };
    }

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var link = new Link(net, light.Address);
        _ = await link.AskStateAsync(AnswerWait, ct).ConfigureAwait(false) ?? throw NoAnswer();
        return LightDescription.Single();
    }

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        var link = new Link(net, light.Address);
        try
        {
            // What it shows now, to put back afterwards.
            var before = await link.AskStateAsync(AnswerWait, ct).ConfigureAwait(false) ?? throw NoAnswer();
            var strip = light.Data.TryGetValue("type", out var type) && type == "strip";
            return new Session(link, before, strip);
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    // ---- direct control: the same captured packets (state question, power with brightness, one colour)

    public LightControls Controls(PairedLight light) =>
        new(LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.Yes);

    // A small Wi-Fi chip with no known safe rate: changes are kept half a second apart.
    public TimeSpan ControlGap => TimeSpan.FromMilliseconds(500);

    private static bool IsStrip(PairedLight light) => light.Data.TryGetValue("type", out var type) && type == "strip";

    public async Task<LightReading> ReadAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var link = new Link(net, light.Address);
        var state = await link.AskStateAsync(AnswerWait, ct).ConfigureAwait(false) ?? throw NoAnswer();
        return new LightReading(state.On, state.Brightness, state.Colour is { } c ? LightColour.Full(c) : null);
    }

    internal sealed record Saved(bool On, int Brightness, string? Shown);

    public async Task<string> SaveAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        using var link = new Link(net, light.Address);
        var state = await link.AskStateAsync(AnswerWait, ct).ConfigureAwait(false) ?? throw NoAnswer();
        return System.Text.Json.JsonSerializer.Serialize(new Saved(state.On, state.Brightness, state.Shown is { } s ? Convert.ToHexString(s) : null));
    }

    public async Task SetAsync(ILightNetwork net, PairedLight light, LightChange change, CancellationToken ct)
    {
        using var link = new Link(net, light.Address);
        // Its brightness now, since power and brightness go in one packet.
        var state = await link.AskStateAsync(AnswerWait, ct).ConfigureAwait(false) ?? throw NoAnswer();
        var brightness = Math.Clamp(change.Brightness ?? (state.Brightness >= 1 ? state.Brightness : SyncBrightness), 1, 100);
        if (change.TurnsOff)
        {
            link.Send(Power(false, Math.Clamp(state.Brightness, 1, 100)));
            return;
        }
        link.Send(Power(true, brightness));
        if (change.Colour is { } c) link.Send(Colour(IsStrip(light), c & 0xFFFFFF));
    }

    public Task GiveBackAsync(ILightNetwork net, PairedLight light, string saved, CancellationToken ct)
    {
        Saved? was;
        try { was = System.Text.Json.JsonSerializer.Deserialize<Saved>(saved); }
        catch (System.Text.Json.JsonException) { was = null; }
        if (was is null) throw new InvalidOperationException("What the Cololight showed before couldn't be read.");
        ct.ThrowIfCancellationRequested();
        using var link = new Link(net, light.Address);
        if (was.Shown is { Length: 8 } hex) link.Send(Show(IsStrip(light), Convert.FromHexString(hex)));
        link.Send(Power(was.On, was.Brightness >= 1 ? was.Brightness : SyncBrightness));
        return Task.CompletedTask;
    }

    private static InvalidOperationException NoAnswer() =>
        new("The Cololight doesn't answer. Check that it is switched on and still has the same address.");

    // ---- packets

    /// <summary>What a light said about itself. <see cref="Shown"/> is four bytes (00 R G B for a plain colour, else an effect code), or null when it didn't say.</summary>
    internal sealed record State(bool On, int Brightness, byte[]? Shown, string Mac)
    {
        /// <summary>The plain colour it shows (0xRRGGBB), or null for an effect or nothing known.</summary>
        public int? Colour => Shown is { Length: 4 } s && s[0] == 0 ? (s[1] << 16) | (s[2] << 8) | s[3] : null;
    }

    internal static byte[] Frame(int counter, byte[] body)
    {
        var frame = new byte[36 + body.Length];
        frame[0] = (byte)'S';
        frame[1] = (byte)'Z';
        frame[2] = (byte)'0';
        frame[3] = (byte)'0';
        frame[9] = (byte)(26 + body.Length);   // everything after this byte
        frame[26] = (byte)counter;
        body.CopyTo(frame, 36);
        return frame;
    }

    internal static byte[] StateQuery() => new byte[] { 0x03, 0x02, 0x01, 0x00 };

    /// <summary>Power on or off. The brightness (1-100) is also taken while off: it is what the light comes back on at.</summary>
    internal static byte[] Power(bool on, int brightness) =>
        new byte[] { 0x04, 0x01, 0x03, 0x01, (byte)(on ? 0xCF : 0xCE), (byte)Math.Clamp(brightness, 1, 100) };

    /// <summary>Shows four bytes: 00 R G B for a plain colour, or an effect code the light gave earlier.</summary>
    internal static byte[] Show(bool strip, byte[] shown) =>
        new byte[] { (byte)(strip ? 0x07 : 0x04), 0x01, 0x06, 0x02, 0xFF, shown[0], shown[1], shown[2], shown[3] };

    internal static byte[] Colour(bool strip, int rgb) =>
        Show(strip, new byte[] { 0x00, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb });

    /// <summary>Reads a state answer; null when the packet is something else.</summary>
    internal static State? ParseState(byte[] reply)
    {
        if (reply.Length < 56 || reply[0] != 'S' || reply[1] != 'Z' || reply[2] != '0' || reply[3] != '0') return null;
        if (reply[36] != 0x83 || reply[40] is not (0xCF or 0xCE) || reply[41] > 100) return null;

        // The last 14 bytes: 0D 01 and the light's hardware address as twelve letters and digits.
        var tail = reply.AsSpan(reply.Length - 14);
        if (tail[0] != 0x0D || tail[1] != 0x01) return null;
        var mac = new StringBuilder(12);
        foreach (var b in tail[2..])
        {
            if (!Uri.IsHexDigit((char)b)) return null;
            mac.Append(char.ToLowerInvariant((char)b));
        }

        var shown = reply[42] == 0x02 && reply[43] == 0xFF ? reply[44..48] : null;
        return new State(reply[40] == 0xCF, reply[41], shown, mac.ToString());
    }

    /// <summary>One UDP port talking to one light, and nothing else.</summary>
    private sealed class Link : IDisposable
    {
        private readonly IUdpPort _port;
        private readonly string _address;
        private int _counter = 1;

        public Link(ILightNetwork net, string address)
        {
            _address = address;
            _port = net.OpenUdp();
        }

        public void Send(byte[] body)
        {
            var counter = _counter;
            _counter = counter == 1 ? 2 : 1;
            _port.Send(Frame(counter, body), _address, Port);
        }

        /// <summary>Asks what the light shows; asked a second time when the first goes unanswered. Null = no answer.</summary>
        public async Task<State?> AskStateAsync(TimeSpan wait, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Send(StateQuery());
                var until = DateTime.UtcNow + wait;
                while (true)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    var got = await _port.ReceiveAsync(left, ct).ConfigureAwait(false);
                    if (got is null) break;
                    // Only the light that was asked counts.
                    if (LanAddress.Same(got.Host, _address) && ParseState(got.Data) is { } state) return state;
                }
            }
            return null;
        }

        public void Dispose() => _port.Dispose();
    }

    private sealed class Session : ILightSession
    {
        private readonly Link _link;
        private readonly State _before;
        private readonly bool _strip;
        private bool _on;
        private int _brightness;
        private int? _colour;
        private DateTime _colourSent = DateTime.MinValue;
        private bool _changed;
        private bool _stopped;

        public Session(Link link, State before, bool strip)
        {
            _link = link;
            _before = before;
            _strip = strip;
            _on = before.On;
            _brightness = before.Brightness;
        }

        // The light keeps a colour by itself; it is only told again now and then, in case something else changed it.
        public TimeSpan KeepAlive => TimeSpan.FromMinutes(10);

        public Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (colours.Count == 0 || _stopped) return Task.CompletedTask;
            var rgb = colours[0] & 0xFFFFFF;
            var brightest = Math.Max(rgb >> 16, Math.Max((rgb >> 8) & 0xFF, rgb & 0xFF));

            if (brightest < NearBlack)
            {
                // Dark picture: switched off once, not on every frame.
                if (_on)
                {
                    _changed = true;
                    _link.Send(Power(false, SyncBrightness));
                    _on = false;
                    _brightness = SyncBrightness;
                }
                return Task.CompletedTask;
            }

            if (!_on || _brightness != SyncBrightness)
            {
                _changed = true;
                _link.Send(Power(true, SyncBrightness));
                _on = true;
                _brightness = SyncBrightness;
            }
            if (_colour != rgb || DateTime.UtcNow - _colourSent >= KeepAlive - TimeSpan.FromSeconds(5))
            {
                _changed = true;
                _link.Send(Colour(_strip, rgb));
                _colour = rgb;
                _colourSent = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            if (_stopped) return Task.CompletedTask;
            _stopped = true;
            // Never touched = nothing to put back.
            if (!_changed) return Task.CompletedTask;

            if (_before.Shown is { } shown) _link.Send(Show(_strip, shown));
            // A light that was off and gave no brightness comes back on at full next time.
            var brightness = _before.Brightness >= 1 ? _before.Brightness : SyncBrightness;
            _link.Send(Power(_before.On, brightness));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _link.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
