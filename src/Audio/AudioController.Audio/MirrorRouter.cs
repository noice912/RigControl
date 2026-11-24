using System.Runtime.InteropServices;
using AudioController.Audio.Routing;
using AudioController.Core.AudioMath;
using AudioController.Core.Devices;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioController.Audio;

/// <summary>The state of one "Also play on" output (display / diagnostics only).</summary>
/// <param name="DeviceId">The playback endpoint.</param>
/// <param name="DeviceName">Its name.</param>
/// <param name="Running">The copy is playing on it.</param>
/// <param name="Error">Why it is not playing (it is retried by itself), or null.</param>
/// <param name="DelayMs">The delay in use.</param>
/// <param name="VolumePercent">The volume in use.</param>
/// <param name="BufferedMs">Audio waiting in its buffer (the cushion, ~40 ms; the delay line comes on top).</param>
/// <param name="Stats">Drift corrections and underruns so far.</param>
public sealed record MirrorTargetStatus(
    string DeviceId, string DeviceName, bool Running, string? Error, int DelayMs, int VolumePercent, double BufferedMs, MirrorBufferStats Stats);

/// <summary>A snapshot of the <see cref="MirrorRouter"/>.</summary>
/// <param name="Running">The headset's loopback is being captured.</param>
/// <param name="SourceId">The headset being copied (the one asked for, even while it is failing), or null when stopped.</param>
/// <param name="SourceError">Why the headset can't be captured right now (retried by itself), or null.</param>
/// <param name="LowLatencyCapture">Event-driven loopback (else NAudio's polled fallback, with a larger cushion).</param>
/// <param name="Targets">One entry per output asked for, in order.</param>
public sealed record MirrorRouterStatus(
    bool Running, string? SourceId, string? SourceError, bool LowLatencyCapture, IReadOnlyList<MirrorTargetStatus> Targets)
{
    /// <summary>Stopped, nothing asked for.</summary>
    public static MirrorRouterStatus Stopped { get; } = new(false, null, null, false, Array.Empty<MirrorTargetStatus>());

    /// <summary>The outputs the copy is playing on right now.</summary>
    public IEnumerable<MirrorTargetStatus> RunningTargets => Targets.Where(t => t.Running);
}

/// <summary>
/// "Also play on": copies everything that plays on the headset to any number of other playback devices (e.g. an optical
/// / Toslink output) while the app runs. Windows renders a program to one endpoint only, so the router WASAPI-loopback-
/// captures the HEADSET endpoint — after Equalizer APO, so the copy already carries the user's EQ and effects; the router
/// never equalizes the extra outputs.
/// </summary>
/// <remarks>
/// <para>Per output: <see cref="MirrorBuffer"/> (primes to a ~40 ms cushion — 100 ms with the polled capture fallback —
/// pads silence while nothing plays, and drops or repeats one frame now and then so the different hardware clocks never
/// make latency grow or the buffer run dry) → <see cref="DelayLine"/> (0–500 ms, to line up with Bluetooth) →
/// <see cref="GainStage"/> (0–100 %, never a boost) → <c>SampleChain.AdaptTo</c> (only the format conversion the device
/// needs) → ±1.0 clamp → shared-mode, event-driven <see cref="WasapiOut"/> (30 ms).</para>
/// <para>Never an output: the headset itself, any endpoint of the virtual-mic cable ("CABLE Input" / "CABLE In 16ch",
/// renamed or not) — those are refused before any device is opened. It never touches the microphone, the virtual cable,
/// the soundboard bus or Equalizer APO's files.</para>
/// <para>Robustness: an output that fails or is unplugged mid-stream is stopped on its own (the others keep playing),
/// <see cref="RouterError"/> and <see cref="StatusChanged"/> are raised, and it is retried with a growing back-off (1 s
/// up to 30 s) and immediately on <see cref="Apply"/> with <c>retryFailedNow</c> (the app passes it on every device
/// scan, so it restarts as soon as Windows reports the device back). Losing the headset stops everything the same way.
/// Audio callbacks never throw; <see cref="Stop"/> and <see cref="Dispose"/> are idempotent. Mirror audio lives in
/// memory only. Events are raised on thread-pool threads.</para>
/// </remarks>
public sealed class MirrorRouter : IDisposable
{
    /// <summary>Render latency of each output (shared mode, event-driven).</summary>
    public const int RenderLatencyMs = 30;

    /// <summary>Event-driven loopback buffer.</summary>
    public const int CaptureBufferMs = 20;

    /// <summary>Buffer cushion with the event-driven capture (~10 ms packets).</summary>
    public const double EventSetpointMs = 40;

    /// <summary>Buffer cushion with NAudio's polled loopback capture (~50 ms bursts).</summary>
    public const double PolledSetpointMs = 100;

