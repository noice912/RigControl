using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// Govee lights through Govee's LAN API: small JSON messages over UDP, no account and no key. The light must have
/// "LAN Control" switched on in the Govee Home app. Built from the published protocol and from open-source programs
/// that speak it (OpenRGB, govee-local-api, govee2mqtt, homebridge-govee, openHAB); not yet tried on a real device.
///
/// How it talks: a search question goes to the multicast group 239.255.255.250 port 4001, commands go to the light's
/// port 4003, and every answer comes back to UDP port 4002 on this PC, whatever port the question left from. So one
/// shared listener on 4002 (<see cref="ReplyPort"/>) hears all answers and tells them apart by the sender's address.
///
/// Two ways to send colours: one colour for the whole light (the official "colorwc" command, slow), or a colour per
/// piece through the unofficial "razer" stream that Govee made for Razer Chroma (as read by OpenRGB). The light does
/// not say how many pieces it has, so the user picks a number when pairing.
/// </summary>
public sealed class GoveeProvider : INetworkLightProvider
{
    internal const string ScanGroup = "239.255.255.250";
    internal const int ScanPort = 4001;
    internal const int ReplyPortNumber = 4002;
    internal const int ControlPort = 4003;

    internal const string ScanJson = "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}";
    internal const string StateJson = "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}";
    internal const string RazerStatusJson = "{\"msg\":{\"cmd\":\"status\",\"data\":{}}}";

    internal const string NoAnswerAtAddress =
        "No Govee light answered at that address. Check LAN Control is switched on for it in the Govee Home app.";
    internal const string NoRazer =
        "This Govee light doesn't take a colour per piece over the network. Pair it again with one colour for the whole light.";
    internal const string PortBusy =
        "Another program is using Govee's reply port (4002). Close OpenRGB's Govee support, Razer Synapse or another Govee tool and try again.";

    private static readonly string[] SegmentChoices = { "5", "10", "15", "20", "30" };

    private readonly TimeSpan _scanWait;
    private readonly TimeSpan _answerWait;
    private readonly TimeSpan _razerWait;
    private readonly TimeSpan _gap;

