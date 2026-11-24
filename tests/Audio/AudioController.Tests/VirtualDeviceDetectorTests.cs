using AudioController.Core.Devices;
using AudioController.Core.Models;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// Virtual-vs-hardware classification. The first theory is the user's real machine, measured with the
/// endpoints' DEVPKEY_Device_EnumeratorName values.
/// </summary>
public sealed class VirtualDeviceDetectorTests
{
    [Theory]
    // playback
    [InlineData("ROOT", "Dummy Output (Voicemod)", true)]
    [InlineData("ROOT", "CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("ROOT", "CABLE In 16ch (VB-Audio Virtual Cable)", true)]
    [InlineData("HDAUDIO", "LG FULL HD (NVIDIA High Definition Audio)", false)]
    [InlineData("BTHENUM", "Headphones (Crusher ANC 2)", false)]
    [InlineData("USB", "Digital Output (Realtek USB2.0 Audio)", false)]
    [InlineData("HDAUDIO", "LG ULTRAGEAR+ (NVIDIA High Definition Audio)", false)]
    // capture
    [InlineData("ROOT", "CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("ROOT", "Microphone (Voicemod)", true)]
    [InlineData("BTHHFENUM", "Headset (Crusher ANC 2)", false)]
    public void IsVirtual_UsersRealDevices_AreClassifiedCorrectly(string enumerator, string name, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsVirtual(enumerator, name));
    }

    [Theory]
    [InlineData("ROOT")]
    [InlineData("root")]
    [InlineData("SWD")]
    [InlineData("SW")]
    [InlineData("ROOT\\MEDIA\\0001")]
    [InlineData("VBAUDIOVACMME")]   // any enumerator that is not a hardware bus
    public void IsVirtual_SoftwareEnumerators_AreVirtual_EvenWithPlainNames(string enumerator)
    {
        Assert.True(VirtualDeviceDetector.IsVirtual(enumerator, "Speakers (Some Audio Device)"));
    }

    [Theory]
    [InlineData("HDAUDIO")]
    [InlineData("USB")]
    [InlineData("usb")]
    [InlineData("BTHENUM")]
    [InlineData("BTHHFENUM")]
    [InlineData("BTHLEDEVICE")]
    [InlineData("INTELAUDIO")]
    [InlineData("DISPLAY")]
    [InlineData("PCI")]
    [InlineData("ACPI")]
    [InlineData("SCSI")]
    [InlineData("USB\\VID_046D&PID_0A44\\5&1234")]
    public void IsVirtual_HardwareBuses_AreReal(string enumerator)
    {
        Assert.False(VirtualDeviceDetector.IsVirtual(enumerator, "Speakers (Some Audio Device)"));
        Assert.True(VirtualDeviceDetector.IsHardwareEnumerator(enumerator));
    }

    [Fact]
    public void IsVirtual_GenericWordVirtual_DoesNotOverrideAHardwareBus()
    {
        // A real USB gaming headset whose driver names itself "Virtual Surround".
        const string name = "Speakers (HyperX Virtual Surround Sound)";

        Assert.False(VirtualDeviceDetector.IsVirtual("USB", name));
        Assert.True(VirtualDeviceDetector.IsVirtual(null, name)); // unknown bus: the name hint decides
        Assert.True(VirtualDeviceDetector.IsVirtual("ROOT", name));
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE-A Input (VB-Audio Cable A)")]
    [InlineData("Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)")]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)")]
    [InlineData("Speakers (Steam Streaming Speakers)")]
    [InlineData("Dummy Output (Voicemod)")]
    [InlineData("Line (Voicemod Virtual Audio Device (WDM))")]
    public void IsVirtual_KnownVirtualProducts_AreVirtual_WithoutAnEnumerator(string name)
    {
        Assert.True(VirtualDeviceDetector.IsVirtual(null, name));
        Assert.True(VirtualDeviceDetector.IsVirtual("", name));
        Assert.True(VirtualDeviceDetector.IsVirtualProductName(name));
    }

    [Fact]
    public void IsVirtual_SpecificProductName_WinsOverAHardwareEnumerator()
    {
        Assert.True(VirtualDeviceDetector.IsVirtual("USB", "CABLE Input (VB-Audio Virtual Cable)"));
        Assert.True(VirtualDeviceDetector.IsVirtual("HDAUDIO", "Dummy Output (Voicemod)"));
    }

    [Theory]
    [InlineData(null, "Speakers (Realtek(R) Audio)")]
    [InlineData("  ", "Headphones (Crusher ANC 2)")]
    [InlineData(null, "")]
    public void IsVirtual_UnknownBusAndPlainName_IsReal(string? enumerator, string name)
    {
        Assert.False(VirtualDeviceDetector.IsVirtual(enumerator, name));
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)", true)]
    [InlineData("cable input", true)]
    [InlineData("CABLE-A Input (VB-Audio Cable A)", false)]
    [InlineData("CABLE-A In 16ch (VB-Audio Cable A)", false)]
    [InlineData("Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)", false)]
    [InlineData("Dummy Output (Voicemod)", false)]
    [InlineData(null, false)]
    public void IsVirtualMicInput_MatchesOnlyTheCableFeedingCableOutput(string? name, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsVirtualMicInput(name));
    }

    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("Headset (Crusher ANC 2)", false)]
    [InlineData(null, false)]
    public void IsVirtualMicOutput_MatchesCableOutput(string? name, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsVirtualMicOutput(name));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("ROOT", false)]
    [InlineData("SWD", false)]
    [InlineData("BTHFOO", true)]
    public void IsHardwareEnumerator_Edges(string? enumerator, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsHardwareEnumerator(enumerator));
    }

    // ---------------------------------------------------------------- rename-proof cable detection

    [Theory]
    // the user's current names
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    // renamed in Windows: the interface suffix stays
    [InlineData("Audio Controller Mic (VB-Audio Virtual Cable)", true)]
    [InlineData("Audio Controller (internal) (VB-Audio Virtual Cable)", true)]
    [InlineData("  Spare (vb-audio virtual cable)  ", true)]
    // unsuffixed legacy names
    [InlineData("CABLE Input", true)]
    [InlineData("cable output", true)]
    // other cables and real devices
    [InlineData("CABLE-A Input (VB-Audio Cable A)", false)]
    [InlineData("CABLE-B Output (VB-Audio Cable B)", false)]
    [InlineData("Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)", false)]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", false)]
    [InlineData("Headphones (Crusher ANC 2)", false)]
    [InlineData("Line 1 (Virtual Audio Cable)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOnVirtualMicCable_IsRenameProof(string? name, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsOnVirtualMicCable(name));
    }

    [Theory]
    [InlineData("Audio Controller (internal) (VB-Audio Virtual Cable)", true)]
    [InlineData("Spare (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", false)] // the recording side's own name
    public void IsVirtualMicInput_RenamedPlaybackEndpoints(string name, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsVirtualMicInput(name));
    }

    [Theory]
    [InlineData("Audio Controller Mic (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", false)] // the playback side's own name
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)", false)]
    [InlineData("CABLE-A Output (VB-Audio Cable A)", false)]
    public void IsVirtualMicOutput_RenamedRecordingEndpoints(string name, bool expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.IsVirtualMicOutput(name));
    }

    [Fact]
    public void IsVirtual_RenamedCableEndpoints_StayVirtual()
    {
        Assert.True(VirtualDeviceDetector.IsVirtual("ROOT", "Audio Controller Mic (VB-Audio Virtual Cable)"));
        Assert.True(VirtualDeviceDetector.IsVirtual(null, "Audio Controller (internal) (VB-Audio Virtual Cable)"));
    }

    private static AudioDeviceInfo Ep(string id, string name, DeviceKind kind = DeviceKind.Playback, DeviceActivity state = DeviceActivity.Active) =>
        new() { Id = id, FriendlyName = name, Kind = kind, State = state, IsVirtual = VirtualDeviceDetector.IsVirtual("ROOT", name) };

    private static readonly AudioDeviceInfo Headphones = new()
    {
        Id = "{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}", FriendlyName = "Headphones (Crusher ANC 2)", Kind = DeviceKind.Playback,
    };

    [Fact]
    public void ChooseCableFeed_DefaultNames_PicksCableInput_Not16ch()
    {
        var devices = new List<AudioDeviceInfo>
        {
            Ep("16", "CABLE In 16ch (VB-Audio Virtual Cable)"), Ep("in", "CABLE Input (VB-Audio Virtual Cable)"), Headphones,
            Ep("out", "CABLE Output (VB-Audio Virtual Cable)", DeviceKind.Capture),
        };

        Assert.Equal("in", VirtualDeviceDetector.ChooseCableFeed(devices, null)?.Id);
        Assert.Equal("out", VirtualDeviceDetector.ChooseCableMic(devices)?.Id);
    }

    [Fact]
    public void ChooseCableFeed_SavedIdOnTheCable_Wins()
    {
        var devices = new List<AudioDeviceInfo>
        {
            Ep("16", "CABLE In 16ch (VB-Audio Virtual Cable)"), Ep("in", "CABLE Input (VB-Audio Virtual Cable)"),
        };

        Assert.Equal("16", VirtualDeviceDetector.ChooseCableFeed(devices, "16")?.Id);
        Assert.Equal("16", VirtualDeviceDetector.ChooseCableFeed(devices, "16".ToUpperInvariant())?.Id);
        Assert.Equal("in", VirtualDeviceDetector.ChooseCableFeed(devices, Headphones.Id)?.Id); // not on the cable: ignored
    }

    [Fact]
    public void ChooseCableFeed_Renamed_PicksTheOneThatIsNot16ch()
    {
        var devices = new List<AudioDeviceInfo>
        {
            Ep("16", "CABLE In 16ch (VB-Audio Virtual Cable)"), Ep("in", "Audio Controller (internal) (VB-Audio Virtual Cable)"),
            Ep("mic", "Audio Controller Mic (VB-Audio Virtual Cable)", DeviceKind.Capture),
        };

        Assert.Equal("in", VirtualDeviceDetector.ChooseCableFeed(devices, null)?.Id);
        Assert.Equal("mic", VirtualDeviceDetector.ChooseCableMic(devices)?.Id);
    }

    [Fact]
    public void ChooseCableFeed_BothRenamed_FallsBackToTheFirstByName_AndHidesTheOther()
    {
        var devices = new List<AudioDeviceInfo>
        {
            Ep("b", "Zeta (VB-Audio Virtual Cable)"), Ep("a", "Audio Controller (internal) (VB-Audio Virtual Cable)"),
        };

        var feed = VirtualDeviceDetector.ChooseCableFeed(devices, null);

        Assert.Equal("a", feed?.Id);
        Assert.False(VirtualDeviceDetector.IsHiddenCableEndpoint(devices[1], feed!.Id));
        Assert.True(VirtualDeviceDetector.IsHiddenCableEndpoint(devices[0], feed.Id));
    }

    [Fact]
    public void ChooseCableFeed_IgnoresInactiveAndOtherCables()
    {
        var devices = new List<AudioDeviceInfo>
        {
            Ep("gone", "CABLE Input (VB-Audio Virtual Cable)", state: DeviceActivity.Disabled),
            Ep("a", "CABLE-A Input (VB-Audio Cable A)"), Headphones,
        };

        Assert.Null(VirtualDeviceDetector.ChooseCableFeed(devices, "gone"));
        Assert.Null(VirtualDeviceDetector.ChooseCableMic(devices));
    }

    [Fact]
    public void IsHiddenCableEndpoint_Only16chAndOtherCableRenderEndpoints()
    {
        var feed = Ep("in", "CABLE Input (VB-Audio Virtual Cable)");
        var sixteen = Ep("16", "CABLE In 16ch (VB-Audio Virtual Cable)");
        var mic = Ep("out", "CABLE Output (VB-Audio Virtual Cable)", DeviceKind.Capture);
        var cableA16 = Ep("a16", "CABLE-A In 16ch (VB-Audio Cable A)");

        Assert.True(VirtualDeviceDetector.IsHiddenCableEndpoint(sixteen, "in"));
        Assert.True(VirtualDeviceDetector.IsHiddenCableEndpoint(sixteen, null)); // no feed known: by its "16ch" name
        Assert.False(VirtualDeviceDetector.IsHiddenCableEndpoint(feed, "in"));
        Assert.False(VirtualDeviceDetector.IsHiddenCableEndpoint(feed, null));
        Assert.False(VirtualDeviceDetector.IsHiddenCableEndpoint(mic, "in"));     // the virtual mic stays listed
        Assert.False(VirtualDeviceDetector.IsHiddenCableEndpoint(cableA16, "in")); // another cable entirely
        Assert.False(VirtualDeviceDetector.IsHiddenCableEndpoint(Headphones, "in"));
    }

    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", "CABLE Output")]
    [InlineData("Audio Controller Mic (VB-Audio Virtual Cable)", "Audio Controller Mic")]
    [InlineData("Headset (Crusher ANC 2)", "Headset (Crusher ANC 2)")]
    [InlineData(" (VB-Audio Virtual Cable) ", "(VB-Audio Virtual Cable)")]
    public void ShortCableName_DropsTheCableSuffix(string name, string expected)
    {
        Assert.Equal(expected, VirtualDeviceDetector.ShortCableName(name));
    }
}
