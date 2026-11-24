using System.Globalization;
using AudioController.Audio;
using AudioController.Core.Equalizer;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The Equalizer APO file generator (pure): exact text, the same filters as the app's TenBandEq, headset-only scoping,
/// invariant numbers, and the one-line config.txt include edit.
/// </summary>
public sealed class EqualizerApoConfigTests
{
    private const string HeadphonesId = "{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string HeadsetMicId = "{0.0.1.00000000}.{bf75d670-a9c6-406d-8aa9-623656457f84}";
    private const string Selector = "{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string Header = "# Written by Audio Controller from its EQ sliders - edits here are overwritten. Applies to your headset only.\r\n";
    private const string DeviceLine = "Device: " + Selector + "\r\n";
    private const string ChannelLine = "Channel: all\r\n";
    private const string ResetLine = "Device: all\r\n";

    private static float[] Flat() => new float[10];

    // ---------------------------------------------------------------- exact text

    [Fact]
    public void Generate_Flat_HasNoFilters_AndZeroPreamp()
    {
        string text = EqualizerApoConfig.Generate(Flat(), eqEnabled: true, Selector, autoPreamp: true);

        Assert.Equal(Header + DeviceLine + ChannelLine + "Preamp: 0 dB\r\n" + ResetLine, text);
    }

    [Fact]
    public void Generate_AFewGains_AutoPreampOn()
    {
        var gains = new[] { 3f, 0f, -2.5f, 0f, 0f, 24f, 0f, 0f, 0.005f, -24f };

        string text = EqualizerApoConfig.Generate(gains, eqEnabled: true, Selector, autoPreamp: true);

        Assert.Equal(
            Header + DeviceLine + ChannelLine +
            "Preamp: -24 dB\r\n" +
            "Filter 1: ON PK Fc 31 Hz Gain 3 dB Q 1\r\n" +
            "Filter 2: ON PK Fc 125 Hz Gain -2.5 dB Q 1\r\n" +
            "Filter 3: ON PK Fc 1000 Hz Gain 24 dB Q 1\r\n" +
            "Filter 4: ON PK Fc 16000 Hz Gain -24 dB Q 1\r\n" +
            ResetLine,
            text);
    }

    [Fact]
    public void Generate_AFewGains_AutoPreampOff()
    {
        var gains = new[] { 0f, 1.25f, 0f, 0f, 9.75f, 0f, -6f, 0f, 0f, 0f };

        string text = EqualizerApoConfig.Generate(gains, eqEnabled: true, Selector, autoPreamp: false);

        Assert.Equal(
            Header + DeviceLine + ChannelLine +
            "Preamp: 0 dB\r\n" +
            "Filter 1: ON PK Fc 62 Hz Gain 1.25 dB Q 1\r\n" +
            "Filter 2: ON PK Fc 500 Hz Gain 9.75 dB Q 1\r\n" +
            "Filter 3: ON PK Fc 2000 Hz Gain -6 dB Q 1\r\n" +
            ResetLine,
            text);
    }

    [Fact]
    public void Generate_Plus24At1k_TheLiveCheckFile()
    {
        var gains = Flat();
        gains[5] = 24f;

        Assert.Equal(
            Header + DeviceLine + ChannelLine + "Preamp: -24 dB\r\n" + "Filter 1: ON PK Fc 1000 Hz Gain 24 dB Q 1\r\n" + ResetLine,
            EqualizerApoConfig.Generate(gains, true, Selector, autoPreamp: true));
        Assert.Equal(
            Header + DeviceLine + ChannelLine + "Preamp: 0 dB\r\n" + "Filter 1: ON PK Fc 1000 Hz Gain 24 dB Q 1\r\n" + ResetLine,
            EqualizerApoConfig.Generate(gains, true, Selector, autoPreamp: false));
    }

    [Fact]
    public void Generate_AutoPreamp_UsesTheLargestBoost_AndIgnoresCuts()
    {
        var boosts = new[] { 2f, 7.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, -12f };
        var cutsOnly = new[] { -3f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, -12f };

        Assert.Contains("Preamp: -7.5 dB\r\n", EqualizerApoConfig.Generate(boosts, true, Selector, true));
        Assert.Contains("Preamp: 0 dB\r\n", EqualizerApoConfig.Generate(cutsOnly, true, Selector, true));
    }

    [Fact]
    public void Generate_EqDisabled_HasNoFilters_WhateverTheGains()
    {
        var gains = new[] { 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f };

        string text = EqualizerApoConfig.Generate(gains, eqEnabled: false, Selector, autoPreamp: true);

        Assert.Equal(Header + DeviceLine + ChannelLine + "Preamp: 0 dB\r\n" + ResetLine, text);
    }

