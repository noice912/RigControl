using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AudioController.Audio.Routing;
using AudioController.Core.Abstractions;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioController.Audio;

/// <summary>
/// Virtual-microphone routing engine: captures the real microphone with WASAPI and writes it to the
/// virtual cable unchanged, summing soundboard audio in only while sounds are playing.
/// </summary>
/// <remarks>
/// <para>Microphone path: capture → jitter buffer → only the format conversion the output device
/// requires (channel up/down-mix; resampling only when rates differ). No gain, EQ or effects.
/// With no sound active the samples are copied through untouched (bit-exact when formats match) and
/// <see cref="AudioController.Core.AudioMath.MixerMath"/> is not called.</para>
/// <para>Soundboard bus: 48 kHz stereo float mixer of per-sound volume stages, then the Effects chain (EQ + effects),
/// then adaptation to the output format, summed in by <c>MixerMath.MixInto</c> (the only clamp).</para>
/// <para>Monitor: an optional second output fed by its own bus and its own readers of each sound;
/// it never carries microphone audio.</para>
/// <para>Mic mute: the capture stream and its device are released entirely (not zero-filled), so no
/// microphone is open while muted; the jitter buffer then supplies silence and rendering continues.</para>
/// <para><see cref="StatusChanged"/> and <see cref="EngineError"/> are raised on thread-pool threads.</para>
/// </remarks>
public sealed class AudioEngine : IAudioEngine
{
    private const int LatencyMs = 20;
    private const int StatusIntervalMs = 100;

    private const string FeedbackLoopMessage =
        "The microphone is the virtual cable's own \"CABLE Output\". Routing it back into \"CABLE Input\" " +
        "would create a feedback loop. Choose your real microphone instead.";
    private static readonly TimeSpan MicBufferDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MicPrime = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MicTrimAbove = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MicTrimTo = TimeSpan.FromMilliseconds(40);

    /// <summary>The soundboard bus format (the canonical library format).</summary>
    private static readonly WaveFormat BusFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly object _sync = new();
    private FxState _fx = FxState.Neutral;
    private volatile Session? _session;
    private string? _monitorDeviceId;
    private bool _monitorEnabled;
    private volatile bool _monitorEqBypass;
    private volatile bool _micMuted;
    private Timer? _statusTimer;
    private int _statusBusy;
    private volatile bool _disposed;

    /// <summary>Creates a stopped engine. Devices are chosen with <see cref="Start"/> and <see cref="SetMonitor"/>.</summary>
    public AudioEngine()
    {
    }

    /// <summary>Raised about ten times per second while running, and once after every start or stop.</summary>
    public event EventHandler<EngineStatus>? StatusChanged;

    /// <summary>Raised with a readable message when a device fails or disappears mid-stream.</summary>
    public event EventHandler<string>? EngineError;

    /// <inheritdoc />
    public bool IsRunning => _session is not null;

    /// <inheritdoc />
    public bool AnySoundPlaying => _session is { } s && Volatile.Read(ref s.Counters.Instances) > 0;

