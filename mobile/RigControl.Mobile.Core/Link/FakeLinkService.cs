namespace RigControl.Mobile.Core.Link;

/// <summary>
/// A stand-in for the real link, with sample PCs, so the app can be built and tried before RigControl.Link and the
/// relay exist. Nothing leaves the phone. Readings move a little on every snapshot; actions change the sample
/// state and raise the same events the real link will.
/// Pairing: any valid code links a new sample PC, except a code of all zeros, which fails like an expired code.
/// </summary>
public sealed class FakeLinkService : ILinkService
{
    private readonly object _gate = new();
    private readonly List<LinkedPc> _pcs = [];
    private readonly Dictionary<string, PcSnapshot> _state = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connected = new(StringComparer.Ordinal);
    private readonly Random _random;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;

    public event EventHandler<LinkEvent>? EventReceived;

    /// <param name="withSamples">Start with two sample PCs (one online, one offline).</param>
    /// <param name="delay">Pretend network time per call (zero in tests).</param>
    public FakeLinkService(bool withSamples = true, TimeSpan? delay = null, TimeProvider? time = null, int seed = 7)
    {
        _random = new Random(seed);
        _delay = delay ?? TimeSpan.FromMilliseconds(350);
        _time = time ?? TimeProvider.System;
        if (!withSamples)
            return;

        var now = _time.GetUtcNow();
        Add(new LinkedPc("sample-desk-pc", "Desk PC", "relay.sample", true, now), SampleSnapshot(now, liquid: true));
        Add(new LinkedPc("sample-living-room", "Living room PC", "relay.sample", false, now.AddHours(-26)),
            SampleSnapshot(now.AddHours(-26), liquid: false) with { Lighting = new LightingState { On = false, Effects = Effects } });
    }

    private static readonly LightingEffect[] Effects =
    [
        new("screen", "Screen ambience"), new("solid", "Solid"), new("breathing", "Breathing"), new("cycle", "Colour cycle"),
        new("wave", "Wave"), new("rain", "Rain"), new("visualiser", "Visualiser"),
    ];

    private void Add(LinkedPc pc, PcSnapshot snapshot)
    {
        _pcs.Add(pc);
        _state[pc.Id] = snapshot;
    }

    private static PcSnapshot SampleSnapshot(DateTimeOffset at, bool liquid) => new()
    {
        TakenAt = at,
        CpuTempC = 48,
        GpuTempC = 41,
        LiquidTempC = liquid ? 31.5 : null,
        CpuLoad = 12,
        GpuLoad = 6,
        Fans = liquid
            ? [new("Pump", 2400), new("Radiator 1", 980), new("Radiator 2", 1010), new("Rear", 760)]
            : [new("CPU fan", 1150), new("Case", 700)],
        Recording = new RecordingState { IsRecording = false, ReplayBufferOn = true },
        Lighting = new LightingState { On = true, Effect = "wave", Effects = Effects },
        NowPlaying = liquid ? "Listening to Spotify" : null,
    };

    private Task Wait(CancellationToken ct) => _delay > TimeSpan.Zero ? Task.Delay(_delay, ct) : Task.CompletedTask;

    public async Task<PairResult> PairAsync(PairingRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report("Linking with the sample PC...");
        await Wait(cancellationToken).ConfigureAwait(false);
        if (request.Code.Value.All(c => c == '0'))
            return PairResult.Fail("That code has expired or was already used. Show a new code on the PC.");

        var now = _time.GetUtcNow();
        LinkedPc pc;
        lock (_gate)
        {
            var id = "sample-" + request.Code.Value[..8].ToLowerInvariant();
            if (_pcs.Any(p => p.Id == id))
                return PairResult.Fail("This phone is already linked to that PC.");
            pc = new LinkedPc(id, $"New PC {_pcs.Count + 1}", request.Relay, true, now);
            Add(pc, SampleSnapshot(now, liquid: false));
        }
        return PairResult.Success(pc);
    }

    /// <summary>The sample never asks to replace a link, so there is nothing to confirm.</summary>
    public Task<PairResult> ConfirmReplaceAsync(string replaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PairResult.Fail("Nothing is waiting to be replaced. Show a new code on the PC and try again."));

    public void DiscardReplace(string replaceId)
    {
    }