    [Fact]
    public void Generate_ClampsLikeTenBandEq_AndSkipsNearZeroBands()
    {
        var gains = new[] { 30f, -99f, float.NaN, 0.0099f, -0.0099f, 0.01f, 0f, 0f, 0f, 0f, 12f /* 11th: ignored */ };

        string text = EqualizerApoConfig.Generate(gains, true, Selector, autoPreamp: false);

        Assert.Equal(
            Header + DeviceLine + ChannelLine + "Preamp: 0 dB\r\n" +
            "Filter 1: ON PK Fc 31 Hz Gain 24 dB Q 1\r\n" +
            "Filter 2: ON PK Fc 62 Hz Gain -24 dB Q 1\r\n" +
            "Filter 3: ON PK Fc 1000 Hz Gain 0.01 dB Q 1\r\n" +
            ResetLine,
            text);
    }

    [Fact]
    public void Generate_FewerThanTenGains_MissingBandsAreFlat()
    {
        string text = EqualizerApoConfig.Generate(new[] { 0f, 4f }, true, Selector, autoPreamp: true);

        Assert.Equal(Header + DeviceLine + ChannelLine + "Preamp: -4 dB\r\n" + "Filter 1: ON PK Fc 62 Hz Gain 4 dB Q 1\r\n" + ResetLine, text);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Generate_UsesInvariantNumbers_UnderAnyCulture(string culture)
    {
        var gains = new[] { -2.5f, 0f, 0f, 0f, 0f, 1.25f, 0f, 0f, 0f, 0f };
        string expected;
        using (CultureScope.Invariant())
        {
            expected = EqualizerApoConfig.Generate(gains, true, Selector, true);
        }

        using (new CultureScope(new CultureInfo(culture)))
        {
            string text = EqualizerApoConfig.Generate(gains, true, Selector, true);

            Assert.Equal(expected, text);
            Assert.Contains("Gain -2.5 dB", text);
            Assert.Contains("Gain 1.25 dB", text);
            Assert.Contains("Preamp: -1.25 dB", text);
            Assert.DoesNotContain("2,5", text);
        }
    }

    // ---------------------------------------------------------------- device scoping (mic safety)

    [Fact]
    public void Generate_ScopesEverythingToTheHeadset_AndResetsAtTheEnd()
    {
        var gains = new[] { 6f, 0f, 0f, 0f, 0f, 24f, 0f, 0f, 0f, -3f };
        string selector = EqualizerApoConfig.DeviceSelectorFor(HeadphonesId)!;

        var lines = EqualizerApoConfig.Generate(gains, true, selector, true)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var commands = lines.Where(l => !l.StartsWith('#')).ToList();

        Assert.Equal("Device: " + Selector, commands[0]);    // the first command scopes to the headset
        Assert.Equal("Channel: all", commands[1]);           // then every channel, whatever config.txt selected
        Assert.Equal("Device: all", commands[^1]);           // the last one resets the scope
        Assert.Equal(2, commands.Count(c => c.StartsWith("Device:", StringComparison.Ordinal)));
        Assert.All(commands.Skip(2).SkipLast(1), c => Assert.True(c.StartsWith("Preamp:") || c.StartsWith("Filter ")));
    }

    [Fact]
    public void Generate_ResetsTheChannelSelection_InsideTheHeadsetScope_AndLeavesTheStageAlone()
    {
        // Equalizer APO runs an Include inline and keeps Channel:/Stage: from config.txt: a user's "Channel: R" left at
        // the end of config.txt must not narrow the app's filters to one channel.
        string text = EqualizerApoConfig.Generate(new[] { 0f, 0f, 0f, 0f, 0f, 24f, 0f, 0f, 0f, 0f }, true, Selector, true);
        var commands = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToList();

        int device = commands.IndexOf("Device: " + Selector);
        int channel = commands.IndexOf(EqualizerApoConfig.ChannelResetLine);
        Assert.Equal(0, device);
        Assert.Equal(device + 1, channel); // after Device:, so other devices never see it
        Assert.True(channel < commands.FindIndex(c => c.StartsWith("Preamp:", StringComparison.Ordinal)));
        Assert.True(channel < commands.FindIndex(c => c.StartsWith("Filter ", StringComparison.Ordinal)));
        Assert.Single(commands, c => c.StartsWith("Channel:", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.StartsWith("Stage:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Channel: all", EqualizerApoConfig.ChannelResetLine);
    }

    [Fact]
    public void Generate_ForTheHeadset_NeverNamesTheMicrophone()
    {
        string text = EqualizerApoConfig.Generate(new[] { 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f, 24f }, true,
            EqualizerApoConfig.DeviceSelectorFor(HeadphonesId)!, true);

        Assert.DoesNotContain("bf75d670", text, StringComparison.OrdinalIgnoreCase); // the Crusher hands-free mic
        Assert.DoesNotContain("Device: all\r\nFilter", text);
        Assert.DoesNotContain("{0.0.1.", text);
    }

    [Fact]
    public void DeviceSelectorFor_IsTheBracedLowerCaseEndpointGuid()
    {
        Assert.Equal(Selector, EqualizerApoConfig.DeviceSelectorFor(HeadphonesId));
        Assert.Equal(Selector, EqualizerApoConfig.DeviceSelectorFor(HeadphonesId.ToUpperInvariant()));
        Assert.Equal(Selector, EqualizerApoConfig.DeviceSelectorFor("{9a828b4a-e626-46d3-b084-908d7994216b}"));
        Assert.Equal("{bf75d670-a9c6-406d-8aa9-623656457f84}", EqualizerApoConfig.DeviceSelectorFor(HeadsetMicId));
        Assert.Null(EqualizerApoConfig.DeviceSelectorFor(null));
        Assert.Null(EqualizerApoConfig.DeviceSelectorFor(""));
        Assert.Null(EqualizerApoConfig.DeviceSelectorFor("Headphones (Crusher ANC 2)"));
    }

    [Fact]
    public void Generate_RejectsMultiLineOrEmptySelectors()
    {
        Assert.Throws<ArgumentException>(() => EqualizerApoConfig.Generate(Flat(), true, "abc\r\nFilter: ON PK Fc 1000 Hz Gain 24 dB Q 1", true));
        Assert.ThrowsAny<ArgumentException>(() => EqualizerApoConfig.Generate(Flat(), true, " ", true));
        Assert.Throws<ArgumentNullException>(() => EqualizerApoConfig.Generate(null!, true, Selector, true));
    }

    [Fact]
    public void NeutralText_HasNoCommands()
    {
        var commands = EqualizerApoConfig.NeutralText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.TrimStart().StartsWith('#'));

        Assert.Empty(commands);
    }

    // ---------------------------------------------------------------- same filters as the app's own EQ

    [Fact]
    public void Bands_MatchTenBandEq()
    {
        Assert.Equal(TenBandEq.BandFrequenciesHz, EqualizerApoConfig.BandFrequenciesHz);
        Assert.Equal(TenBandEq.BandQ, EqualizerApoConfig.BandQ);
        Assert.Equal(TenBandEq.MinGainDb, EqualizerApoConfig.MinGainDb);
        Assert.Equal(TenBandEq.MaxGainDb, EqualizerApoConfig.MaxGainDb);
        Assert.Equal(TenBandEq.BandCount, EqualizerApoConfig.BandFrequenciesHz.Count);
    }

    // ---------------------------------------------------------------- endpoint ids ↔ registry keys

    [Fact]
    public void EndpointIdFor_BuildsWindowsEndpointIds()
    {
        Assert.Equal(HeadphonesId, EqualizerApoConfig.EndpointIdFor(capture: false, "{9a828b4a-e626-46d3-b084-908d7994216b}"));
        Assert.Equal(HeadsetMicId, EqualizerApoConfig.EndpointIdFor(capture: true, "{BF75D670-A9C6-406D-8AA9-623656457F84}"));
        Assert.Null(EqualizerApoConfig.EndpointIdFor(false, "Properties"));
        Assert.Null(EqualizerApoConfig.EndpointIdFor(false, null));
    }

    [Fact]
    public void EndpointGuid_AndCaptureDetection()
    {
        Assert.Equal(new Guid("9a828b4a-e626-46d3-b084-908d7994216b"), EqualizerApoConfig.EndpointGuid(HeadphonesId));
        Assert.True(EqualizerApoConfig.IsCaptureEndpointId(HeadsetMicId));
        Assert.False(EqualizerApoConfig.IsCaptureEndpointId(HeadphonesId));
        Assert.False(EqualizerApoConfig.IsCaptureEndpointId(null));
    }

    [Theory]
    [InlineData("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5", "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", true)]  // pre-mix
    [InlineData("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6", "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}", true)]  // post-mix
    [InlineData("{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D},1", "{eacd2258-fcac-4ff4-b36d-419e924a6d79}", true)]  // case
    [InlineData("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5", "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}", false)] // another APO
    [InlineData("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5", "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", false)] // other property set
    [InlineData(null, "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", false)]
    public void NamesEqualizerApo_String(string? valueName, string value, bool expected)
    {
        Assert.Equal(expected, EqualizerApoFx.NamesEqualizerApo(valueName, value));
    }

    [Fact]
    public void NamesEqualizerApo_MultiStringAndOtherTypes()
    {
        const string name = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        Assert.True(EqualizerApoFx.NamesEqualizerApo(name, new[] { "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}", EqualizerApoFx.PostMixClsid }));
        Assert.False(EqualizerApoFx.NamesEqualizerApo(name, new[] { "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}" }));
        Assert.False(EqualizerApoFx.NamesEqualizerApo(name, new byte[] { 1, 2, 3 }));
        Assert.False(EqualizerApoFx.NamesEqualizerApo(name, null));
    }

    [Fact]
    public void State_IsAttachedTo_IgnoresCase_AndNeedsAnInstall()
    {
        var state = new EqualizerApoState { Installed = true, AttachedEndpointIds = new[] { HeadphonesId } };

        Assert.True(state.IsAttachedTo(HeadphonesId.ToUpperInvariant()));
        Assert.False(state.IsAttachedTo(HeadsetMicId));
        Assert.False(state.IsAttachedTo(null));
        Assert.False((state with { Installed = false }).IsAttachedTo(HeadphonesId));
        Assert.False(EqualizerApoState.NotInstalled.IsAttachedTo(HeadphonesId));
        Assert.True(state.SameAs(state with { AttachedEndpointIds = new[] { HeadphonesId.ToUpperInvariant() } }));
        Assert.False(state.SameAs(state with { AttachedEndpointIds = new[] { HeadsetMicId } }));
    }

    // ---------------------------------------------------------------- config.txt include

    private const string Include = "Include: AudioController.txt";

    [Fact]
    public void EnsureInclude_UsersCurrentConfig_AppendsTheIncludeOnce()
    {
        const string current = "# Neutral until Audio Controller takes over (original saved as config.original.txt)\r\nPreamp: 0 dB\r\n";

        string updated = EqualizerApoConfig.EnsureInclude(current);

        Assert.Equal(current + Include + "\r\n", updated);
        Assert.Same(updated, EqualizerApoConfig.EnsureInclude(updated)); // idempotent: unchanged instance
    }

    [Fact]
    public void EnsureInclude_KeepsEveryOtherLine_AndNeverReAddsExampleTxt()
    {
        const string original = "Preamp: -6 dB\r\nInclude: example.txt\r\nGraphicEQ: 25 0; 40 0\r\n";
        string updated = EqualizerApoConfig.EnsureInclude(original);
        Assert.Equal(original + Include + "\r\n", updated);

        const string withoutExample = "Preamp: -6 dB\r\n";
        Assert.DoesNotContain("example.txt", EqualizerApoConfig.EnsureInclude(withoutExample));
    }

    [Fact]
    public void EnsureInclude_RemovesDuplicates_KeepingTheFirst()
    {
        string text = "Preamp: 0 dB\r\n" + Include + "\r\n# mine\r\ninclude:audiocontroller.txt\r\n  Include:   AudioController.txt  \r\n";

        string updated = EqualizerApoConfig.EnsureInclude(text);

        Assert.Equal("Preamp: 0 dB\r\n" + Include + "\r\n# mine\r\n", updated);
    }

    [Fact]
    public void EnsureInclude_KeepsLfLineEndings_AndAddsAMissingFinalNewline()
    {
        Assert.Equal("Preamp: 0 dB\n" + Include + "\n", EqualizerApoConfig.EnsureInclude("Preamp: 0 dB\n"));
        Assert.Equal("Preamp: 0 dB\r\n" + Include + "\r\n", EqualizerApoConfig.EnsureInclude("Preamp: 0 dB"));
    }

    [Theory]
    [InlineData("# Include: AudioController.txt\r\n")]
    [InlineData("Include: AudioController.txt.bak\r\n")]
    [InlineData("Include: other\\AudioController.txt\r\n")]
    [InlineData("Includes: AudioController.txt\r\n")]
    public void EnsureInclude_OnlyARealIncludeOfOurFileCounts(string line)
    {
        Assert.Equal(line + Include + "\r\n", EqualizerApoConfig.EnsureInclude(line));
    }

    [Fact]
    public void EnsureInclude_EmptyOrMissingConfig()
    {
        Assert.Equal(Include + "\r\n", EqualizerApoConfig.EnsureInclude(""));
        Assert.Equal(Include + "\r\n", EqualizerApoConfig.EnsureInclude(null));
    }

    [Theory]
    [InlineData("Include: AudioController.txt", true)]
    [InlineData("  include :  audiocontroller.TXT ", true)]
    [InlineData("#Include: AudioController.txt", false)]
    [InlineData("Include: example.txt", false)]
    [InlineData("Include AudioController.txt", false)]
    [InlineData(null, false)]
    public void IsOurInclude(string? line, bool expected)
    {
        Assert.Equal(expected, EqualizerApoConfig.IsOurInclude(line));
    }
}
