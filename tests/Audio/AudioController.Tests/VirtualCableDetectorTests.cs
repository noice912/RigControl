using AudioController.Audio;
using AudioController.Core.Devices;
using AudioController.Core.Models;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// SPEC 5: the app detects VB-Audio Virtual Cable — rename-proof, since the user renames its endpoints in Windows
/// (Windows keeps the "(VB-Audio Virtual Cable)" interface suffix).
/// </summary>
public sealed class VirtualCableDetectorTests
{
    private const string InId = "{0.0.0.00000000}.{5e7ce607-be51-4e86-8e6c-80f7903a033e}";
    private const string In16Id = "{0.0.0.00000000}.{95350cad-93e1-48da-88e1-bf7af96ed163}";
    private const string OutId = "{0.0.1.00000000}.{197f05dd-3634-40fe-900a-a1ab49f285b5}";
    private const string HeadphonesId = "{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string HeadsetMicId = "{0.0.1.00000000}.{bf75d670-a9c6-406d-8aa9-623656457f84}";

    private static AudioDeviceInfo Ep(string id, string name, DeviceKind kind, DeviceActivity state = DeviceActivity.Active) =>
        new() { Id = id, FriendlyName = name, Kind = kind, State = state, IsVirtual = VirtualDeviceDetector.IsVirtualProductName(name) };

    private static List<AudioDeviceInfo> Machine(string inName, string in16Name, string outName) => new()
    {
        Ep(In16Id, in16Name, DeviceKind.Playback),
        Ep(InId, inName, DeviceKind.Playback),
        Ep(HeadphonesId, "Headphones (Crusher ANC 2)", DeviceKind.Playback),
        Ep(OutId, outName, DeviceKind.Capture),
        Ep(HeadsetMicId, "Headset (Crusher ANC 2)", DeviceKind.Capture),
    };

    [Fact]
    public void IsInstalled_UsersCurrentNames()
    {
        var devices = new ListDeviceManager(Machine(
            "CABLE Input (VB-Audio Virtual Cable)", "CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)"));

        Assert.True(VirtualCableDetector.IsInstalled(devices, out var input, out var output));
        Assert.Equal(InId, input);
        Assert.Equal(OutId, output);
    }

    [Fact]
    public void IsInstalled_RenamedEndpoints_AreStillFound()
    {
        var devices = new ListDeviceManager(Machine(
            "Audio Controller (internal) (VB-Audio Virtual Cable)", "CABLE In 16ch (VB-Audio Virtual Cable)",
            "Audio Controller Mic (VB-Audio Virtual Cable)"));

        Assert.True(VirtualCableDetector.IsInstalled(devices, out var input, out var output));
        Assert.Equal(InId, input);   // not the 16-channel endpoint
        Assert.Equal(OutId, output);
    }

    [Fact]
    public void IsInstalled_EverythingRenamed_UsesTheSavedFeed()
    {
        var devices = new ListDeviceManager(Machine(
            "Zeta feed (VB-Audio Virtual Cable)", "Alpha (VB-Audio Virtual Cable)", "Audio Controller Mic (VB-Audio Virtual Cable)"));

        Assert.True(VirtualCableDetector.IsInstalled(devices, InId, out var saved, out _));
        Assert.Equal(InId, saved);

        // Without a saved choice neither name says "CABLE Input" or "16ch": the first by name.
        Assert.True(VirtualCableDetector.IsInstalled(devices, out var first, out _));
        Assert.Equal(In16Id, first);
    }

    [Fact]
    public void IsInstalled_SavedIdNotOnTheCable_IsIgnored()
    {
        var devices = new ListDeviceManager(Machine(
            "CABLE Input (VB-Audio Virtual Cable)", "CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)"));

        Assert.True(VirtualCableDetector.IsInstalled(devices, HeadphonesId, out var input, out _));
        Assert.Equal(InId, input);
    }

    [Fact]
    public void IsInstalled_MissingRecordingSide_IsNotInstalled()
    {
        var list = Machine("CABLE Input (VB-Audio Virtual Cable)", "CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)");
        list.RemoveAll(d => d.Id == OutId);

        Assert.False(VirtualCableDetector.IsInstalled(new ListDeviceManager(list), out var input, out var output));
        Assert.Equal(InId, input);
        Assert.Null(output);
    }

    [Fact]
    public void IsInstalled_OtherCablesOnly_IsNotInstalled()
    {
        var devices = new ListDeviceManager(new List<AudioDeviceInfo>
        {
            Ep("a-in", "CABLE-A Input (VB-Audio Cable A)", DeviceKind.Playback),
            Ep("a-out", "CABLE-A Output (VB-Audio Cable A)", DeviceKind.Capture),
            Ep(HeadphonesId, "Headphones (Crusher ANC 2)", DeviceKind.Playback),
        });

        Assert.False(VirtualCableDetector.IsInstalled(devices, out var input, out var output));
        Assert.Null(input);
        Assert.Null(output);
    }

    [Fact]
    public void IsInstalled_InactiveCable_IsNotInstalled()
    {
        var list = Machine("CABLE Input (VB-Audio Virtual Cable)", "CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)")
            .Select(d => d.IsVirtual ? Ep(d.Id, d.FriendlyName, d.Kind, DeviceActivity.Disabled) : d).ToList();

        Assert.False(VirtualCableDetector.IsInstalled(new ListDeviceManager(list), out _, out _));
    }

    [Fact]
    public void IsInstalled_EnumerationFailure_IsNotInstalled()
    {
        Assert.False(VirtualCableDetector.IsInstalled(new ListDeviceManager(null), out var input, out var output));
        Assert.Null(input);
        Assert.Null(output);
    }
}