    /// <inheritdoc />
    /// <remarks>
    /// Muting stops and disposes the <see cref="WasapiCapture"/> and its device (events unhooked first, so the
    /// deliberate teardown is never reported as a failure) and clears the jitter buffer; the output keeps
    /// rendering, with silence on the mic branch. Unmuting reopens the configured input; when that fails the
    /// reason is raised through <see cref="EngineError"/> and the engine stays muted.
    /// </remarks>
    public bool MicMuted
    {
        get => _micMuted;
        set
        {
            bool running;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                var session = _session;
                running = session is not null;
                if (value)
                {
                    _micMuted = true;
                    if (session is not null)
                    {
                        ReleaseMic(session);
                    }
                }
                else if (session is null)
                {
                    _micMuted = false;
                }
                else if (session.Mic is null && TryOpenMicLocked(session))
                {
                    _micMuted = false;
                }
            }

            if (running)
            {
                QueueStatus();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Restarts the engine if it is already running. Both devices are validated even when
    /// <see cref="MicMuted"/> is set, but a muted start never opens the microphone.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A device is missing, inactive, or could not be opened.</exception>
    public void Start(string inputDeviceId, string outputDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDeviceId);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();

            MMDevice? input = null;
            MMDevice? output = null;
            Session? session = null;
            try
            {
                using (var enumerator = CreateEnumerator())
                {
                    input = GetActiveDevice(enumerator, inputDeviceId, DataFlow.Capture, "microphone");
                    output = GetActiveDevice(enumerator, outputDeviceId, DataFlow.Render, "virtual cable output");
                }

                if (IsSameVirtualCable(input, output))
                {
                    throw new InvalidOperationException(FeedbackLoopMessage);
                }

                if (_micMuted)
                {
                    // Validated above but never opened: a muted session holds no capture device at all.
                    SafeDispose(input);
                    input = null;
                }

                session = BuildSession(inputDeviceId, outputDeviceId, input, output);
                session.Mic?.Capture.StartRecording();
                session.Render.Play();
            }
            catch (Exception ex)
            {
                if (session is not null)
                {
                    TearDown(session);
                }
                else
                {
                    SafeDispose(input);
                    SafeDispose(output);
                }

                throw new InvalidOperationException($"Could not start audio routing: {Describe(ex)}", ex);
            }

            _session = session;

            if (_monitorEnabled && !string.IsNullOrWhiteSpace(_monitorDeviceId))
            {
                OpenMonitorLocked(session, _monitorDeviceId);
            }

            _statusTimer = new Timer(OnStatusTick, null, StatusIntervalMs, StatusIntervalMs);
        }

        QueueStatus();
    }