    /// <summary>Drift dead band: corrections start when the average fill is this far from the cushion.</summary>
    public const double DriftToleranceMs = 10;

    /// <summary>A fill this far above the cushion is cut back at once (latency can never grow unbounded).</summary>
    public const double HardLimitMs = 150;

    /// <summary>Fade when the delay slider moves.</summary>
    public const double DelayCrossfadeMs = 10;

    /// <summary>Shown when an output is the headset itself.</summary>
    public const string SelfTargetMessage =
        "That's your headset itself — the copy would play into what it is copied from.";

    /// <summary>Shown when an output is on the virtual-mic cable.</summary>
    public const string CableTargetMessage =
        "That's the virtual mic feed (\"CABLE Input\") — Discord would hear your PC audio, so it is never used.";

    private readonly object _sync = new();
    private readonly IMirrorEndpoints _endpoints;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly Timer _retryTimer;
    private readonly Dictionary<string, TargetRun> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Failure> _targetFailures = new(StringComparer.OrdinalIgnoreCase);
    private string? _desiredSource;
    private List<MirrorRoute> _desired = new();
    private volatile SourceRun? _source;
    private Failure? _sourceFailure;
    private volatile TargetRun[] _fanout = Array.Empty<TargetRun>();
    private volatile bool _disposed;

    /// <summary>Creates a stopped router on the real Windows audio devices.</summary>
    public MirrorRouter()
        : this(new WasapiMirrorEndpoints(), null)
    {
    }

    /// <summary>Test seam: other endpoints (fakes) and another retry back-off.</summary>
    internal MirrorRouter(IMirrorEndpoints endpoints, Func<int, TimeSpan>? backoff)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _backoff = backoff ?? DefaultBackoff;
        _retryTimer = new Timer(_ => OnRetryTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised after anything starts, stops, fails or recovers.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised with a readable message when the headset or an output fails mid-stream.</summary>
    public event EventHandler<string>? RouterError;

    /// <summary>True while the headset's loopback is captured (at least one output asked for).</summary>
    public bool IsRunning => _source is not null;

    /// <summary>The headset being copied, or null when stopped.</summary>
    public string? SourceDeviceId => _source?.DeviceId;

    /// <summary>The outputs the copy is playing on right now.</summary>
    public IReadOnlyList<string> ActiveTargetIds => _fanout.Select(t => t.Route.DeviceId).ToList();

    /// <summary>
    /// Returns null when <paramref name="target"/> may receive a copy of <paramref name="sourceDeviceId"/>, else a
    /// readable reason. Hardware-free: never the headset itself, never a recording device, never the virtual-mic cable
    /// (renamed or not), only active devices.
    /// </summary>
    public static string? CheckTarget(string? sourceDeviceId, AudioDeviceInfo? target)
    {
        if (target is null)
        {
            return "The output was not found. It may have been unplugged or removed.";
        }

        if (sourceDeviceId is not null && string.Equals(target.Id, sourceDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return SelfTargetMessage;
        }

        if (target.Kind != DeviceKind.Playback)
        {
            return $"\"{target.FriendlyName}\" is a recording device, not an output.";
        }

        if (VirtualDeviceDetector.IsOnVirtualMicCable(target.FriendlyName))
        {
            return CableTargetMessage;
        }

        if (target.State != DeviceActivity.Active)
        {
            return $"\"{target.FriendlyName}\" isn't connected.";
        }

        return null;
    }

    /// <summary>Returns null when <paramref name="source"/> (the headset) can be copied, else a readable reason. Hardware-free.</summary>
    public static string? CheckSource(AudioDeviceInfo? source)
    {
        if (source is null)
        {
            return "Your headset was not found. It may have been unplugged or removed.";
        }

        if (source.Kind != DeviceKind.Playback)
        {
            return $"\"{source.FriendlyName}\" is a recording device; the copy is taken from your headset.";
        }

        if (VirtualDeviceDetector.IsOnVirtualMicCable(source.FriendlyName))
        {
            return "The virtual mic feed can't be copied — pick your real headset in Setup.";
        }

        if (source.State != DeviceActivity.Active)
        {
            return $"Your headset \"{source.FriendlyName}\" isn't connected.";
        }

        return null;
    }

    /// <summary>
    /// Copies <paramref name="sourceDeviceId"/> (the headset) to exactly <paramref name="targets"/>: outputs not listed
    /// any more are stopped, new ones are opened, and the delay / volume of running ones change live (no reopening).
    /// A different source restarts everything; an empty list stops everything. Never throws for device problems:
    /// they are reported in the returned status (and retried by themselves).
    /// </summary>
    /// <param name="sourceDeviceId">The headset's playback endpoint id.</param>
    /// <param name="targets">The outputs, delay 0–500 ms and volume 0–100 % (clamped).</param>
    /// <param name="retryFailedNow">Retry failed paths now instead of after their back-off (after a device change).</param>
    /// <exception cref="ObjectDisposedException">The router was disposed.</exception>
    public MirrorRouterStatus Apply(string sourceDeviceId, IReadOnlyList<MirrorRoute> targets, bool retryFailedNow = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        ArgumentNullException.ThrowIfNull(targets);

        var desired = new List<MirrorRoute>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (t is null || string.IsNullOrWhiteSpace(t.DeviceId) || !ids.Add(t.DeviceId))
            {
                continue;
            }

            desired.Add(t with
            {
                DelayMs = MirrorPlanner.ClampDelay(t.DelayMs),
                VolumePercent = MirrorPlanner.ClampVolume(t.VolumePercent),
            });
        }

        bool changed;
        MirrorRouterStatus status;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = false;
            if (!string.Equals(_desiredSource, sourceDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                changed |= StopSourceLocked();
                _sourceFailure = null;
                _targetFailures.Clear();
            }

            _desiredSource = sourceDeviceId;
            _desired = desired;
            if (retryFailedNow)
            {
                long now = Environment.TickCount64;
                if (_sourceFailure is { } sf)
                {
                    _sourceFailure = sf with { NextRetry = now };
                }

                foreach (var key in _targetFailures.Keys.ToList())
                {
                    _targetFailures[key] = _targetFailures[key] with { NextRetry = now };
                }
            }

            changed |= ReconcileLocked();
            ScheduleRetryLocked();
            status = SnapshotLocked();
        }

        if (changed)
        {
            QueueStatus();
        }

        return status;
    }

    /// <summary>Stops copying and releases every device. Idempotent.</summary>
    public void Stop()
    {
        bool stopped;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            bool hadFailures = _sourceFailure is not null || _targetFailures.Count > 0;
            _desiredSource = null;
            _desired = new List<MirrorRoute>();
            _sourceFailure = null;
            _targetFailures.Clear();
            stopped = StopSourceLocked() | hadFailures;
            SafeRun(() => _retryTimer.Change(Timeout.Infinite, Timeout.Infinite));
        }

        if (stopped)
        {
            QueueStatus();
        }
    }