    public GoveeProvider() : this(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1.5), TimeSpan.FromMilliseconds(40)) { }

    /// <summary>For tests: the same provider with shorter waits.</summary>
    internal GoveeProvider(TimeSpan scanWait, TimeSpan answerWait, TimeSpan razerWait, TimeSpan gap)
    {
        _scanWait = scanWait;
        _answerWait = answerWait;
        _razerWait = razerWait;
        _gap = gap;
    }

    public string Brand => "govee";
    public string DisplayName => "Govee";
    public bool Tested => false;
    public string Notes =>
        "Govee lights that have LAN Control in the Govee Home app. One colour for the whole light, or a colour per piece on lights " +
        "that take Govee's Razer stream (not official; you choose the number of pieces when pairing). " +
        "Built from the published protocol and open-source implementations; not yet tried on a real device. " +
        "A scene or music mode that was running can't be seen over the network, so it is not put back when the sync stops: " +
        "the light goes back to its last colour, brightness and on or off.";
    // The official colour command is not made for streaming; the per-piece stream takes more (see the session).
    public double MaxFramesPerSecond => 2;
    public string PointName => "piece";

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Set the light up in the Govee Home phone app, on the same network as this PC.",
            "In Govee Home open the light, tap the settings (gear) icon and switch on LAN Control. It can take a few minutes to show up after first setup. Lights without that switch can't be used.",
            "Do that for every Govee light you want here.",
            "Press Search for lights. If Windows asks, allow RigControl on private networks.",
        },
        ReturnsSecret: false,
        ManualAddress: true,
        Options: new[]
        {
            new PairingOption("segments", "How many separate colours along the light",
                new[] { new PairingChoice("1", "One colour for the whole light") }
                    .Concat(SegmentChoices.Select(n => new PairingChoice(n, n + " colours along the light")))
                    .ToList(),
                "1"),
        },
        NothingFound: "No Govee light answered. Check LAN Control is switched on for it in the Govee Home app, and that no other program is " +
                      "using Govee's reply port (OpenRGB's Govee support, Razer Synapse, Home Assistant). Or type the light's address.",
        NoAnswerHint: "and that LAN Control is still on for it in the Govee Home app");

    // ---- search and pairing

    public async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        using var port = ReplyPort.Open(net);
        // Networks without a router (VPN adapters and the like) are never searched.
        port.SendFromHomeNetworks(ScanJson, ScanGroup, ScanPort);
        var found = new Dictionary<string, FoundLight>(StringComparer.OrdinalIgnoreCase);
        var until = DateTime.UtcNow + _scanWait;
        // More lights than a home has = something making answers up: the search stops listening.
        while (DateTime.UtcNow < until && found.Count < NetworkLights.MostFoundPerBrand)
        {
            var d = await port.ReceiveAsync(until - DateTime.UtcNow, ct).ConfigureAwait(false);
            if (d is null) break;
            if (ScanAnswer(d) is { } light) found[light.Id] = light;
        }
        return found.Values.ToList();
    }

    public async Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        var segments = ParseSegments(request.Options.TryGetValue("segments", out var s) ? s : "1");
        using var port = ReplyPort.Open(net);

        // The same question a search asks, sent to this one address. The answer says which light it is.
        port.Send(ScanJson, request.Address, ScanPort);
        FoundLight? light = null;
        var until = DateTime.UtcNow + _answerWait;
        while (light is null && DateTime.UtcNow < until)
        {
            var d = await port.ReceiveAsync(until - DateTime.UtcNow, ct).ConfigureAwait(false);
            if (d is null) break;
            if (LanAddress.Same(d.Host, request.Address)) light = ScanAnswer(d);
        }
        if (light is null) throw new InvalidOperationException(NoAnswerAtAddress);

        if (segments > 1)
        {
            // A light that knows the per-piece stream says so when asked for its state while the stream is on.
            port.Send(RazerJson(RazerEnable), request.Address, ControlPort);
            await Task.Delay(_gap, ct).ConfigureAwait(false);
            port.Send(RazerStatusJson, request.Address, ControlPort);
            var answer = await port.WaitForAsync(request.Address, "status", _razerWait, ct, IsRazerStatus).ConfigureAwait(false);
            // Either way the stream is switched off again: pairing must not leave the light changed.
            port.Send(RazerJson(RazerDisable), request.Address, ControlPort);
            if (answer is null) throw new InvalidOperationException(NoRazer);
        }

        return new PairResult(light.Id, light.Name, light.Model, request.Address)
        {
            Data = new Dictionary<string, string>
            {
                ["sku"] = light.Model,
                ["segments"] = segments.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        // Asking for its state is the check that it is there and LAN Control is still on.
        using var port = ReplyPort.Open(net);
        _ = await AskStateAsync(port, light.Address, ct).ConfigureAwait(false);
        var segments = Segments(light);
        return segments <= 1 ? LightDescription.Single() : LightDescription.Strip(segments);
    }

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        // What it shows now, to put back afterwards.
        GoveeState before;
        using (var port = ReplyPort.Open(net))
            before = await AskStateAsync(port, light.Address, ct).ConfigureAwait(false);

        // Commands need no answer, so they leave from a port of their own and the reply port is free again.
        var send = net.OpenUdp();
        try
        {
            var segments = Segments(light);
            var session = new Session(send, light.Address, before, segments, _gap);
            if (segments > 1)
            {
                var sku = light.Data.TryGetValue("sku", out var v) ? v : light.Model;
                await session.EnableStreamAsync(RazerSettleTime(sku), ct).ConfigureAwait(false);
            }
            return session;
        }
        catch
        {
            send.Dispose();
            throw;
        }
    }

    // ---- direct control: the official LAN commands "turn", "brightness" and "colorwc" to port 4003. They are not
    // answered, so nothing here waits for (or needs) Govee's reply port.

    internal const string NoReadBack =
        "A Govee light only answers on a fixed port of this PC, and RigControl doesn't wait for those answers when you change a light. So it can't show what the light shows now.";
    internal const string NoGiveBack =
        "Without the light's answer RigControl can't know what it showed before, so it can't put that back. Pick a scene in the Govee Home app instead.";

    public LightControls Controls(PairedLight light) =>
        new(LightAbility.Yes, LightAbility.Yes, LightAbility.Yes, LightAbility.No(NoReadBack), LightAbility.No(NoGiveBack));

    public async Task SetAsync(ILightNetwork net, PairedLight light, LightChange change, CancellationToken ct)
    {
        // Commands leave from a port of their own; the light's answers (if any) go to 4002 and are not waited for.
        using var port = net.OpenUdp();
        void Send(string json) => port.Send(Encoding.UTF8.GetBytes(json), light.Address, ControlPort);
        if (change.TurnsOff)
        {
            Send(TurnJson(false));
            return;
        }
        Send(TurnJson(true));
        if (change.Brightness is { } b)
        {
            await Task.Delay(_gap, ct).ConfigureAwait(false);
            Send(BrightnessJson(b));
        }
        if (change.Colour is { } c)
        {
            await Task.Delay(_gap, ct).ConfigureAwait(false);
            Send(ColourJson(c & 0xFFFFFF));
        }
    }

    private async Task<GoveeState> AskStateAsync(ReplyPort.Lease port, string address, CancellationToken ct)
    {
        port.Send(StateJson, address, ControlPort);
        var data = await port.WaitForAsync(address, "devStatus", _answerWait, ct).ConfigureAwait(false);
        if (data is null) throw new IOException("it sent no reply. Check LAN Control is still switched on for it in the Govee Home app");
        return GoveeState.From(data.Value);
    }

    private static int Segments(PairedLight light) =>
        light.Data.TryGetValue("segments", out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, 1, 255)
            : 1;

    private static int ParseSegments(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 255)
            throw new InvalidOperationException("Choose between 1 and 255 colours along the light.");
        return n;
    }

    /// <summary>The H6159 needs longer than other lights before it takes the first frame (from OpenRGB).</summary>
    internal static TimeSpan RazerSettleTime(string sku) =>
        TimeSpan.FromMilliseconds(sku.Equals("H6159", StringComparison.OrdinalIgnoreCase) ? 700 : 50);

    // ---- messages

    /// <summary>A reply to the search question, as a found light; null for anything else.</summary>
    private static FoundLight? ScanAnswer(UdpDatagram d)
    {
        if (Parse(d.Data) is not { Cmd: "scan" } m) return null;
        // A real id is a short row of hex pairs and a model is a few letters and digits; anything else is not a Govee light.
        var id = Text(m.Data, "device");
        if (id.Length is 0 or > 64 || id.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        var sku = Text(m.Data, "sku");
        if (sku.Length > 40) return null;
        sku = DeviceText.Tidy(sku, 40);
        // Where the answer came from is where the light is, whatever address it wrote inside.
        return new FoundLight(id, sku.Length > 0 ? "Govee " + sku : "Govee light", sku, d.Host)
        {
            Data = new Dictionary<string, string> { ["sku"] = sku },
        };
    }

    /// <summary>Reads one message: its command and its data. Some lights pad the packet with zero bytes.</summary>
    internal static (string Cmd, JsonElement Data)? Parse(byte[] packet)
    {
        var end = packet.Length;
        while (end > 0 && (packet[end - 1] == 0 || packet[end - 1] is (byte)' ' or (byte)'\r' or (byte)'\n')) end--;
        if (end == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(packet.AsMemory(0, end));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("msg", out var msg) || msg.ValueKind != JsonValueKind.Object
                || !msg.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String)
                return null;
            var data = msg.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d.Clone() : default;
            return (cmd.GetString() ?? "", data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    private static int Number(JsonElement data, string name, int fallback) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
            ? (int)Math.Round(n)
            : fallback;

    internal static string TurnJson(bool on) => "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":" + (on ? "1" : "0") + "}}}";

    internal static string BrightnessJson(int percent) =>
        "{\"msg\":{\"cmd\":\"brightness\",\"data\":{\"value\":" + Math.Clamp(percent, 1, 100).ToString(CultureInfo.InvariantCulture) + "}}}";

    /// <summary>A colour, or a white temperature: the light wants one of the two to be zero.</summary>
    internal static string ColourJson(int rgb, int kelvin = 0)
    {
        if (kelvin > 0) rgb = 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"msg\":{{\"cmd\":\"colorwc\",\"data\":{{\"color\":{{\"r\":{(rgb >> 16) & 255},\"g\":{(rgb >> 8) & 255},\"b\":{rgb & 255}}},\"colorTemInKelvin\":{Math.Max(0, kelvin)}}}}}}}");
    }

    internal static string RazerJson(byte[] packet) =>
        "{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"" + Convert.ToBase64String(packet) + "\"}}}";

    // The per-piece stream. A packet is BB, the payload's length (two bytes, high first), a command, the payload and
    // a check byte (every byte before it, XORed together). Described by OpenRGB's Govee controller.
    internal static byte[] RazerEnable => RazerPacket(0xB1, 0x01);
    internal static byte[] RazerDisable => RazerPacket(0xB1, 0x00);

    /// <summary>One frame: a colour per piece (at most 255). The light blends between neighbours.</summary>
    internal static byte[] RazerFrame(IReadOnlyList<int> colours, bool blend = true)
    {
        var n = Math.Clamp(colours.Count, 1, 255);
        var payload = new byte[2 + 3 * n];
        payload[0] = (byte)(blend ? 1 : 0);
        payload[1] = (byte)n;
        for (var i = 0; i < n; i++)
        {
            var c = i < colours.Count ? colours[i] : 0;
            payload[2 + 3 * i] = (byte)(c >> 16);
            payload[3 + 3 * i] = (byte)(c >> 8);
            payload[4 + 3 * i] = (byte)c;
        }
        return RazerPacket(0xB0, payload);
    }

    internal static byte[] RazerPacket(byte command, params byte[] payload)
    {
        var p = new byte[5 + payload.Length];
        p[0] = 0xBB;
        p[1] = (byte)(payload.Length >> 8);
        p[2] = (byte)payload.Length;
        p[3] = command;
        payload.CopyTo(p, 4);
        byte check = 0;
        for (var i = 0; i < p.Length - 1; i++) check ^= p[i];
        p[^1] = check;
        return p;
    }

    /// <summary>True for the answer of a light that knows the stream: a well-formed "is it on" packet.</summary>
    private static bool IsRazerStatus(JsonElement data)
    {
        try
        {
            var p = Convert.FromBase64String(Text(data, "pt"));
            if (p.Length < 6 || p[0] != 0xBB || p[3] != 0xB2) return false;
            byte check = 0;
            for (var i = 0; i < p.Length - 1; i++) check ^= p[i];
            return check == p[^1];
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>What a light said it shows: kept to put back afterwards.</summary>
    internal sealed record GoveeState(bool On, int Brightness, int Rgb, int Kelvin)
    {
        public static GoveeState From(JsonElement data)
        {
            var rgb = 0xFFFFFF;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Object)
                rgb = (Math.Clamp(Number(c, "r", 255), 0, 255) << 16) | (Math.Clamp(Number(c, "g", 255), 0, 255) << 8) | Math.Clamp(Number(c, "b", 255), 0, 255);
            return new GoveeState(Number(data, "onOff", 1) != 0, Math.Clamp(Number(data, "brightness", 100), 1, 100), rgb, Math.Max(0, Number(data, "colorTemInKelvin", 0)));
        }
    }

    // ---- a light being sent colours

    private sealed class Session : ILightSession
    {
        // The stream stops by itself after about a minute without frames; after a long gap it is asked for again.
        private static readonly TimeSpan StreamLapse = TimeSpan.FromSeconds(30);
        // The same single colour is not sent twice within this time.
        private static readonly TimeSpan SameColourQuiet = TimeSpan.FromSeconds(10);

        private readonly IUdpPort _port;
        private readonly string _address;
        private readonly GoveeState _before;
        private readonly int _segments;
        private readonly TimeSpan _gap;
        private TimeSpan _settle;
        private bool _streaming;
        private bool _on;
        private bool _levelSet;
        private int _last = -1;
        private DateTime _lastSent = DateTime.MinValue;

        public Session(IUdpPort port, string address, GoveeState before, int segments, TimeSpan gap)
        {
            _port = port;
            _address = address;
            _before = before;
            _segments = segments;
            _gap = gap;
            _on = before.On;
        }

        private bool PerPiece => _segments > 1;

        public double MaxFramesPerSecond => PerPiece ? 20 : 0;
        public TimeSpan KeepAlive => PerPiece ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);

        public async Task EnableStreamAsync(TimeSpan settle, CancellationToken ct)
        {
            _settle = settle;
            Send(RazerJson(RazerEnable));
            _streaming = true;
            await Task.Delay(settle, ct).ConfigureAwait(false);
            _lastSent = DateTime.UtcNow;
        }

        public async Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            if (colours.Count == 0) return;
            var now = DateTime.UtcNow;
            if (PerPiece)
            {
                if (now - _lastSent > StreamLapse) await EnableStreamAsync(_settle, ct).ConfigureAwait(false);
                // A light that was off stays off until there is something to show.
                if (colours.Any(c => (c & 0xFFFFFF) != 0)) await WakeAsync(ct).ConfigureAwait(false);
                Send(RazerJson(RazerFrame(colours.Take(_segments).ToList())));
                _lastSent = DateTime.UtcNow;
                return;
            }

            var colour = colours[0] & 0xFFFFFF;
            if (colour == 0)
            {
                // Black is "off": the light has no darker colour than its dimmest.
                if (_on)
                {
                    Send(TurnJson(false));
                    _on = false;
                }
                _last = 0;
                _lastSent = now;
                return;
            }
            if (colour == _last && _on && now - _lastSent < SameColourQuiet) return;
            await WakeAsync(ct).ConfigureAwait(false);
            Send(ColourJson(colour));
            _last = colour;
            _lastSent = DateTime.UtcNow;
        }

        /// <summary>Switches the light on if it is off, and once sets it to full brightness so the colour alone carries the level.</summary>
        private async Task WakeAsync(CancellationToken ct)
        {
            if (!_on)
            {
                Send(TurnJson(true));
                _on = true;
                await Task.Delay(_gap, ct).ConfigureAwait(false);
            }
            if (!_levelSet)
            {
                Send(BrightnessJson(100));
                _levelSet = true;
                await Task.Delay(_gap, ct).ConfigureAwait(false);
            }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (_streaming)
            {
                Send(RazerJson(RazerDisable));
                _streaming = false;
                await Task.Delay(_gap, ct).ConfigureAwait(false);
            }
            Send(ColourJson(_before.Rgb, _before.Kelvin));
            await Task.Delay(_gap, ct).ConfigureAwait(false);
            Send(BrightnessJson(_before.Brightness));
            await Task.Delay(_gap, ct).ConfigureAwait(false);
            Send(TurnJson(_before.On));
            _on = _before.On;
        }

        private void Send(string json) => _port.Send(Encoding.UTF8.GetBytes(json), _address, ControlPort);

        public ValueTask DisposeAsync()
        {
            _port.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ---- the one reply port

    /// <summary>
    /// Govee lights answer to UDP port 4002 on this PC and nowhere else. Two listeners on the same port would take
    /// each other's answers, so everything that waits for an answer shares one listener per local network here, open
    /// only while somebody waits. It is opened shared, so other Govee programs that also share it keep working.
    /// </summary>
    private sealed class ReplyPort
    {
        private static readonly ConditionalWeakTable<ILightNetwork, ReplyPort> All = new();

        private readonly ILightNetwork _net;
        private readonly object _gate = new();
        private readonly List<(LocalNetwork? Network, IUdpPort Port)> _ports = new();
        private readonly List<Channel<UdpDatagram>> _listeners = new();
        private CancellationTokenSource? _stop;
        private int _users;

        private ReplyPort(ILightNetwork net) => _net = net;

        public static Lease Open(ILightNetwork net)
        {
            var shared = All.GetValue(net, n => new ReplyPort(n));
            return shared.Join();
        }

        private Lease Join()
        {
            lock (_gate)
            {
                if (_users == 0) OpenPorts();
                _users++;
                // Bounded: a flood of answers is dropped, not kept.
                var inbox = Channel.CreateBounded<UdpDatagram>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropWrite });
                _listeners.Add(inbox);
                return new Lease(this, inbox);
            }
        }

        private void OpenPorts()
        {
            var networks = _net.LocalNetworks();
            foreach (var n in networks)
            {
                try
                {
                    _ports.Add((n, _net.OpenUdp(new UdpOptions { LocalAddress = n.Address, LocalPort = ReplyPortNumber, Share = true })));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Tried on the next network; if none opens, the user is told below.
                }
            }
            if (_ports.Count == 0)
            {
                // Sharing was asked for, so this only fails when another program holds the port for itself alone.
                if (networks.Count > 0) throw new InvalidOperationException(PortBusy);
                try
                {
                    _ports.Add((null, _net.OpenUdp(new UdpOptions { LocalPort = ReplyPortNumber, Share = true })));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(PortBusy);
                }
            }
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            foreach (var (_, port) in _ports) _ = Task.Run(() => PumpAsync(port, token));
        }

        private async Task PumpAsync(IUdpPort port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var d = await port.ReceiveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    if (d is null) continue;
                    lock (_gate)
                        foreach (var l in _listeners) l.Writer.TryWrite(d);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                    try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private void Leave(Channel<UdpDatagram> inbox)
        {
            lock (_gate)
            {
                if (!_listeners.Remove(inbox)) return;
                if (--_users > 0) return;
                _stop?.Cancel();
                _stop = null;
                foreach (var (_, port) in _ports)
                {
                    try { port.Dispose(); } catch { /* already closed */ }
                }
                _ports.Clear();
            }
        }

        /// <summary>The port on the same network as <paramref name="host"/>, so the answer comes back to it.</summary>
        private IUdpPort PortFor(string host)
        {
            lock (_gate)
            {
                if (_ports.Count == 0) throw new ObjectDisposedException(nameof(ReplyPort));
                if (System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var target = LanAddress.ToUInt(ip);
                    foreach (var (n, port) in _ports)
                    {
                        if (n is null || !System.Net.IPAddress.TryParse(n.Address, out var own)) continue;
                        var mask = n.PrefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(32, n.PrefixLength));
                        if ((LanAddress.ToUInt(own) & mask) == (target & mask)) return port;
                    }
                }
                return _ports[0].Port;
            }
        }

        /// <summary>The ports a search question leaves from: home networks (ones with a router), or the one port there is when Windows names no network.</summary>
        private IUdpPort[] SearchPorts()
        {
            lock (_gate) return _ports.Where(p => p.Network is null || p.Network.HasGateway).Select(p => p.Port).ToArray();
        }

        /// <summary>One user's share of the reply port: it hears every answer that arrives while it is held.</summary>
        public sealed class Lease : IDisposable
        {
            private readonly ReplyPort _owner;
            private readonly Channel<UdpDatagram> _inbox;

            public Lease(ReplyPort owner, Channel<UdpDatagram> inbox)
            {
                _owner = owner;
                _inbox = inbox;
            }

            public void Send(string json, string host, int port) =>
                _owner.PortFor(host).Send(Encoding.UTF8.GetBytes(json), host, port);

            /// <summary>
            /// Sends out of every home network (for the search). Networks without a router (VPN adapters, virtual
            /// switches) are left out; a network that can't send is skipped.
            /// </summary>
            public void SendFromHomeNetworks(string json, string host, int port)
            {
                var data = Encoding.UTF8.GetBytes(json);
                foreach (var p in _owner.SearchPorts())
                {
                    try { p.Send(data, host, port); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* that network is down */ }
                }
            }

            public async Task<UdpDatagram?> ReceiveAsync(TimeSpan wait, CancellationToken ct)
            {
                if (_inbox.Reader.TryRead(out var ready)) return ready;
                if (wait <= TimeSpan.Zero) return null;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(wait);
                try { return await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            }

            /// <summary>
            /// The data of the next answer with that command from that address, or null when none came in time.
            /// Answers carry no device id, so the sender's address is what tells lights apart.
            /// </summary>
            public async Task<JsonElement?> WaitForAsync(string address, string cmd, TimeSpan wait, CancellationToken ct, Func<JsonElement, bool>? accept = null)
            {
                var until = DateTime.UtcNow + wait;
                while (true)
                {
                    var d = await ReceiveAsync(until - DateTime.UtcNow, ct).ConfigureAwait(false);
                    if (d is null) return null;
                    if (!LanAddress.Same(d.Host, address)) continue;
                    if (Parse(d.Data) is { } m && m.Cmd == cmd && (accept is null || accept(m.Data))) return m.Data;
                }
            }

            public void Dispose() => _owner.Leave(_inbox);
        }
    }
}
