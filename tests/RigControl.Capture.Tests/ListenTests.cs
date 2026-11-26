using RigControl.Capture.Listen;

namespace RigControl.Capture.Tests;

public class GainLimiterTests
{
    private const int Rate = 48000;

    private static float[] Sine(float amplitude, int frames, int channels = 2, float hz = 440)
    {
        var s = new float[frames * channels];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++) s[f * channels + c] = amplitude * MathF.Sin(2 * MathF.PI * hz * f / Rate);
        return s;
    }

    [Fact]
    public void A_quiet_signal_comes_out_five_times_louder_and_unchanged_in_shape()
    {
        var limiter = new GainLimiter(Rate, 2) { Gain = 5f };
        var input = Sine(0.05f, 4800);
        var output = (float[])input.Clone();
        var loudest = limiter.Process(output);

        var d = limiter.DelayFrames;
        for (var f = d; f < 4800; f++)
            for (var c = 0; c < 2; c++)
                Assert.Equal(input[(f - d) * 2 + c] * 5f, output[f * 2 + c], 4);
        Assert.InRange(loudest, 0.24f, 0.2501f);
        Assert.Equal(1f, limiter.AppliedGain, 4);
    }

    [Fact]
    public void Loud_parts_never_go_above_full_scale_and_the_gain_moves_smoothly()
    {
        var limiter = new GainLimiter(Rate, 2) { Gain = 6f };
        // Quiet, then suddenly very loud (0.8 x 6 = 4.8), then quiet again.
        var samples = Sine(0.05f, 2400).Concat(Sine(0.8f, 4800)).Concat(Sine(0.05f, 24000)).ToArray();
        var gains = new List<float>();
        var frame = new float[2];
        var max = 0f;
        for (var f = 0; f < samples.Length / 2; f++)
        {
            frame[0] = samples[2 * f];
            frame[1] = samples[2 * f + 1];
            max = Math.Max(max, limiter.Process(frame));
            Assert.True(MathF.Abs(frame[0]) <= 1f && MathF.Abs(frame[1]) <= 1f);
            gains.Add(limiter.AppliedGain);
        }

        Assert.InRange(max, 0.9f, GainLimiter.Ceiling);
        Assert.True(gains.Min() < 0.25f, "the limiter should have turned the loud part down");
        // No jumps: the gain moves by small steps from one sample to the next (no hard clipping).
        var biggestStep = gains.Zip(gains.Skip(1), (a, b) => MathF.Abs(a - b)).Max();
        Assert.True(biggestStep < 0.02f, $"gain jumped by {biggestStep}");
        // And it comes back up once the loud part is over.
        Assert.True(gains[^1] > 0.95f, $"gain stayed at {gains[^1]}");
    }

    [Fact]
    public void Garbage_input_comes_out_silent()
    {
        var limiter = new GainLimiter(Rate, 1) { Gain = 5f };
        var s = new[] { float.NaN, float.PositiveInfinity, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };
        limiter.Process(s);
        Assert.All(s, x => Assert.True(float.IsFinite(x) && MathF.Abs(x) <= 1f));
    }
}

public class BoundedSampleBufferTests
{
    [Fact]
    public void It_keeps_at_most_twice_the_latency_and_drops_the_oldest()
    {
        var max = BoundedSampleBuffer.MaxFramesFor(48000, 30);
        Assert.Equal(2880, max); // 60 ms at 48 kHz
        var b = new BoundedSampleBuffer(2, 4);
        b.Write(new float[] { 1, 1, 2, 2, 3, 3 });
        b.Write(new float[] { 4, 4, 5, 5, 6, 6 });
        Assert.Equal(4, b.Frames);
        Assert.Equal(2, b.DroppedFrames);
        var out1 = new float[8];
        b.Read(out1);
        Assert.Equal(new float[] { 3, 3, 4, 4, 5, 5, 6, 6 }, out1);
    }

    [Fact]
    public void A_write_bigger_than_the_buffer_keeps_only_the_newest()
    {
        var b = new BoundedSampleBuffer(1, 3);
        b.Write(new float[] { 1, 2, 3, 4, 5 });
        var o = new float[3];
        b.Read(o);
        Assert.Equal(new float[] { 3, 4, 5 }, o);
    }

    [Fact]
    public void Reading_more_than_is_there_gives_silence_and_wraps_correctly()
    {
        var b = new BoundedSampleBuffer(1, 4);
        b.Write(new float[] { 1, 2, 3 });
        var o = new float[2];
        b.Read(o);
        Assert.Equal(new float[] { 1, 2 }, o);
        b.Write(new float[] { 4, 5, 6 }); // wraps round the end
        var o2 = new float[6];
        b.Read(o2);
        Assert.Equal(new float[] { 3, 4, 5, 6, 0, 0 }, o2);
        Assert.Equal(2, b.MissingFrames);
        Assert.Equal(0, b.Frames);
    }
}

public class CaptureCardPickerTests
{
    private static readonly AudioEndpoint Mic = new("mic", "Microphone (Realtek USB Audio)", @"USB\VID_0BDA&PID_4014");
    private static readonly AudioEndpoint Card = new("card", "Digital Audio Interface (USB Digital Audio)", @"{1}.USB\VID_534D&PID_2109&MI_02\7&1234");
    private static readonly AudioEndpoint NamedOnly = new("named", "Digital Audio Interface (USB Digital Audio)");
    private static readonly AudioEndpoint Other = new("other", "Line In (Something)");

