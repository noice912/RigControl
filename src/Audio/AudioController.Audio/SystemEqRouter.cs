using System.Runtime.InteropServices;
using AudioController.Audio.Routing;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioController.Audio;

/// <summary>
/// System EQ: equalizes everything Windows plays. The user makes a second virtual playback device (for
/// example VB-Audio CABLE A or Voicemod's virtual device) the Windows default output, so all system audio
/// lands there; this router loopback-captures that device, runs the audio through its own
/// <see cref="FxChain"/> (the same EQ + effects as the soundboard bus) and renders it to the real headphones or speakers.
/// </summary>
/// <remarks>
/// <para>Chain: WASAPI loopback capture of the source → 500 ms buffer with a small jitter cushion → only the
/// format conversion needed (resampled at most once, to the output rate, stereo) → the Effects chain (EQ + effects, same as the soundboard bus) → channel
/// adaptation to the output → hard clamp to ±1.0 (the final stage) → shared-mode, event-driven
/// <see cref="WasapiOut"/> on the output device.</para>
/// <para>Completely independent of <see cref="AudioEngine"/>: it never touches the microphone, the virtual
/// cable or the soundboard bus, and keeps running whether the virtual mic is running, stopped or muted.
/// It refuses the VB-Audio "CABLE Input" endpoint as either end, because that is the virtual-mic path.</para>
/// <para>A loopback capture only delivers data while something is playing to the source; the buffer pads
/// silence in between. <see cref="StatusChanged"/> and <see cref="RouterError"/> are raised on thread-pool
/// threads.</para>
/// </remarks>
public sealed class SystemEqRouter : IDisposable
{
    /// <summary>Shown when the source and the output are the same endpoint.</summary>
    public const string SameDeviceMessage =
        "The System EQ source and output are the same device, so its audio would loop straight back into itself. " +
        "Keep the virtual device as the source and choose your headphones or speakers as the output.";

    /// <summary>Shown when the source is the virtual cable's "CABLE Input" (or its "CABLE In 16ch").</summary>
    public const string CableSourceMessage =
        "The virtual cable's \"CABLE Input\" can't be the System EQ source: it is the virtual-microphone path, so " +
        "system audio sent there would be heard by Discord (and every app using \"CABLE Output\" as its mic). Use a " +
        "second virtual device instead, such as VB-Audio CABLE A or Voicemod's virtual device.";

    /// <summary>Shown when the output is the virtual cable's "CABLE Input" (or its "CABLE In 16ch").</summary>
    public const string CableOutputMessage =
        "The virtual cable's \"CABLE Input\" can't be the System EQ output: it is the virtual-microphone path, so " +
        "all your system audio would be heard by Discord. Choose your headphones or speakers.";

    /// <summary>Shown when no source is chosen.</summary>
    public const string NoSourceMessage =
        "Choose a System EQ source: the virtual device you set as the Windows default output.";

    /// <summary>Shown when no output is chosen.</summary>
    public const string NoOutputMessage =
        "Choose a System EQ output: your headphones or speakers.";

    private const int LatencyMs = 20;
    private const int EventCaptureBufferMs = 20;
    private const int EqChannels = 2;

    private static readonly TimeSpan BufferDuration = TimeSpan.FromMilliseconds(500);

    // Event-driven loopback delivers ~10 ms packets; NAudio's polled WasapiLoopbackCapture (the fallback)
    // delivers ~50 ms bursts, so its cushion must outlast one burst interval plus timer jitter.
    private static readonly JitterProfile EventDrivenJitter = new(
        TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(50));

    private static readonly JitterProfile PolledJitter = new(
        TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(100));

    private readonly object _sync = new();
    private readonly float[] _eqGains = new float[TenBandEq.BandCount];
    private FxState _fx = FxState.Neutral;
    private bool _eqEnabled;
    private volatile Route? _route;
    private volatile bool _disposed;

    /// <summary>Creates a stopped router. Devices are chosen with <see cref="Start"/>.</summary>
    public SystemEqRouter()
    {
    }

    /// <summary>Raised after every start and stop, including a stop caused by a device failure.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised with a readable message when a device fails or disappears mid-stream.</summary>
    public event EventHandler<string>? RouterError;

    /// <summary>True while audio is being routed.</summary>
    public bool IsRunning => _route is not null;

    /// <summary>The playback endpoint being loopback-captured, or null when stopped.</summary>
    public string? SourceDeviceId => _route?.SourceId;

    /// <summary>The endpoint the equalized audio is rendered to, or null when stopped.</summary>
    public string? OutputDeviceId => _route?.OutputId;

    /// <summary>
    /// True when the running capture is the low-latency event-driven loopback; false when this endpoint
    /// needed the polled <see cref="WasapiLoopbackCapture"/> fallback (or when stopped).
    /// </summary>
    public bool LowLatencyCapture => _route?.EventDriven == true;