    /// <summary>A snapshot of what runs, what failed and the buffers.</summary>
    public MirrorRouterStatus GetStatus()
    {
        lock (_sync)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>Stops everything. Idempotent; no events are raised afterwards.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _desiredSource = null;
            _desired = new List<MirrorRoute>();
            _sourceFailure = null;
            _targetFailures.Clear();
            StopSourceLocked();
        }

        SafeDispose(_retryTimer);
    }

    // ------------------------------------------------------------------ reconcile

    /// <summary>Brings the running paths in line with the desired ones. Caller holds the lock. True when anything changed.</summary>
    private bool ReconcileLocked()
    {
        bool changed = false;
        long now = Environment.TickCount64;
        var wanted = new HashSet<string>(_desired.Select(d => d.DeviceId), StringComparer.OrdinalIgnoreCase);

        foreach (var id in _targets.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            RemoveTargetLocked(id);
            changed = true;
        }

        foreach (var id in _targetFailures.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _targetFailures.Remove(id);
            changed = true;
        }

        if (_desired.Count == 0 || _desiredSource is null)
        {
            changed |= StopSourceLocked();
            if (_sourceFailure is not null)
            {
                _sourceFailure = null;
                changed = true;
            }

            return changed;
        }

        if (_source is null)
        {
            if (_sourceFailure is { } waiting && waiting.NextRetry > now)
            {
                return changed;
            }

            string sourceId = _desiredSource;
            string? problem = CheckSource(SafeDescribe(sourceId));
            if (problem is null)
            {
                try
                {
                    _source = StartSourceLocked(sourceId);
                    _sourceFailure = null;
                    _targetFailures.Clear();
                    changed = true;
                }
                catch (Exception ex)
                {
                    problem = $"Could not capture your headset: {Describe(ex)}";
                }
            }

            if (problem is not null)
            {
                changed |= RecordFailure(ref _sourceFailure, problem, now, permanent: false);
                return changed;
            }
        }

        var source = _source!;
        foreach (var route in _desired)
        {
            if (_targets.TryGetValue(route.DeviceId, out var running))
            {
                running.Update(route, source.SampleRate);
                continue;
            }

            Failure? failure = _targetFailures.GetValueOrDefault(route.DeviceId);
            if (failure is not null && failure.NextRetry > now)
            {
                continue;
            }

            var device = SafeDescribe(route.DeviceId);
            string message;
            bool permanent = false;
            if (CheckTarget(source.DeviceId, device) is { } refusal)
            {
                message = refusal;
                permanent = refusal is SelfTargetMessage or CableTargetMessage; // never becomes valid: no retries
            }
            else
            {
                try
                {
                    var run = StartTargetLocked(source, route, device!.FriendlyName);
                    _targets[route.DeviceId] = run;
                    _fanout = _fanout.Append(run).ToArray();
                    _targetFailures.Remove(route.DeviceId);
                    changed = true;
                    continue;
                }
                catch (Exception ex)
                {
                    message = $"Could not play on \"{device!.FriendlyName}\": {Describe(ex)}";
                }
            }

            changed |= RecordFailure(ref failure, message, now, permanent);
            _targetFailures[route.DeviceId] = failure!;
        }

        return changed;
    }

    /// <summary>Records (or repeats) a failure with the next back-off. True when the message is new.</summary>
    private bool RecordFailure(ref Failure? failure, string message, long now, bool permanent)
    {
        int attempts = failure is null ? 0 : failure.Attempts + 1;
        long next = permanent ? long.MaxValue : now + (long)_backoff(attempts).TotalMilliseconds;
        bool isNew = failure is null || !string.Equals(failure.Message, message, StringComparison.Ordinal);
        failure = new Failure(message, attempts, next);
        return isNew;
    }

    private void ScheduleRetryLocked()
    {
        if (_disposed)
        {
            return;
        }

        long next = long.MaxValue;
        if (_desiredSource is not null && _desired.Count > 0)
        {
            if (_source is null && _sourceFailure is { } sf)
            {
                next = sf.NextRetry;
            }
            else if (_source is not null)
            {
                foreach (var f in _targetFailures.Values)
                {
                    next = Math.Min(next, f.NextRetry);
                }
            }
        }

        long due = next == long.MaxValue ? Timeout.Infinite : Math.Max(0, next - Environment.TickCount64);
        SafeRun(() => _retryTimer.Change(due, Timeout.Infinite));
    }

    private void OnRetryTimer()
    {
        try
        {
            bool changed;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                changed = ReconcileLocked();
                ScheduleRetryLocked();
            }

            if (changed)
            {
                RaiseStatus();
            }
        }
        catch (Exception)
        {
            // A retry is best-effort and must never crash the process.
        }
    }

    // ------------------------------------------------------------------ paths

    private SourceRun StartSourceLocked(string sourceId)
    {
        var run = new SourceRun(sourceId);
        run.Stream = _endpoints.StartLoopback(
            sourceId,
            (samples, count) => OnSamples(run, samples, count),
            ex => OnSourceStopped(run, ex));
        return run;
    }

    private TargetRun StartTargetLocked(SourceRun source, MirrorRoute route, string deviceName)
    {
        var stream = source.Stream!;
        int rate = stream.SampleRate;
        int channels = stream.Channels;
        var options = MirrorBufferOptions.ForMilliseconds(
            rate, stream.LowLatency ? EventSetpointMs : PolledSetpointMs, DriftToleranceMs, HardLimitMs);
        var buffer = new MirrorBuffer(channels, rate, options);
        var delay = new DelayLine(
            channels,
            DelayLine.FramesFor(MirrorTarget.MaxDelayMs, rate),
            DelayLine.FramesFor(DelayCrossfadeMs, rate),
            DelayLine.FramesFor(route.DelayMs, rate));
        var gain = new GainStage(channels, GainStage.FromPercent(route.VolumePercent));
        var provider = new MirrorTargetProvider(buffer, delay, gain, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        var run = new TargetRun(route with { DeviceName = deviceName }, buffer, delay, gain);

        run.Stream = _endpoints.StartRender(
            route.DeviceId,
            (outRate, outChannels) => new ClampSampleProvider(SampleChain.AdaptTo(provider, outRate, outChannels)),
            ex => OnTargetStopped(run, ex),
            ex => ReportAsync($"Also play on: processing for \"{run.Route.DeviceName}\" failed, so it is silent: {Describe(ex)}"));
        return run;
    }

    private void RemoveTargetLocked(string id)
    {
        if (!_targets.Remove(id, out var run))
        {
            return;
        }

        _fanout = _fanout.Where(t => !ReferenceEquals(t, run)).ToArray();
        run.Stopping = true;
        SafeDispose(run.Stream);
    }

    /// <summary>Stops every output and the capture. Caller holds the lock. True when anything was running.</summary>
    private bool StopSourceLocked()
    {
        bool any = _targets.Count > 0 || _source is not null;
        foreach (var id in _targets.Keys.ToList())
        {
            RemoveTargetLocked(id);
        }

        _fanout = Array.Empty<TargetRun>();
        if (_source is { } source)
        {
            _source = null;
            source.Stopping = true;
            SafeDispose(source.Stream); // joins the capture thread: no packet arrives after this
        }

        return any;
    }

    // ------------------------------------------------------------------ audio-thread callbacks

    private void OnSamples(SourceRun run, float[] samples, int count)
    {
        try
        {
            if (run.Stopping || count <= 0)
            {
                return;
            }

            var span = new ReadOnlySpan<float>(samples, 0, count);
            foreach (var target in _fanout)
            {
                if (!target.Stopping)
                {
                    target.Buffer.Write(span);
                }
            }
        }
        catch (Exception)
        {
            // A malformed packet is dropped; the buffers pad the gap with silence.
        }
    }

    private void OnSourceStopped(SourceRun run, Exception? exception)
    {
        try
        {
            if (run.Stopping)
            {
                return;
            }

            string reason = exception is null ? "it stopped unexpectedly" : Describe(exception);

            // Tear down on the thread pool: this handler runs on the capture thread, which teardown joins.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string? message = null;
                    lock (_sync)
                    {
                        if (_disposed || !ReferenceEquals(_source, run))
                        {
                            return;
                        }

                        StopSourceLocked();
                        message = $"Also play on stopped: your headset stopped ({reason}). It starts again by itself when the headset is back.";
                        Failure? failure = null;
                        RecordFailure(ref failure, message, Environment.TickCount64, permanent: false);
                        _sourceFailure = failure;
                        ScheduleRetryLocked();
                    }

                    RaiseError(message);
                    RaiseStatus();
                }
                catch (Exception)
                {
                    // Fault handling is best-effort and must never crash the process.
                }
            });
        }
        catch (Exception)
        {
            // Never throw from an audio-thread callback.
        }
    }

    private void OnTargetStopped(TargetRun run, Exception? exception)
    {
        try
        {
            if (run.Stopping)
            {
                return;
            }

            string reason = exception is null ? "it stopped unexpectedly" : Describe(exception);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string? message = null;
                    lock (_sync)
                    {
                        string id = run.Route.DeviceId;
                        if (_disposed || !_targets.TryGetValue(id, out var current) || !ReferenceEquals(current, run))
                        {
                            return;
                        }

                        RemoveTargetLocked(id);
                        message = $"Also play on: \"{run.Route.DeviceName}\" stopped ({reason}). It starts again by itself when the device is back.";
                        Failure? failure = _targetFailures.GetValueOrDefault(id);
                        RecordFailure(ref failure, message, Environment.TickCount64, permanent: false);
                        _targetFailures[id] = failure!;
                        ScheduleRetryLocked();
                    }

                    RaiseError(message);
                    RaiseStatus();
                }
                catch (Exception)
                {
                    // Fault handling is best-effort and must never crash the process.
                }
            });
        }
        catch (Exception)
        {
            // Never throw from an audio-thread callback.
        }
    }

    // ------------------------------------------------------------------ status and errors

    private MirrorRouterStatus SnapshotLocked()
    {
        if (_desiredSource is null)
        {
            return MirrorRouterStatus.Stopped;
        }

        var targets = new List<MirrorTargetStatus>(_desired.Count);
        foreach (var route in _desired)
        {
            if (_targets.TryGetValue(route.DeviceId, out var run))
            {
                var buffer = run.Buffer;
                targets.Add(new MirrorTargetStatus(
                    route.DeviceId, run.Route.DeviceName, true, null, route.DelayMs, route.VolumePercent,
                    buffer.BufferedFrames * 1000.0 / buffer.SampleRate, buffer.Stats));
            }
            else
            {
                _targetFailures.TryGetValue(route.DeviceId, out var failure);
                string? error = failure?.Message ?? (_source is null ? null : "starting…");
                targets.Add(new MirrorTargetStatus(route.DeviceId, route.DeviceName, false, error, route.DelayMs, route.VolumePercent, 0, default));
            }
        }

        return new MirrorRouterStatus(
            _source is not null, _desiredSource, _source is null ? _sourceFailure?.Message : null,
            _source?.Stream?.LowLatency == true, targets.AsReadOnly());
    }

    private void QueueStatus() => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            RaiseStatus();
        }
        catch (Exception)
        {
            // Status is display-only; a failure must never crash the process.
        }
    });

    private void RaiseStatus()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber failure must not propagate into router threads.
        }
    }

    private void ReportAsync(string message) => ThreadPool.QueueUserWorkItem(_ => RaiseError(message));

    private void RaiseError(string? message)
    {
        if (_disposed || message is null)
        {
            return;
        }

        try
        {
            RouterError?.Invoke(this, message);
        }
        catch (Exception)
        {
            // A subscriber failure must not propagate into router threads.
        }
    }

    // ------------------------------------------------------------------ helpers

    private static TimeSpan DefaultBackoff(int attempt) => attempt switch
    {
        <= 0 => TimeSpan.FromSeconds(1),
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30),
    };

    private AudioDeviceInfo? SafeDescribe(string deviceId)
    {
        try
        {
            return _endpoints.Describe(deviceId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Describe(Exception ex)
    {
        int hr = ex.HResult;
        if (ex is UnauthorizedAccessException || hr == Interop.HResults.EAccessDenied)
        {
            return "Windows blocked access to the device";
        }

        bool isAudioClientError = (unchecked((uint)hr) & 0xFFFF0000u) == 0x88890000u;
        if (ex is COMException || isAudioClientError)
        {
            return DeviceManager.DescribeHResult(hr);
        }

        return ex.Message;
    }

    internal static void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Teardown is best-effort: a device that already vanished cannot be stopped twice.
        }
    }

    internal static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception)
        {
            // Teardown is best-effort: a device that already vanished cannot be released twice.
        }
    }

    // ------------------------------------------------------------------ nested types

    private sealed record Failure(string Message, int Attempts, long NextRetry);

    /// <summary>The headset's capture.</summary>
    private sealed class SourceRun(string deviceId)
    {
        public string DeviceId { get; } = deviceId;
        public IMirrorStream? Stream { get; set; }
        public int SampleRate => Stream?.SampleRate ?? 48000;

        /// <summary>Set before teardown so the expected stop event and late packets are ignored.</summary>
        public volatile bool Stopping;
    }

    /// <summary>One output: its buffer, delay, volume and render stream.</summary>
    private sealed class TargetRun(MirrorRoute route, MirrorBuffer buffer, DelayLine delay, GainStage gain)
    {
        public MirrorRoute Route { get; private set; } = route;
        public MirrorBuffer Buffer { get; } = buffer;
        public DelayLine Delay { get; } = delay;
        public GainStage Gain { get; } = gain;
        public IMirrorStream? Stream { get; set; }

        /// <summary>Set before teardown so the expected stop event is ignored.</summary>
        public volatile bool Stopping;

        /// <summary>Live delay / volume change (no reopening). The name is kept.</summary>
        public void Update(MirrorRoute next, int sampleRate)
        {
            if (next.DelayMs != Route.DelayMs)
            {
                Delay.SetDelay(DelayLine.FramesFor(next.DelayMs, sampleRate));
            }

            if (next.VolumePercent != Route.VolumePercent)
            {
                Gain.Gain = GainStage.FromPercent(next.VolumePercent);
            }

            Route = next with { DeviceName = Route.DeviceName };
        }
    }
}

