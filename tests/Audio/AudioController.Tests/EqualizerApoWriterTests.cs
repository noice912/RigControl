using System.Text;
using AudioController.Audio;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The Equalizer APO writer against a temp "config" folder (never the real one): atomic writes, the single include line
/// in config.txt with every other byte kept, config.original.txt untouched, and the ~150 ms debounce.
/// </summary>
public sealed class EqualizerApoWriterTests : IDisposable
{
    private const string Selector = "{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string UsersConfig = "# Neutral until Audio Controller takes over (original saved as config.original.txt)\r\nPreamp: 0 dB\r\n";
    private const string OriginalConfig = "Preamp: -6 dB\r\nInclude: example.txt\r\nGraphicEQ: 25 0; 40 0; 63 0\r\n";

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string ConfigTxt => _dir.Combine("config.txt");

    private string OurFile => _dir.Combine(EqualizerApoConfig.FileName);

    private static string Plus24At1k()
    {
        var gains = new float[10];
        gains[5] = 24f;
        return EqualizerApoConfig.Generate(gains, true, Selector, autoPreamp: true);
    }

    [Fact]
    public void WriteNow_WritesTheFile_AndIncludesItOnce_KeepingTheRest()
    {
        File.WriteAllText(ConfigTxt, UsersConfig);
        File.WriteAllText(_dir.Combine("config.original.txt"), OriginalConfig);
        using var writer = new EqualizerApoWriter(_dir.Path);

        Assert.Null(writer.WriteNow(Plus24At1k(), ensureInclude: true));
        Assert.Null(writer.WriteNow(Plus24At1k(), ensureInclude: true)); // twice: still one include

        Assert.Equal(Plus24At1k(), File.ReadAllText(OurFile));
        Assert.Equal(UsersConfig + "Include: AudioController.txt\r\n", File.ReadAllText(ConfigTxt));
        Assert.Equal(OriginalConfig, File.ReadAllText(_dir.Combine("config.original.txt")));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
        Assert.False(File.Exists(_dir.Combine("example.txt")));
    }

    [Fact]
    public void WriteNow_WithoutInclude_DoesNotTouchConfigTxt()
    {
        File.WriteAllText(ConfigTxt, UsersConfig);
        var before = File.ReadAllBytes(ConfigTxt);
        using var writer = new EqualizerApoWriter(_dir.Path);

        Assert.Null(writer.WriteNow(EqualizerApoConfig.NeutralText, ensureInclude: false));

        Assert.Equal(before, File.ReadAllBytes(ConfigTxt));
        Assert.Equal(EqualizerApoConfig.NeutralText, File.ReadAllText(OurFile));
    }