    /// <summary>The EQ enabled flag last set with <see cref="SetEq"/>.</summary>
    public bool EqEnabled
    {
        get
        {
            lock (_sync)
            {
                return _eqEnabled;
            }
        }
    }

    /// <summary>
    /// Returns null when <paramref name="source"/> → <paramref name="output"/> is a valid System EQ route,
    /// otherwise a readable reason. Needs no audio hardware: the caller describes the endpoints (null = not
    /// found). Both must be distinct, active playback endpoints, and neither may be "CABLE Input".
    /// </summary>
    public static string? CheckDevices(string? sourceDeviceId, string? outputDeviceId, AudioDeviceInfo? source, AudioDeviceInfo? output)
    {
        if (string.IsNullOrWhiteSpace(sourceDeviceId))
        {
            return NoSourceMessage;
        }

        if (string.IsNullOrWhiteSpace(outputDeviceId))
        {
            return NoOutputMessage;
        }

        if (string.Equals(sourceDeviceId, outputDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return SameDeviceMessage;
        }

        if (source is null)
        {
            return "The System EQ source device was not found. It may have been unplugged or removed.";
        }

        if (output is null)
        {
            return "The System EQ output device was not found. It may have been unplugged or removed.";
        }

        if (source.Kind != DeviceKind.Playback)
        {
            return $"The System EQ source \"{source.FriendlyName}\" is a recording device. Choose a playback device: System EQ captures what Windows plays to it.";
        }

        if (output.Kind != DeviceKind.Playback)
        {
            return $"The System EQ output \"{output.FriendlyName}\" is a recording device. Choose your headphones or speakers.";
        }

        if (source.State != DeviceActivity.Active)
        {
            return $"The System EQ source \"{source.FriendlyName}\" is {DescribeState(source.State)}. Only active devices can be used.";
        }

        if (output.State != DeviceActivity.Active)
        {
            return $"The System EQ output \"{output.FriendlyName}\" is {DescribeState(output.State)}. Only active devices can be used.";
        }

        if (IsVirtualMicInput(source.FriendlyName))
        {
            return CableSourceMessage;
        }

        if (IsVirtualMicInput(output.FriendlyName))
        {
            return CableOutputMessage;
        }

        return null;
    }

    /// <summary>
    /// True for the VB-Audio Virtual Cable playback endpoints that feed "CABLE Output" (the virtual
    /// microphone): "CABLE Input" and the cable's 16-channel "CABLE In 16ch", also after the user renamed them
    /// (recognised by the "(VB-Audio Virtual Cable)" suffix Windows keeps). VB-Audio CABLE A/B are separate
    /// cables ("CABLE-A Input") and are allowed.
    /// </summary>
    public static bool IsVirtualMicInput(string? friendlyName) =>
        AudioController.Core.Devices.VirtualDeviceDetector.IsVirtualMicInput(friendlyName);

    /// <summary>
    /// Starts (or restarts) routing <paramref name="sourceDeviceId"/>'s loopback through the EQ to
    /// <paramref name="outputDeviceId"/>. Both are playback endpoint ids.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The devices are the same, missing, inactive, "CABLE Input", or could not be opened. The message is readable.
    /// </exception>
    public void Start(string sourceDeviceId, string outputDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDeviceId);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();

            Route route;
            try
            {
                route = OpenRoute(sourceDeviceId, outputDeviceId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not start System EQ: {Describe(ex)}", ex);
            }

            _route = route;
        }

        QueueStatus();
    }

    /// <summary>Stops routing and releases both devices. Idempotent.</summary>
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

