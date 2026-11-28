using RigControl.Core.Logging;
using RigControl.Radio.Speech;

namespace RigControl.Radio.Tests;

/// <summary>A speech engine for the helper side of the tests: says how many samples it got, or crashes.</summary>
internal sealed class FakeSpeech(string device, Func<float[], string> answer) : ISpeech
{
    public string Device { get; } = device;
    public Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct) => Task.FromResult(answer(audio16k));
    public void Dispose() { }
}

public class SpeechWireTests
{
    [Fact]
    public async Task Frames_round_trip_and_oversized_ones_are_refused()
    {
        var ms = new MemoryStream();
        var audio = Enumerable.Range(0, 1000).Select(i => i / 1000f).ToArray();
        await SpeechWire.WriteAudioAsync(ms, audio, default);
        await SpeechWire.WriteTextAsync(ms, SpeechWire.Text, "some words", default);
        ms.Position = 0;
        var a = await SpeechWire.ReadAsync(ms, default);
        Assert.Equal(SpeechWire.Audio, a.Type);
        Assert.Equal(audio, SpeechWire.ToSamples(a.Payload));
        var t = await SpeechWire.ReadAsync(ms, default);
        Assert.Equal("some words", SpeechWire.ToText(t.Payload));

        var huge = new MemoryStream();
        huge.WriteByte(SpeechWire.Text);
        huge.Write(BitConverter.GetBytes(int.MaxValue));
        huge.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechWire.ReadAsync(huge, default));
    }
}

public class SpeechHelperClientTests
{
    private readonly List<(string Pipe, bool Cpu)> _starts = new();

    /// <summary>Starts the "helper" in this process, like RigControl.exe --speech-helper would.</summary>
    private Func<string, bool, bool> Starter(Func<bool, ISpeech> load) => (pipe, cpu) =>
    {
        lock (_starts) _starts.Add((pipe, cpu));
        _ = Task.Run(() => SpeechHelperHost.RunAsync(pipe, cpu, load, NullLog.Instance, default));
        return true;
    };

    [Fact]
    public async Task Words_come_back_from_the_helper()
    {
        using var client = new SpeechHelperClient(Starter(cpu => new FakeSpeech(cpu ? "cpu" : "gpu", a => $"heard {a.Length}")), NullLog.Instance);
        Assert.Equal("heard 16000", await client.TranscribeAsync(new float[16000], default));
        Assert.Equal("gpu", client.Device);
        Assert.Equal("heard 8000", await client.TranscribeAsync(new float[8000], default));
        Assert.Single(_starts);
        Assert.Matches(@"^RigControl-speech-[0-9a-f]{32}$", _starts[0].Pipe);
    }

    [Fact]
    public async Task A_crashed_helper_is_started_again_on_the_processor()
    {
        var crashes = 1;
        using var client = new SpeechHelperClient(Starter(cpu => new FakeSpeech(cpu ? "cpu" : "gpu", a =>
        {
            if (!cpu && Interlocked.Decrement(ref crashes) >= 0) throw new System.Runtime.InteropServices.SEHException("External component has thrown an exception.");
            return "fine";
        })), NullLog.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(new float[1600], default));
        Assert.Equal("fine", await client.TranscribeAsync(new float[1600], default));
        Assert.Equal("cpu", client.Device);
        Assert.Equal(new[] { false, true }, _starts.Select(s => s.Cpu));
    }

    [Fact]
    public async Task After_three_starts_it_gives_up()
    {
        using var client = new SpeechHelperClient(Starter(_ => new FakeSpeech("cpu", _ => throw new System.Runtime.InteropServices.SEHException("boom"))), NullLog.Instance);
        for (var i = 0; i < 3; i++) await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(new float[1600], default));
        var last = await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(new float[1600], default));
        Assert.Contains("stopped working", last.Message);
        Assert.Equal(3, _starts.Count);
    }

    [Fact]
    public async Task A_helper_that_never_answers_times_out()
    {
        using var client = new SpeechHelperClient((_, _) => true, NullLog.Instance) { ConnectTimeout = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(new float[1600], default));
    }

    [Fact]
    public async Task After_a_graphics_card_crash_later_starts_use_the_processor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigcontrol-gpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "radio", "speech-on-processor");
        try
        {
            using (var client = new SpeechHelperClient(Starter(cpu => new FakeSpeech(cpu ? "cpu" : "gpu", _ =>
                       cpu ? "fine" : throw new System.Runtime.InteropServices.SEHException("boom"))), NullLog.Instance, marker))
            {
                await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(new float[1600], default));
                Assert.Equal("fine", await client.TranscribeAsync(new float[1600], default));
            }
            Assert.True(File.Exists(marker));
            _starts.Clear();
            using (var again = new SpeechHelperClient(Starter(cpu => new FakeSpeech(cpu ? "cpu" : "gpu", _ => "fine")), NullLog.Instance, marker))
            {
                Assert.Equal("fine", await again.TranscribeAsync(new float[1600], default));
                Assert.Equal("cpu", again.Device);
            }
            Assert.Equal(new[] { true }, _starts.Select(s => s.Cpu)); // never tried the graphics card again
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
