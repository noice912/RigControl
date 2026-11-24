using AudioController.Audio;
using AudioController.Audio.Routing;
using AudioController.Core.Models;
using NAudio.Wave;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// System EQ parts that need no audio hardware: the device guards, the ±1.0 clamp stage and the router's
/// lifecycle. Routing real loopback audio is a live check (it needs a second virtual playback device).
/// </summary>
public sealed class SystemEqRouterTests
{
    // Well-formed playback endpoint ids that no machine has.
    private const string MissingSourceId = "{0.0.0.00000000}.{00000000-0000-0000-0000-00000000c0c0}";
    private const string MissingOutputId = "{0.0.0.00000000}.{00000000-0000-0000-0000-00000000d0d0}";

    private const string SourceId = "{0.0.0.00000000}.{11111111-0000-0000-0000-000000000001}";
    private const string OutputId = "{0.0.0.00000000}.{22222222-0000-0000-0000-000000000002}";

    private static AudioDeviceInfo Device(
        string id,
        string name,
        DeviceKind kind = DeviceKind.Playback,
        DeviceActivity state = DeviceActivity.Active) =>
        new() { Id = id, FriendlyName = name, Kind = kind, State = state };

    private static readonly AudioDeviceInfo VirtualSource = Device(SourceId, "Line (Voicemod Virtual Audio Device (WDM))");
    private static readonly AudioDeviceInfo Headphones = Device(OutputId, "Headphones (Crusher ANC 2)");

    // ---------------------------------------------------------------- device guards (hardware-free)

    [Fact]
    public void CheckDevices_ValidVirtualSourceToHeadphones_IsAccepted()
    {
        Assert.Null(SystemEqRouter.CheckDevices(SourceId, OutputId, VirtualSource, Headphones));
    }

    [Fact]
    public void CheckDevices_SecondVbAudioCable_IsAcceptedAsSource()
    {
        // VB-Audio CABLE A is a different cable from the virtual-mic "CABLE Input".
        var cableA = Device(SourceId, "CABLE-A Input (VB-Audio Cable A)");

        Assert.Null(SystemEqRouter.CheckDevices(SourceId, OutputId, cableA, Headphones));
    }

    [Theory]
    [InlineData(SourceId, SourceId)]
    [InlineData("{0.0.0.00000000}.{AAAAAAAA-0000-0000-0000-000000000001}", "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-000000000001}")]
    public void CheckDevices_SourceEqualsOutput_IsRejected(string sourceId, string outputId)
    {
        var device = Device(sourceId, "Speakers (Realtek(R) Audio)");

        Assert.Equal(SystemEqRouter.SameDeviceMessage, SystemEqRouter.CheckDevices(sourceId, outputId, device, device));
    }

    [Fact]
    public void CheckDevices_NoDevicesChosen_AsksForThem()
    {
        Assert.Equal(SystemEqRouter.NoSourceMessage, SystemEqRouter.CheckDevices(null, OutputId, null, Headphones));
        Assert.Equal(SystemEqRouter.NoSourceMessage, SystemEqRouter.CheckDevices("  ", OutputId, null, Headphones));
        Assert.Equal(SystemEqRouter.NoOutputMessage, SystemEqRouter.CheckDevices(SourceId, null, VirtualSource, null));
        Assert.Equal(SystemEqRouter.NoOutputMessage, SystemEqRouter.CheckDevices(SourceId, "", VirtualSource, null));
    }

    [Fact]
    public void CheckDevices_MissingDevices_HaveClearMessages()
    {
        string? noSource = SystemEqRouter.CheckDevices(SourceId, OutputId, null, Headphones);
        string? noOutput = SystemEqRouter.CheckDevices(SourceId, OutputId, VirtualSource, null);

        Assert.NotNull(noSource);
        Assert.Contains("source device was not found", noSource);
        Assert.Contains("unplugged or removed", noSource);
        Assert.NotNull(noOutput);
        Assert.Contains("output device was not found", noOutput);
    }

    [Theory]
    [InlineData(DeviceActivity.Disabled, "disabled")]
    [InlineData(DeviceActivity.Unplugged, "unplugged")]
    [InlineData(DeviceActivity.NotPresent, "not present")]
    public void CheckDevices_InactiveDevices_AreRejectedWithTheirState(DeviceActivity state, string stateText)
    {
        var inactiveSource = Device(SourceId, "Voicemod", state: state);
        var inactiveOutput = Device(OutputId, "Headphones (Crusher ANC 2)", state: state);

        string? source = SystemEqRouter.CheckDevices(SourceId, OutputId, inactiveSource, Headphones);
        string? output = SystemEqRouter.CheckDevices(SourceId, OutputId, VirtualSource, inactiveOutput);

        Assert.NotNull(source);
        Assert.Contains($"source \"Voicemod\" is {stateText}", source);
        Assert.Contains("Only active devices", source);
        Assert.NotNull(output);
        Assert.Contains($"output \"Headphones (Crusher ANC 2)\" is {stateText}", output);
    }