    /// <summary>
    /// Sets the EQ (gains in dB, clamped to −24..+24, NaN = 0; enabled flag). May be called at any time;
    /// while running it applies immediately. Mirrors <see cref="AudioEngine.SetEq"/>.
    /// </summary>
    public void SetEq(float[] gainsDb, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            for (int i = 0; i < _eqGains.Length; i++)
            {
                float g = i < gainsDb.Length ? gainsDb[i] : 0f;
                _eqGains[i] = float.IsNaN(g) ? 0f : Math.Clamp(g, TenBandEq.MinGainDb, TenBandEq.MaxGainDb);
            }

            _eqEnabled = enabled;
            _fx = FxState.FromGains(FxSettings.DefaultTenBandFrequenciesHz, _eqGains, enabled);

            if (_route is { } route)
            {
                ApplyEq(route.Eq);
            }
        }
    }

    /// <summary>
    /// Sets the whole Effects state (graphic EQ + effects; mirrors <see cref="AudioEngine.SetFx"/>). May be called at
    /// any time; while running it applies immediately. <see cref="GetEqGains"/> / <see cref="EqEnabled"/> then report
    /// its 10 band gains and EQ switch.
    /// </summary>
    public void SetFx(FxState fx)
    {
        ArgumentNullException.ThrowIfNull(fx);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _fx = fx;
            var gains = fx.LegacyGainsDb;
            Array.Copy(gains, _eqGains, Math.Min(gains.Length, _eqGains.Length));
            _eqEnabled = fx.LegacyEqEnabled;

            if (_route is { } route)
            {
                ApplyEq(route.Eq);
            }
        }
    }

    /// <summary>Returns a copy of the gains last set with <see cref="SetEq"/>.</summary>
    public float[] GetEqGains()
    {
        lock (_sync)
        {
            return (float[])_eqGains.Clone();
        }
    }

    /// <summary>Stops routing and releases everything. Idempotent; no events are raised afterwards.</summary>
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

    // ------------------------------------------------------------------ route construction

    /// <summary>Opens and validates both endpoints, then starts the route. Caller holds the lock.</summary>
    private Route OpenRoute(string sourceId, string outputId)
    {
        // Checked before touching any device, so this guard never depends on audio hardware.
        if (string.Equals(sourceId, outputId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(SameDeviceMessage);
        }

        MMDevice? source = null;
        MMDevice? output = null;
        try
        {
            using (var enumerator = CreateEnumerator())
            {
                source = TryGetDevice(enumerator, sourceId);
                output = TryGetDevice(enumerator, outputId);
            }

            string? problem = CheckDevices(sourceId, outputId, DescribeDevice(source), DescribeDevice(output));
            if (problem is not null)
            {
                throw new InvalidOperationException(problem);
            }

            try
            {
                return StartRoute(sourceId, outputId, source!, output!, eventDriven: true);
            }
            catch (COMException)
            {
                // This endpoint refused event-driven loopback; NAudio's polled loopback capture still works
                // (with more delay). A failure that is not about the capture repeats here and is reported.
                return StartRoute(sourceId, outputId, source!, output!, eventDriven: false);
            }
        }
        catch
        {
            SafeDispose(source);
            SafeDispose(output);
            throw;
        }
    }

    /// <summary>
    /// Builds and starts capture → EQ → clamp → render. On failure it releases what it created (not the
    /// devices, which the caller owns until a route is returned) and rethrows.
    /// </summary>
    private Route StartRoute(string sourceId, string outputId, MMDevice source, MMDevice output, bool eventDriven)
    {
        WaveFormat renderFormat = GetRenderFormat(output);
        int outRate = renderFormat.SampleRate;

        WasapiCapture? capture = null;
        WasapiOut? render = null;
        Route? route = null;
        try
        {
            capture = WithoutSyncContext<WasapiCapture>(() => eventDriven
                ? new EventLoopbackCapture(source, EventCaptureBufferMs)
                : new WasapiLoopbackCapture(source));

            WaveFormat sourceFormat = SampleChain.ToPlainEquivalent(capture.WaveFormat)
                ?? throw new InvalidOperationException(
                    $"The source device uses an audio format System EQ can't read ({capture.WaveFormat.Encoding}).");

            var profile = eventDriven ? EventDrivenJitter : PolledJitter;
            var buffer = new BufferedWaveProvider(sourceFormat)
            {
                BufferDuration = BufferDuration,
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            var jitter = new MicJitterBuffer(buffer, profile.Prime, profile.TrimAbove, profile.TrimTo);

            // The EQ runs at the output rate so the audio is resampled at most once.
            var eq = new FxChain(outRate, EqChannels);
            ApplyEq(eq);

            ISampleProvider chain = SampleChain.AdaptTo(jitter.ToSampleProvider(), outRate, EqChannels);
            chain = new FxSampleProvider(chain, eq);
            chain = SampleChain.AdaptTo(chain, outRate, renderFormat.Channels);
            chain = new ClampSampleProvider(chain); // final stage: an EQ boost may exceed full scale

            var provider = new FloatWaveProvider(
                chain,
                renderFormat,
                ex => ReportAsync($"System EQ audio processing failed, so its output is silent: {Describe(ex)}"));

            render = WithoutSyncContext(() => new WasapiOut(output, AudioClientShareMode.Shared, true, LatencyMs));
            render.Init(provider);

            route = new Route(sourceId, outputId, SafeName(source), SafeName(output), source, output, capture, render, buffer, eq, eventDriven);
            var current = route;
            capture.DataAvailable += (_, e) =>
            {
                try
                {
                    if (!current.Stopping)
                    {
                        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    }
                }
                catch (Exception)
                {
                    // A malformed packet is dropped; the buffer pads the gap with silence.
                }
            };
            capture.RecordingStopped += (_, e) => OnRouteStopped(current, e.Exception, isSource: true);
            render.PlaybackStopped += (_, e) => OnRouteStopped(current, e.Exception, isSource: false);

            capture.StartRecording();
            render.Play();
            return route;
        }
        catch
        {
            if (route is not null)
            {
                route.Stopping = true;
            }

            if (capture is not null)
            {
                SafeRun(capture.StopRecording);
            }

            SafeDispose(render);
            SafeDispose(capture);
            throw;
        }
    }

    // ------------------------------------------------------------------ teardown

    /// <summary>Stops the current route. Caller holds <see cref="_sync"/>. Returns true if one was running.</summary>
    private bool StopLocked()
    {
        var route = _route;
        if (route is null)
        {
            return false;
        }

        _route = null;
        TearDown(route);
        return true;
    }

    private static void TearDown(Route route)
    {
        route.Stopping = true;
        SafeRun(route.Capture.StopRecording);
        SafeDispose(route.Capture); // joins the capture thread: no packet can arrive after this
        SafeRun(route.Render.Stop);
        SafeDispose(route.Render);
        SafeRun(route.Buffer.ClearBuffer);
        SafeDispose(route.SourceDevice);
        SafeDispose(route.OutputDevice);
    }

    private void OnRouteStopped(Route route, Exception? exception, bool isSource)
    {
        try
        {
            if (route.Stopping)
            {
                return;
            }

            string reason = exception is null ? "it stopped unexpectedly" : Describe(exception);
            string message = isSource
                ? $"System EQ stopped: the source device \"{route.SourceName}\" stopped ({reason}). " +
                  "Reconnect it, or choose another default playback device in Windows."
                : $"System EQ stopped: the output device \"{route.OutputName}\" stopped ({reason}). " +
                  $"Windows is still sending audio to \"{route.SourceName}\", so reconnect the output or choose another default playback device.";

            // Tear down on the thread pool: this handler runs on the audio thread, which teardown joins.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool stopped = false;
                    lock (_sync)
                    {
                        if (ReferenceEquals(_route, route))
                        {
                            stopped = StopLocked();
                        }
                    }

                    if (stopped)
                    {
                        RaiseError(message);
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

    // ------------------------------------------------------------------ status and errors

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

    private void RaiseError(string message)
    {
        if (_disposed)
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

    private void ApplyEq(FxChain eq)
    {
        eq.SetState(_fx);
        eq.Bypassed = false;
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

    /// <summary>The endpoint, or null when Windows does not know the id.</summary>
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

    /// <summary>What <see cref="CheckDevices"/> needs to know about an opened endpoint (null when unreadable).</summary>
    private static AudioDeviceInfo? DescribeDevice(MMDevice? device)
    {
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

    private static string DescribeState(DeviceActivity state) => state switch
    {
        DeviceActivity.Active => "active",
        DeviceActivity.Disabled => "disabled",
        DeviceActivity.Unplugged => "unplugged",
        _ => "not present",
    };

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
            return "Windows blocked access to the device";
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

    /// <summary>Jitter cushion for one capture style (see <see cref="MicJitterBuffer"/>).</summary>
    private sealed record JitterProfile(TimeSpan Prime, TimeSpan TrimAbove, TimeSpan TrimTo);

    /// <summary>Everything belonging to one Start → Stop run.</summary>
    private sealed class Route(
        string sourceId,
        string outputId,
        string sourceName,
        string outputName,
        MMDevice sourceDevice,
        MMDevice outputDevice,
        WasapiCapture capture,
        WasapiOut render,
        BufferedWaveProvider buffer,
        FxChain eq,
        bool eventDriven)
    {
        public string SourceId { get; } = sourceId;
        public string OutputId { get; } = outputId;
        public string SourceName { get; } = sourceName;
        public string OutputName { get; } = outputName;
        public MMDevice SourceDevice { get; } = sourceDevice;
        public MMDevice OutputDevice { get; } = outputDevice;
        public WasapiCapture Capture { get; } = capture;
        public WasapiOut Render { get; } = render;
        public BufferedWaveProvider Buffer { get; } = buffer;
        public FxChain Eq { get; } = eq;
        public bool EventDriven { get; } = eventDriven;

        /// <summary>Set before teardown so expected stop events and late packets are ignored.</summary>
        public volatile bool Stopping;
    }

    /// <summary>
    /// Event-driven WASAPI loopback capture with a short buffer. NAudio's <see cref="WasapiLoopbackCapture"/> is
    /// the same thing (a <see cref="WasapiCapture"/> with the loopback stream flag) but fixed to a polled 100 ms
    /// buffer, which arrives in ~50 ms bursts and adds noticeable delay. Event-driven loopback is supported from
    /// Windows 10 on; when an endpoint refuses it the router falls back to <see cref="WasapiLoopbackCapture"/>.
    /// </summary>
    private sealed class EventLoopbackCapture(MMDevice device, int bufferMilliseconds)
        : WasapiCapture(device, true, bufferMilliseconds)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
