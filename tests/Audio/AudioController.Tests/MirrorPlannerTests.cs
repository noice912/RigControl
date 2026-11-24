using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// "Also play on" planning (<see cref="MirrorPlanner"/>, pure): which playback devices may receive the copy (never the
/// headset itself, never the virtual-mic cable, never a virtual device, only connected ones), which ticked outputs are
/// played on, the inactive reasons, the stored settings' repair, and the follow-by-name rule for re-plugged / re-paired
/// outputs (<see cref="RoutingPlanner.ReadoptRenamedIds"/>).
/// </summary>
public sealed class MirrorPlannerTests
{
    private const string Headphones = "{0.0.0.00000000}.{07abda82-1125-4339-aca5-dca468fc985a}";
    private const string Digital = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}";
    private const string Speakers = "{0.0.0.00000000}.{33333333-0000-0000-0000-000000000003}";
    private const string CableIn = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000004}";
    private const string Cable16 = "{0.0.0.00000000}.{55555555-0000-0000-0000-000000000005}";
    private const string CableRenamed = "{0.0.0.00000000}.{66666666-0000-0000-0000-000000000006}";
    private const string CableA = "{0.0.0.00000000}.{77777777-0000-0000-0000-000000000007}";
    private const string Monitor = "{0.0.0.00000000}.{88888888-0000-0000-0000-000000000008}";
    private const string Mic = "{0.0.1.00000000}.{99999999-0000-0000-0000-000000000009}";
    private const string NewDigital = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-00000000000a}";

    private const string DigitalName = "Digital Output (Realtek USB2.0 Audio)";
    private const string HeadphonesName = "Headphones (Crusher ANC 2)";

    private static AudioDeviceInfo Dev(string id, string name, DeviceKind kind = DeviceKind.Playback,
        DeviceActivity state = DeviceActivity.Active, bool isVirtual = false, bool bluetooth = false) =>
        new() { Id = id, FriendlyName = name, Kind = kind, State = state, IsVirtual = isVirtual, IsBluetooth = bluetooth };

    /// <summary>The user's PC: Bluetooth headphones, the optical output, the VB-Audio cable (both endpoints), CABLE A, a mic.</summary>
    private static List<AudioDeviceInfo> Machine() => new()
    {
        Dev(Headphones, HeadphonesName, bluetooth: true),
        Dev(Digital, DigitalName),
        Dev(Speakers, "Speakers (Realtek(R) Audio)"),
        Dev(CableIn, "CABLE Input (VB-Audio Virtual Cable)", isVirtual: true),
        Dev(Cable16, "CABLE In 16ch (VB-Audio Virtual Cable)", isVirtual: true),
        Dev(CableRenamed, "Audio Controller (internal) (VB-Audio Virtual Cable)"), // renamed, enumerator unknown → not flagged virtual
        Dev(CableA, "CABLE-A Input (VB-Audio Cable A)", isVirtual: true),
        Dev(Monitor, "LG HDR 4K (NVIDIA High Definition Audio)", state: DeviceActivity.Unplugged),
        Dev(Mic, "Microphone (USB Mic)", DeviceKind.Capture),
    };

    private static AppSettings Settings(params MirrorTarget[] targets) => new()
    {
        HeadsetDeviceId = Headphones,
        HeadsetDeviceName = HeadphonesName,
        MirrorEnabled = true,
        MirrorTargets = targets.ToList(),
    };

    private static MirrorTarget Target(string id, string? name = null, bool enabled = true, int delay = 0, int volume = 100) =>
        new() { DeviceId = id, DeviceName = name, Enabled = enabled, DelayMs = delay, VolumePercent = volume };

    // ---------------------------------------------------------------- which devices are offered

    [Fact]
    public void TargetChoices_AreConnectedRealOutputs_OtherThanTheHeadset()
    {
        var choices = MirrorPlanner.TargetChoices(Machine(), Headphones);

        Assert.Equal(new[] { Digital, Speakers }, choices.Select(d => d.Id));
    }

    [Theory]
    [InlineData(Headphones, "headset itself")]
    [InlineData(CableIn, "virtual mic feed")]
    [InlineData(Cable16, "virtual mic feed")]
    [InlineData(CableRenamed, "virtual mic feed")]
    [InlineData(CableA, "virtual devices")]
    [InlineData(Monitor, "not connected")]
    [InlineData(Mic, "not a playback device")]
    public void RejectReason_NamesWhyADeviceIsNeverATarget(string id, string reason)
    {
        var device = Machine().Single(d => d.Id == id);

        Assert.Contains(reason, MirrorPlanner.RejectReason(device, Headphones));
    }

    [Fact]
    public void RejectReason_TheVirtualMicOutputOverride_IsExcludedToo()
    {
        var digital = Machine().Single(d => d.Id == Digital);

        Assert.Null(MirrorPlanner.RejectReason(digital, Headphones));
        Assert.Contains("virtual mic output", MirrorPlanner.RejectReason(digital, Headphones, virtualMicOutputId: Digital));
    }

    // ---------------------------------------------------------------- plan

    [Fact]
    public void Plan_TickedDigitalOutput_IsCopiedFromTheHeadset_WithItsDelayAndVolume()
    {
        var s = Settings(Target(Digital, DigitalName, delay: 180, volume: 80));

        var plan = MirrorPlanner.Plan(s, Machine());

        Assert.True(plan.Active);
        Assert.Equal(Headphones, plan.SourceId);
        Assert.Equal(HeadphonesName, plan.SourceName);
        Assert.Equal(new MirrorRoute(Digital, DigitalName, 180, 80), Assert.Single(plan.Routes));
        Assert.Empty(plan.Skipped);
        Assert.Equal("", plan.InactiveReason);
    }

    [Fact]
    public void Plan_AnyNumberOfTargets_InTickOrder()
    {
        var s = Settings(Target(Speakers), Target(Digital));

        var plan = MirrorPlanner.Plan(s, Machine());

        Assert.Equal(new[] { Speakers, Digital }, plan.Routes.Select(r => r.DeviceId));
    }

    [Fact]
    public void Plan_DisabledTargets_AreSkippedSilently()
    {
        var s = Settings(Target(Digital, enabled: false), Target(Speakers));

        var plan = MirrorPlanner.Plan(s, Machine());

        Assert.Equal(Speakers, Assert.Single(plan.Routes).DeviceId);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_HeadsetCableVirtualAndUnpluggedTargets_AreNeverPlayedOn_AndSaySo()
    {
        var s = Settings(Target(Headphones), Target(CableIn), Target(Cable16), Target(CableRenamed), Target(CableA), Target(Monitor),
            Target("{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}", "Old speaker"));

        var plan = MirrorPlanner.Plan(s, Machine());

        Assert.False(plan.Active);
        Assert.Empty(plan.Routes);
        Assert.Equal(7, plan.Skipped.Count);
        Assert.Contains("headset itself", plan.Skipped[0].Reason);
        Assert.All(plan.Skipped.Skip(1).Take(3), k => Assert.Contains("virtual mic", k.Reason));
        Assert.Contains("virtual devices", plan.Skipped[4].Reason);
        Assert.Equal("not connected", plan.Skipped[5].Reason);
        Assert.Equal("Old speaker", plan.Skipped[6].DeviceName);
        Assert.StartsWith("Nothing to play on:", plan.InactiveReason);
    }

    [Fact]
    public void Plan_RepeatedIds_ArePlayedOnOnce()
    {
        var s = Settings(Target(Digital, delay: 10), Target(Digital.ToUpperInvariant(), delay: 99));

        var plan = MirrorPlanner.Plan(s, Machine());

        Assert.Equal(10, Assert.Single(plan.Routes).DelayMs);
    }

    [Fact]
    public void Plan_ClampsDelayAndVolume()
    {
        var s = Settings(Target(Digital, delay: 9000, volume: 300), Target(Speakers, delay: -5, volume: -1));

        var plan = MirrorPlanner.Plan(s, Machine());

        Assert.Equal((500, 100), (plan.Routes[0].DelayMs, plan.Routes[0].VolumePercent));
        Assert.Equal((0, 0), (plan.Routes[1].DelayMs, plan.Routes[1].VolumePercent));
    }

    [Fact]
    public void Plan_InactiveReasons()
    {
        var off = Settings(Target(Digital));
        off.MirrorEnabled = false;
        Assert.Equal(MirrorPlanner.OffText, MirrorPlanner.Plan(off, Machine()).InactiveReason);
        Assert.False(MirrorPlanner.Plan(off, Machine()).Active);

        Assert.Equal(MirrorPlanner.NothingTickedText, MirrorPlanner.Plan(Settings(), Machine()).InactiveReason);

        var noHeadset = Settings(Target(Digital));
        noHeadset.HeadsetDeviceId = null;
        Assert.Equal(MirrorPlanner.PickHeadsetText, MirrorPlanner.Plan(noHeadset, Machine()).InactiveReason);

        var devices = Machine();
        devices[0] = Dev(Headphones, HeadphonesName, state: DeviceActivity.Unplugged, bluetooth: true);
        var missing = MirrorPlanner.Plan(Settings(Target(Digital)), devices);
        Assert.False(missing.Active);
        Assert.Null(missing.SourceId);
        Assert.Equal(MirrorPlanner.HeadsetMissingText, missing.InactiveReason);
    }

    [Fact]
    public void Plan_UsesTheRoutingPlansHeadset_AndExcludesTheVirtualMicOutput()
    {
        var s = Settings(Target(Digital), Target(Speakers));
        var routing = new RoutingPlan { HeadsetId = Headphones, VirtualMicOutputId = Speakers };

        var plan = MirrorPlanner.Plan(s, Machine(), routing);

        Assert.Equal(Digital, Assert.Single(plan.Routes).DeviceId);
        Assert.Contains("virtual mic output", Assert.Single(plan.Skipped).Reason);

        var gone = MirrorPlanner.Plan(s, Machine(), new RoutingPlan { HeadsetId = null, HeadsetMissing = true });
        Assert.False(gone.Active);
        Assert.Equal(MirrorPlanner.HeadsetMissingText, gone.InactiveReason);
    }

    [Fact]
    public void Plan_EqualizerApoOnAnExtraOutput_IsNotedAsNeutral()
    {
        var apo = new EqualizerApoState { Installed = true, AttachedEndpointIds = new[] { Headphones, Digital } };

        var plan = MirrorPlanner.Plan(Settings(Target(Digital)), Machine(), apo: apo);

        var note = Assert.Single(plan.Notes);
        Assert.Contains(DigitalName, note);
        Assert.Contains("neutral", note);
    }

    [Fact]
    public void Plan_NeverModifiesTheSettings()
    {
        var s = Settings(Target(Digital, delay: 9000), Target(Digital), null!);
        var before = System.Text.Json.JsonSerializer.Serialize(s.MirrorTargets);

        MirrorPlanner.Plan(s, Machine());

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(s.MirrorTargets));
        Assert.Equal(3, s.MirrorTargets.Count);
    }

    // ---------------------------------------------------------------- stored settings

    [Fact]
    public void AppSettings_MirrorDefaults_OffAndEmpty()
    {
        var s = new AppSettings();

        Assert.False(s.MirrorEnabled);
        Assert.NotNull(s.MirrorTargets);
        Assert.Empty(s.MirrorTargets);
        var t = new MirrorTarget();
        Assert.True(t.Enabled);
        Assert.Equal(0, t.DelayMs);
        Assert.Equal(100, t.VolumePercent);
    }

    [Fact]
    public void Normalize_RepairsNullsDuplicatesAndRanges_KeepsValidEntries()
    {
        var s = new AppSettings
        {
            MirrorTargets = new List<MirrorTarget>
            {
                null!,
                Target(" " + Digital + " ", "  " + DigitalName + "  ", delay: 600, volume: 120),
                Target(Digital.ToUpperInvariant(), delay: 5),
                Target("   "),
                Target(Speakers, name: "   ", enabled: false, delay: -1, volume: -9),
            },
        };

        Assert.True(MirrorPlanner.Normalize(s));

        Assert.Equal(2, s.MirrorTargets.Count);
        Assert.Equal((Digital, DigitalName, 500, 100), (s.MirrorTargets[0].DeviceId, s.MirrorTargets[0].DeviceName, s.MirrorTargets[0].DelayMs, s.MirrorTargets[0].VolumePercent));
        Assert.Equal((Speakers, (string?)null, false, 0, 0), (s.MirrorTargets[1].DeviceId, s.MirrorTargets[1].DeviceName, s.MirrorTargets[1].Enabled, s.MirrorTargets[1].DelayMs, s.MirrorTargets[1].VolumePercent));
        Assert.False(MirrorPlanner.Normalize(s)); // idempotent

        var missing = new AppSettings { MirrorTargets = null! };
        Assert.True(MirrorPlanner.Normalize(missing));
        Assert.Empty(missing.MirrorTargets);
    }

    [Fact]
    public void GetOrAdd_AddsOnce_AndRefreshesTheName()
    {
        var s = new AppSettings();

        var a = MirrorPlanner.GetOrAdd(s, Digital, "Old name");
        var b = MirrorPlanner.GetOrAdd(s, Digital.ToUpperInvariant(), DigitalName);

        Assert.Same(a, b);
        Assert.Single(s.MirrorTargets);
        Assert.Equal(DigitalName, a.DeviceName);
        Assert.True(a.Enabled);
        Assert.Same(a, MirrorPlanner.Find(s, Digital));
        Assert.Null(MirrorPlanner.Find(s, Speakers));
    }

    // ---------------------------------------------------------------- re-plugged / re-paired outputs

    [Fact]
    public void Readopt_TargetWhoseIdVanished_FollowsTheSameNameOntoTheNewId()
    {
        var s = Settings(Target(Digital, DigitalName, delay: 180, volume: 70));
        var devices = Machine();
        devices[1] = Dev(NewDigital, DigitalName); // Windows created a new endpoint (other USB port)

        Assert.True(RoutingPlanner.ReadoptRenamedIds(s, devices));

        var t = Assert.Single(s.MirrorTargets);
        Assert.Equal(NewDigital, t.DeviceId);
        Assert.Equal((180, 70), (t.DelayMs, t.VolumePercent)); // the user's delay and volume are kept
        Assert.Equal(NewDigital, Assert.Single(MirrorPlanner.Plan(s, devices).Routes).DeviceId);
    }

    [Fact]
    public void Readopt_TargetStillListed_OnlyRefreshesItsName()
    {
        var s = Settings(Target(Digital, "Old optical name"));

        Assert.True(RoutingPlanner.ReadoptRenamedIds(s, Machine()));
        Assert.Equal((Digital, DigitalName), (s.MirrorTargets[0].DeviceId, s.MirrorTargets[0].DeviceName));
        Assert.False(RoutingPlanner.ReadoptRenamedIds(s, Machine()));

        // Unplugged but still listed: kept as is (it comes back with the same id).
        var devices = Machine();
        devices[1] = Dev(Digital, DigitalName, state: DeviceActivity.Unplugged);
        Assert.False(RoutingPlanner.ReadoptRenamedIds(s, devices));
        Assert.Equal(Digital, s.MirrorTargets[0].DeviceId);
    }

    [Fact]
    public void Readopt_Target_AmbiguousVirtualOrMissingNames_ChangeNothing()
    {
        var gone = "{0.0.0.00000000}.{bbbbbbbb-0000-0000-0000-00000000000b}";

        var ambiguous = Machine();
        ambiguous.Add(Dev(NewDigital, DigitalName));
        ambiguous.Add(Dev("{0.0.0.00000000}.{cccccccc-0000-0000-0000-00000000000c}", DigitalName));
        var s1 = Settings(Target(gone, DigitalName));
        Assert.False(RoutingPlanner.ReadoptRenamedIds(s1, ambiguous));
        Assert.Equal(gone, s1.MirrorTargets[0].DeviceId);

        var s2 = Settings(Target(gone, "CABLE Input (VB-Audio Virtual Cable)")); // never onto the cable
        Assert.False(RoutingPlanner.ReadoptRenamedIds(s2, Machine()));
        Assert.Equal(gone, s2.MirrorTargets[0].DeviceId);

        var s3 = Settings(Target(gone, name: null));
        Assert.False(RoutingPlanner.ReadoptRenamedIds(s3, Machine()));
    }

    [Fact]
    public void Readopt_Target_NeverOntoTheHeadset_OrOntoAnotherTargetsDevice()
    {
        var gone = "{0.0.0.00000000}.{bbbbbbbb-0000-0000-0000-00000000000b}";

        var s1 = Settings(Target(gone, HeadphonesName));
        RoutingPlanner.ReadoptRenamedIds(s1, Machine());
        Assert.Equal(gone, s1.MirrorTargets[0].DeviceId);

        var s2 = Settings(Target(Digital, DigitalName), Target(gone, DigitalName));
        RoutingPlanner.ReadoptRenamedIds(s2, Machine());
        Assert.Equal(new[] { Digital, gone }, s2.MirrorTargets.Select(t => t.DeviceId));
    }

    [Fact]
    public void Readopt_HeadsetAndTarget_RepairedTogether_BothFollow()
    {
        var newHeadphones = "{0.0.0.00000000}.{dddddddd-0000-0000-0000-00000000000d}";
        var s = Settings(Target(Digital, DigitalName));
        var devices = Machine();
        devices[0] = Dev(newHeadphones, HeadphonesName, bluetooth: true);
        devices[1] = Dev(NewDigital, DigitalName);

        Assert.True(RoutingPlanner.ReadoptRenamedIds(s, devices));

        Assert.Equal(newHeadphones, s.HeadsetDeviceId);
        Assert.Equal(NewDigital, s.MirrorTargets[0].DeviceId);
        var plan = MirrorPlanner.Plan(s, devices);
        Assert.Equal(newHeadphones, plan.SourceId);
        Assert.Equal(NewDigital, Assert.Single(plan.Routes).DeviceId);
    }
}