    [Fact]
    public void CheckDevices_RecordingEndpoints_AreRejected()
    {
        var mic = Device(SourceId, "Microphone (Crusher ANC 2)", DeviceKind.Capture);
        var cableOutput = Device(OutputId, "CABLE Output (VB-Audio Virtual Cable)", DeviceKind.Capture);

        Assert.Contains("is a recording device", SystemEqRouter.CheckDevices(SourceId, OutputId, mic, Headphones));
        Assert.Contains("is a recording device", SystemEqRouter.CheckDevices(SourceId, OutputId, VirtualSource, cableOutput));
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("cable input (vb-audio virtual cable)")]
    [InlineData("CABLE Input")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    public void CheckDevices_CableInputAsSource_IsRejectedBecauseDiscordWouldHearIt(string name)
    {
        var cable = Device(SourceId, name);

        string? message = SystemEqRouter.CheckDevices(SourceId, OutputId, cable, Headphones);

        Assert.Equal(SystemEqRouter.CableSourceMessage, message);
        Assert.Contains("virtual-microphone path", message);
        Assert.Contains("Discord", message);
    }

    [Fact]
    public void CheckDevices_CableInputAsOutput_IsRejected()
    {
        var cable = Device(OutputId, "CABLE Input (VB-Audio Virtual Cable)");

        string? message = SystemEqRouter.CheckDevices(SourceId, OutputId, VirtualSource, cable);

        Assert.Equal(SystemEqRouter.CableOutputMessage, message);
        Assert.Contains("Discord", message);
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE-A Input (VB-Audio Cable A)", false)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", false)]
    [InlineData("Line (Voicemod Virtual Audio Device (WDM))", false)]
    [InlineData("Speakers (NVIDIA Virtual Audio Device (Wave Extensible) (WDM))", false)]
    [InlineData(null, false)]
    public void IsVirtualMicInput_MatchesOnlyTheVirtualMicCable(string? name, bool expected)
    {
        Assert.Equal(expected, SystemEqRouter.IsVirtualMicInput(name));
    }

    // ---------------------------------------------------------------- Start guards

    [Fact]
    public void Start_SourceEqualsOutput_ThrowsReadableError_BeforeTouchingDevices()
    {
        using var router = new SystemEqRouter();

        var ex = Assert.Throws<InvalidOperationException>(() => router.Start(SourceId, SourceId.ToUpperInvariant()));

        Assert.Contains("same device", ex.Message);
        Assert.StartsWith("Could not start System EQ:", ex.Message);
        Assert.False(router.IsRunning);
        Assert.Null(router.SourceDeviceId);
        Assert.Null(router.OutputDeviceId);
    }

