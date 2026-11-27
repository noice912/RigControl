using System.Text.Json;
using System.Text.Json.Nodes;
using QRCoder;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Platform;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Link;
using RigControl.Sensors;

namespace RigControl.App.Link;

/// <summary>A linked phone as stored in settings ("link.phones"). Keys are public halves only (base64url SPKI).</summary>
public sealed class LinkedPhoneSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public string AgreementKey { get; set; } = "";
    public DateTime Added { get; set; }
    public DateTime? LastSeen { get; set; }

    /// <summary>The last message counter accepted from this phone (replay protection across restarts).</summary>
    public long LastIn { get; set; }

    /// <summary>The highest message counter this PC sent the phone: never sent lower (even after the clock is set back).</summary>
    public long LastOut { get; set; }

    /// <summary>The epoch of this pairing (16 random bytes, base64url): part of the session key and the AAD.</summary>
    public string Epoch { get; set; } = "";
}

/// <summary>Settings section "link".</summary>
public sealed class LinkSettings
{
    public const string Section = "link";

    /// <summary>False until the first phone is linked; false again when the last one is removed.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The relay. Defaults to this build's <see cref="LinkDefaults.RelayUrl"/>; only wss:// (or ws:// to this computer,
    /// for a local test relay) is used, anything else falls back to the build's address.
    /// </summary>
    public string RelayUrl { get; set; } = LinkDefaults.RelayUrl;

    /// <summary>This PC's link keys, protected for the Windows user (ISecretProtector), base64. Empty until first needed.</summary>
    public string DeviceKey { get; set; } = "";

    public List<LinkedPhoneSettings> Phones { get; set; } = new();
}

public sealed record LinkPairingDto(string Code, IReadOnlyList<string> Groups, string QrSvg, string QrText, string ExpiresAt);

public sealed record LinkPhoneDto(string Id, string Name, string Added, string? LastSeen, bool Online);

/// <summary>What link.getStatus answers and "link.status" carries.</summary>
/// <param name="Connection">"off", "connecting", "online" or "waiting".</param>
/// <param name="KeysUnreadable">This PC's stored link keys could not be read: the phones are kept but can't connect
/// until the user confirms making new keys (link.resetKeys), which unlinks them.</param>
public sealed record LinkStatusDto(bool Enabled, bool RelaySet, string Relay, string Connection, string Message,
    LinkPairingDto? Pairing, string? PairingError, IReadOnlyList<LinkPhoneDto> Phones, bool KeysUnreadable = false);

/// <summary>
/// RigControl Link on the PC (docs/protocols/link.md): pairing codes, linked phones, and answering them through the
/// relay. Only outgoing connections: nothing connects until a pairing starts or a phone is linked. A phone may only
/// read a snapshot and run the fixed list in <see cref="LinkActions"/>; every action goes through the normal RPC
/// dispatcher (so the same checks apply) and shows a tray notice (except the phone's song, which repeats every few
/// seconds and is only named in the log, at most once a minute).
/// </summary>
public sealed class LinkModule : IAppModule, IPcLinkHost
{
    internal const int MaxPhones = 32;

    private static readonly byte[] KeyPurpose = "RigControl.Link.DeviceKeys.v1"u8.ToArray();

    private readonly RpcDispatcher _dispatcher;
    private readonly EventBus _bus;
    private readonly Action<string, string> _notice;
    private readonly IRelayTransport _transport;
    private readonly object _gate = new();
    private ModuleContext? _ctx;
    private ILog? _log;
    private LinkSettings _settings = new();
    private DeviceKeys? _keys;
    private PcSide? _pc;
    private ModuleStatus _status = ModuleStatus.NotStarted;
    private SensorSnapshotDto? _sensors;
    private CancellationTokenSource? _pairingCts;
    private LinkPairingDto? _pairing;
    private string? _pairingError;
    private DateTime _lastCounterSave = DateTime.MinValue;
    private bool _keysUnreadable;

    internal const string KeysUnreadableMessage =
        "This PC's phone link keys can't be read (for example after restoring settings or under another Windows user). "
        + "Your linked phones are kept but can't connect. Make new keys to link phones again.";

    /// <param name="buildRelayUrl">This build's relay (tests pass a ".invalid" one to run as a build without a relay).</param>
    public LinkModule(RpcDispatcher dispatcher, EventBus bus, Action<string, string> notice, IRelayTransport? transport = null,
        string buildRelayUrl = LinkDefaults.RelayUrl)
    {
        _dispatcher = dispatcher;
        _bus = bus;
        _notice = notice;
        _transport = transport ?? new WebSocketRelayTransport();
        _buildRelayUrl = buildRelayUrl;
    }