/// <summary>Samples from a capture (interleaved float, <c>count</c> valid entries). Called on the capture thread.</summary>
internal delegate void MirrorSampleHandler(float[] samples, int count);

/// <summary>A started capture or render stream. <see cref="IDisposable.Dispose"/> stops and releases it (idempotent, never throws).</summary>
internal interface IMirrorStream : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Event-driven capture (small packets); false for the polled fallback. Renders report true.</summary>
    bool LowLatency { get; }
}

/// <summary>Where the router opens devices (real WASAPI, or fakes in tests).</summary>
internal interface IMirrorEndpoints
{
    /// <summary>The endpoint's name, kind and state, or null when Windows does not know the id.</summary>
    AudioDeviceInfo? Describe(string deviceId);

    /// <summary>Starts loopback-capturing a playback endpoint. Throws when it cannot.</summary>
    IMirrorStream StartLoopback(string deviceId, MirrorSampleHandler onSamples, Action<Exception?> onStopped);

    /// <summary>
    /// Starts rendering on a playback endpoint; <paramref name="buildSource"/> gets the device's rate and channel count and
    /// returns the float source at exactly that format. Throws when it cannot.
    /// </summary>
    IMirrorStream StartRender(string deviceId, Func<int, int, ISampleProvider> buildSource, Action<Exception?> onStopped, Action<Exception> onProcessingError);
}

