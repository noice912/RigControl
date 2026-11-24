using AudioController.Core.Models;
using AudioController.Core.Routing;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// Re-pairing a Bluetooth headset makes Windows delete its endpoints and create new ones with new ids. The Setup picks
/// must follow the device by name (<see cref="RoutingPlanner.ReadoptRenamedIds"/>) — this is what silently broke the
/// EQ when the headphones were re-paired: Equalizer APO and the app both still pointed at the deleted endpoint.
/// </summary>
public sealed class RepairedDeviceTests
{
    private const string OldHeadphones = "{0.0.0.00000000}.{11111111-0000-0000-0000-000000000001}";
    private const string NewHeadphones = "{0.0.0.00000000}.{22222222-0000-0000-0000-000000000001}";
    private const string OldMic = "{0.0.1.00000000}.{11111111-0000-0000-0000-000000000002}";
    private const string NewMic = "{0.0.1.00000000}.{22222222-0000-0000-0000-000000000002}";
    private const string Speakers = "{0.0.0.00000000}.{33333333-0000-0000-0000-000000000003}";
    private const string CableIn = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000004}";

    private const string HeadphonesName = "Headphones (Crusher ANC 2)";
    private const string MicName = "Headset (Crusher ANC 2)";

    private static AudioDeviceInfo Dev(string id, string name, DeviceKind kind,
        DeviceActivity state = DeviceActivity.Active, bool isVirtual = false) =>
        new() { Id = id, FriendlyName = name, Kind = kind, State = state, IsVirtual = isVirtual, IsBluetooth = !isVirtual };

    /// <summary>After re-pairing: only the NEW endpoints are listed (the old ones are gone from Windows).</summary>
    private static List<AudioDeviceInfo> AfterRepair() => new()
    {
        Dev(NewHeadphones, HeadphonesName, DeviceKind.Playback),
        Dev(NewMic, MicName, DeviceKind.Capture),
        Dev(Speakers, "Speakers (Realtek USB2.0 Audio)", DeviceKind.Playback),
        Dev(CableIn, "CABLE Input (VB-Audio Virtual Cable)", DeviceKind.Playback, isVirtual: true),
    };

    private static AppSettings SavedBeforeRepair() => new()
    {
        InputDeviceId = OldMic,
        InputDeviceName = MicName,
        HeadsetDeviceId = OldHeadphones,
        HeadsetDeviceName = HeadphonesName,
        MonitorDeviceId = OldHeadphones,
        SystemEqOutputDeviceId = OldHeadphones,
    };

    [Fact]
    public void ReadoptRenamedIds_DevicesRepaired_FollowsMicAndHeadsetByName()
    {
        var s = SavedBeforeRepair();

        bool changed = RoutingPlanner.ReadoptRenamedIds(s, AfterRepair());

        Assert.True(changed);
        Assert.Equal(NewMic, s.InputDeviceId);
        Assert.Equal(NewHeadphones, s.HeadsetDeviceId);
        Assert.Equal(MicName, s.InputDeviceName);
        Assert.Equal(HeadphonesName, s.HeadsetDeviceName);
    }

    [Fact]
    public void ReadoptRenamedIds_HeadsetRepaired_MonitorAndSystemEqOutputFollowWhenTheyPointedAtIt()
    {
        var s = SavedBeforeRepair();

        RoutingPlanner.ReadoptRenamedIds(s, AfterRepair());

        Assert.Equal(NewHeadphones, s.MonitorDeviceId);
        Assert.Equal(NewHeadphones, s.SystemEqOutputDeviceId);
    }

    [Fact]
    public void ReadoptRenamedIds_AdvancedOverrideElsewhere_IsKept()
    {
        var s = SavedBeforeRepair();
        s.MonitorDeviceId = Speakers; // the user chose another monitor in Advanced

        RoutingPlanner.ReadoptRenamedIds(s, AfterRepair());

        Assert.Equal(Speakers, s.MonitorDeviceId);
    }

    [Fact]
    public void ReadoptRenamedIds_DeviceMerelyDisconnected_KeepsItsId()
    {
        var s = SavedBeforeRepair();
        var devices = new List<AudioDeviceInfo>
        {
            // Disconnected headphones keep their endpoint (Unplugged), and a same-named device elsewhere must not win.
            Dev(OldHeadphones, HeadphonesName, DeviceKind.Playback, DeviceActivity.Unplugged),
            Dev(NewHeadphones, HeadphonesName, DeviceKind.Playback),
            Dev(OldMic, MicName, DeviceKind.Capture, DeviceActivity.Unplugged),
        };

        bool changed = RoutingPlanner.ReadoptRenamedIds(s, devices);

        Assert.False(changed);
        Assert.Equal(OldHeadphones, s.HeadsetDeviceId);
        Assert.Equal(OldMic, s.InputDeviceId);
    }

    [Fact]
    public void ReadoptRenamedIds_TwoActiveDevicesWithTheSavedName_ChangesNothing()
    {
        var s = SavedBeforeRepair();
        var devices = AfterRepair();
        devices.Add(Dev("{0.0.0.00000000}.{55555555-0000-0000-0000-000000000005}", HeadphonesName, DeviceKind.Playback));

        RoutingPlanner.ReadoptRenamedIds(s, devices);

        Assert.Equal(OldHeadphones, s.HeadsetDeviceId); // ambiguous: left for the user to pick
        Assert.Equal(NewMic, s.InputDeviceId);           // the mic is unambiguous and still follows
    }

    [Fact]
    public void ReadoptRenamedIds_NoSavedName_ChangesNothing()
    {
        var s = SavedBeforeRepair();
        s.HeadsetDeviceName = null;
        s.InputDeviceName = null;

        bool changed = RoutingPlanner.ReadoptRenamedIds(s, AfterRepair());

        Assert.False(changed);
        Assert.Equal(OldHeadphones, s.HeadsetDeviceId);
        Assert.Equal(OldMic, s.InputDeviceId);
    }

    [Fact]
    public void ReadoptRenamedIds_InactiveOrVirtualSameName_IsNotAdopted()
    {
        var s = new AppSettings { HeadsetDeviceId = OldHeadphones, HeadsetDeviceName = "CABLE Input (VB-Audio Virtual Cable)" };
        var devices = new List<AudioDeviceInfo>
        {
            Dev(CableIn, "CABLE Input (VB-Audio Virtual Cable)", DeviceKind.Playback, isVirtual: true),
            Dev(NewHeadphones, HeadphonesName, DeviceKind.Playback, DeviceActivity.Disabled),
        };

        bool changed = RoutingPlanner.ReadoptRenamedIds(s, devices);

        Assert.False(changed);
        Assert.Equal(OldHeadphones, s.HeadsetDeviceId);
    }

    [Fact]
    public void ReadoptRenamedIds_SavedIdListed_RefreshesTheNameOnly()
    {
        var s = new AppSettings { HeadsetDeviceId = NewHeadphones, HeadsetDeviceName = null };

        bool changed = RoutingPlanner.ReadoptRenamedIds(s, AfterRepair());

        Assert.True(changed);
        Assert.Equal(NewHeadphones, s.HeadsetDeviceId);
        Assert.Equal(HeadphonesName, s.HeadsetDeviceName);
    }

    [Fact]
    public void ReadoptRenamedIds_EverythingCurrent_ReturnsFalse()
    {
        var s = new AppSettings
        {
            InputDeviceId = NewMic,
            InputDeviceName = MicName,
            HeadsetDeviceId = NewHeadphones,
            HeadsetDeviceName = HeadphonesName,
        };

        Assert.False(RoutingPlanner.ReadoptRenamedIds(s, AfterRepair()));
    }

    [Fact]
    public void ReadoptRenamedIds_NamesMatchCaseInsensitively_AndAdoptTheCurrentSpelling()
    {
        var s = new AppSettings { HeadsetDeviceId = OldHeadphones, HeadsetDeviceName = "headphones (crusher anc 2)" };

        RoutingPlanner.ReadoptRenamedIds(s, AfterRepair());

        Assert.Equal(NewHeadphones, s.HeadsetDeviceId);
        Assert.Equal(HeadphonesName, s.HeadsetDeviceName);
    }
}