    private readonly string _buildRelayUrl;

    public string Name => "link";
    public string DisplayName => "Phone link";

    public ModuleStatus Status
    {
        get { lock (_gate) return _status; }
    }

    // ---------------------------------------------------------------- start / stop

    private IPhone.IphoneRenewal? _iphoneRenewal;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Log;
        // Signs RigControl Mobile on a connected iPhone again before its free 7-day signature runs out.
        _iphoneRenewal = new IPhone.IphoneRenewal(_log);
        _bus.Published += (topic, payload) =>
        {
            if (topic == "sensors.snapshot" && payload is SensorSnapshotDto s) _sensors = s;
        };
        lock (_gate)
        {
            _settings = context.Settings.Get<LinkSettings>(LinkSettings.Section);
            // Phones with a broken entry are left out rather than letting a bad settings file break the link.
            _settings.Phones = _settings.Phones.Where(ValidPhone).GroupBy(p => p.Id).Select(g => g.First()).ToList();
            _settings.Enabled = _settings.Phones.Count > 0;
            _status = Relay() is null
                ? new ModuleStatus(ModuleState.Degraded, "The relay address is not set in this build, so phones can't be linked yet.")
                : new ModuleStatus(ModuleState.Ready, _settings.Phones.Count == 0 ? "No phone linked." : null);
        }
        // Stored keys are read now, so a key that can't be read shows at once (and nothing is replaced without asking).
        if (_settings.DeviceKey != "") TryKeys();
        if (_settings.Enabled && !_keysUnreadable) EnsureRunning();
        return Task.CompletedTask;
    }

    private static bool ValidPhone(LinkedPhoneSettings p)
        => B64Url.TryDecode(p.SigningKey, out var s) && DeviceKeys.IsValidPublicKey(s) && DeviceKeys.IdOf(s) == p.Id
           && B64Url.TryDecode(p.AgreementKey, out var a) && DeviceKeys.IsValidPublicKey(a) && LinkProtocol.IsEpoch(p.Epoch);

    /// <summary>The relay to use, or null when this build has none (the placeholder) or the address is not usable.</summary>
    private Uri? Relay()
    {
        var text = LinkEndpoints.ParseRelay(_settings.RelayUrl) is not null && !LinkDefaults.IsPlaceholder(_settings.RelayUrl)
            ? _settings.RelayUrl
            : _buildRelayUrl;
        return LinkDefaults.IsPlaceholder(text) ? null : LinkEndpoints.ParseRelay(text);
    }

    private bool TryKeys()
    {
        try
        {
            Keys();
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
    }

    /// <summary>
    /// This PC's keys: the stored ones, or new ones when none are stored yet. Stored keys that can't be read are never
    /// replaced here: the module is Degraded, the phones stay listed, and only link.resetKeys (after the user confirms)
    /// makes new ones.
    /// </summary>
    private DeviceKeys Keys()
    {
        lock (_gate)
        {
            if (_keys is not null) return _keys;
            if (_keysUnreadable) throw RpcException.Unavailable(KeysUnreadableMessage);
            var secrets = _ctx!.Platform.Secrets;
            if (_settings.DeviceKey != "")
            {
                try
                {
                    var raw = secrets.Unprotect(Convert.FromBase64String(_settings.DeviceKey), KeyPurpose);
                    return _keys = DeviceKeys.ImportPrivate(raw);
                }
                catch (Exception ex)
                {
                    _log?.Warn("Phone link: the stored keys could not be read (" + ex.GetType().Name + "); phones are kept, new keys only when confirmed.");
                    _keysUnreadable = true;
                    _status = new ModuleStatus(ModuleState.Degraded, KeysUnreadableMessage);
                    throw RpcException.Unavailable(KeysUnreadableMessage);
                }
            }
            _keys = DeviceKeys.Create();
            _settings.DeviceKey = Convert.ToBase64String(secrets.Protect(_keys.ExportPrivate(), KeyPurpose));
            Save();
            return _keys;
        }
    }

    private void Save()
    {
        lock (_gate) _ctx?.Settings.Set(LinkSettings.Section, _settings);
    }

    /// <summary>Starts the relay connection if it isn't running. Returns false when there is no relay in this build.</summary>
    private bool EnsureRunning()
    {
        var relay = Relay();
        if (relay is null) return false;
        PcSide pc;
        lock (_gate)
        {
            if (_pc is not null) return true;
            pc = _pc = new PcSide(_transport, relay, Keys(), this, new PcSideOptions
            {
                Log = s => _log?.Info(s),
                Relay = new RelayLinkOptions { Log = s => _log?.Info(s) },
            });
        }
        pc.StateChanged += PublishStatus;
        pc.Start();
        _log?.Info("Phone link: connecting to the relay.");
        PublishStatus();
        return true;
    }

    private async Task StopIfIdleAsync()
    {
        PcSide? pc;
        lock (_gate)
        {
            if (_settings.Phones.Count > 0 || _pairingCts is not null || _pc is null) return;
            pc = _pc;
            _pc = null;
        }
        await pc.DisposeAsync().ConfigureAwait(false);
        _log?.Info("Phone link: no phone linked, disconnected from the relay.");
        PublishStatus();
    }

    public async ValueTask DisposeAsync()
    {
        if (_iphoneRenewal is not null) await _iphoneRenewal.DisposeAsync().ConfigureAwait(false);
        PcSide? pc;
        lock (_gate)
        {
            _pairingCts?.Cancel();
            pc = _pc;
            _pc = null;
            if (_ctx is not null) _ctx.Settings.Set(LinkSettings.Section, _settings);
        }
        if (pc is not null) await pc.DisposeAsync().ConfigureAwait(false);
        _keys?.Dispose();
    }

    // ---------------------------------------------------------------- RPC

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        switch (method)
        {
            case "getStatus": return BuildStatus();
            case "startPairing": return StartPairing();
            case "cancelPairing": await CancelPairingAsync().ConfigureAwait(false); return BuildStatus();
            case "removePhone": await RemovePhoneAsync(RpcArgs.Require<PhoneArgs>(args).Id).ConfigureAwait(false); return BuildStatus();
            case "resetKeys": ResetKeys(RpcArgs.Require<ResetArgs>(args).Confirm); return BuildStatus();
            case "openIphoneInstaller": OpenIphoneInstaller(); return null;
            case "getAppleComponents": return await AppleComponentsAsync(ct).ConfigureAwait(false);
            default: throw RpcException.UnknownMethod(Name, method);
        }
    }

    /// <summary>
    /// Opens RigControl's iPhone installer: this same exe with "--iphone", started WITHOUT administrator rights, so the
    /// Apple ID password and the signing key never reach this (elevated) process.
    /// </summary>
    private static void OpenIphoneInstaller()
    {
        var exe = System.Environment.ProcessPath ?? throw new InvalidOperationException("RigControl can't find its own program file.");
        RigControl.Presence.Launcher.StartProgram(exe, "--iphone");
    }

    /// <summary>
    /// Whether Apple's Windows components the iPhone installer needs are on this PC: the sign-in component (CoreADI,
    /// from Apple Devices, iTunes or Apple Application Support) and Apple's USB service (usbmuxd on 127.0.0.1:27015).
    /// Nothing is loaded here; the files are only looked for.
    /// </summary>
    private static async Task<object> AppleComponentsAsync(CancellationToken ct)
    {
        var signIn = RigControl.iPhone.Anisette.CoreAdiLibrary.Candidates().Count > 0;
        bool usb;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, 27015, timeout.Token).ConfigureAwait(false);
            usb = true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            usb = false;
        }
        return new { signIn, usb, storeUrl = "https://apps.microsoft.com/detail/9NP83LWLPZ9K" };
    }

    private sealed class PhoneArgs
    {
        public string? Id { get; set; }
    }

    private sealed class ResetArgs
    {
        public bool Confirm { get; set; }
    }

    /// <summary>
    /// link.resetKeys {"confirm":true}: only when the stored keys can't be read. Forgets them and the phones linked with
    /// them (they can't connect anyway); new keys are made when the next phone is linked.
    /// </summary>
    private void ResetKeys(bool confirm)
    {
        if (!confirm) throw RpcException.InvalidArgs("Confirm making new keys: the linked phones are removed.");
        lock (_gate)
        {
            if (!_keysUnreadable) throw RpcException.InvalidArgs("This PC's link keys are fine; nothing to reset.");
            _keysUnreadable = false;
            _keys = null;
            _settings.DeviceKey = "";
            _settings.Phones.Clear();
            _settings.Enabled = false;
            _status = Relay() is null
                ? new ModuleStatus(ModuleState.Degraded, "The relay address is not set in this build, so phones can't be linked yet.")
                : new ModuleStatus(ModuleState.Ready, "No phone linked.");
            Save();
        }
        _log?.Info("Phone link: unreadable keys forgotten after the user confirmed; phones must be linked again.");
        PublishStatus();
    }

    private LinkStatusDto StartPairing()
    {
        if (_ctx is null) throw RpcException.Unavailable("The phone link isn't ready yet.");
        lock (_gate)
        {
            if (_keysUnreadable) throw RpcException.Unavailable(KeysUnreadableMessage);
        }
        var relay = Relay() ?? throw RpcException.Unavailable("The relay address is not set in this build, so phones can't be linked yet.");
        lock (_gate)
        {
            // The relay's allow-list holds at most 32 phones.
            if (_settings.Phones.Count >= MaxPhones)
                throw RpcException.InvalidArgs($"Up to {MaxPhones} phones can be linked. Remove one first.");
        }
        var code = PairingCode.New();
        var expires = DateTime.UtcNow + LinkDefaults.PairingLifetime;
        var qr = PairingCode.QrText(LinkEndpoints.HostForQr(relay), code);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _pairingCts?.Cancel();
            _pairingCts = cts;
            _pairing = new LinkPairingDto(PairingCode.Display(code), PairingCode.Groups(code), QrSvg(qr), qr, expires.ToString("o"));
            _pairingError = null;
        }
        EnsureRunning();
        var pc = _pc!;
        _ = Task.Run(() => PairAsync(pc, code, cts));
        PublishStatus();
        return BuildStatus();
    }

    private async Task PairAsync(PcSide pc, string code, CancellationTokenSource cts)
    {
        string? error = null;
        PairedPhone? phone = null;
        try
        {
            phone = await pc.PairAsync(code, Environment.MachineName, LinkDefaults.PairingLifetime, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: no message.
        }
        catch (LinkException ex)
        {
            error = ex.Code switch
            {
                "expired" => "The code ran out. Start again for a new one.",
                "replaced" or "nopair" => "The relay ended this code. Start again for a new one.",
                "wrongcode" or "badbox" => "A phone answered with the wrong code. Start again for a new one.",
                "interrupted" or "unreachable" or "dropped" => "The connection to the relay was lost. Start again for a new one.",
                "ratelimit" => "The relay asked to wait a little. Start again in a minute.",
                _ => "Linking stopped. Start again for a new one.",
            };
            _log?.Info("Phone link: pairing ended (" + RelayCodes.Shown(ex.Code) + ").");
        }
        catch (Exception ex)
        {
            error = "The relay could not be reached. Start again for a new one.";
            _log?.Warn("Phone link: pairing failed: " + ex.Message);
        }

        lock (_gate)
        {
            if (_pairingCts != cts) return; // a newer pairing took over
            _pairingCts = null;
            _pairing = null;
            _pairingError = error;
            if (phone is not null)
            {
                Relinked(_settings.Phones, phone, DateTime.UtcNow);
                _settings.Enabled = true;
                _status = new ModuleStatus(ModuleState.Ready, null);
                Save();
            }
        }
        cts.Dispose();
        if (phone is not null)
        {
            _log?.Info("Phone link: a phone was linked.");
            _notice("Phone linked", $"\"{phone.Name}\" can now see this PC and use the phone controls.");
            await pc.RefreshAllowListAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await StopIfIdleAsync().ConfigureAwait(false);
        }
        PublishStatus();
    }

    /// <summary>
    /// Puts a newly paired phone in the list, replacing an earlier pairing of the same phone. A new pairing has a new
    /// epoch and so a new key: frames from the earlier one can't be opened at all, and its counter starts again at 0
    /// (the phone's channel for the new epoch starts fresh too).
    /// </summary>
    internal static void Relinked(List<LinkedPhoneSettings> phones, PairedPhone phone, DateTime now)
    {
        phones.RemoveAll(p => p.Id == phone.Id);
        phones.Add(new LinkedPhoneSettings
        {
            Id = phone.Id, Name = phone.Name, SigningKey = phone.SigningKey, AgreementKey = phone.AgreementKey,
            Added = now, LastSeen = now, LastIn = 0, LastOut = 0, Epoch = phone.Epoch,
        });
    }

    private async Task CancelPairingAsync()
    {
        lock (_gate)
        {
            _pairingCts?.Cancel();
            _pairingCts = null;
            _pairing = null;
            _pairingError = null;
        }
        await StopIfIdleAsync().ConfigureAwait(false);
        PublishStatus();
    }

    private async Task RemovePhoneAsync(string? id)
    {
        PcSide? pc;
        lock (_gate)
        {
            if (id is null || _settings.Phones.RemoveAll(p => p.Id == id) == 0) throw RpcException.InvalidArgs("That phone isn't linked.");
            _settings.Enabled = _settings.Phones.Count > 0;
            if (!_settings.Enabled) _status = new ModuleStatus(_status.State, "No phone linked.");
            Save();
            pc = _pc;
        }
        _log?.Info("Phone link: a phone was removed.");
        // The PC refuses that phone from now on; the relay drops it as soon as it gets the new list.
        if (pc is not null) await pc.RefreshAllowListAsync(CancellationToken.None).ConfigureAwait(false);
        await StopIfIdleAsync().ConfigureAwait(false);
        PublishStatus();
    }

    private static string QrSvg(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(6);
    }

    internal LinkStatusDto BuildStatus()
    {
        lock (_gate)
        {
            var relay = Relay();
            var online = _pc?.OnlinePhones.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            var state = _pc?.State ?? LinkConnectionState.Off;
            var connection = state switch
            {
                LinkConnectionState.Connecting => "connecting",
                LinkConnectionState.Online => "online",
                LinkConnectionState.Waiting => "waiting",
                _ => "off",
            };
            var message = _keysUnreadable ? KeysUnreadableMessage
                : relay is null ? "The relay address is not set in this build, so phones can't be linked yet."
                : state switch
                {
                    LinkConnectionState.Online => "Connected to the relay.",
                    LinkConnectionState.Connecting => "Connecting to the relay...",
                    LinkConnectionState.Waiting => WaitingMessage(_pc?.LastError),
                    _ => _settings.Phones.Count == 0 ? "No phone linked. Nothing connects to the relay." : "Not connected.",
                };
            var phones = _settings.Phones.Select(p => new LinkPhoneDto(p.Id, p.Name, p.Added.ToString("o"), p.LastSeen?.ToString("o"),
                online.Contains(p.Id))).ToList();
            return new LinkStatusDto(_settings.Enabled, relay is not null, relay is null ? "" : relay.Host, connection, message,
                _pairing, _pairingError, phones, _keysUnreadable);
        }
    }

    /// <summary>Fixed wording for why the PC is waiting: the relay's code itself is never shown.</summary>
    internal static string WaitingMessage(string? code) => RelayCodes.Shown(code) switch
    {
        "ratelimit" => "The relay asked to wait before connecting again; trying again soon.",
        "dropped" or "closed" => "The connection to the relay dropped; trying again.",
        "replaced" => "This PC's link connected again from somewhere else; trying again.",
        "handshake" => "The relay didn't answer properly; trying again.",
        "badsig" or "badkey" or "badroom" or "forbidden" or "denied" => "The relay refused this PC; trying again.",
        "timeout" => "The relay timed out; trying again.",
        _ => "The relay can't be reached right now; trying again.",
    };

    /// <summary>Shortest gap between two "link.status" events: bursts (presence, reconnects) are folded into one.</summary>
    internal static readonly TimeSpan StatusGap = TimeSpan.FromMilliseconds(250);

    private readonly object _publishGate = new();
    private long _lastPublish = long.MinValue / 2;
    private bool _publishPending;

    /// <summary>
    /// Sends "link.status" at most about 4 times a second: the first change goes at once, later ones within the gap
    /// are folded into one event sent when it ends (with the state at that moment).
    /// </summary>
    internal void PublishStatus()
    {
        long wait;
        lock (_publishGate)
        {
            if (_publishPending) return;
            var since = Environment.TickCount64 - _lastPublish;
            wait = (long)StatusGap.TotalMilliseconds - since;
            if (wait <= 0) _lastPublish = Environment.TickCount64;
            else _publishPending = true;
        }
        if (wait <= 0)
        {
            PublishNow();
            return;
        }
        _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ =>
        {
            lock (_publishGate)
            {
                _publishPending = false;
                _lastPublish = Environment.TickCount64;
            }
            PublishNow();
        }, TaskScheduler.Default);
    }

    private void PublishNow()
    {
        try { _bus.Publish("link.status", BuildStatus()); }
        catch (Exception ex) { _log?.Warn("Phone link status could not be sent: " + ex.Message); }
    }

    // ---------------------------------------------------------------- IPcLinkHost

    public IReadOnlyList<PhoneRecord> LinkedPhones()
    {
        lock (_gate) return _settings.Phones.Select(p => new PhoneRecord(p.Id, p.Name, p.AgreementKey, p.LastIn, p.Epoch, p.LastOut)).ToList();
    }

    public void Accepted(string phoneId, long counter) => Accepted(phoneId, counter, saveNow: false);

    public void Accepted(string phoneId, long counter, bool saveNow)
    {
        lock (_gate)
        {
            var p = _settings.Phones.FirstOrDefault(x => x.Id == phoneId);
            if (p is null) return;
            p.LastIn = Math.Max(p.LastIn, counter);
            p.LastSeen = DateTime.UtcNow;
            // Reading a snapshot again is harmless, so the counter is written at most every 30 s; an action's counter
            // is written at once (before the rate check, so also a "busy" one) so it can never be replayed after a restart.
            if (saveNow || DateTime.UtcNow - _lastCounterSave > TimeSpan.FromSeconds(30))
            {
                _lastCounterSave = DateTime.UtcNow;
                Save();
            }
        }
    }

    /// <summary>Kept in memory and written with the next save (at most 30 s later, and when the app closes).</summary>
    public void Sent(string phoneId, long counter)
    {
        lock (_gate)
        {
            var p = _settings.Phones.FirstOrDefault(x => x.Id == phoneId);
            if (p is not null) p.LastOut = Math.Max(p.LastOut, counter);
        }
    }

    public void PresenceChanged(string phoneId, bool online)
    {
        lock (_gate)
        {
            var p = _settings.Phones.FirstOrDefault(x => x.Id == phoneId);
            if (p is not null) p.LastSeen = DateTime.UtcNow;
        }
        PublishStatus();
    }

    public async Task<JsonObject> SnapshotAsync(PhoneRecord phone, CancellationToken ct)
    {
        var o = new JsonObject
        {
            ["pc"] = Environment.MachineName,
            ["time"] = DateTime.UtcNow.ToString("o"),
            ["sensors"] = SensorsJson(_sensors),
        };
        var capture = await CallAsync("capture.getStatus", null, ct).ConfigureAwait(false);
        o["recording"] = capture is JsonObject c
            ? new JsonObject
            {
                ["available"] = BoolNode(c["available"]),
                ["recording"] = BoolNode(c["recording"]),
                ["since"] = CutNode(c["recordingSince"], 40),
                ["replayRunning"] = BoolNode(c["replayRunning"]),
            }
            : null;
        var sync = await CallAsync("lighting.getSync", null, ct).ConfigureAwait(false);
        o["lighting"] = sync is JsonObject l
            ? new JsonObject
            {
                ["on"] = BoolNode(l["enabled"]),
                ["effect"] = CutNode(l["effect"], 32),
                ["status"] = CutNode(l["status"], 96),
                ["color"] = CutNode(l["color"], 16),
                ["effects"] = new JsonArray((l["effects"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>().Take(32)
                    .Select(e => (JsonNode)new JsonObject { ["id"] = CutNode(e["id"], 32), ["name"] = CutNode(e["name"], 48) }).ToArray()),
            }
            : null;
        var presence = await CallAsync("presence.getStatus", null, ct).ConfigureAwait(false);
        if (presence is JsonObject p)
        {
            var music = p["music"] as JsonObject;
            var custom = (p["settings"]?["customStatuses"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(20)
                .Select(x => (JsonNode)new JsonObject
                {
                    ["id"] = CutNode(x["id"], 64),
                    ["label"] = Cut(Str(x, "name") is { Length: > 0 } n ? n : Str(x, "details") ?? "", 64),
                    ["on"] = BoolNode(x["enabled"]),
                }).ToArray();
            o["presence"] = new JsonObject
            {
                ["status"] = CutNode(p["status"], 64),
                ["playing"] = CutNode(p["playing"], 96),
                ["music"] = music is null ? null : new JsonObject
                {
                    ["title"] = CutNode(music["title"], 96),
                    ["artist"] = CutNode(music["artist"], 96),
                    ["player"] = CutNode(music["player"], 48),
                    ["playing"] = BoolNode(music["playing"]),
                },
                ["custom"] = new JsonArray(custom),
            };
        }
        else
        {
            o["presence"] = null;
        }
        return o;
    }

    private static JsonObject? SensorsJson(SensorSnapshotDto? s)
    {
        if (s is null) return null;
        return new JsonObject
        {
            ["time"] = s.Time,
            ["cpu"] = s.Cpu is null ? null : new JsonObject
            {
                ["name"] = s.Cpu.Name, ["tempC"] = Num(s.Cpu.PackageTempC ?? s.Cpu.CoreMaxTempC), ["loadPercent"] = Num(s.Cpu.LoadPercent),
                ["powerW"] = Num(s.Cpu.PowerW),
            },
            ["gpu"] = s.Gpu is null ? null : new JsonObject
            {
                ["name"] = s.Gpu.Name, ["tempC"] = Num(s.Gpu.TempC), ["loadPercent"] = Num(s.Gpu.LoadPercent), ["powerW"] = Num(s.Gpu.PowerW),
                ["fanPercent"] = Num(s.Gpu.FanPercent),
            },
            ["memory"] = new JsonObject { ["loadPercent"] = Num(s.Memory.LoadPercent), ["usedGB"] = Num(s.Memory.UsedGB), ["totalGB"] = Num(s.Memory.TotalGB) },
            ["fans"] = new JsonArray(s.Fans.Take(24).Select(f => (JsonNode)new JsonObject { ["name"] = Cut(f.Name, 48), ["rpm"] = f.Rpm }).ToArray()),
            ["temps"] = new JsonArray(s.Temperatures.Take(24).Select(t => (JsonNode)new JsonObject { ["name"] = Cut(t.Name, 48), ["c"] = Num(t.Value) }).ToArray()),
        };
    }

    private static double? Num(double? v) => v is { } d && double.IsFinite(d) ? Math.Round(d, 1) : null;

    /// <summary>At most <paramref name="max"/> UTF-16 units, never ending in half a surrogate pair.</summary>
    internal static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var end = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..end];
    }

    private static bool? BoolNode(JsonNode? n)
        => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    /// <summary>A string from another module's answer, cut to <paramref name="max"/>; anything that isn't a string is null.</summary>
    private static string? CutNode(JsonNode? n, int max)
        => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? Cut(v.GetValue<string>(), max) : null;

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public async Task<LinkActionResult> RunAsync(PhoneRecord phone, LinkAction action, CancellationToken ct)
    {
        Save(); // the accepted counter, before anything happens
        var (rpcs, notice) = Plan(action);
        if (action.Name == LinkActions.PresenceSetCustom)
        {
            var r = await SetCustomAsync(action, ct).ConfigureAwait(false);
            if (r.Ok) Notice(phone, notice);
            return r;
        }
        foreach (var (method, args) in rpcs)
        {
            var (ok, code) = await CallCheckedAsync(method, args, ct).ConfigureAwait(false);
            if (!ok) return RpcFailure(code);
        }
        if (notice.Length > 0) Notice(phone, notice);
        else QuietNote(phone);
        return LinkActionResult.Success;
    }

    private DateTime _quietLoggedAt = DateTime.MinValue;

    /// <summary>An action without a notice (the phone's song): named in the log at most once a minute, never in the tray.</summary>
    private void QuietNote(PhoneRecord phone)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (now - _quietLoggedAt < TimeSpan.FromMinutes(1)) return;
            _quietLoggedAt = now;
        }
        _log?.Info($"Phone link: {phone.Name} is sending what its Music app plays.");
    }

    /// <summary>Which RPCs carry out an allowed action, and the tray notice for it. Never anything outside this list.</summary>
    internal static (IReadOnlyList<(string Method, JsonObject? Args)> Rpcs, string Notice) Plan(LinkAction action)
    {
        switch (action.Name)
        {
            case LinkActions.LightingSetEffect when Str(action.Args, "scene") is { } scene:
                return (new[] { ("lighting.applyScene", (JsonObject?)new JsonObject { ["name"] = scene }) }, $"switched the lights to the scene \"{scene}\"");
            case LinkActions.LightingSetEffect:
                var effect = Str(action.Args, "effect")!;
                return (new[] { ("lighting.setSync", (JsonObject?)new JsonObject { ["enabled"] = true, ["effect"] = effect }) }, $"switched the lights to \"{effect}\"");
            case LinkActions.LightingOn:
                return (new[] { ("lighting.setSync", (JsonObject?)new JsonObject { ["enabled"] = true }) }, "turned the lighting effect on");
            case LinkActions.LightingOff:
                return (new[] { ("lighting.off", (JsonObject?)null) }, "turned the lights off");
            case LinkActions.CaptureStart:
                return (new[] { ("capture.startRecording", (JsonObject?)null) }, "started a recording");
            case LinkActions.CaptureStop:
                return (new[] { ("capture.stopRecording", (JsonObject?)null) }, "stopped the recording");
            case LinkActions.CaptureSaveReplay:
                return (new[] { ("capture.saveReplay", (JsonObject?)null) }, "saved an instant replay");
            case LinkActions.PresenceSetCustom:
                var on = action.Args["on"]?.GetValue<bool>() == true;
                return (Array.Empty<(string, JsonObject?)>(), on ? "switched a custom Discord status on" : "switched a custom Discord status off");
            case LinkActions.PresencePhoneMusic:
                // Arrives every ~15 s while the phone app is open: no notice (see RunAsync).
                return (new[] { ("presence.setPhoneMusic", (JsonObject?)action.Args.DeepClone().AsObject()) }, "");
            case LinkActions.PresenceSpotifyConnect:
                return (new[] { ("presence.connectSpotify", (JsonObject?)new JsonObject { ["refreshToken"] = Str(action.Args, "refreshToken") }) },
                    "connected Spotify for your Discord status");
            case LinkActions.PresenceSpotifyDisconnect:
                return (new[] { ("presence.disconnectSpotify", (JsonObject?)null) }, "disconnected Spotify");
            default:
                throw new InvalidOperationException("Not an allowed action.");
        }
    }

    /// <summary>presence.setCustom: switches one existing custom status on or off and leaves the rest as they are.</summary>
    private async Task<LinkActionResult> SetCustomAsync(LinkAction action, CancellationToken ct)
    {
        var id = Str(action.Args, "id")!;
        var on = action.Args["on"]!.GetValue<bool>();
        var status = await CallAsync("presence.getStatus", null, ct).ConfigureAwait(false);
        if (status?["settings"]?["customStatuses"] is not JsonArray list) return LinkActionResult.Fail("Discord status isn't available on this PC.");
        var item = list.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == id);
        if (item is null) return LinkActionResult.Fail("There is no custom status with that id on this PC.");
        item["enabled"] = on;
        var (ok, code) = await CallCheckedAsync("presence.setCustomStatuses", new JsonObject { ["items"] = list.DeepClone() }, ct).ConfigureAwait(false);
        return ok ? LinkActionResult.Success : RpcFailure(code);
    }

    private void Notice(PhoneRecord phone, string what)
    {
        _log?.Info($"Phone link: {phone.Name} {what}.");
        try { _notice("From your phone", $"{phone.Name} {what}."); }
        catch { /* the tray may be gone while closing */ }
    }

    private async Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken ct)
    {
        var (ok, result, _) = await DispatchAsync(method, args, ct).ConfigureAwait(false);
        return ok ? result : null;
    }

    /// <summary>Runs an RPC; on failure gives its error code (never its message, which stays on the PC).</summary>
    private async Task<(bool Ok, string? Code)> CallCheckedAsync(string method, JsonObject? args, CancellationToken ct)
    {
        var (ok, _, code) = await DispatchAsync(method, args, ct).ConfigureAwait(false);
        return (ok, code);
    }

    /// <summary>What the phone is told when an RPC failed: a short fixed message and the RPC's error code.</summary>
    internal static LinkActionResult RpcFailure(string? code) => code switch
    {
        RpcErrorCodes.Unavailable => LinkActionResult.Fail("That isn't available on the PC right now.", code),
        RpcErrorCodes.NeedsAdmin => LinkActionResult.Fail("RigControl on the PC needs to run as administrator for that.", code),
        RpcErrorCodes.InvalidArgs => LinkActionResult.Fail("The PC refused that.", code),
        RpcErrorCodes.Unsafe => LinkActionResult.Fail("The PC refused that to stay within its safety limits.", code),
        RpcErrorCodes.Cancelled => LinkActionResult.Fail("That was cancelled on the PC.", code),
        _ => LinkActionResult.Fail("The PC could not do that.", "internal"),
    };

    private async Task<(bool Ok, JsonNode? Result, string? Error)> DispatchAsync(string method, JsonObject? args, CancellationToken ct)
    {
        var req = new JsonObject { ["id"] = 0, ["method"] = method, ["params"] = args };
        var text = await _dispatcher.HandleAsync(req.ToJsonString(), ct).ConfigureAwait(false);
        if (JsonNode.Parse(text) is not JsonObject res) return (false, null, null);
        if (res["ok"]?.GetValue<bool>() == true) return (true, res["result"], null);
        if (res["error"]?["message"] is JsonValue msg && msg.TryGetValue(out string? why)) _log?.Info($"Phone link: {method} failed: {why}");
        return (false, null, res["error"]?["code"] is JsonValue c && c.TryGetValue(out string? code) ? code : null);
    }
}