    [Fact]
    public void The_MacroSilicon_device_wins()
        => Assert.Equal("card", CaptureCardPicker.Pick(new[] { Mic, NamedOnly, Card })!.Id);

    [Fact]
    public void Without_a_USB_id_the_name_is_enough()
        => Assert.Equal("named", CaptureCardPicker.Pick(new[] { Mic, NamedOnly })!.Id);

    [Fact]
    public void No_capture_card_means_none()
    {
        Assert.Null(CaptureCardPicker.Pick(new[] { Mic, Other }));
        Assert.Null(CaptureCardPicker.Pick(Array.Empty<AudioEndpoint>()));
    }
}

public class ListenSettingsTests
{
    [Fact]
    public void Defaults_are_off_automatic_500_percent_30_ms()
    {
        var s = ListenSettings.Sanitise(new ListenSettings());
        Assert.False(s.Enabled);
        Assert.Null(s.InputId);
        Assert.Null(s.OutputId);
        Assert.Equal(500, s.GainPercent);
        Assert.Equal(30, s.LatencyMs);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(250, 250)]
    [InlineData(601, 600)]
    [InlineData(5000, 600)]
    public void Boost_is_kept_between_100_and_600(int given, int kept)
        => Assert.Equal(kept, ListenSettings.Sanitise(new ListenSettings { GainPercent = given }).GainPercent);

    [Fact]
    public void Blank_ids_mean_automatic()
    {
        var s = ListenSettings.Sanitise(new ListenSettings { InputId = "", OutputId = "  ", LatencyMs = 1 });
        Assert.Null(s.InputId);
        Assert.Null(s.OutputId);
        Assert.Equal(ListenSettings.MinLatency, s.LatencyMs);
    }
}

public class ListenModuleTests
{
    private sealed class FakeSession : IListenSession
    {
        public float Gain { get; set; }
        public bool Faulted { get; set; }
        public string? Problem { get; set; }
        public bool Disposed { get; private set; }
        public float TakePeak() => 0.5f;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Microphones_and_headsets_are_never_picked_and_a_switched_off_capture_card_is_still_found()
    {
        var mic = new AudioEndpoint("mic", "Headset (Crusher ANC 2)", FormFactor: 5);
        var realMic = new AudioEndpoint("m2", "Microphone (Realtek USB Audio)", FormFactor: 4);
        var card = new AudioEndpoint("card", "Digital Audio Interface (USB Digital Audio)", @"USB\VID_534D&PID_2109", 7, Enabled: false);
        Assert.Equal("card", CaptureCardPicker.Pick(new[] { mic, realMic, card })!.Id);
        Assert.True(mic.IsMicrophoneLike);
        Assert.False(card.IsMicrophoneLike);
        // A microphone named like a capture card still isn't one to play.
        Assert.Null(CaptureCardPicker.Pick(new[] { new AudioEndpoint("x", "USB Digital Audio mic", FormFactor: 4) }));
    }

    private sealed class FakeEngine : IListenEngine
    {
        public List<AudioEndpoint> In = new();
        public List<AudioEndpoint> Out = new() { new("hp", "Headphones (Realtek USB Audio)") };
        public string? Default = "hp";
        public List<FakeSession> Sessions = new();
        public IReadOnlyList<AudioEndpoint> Inputs() => In;
        public IReadOnlyList<AudioEndpoint> Outputs() => Out;
        public string? DefaultOutputId() => Default;
        public List<string> Enabled = new();
        public bool EnableInput(string id)
        {
            Enabled.Add(id);
            var i = In.FindIndex(d => d.Id == id);
            if (i >= 0) In[i] = In[i] with { Enabled = true };
            return true;
        }
        public IListenSession Start(AudioEndpoint input, AudioEndpoint output, float gain, int latencyMs)
        {
            var s = new FakeSession { Gain = gain };
            Sessions.Add(s);
            return s;
        }
    }

    [Fact]
    public void It_waits_for_the_capture_card_then_plays_it_and_starts_again_after_an_unplug()
    {
        var engine = new FakeEngine();
        var m = new ListenModule(engine, runLoop: false);
        m.SetSettings(new ListenModule.SettingsArgs { Enabled = true });
        Assert.Equal("waiting", m.GetStatus().State);
        Assert.Contains("No capture card", m.GetStatus().Problem);

        engine.In.Add(new("card", "Digital Audio Interface (USB Digital Audio)"));
        m.Check();
        var st = m.GetStatus();
        Assert.Equal("running", st.State);
        Assert.Equal("Headphones (Realtek USB Audio)", st.OutputName);
        Assert.Equal(5f, engine.Sessions[0].Gain);

        m.SetSettings(new ListenModule.SettingsArgs { GainPercent = 9000 });
        Assert.Equal(6f, engine.Sessions[0].Gain);
        Assert.Equal(600, m.GetStatus().Settings.GainPercent);

        engine.Sessions[0].Faulted = true;
        m.Check();
        Assert.True(engine.Sessions[0].Disposed);
        Assert.Equal(2, engine.Sessions.Count);
        Assert.Equal("running", m.GetStatus().State);

        m.SetSettings(new ListenModule.SettingsArgs { Enabled = false });
        Assert.True(engine.Sessions[1].Disposed);
        Assert.Equal("off", m.GetStatus().State);
    }
}
