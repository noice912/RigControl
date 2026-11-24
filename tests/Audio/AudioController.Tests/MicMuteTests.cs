using AudioController.Audio;
using AudioController.Core.Abstractions;
using AudioController.Core.Models;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The "Mute Mic" contract parts that need no audio hardware. Releasing and reopening a real capture
/// stream while running is a manual check (it needs a microphone and the virtual cable).
/// </summary>
public sealed class MicMuteTests
{
    // A well-formed endpoint id that no machine has.
    private const string MissingCaptureId = "{0.0.1.00000000}.{00000000-0000-0000-0000-00000000a0a0}";
    private const string MissingRenderId = "{0.0.0.00000000}.{00000000-0000-0000-0000-00000000b0b0}";

    [Fact]
    public void MicMuted_DefaultsToFalse_AndIsRecordedWhileStopped()
    {
        using var engine = new AudioEngine();
        Assert.False(engine.MicMuted);

        engine.MicMuted = true;
        Assert.True(engine.MicMuted);
        Assert.False(engine.IsRunning);

        engine.MicMuted = true; // idempotent
        Assert.True(engine.MicMuted);

        engine.MicMuted = false;
        Assert.False(engine.MicMuted);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void MicMuted_SurvivesStop_AndIsIgnoredAfterDispose()
    {
        var engine = new AudioEngine();
        engine.MicMuted = true;

        engine.Stop();
        engine.Stop();
        Assert.True(engine.MicMuted);

        engine.Dispose();
        engine.Dispose();
        engine.MicMuted = false; // must never throw on a disposed engine
        Assert.True(engine.MicMuted);
    }

    [Fact]
    public void Start_WhileMuted_StillValidatesDevices_AndKeepsTheMute()
    {
        using var engine = new AudioEngine();
        engine.MicMuted = true;

        Assert.Throws<InvalidOperationException>(() => engine.Start(MissingCaptureId, MissingRenderId));

        Assert.False(engine.IsRunning);
        Assert.True(engine.MicMuted);
    }

    [Fact]
    public void Defaults_AreUnmuted()
    {
        Assert.False(new AppSettings().MicMuted);
        Assert.False(new EngineStatus().MicMuted);
    }
}