/// <summary>Buffer → delay → volume for one output, at the headset's rate and channel count.</summary>
internal sealed class MirrorTargetProvider(MirrorBuffer buffer, DelayLine delay, GainStage gain, WaveFormat format) : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = format;

    public int Read(float[] destination, int offset, int count)
    {
        var span = destination.AsSpan(offset, count);
        buffer.Read(span);
        delay.Process(span);
        gain.Process(span);
        return count;
    }
}

/// <summary>The real endpoints: WASAPI loopback capture and shared-mode WasapiOut.</summary>
internal sealed class WasapiMirrorEndpoints : IMirrorEndpoints
{
    public AudioDeviceInfo? Describe(string deviceId)
    {
        using var enumerator = CreateEnumerator();
        using var device = TryGetDevice(enumerator, deviceId);
        if (device is null)
        {
            return null;
        }

        try
        {
            return new AudioDeviceInfo
            {
                Id = device.ID,
                FriendlyName = SafeName(device),
                Kind = device.DataFlow == DataFlow.Capture ? DeviceKind.Capture : DeviceKind.Playback,
                State = device.State switch
                {
                    DeviceState.Active => DeviceActivity.Active,
                    DeviceState.Disabled => DeviceActivity.Disabled,
                    DeviceState.Unplugged => DeviceActivity.Unplugged,
                    _ => DeviceActivity.NotPresent,
                },
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException)
        {
            return null;
        }
    }

    public IMirrorStream StartLoopback(string deviceId, MirrorSampleHandler onSamples, Action<Exception?> onStopped)
    {
        var device = OpenDevice(deviceId);
        try
        {
            try
            {
                return LoopbackStream.Start(device, eventDriven: true, onSamples, onStopped);
            }
            catch (COMException)
            {
                // This endpoint refused event-driven loopback; NAudio's polled loopback capture still works (more delay).
                return LoopbackStream.Start(device, eventDriven: false, onSamples, onStopped);
            }
        }
        catch
        {
            MirrorRouter.SafeDispose(device);
            throw;
        }
    }

    public IMirrorStream StartRender(
        string deviceId, Func<int, int, ISampleProvider> buildSource, Action<Exception?> onStopped, Action<Exception> onProcessingError)
    {
        var device = OpenDevice(deviceId);
        try
        {
            return RenderStream.Start(device, buildSource, onStopped, onProcessingError);
        }
        catch
        {
            MirrorRouter.SafeDispose(device);
            throw;
        }
    }

    private static MMDevice OpenDevice(string deviceId)
    {
        using var enumerator = CreateEnumerator();
        return TryGetDevice(enumerator, deviceId)
               ?? throw new InvalidOperationException("the device was not found (it may have been unplugged or removed)");
    }

    private static MMDeviceEnumerator CreateEnumerator()
    {
        try
        {
            return new MMDeviceEnumerator();
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            throw new InvalidOperationException("Windows audio device services are not available.", ex);
        }
    }

    private static MMDevice? TryGetDevice(MMDeviceEnumerator enumerator, string deviceId)
    {
        try
        {
            return enumerator.GetDevice(deviceId);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException)
        {
            return device.ID;
        }
    }

    /// <summary>
    /// Creates NAudio objects without the caller's SynchronizationContext so their stop events fire on the audio threads
    /// (handled without blocking) rather than queuing behind a possibly busy UI thread.
    /// </summary>
    private static T WithoutSyncContext<T>(Func<T> factory)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return factory();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Loopback capture of one playback endpoint, delivering float samples.</summary>
    private sealed class LoopbackStream : IMirrorStream
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly WaveFormat _format;
        private readonly MirrorSampleHandler _onSamples;
        private readonly Action<Exception?> _onStopped;
        private float[] _scratch = Array.Empty<float>();
        private volatile bool _stopping;
        private int _disposed;

        private LoopbackStream(MMDevice device, WasapiCapture capture, WaveFormat format, bool eventDriven,
            MirrorSampleHandler onSamples, Action<Exception?> onStopped)
        {
            _device = device;
            _capture = capture;
            _format = format;
            LowLatency = eventDriven;
            _onSamples = onSamples;
            _onStopped = onStopped;
        }

        public int SampleRate => _format.SampleRate;

        public int Channels => _format.Channels;

        public bool LowLatency { get; }

        public static LoopbackStream Start(MMDevice device, bool eventDriven, MirrorSampleHandler onSamples, Action<Exception?> onStopped)
        {
            WasapiCapture capture = WithoutSyncContext<WasapiCapture>(() => eventDriven
                ? new EventLoopbackCapture(device, MirrorRouter.CaptureBufferMs)
                : new WasapiLoopbackCapture(device));
            try
            {
                var format = SampleChain.ToPlainEquivalent(capture.WaveFormat)
                             ?? throw new InvalidOperationException($"the headset uses an audio format that can't be read ({capture.WaveFormat.Encoding})");
                var stream = new LoopbackStream(device, capture, format, eventDriven, onSamples, onStopped);
                capture.DataAvailable += stream.OnData;
                capture.RecordingStopped += stream.OnRecordingStopped;
                capture.StartRecording();
                return stream;
            }
            catch
            {
                MirrorRouter.SafeRun(capture.StopRecording);
                MirrorRouter.SafeDispose(capture);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopping = true;
            MirrorRouter.SafeRun(_capture.StopRecording);
            MirrorRouter.SafeDispose(_capture); // joins the capture thread
            MirrorRouter.SafeDispose(_device);
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (_stopping || e.BytesRecorded <= 0)
                {
                    return;
                }

                int count = PcmToFloat.Convert(e.Buffer, e.BytesRecorded, _format, ref _scratch);
                if (count > 0)
                {
                    _onSamples(_scratch, count);
                }
            }
            catch (Exception)
            {
                // A malformed packet is dropped.
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                if (!_stopping)
                {
                    _onStopped(e.Exception);
                }
            }
            catch (Exception)
            {
                // Never throw from an audio-thread callback.
            }
        }
    }

