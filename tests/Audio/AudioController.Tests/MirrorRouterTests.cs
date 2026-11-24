using System.Runtime.InteropServices;
using AudioController.Audio;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using NAudio.Wave;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// <see cref="MirrorRouter"/> ("Also play on") lifecycle and signal flow, driven through fake endpoints (no hardware):
/// one loopback capture of the headset fans out to any number of outputs; delay and volume change live; outputs that
/// are the headset or the virtual-mic cable are never opened; an output or the headset failing mid-stream stops only
/// that path, raises <see cref="MirrorRouter.RouterError"/> and comes back by itself; Stop / Dispose are idempotent.
/// The real-device check is the live harness.
/// </summary>
public sealed class MirrorRouterTests
{
    private const string Headset = "{0.0.0.00000000}.{07abda82-1125-4339-aca5-dca468fc985a}";
    private const string Digital = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}";
    private const string Speakers = "{0.0.0.00000000}.{33333333-0000-0000-0000-000000000003}";
    private const string CableIn = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000004}";
    private const string Cable16 = "{0.0.0.00000000}.{55555555-0000-0000-0000-000000000005}";
    private const string CableRenamed = "{0.0.0.00000000}.{66666666-0000-0000-0000-000000000006}";
    private const string Missing = "{0.0.0.00000000}.{00000000-0000-0000-0000-00000000dead}";

    private const int Block = 480; // 10 ms at 48 kHz

    private static FakeMirrorEndpoints Machine()
    {
        var e = new FakeMirrorEndpoints();
        e.Add(Headset, "Headphones (Crusher ANC 2)");
        e.Add(Digital, "Digital Output (Realtek USB2.0 Audio)");
        e.Add(Speakers, "Speakers (Realtek(R) Audio)");
        e.Add(CableIn, "CABLE Input (VB-Audio Virtual Cable)");
        e.Add(Cable16, "CABLE In 16ch (VB-Audio Virtual Cable)");
        e.Add(CableRenamed, "Audio Controller (internal) (VB-Audio Virtual Cable)");
        return e;
    }

    private static MirrorRoute Route(string id, int delay = 0, int volume = 100) => new(id, id, delay, volume);

    private static MirrorRouter Router(FakeMirrorEndpoints endpoints, int retryMs = 60_000) =>
        new(endpoints, _ => TimeSpan.FromMilliseconds(retryMs));

    private static float[] Tone(int frames, int offset = 0)
    {
        var data = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * (f + offset) / 48000.0);
            data[2 * f] = v;
            data[2 * f + 1] = -v;
        }

        return data;
    }

    /// <summary>Pushes <paramref name="blocks"/> 10 ms packets into the headset capture and pulls the same from each render.</summary>
    private static Dictionary<string, List<float>> Run(FakeMirrorEndpoints e, int blocks, string[] targets, int prime = 3)
    {
        var outputs = targets.ToDictionary(t => t, _ => new List<float>(), StringComparer.OrdinalIgnoreCase);
        var capture = e.LiveCapture(Headset)!;
        int frame = 0;
        for (int i = 0; i < prime; i++)
        {
            capture.Push(Tone(Block, frame));
            frame += Block;
        }

        for (int i = 0; i < blocks; i++)
        {
            capture.Push(Tone(Block, frame));
            frame += Block;
            foreach (var t in targets)
            {
                outputs[t].AddRange(e.LiveRender(t)!.Pull(Block));
            }
        }

        return outputs;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    // ---------------------------------------------------------------- start / fan-out

    [Fact]
    public void Apply_CapturesTheHeadset_AndOpensOneRenderPerOutput()
    {
        var e = Machine();
        using var router = Router(e);

        var status = router.Apply(Headset, new[] { Route(Digital), Route(Speakers) });

        Assert.True(router.IsRunning);
        Assert.Equal(Headset, router.SourceDeviceId);
        Assert.Equal(new[] { Digital, Speakers }, router.ActiveTargetIds);
        Assert.Equal(Headset, Assert.Single(e.Captures).DeviceId);
        Assert.Equal(new[] { Digital, Speakers }, e.Renders.Select(r => r.DeviceId));
        Assert.True(status.Running);
        Assert.All(status.Targets, t => Assert.True(t.Running));
        Assert.Null(status.SourceError);
    }

    [Fact]
    public void Copy_AtFullVolumeAndNoDelay_IsTheHeadsetAudioBitExact_AfterTheShortFadeIn()
    {
        var e = Machine();
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital) });

        var output = Run(e, 50, new[] { Digital })[Digital].ToArray();
        var expected = Tone(Block * 53); // the first pull plays from the first captured frame

        int fade = 96 * 2;
        for (int i = fade; i < output.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(output[i]));
        }
    }

    [Fact]
    public void Copy_Volume50_HalvesIt_AndEachOutputHasItsOwnVolume()
    {
        var e = Machine();
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital, volume: 50), Route(Speakers, volume: 100) });

        var outputs = Run(e, 30, new[] { Digital, Speakers });

        var half = outputs[Digital];
        var full = outputs[Speakers];
        for (int i = 200; i < half.Count; i++)
        {
            Assert.Equal(full[i] / 2, half[i]);
        }
    }

    [Fact]
    public void Copy_Delay200ms_ArrivesExactly9600FramesLater_ThanTheUndelayedOutput()
    {
        var e = Machine();
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital, delay: 200), Route(Speakers, delay: 0) });

        var outputs = Run(e, 60, new[] { Digital, Speakers });

        var delayed = outputs[Digital];
        var direct = outputs[Speakers];
        Assert.All(delayed.Take(9600 * 2), s => Assert.Equal(0f, s));
        for (int i = 9600 * 2 + 400; i < delayed.Count; i++)
        {
            Assert.Equal(direct[i - 9600 * 2], delayed[i]);
        }
    }

    [Fact]
    public void Silence_WhenTheHeadsetDeliversNothing_TheOutputsKeepRunningOnSilence()
    {
        var e = Machine();
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital) });

        var render = e.LiveRender(Digital)!;
        for (int i = 0; i < 100; i++)
        {
            Assert.All(render.Pull(Block), s => Assert.Equal(0f, s));
        }

        Assert.True(router.IsRunning);
        Assert.Single(e.Renders);
    }

    // ---------------------------------------------------------------- live changes

    [Fact]
    public void Apply_DelayAndVolumeChange_Live_WithoutReopening()
    {
        var e = Machine();
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital, delay: 0, volume: 100) });

        var status = router.Apply(Headset, new[] { Route(Digital, delay: 180, volume: 40) });

        Assert.Single(e.Renders);
        Assert.Single(e.Captures);
        var t = Assert.Single(status.Targets);
        Assert.Equal((180, 40), (t.DelayMs, t.VolumePercent));
    }

    [Fact]
    public void Apply_AddAndRemoveOutputs_TouchesOnlyThoseOutputs()
    {
        var e = Machine();
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital) });

        router.Apply(Headset, new[] { Route(Digital), Route(Speakers) });
        Assert.Equal(2, e.Renders.Count);
        Assert.False(e.Renders[0].Disposed);

        router.Apply(Headset, new[] { Route(Speakers) });
        Assert.True(e.Renders[0].Disposed);
        Assert.False(e.Renders[1].Disposed);
        Assert.Equal(new[] { Speakers }, router.ActiveTargetIds);
        Assert.Single(e.Captures);

        router.Apply(Headset, Array.Empty<MirrorRoute>()); // nothing to play on: the capture is released too
        Assert.False(router.IsRunning);
        Assert.True(e.Captures[0].Disposed);
        Assert.All(e.Renders, r => Assert.True(r.Disposed));
    }

    [Fact]
    public void Apply_ANewHeadset_RestartsEverything()
    {
        var e = Machine();
        var other = "{0.0.0.00000000}.{abababab-0000-0000-0000-00000000abab}";
        e.Add(other, "Headphones (Other)");
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital) });

        router.Apply(other, new[] { Route(Digital) });

        Assert.Equal(2, e.Captures.Count);
        Assert.True(e.Captures[0].Disposed);
        Assert.Equal(other, router.SourceDeviceId);
        Assert.Equal(2, e.Renders.Count);
        Assert.True(e.Renders[0].Disposed);
    }

    // ---------------------------------------------------------------- guards: never the headset itself, never the cable

    [Theory]
    [InlineData(Headset, MirrorRouter.SelfTargetMessage)]
    [InlineData(CableIn, MirrorRouter.CableTargetMessage)]
    [InlineData(Cable16, MirrorRouter.CableTargetMessage)]
    [InlineData(CableRenamed, MirrorRouter.CableTargetMessage)]
    public void Apply_HeadsetOrCableAsOutput_IsRefused_NeverOpened(string target, string message)
    {
        var e = Machine();
        using var router = Router(e, retryMs: 10);

        var status = router.Apply(Headset, new[] { Route(target), Route(Digital) });

        Assert.Equal(new[] { Digital }, e.Renders.Select(r => r.DeviceId)); // only the valid output was opened
        var refused = status.Targets.Single(t => t.DeviceId == target);
        Assert.False(refused.Running);
        Assert.Equal(message, refused.Error);
        Thread.Sleep(100); // no retries for a refusal that can never become valid
        Assert.Single(e.Renders);
    }

    [Fact]
    public void CheckTarget_And_CheckSource_AreHardwareFree()
    {
        AudioDeviceInfo Dev(string id, string name, DeviceKind kind = DeviceKind.Playback, DeviceActivity state = DeviceActivity.Active) =>
            new() { Id = id, FriendlyName = name, Kind = kind, State = state };

        Assert.Null(MirrorRouter.CheckTarget(Headset, Dev(Digital, "Digital Output (Realtek USB2.0 Audio)")));
        Assert.Equal(MirrorRouter.SelfTargetMessage, MirrorRouter.CheckTarget(Headset, Dev(Headset.ToUpperInvariant(), "Headphones")));
        Assert.Equal(MirrorRouter.CableTargetMessage, MirrorRouter.CheckTarget(Headset, Dev(CableIn, "CABLE Input (VB-Audio Virtual Cable)")));
        Assert.Contains("recording device", MirrorRouter.CheckTarget(Headset, Dev(Digital, "Line In", DeviceKind.Capture)));
        Assert.Contains("isn't connected", MirrorRouter.CheckTarget(Headset, Dev(Digital, "Digital Output", state: DeviceActivity.Unplugged)));
        Assert.Contains("not found", MirrorRouter.CheckTarget(Headset, null));

        Assert.Null(MirrorRouter.CheckSource(Dev(Headset, "Headphones (Crusher ANC 2)")));
        Assert.Contains("not found", MirrorRouter.CheckSource(null));
        Assert.Contains("virtual mic feed", MirrorRouter.CheckSource(Dev(CableIn, "CABLE Input (VB-Audio Virtual Cable)")));
        Assert.Contains("isn't connected", MirrorRouter.CheckSource(Dev(Headset, "Headphones", state: DeviceActivity.Unplugged)));
    }

    // ---------------------------------------------------------------- failures and recovery

    [Fact]
    public void OutputUnpluggedMidStream_StopsOnlyThatOutput_RaisesAnError_AndComesBackByItself()
    {
        var e = Machine();
        using var router = Router(e, retryMs: 50);
        var errors = new List<string>();
        router.RouterError += (_, m) => { lock (errors) errors.Add(m); };
        router.Apply(Headset, new[] { Route(Digital), Route(Speakers) });
        var lost = e.LiveRender(Digital)!;

        e.SetActive(Digital, false); // unplugged: reopening fails until it is back
        lost.Fail(new COMException("gone", unchecked((int)0x88890004)));

        Assert.True(WaitUntil(() => { lock (errors) return errors.Count == 1; }));
        Assert.True(WaitUntil(() => lost.Disposed && !router.ActiveTargetIds.Contains(Digital)));
        Assert.Contains("Digital", errors[0]);
        Assert.Contains("disconnected", errors[0]);
        Assert.Contains(Speakers, router.ActiveTargetIds); // the other output keeps playing
        Assert.False(e.LiveRender(Speakers)!.Disposed);
        Assert.False(router.GetStatus().Targets.Single(t => t.DeviceId == Digital).Running);

        e.SetActive(Digital, true); // plugged back in → the back-off retry reopens it
        Assert.True(WaitUntil(() => router.ActiveTargetIds.Contains(Digital)));
        Assert.Equal(3, e.Renders.Count);
        Assert.Single(e.Captures);
        lock (errors) Assert.Single(errors);
    }

    [Fact]
    public void OutputFailure_ApplyWithRetryNow_ReopensImmediately_WithoutWaitingForTheBackOff()
    {
        var e = Machine();
        using var router = Router(e, retryMs: 60_000);
        router.Apply(Headset, new[] { Route(Digital) });
        e.LiveRender(Digital)!.Fail(new COMException("gone", unchecked((int)0x88890004)));
        Assert.True(WaitUntil(() => router.ActiveTargetIds.Count == 0));

        router.Apply(Headset, new[] { Route(Digital) }); // a slider move: no retry yet (back-off)
        Assert.Empty(router.ActiveTargetIds);

        router.Apply(Headset, new[] { Route(Digital) }, retryFailedNow: true); // a device scan: retry now
        Assert.Equal(new[] { Digital }, router.ActiveTargetIds);
    }

    [Fact]
    public void HeadsetLostMidStream_StopsEverything_RaisesAnError_AndRestartsWhenItIsBack()
    {
        var e = Machine();
        using var router = Router(e, retryMs: 50);
        string? error = null;
        router.RouterError += (_, m) => Volatile.Write(ref error, m);
        router.Apply(Headset, new[] { Route(Digital), Route(Speakers) });

        e.SetActive(Headset, false);
        e.LiveCapture(Headset)!.Fail(new COMException("gone", unchecked((int)0x88890004)));

        Assert.True(WaitUntil(() => !router.IsRunning));
        Assert.True(WaitUntil(() => Volatile.Read(ref error) is not null));
        Assert.Contains("headset", error);
        Assert.All(e.Renders, r => Assert.True(r.Disposed));
        Assert.NotNull(router.GetStatus().SourceError);

        e.SetActive(Headset, true);
        Assert.True(WaitUntil(() => router.IsRunning && router.ActiveTargetIds.Count == 2));
        Assert.Equal(2, e.Captures.Count);
    }

    [Fact]
    public void Apply_HeadsetMissing_ReportsIt_StaysStopped_NeverThrows()
    {
        var e = Machine();
        using var router = Router(e);

        var status = router.Apply(Missing, new[] { Route(Digital) });

        Assert.False(router.IsRunning);
        Assert.Empty(e.Captures);
        Assert.Empty(e.Renders);
        Assert.Contains("not found", status.SourceError);
    }

    [Fact]
    public void Apply_AnOutputThatCannotOpen_IsReported_TheOthersStillPlay()
    {
        var e = Machine();
        e.FailOpen.Add(Speakers);
        using var router = Router(e);

        var status = router.Apply(Headset, new[] { Route(Speakers), Route(Digital) });

        Assert.Equal(new[] { Digital }, router.ActiveTargetIds);
        Assert.Contains("in use", status.Targets.Single(t => t.DeviceId == Speakers).Error);
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public void StopAndDispose_AreIdempotent_ReleaseEverything_AndApplyAfterDisposeThrows()
    {
        var e = Machine();
        var router = Router(e);
        int events = 0;
        router.StatusChanged += (_, _) => Interlocked.Increment(ref events);
        router.Apply(Headset, new[] { Route(Digital), Route(Speakers) });

        router.Stop();
        router.Stop();
        Assert.False(router.IsRunning);
        Assert.Empty(router.ActiveTargetIds);
        Assert.All(e.Captures, c => Assert.True(c.Disposed));
        Assert.All(e.Renders, r => Assert.True(r.Disposed));
        Assert.Equal(MirrorRouterStatus.Stopped, router.GetStatus());

        router.Dispose();
        router.Dispose();
        router.Stop();
        Assert.Throws<ObjectDisposedException>(() => router.Apply(Headset, new[] { Route(Digital) }));

        Thread.Sleep(100);
        int after = Volatile.Read(ref events);
        Thread.Sleep(100);
        Assert.Equal(after, Volatile.Read(ref events)); // nothing is raised after Dispose
    }

    [Fact]
    public void Dispose_WhileRunning_ReleasesEverything_AndLatePacketsAreIgnored()
    {
        var e = Machine();
        var router = Router(e);
        router.Apply(Headset, new[] { Route(Digital) });
        var capture = e.LiveCapture(Headset)!;

        router.Dispose();

        Assert.True(capture.Disposed);
        Assert.True(e.Renders[0].Disposed);
        capture.PushEvenIfDisposed(Tone(Block)); // a packet racing the teardown: never throws
        capture.FailEvenIfDisposed(new COMException("late"));
    }

    [Fact]
    public void Apply_BadArguments()
    {
        using var router = Router(Machine());
        Assert.ThrowsAny<ArgumentException>(() => router.Apply("", new[] { Route(Digital) }));
        Assert.ThrowsAny<ArgumentException>(() => router.Apply("  ", new[] { Route(Digital) }));
        Assert.Throws<ArgumentNullException>(() => router.Apply(Headset, null!));
    }

    [Fact]
    public void RealRouter_UnknownDevices_ReportsWithoutThrowing_AndTouchesNoHardware()
    {
        using var router = new MirrorRouter();

        var status = router.Apply(Missing, new[] { Route("{0.0.0.00000000}.{00000000-0000-0000-0000-00000000beef}") });

        Assert.False(router.IsRunning);
        Assert.NotNull(status.SourceError);
        router.Stop();
        Assert.Equal(MirrorRouterStatus.Stopped, router.GetStatus());
    }

    [Fact]
    public void FormatAdaptation_AnOutputAt44k1_IsResampled_NotSilent()
    {
        var e = Machine();
        e.RenderRates[Speakers] = 44100;
        using var router = Router(e);
        router.Apply(Headset, new[] { Route(Speakers) });

        // 10 ms in (480 frames at 48 kHz), 10 ms out (441 frames at 44.1 kHz).
        var capture = e.LiveCapture(Headset)!;
        var render = e.LiveRender(Speakers)!;
        var output = new List<float>();
        for (int i = 0, frame = 0; i < 60; i++, frame += Block)
        {
            capture.Push(Tone(Block, frame));
            if (i >= 3) output.AddRange(render.Pull(441));
        }

        Assert.Equal(44100, render.SampleRate);
        float peak = output.Skip(output.Count / 2).Max(MathF.Abs);
        Assert.InRange(peak, 0.45f, 0.55f); // same level, other rate
    }
}