    /// <inheritdoc />
    /// <remarks>Idempotent. Stops every sound, the monitor, the capture and the output.</remarks>
    public void Stop()
    {
        bool stopped;
        lock (_sync)
        {
            stopped = StopLocked();
        }

        if (stopped)
        {
            QueueStatus();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Has no effect while the engine is stopped. Each call starts a new instance (sounds may overlap);
    /// toggle and hold behaviour is decided by the caller through <see cref="StopSound"/>.
    /// </remarks>
    /// <exception cref="InvalidDataException"><paramref name="wavData"/> is not a readable PCM/float WAV.</exception>
    /// <exception cref="InvalidOperationException">The mixer cannot accept another simultaneous sound.</exception>
    public void PlaySound(SoundSlot slot, byte[] wavData)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(wavData);

        lock (_sync)
        {
            var session = _session;
            if (session is null)
            {
                return;
            }

            float volume = float.IsFinite(slot.Volume) ? Math.Clamp(slot.Volume, 0f, 2f) : 1f;
            var monitor = session.Monitor;
            var list = session.Instances.GetOrAdd(slot.Id, static _ => new List<SoundInstance>());
            var instance = new SoundInstance(session.Counters, list);

            var main = CreateReader(wavData, volume, instance.OnMainEnded);
            EndTrackingSampleProvider? monitorReader = null;
            if (monitor is not null)
            {
                try
                {
                    monitorReader = CreateReader(wavData, volume, instance.OnMonitorEnded);
                }
                catch
                {
                    main.Discard();
                    throw;
                }
            }

            instance.Activate(main, monitorReader, monitorReader is null ? null : monitor!.Mixer);

            try
            {
                session.MainMixer.AddMixerInput(main);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                main.End();
                monitorReader?.End();
                throw new InvalidOperationException("Too many sounds are playing at once to start another.", ex);
            }

            if (monitorReader is not null)
            {
                try
                {
                    monitor!.Mixer.AddMixerInput(monitorReader);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // The local monitor is a convenience; the sound still plays into the virtual mic.
                    monitorReader.End();
                }
            }
        }
    }

    /// <inheritdoc />
    public void StopSound(Guid slotId)
    {
        lock (_sync)
        {
            var session = _session;
            if (session is null || !session.Instances.TryGetValue(slotId, out var list))
            {
                return;
            }

            foreach (var instance in Snapshot(list))
            {
                instance.Stop(session.MainMixer);
            }
        }
    }

    /// <inheritdoc />
    public bool IsSlotPlaying(Guid slotId)
    {
        var session = _session;
        if (session is null || !session.Instances.TryGetValue(slotId, out var list))
        {
            return false;
        }

        lock (list)
        {
            return list.Count > 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>Clears both the main (virtual mic) and monitor buses.</remarks>
    public void StopAll()
    {
        lock (_sync)
        {
            var session = _session;
            if (session is not null)
            {
                StopAllLocked(session);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// May be called at any time; while running the change applies immediately. Failures to open the
    /// monitor device are reported through <see cref="EngineError"/> rather than thrown, and never affect
    /// the virtual microphone. The monitor cannot be the virtual cable output itself.
    /// </remarks>
    public void SetMonitor(string? deviceId, bool enabled)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _monitorDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            _monitorEnabled = enabled;

            var session = _session;
            if (session is null)
            {
                return;
            }

            if (!enabled || _monitorDeviceId is null)
            {
                CloseMonitorLocked(session);
                return;
            }

            if (session.Monitor is { } current
                && string.Equals(current.DeviceId, _monitorDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CloseMonitorLocked(session);
            OpenMonitorLocked(session, _monitorDeviceId);
        }
    }

    /// <inheritdoc />
    /// <remarks>Applies to the soundboard bus (and its local monitor) only — never to the microphone.</remarks>
    public void SetEq(float[] gainsDb, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        SetFx(FxState.FromGains(FxSettings.DefaultTenBandFrequenciesHz, gainsDb, enabled));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Applies to the soundboard bus (and its local monitor, unless bypassed) only — never to the microphone, whose
    /// samples never pass through an <see cref="FxChain"/>.
    /// </remarks>
    public void SetFx(FxState fx)
    {
        ArgumentNullException.ThrowIfNull(fx);
        lock (_sync)
        {
            _fx = fx;
            if (_session is { } session)
            {
                ApplyEq(session.MainEq);
                if (session.Monitor is { } monitor)
                {
                    ApplyEq(monitor.Eq, forMonitor: true);
                }
            }
        }
    }

    /// <summary>The Effects state last set (see <see cref="SetFx"/>).</summary>
    public FxState Fx => _fx;

    /// <inheritdoc />
    /// <remarks>Only the monitor's own <see cref="FxChain"/> is switched; the main (virtual-mic) bus and the mic path are untouched.</remarks>
    public void SetMonitorEqBypass(bool bypass)
    {
        lock (_sync)
        {
            if (_disposed || _monitorEqBypass == bypass)
            {
                return;
            }

            _monitorEqBypass = bypass;
            if (_session?.Monitor is { } monitor)
            {
                ApplyEq(monitor.Eq, forMonitor: true);
            }
        }
    }

    /// <summary>True while the in-app EQ is bypassed on the monitor bus (see <see cref="SetMonitorEqBypass"/>).</summary>
    public bool MonitorEqBypassed => _monitorEqBypass;

    /// <summary>Stops everything and releases all devices. Idempotent; no events are raised afterwards.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked();
        }
    }

    // ------------------------------------------------------------------ session construction

    /// <summary>
    /// Builds a session on <paramref name="output"/>. With <paramref name="input"/> null (muted) no capture is
    /// created: the mic branch is a silent placeholder already at the output format, replaced on unmute.
    /// </summary>
    private Session BuildSession(string inputId, string outputId, MMDevice? input, MMDevice output)
    {
        WaveFormat renderFormat = GetRenderFormat(output);
        int outRate = renderFormat.SampleRate;
        int outChannels = renderFormat.Channels;

        WasapiCapture? capture = null;
        WasapiOut? render = null;
        try
        {
            WaveFormat micFormat;
            if (input is not null)
            {
                capture = CreateCapture(input, out micFormat);
            }
            else
            {
                micFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, outChannels);
            }

            render = WithoutSyncContext(() => new WasapiOut(output, AudioClientShareMode.Shared, true, LatencyMs));

            var micChain = CreateMicChain(micFormat, renderFormat);

            var counters = new PlaybackCounters();
            var mainMixer = CreateBusMixer();
            var mainEq = CreateEq();
            var eqStage = new FxSampleProvider(mainMixer, mainEq);
            bool busMatchesOutput = outRate == BusFormat.SampleRate && outChannels == BusFormat.Channels;
            Func<ISampleProvider> boardChain = busMatchesOutput
                ? () => eqStage
                : () => SampleChain.AdaptTo(eqStage, outRate, outChannels);

            var routing = new RoutingWaveProvider(
                renderFormat,
                micChain.Samples,
                boardChain,
                counters,
                ex => ReportAsync($"Audio processing failed and the virtual microphone is silent: {Describe(ex)}"));

            render.Init(routing);

            var session = new Session(inputId, outputId, output, render, micChain.Buffer, mainMixer, mainEq, routing, counters);
            render.PlaybackStopped += (_, e) => OnSessionStopped(session, e.Exception, "virtual cable output");
            if (capture is not null)
            {
                AttachMic(session, input!, capture, micChain.Buffer);
            }

            return session;
        }
        catch
        {
            SafeDispose(render);
            SafeDispose(capture);
            throw;
        }
    }

    /// <summary>Opens a shared, event-driven capture on <paramref name="input"/> (not started yet).</summary>
    private static WasapiCapture CreateCapture(MMDevice input, out WaveFormat micFormat)
    {
        var capture = WithoutSyncContext(() => new WasapiCapture(input, true, LatencyMs));
        try
        {
            var captureFormat = capture.WaveFormat;
            var plain = SampleChain.ToPlainEquivalent(captureFormat);
            if (plain is null)
            {
                // Not PCM/float: ask WASAPI for float at the device's own rate and channel count.
                plain = WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, captureFormat.Channels);
                capture.WaveFormat = plain;
            }

            micFormat = plain;
            return capture;
        }
        catch
        {
            SafeDispose(capture);
            throw;
        }
    }

    /// <summary>Mic branch: jitter buffer → only the conversion the output needs. No gain, EQ or effects.</summary>
    private static MicChain CreateMicChain(WaveFormat micFormat, WaveFormat renderFormat)
    {
        var buffer = new BufferedWaveProvider(micFormat)
        {
            BufferDuration = MicBufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        var jitter = new MicJitterBuffer(buffer, MicPrime, MicTrimAbove, MicTrimTo);
        var samples = SampleChain.AdaptTo(jitter.ToSampleProvider(), renderFormat.SampleRate, renderFormat.Channels);
        return new MicChain(buffer, samples);
    }

    /// <summary>Hooks a capture to <paramref name="buffer"/> and makes it the session's microphone. Caller holds the lock.</summary>
    private void AttachMic(Session session, MMDevice device, WasapiCapture capture, BufferedWaveProvider buffer)
    {
        var mic = new MicInput(device, capture);
        mic.DataHandler = (_, e) =>
        {
            try
            {
                if (!mic.Released)
                {
                    buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            }
            catch (Exception)
            {
                // A malformed packet is dropped; the jitter buffer pads the gap with silence.
            }
        };
        mic.StoppedHandler = (_, e) => OnSessionStopped(session, e.Exception, "microphone", mic);
        capture.DataAvailable += mic.DataHandler;
        capture.RecordingStopped += mic.StoppedHandler;
        session.Mic = mic;
    }

    /// <summary>
    /// Reopens the configured microphone on a running, muted session. Caller holds the lock. On failure the
    /// reason goes to <see cref="EngineError"/>, nothing stays open, and false is returned (still muted).
    /// </summary>
    private bool TryOpenMicLocked(Session session)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        bool attached = false;
        try
        {
            using (var enumerator = CreateEnumerator())
            {
                device = GetActiveDevice(enumerator, session.InputId, DataFlow.Capture, "microphone");
            }

            if (IsSameVirtualCable(device, session.OutputDevice))
            {
                throw new InvalidOperationException(FeedbackLoopMessage);
            }

            capture = CreateCapture(device, out var micFormat);

            var buffer = session.MicBuffer;
            if (micFormat.Equals(buffer.WaveFormat))
            {
                buffer.ClearBuffer();
            }
            else
            {
                // A different capture format (always the case after a muted start, whose placeholder is at the
                // output format): rebuild only the microphone branch for it.
                var chain = CreateMicChain(micFormat, session.Routing.WaveFormat);
                session.Routing.ReplaceMic(chain.Samples);
                session.MicBuffer = chain.Buffer;
                buffer = chain.Buffer;
            }

            AttachMic(session, device, capture, buffer);
            attached = true;
            capture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            if (attached)
            {
                ReleaseMic(session);
            }
            else
            {
                SafeDispose(capture);
                SafeDispose(device);
            }

            ReportAsync($"Could not unmute the microphone ({Describe(ex).TrimEnd('.')}). It stays muted; the soundboard keeps playing.");
            return false;
        }
    }

    /// <summary>
    /// Mutes: unhooks, stops and disposes the capture and its device so Windows sees no open microphone
    /// stream, then clears the jitter buffer so no stale audio remains. Caller holds the lock (or is tearing down).
    /// </summary>
    private static void ReleaseMic(Session session)
    {
        var mic = session.Mic;
        if (mic is null)
        {
            return;
        }

        session.Mic = null;
        mic.Released = true;

        // Unhook first: this teardown is deliberate and must never be reported as a device failure.
        if (mic.DataHandler is { } onData)
        {
            mic.Capture.DataAvailable -= onData;
        }

        if (mic.StoppedHandler is { } onStopped)
        {
            mic.Capture.RecordingStopped -= onStopped;
        }

        SafeRun(mic.Capture.StopRecording);
        SafeDispose(mic.Capture); // joins the capture thread: no packet can arrive after this
        SafeDispose(mic.Device);
        SafeRun(session.MicBuffer.ClearBuffer);
    }

    private void OpenMonitorLocked(Session session, string deviceId)
    {
        if (string.Equals(deviceId, session.OutputId, StringComparison.OrdinalIgnoreCase))
        {
            ReportAsync("The local monitor can't be the virtual cable output. Choose your headphones or speakers.");
            return;
        }

        MMDevice? device = null;
        WasapiOut? render = null;
        try
        {
            using (var enumerator = CreateEnumerator())
            {
                device = GetActiveDevice(enumerator, deviceId, DataFlow.Render, "monitor (headphones)");
            }

            var format = GetRenderFormat(device);
            var mixer = CreateBusMixer();
            var eq = CreateEq(forMonitor: true);
            var source = SampleChain.AdaptTo(new FxSampleProvider(mixer, eq), format.SampleRate, format.Channels);
            var provider = new FloatWaveProvider(
                source,
                format,
                ex => ReportAsync($"Local monitoring failed: {Describe(ex)}"));

            var openedDevice = device;
            render = WithoutSyncContext(() => new WasapiOut(openedDevice, AudioClientShareMode.Shared, true, LatencyMs));
            render.Init(provider);

            var monitor = new MonitorOutput(deviceId, device, render, mixer, eq);
            render.PlaybackStopped += (_, e) => OnMonitorStopped(session, monitor, e.Exception);
            render.Play();
            session.Monitor = monitor;
        }
        catch (Exception ex)
        {
            SafeDispose(render);
            SafeDispose(device);
            ReportAsync($"Could not start local monitoring: {Describe(ex)}");
        }
    }

    // ------------------------------------------------------------------ teardown

    /// <summary>Stops the current session. Caller holds <see cref="_sync"/>. Returns true if one was running.</summary>
    private bool StopLocked()
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        _session = null;
        _statusTimer?.Dispose();
        _statusTimer = null;
        TearDown(session);
        return true;
    }

    private static void TearDown(Session session)
    {
        session.Stopping = true;
        StopAllLocked(session);
        CloseMonitorLocked(session);

        SafeRun(session.Render.Stop);
        SafeDispose(session.Render);
        ReleaseMic(session);
        SafeRun(session.MicBuffer.ClearBuffer);
        SafeDispose(session.OutputDevice);
    }

    private static void StopAllLocked(Session session)
    {
        foreach (var list in session.Instances.Values)
        {
            foreach (var instance in Snapshot(list))
            {
                instance.EndAll();
            }
        }

        SafeRun(session.MainMixer.RemoveAllMixerInputs);
        if (session.Monitor is { } monitor)
        {
            SafeRun(monitor.Mixer.RemoveAllMixerInputs);
        }
    }

    private static void CloseMonitorLocked(Session session)
    {
        var monitor = session.Monitor;
        if (monitor is null)
        {
            return;
        }

        session.Monitor = null;
        monitor.Stopping = true;

        foreach (var list in session.Instances.Values)
        {
            foreach (var instance in Snapshot(list))
            {
                instance.EndMonitor(monitor.Mixer);
            }
        }

        SafeRun(monitor.Mixer.RemoveAllMixerInputs);
        SafeRun(monitor.Render.Stop);
        SafeDispose(monitor.Render);
        SafeDispose(monitor.Device);
    }

    /// <param name="mic">For a capture stop: that capture. A capture released by mute, or since replaced, is ignored.</param>
    private void OnSessionStopped(Session session, Exception? exception, string what, MicInput? mic = null)
    {
        try
        {
            if (session.Stopping || mic is { Released: true })
            {
                return;
            }

            string reason = exception is null ? "it stopped unexpectedly" : Describe(exception);

            // Tear down on the thread pool: this handler runs on the audio thread, which teardown joins.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool stopped = false;
                    lock (_sync)
                    {
                        if (ReferenceEquals(_session, session) && (mic is null || ReferenceEquals(session.Mic, mic)))
                        {
                            stopped = StopLocked();
                        }
                    }

                    if (stopped)
                    {
                        RaiseError($"The {what} stopped ({reason}). Audio routing has been stopped.");
                        RaiseStatus();
                    }
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

    private void OnMonitorStopped(Session session, MonitorOutput monitor, Exception? exception)
    {
        try
        {
            if (monitor.Stopping)
            {
                return;
            }

            string reason = exception is null ? "it stopped unexpectedly" : Describe(exception);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool closed = false;
                    lock (_sync)
                    {
                        if (ReferenceEquals(_session, session) && ReferenceEquals(session.Monitor, monitor))
                        {
                            CloseMonitorLocked(session);
                            closed = true;
                        }
                    }

                    if (closed)
                    {
                        RaiseError($"Local monitoring stopped ({reason}). The virtual microphone is still running.");
                    }
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

    private void OnStatusTick(object? state)
    {
        if (Interlocked.Exchange(ref _statusBusy, 1) == 1)
        {
            return;
        }

        try
        {
            RaiseStatus();
        }
        finally
        {
            Volatile.Write(ref _statusBusy, 0);
        }
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

        var session = _session;
        EngineStatus status = session is null
            ? new EngineStatus { Running = false, MicMuted = _micMuted }
            : new EngineStatus
            {
                Running = true,
                InputDeviceId = session.InputId,
                OutputDeviceId = session.OutputId,
                AnySoundPlaying = Volatile.Read(ref session.Counters.Instances) > 0,
                PeakLevel = Math.Clamp(session.Routing.TakePeak(), 0f, 1f),
                MicMuted = _micMuted,
            };

        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception)
        {
            // A subscriber failure must not stop status updates.
        }
    }

    private void ReportAsync(string message) => ThreadPool.QueueUserWorkItem(_ => RaiseError(message));

    private void RaiseError(string message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            EngineError?.Invoke(this, message);
        }
        catch (Exception)
        {
            // A subscriber failure must not propagate into engine threads.
        }
    }

    // ------------------------------------------------------------------ helpers

    private FxChain CreateEq(bool forMonitor = false)
    {
        var eq = new FxChain(BusFormat.SampleRate, BusFormat.Channels);
        ApplyEq(eq, forMonitor);
        return eq;
    }

    private void ApplyEq(FxChain eq, bool forMonitor = false)
    {
        eq.SetState(_fx);
        eq.Bypassed = forMonitor && _monitorEqBypass;
    }

    private static MixingSampleProvider CreateBusMixer()
    {
        var mixer = new MixingSampleProvider(BusFormat) { ReadFully = true };
        mixer.MixerInputEnded += (_, e) =>
        {
            try
            {
                (e.SampleProvider as EndTrackingSampleProvider)?.End();
            }
            catch (Exception)
            {
                // Runs inside the mixer's read on the audio thread; it must never throw.
            }
        };
        return mixer;
    }

    /// <summary>WAV bytes → independent in-memory reader → float → 48 kHz stereo → slot volume → end tracking.</summary>
    private static EndTrackingSampleProvider CreateReader(byte[] wavData, float volume, Action onEnded)
    {
        WaveFileReader reader;
        try
        {
            reader = new WaveFileReader(new MemoryStream(wavData, writable: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The sound data is not a valid WAV file.", ex);
        }

        try
        {
            var plain = SampleChain.ToPlainEquivalent(reader.WaveFormat)
                ?? throw new InvalidDataException($"Unsupported WAV encoding: {reader.WaveFormat.Encoding}.");
            IWaveProvider wave = ReferenceEquals(plain, reader.WaveFormat) ? reader : new RelabelWaveProvider(reader, plain);
            var samples = SampleChain.AdaptTo(wave.ToSampleProvider(), BusFormat.SampleRate, BusFormat.Channels);
            var withVolume = new VolumeSampleProvider(samples) { Volume = volume };
            return new EndTrackingSampleProvider(withVolume, reader, onEnded);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The device's shared-mode mix format when it is float32 (always, in practice), else float32 at its rate/channels.</summary>
    private static WaveFormat GetRenderFormat(MMDevice device)
    {
        using var client = device.AudioClient;
        var mix = client.MixFormat;
        return SampleChain.IsFloat32(mix) ? mix : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
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

    private static MMDevice GetActiveDevice(MMDeviceEnumerator enumerator, string deviceId, DataFlow flow, string role)
    {
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new InvalidOperationException($"The selected {role} device was not found. It may have been unplugged or removed.", ex);
        }

        try
        {
            if (device.DataFlow != flow)
            {
                throw new InvalidOperationException(
                    $"The selected {role} device is not a {(flow == DataFlow.Capture ? "recording" : "playback")} device.");
            }

            var state = device.State;
            if (state != DeviceState.Active)
            {
                throw new InvalidOperationException(
                    $"The selected {role} device \"{SafeName(device)}\" is {DeviceManager.DescribeState(state)}. Only active devices can be used.");
            }

            return device;
        }
        catch (COMException ex)
        {
            device.Dispose();
            throw new InvalidOperationException($"The selected {role} device is no longer available.", ex);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>True when the microphone is "CABLE Output" of the very cable whose "CABLE Input" is the output.</summary>
    private static bool IsSameVirtualCable(MMDevice input, MMDevice output)
    {
        try
        {
            string inName = input.FriendlyName;
            string outName = output.FriendlyName;
            // Rename-proof for the VB-Audio Virtual Cable ("… (VB-Audio Virtual Cable)" survives a rename in Windows).
            bool inIsCable = AudioController.Core.Devices.VirtualDeviceDetector.IsVirtualMicOutput(inName)
                             || (inName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && inName.Contains("Output", StringComparison.OrdinalIgnoreCase));
            bool outIsCable = AudioController.Core.Devices.VirtualDeviceDetector.IsVirtualMicInput(outName)
                              || (outName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && outName.Contains("Input", StringComparison.OrdinalIgnoreCase));
            return inIsCable && outIsCable
                && string.Equals(input.DeviceFriendlyName, output.DeviceFriendlyName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException)
        {
            return false;
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

    private static string Describe(Exception ex)
    {
        int hr = ex.HResult;
        if (ex is UnauthorizedAccessException || hr == Interop.HResults.EAccessDenied)
        {
            return "Windows blocked access to the device; check Settings > Privacy & security > Microphone";
        }

        bool isAudioClientError = (unchecked((uint)hr) & 0xFFFF0000u) == 0x88890000u;
        if (ex is COMException || isAudioClientError)
        {
            return DeviceManager.DescribeHResult(hr);
        }

        return ex.Message;
    }

    /// <summary>
    /// Creates NAudio objects without the caller's SynchronizationContext so their stop events fire on the
    /// audio threads (handled without blocking) rather than queuing behind a possibly busy UI thread.
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

    private static SoundInstance[] Snapshot(List<SoundInstance> list)
    {
        lock (list)
        {
            return list.ToArray();
        }
    }

    private static void SafeRun(Action action)
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

    private static void SafeDispose(IDisposable? disposable)
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

    /// <summary>Everything belonging to one Start → Stop run.</summary>
    private sealed class Session(
        string inputId,
        string outputId,
        MMDevice outputDevice,
        WasapiOut render,
        BufferedWaveProvider micBuffer,
        MixingSampleProvider mainMixer,
        FxChain mainEq,
        RoutingWaveProvider routing,
        PlaybackCounters counters)
    {
        public string InputId { get; } = inputId;
        public string OutputId { get; } = outputId;
        public MMDevice OutputDevice { get; } = outputDevice;
        public WasapiOut Render { get; } = render;

        /// <summary>The jitter buffer the current mic chain reads. Replaced (under the engine lock) only on unmute.</summary>
        public BufferedWaveProvider MicBuffer { get; set; } = micBuffer;

        /// <summary>The open microphone, or null while muted. Mutated only under the engine lock.</summary>
        public MicInput? Mic { get; set; }

        public MixingSampleProvider MainMixer { get; } = mainMixer;
        public FxChain MainEq { get; } = mainEq;
        public RoutingWaveProvider Routing { get; } = routing;
        public PlaybackCounters Counters { get; } = counters;

        /// <summary>Playing instances per slot id. Lists are guarded by locking the list itself.</summary>
        public ConcurrentDictionary<Guid, List<SoundInstance>> Instances { get; } = new();

        /// <summary>Current local monitor, or null. Mutated only under the engine lock.</summary>
        public volatile MonitorOutput? Monitor;

        /// <summary>Set before teardown so expected stop events are not reported as errors.</summary>
        public volatile bool Stopping;
    }

    /// <summary>One open microphone capture. Released (and never reused) on mute or teardown.</summary>
    private sealed class MicInput(MMDevice device, WasapiCapture capture)
    {
        public MMDevice Device { get; } = device;
        public WasapiCapture Capture { get; } = capture;
        public EventHandler<WaveInEventArgs>? DataHandler { get; set; }
        public EventHandler<StoppedEventArgs>? StoppedHandler { get; set; }

        /// <summary>Set before the capture is torn down so late packets and stop events are ignored.</summary>
        public volatile bool Released;
    }

    /// <summary>A mic jitter buffer and the sample chain reading it at the output format.</summary>
    private sealed record MicChain(BufferedWaveProvider Buffer, ISampleProvider Samples);

    /// <summary>The optional "hear soundboard locally" output.</summary>
    private sealed class MonitorOutput(string deviceId, MMDevice device, WasapiOut render, MixingSampleProvider mixer, FxChain eq)
    {
        public string DeviceId { get; } = deviceId;
        public MMDevice Device { get; } = device;
        public WasapiOut Render { get; } = render;
        public MixingSampleProvider Mixer { get; } = mixer;
        public FxChain Eq { get; } = eq;
        public volatile bool Stopping;
    }

    /// <summary>
    /// One press of a slot: a main-bus reader and, when monitoring, a monitor-bus reader over the same
    /// bytes. Counters and the per-slot list are updated exactly once per reader as each one ends.
    /// </summary>
    private sealed class SoundInstance(PlaybackCounters counters, List<SoundInstance> list)
    {
        private EndTrackingSampleProvider? _main;
        private EndTrackingSampleProvider? _monitor;
        private MixingSampleProvider? _monitorMixer;
        private int _pendingReaders;
        private int _mainPending;

        /// <summary>Registers the instance as playing. Must be called before the readers join any mixer.</summary>
        public void Activate(EndTrackingSampleProvider main, EndTrackingSampleProvider? monitor, MixingSampleProvider? monitorMixer)
        {
            _main = main;
            _monitor = monitor;
            _monitorMixer = monitorMixer;
            _pendingReaders = monitor is null ? 1 : 2;
            _mainPending = 1;

            lock (list)
            {
                list.Add(this);
            }

            Interlocked.Increment(ref counters.Instances);
            Interlocked.Increment(ref counters.MainActive);
        }

        public void OnMainEnded()
        {
            if (Interlocked.Exchange(ref _mainPending, 0) == 1)
            {
                Interlocked.Decrement(ref counters.MainActive);
            }

            ReaderDone();
        }

        public void OnMonitorEnded() => ReaderDone();

        /// <summary>Stops both readers and removes them from their mixers (StopSound).</summary>
        public void Stop(MixingSampleProvider mainMixer)
        {
            EndAll();
            if (_main is { } main)
            {
                mainMixer.RemoveMixerInput(main);
            }

            if (_monitor is { } monitor && _monitorMixer is { } monitorMixer)
            {
                monitorMixer.RemoveMixerInput(monitor);
            }
        }

        /// <summary>Ends both readers; mixers drop ended readers on their next read.</summary>
        public void EndAll()
        {
            _main?.End();
            _monitor?.End();
        }

        /// <summary>Ends only the monitor reader when it belongs to <paramref name="mixer"/>.</summary>
        public void EndMonitor(MixingSampleProvider mixer)
        {
            if (ReferenceEquals(_monitorMixer, mixer))
            {
                _monitor?.End();
            }
        }

        private void ReaderDone()
        {
            if (Interlocked.Decrement(ref _pendingReaders) != 0)
            {
                return;
            }

            lock (list)
            {
                list.Remove(this);
            }

            Interlocked.Decrement(ref counters.Instances);
        }
    }
}