    /// <summary>A shared-mode, event-driven WasapiOut on one output.</summary>
    private sealed class RenderStream : IMirrorStream
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _render;
        private readonly Action<Exception?> _onStopped;
        private volatile bool _stopping;
        private int _disposed;

        private RenderStream(MMDevice device, WasapiOut render, WaveFormat format, Action<Exception?> onStopped)
        {
            _device = device;
            _render = render;
            SampleRate = format.SampleRate;
            Channels = format.Channels;
            _onStopped = onStopped;
        }

        public int SampleRate { get; }

        public int Channels { get; }

        public bool LowLatency => true;

        public static RenderStream Start(
            MMDevice device, Func<int, int, ISampleProvider> buildSource, Action<Exception?> onStopped, Action<Exception> onProcessingError)
        {
            WaveFormat format = GetRenderFormat(device);
            var source = buildSource(format.SampleRate, format.Channels);
            var provider = new FloatWaveProvider(source, format, onProcessingError);
            var render = WithoutSyncContext(() => new WasapiOut(device, AudioClientShareMode.Shared, true, MirrorRouter.RenderLatencyMs));
            try
            {
                render.Init(provider);
                var stream = new RenderStream(device, render, format, onStopped);
                render.PlaybackStopped += stream.OnPlaybackStopped;
                render.Play();
                return stream;
            }
            catch
            {
                MirrorRouter.SafeDispose(render);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopping = true;
            MirrorRouter.SafeRun(_render.Stop); // joins the render thread
            MirrorRouter.SafeDispose(_render);
            MirrorRouter.SafeDispose(_device);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                if (!_stopping)
                {
                    _onStopped(e.Exception);
                }
            }
            catch (Exception)
            {
                // Never throw from an audio-thread callback.
            }
        }