/// <summary>Fake endpoints for <see cref="MirrorRouter"/>: a device table, started captures and renders, and failure switches.</summary>
internal sealed class FakeMirrorEndpoints : IMirrorEndpoints
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AudioDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FakeMirrorCapture> _captures = new();
    private readonly List<FakeMirrorRender> _renders = new();

    /// <summary>Ids whose StartLoopback / StartRender throw AUDCLNT_E_DEVICE_IN_USE.</summary>
    public HashSet<string> FailOpen { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-device render rate (default 48 kHz).</summary>
    public Dictionary<string, int> RenderRates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FakeMirrorCapture> Captures
    {
        get { lock (_gate) return _captures.ToList(); }
    }

    public List<FakeMirrorRender> Renders
    {
        get { lock (_gate) return _renders.ToList(); }
    }

    public void Add(string id, string name, DeviceActivity state = DeviceActivity.Active)
    {
        lock (_gate) _devices[id] = new AudioDeviceInfo { Id = id, FriendlyName = name, Kind = DeviceKind.Playback, State = state };
    }

    public void SetActive(string id, bool active)
    {
        lock (_gate)
        {
            var d = _devices[id];
            _devices[id] = new AudioDeviceInfo { Id = d.Id, FriendlyName = d.FriendlyName, Kind = d.Kind, State = active ? DeviceActivity.Active : DeviceActivity.Unplugged };
        }
    }

    public FakeMirrorCapture? LiveCapture(string id)
    {
        lock (_gate) return _captures.LastOrDefault(c => string.Equals(c.DeviceId, id, StringComparison.OrdinalIgnoreCase) && !c.Disposed);
    }

    public FakeMirrorRender? LiveRender(string id)
    {
        lock (_gate) return _renders.LastOrDefault(r => string.Equals(r.DeviceId, id, StringComparison.OrdinalIgnoreCase) && !r.Disposed);
    }

    public AudioDeviceInfo? Describe(string deviceId)
    {
        lock (_gate) return _devices.TryGetValue(deviceId, out var d) ? d : null;
    }

    public IMirrorStream StartLoopback(string deviceId, MirrorSampleHandler onSamples, Action<Exception?> onStopped)
    {
        lock (_gate)
        {
            ThrowIfUnavailable(deviceId);
            var c = new FakeMirrorCapture(deviceId, onSamples, onStopped);
            _captures.Add(c);
            return c;
        }
    }

    public IMirrorStream StartRender(string deviceId, Func<int, int, ISampleProvider> buildSource, Action<Exception?> onStopped, Action<Exception> onProcessingError)
    {
        lock (_gate)
        {
            ThrowIfUnavailable(deviceId);
            int rate = RenderRates.TryGetValue(deviceId, out var r) ? r : 48000;
            var render = new FakeMirrorRender(deviceId, rate, buildSource(rate, 2), onStopped);
            _renders.Add(render);
            return render;
        }
    }

    private void ThrowIfUnavailable(string deviceId)
    {
        if (FailOpen.Contains(deviceId))
        {
            throw new COMException("in use", unchecked((int)0x8889000A));
        }

        if (!_devices.TryGetValue(deviceId, out var d) || d.State != DeviceActivity.Active)
        {
            throw new COMException("gone", unchecked((int)0x88890004));
        }
    }
}