    [Fact]
    public void Start_MissingDevices_ThrowsReadableError_AndStaysStopped()
    {
        using var router = new SystemEqRouter();
        int statusEvents = 0;
        router.StatusChanged += (_, _) => Interlocked.Increment(ref statusEvents);

        var ex = Assert.Throws<InvalidOperationException>(() => router.Start(MissingSourceId, MissingOutputId));

        Assert.StartsWith("Could not start System EQ:", ex.Message);
        Assert.True(ex.Message.Length > "Could not start System EQ:".Length + 10, ex.Message);
        Assert.False(router.IsRunning);
        Assert.False(router.LowLatencyCapture);
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref statusEvents)); // a failed start never reports a state change
    }

    [Fact]
    public void Start_BlankIds_AreArgumentErrors()
    {
        using var router = new SystemEqRouter();

        Assert.ThrowsAny<ArgumentException>(() => router.Start("", OutputId));
        Assert.ThrowsAny<ArgumentException>(() => router.Start(SourceId, "   "));
        Assert.ThrowsAny<ArgumentException>(() => router.Start(null!, OutputId));
    }

    // ---------------------------------------------------------------- lifecycle and EQ state

    [Fact]
    public void StopAndDispose_AreIdempotent_AndStartAfterDisposeThrows()
    {
        var router = new SystemEqRouter();
        Assert.False(router.IsRunning);

        router.Stop();
        router.Stop();
        router.Dispose();
        router.Dispose();
        router.Stop();

        Assert.False(router.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => router.Start(SourceId, OutputId));
    }

    [Fact]
    public void SetEq_WhileStopped_IsRecorded_ClampedLikeTheEngine()
    {
        using var router = new SystemEqRouter();
        Assert.False(router.EqEnabled);
        Assert.Equal(new float[TenBandEq.BandCount], router.GetEqGains());

        router.SetEq(new[] { 40f, -40f, float.NaN, 3.5f }, enabled: true);

        Assert.True(router.EqEnabled);
        Assert.Equal(new[] { 24f, -24f, 0f, 3.5f, 0f, 0f, 0f, 0f, 0f, 0f }, router.GetEqGains());

        router.SetEq(new float[TenBandEq.BandCount], enabled: false);
        Assert.False(router.EqEnabled);
        Assert.Throws<ArgumentNullException>(() => router.SetEq(null!, true));
    }

    [Fact]
    public void SetEq_AfterDispose_IsIgnored()
    {
        var router = new SystemEqRouter();
        router.SetEq(new[] { 6f }, enabled: true);
        router.Dispose();

        router.SetEq(new[] { -6f }, enabled: false); // must not throw

        Assert.True(router.EqEnabled);
        Assert.Equal(6f, router.GetEqGains()[0]);
    }

    [Fact]
    public void AppSettings_SystemEqDefaultsToOff()
    {
        var s = new AppSettings();

        Assert.False(s.SystemEqEnabled);
        Assert.Null(s.SystemEqSourceDeviceId);
        Assert.Null(s.SystemEqOutputDeviceId);
    }

    // ---------------------------------------------------------------- clamp stage

    private static readonly WaveFormat Stereo48k = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    [Fact]
    public void Clamp_InRangeSamples_PassThroughBitExact()
    {
        float[] samples =
        {
            0f, -0f, 1f, -1f, 0.5f, -0.5f, 0.99999994f, -0.99999994f, float.Epsilon, -float.Epsilon,
            1e-30f, 0.123456789f, -0.987654321f, BitConverter.Int32BitsToSingle(0x00000001), BitConverter.Int32BitsToSingle(unchecked((int)0x807FFFFF)),
        };
        var random = new Random(42);
        var data = samples.Concat(Enumerable.Range(0, 4096).Select(_ => (float)(random.NextDouble() * 2 - 1))).ToArray();
        var clamp = new ClampSampleProvider(new ArraySource(data, Stereo48k));
        var output = new float[data.Length];

        int read = clamp.Read(output, 0, output.Length);

        Assert.Equal(data.Length, read);
        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(data[i]), BitConverter.SingleToInt32Bits(output[i]));
        }
    }

    [Fact]
    public void Clamp_OutOfRangeSamples_AreLimitedToPlusMinusOne()
    {
        float[] data = { 1.0000001f, 1.5f, 3.98f, 1e30f, float.PositiveInfinity, -1.0000001f, -2f, -1e30f, float.NegativeInfinity, float.NaN };
        float[] expected = { 1f, 1f, 1f, 1f, 1f, -1f, -1f, -1f, -1f, 0f };
        var clamp = new ClampSampleProvider(new ArraySource(data, Stereo48k));
        var output = new float[data.Length];

        clamp.Read(output, 0, output.Length);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void Clamp_TouchesOnlyTheSamplesItRead()
    {
        // The source runs dry after 3 samples; nothing past them (or before the offset) may change.
        var clamp = new ClampSampleProvider(new ArraySource(new[] { 5f, -5f, 0.25f }, Stereo48k));
        var output = new[] { 9f, 9f, 9f, 9f, 9f, 9f, 9f };

        int read = clamp.Read(output, 2, 5);

        Assert.Equal(3, read);
        Assert.Equal(new[] { 9f, 9f, 1f, -1f, 0.25f, 9f, 9f }, output);
        Assert.Same(Stereo48k, clamp.WaveFormat);
        Assert.Throws<ArgumentNullException>(() => new ClampSampleProvider(null!));
    }

    [Fact]
    public void Clamp_AfterA24dBBoost_KeepsTheLoudToneAtFullScale()
    {
        // A loud 1 kHz tone through +24 dB at 1 kHz goes far past full scale; the clamp holds it at ±1.0.
        const int rate = 48_000;
        var tone = new float[rate / 2 * 2];
        for (int i = 0; i < tone.Length / 2; i++)
        {
            float v = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
            tone[2 * i] = v;
            tone[2 * i + 1] = v;
        }

        var eq = new TenBandEq(rate, 2);
        var gains = new float[TenBandEq.BandCount];
        gains[5] = 24f;
        eq.SetGains(gains);
        eq.Enabled = true;
        var boosted = (float[])tone.Clone();
        eq.Process(boosted, 0, boosted.Length);
        Assert.True(boosted.Max(MathF.Abs) > 2f, "The boost should exceed full scale before the clamp.");

        var output = new float[boosted.Length];
        new ClampSampleProvider(new ArraySource(boosted, Stereo48k)).Read(output, 0, output.Length);

        Assert.All(output, s => Assert.InRange(s, -1f, 1f));
        Assert.Equal(1f, output.Max());
        Assert.Equal(-1f, output.Min());
    }

    /// <summary>Plays a fixed array once, then reports the end with short reads.</summary>
    private sealed class ArraySource(float[] data, WaveFormat format) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