        /// <summary>The device's shared-mode mix format when it is float32 (always, in practice), else float32 at its rate/channels.</summary>
        private static WaveFormat GetRenderFormat(MMDevice device)
        {
            using var client = device.AudioClient;
            var mix = client.MixFormat;
            return SampleChain.IsFloat32(mix) ? mix : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        }
    }

    /// <summary>
    /// Event-driven WASAPI loopback capture with a short buffer (NAudio's <see cref="WasapiLoopbackCapture"/> is polled
    /// with a 100 ms buffer and arrives in ~50 ms bursts). Supported from Windows 10 on.
    /// </summary>
    private sealed class EventLoopbackCapture(MMDevice device, int bufferMilliseconds)
        : WasapiCapture(device, true, bufferMilliseconds)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}

/// <summary>Converts captured PCM / float bytes to interleaved float samples (no other processing).</summary>
internal static class PcmToFloat
{
    /// <summary>Converts <paramref name="bytes"/> bytes of <paramref name="format"/> (a plain PCM / float format) into
    /// <paramref name="destination"/> (grown when needed). Returns the number of samples.</summary>
    public static int Convert(byte[] source, int bytes, WaveFormat format, ref float[] destination)
    {
        int bits = format.BitsPerSample;
        int bytesPerSample = Math.Max(1, bits / 8);
        int samples = bytes / bytesPerSample;
        if (destination.Length < samples)
        {
            destination = new float[samples];
        }

        var dst = destination;
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when bits == 32:
                Buffer.BlockCopy(source, 0, dst, 0, samples * 4);
                break;
            case WaveFormatEncoding.IeeeFloat when bits == 64:
                for (int i = 0; i < samples; i++)
                {
                    dst[i] = (float)BitConverter.ToDouble(source, i * 8);
                }

                break;
            case WaveFormatEncoding.Pcm when bits == 16:
                for (int i = 0; i < samples; i++)
                {
                    dst[i] = BitConverter.ToInt16(source, i * 2) / 32768f;
                }

                break;
            case WaveFormatEncoding.Pcm when bits == 24:
                for (int i = 0, b = 0; i < samples; i++, b += 3)
                {
                    int v = source[b] | (source[b + 1] << 8) | ((sbyte)source[b + 2] << 16);
                    dst[i] = v / 8388608f;
                }

                break;
            case WaveFormatEncoding.Pcm when bits == 32:
                for (int i = 0; i < samples; i++)
                {
                    dst[i] = BitConverter.ToInt32(source, i * 4) / 2147483648f;
                }

                break;
            case WaveFormatEncoding.Pcm when bits == 8:
                for (int i = 0; i < samples; i++)
                {
                    dst[i] = (source[i] - 128) / 128f;
                }

                break;
            default:
                return 0;
        }

        return samples;
    }
}