    [Fact]
    public void WriteNow_SameContent_IsNotRewritten()
    {
        using var writer = new EqualizerApoWriter(_dir.Path);
        int writes = 0;
        writer.Written += (_, _) => writes++;

        writer.WriteNow(Plus24At1k(), ensureInclude: true);
        var stamp = File.GetLastWriteTimeUtc(OurFile);
        writer.WriteNow(Plus24At1k(), ensureInclude: true);

        Assert.Equal(1, writes);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(OurFile));
    }

    [Fact]
    public void WriteNow_FileIsUtf8WithoutBom()
    {
        using var writer = new EqualizerApoWriter(_dir.Path);
        writer.WriteNow(Plus24At1k(), ensureInclude: false);

        var bytes = File.ReadAllBytes(OurFile);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(Encoding.UTF8.GetBytes(Plus24At1k()), bytes);
    }

    [Fact]
    public void EnsureIncluded_KeepsABomAndNonUtf8BytesExactly()
    {
        // A UTF-8 file with a BOM, and an ANSI (Windows-1252) one with an "é": both must come back byte for byte.
        byte[] bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# café\r\nPreamp: 0 dB\r\n")];
        File.WriteAllBytes(ConfigTxt, bom);
        Assert.True(EqualizerApoWriter.EnsureIncluded(ConfigTxt));
        Assert.Equal([.. bom, .. Encoding.ASCII.GetBytes("Include: AudioController.txt\r\n")], File.ReadAllBytes(ConfigTxt));

        byte[] ansi = [.. Encoding.ASCII.GetBytes("# caf"), 0xE9, .. Encoding.ASCII.GetBytes("\r\nPreamp: 0 dB\r\n")];
        File.WriteAllBytes(ConfigTxt, ansi);
        Assert.True(EqualizerApoWriter.EnsureIncluded(ConfigTxt));
        Assert.Equal([.. ansi, .. Encoding.ASCII.GetBytes("Include: AudioController.txt\r\n")], File.ReadAllBytes(ConfigTxt));

        Assert.False(EqualizerApoWriter.EnsureIncluded(ConfigTxt)); // already there: untouched
    }

    [Fact]
    public void EnsureIncluded_CreatesAMissingConfigTxt()
    {
        Assert.True(EqualizerApoWriter.EnsureIncluded(ConfigTxt));
        Assert.Equal("Include: AudioController.txt\r\n", File.ReadAllText(ConfigTxt));
    }

    [Fact]
    public void Schedule_Debounces_OnlyTheLastContentIsWritten()
    {
        using var writer = new EqualizerApoWriter(_dir.Path);
        int writes = 0;
        writer.Written += (_, _) => Interlocked.Increment(ref writes);

        for (int i = 1; i <= 10; i++)
        {
            var gains = new float[10];
            gains[5] = i;
            writer.Schedule(EqualizerApoConfig.Generate(gains, true, Selector, true), ensureInclude: i == 1);
        }

        writer.Flush();

        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.Contains("Gain 10 dB", File.ReadAllText(OurFile));
        Assert.Contains("Include: AudioController.txt", File.ReadAllText(ConfigTxt)); // the first call's include request is kept
    }

    [Fact]
    public void Schedule_WritesByItselfAfterTheDebounce()
    {
        using var writer = new EqualizerApoWriter(_dir.Path);
        using var written = new ManualResetEventSlim();
        writer.Written += (_, _) => written.Set();

        writer.Schedule(Plus24At1k(), ensureInclude: false);

        Assert.True(written.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Plus24At1k(), File.ReadAllText(OurFile));
    }

    [Fact]
    public void Dispose_FlushesAPendingWrite()
    {
        var writer = new EqualizerApoWriter(_dir.Path);
        writer.Schedule(Plus24At1k(), ensureInclude: false);
        writer.Dispose();

        Assert.Equal(Plus24At1k(), File.ReadAllText(OurFile));
        Assert.NotNull(writer.WriteNow("x", false)); // closed
    }

    [Fact]
    public void WriteNow_MissingConfigFolder_ReportsInsteadOfThrowing()
    {
        using var writer = new EqualizerApoWriter(_dir.Combine("not-there"));

        string? error = writer.WriteNow(Plus24At1k(), ensureInclude: true);

        Assert.NotNull(error);
        Assert.False(Directory.Exists(_dir.Combine("not-there")));
    }

    [Fact]
    public void WriteApoState_WithGainAndApoAttached_WritesFilterLineScopedToTheHeadsetGuid()
    {
        // Reproduces the bug: moving a slider has to reach the file. Without the WriteApoState wiring there would be
        // no semantic entry point for the view model to drive the writer with (gains, enabled, headsetId, autoPreamp).
        using var writer = new EqualizerApoWriter(_dir.Path);
        var gains = new float[10];
        gains[5] = 24f; // 1 kHz

        writer.WriteApoState(gains, eqEnabled: true, headsetDeviceId: "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: true, autoPreamp: true);
        writer.Flush();

        string content = File.ReadAllText(OurFile);
        Assert.Contains("Device: " + Selector, content);
        Assert.Contains("Preamp: -24 dB", content);
        Assert.Contains("Filter 1: ON PK Fc 1000 Hz Gain 24 dB Q 1", content);
        Assert.Contains("Include: AudioController.txt", File.ReadAllText(ConfigTxt));
    }

    [Fact]
    public void WriteApoState_WithEqDisabled_WritesAScopedOffFileWithoutFilters()
    {
        // The user's live state: slider gains are extreme but Enable EQ is off, so the file must be flat.
        using var writer = new EqualizerApoWriter(_dir.Path);
        var gains = new float[10];
        for (int i = 0; i < gains.Length; i++) gains[i] = -24f;
        gains[9] = 24f; // the user's observed extreme state

        writer.WriteApoState(gains, eqEnabled: false, headsetDeviceId: "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: true, autoPreamp: true);
        writer.Flush();

        string content = File.ReadAllText(OurFile);
        Assert.DoesNotContain("Filter", content);
        Assert.Contains("Preamp: 0 dB", content);
        Assert.Contains("Device: " + Selector, content);
        Assert.EndsWith("Device: all\r\n", content);
    }

    [Fact]
    public void WriteApoState_ApoNotAttachedToHeadset_WritesNeutralTextAndDropsStaleFilters()
    {
        // Stale filter lines pointing at a device Equalizer APO is no longer being used for must not linger.
        using var writer = new EqualizerApoWriter(_dir.Path);
        var gains = new float[10];
        gains[5] = 24f;
        writer.WriteApoState(gains, eqEnabled: true, headsetDeviceId: "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: true, autoPreamp: true);
        writer.Flush();
        Assert.Contains("Filter 1: ON PK", File.ReadAllText(OurFile));

        writer.WriteApoState(gains, eqEnabled: true, headsetDeviceId: "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: false, autoPreamp: true);
        writer.Flush();

        Assert.Equal(EqualizerApoConfig.NeutralText, File.ReadAllText(OurFile));
    }

    [Fact]
    public void Detector_NeverThrows_AndReportsAConsistentState()
    {
        var state = EqualizerApoDetector.Detect();

        if (state.Installed)
        {
            Assert.False(string.IsNullOrEmpty(state.ConfigPath));
            Assert.True(Directory.Exists(state.ConfigPath));
            Assert.All(state.AttachedEndpointIds, id => Assert.Matches(@"^\{0\.0\.[01]\.00000000\}\.\{[0-9a-f-]{36}\}$", id));
        }
        else
        {
            Assert.Empty(state.AttachedEndpointIds);
        }
    }

    [Fact]
    public void Engine_MonitorEqBypass_IsRecordedWhileStopped()
    {
        using var engine = new AudioEngine();

        engine.SetMonitorEqBypass(true);
        Assert.True(engine.MonitorEqBypassed);
        engine.SetMonitorEqBypass(false);
        Assert.False(engine.MonitorEqBypassed);
    }

    // ---------------------------------------------------------------- Effects (FxState) writes

    private static FxState Effects(EffectLevels levels)
    {
        var s = new AppSettings { EqEnabled = true };
        FxSettings.Normalize(s);
        s.Effects = levels;
        return FxState.From(s);
    }

    [Fact]
    public void WriteFxState_Attached_WritesTheEffectsFile_AndTheImpulseAtTheDeviceRate()
    {
        File.WriteAllText(ConfigTxt, UsersConfig);
        using var writer = new EqualizerApoWriter(_dir.Path);
        var fx = Effects(new EffectLevels { Ambience = 40, Surround = 20 });

        writer.WriteFxState(fx, "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: true, autoPreamp: true, channels: 2, sampleRate: 44100);
        writer.Flush();

        Assert.Equal(EqualizerApoConfig.Generate(fx, Selector, true, 2), File.ReadAllText(OurFile));
        Assert.Equal(AmbienceImpulse.CreateWav(40, 44100), File.ReadAllBytes(writer.AmbiencePath));
        Assert.Equal(UsersConfig + "Include: AudioController.txt\r\n", File.ReadAllText(ConfigTxt));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void WriteFxState_NoAmbience_WritesNoImpulse_AndAMonoDeviceGetsNoCopy()
    {
        using var writer = new EqualizerApoWriter(_dir.Path);
        var fx = Effects(new EffectLevels { Surround = 100, Bass = 10 });

        writer.WriteFxState(fx, "{0.0.0.00000000}." + Selector, true, true, channels: 1);
        writer.Flush();

        Assert.False(File.Exists(writer.AmbiencePath));
        Assert.DoesNotContain("Copy:", File.ReadAllText(OurFile));
        Assert.Contains("ON LS Fc 100 Hz Gain 1.2 dB Q 0.7071", File.ReadAllText(OurFile));
    }

    [Fact]
    public void WriteFxState_NotAttached_WritesNeutral_OnlyWhenTheFileExists()
    {
        using var writer = new EqualizerApoWriter(_dir.Path);
        var fx = Effects(new EffectLevels { Bass = 100 });

        writer.WriteFxState(fx, "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: false, autoPreamp: true);
        writer.Flush();
        Assert.False(File.Exists(OurFile)); // nothing is created just to say nothing

        File.WriteAllText(OurFile, "stale");
        writer.WriteFxState(fx, "{0.0.0.00000000}." + Selector, apoAttachedToHeadset: false, autoPreamp: true);
        writer.Flush();
        Assert.Equal(EqualizerApoConfig.NeutralText, File.ReadAllText(OurFile));
    }

    [Fact]
    public void LegacyWriter_FallsBackToTheTenGains()
    {
        // A writer that only implements the 1.4 entry point still gets the 10 band gains from WriteFxState.
        var legacy = new LegacyOnlyWriter();
        IEqualizerApoWriter w = legacy;
        var s = new AppSettings { EqEnabled = true, EqGainsDb = new[] { 0f, 0, 0, 0, 0, 9f, 0, 0, 0, 0 } };
        FxSettings.Normalize(s);

        w.WriteFxState(FxState.From(s), "x", true, true);

        Assert.Equal(9f, legacy.Gains![5]);
        Assert.True(legacy.Enabled);
    }

    private sealed class LegacyOnlyWriter : IEqualizerApoWriter
    {
        public float[]? Gains { get; private set; }
        public bool Enabled { get; private set; }
        public event EventHandler<string>? WriteFailed { add { } remove { } }
        public event EventHandler? Written { add { } remove { } }
        public string ConfigDirectory => "";
        public string FilePath => "";
        public bool FileExists => false;

        public void WriteApoState(float[] gainsDb, bool eqEnabled, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp)
        {
            Gains = gainsDb;
            Enabled = eqEnabled;
        }

        public string? WriteNow(string content, bool ensureInclude) => null;
        public void Schedule(string content, bool ensureInclude) { }
        public void Flush() { }
        public void Dispose() { }
    }
}