    public async Task<IReadOnlyList<LinkedPc>> GetPcsAsync(CancellationToken cancellationToken = default)
    {
        await Wait(cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return _pcs.ToArray();
    }

    public async Task ConnectAsync(string pcId, CancellationToken cancellationToken = default)
    {
        await Wait(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            Find(pcId);
            _connected.Add(pcId);
        }
    }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task DisconnectAsync(string pcId)
    {
        lock (_gate)
            _connected.Remove(pcId);
        return Task.CompletedTask;
    }

    public async Task<PcSnapshot> GetSnapshotAsync(string pcId, CancellationToken cancellationToken = default)
    {
        await Wait(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var pc = Find(pcId);
            var s = _state[pcId];
            if (!pc.Online)
                return s;
            s = s with
            {
                TakenAt = _time.GetUtcNow(),
                CpuTempC = Drift(s.CpuTempC, 1.5, 32, 92),
                GpuTempC = Drift(s.GpuTempC, 1.5, 30, 85),
                LiquidTempC = Drift(s.LiquidTempC, 0.2, 25, 45),
                CpuLoad = Drift(s.CpuLoad, 6, 1, 100),
                GpuLoad = Drift(s.GpuLoad, 6, 0, 100),
                Fans = s.Fans.Select(f => f with { Rpm = Math.Max(0, f.Rpm + _random.Next(-25, 26)) }).ToArray(),
            };
            _state[pcId] = s;
            return s;
        }
    }

    private double? Drift(double? v, double step, double min, double max) =>
        v is null ? null : Math.Round(Math.Clamp(v.Value + (_random.NextDouble() * 2 - 1) * step, min, max), 1);

    public async Task<ActionResult> DoAsync(string pcId, PcAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Wait(cancellationToken).ConfigureAwait(false);
        PcSnapshot updated;
        string? notice = null;
        lock (_gate)
        {
            var pc = Find(pcId);
            if (!pc.Online)
                return ActionResult.Fail($"{pc.Name} is offline.");
            var s = _state[pcId];
            var now = _time.GetUtcNow();
            switch (action.Name)
            {
                case PcAction.LightingOn:
                    s = s with { Lighting = s.Lighting with { On = true, Effect = s.Lighting.Effect ?? "solid" } };
                    break;
                case PcAction.LightingOff:
                    s = s with { Lighting = s.Lighting with { On = false } };
                    break;
                case PcAction.LightingSetEffect:
                    var effect = action.Args["effect"];
                    if (s.Lighting.Effects.All(e => e.Id != effect))
                        return ActionResult.Fail("The PC doesn't have that effect.");
                    s = s with { Lighting = s.Lighting with { On = true, Effect = effect } };
                    break;
                case PcAction.CaptureStart:
                    if (s.Recording.IsRecording)
                        return ActionResult.Fail("Already recording.");
                    s = s with { Recording = s.Recording with { IsRecording = true, RecordingSince = now } };
                    notice = "Recording started";
                    break;
                case PcAction.CaptureStop:
                    if (!s.Recording.IsRecording)
                        return ActionResult.Fail("Not recording.");
                    s = s with { Recording = s.Recording with { IsRecording = false, RecordingSince = null } };
                    notice = "Recording saved";
                    break;
                case PcAction.CaptureSaveReplay:
                    if (!s.Recording.ReplayBufferOn)
                        return ActionResult.Fail("Instant replay is off on the PC.");
                    notice = "Replay saved";
                    break;
                case PcAction.PresenceSetCustom:
                    notice = "Discord status changed";
                    break;
                case PcAction.PresencePhoneMusic:
                    break;
                case PcAction.PresenceSpotifyConnect:
                    notice = "Spotify connected";
                    break;
                case PcAction.PresenceSpotifyDisconnect:
                    notice = "Spotify disconnected";
                    break;
                default:
                    return ActionResult.Fail("Not allowed.");
            }
            _state[pcId] = updated = s;
        }

        EventReceived?.Invoke(this, new PcSnapshotReceived(pcId, updated));
        if (notice is not null)
            EventReceived?.Invoke(this, new PcNotice(pcId, action.Name, notice));
        return ActionResult.Success;
    }

    public Task ForgetAsync(string pcId)
    {
        lock (_gate)
        {
            _pcs.RemoveAll(p => p.Id == pcId);
            _state.Remove(pcId);
            _connected.Remove(pcId);
        }
        return Task.CompletedTask;
    }

    /// <summary>Test and demo hook: the PC goes online or offline, as the relay's "presence" would say.</summary>
    public void SetOnline(string pcId, bool online)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var i = _pcs.FindIndex(p => p.Id == pcId);
            if (i < 0)
                return;
            _pcs[i] = _pcs[i] with { Online = online, LastSeen = now };
        }
        EventReceived?.Invoke(this, new PcPresenceChanged(pcId, online, now));
    }

    private LinkedPc Find(string pcId) =>
        _pcs.FirstOrDefault(p => p.Id == pcId) ?? throw new KeyNotFoundException("That PC is not linked to this phone.");
}