/// <summary>A fake headset loopback: 48 kHz stereo, event-driven.</summary>
internal sealed class FakeMirrorCapture(string deviceId, MirrorSampleHandler onSamples, Action<Exception?> onStopped) : IMirrorStream
{
    private volatile bool _disposed;

    public string DeviceId { get; } = deviceId;

    public int SampleRate => 48000;

    public int Channels => 2;

    public bool LowLatency => true;

    public bool Disposed => _disposed;

    public void Push(float[] samples)
    {
        if (!_disposed) onSamples(samples, samples.Length);
    }

    public void PushEvenIfDisposed(float[] samples) => onSamples(samples, samples.Length);

    public void Fail(Exception? ex)
    {
        if (!_disposed) onStopped(ex);
    }

    public void FailEvenIfDisposed(Exception? ex) => onStopped(ex);

    public void Dispose() => _disposed = true;
}

/// <summary>A fake output: the test pulls what it would play.</summary>
internal sealed class FakeMirrorRender(string deviceId, int sampleRate, ISampleProvider source, Action<Exception?> onStopped) : IMirrorStream
{
    private volatile bool _disposed;

    public string DeviceId { get; } = deviceId;

    public int SampleRate { get; } = sampleRate;

    public int Channels => 2;

    public bool LowLatency => true;

    public bool Disposed => _disposed;

    public ISampleProvider Source { get; } = source;

    public float[] Pull(int frames)
    {
        var buffer = new float[frames * Channels];
        int read = Source.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, read);
        return buffer;
    }

    public void Fail(Exception? ex)
    {
        if (!_disposed) onStopped(ex);
    }

    public void Dispose() => _disposed = true;
}
