using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RigControl.Lighting.Network.Providers;

/// <summary>
/// Yeelight bulbs, strips and ceiling lights through their "LAN Control" (Yeelight's own published
/// "WiFi Light Inter-Operation Specification"). Built from that specification; not yet tried on a real light.
/// One colour for the whole light, plus one for the background light on models that have one.
///  - Found by one search packet to 239.255.255.250 port 1982; the light answers with its id, model and what it can do.
///  - No pairing and no key: the user switches LAN Control on for the light in the Yeelight app.
///  - Colours go as "set_scene" (colour and brightness in one command, works whether the light is on or off).
///  - A light takes only 60 commands a minute on a normal connection. For anything faster it is asked to connect
///    back to this PC ("music mode"), where there is no limit. If it can't (firewall, VPN), it gets a colour every
///    1.3 seconds (45 commands a minute), which leaves room for the commands that put it back afterwards.
///  - Nothing is ever written to the light's permanent memory ("set_default" is never sent).
/// </summary>
public sealed class YeelightProvider : INetworkLightProvider
{
    public const string SearchAddress = "239.255.255.250";
    public const int SearchPort = 1982;
    public const int ControlPort = 55443;

    /// <summary>The search question, exactly as Yeelight's specification gives it.</summary>
    public const string SearchText = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n";

    private const string NoAnswer = "No Yeelight answered at that address. Check LAN Control is switched on for it in the Yeelight app.";
    private const string WhiteOnly = "This Yeelight is white-only, so it can't follow colours.";

    public string Brand => "yeelight";
    public string DisplayName => "Yeelight";
    public bool Tested => false;
    public string Notes =>
        "One colour for the whole light, plus the background light on models that have one. Built from Yeelight's published " +
        "specification; not yet tried on a real light. Lights set up through Mi Home and some newer models have no LAN Control " +
        "switch and can't be used. Anyone on your network can control a light that has LAN Control on.";
    public double MaxFramesPerSecond => 15;

    /// <summary>How long a search listens for answers.</summary>
    public TimeSpan ListenTime { get; init; } = TimeSpan.FromSeconds(2.5);
    /// <summary>How long the light gets to connect back to this PC for fast updates.</summary>
    public TimeSpan CallbackTime { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long the light gets to answer one command.</summary>
    public TimeSpan ReplyTime { get; init; } = TimeSpan.FromSeconds(1.5);

    public PairingInfo Pairing { get; } = new(
        new[]
        {
            "Set the light up in the Yeelight phone app, on the same network as this PC.",
            "Open the light in the app, open its settings and switch on \"LAN Control\" (older app versions call it \"Developer mode\"). It is a switch for each light. If the switch is missing, update the light's firmware.",
            "Press Search for lights, or type the light's address. If Windows asks, allow RigControl on private networks (the light connects back to this PC for fast updates).",
        },
        ReturnsSecret: false,
        NothingFound: "No Yeelight answered. Check LAN Control is switched on for the light in the Yeelight app and that it's on the same network as this PC, then search again.",
        NoAnswerHint: "and that LAN Control is still on for it in the Yeelight app");

    // ---------------------------------------------------------------- searching

    public async Task<IReadOnlyList<FoundLight>> DiscoverAsync(ILightNetwork net, CancellationToken ct)
    {
        var ports = new List<IUdpPort>();
        try
        {
            // Networks without a router (VPN adapters and the like) are never searched.
            foreach (var n in net.LocalNetworks().Where(n => n.HasGateway))
            {
                try
                {
                    var port = net.OpenUdp(new UdpOptions { LocalAddress = n.Address });
                    ports.Add(port);
                    port.Send(Encoding.ASCII.GetBytes(SearchText), SearchAddress, SearchPort);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // that network can't be searched; the others still are
                }
            }
            var found = new ConcurrentDictionary<string, FoundLight>(StringComparer.Ordinal);
            var until = DateTime.UtcNow + ListenTime;
            // A light answers straight back to the port the question came from.
            await Task.WhenAll(ports.Select(p => ListenAsync(p, until, found, null, ct))).ConfigureAwait(false);
            return found.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }
        finally
        {
            foreach (var p in ports) p.Dispose();
        }
    }

    private static async Task ListenAsync(IUdpPort port, DateTime until, ConcurrentDictionary<string, FoundLight> found, string? onlyFrom, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return;
                var datagram = await port.ReceiveAsync(left, ct).ConfigureAwait(false);
                if (datagram is null) return;
                if (ParseReply(datagram) is not { } light) continue;
                if (onlyFrom is not null && !LanAddress.Same(light.Address, onlyFrom)) continue;
                if (found.Count >= NetworkLights.MostFoundPerBrand) return; // more than a home has: enough
                found.TryAdd(light.Id, light); // the same light answering twice is one light
                if (onlyFrom is not null) return;
            }
        }
        catch (OperationCanceledException)
        {
            // stopped: what was found so far is kept
        }
    }

    /// <summary>
    /// Reads a light's answer to the search. Null for anything else (the adverts lights send by themselves, other
    /// devices, an answer that names another address than the one it came from).
    /// </summary>
    public static FoundLight? ParseReply(UdpDatagram datagram)
    {
        string text;
        try { text = Encoding.UTF8.GetString(datagram.Data); }
        catch (ArgumentException) { return null; }
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        const string scheme = "yeelight://";
        if (!headers.TryGetValue("Location", out var location) || !location.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var where = location[scheme.Length..].TrimEnd('/');
        var split = where.LastIndexOf(':');
        var host = split > 0 ? where[..split] : where;
        var port = ControlPort;
        if (split > 0 && (!int.TryParse(where[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return null;
        // A light speaks for itself only: the address it names must be the one the answer came from.
        if (!LanAddress.IsPrivate(host) || !LanAddress.Same(host, datagram.Host)) return null;

        var id = headers.GetValueOrDefault("id", "").ToLowerInvariant();
        if (id.Length is 0 or > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c))) return null;
        var model = Tidy(headers.GetValueOrDefault("model", ""), 40);
        var name = Tidy(headers.GetValueOrDefault("name", ""), 64);
        return new FoundLight(id, name != "" ? name : NameFor(model), model, LanAddress.Normalise(host))
        {
            Data = new Dictionary<string, string>
            {
                ["port"] = port.ToString(CultureInfo.InvariantCulture),
                ["support"] = Tidy(headers.GetValueOrDefault("support", ""), 1000),
                ["model"] = model,
            },
        };
    }

    private static string NameFor(string model) => model != "" ? "Yeelight " + model : "Yeelight";

    private static string Tidy(string text, int most)
    {
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > most ? clean[..most] : clean;
    }

    // ---------------------------------------------------------------- pairing (there is none: it is only checked)

    public async Task<PairResult> PairAsync(ILightNetwork net, PairRequest request, CancellationToken ct)
    {
        var address = request.Address;
        var found = request.Found;
        var port = PortOf(found?.Data);

        // Does it answer commands at all? With LAN Control off the light refuses the connection.
        string name;
        try
        {
            using var wire = new Wire(await net.ConnectTcpAsync(address, port, TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false));
            var reply = await wire.AskAsync(1, Command(1, "get_prop", "name"), ReplyTime, ct).ConfigureAwait(false)
                        ?? throw new IOException("no answer");
            name = reply.Result is { Count: > 0 } r ? Tidy(r[0], 64) : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw new InvalidOperationException(NoAnswer);
        }

        if (found is null)
        {
            // A typed address: the light's lasting id only comes in its answer to the search question, so it is asked directly.
            found = await AskWhoAsync(net, address, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("That Yeelight answered, but didn't say which light it is. Press Search for lights and pick it from the list instead.");
            if (PortOf(found.Data) != port)
                throw new InvalidOperationException("That Yeelight uses an unusual port. Press Search for lights and pick it from the list instead.");
        }

        var caps = Caps.Of(found.Data);
        if (!caps.Main && !caps.Background) throw new InvalidOperationException(WhiteOnly);

        if (name == "") name = found.Name;
        return new PairResult(found.Id, name != "" ? name : NameFor(found.Model), found.Model, address)
        {
            Data = new Dictionary<string, string>
            {
                ["port"] = port.ToString(CultureInfo.InvariantCulture),
                ["support"] = found.Data.GetValueOrDefault("support", ""),
                ["model"] = found.Model,
            },
        };
    }

    /// <summary>The search question sent to one address only.</summary>
    private static async Task<FoundLight?> AskWhoAsync(ILightNetwork net, string address, CancellationToken ct)
    {
        try
        {
            using var port = net.OpenUdp();
            port.Send(Encoding.ASCII.GetBytes(SearchText), address, SearchPort);
            var found = new ConcurrentDictionary<string, FoundLight>(StringComparer.Ordinal);
            await ListenAsync(port, DateTime.UtcNow + TimeSpan.FromSeconds(1.5), found, address, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return found.Values.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static int PortOf(IReadOnlyDictionary<string, string>? data) =>
        data is not null && data.TryGetValue("port", out var p) && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : ControlPort;

    /// <summary>What a light can do, from the list of commands it said it accepts.</summary>
    private sealed record Caps(bool Main, bool Background, bool Music)
    {
        public static Caps Of(IReadOnlyDictionary<string, string> data)
        {
            var support = data.GetValueOrDefault("support", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Nothing known: treated as a plain colour bulb.
            if (support.Length == 0) return new Caps(true, false, true);
            // White-only lights also list set_scene (for brightness and white scenes), so colour is told by set_rgb.
            return new Caps(support.Contains("set_rgb"), support.Contains("bg_set_rgb") || support.Contains("bg_set_scene"), support.Contains("set_music"));
        }
    }

    // ---------------------------------------------------------------- describing

    public async Task<LightDescription> DescribeAsync(ILightNetwork net, PairedLight light, CancellationToken ct)
    {
        var caps = Caps.Of(light.Data);
        if (!caps.Main && !caps.Background) throw new InvalidOperationException(WhiteOnly);
        using (var wire = new Wire(await net.ConnectTcpAsync(light.Address, PortOf(light.Data), TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false)))
        {
            _ = await wire.AskAsync(1, Command(1, "get_prop", "power"), ReplyTime, ct).ConfigureAwait(false)
                ?? throw new IOException("it didn't answer");
        }
        if (caps.Main && caps.Background)
            return LightDescription.Strip(2) with { Note = "It has two lights: the first is the main light, the second is the background light." };
        if (caps.Background)
            return LightDescription.Single() with { Note = "Only its background light has colour, so only that follows. The main light is left alone." };
        return LightDescription.Single();
    }

    // ---------------------------------------------------------------- sending colours

    public async Task<ILightSession> StartAsync(ILightNetwork net, PairedLight light, LightDescription description, CancellationToken ct)
    {
        var caps = Caps.Of(light.Data);
        if (!caps.Main && !caps.Background) throw new InvalidOperationException(WhiteOnly);
        var session = new Session(this, net, light, caps);
        try
        {
            await session.OpenAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One command: a JSON object with all three keys, on one line.</summary>
    private static string Command(int id, string method, params object[] parameters) =>
        $"{{\"id\":{id.ToString(CultureInfo.InvariantCulture)},\"method\":\"{method}\",\"params\":{JsonSerializer.Serialize(parameters)}}}";

    /// <summary>
    /// A colour as the light takes it: the hue at full strength, and how bright as 1 to 100. Black keeps the last hue at
    /// brightness 1 (the light refuses colour 0, and switching it off for every dark frame would make it click and lag).
    /// </summary>
    private static (int Rgb, int Bright) Scene(int colour, int lastRgb)
    {
        int r = (colour >> 16) & 255, g = (colour >> 8) & 255, b = colour & 255;
        var max = Math.Max(r, Math.Max(g, b));
        if (max == 0) return (lastRgb > 0 ? lastRgb : 0xFFFFFF, 1);
        var rgb = ((int)Math.Round(r * 255.0 / max) << 16) | ((int)Math.Round(g * 255.0 / max) << 8) | (int)Math.Round(b * 255.0 / max);
        return (Math.Max(1, rgb), Math.Clamp((int)Math.Round(max * 100.0 / 255.0), 1, 100));
    }

    private sealed record Reply(IReadOnlyList<string>? Result, string? Error);

    /// <summary>
    /// One connection to a light. Everything it says is read in the background, line by line: answers are handed to
    /// whoever asked (matched by id), and the "props" news it sends by itself is thrown away.
    /// </summary>
    private sealed class Wire : IDisposable
    {
        private readonly ITcpLink _link;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<Reply?>> _waiting = new();
        private volatile bool _closed;

        public Wire(ITcpLink link)
        {
            _link = link;
            _ = Task.Run(PumpAsync);
        }

        public bool Closed => _closed;
        public string LocalHost => _link.LocalHost;

        public Task WriteAsync(string line, CancellationToken ct)
        {
            if (_closed) throw new IOException("The light closed the connection.");
            return _link.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct);
        }

        /// <summary>Sends a command and waits for its answer; null when none came in time or the light hung up.</summary>
        public async Task<Reply?> AskAsync(int id, string line, TimeSpan wait, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<Reply?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = answer;
            try
            {
                await WriteAsync(line, ct).ConfigureAwait(false);
                return await answer.Task.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                _waiting.TryRemove(id, out _);
            }
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[2048];
            var pending = new List<byte>();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await _link.ReadAsync(buffer, TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    if (n < 0) continue;
                    pending.AddRange(buffer.AsSpan(0, n).ToArray());
                    int end;
                    while ((end = pending.IndexOf((byte)'\n')) >= 0)
                    {
                        var line = Encoding.UTF8.GetString(pending.GetRange(0, end).ToArray()).Trim();
                        pending.RemoveRange(0, end + 1);
                        if (line.Length > 0) Handle(line);
                    }
                    if (pending.Count > 65536) pending.Clear(); // not a Yeelight talking
                }
            }
            catch
            {
                // closed or broken: the same to the caller
            }
            _closed = true;
            foreach (var w in _waiting.Values) w.TrySetResult(null);
        }

        private void Handle(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                // No id = news the light sends by itself ({"method":"props",...}): skipped.
                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id)) return;
                if (!_waiting.TryGetValue(id, out var answer)) return;
                if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                    answer.TrySetResult(new Reply(result.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList(), null));
                else
                    answer.TrySetResult(new Reply(null,
                        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString() ?? "error"
                            : "error"));
            }
            catch (JsonException)
            {
                // not a line from a Yeelight
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already over */ }
            _link.Dispose();
            foreach (var w in _waiting.Values) w.TrySetResult(null);
        }
    }

    private sealed class Session : ILightSession
    {
        private static readonly string[] MainProps = { "power", "bright", "color_mode", "ct", "rgb", "hue", "sat", "flowing", "flow_params" };
        private static readonly string[] BackgroundProps = { "bg_power", "bg_bright", "bg_lmode", "bg_rgb", "bg_ct", "bg_hue", "bg_sat" };

        private readonly YeelightProvider _owner;
        private readonly ILightNetwork _net;
        private readonly PairedLight _light;
        private readonly Caps _caps;
        private readonly int _port;
        /// <summary>The command each light point's colour goes to, in the description's order.</summary>
        private readonly string[] _targets;
        private readonly int[] _lastRgb;
        private readonly int[] _lastBright;
        private readonly DateTime[] _lastAt;
        private readonly Dictionary<string, string> _before = new(StringComparer.Ordinal);
        private Wire? _control;
        private volatile Wire? _music;
        private int _musicTries;
        private int _id;

        public Session(YeelightProvider owner, ILightNetwork net, PairedLight light, Caps caps)
        {
            _owner = owner;
            _net = net;
            _light = light;
            _caps = caps;
            _port = PortOf(light.Data);
            _targets = (caps.Main, caps.Background) switch
            {
                (true, true) => new[] { "set_scene", "bg_set_scene" },
                (false, true) => new[] { "bg_set_scene" },
                _ => new[] { "set_scene" },
            };
            _lastRgb = new int[_targets.Length];
            _lastBright = new int[_targets.Length];
            _lastAt = new DateTime[_targets.Length];
        }

        /// <summary>Commands a minute on the normal connection: under the light's limit of 60, with room left for the start and the put-back.</summary>
        internal const double SlowCommandsPerMinute = 45;

        // Fast only while the light is connected back to this PC. Otherwise it takes 60 commands a minute and refuses
        // the rest, so it is sent 45: a colour every 1.3 seconds, or every 2.7 when each frame is two commands
        // (main and background light).
        public double MaxFramesPerSecond => _music is { Closed: false } ? 15 : SlowCommandsPerMinute / 60 / _targets.Length;

        // Each put-back command may be asked twice (the second time on a fresh connection).
        public TimeSpan StopBudget => TimeSpan.FromSeconds(6);

        // A Yeelight keeps the colour it was given.
        public TimeSpan KeepAlive => TimeSpan.FromSeconds(60);

        private int NextId() => Interlocked.Increment(ref _id);

        public async Task OpenAsync(CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var control = await ControlAsync(ct).ConfigureAwait(false);

            // How it is now, to put back afterwards. Only the lights that will be changed are read.
            var names = (_caps.Main ? MainProps : Array.Empty<string>()).Concat(_caps.Background ? BackgroundProps : Array.Empty<string>()).ToArray();
            var id = NextId();
            var reply = await control.AskAsync(id, Command(id, "get_prop", names.Cast<object>().ToArray()), _owner.ReplyTime, ct).ConfigureAwait(false)
                        ?? throw new IOException("it didn't answer");
            if (reply.Result is null) throw new IOException("it answered with an error (" + reply.Error + ")");
            for (var i = 0; i < names.Length && i < reply.Result.Count; i++) _before[names[i]] = reply.Result[i];

            if (!_caps.Music) return;
            // The whole start has about ten seconds; the wait for the light to connect back fits inside it.
            var most = TimeSpan.FromSeconds(8.5) - (DateTime.UtcNow - started);
            _music = await TryMusicAsync(most < _owner.CallbackTime ? most : _owner.CallbackTime, ct).ConfigureAwait(false);
            // If the fast connection drops later it is asked for once more; if it never came, it is not asked for again.
            _musicTries = _music is not null ? 1 : 0;
        }

        private async Task<Wire> ControlAsync(CancellationToken ct)
        {
            if (_control is { Closed: false } open) return open;
            _control?.Dispose();
            _control = null;
            return _control = new Wire(await _net.ConnectTcpAsync(_light.Address, _port, TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false));
        }

        /// <summary>Writes on the normal connection, connecting again once if it had gone.</summary>
        private async Task ControlWriteAsync(string line, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var control = await ControlAsync(ct).ConfigureAwait(false);
                    await control.WriteAsync(line, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt == 0)
                {
                    _control?.Dispose();
                    _control = null;
                }
            }
        }

        private async Task ControlAskAsync(TimeSpan wait, CancellationToken ct, string method, params object[] parameters)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var control = await ControlAsync(ct).ConfigureAwait(false);
                    var id = NextId();
                    await control.AskAsync(id, Command(id, method, parameters), wait, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt == 0)
                {
                    _control?.Dispose();
                    _control = null;
                }
            }
        }

        /// <summary>
        /// Asks the light to connect back to this PC for fast updates. Null when it doesn't (a firewall or VPN in the
        /// way, or the light said no). Only a connection from the light's own address is taken.
        /// </summary>
        private async Task<Wire?> TryMusicAsync(TimeSpan most, CancellationToken ct)
        {
            ITcpListener? listener = null;
            try
            {
                var control = await ControlAsync(ct).ConfigureAwait(false);
                // This PC's address as the light sees it: the local end of the connection that is already open.
                var me = control.LocalHost;
                listener = _net.ListenTcp(me);
                var until = DateTime.UtcNow + most;
                var id = NextId();
                var reply = await control.AskAsync(id, Command(id, "set_music", 1, me, listener.Port), _owner.ReplyTime, ct).ConfigureAwait(false);
                if (reply is { Result: null }) return null; // it said no
                while (true)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return null;
                    var link = await listener.AcceptAsync(left, ct).ConfigureAwait(false);
                    if (link is null) return null;
                    if (LanAddress.Same(link.RemoteHost, _light.Address)) return new Wire(link);
                    link.Dispose(); // somebody else: not let in
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
            finally
            {
                listener?.Dispose();
            }
        }

        private void DropMusic()
        {
            _music?.Dispose();
            _music = null;
        }

        public async Task SendAsync(IReadOnlyList<int> colours, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var lines = new List<string>(_targets.Length);
            for (var i = 0; i < _targets.Length && i < colours.Count; i++)
            {
                var (rgb, bright) = Scene(colours[i], _lastRgb[i]);
                // A light that already shows this colour is not told again (commands are rationed).
                if (rgb == _lastRgb[i] && bright == _lastBright[i] && now - _lastAt[i] < TimeSpan.FromSeconds(30)) continue;
                (_lastRgb[i], _lastBright[i], _lastAt[i]) = (rgb, bright, now);
                lines.Add(Command(NextId(), _targets[i], "color", rgb, bright));
            }
            if (lines.Count == 0) return;

            while (true)
            {
                if (_music is { Closed: true }) DropMusic();
                if (_music is null && _musicTries > 0)
                {
                    _musicTries--;
                    _music = await TryMusicAsync(_owner.CallbackTime, ct).ConfigureAwait(false);
                }
                if (_music is not { } music) break;
                try
                {
                    // Nothing is answered on this connection, so nothing is waited for.
                    foreach (var line in lines) await music.WriteAsync(line, ct).ConfigureAwait(false);
                    return;
                }
                catch (IOException)
                {
                    DropMusic();
                }
            }
            foreach (var line in lines) await ControlWriteAsync(line, ct).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Min(700, _owner.ReplyTime.TotalMilliseconds));
            var music = _music;
            _music = null;
            _musicTries = 0;
            try
            {
                if (music is not null) await ControlAskAsync(wait, ct, "set_music", 0).ConfigureAwait(false);
            }
            finally
            {
                music?.Dispose(); // closing it also ends music mode
            }
            string? problem = null;
            if (_caps.Main && Restore("", "color_mode") is { } main) problem = await PutBackAsync(wait, ct, main.Method, main.Parameters).ConfigureAwait(false);
            if (_caps.Background && Restore("bg_", "bg_lmode") is { } background)
                problem = await PutBackAsync(wait, ct, background.Method, background.Parameters).ConfigureAwait(false) ?? problem;
            if (problem is not null) throw new IOException(problem);
        }

        /// <summary>
        /// Sends one put-back command and checks the answer. A light that refuses (it counts commands for each
        /// connection, and says "client quota exceeded" when there were too many) or says nothing is asked once
        /// more on a fresh connection. Null when the light took it, else why not.
        /// </summary>
        private async Task<string?> PutBackAsync(TimeSpan wait, CancellationToken ct, string method, object[] parameters)
        {
            var problem = "it didn't answer";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var control = await ControlAsync(ct).ConfigureAwait(false);
                    var id = NextId();
                    var reply = await control.AskAsync(id, Command(id, method, parameters), wait, ct).ConfigureAwait(false);
                    if (reply is { Result: not null }) return null;
                    problem = reply?.Error is { Length: > 0 } error ? "the light refused: " + DeviceText.Tidy(error, 80) : "it didn't answer";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problem = "it didn't answer";
                }
                _control?.Dispose();
                _control = null;
            }
            return problem;
        }

        /// <summary>
        /// The one command that puts a light (main, or "bg_" for the background) back. A light that was on gets its
        /// colour and brightness back by the mode it was in; a colour flow that was running comes back as its plain
        /// colour (flows are not rebuilt). A light that was off is switched off again; the colour it will show when
        /// next switched on is then the last one sent, because giving back the old colour would switch it on.
        /// </summary>
        private (string Method, object[] Parameters)? Restore(string prefix, string modeName)
        {
            var power = _before.GetValueOrDefault(prefix + "power", "");
            if (power == "off") return (prefix + "set_power", new object[] { "off", "smooth", 300 });
            if (power != "on") return null; // unknown: left as it is
            var bright = Math.Clamp(Number(prefix + "bright", 100), 1, 100);
            int rgb = Number(prefix + "rgb", 0), kelvin = Number(prefix + "ct", 0), hue = Number(prefix + "hue", -1), sat = Number(prefix + "sat", -1);
            object[]? colour = rgb > 0 ? new object[] { "color", rgb, bright } : null;
            object[]? white = kelvin > 0 ? new object[] { "ct", kelvin, bright } : null;
            object[]? hsv = hue >= 0 && sat >= 0 ? new object[] { "hsv", Math.Clamp(hue, 0, 359), Math.Clamp(sat, 0, 100), bright } : null;
            var scene = Number(modeName, 0) switch
            {
                1 => colour ?? white ?? hsv,
                2 => white ?? colour ?? hsv,
                3 => hsv ?? colour ?? white,
                _ => colour ?? white ?? hsv,
            };
            return scene is null ? null : (prefix + "set_scene", scene);
        }

        private int Number(string name, int otherwise) =>
            int.TryParse(_before.GetValueOrDefault(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : otherwise;

        public ValueTask DisposeAsync()
        {
            DropMusic();
            _control?.Dispose();
            _control = null;
            return ValueTask.CompletedTask;
        }
    }
}
