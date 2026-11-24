using AudioController.Core.Devices;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The routing rules behind the two-pick Setup (microphone + headset) and "EQ everything I hear". Fixtures are the
/// user's real machine as measured (names, enumerators, container ids and endpoint ids): since Voicemod was
/// uninstalled there is no spare virtual playback device, so the router path cannot run there; machines that do have
/// one (the old Voicemod setup) are covered by <see cref="WithVoicemod"/>.
/// </summary>
public sealed class RoutingPlannerTests
{
    // ---------------------------------------------------------------- the user's real devices (current)

    private const string CrusherContainer = "{8a0595a4-3da3-5247-ac3c-a0dd01591456}";
    private const string Internal = "{00000000-0000-0000-ffff-ffffffffffff}";

    private const string CableInId = "{0.0.0.00000000}.{5e7ce607-be51-4e86-8e6c-80f7903a033e}";
    private const string Cable16Id = "{0.0.0.00000000}.{95350cad-93e1-48da-88e1-bf7af96ed163}";
    private const string HeadphonesId = "{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string RealtekId = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}";
    private const string LgHdId = "{0.0.0.00000000}.{95ae7c56-8bcc-4d6d-9562-c4abde53b4c6}";
    private const string LgUltraId = "{0.0.0.00000000}.{e4c067f8-1497-4c89-b50b-ee064eb5bd34}";
    private const string CableOutId = "{0.0.1.00000000}.{197f05dd-3634-40fe-900a-a1ab49f285b5}";
    private const string HeadsetMicId = "{0.0.1.00000000}.{bf75d670-a9c6-406d-8aa9-623656457f84}";

    // the old Voicemod devices (uninstalled since), for machines that do have a spare virtual playback device
    private const string DummyId = "{0.0.0.00000000}.{d0d0d0d0-0000-0000-0000-000000000001}";
    private const string VoicemodMicId = "{0.0.1.00000000}.{d0d0d0d0-0000-0000-0000-000000000009}";

    private static AudioDeviceInfo Dev(
        string id, string name, DeviceKind kind, string enumerator, string? container,
        DeviceActivity state = DeviceActivity.Active) =>
        new()
        {
            Id = id,
            FriendlyName = name,
            Kind = kind,
            State = state,
            IsBluetooth = BluetoothDetector.IsBluetooth(enumerator),
            IsVirtual = VirtualDeviceDetector.IsVirtual(enumerator, name),
            ContainerId = container,
        };

    private static AudioDeviceInfo Out(string id, string name, string enumerator, string? container, DeviceActivity state = DeviceActivity.Active) =>
        Dev(id, name, DeviceKind.Playback, enumerator, container, state);

    private static AudioDeviceInfo In(string id, string name, string enumerator, string? container, DeviceActivity state = DeviceActivity.Active) =>
        Dev(id, name, DeviceKind.Capture, enumerator, container, state);

    private static readonly AudioDeviceInfo CableIn = Out(CableInId, "CABLE Input (VB-Audio Virtual Cable)", "ROOT", Internal);
    private static readonly AudioDeviceInfo Cable16 = Out(Cable16Id, "CABLE In 16ch (VB-Audio Virtual Cable)", "ROOT", Internal);
    private static readonly AudioDeviceInfo Headphones = Out(HeadphonesId, "Headphones (Crusher ANC 2)", "BTHENUM", CrusherContainer);
    private static readonly AudioDeviceInfo Realtek = Out(RealtekId, "Digital Output (Realtek USB2.0 Audio)", "USB", Internal);
    private static readonly AudioDeviceInfo LgHd = Out(LgHdId, "LG FULL HD (NVIDIA High Definition Audio)", "HDAUDIO", "{f9f942c5-9991-5135-a31c-0e7a1893fcf3}");
    private static readonly AudioDeviceInfo LgUltra = Out(LgUltraId, "LG ULTRAGEAR+ (NVIDIA High Definition Audio)", "HDAUDIO", "{7fbcfbd5-e0a9-5a6a-b4a2-b77262724ccb}");
    private static readonly AudioDeviceInfo CableOut = In(CableOutId, "CABLE Output (VB-Audio Virtual Cable)", "ROOT", Internal);
    private static readonly AudioDeviceInfo HeadsetMic = In(HeadsetMicId, "Headset (Crusher ANC 2)", "BTHHFENUM", CrusherContainer);

    private static readonly AudioDeviceInfo Dummy = Out(DummyId, "Dummy Output (Voicemod)", "ROOT", Internal);
    private static readonly AudioDeviceInfo VoicemodMic = In(VoicemodMicId, "Microphone (Voicemod)", "ROOT", Internal);

    /// <summary>The user's machine today: no Voicemod, Windows default = the Crusher headphones.</summary>
    private static List<AudioDeviceInfo> RealMachine() =>
        new() { CableIn, Cable16, Headphones, Realtek, LgHd, LgUltra, CableOut, HeadsetMic };

    /// <summary>The same machine while Voicemod was installed (a spare virtual playback device exists).</summary>
    private static List<AudioDeviceInfo> WithVoicemod()
    {
        var list = RealMachine();
        list.Insert(0, Dummy);
        list.Add(VoicemodMic);
        return list;
    }

    /// <summary>Equalizer APO attached to the given endpoints.</summary>
    private static EqualizerApoState Apo(params string[] attached) => new()
    {
        Installed = true,
        InstallPath = @"C:\Program Files\EqualizerAPO",
        ConfigPath = @"C:\Program Files\EqualizerAPO\config",
        AttachedEndpointIds = attached,
    };

    /// <summary>The user's picks: mic = Crusher headset mic, headset = Crusher headphones.</summary>
    private static AppSettings UserSettings(bool? eqAll = null) => new()
    {
        InputDeviceId = HeadsetMicId,
        HeadsetDeviceId = HeadphonesId,
        OutputDeviceId = CableInId,
        MonitorDeviceId = HeadphonesId,
        MonitorEnabled = true,
        EqAllAudio = eqAll,
    };

    private static List<AudioDeviceInfo> Replace(List<AudioDeviceInfo> list, AudioDeviceInfo old, AudioDeviceInfo replacement)
    {
        list[list.IndexOf(old)] = replacement;
        return list;
    }

    // ---------------------------------------------------------------- the current machine (no Voicemod)

    [Fact]
    public void Plan_CurrentMachine_WiresMicHeadsetMonitorAndCable()
    {
        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId);

        Assert.Equal(HeadsetMicId, plan.MicInputId);
        Assert.Equal(CableInId, plan.VirtualMicOutputId);
        Assert.Equal(CableOutId, plan.VirtualMicCaptureId);
        Assert.Equal(HeadphonesId, plan.MonitorOutputId);
        Assert.Equal(HeadphonesId, plan.HeadsetId);
        Assert.False(plan.HeadsetMissing);
        Assert.Null(plan.FallbackOutputId);
        Assert.False(plan.NeedsSetup);
        Assert.False(plan.WindowsDefaultIsVirtualMic);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Plan_CurrentMachine_HasNoInlet_SoTheRouterPathIsDisabledWithGuidance()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), RealMachine(), HeadphonesId);

        Assert.Null(plan.InletId);
        Assert.False(plan.InletAvailable);
        Assert.Equal(EqAllMechanism.None, plan.EqAllMechanism);
        Assert.False(plan.EqAllActive);
        Assert.False(plan.RoutingActive);
        Assert.Null(plan.RouterOutputId);
        Assert.Equal(RoutingPlanner.NoInletText, plan.EqAllStatusText);
        Assert.Contains("Equalizer APO", plan.EqAllStatusText);
        Assert.False(plan.ApoActive);
        Assert.False(plan.ShowApoDeviceSelectorHint); // not installed
        Assert.Empty(plan.Warnings);                  // the guidance sits under the checkbox, not in the warnings
    }

    [Fact]
    public void Plan_CurrentMachine_NeedsNoWindowsDefaultSwitching()
    {
        foreach (bool? eqAll in new bool?[] { null, false, true })
        {
            var plan = RoutingPlanner.Plan(UserSettings(eqAll), RealMachine(), HeadphonesId);

            Assert.Null(plan.DesiredWindowsDefaultOutputId);
            Assert.Equal(HeadphonesId, plan.TargetWindowsDefaultOutputId);
        }
    }

    [Fact]
    public void Plan_CurrentMachine_CrusherMicAndHeadphones_AreBluetoothCallMode()
    {
        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId);

        Assert.True(plan.MicSharesHeadset);
        Assert.True(plan.BluetoothCallMode);
    }

    [Fact]
    public void Choices_CurrentMachine_OfferOnlyRealDevices()
    {
        var mics = RoutingPlanner.MicrophoneChoices(RealMachine()).Select(d => d.FriendlyName).ToList();
        var headsets = RoutingPlanner.HeadsetChoices(RealMachine()).Select(d => d.FriendlyName).ToList();

        Assert.Equal(new[] { "Headset (Crusher ANC 2)" }, mics);
        Assert.Equal(
            new[]
            {
                "Headphones (Crusher ANC 2)", "Digital Output (Realtek USB2.0 Audio)",
                "LG FULL HD (NVIDIA High Definition Audio)", "LG ULTRAGEAR+ (NVIDIA High Definition Audio)",
            },
            headsets);
        Assert.DoesNotContain(headsets, n => n.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Choices_SkipInactiveDevices()
    {
        Assert.Empty(RoutingPlanner.MicrophoneChoices(CrusherDisconnected()));
        Assert.DoesNotContain(RoutingPlanner.HeadsetChoices(CrusherDisconnected()), d => d.Id == HeadphonesId);
    }

    // ---------------------------------------------------------------- Equalizer APO

    [Fact]
    public void Plan_ApoOnTheHeadphones_IsOffered_ButStaysOffUntilTicked()
    {
        // Never chosen: Equalizer APO being on the headset is not consent to re-EQ (and pre-amp) all PC audio.
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: null), RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.True(plan.ApoInstalled);
        Assert.True(plan.ApoAttachedToHeadset);
        Assert.Equal(EqAllMechanism.EqualizerApo, plan.EqAllMechanism); // the checkbox is enabled…
        Assert.False(plan.EqAllRequested);                            // …but unticked
        Assert.False(plan.EqAllActive);
        Assert.False(plan.ApoActive);                                 // nothing is written into Equalizer APO
        Assert.False(plan.MonitorEqBypass);
        Assert.Equal(RoutingPlanner.ApoAvailableText, plan.EqAllStatusText);
        Assert.Contains("tick", plan.EqAllStatusText);
        Assert.False(plan.ShowApoDeviceSelectorHint);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
        Assert.Empty(plan.ApoInfoNotes);
    }

    [Fact]
    public void Plan_ApoOnTheHeadphones_Ticked_EqualizesEverything_WithoutTouchingTheWindowsDefault()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.Equal(EqAllMechanism.EqualizerApo, plan.EqAllMechanism);
        Assert.True(plan.EqAllRequested);
        Assert.True(plan.EqAllActive);
        Assert.True(plan.ApoActive);
        Assert.Equal(RoutingPlanner.ApoViaText, plan.EqAllStatusText);
        Assert.False(plan.RoutingActive);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
        Assert.Equal(HeadphonesId, plan.TargetWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_ApoOnTheHeadphones_LegacySystemEqUser_DefaultsOn()
    {
        // A pre-1.4 user who had opted in to System EQ keeps "EQ everything" — now through Equalizer APO.
        var s = UserSettings(eqAll: null);
        s.SystemEqEnabled = true;

        var plan = RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.True(plan.EqAllRequested);
        Assert.True(plan.ApoActive);
        Assert.Equal(RoutingPlanner.ApoViaText, plan.EqAllStatusText);
    }

    [Fact]
    public void Plan_ApoOnTheHeadphones_MonitorOnTheSameHeadset_BypassesTheMonitorEq()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.True(plan.MonitorEqBypass);
        Assert.Equal(HeadphonesId, plan.MonitorOutputId);
        Assert.Equal(CableInId, plan.VirtualMicOutputId); // the virtual-mic bus keeps the in-app EQ
    }

    [Fact]
    public void Plan_ApoOnTheHeadphones_MonitorElsewhere_KeepsTheMonitorEq()
    {
        var s = UserSettings(eqAll: true);
        s.MonitorDeviceId = LgHdId;

        var plan = RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.True(plan.ApoActive);
        Assert.Equal(LgHdId, plan.MonitorOutputId);
        Assert.False(plan.MonitorEqBypass);
    }

    [Fact]
    public void Plan_ApoOnTheHeadphones_SwitchedOff_IsNeutral_AndKeepsTheMonitorEq()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: false), RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.Equal(EqAllMechanism.EqualizerApo, plan.EqAllMechanism); // available…
        Assert.False(plan.EqAllRequested);                            // …but not wanted
        Assert.False(plan.EqAllActive);
        Assert.False(plan.ApoActive);
        Assert.False(plan.MonitorEqBypass);
    }

    [Fact]
    public void Plan_ApoOnlyOnTheHandsFreeMic_ShowsTheDeviceSelectorHint_AndAnInfoNote()
    {
        // How the user's PC was set up at first: Equalizer APO ticked on the Crusher's hands-free MIC by mistake.
        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId, Apo(HeadsetMicId));

        Assert.True(plan.ApoInstalled);
        Assert.False(plan.ApoAttachedToHeadset);
        Assert.Equal(EqAllMechanism.None, plan.EqAllMechanism);
        Assert.False(plan.ApoActive);
        Assert.False(plan.EqAllActive);
        Assert.True(plan.ShowApoDeviceSelectorHint);
        Assert.Equal(
            "Equalizer APO isn't attached to Headphones (Crusher ANC 2). Open its Device Selector, tick Headphones (Crusher ANC 2) " +
            "under Playback devices, then restart the PC.",
            plan.EqAllStatusText);
        Assert.Equal(new[] { HeadsetMicId }, plan.ApoOtherAttachmentIds);
        Assert.Equal(
            new[] { "Equalizer APO is also attached to Headset (Crusher ANC 2). Audio Controller keeps it neutral there; you can untick it in Device Selector." },
            plan.ApoInfoNotes);
        Assert.False(plan.MonitorEqBypass);
    }

    [Theory]
    [InlineData(CableInId, "CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData(Cable16Id, "CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData(CableOutId, "CABLE Output (VB-Audio Virtual Cable)")]
    public void Plan_ApoOnTheVirtualCable_GetsAnInfoNote(string cableId, string name)
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), RealMachine(), HeadphonesId, Apo(HeadphonesId, cableId));

        Assert.True(plan.ApoActive); // the headset still works
        Assert.Equal(new[] { cableId }, plan.ApoOtherAttachmentIds);
        Assert.Single(plan.ApoInfoNotes);
        Assert.Contains(name, plan.ApoInfoNotes[0]);
    }

    [Fact]
    public void Plan_ApoOnAnotherRealSpeaker_IsNotFlagged()
    {
        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId, Apo(HeadphonesId, LgHdId));

        Assert.Empty(plan.ApoOtherAttachmentIds);
        Assert.Empty(plan.ApoInfoNotes);
    }

    [Fact]
    public void Plan_ApoOnAnUnknownCaptureEndpoint_IsFlaggedByItsIdPrefix()
    {
        const string gone = "{0.0.1.00000000}.{deadbeef-0000-0000-0000-000000000001}";
        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId, Apo(HeadphonesId, gone));

        Assert.Equal(new[] { gone }, plan.ApoOtherAttachmentIds);
        Assert.Contains("a recording device", plan.ApoInfoNotes[0]);
    }

    [Fact]
    public void Plan_ApoNotInstalled_IgnoresAttachmentLists()
    {
        var notInstalled = new EqualizerApoState { Installed = false, AttachedEndpointIds = new[] { HeadphonesId } };

        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId, notInstalled);

        Assert.False(plan.ApoInstalled);
        Assert.False(plan.ApoAttachedToHeadset);
        Assert.False(plan.ApoActive);
        Assert.Empty(plan.ApoInfoNotes);
    }

    [Fact]
    public void Plan_ApoWinsOverTheRouter_WhenBothCouldWork()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), HeadphonesId, Apo(HeadphonesId));

        Assert.Equal(DummyId, plan.InletId); // an inlet exists…
        Assert.Equal(EqAllMechanism.EqualizerApo, plan.EqAllMechanism); // …but Equalizer APO is used
        Assert.True(plan.ApoActive);
        Assert.False(plan.RoutingActive);
        Assert.Null(plan.RouterOutputId);
        Assert.Null(plan.DesiredWindowsDefaultOutputId); // Windows keeps playing straight to the headset
    }

    [Fact]
    public void Plan_ApoOnTheHeadset_ButHeadsetDisconnected_DoesNothing()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), CrusherDisconnected(), RealtekId, Apo(HeadphonesId));

        Assert.True(plan.HeadsetMissing);
        Assert.Equal(EqAllMechanism.None, plan.EqAllMechanism);
        Assert.False(plan.ApoActive);
        Assert.False(plan.MonitorEqBypass);
        Assert.Equal("Your headset isn't connected.", plan.EqAllStatusText);
    }

    [Fact]
    public void Plan_NoHeadsetPicked_AsksForItFirst()
    {
        var s = UserSettings();
        s.HeadsetDeviceId = null;

        var plan = RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.Equal(EqAllMechanism.None, plan.EqAllMechanism);
        Assert.Equal(RoutingPlanner.PickHeadsetFirstText, plan.EqAllStatusText);
        Assert.False(plan.ApoActive);
    }

    [Fact]
    public void Plan_ApoEndpointMatching_IgnoresCase()
    {
        var plan = RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId, Apo(HeadphonesId.ToUpperInvariant()));

        Assert.True(plan.ApoAttachedToHeadset);
    }

    // ---------------------------------------------------------------- the router path (machines with a spare virtual device)

    [Fact]
    public void Plan_WithVoicemod_PicksDummyOutputAsInlet_NeverTheVirtualMicCable()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), HeadphonesId);

        Assert.Equal(DummyId, plan.InletId);
        Assert.NotEqual(CableInId, plan.InletId);
        Assert.NotEqual(Cable16Id, plan.InletId);
        Assert.True(plan.InletAvailable);
        Assert.Equal(EqAllMechanism.Router, plan.EqAllMechanism);
        Assert.True(plan.RoutingActive);
        Assert.Equal(HeadphonesId, plan.RouterOutputId);
        Assert.Equal(RoutingPlanner.RouterViaText("Dummy Output (Voicemod)"), plan.EqAllStatusText);
        Assert.False(plan.MonitorEqBypass); // the monitor plays straight to the headset, not through the router
    }

    [Fact]
    public void Plan_WithVoicemod_RouterIsOffByDefault()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: null), WithVoicemod(), HeadphonesId);

        Assert.Equal(EqAllMechanism.Router, plan.EqAllMechanism);
        Assert.False(plan.EqAllRequested);
        Assert.False(plan.RoutingActive);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_LegacySystemEqOn_DefaultsTheRouterOn()
    {
        var s = UserSettings(eqAll: null);
        s.SystemEqEnabled = true;

        Assert.True(RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).RoutingActive);
    }

    [Fact]
    public void Plan_PrefersCableA_ThenCableB_ThenHiFi_ThenDummyOutput()
    {
        var cableA = Out("{0.0.0.00000000}.{aaaa0000-0000-0000-0000-00000000000a}", "CABLE-A Input (VB-Audio Cable A)", "ROOT", Internal);
        var cableA16 = Out("{0.0.0.00000000}.{aaaa0016-0000-0000-0000-00000000000a}", "CABLE-A In 16ch (VB-Audio Cable A)", "ROOT", Internal);
        var cableB = Out("{0.0.0.00000000}.{bbbb0000-0000-0000-0000-00000000000b}", "CABLE-B Input (VB-Audio Cable B)", "ROOT", Internal);
        var hifi = Out("{0.0.0.00000000}.{f1f10000-0000-0000-0000-00000000000f}", "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)", "ROOT", Internal);

        var devices = WithVoicemod();
        devices.AddRange(new[] { hifi, cableB, cableA16, cableA });

        Assert.Equal(cableA.Id, RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).InletId); // 2ch before 16ch
        devices.Remove(cableA);
        Assert.Equal(cableA16.Id, RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).InletId);
        devices.Remove(cableA16);
        Assert.Equal(cableB.Id, RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).InletId);
        devices.Remove(cableB);
        Assert.Equal(hifi.Id, RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).InletId);
        devices.Remove(hifi);
        Assert.Equal(DummyId, RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).InletId);
    }

    [Fact]
    public void Plan_InletChoice_IsDeterministic_WhateverTheDeviceOrder()
    {
        var line2 = Out("{0.0.0.00000000}.{0e0e0e0e-0000-0000-0000-0000000000e2}", "Line 2 (Virtual Audio Cable)", "ROOT", Internal);
        var line1 = Out("{0.0.0.00000000}.{0e0e0e0e-0000-0000-0000-0000000000e1}", "Line 1 (Virtual Audio Cable)", "ROOT", Internal);
        var forward = RealMachine();
        forward.Add(line2);
        forward.Add(line1);
        var reversed = Enumerable.Reverse(forward).ToList();

        var a = RoutingPlanner.Plan(UserSettings(), forward, HeadphonesId);
        var b = RoutingPlanner.Plan(UserSettings(), reversed, HeadphonesId);

        Assert.Equal(line1.Id, a.InletId); // same tier: by name
        Assert.Equal(a.InletId, b.InletId);

        // same plan apart from the list instances
        Assert.Equal(a, b with { Warnings = a.Warnings, ApoOtherAttachmentIds = a.ApoOtherAttachmentIds, ApoInfoNotes = a.ApoInfoNotes });
    }

    [Fact]
    public void Plan_MixersOtherAppsListenTo_AreNotPickedAutomatically_ButAnAdvancedChoiceIsKept()
    {
        var voicemeeter = Out("{0.0.0.00000000}.{4e4e4e4e-0000-0000-0000-00000000004e}", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", "ROOT", Internal);
        var devices = RealMachine();
        devices.Add(voicemeeter);

        Assert.Null(RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).InletId);

        var chosen = UserSettings();
        chosen.SystemEqSourceDeviceId = voicemeeter.Id;
        Assert.Equal(voicemeeter.Id, RoutingPlanner.Plan(chosen, devices, HeadphonesId).InletId);
    }

    [Fact]
    public void Plan_AdvancedInletChoice_IsIgnoredWhenItIsTheCableOrTheHeadset()
    {
        var s = UserSettings();
        s.SystemEqSourceDeviceId = CableInId;
        Assert.Equal(DummyId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).InletId);

        s.SystemEqSourceDeviceId = Cable16Id;
        Assert.Equal(DummyId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).InletId);

        s.SystemEqSourceDeviceId = HeadphonesId;
        Assert.Equal(DummyId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).InletId);

        s.SystemEqSourceDeviceId = LgHdId; // a real but unused device is a valid (old-style) choice
        Assert.Equal(LgHdId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).InletId);
    }

    [Fact]
    public void Plan_RoutingOn_WantsTheInletAsWindowsDefault()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), HeadphonesId);

        Assert.Equal(DummyId, plan.DesiredWindowsDefaultOutputId);
        Assert.Equal(DummyId, plan.TargetWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_RoutingOn_DefaultAlreadyTheInlet_LeavesItAlone()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), DummyId);

        Assert.Null(plan.DesiredWindowsDefaultOutputId);
        Assert.Equal(DummyId, plan.TargetWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_RoutingOff_DefaultStillOnTheInletTheAppPutItOn_MovesItBackToTheHeadset()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: false), WithVoicemod(), DummyId, windowsDefaultIsAutomatic: true);

        Assert.False(plan.RoutingActive);
        Assert.Null(plan.RouterOutputId);
        Assert.Equal(HeadphonesId, plan.DesiredWindowsDefaultOutputId);
        Assert.Equal(HeadphonesId, plan.TargetWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_RoutingOff_DefaultOnTheInletTheUserChose_IsLeftAlone()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: false), WithVoicemod(), DummyId, windowsDefaultIsAutomatic: false);

        Assert.False(plan.RoutingActive);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
        Assert.Equal(HeadphonesId, plan.TargetWindowsDefaultOutputId); // an explicit action / "Fix it" still goes there
    }

    /// <summary>A machine with VB-Audio CABLE A (a spare virtual playback device the user may feed OBS with).</summary>
    private static readonly AudioDeviceInfo CableA =
        Out("{0.0.0.00000000}.{aaaa0000-0000-0000-0000-00000000000a}", "CABLE-A Input (VB-Audio Cable A)", "ROOT", Internal);

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Plan_EqEverythingOff_UsersDeliberateCableADefault_IsNeverMoved(bool? eqAll)
    {
        // The user's Windows default is CABLE-A Input on purpose (it feeds OBS). CABLE-A is the router's candidate
        // inlet, but the router is off and the app never put the default there: leave it.
        var devices = RealMachine();
        devices.Add(CableA);

        var plan = RoutingPlanner.Plan(UserSettings(eqAll), devices, CableA.Id);

        Assert.Equal(CableA.Id, plan.InletId);
        Assert.False(plan.RoutingActive);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
        Assert.False(plan.WindowsDefaultIsVirtualMic);
    }

    [Fact]
    public void Plan_ApoTakesOver_WhileTheDefaultIsStillOnTheOldInlet_MovesItBackToTheHeadset()
    {
        // The router used to run (System EQ saved on, default left on Dummy Output by a run that did not restore it);
        // now Equalizer APO is on the headset.
        var s = UserSettings(eqAll: true);
        s.SystemEqSourceDeviceId = DummyId;
        s.SystemEqEnabled = true;

        var plan = RoutingPlanner.Plan(s, WithVoicemod(), DummyId, Apo(HeadphonesId));

        Assert.True(plan.ApoActive);
        Assert.False(plan.RoutingActive);
        Assert.Equal(HeadphonesId, plan.DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_ApoActive_DefaultOnASpareDeviceTheUserChose_IsLeftAlone()
    {
        var s = UserSettings(eqAll: true);
        s.SystemEqSourceDeviceId = DummyId; // System EQ never ran (SystemEqEnabled false)

        Assert.Null(RoutingPlanner.Plan(s, WithVoicemod(), DummyId, Apo(HeadphonesId)).DesiredWindowsDefaultOutputId);
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), DummyId, Apo(HeadphonesId), windowsDefaultIsAutomatic: true)
            .DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_RoutingOff_DefaultOnARealDeviceOnceChosenAsSource_IsOnlyMovedWhenTheAppPutItThere()
    {
        var s = UserSettings(eqAll: false);
        s.SystemEqSourceDeviceId = LgUltraId; // an old Advanced choice of a real device as the System EQ source

        // The user's own choice of the LG speakers is respected …
        Assert.Null(RoutingPlanner.Plan(s, RealMachine(), LgUltraId).DesiredWindowsDefaultOutputId);

        // … but a default the app itself switched there while routing is moved back once routing is off.
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, RealMachine(), LgUltraId, windowsDefaultIsAutomatic: true).DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_RoutingOff_AnAutomaticDefaultThatIsNoInlet_IsLeftAlone()
    {
        // Windows picked the LG monitor after another device went away: real speakers are not silent.
        Assert.Null(RoutingPlanner.Plan(UserSettings(eqAll: false), WithVoicemod(), LgHdId, windowsDefaultIsAutomatic: true)
            .DesiredWindowsDefaultOutputId);
    }

    [Theory]
    [InlineData(HeadphonesId)]
    [InlineData(LgHdId)]       // the user's own choice of a real device is respected
    [InlineData(RealtekId)]
    public void Plan_RoutingOff_DefaultAlreadyFine_LeavesItAlone(string currentDefault)
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: false), WithVoicemod(), currentDefault);

        Assert.Null(plan.DesiredWindowsDefaultOutputId);
    }

    [Theory]
    [InlineData(CableInId)]
    [InlineData(Cable16Id)]
    public void Plan_DefaultIsTheVirtualMicCable_IsFlagged_AndLeftForFixIt(string cableId)
    {
        var on = RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), cableId);
        var off = RoutingPlanner.Plan(UserSettings(eqAll: false), WithVoicemod(), cableId);
        var today = RoutingPlanner.Plan(UserSettings(), RealMachine(), cableId);

        Assert.True(on.WindowsDefaultIsVirtualMic);
        Assert.True(off.WindowsDefaultIsVirtualMic);
        Assert.True(today.WindowsDefaultIsVirtualMic);
        Assert.Null(on.DesiredWindowsDefaultOutputId);   // the banner's "Fix it" decides
        Assert.Null(off.DesiredWindowsDefaultOutputId);
        Assert.Null(today.DesiredWindowsDefaultOutputId);
        Assert.Equal(DummyId, on.TargetWindowsDefaultOutputId);         // Fix it with the router on → the inlet
        Assert.Equal(HeadphonesId, off.TargetWindowsDefaultOutputId);   // Fix it with the router off → the headset
        Assert.Equal(HeadphonesId, today.TargetWindowsDefaultOutputId); // Fix it on this PC → the headset
    }

    [Fact]
    public void Plan_DefaultIsTheHeadset_IsNotFlaggedAsVirtualMic()
    {
        Assert.False(RoutingPlanner.Plan(UserSettings(), RealMachine(), HeadphonesId).WindowsDefaultIsVirtualMic);
        Assert.False(RoutingPlanner.Plan(UserSettings(), WithVoicemod(), DummyId).WindowsDefaultIsVirtualMic);
        Assert.False(RoutingPlanner.Plan(UserSettings(), RealMachine(), null).WindowsDefaultIsVirtualMic);
    }

    [Fact]
    public void Plan_NoWindowsDefault_GetsTheTarget()
    {
        Assert.Equal(DummyId, RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), null).DesiredWindowsDefaultOutputId);
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(UserSettings(eqAll: false), WithVoicemod(), null).DesiredWindowsDefaultOutputId);
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(UserSettings(), RealMachine(), null).DesiredWindowsDefaultOutputId);
    }

    // ---------------------------------------------------------------- headset missing (Bluetooth disconnect)

    private static List<AudioDeviceInfo> CrusherDisconnected(List<AudioDeviceInfo>? devices = null)
    {
        devices ??= RealMachine();
        Replace(devices, Headphones, Out(HeadphonesId, "Headphones (Crusher ANC 2)", "BTHENUM", CrusherContainer, DeviceActivity.Unplugged));
        Replace(devices, HeadsetMic, In(HeadsetMicId, "Headset (Crusher ANC 2)", "BTHHFENUM", CrusherContainer, DeviceActivity.Unplugged));
        return devices;
    }

    [Fact]
    public void Plan_HeadsetMissing_StopsTheRoute_AndPicksARealFallback()
    {
        // The router was running: the app had put the default on Dummy Output.
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), CrusherDisconnected(WithVoicemod()), DummyId, windowsDefaultIsAutomatic: true);

        Assert.True(plan.HeadsetMissing);
        Assert.False(plan.RoutingActive);
        Assert.Null(plan.RouterOutputId);
        Assert.Null(plan.HeadsetId);
        Assert.Equal(RealtekId, plan.FallbackOutputId); // first real playback device by name
        Assert.False(RealMachine().Single(d => d.Id == plan.FallbackOutputId).IsVirtual);
        Assert.Equal(RealtekId, plan.DesiredWindowsDefaultOutputId); // never left in silence on the inlet
        Assert.Equal(RealtekId, plan.MonitorOutputId);
        Assert.Contains(plan.Warnings, w => w.Contains("Crusher ANC 2") && w.Contains("isn't connected"));
        Assert.Null(plan.MicInputId); // the headset mic went with it
    }

    [Fact]
    public void Plan_HeadsetMissing_InletLeftOnWhileSystemEqWasOn_IsRescued()
    {
        // Fresh start with the headset off, after a run that ended with the default still on the inlet.
        var s = UserSettings(eqAll: true);
        s.SystemEqEnabled = true;

        Assert.Equal(RealtekId, RoutingPlanner.Plan(s, CrusherDisconnected(WithVoicemod()), DummyId).DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_HeadsetMissing_SpareVirtualDefaultTheUserChose_IsLeftAlone()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: false), CrusherDisconnected(WithVoicemod()), DummyId);

        Assert.True(plan.HeadsetMissing);
        Assert.Equal(RealtekId, plan.FallbackOutputId);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
        Assert.Equal(RealtekId, plan.TargetWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_HeadsetMissing_OnTheCurrentMachine_FallsBackToARealDevice()
    {
        // Windows moved the default to CABLE Input after the headset vanished: nobody chose that.
        var plan = RoutingPlanner.Plan(UserSettings(), CrusherDisconnected(), CableInId);

        Assert.True(plan.HeadsetMissing);
        Assert.True(plan.WindowsDefaultIsVirtualMic);
        Assert.Equal(RealtekId, plan.FallbackOutputId);
        Assert.Equal(RealtekId, plan.DesiredWindowsDefaultOutputId); // always rescued: Discord would hear PC audio
        Assert.Equal(RealtekId, RoutingPlanner.Plan(UserSettings(), CrusherDisconnected(), CableInId, windowsDefaultIsAutomatic: true)
            .DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_HeadsetMissing_KeepsARealDefaultTheUserAlreadyHas()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), CrusherDisconnected(), LgHdId);

        Assert.Equal(LgHdId, plan.FallbackOutputId);
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_HeadsetBack_ResumesRouting()
    {
        var plan = RoutingPlanner.Plan(UserSettings(eqAll: true), WithVoicemod(), RealtekId);

        Assert.False(plan.HeadsetMissing);
        Assert.True(plan.RoutingActive);
        Assert.Equal(DummyId, plan.DesiredWindowsDefaultOutputId);
    }

    // ---------------------------------------------------------------- mic shares the headset (Bluetooth call mode)

    [Fact]
    public void Plan_MicSharesHeadset_FallsBackToTheNameInParentheses_WhenContainerIdsAreUnknown()
    {
        var devices = RealMachine();
        Replace(devices, Headphones, Out(HeadphonesId, "Headphones (Crusher ANC 2)", "BTHENUM", null));
        Replace(devices, HeadsetMic, In(HeadsetMicId, "Headset (Crusher ANC 2)", "BTHHFENUM", null));

        var plan = RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId);

        Assert.True(plan.MicSharesHeadset);
        Assert.True(plan.BluetoothCallMode);
    }

    [Fact]
    public void Plan_MicSharesHeadset_IgnoresTheHandsFreeProfileWordInTheName()
    {
        // Some Windows builds name the HFP endpoint "Headset (Crusher ANC 2 Hands-Free)".
        var devices = RealMachine();
        Replace(devices, Headphones, Out(HeadphonesId, "Headphones (Crusher ANC 2)", "BTHENUM", null));
        Replace(devices, HeadsetMic, In(HeadsetMicId, "Headset (Crusher ANC 2 Hands-Free)", "BTHHFENUM", null));

        Assert.True(RoutingPlanner.Plan(UserSettings(), devices, HeadphonesId).BluetoothCallMode);
    }

    [Fact]
    public void Plan_SeparateUsbMic_DoesNotShareTheHeadset()
    {
        var yeti = In("{0.0.1.00000000}.{7e717e71-0000-0000-0000-0000000000ae}", "Microphone (Yeti Stereo Microphone)", "USB", "{5e5e5e5e-1111-2222-3333-444444444444}");
        var devices = RealMachine();
        devices.Add(yeti);
        var s = UserSettings();
        s.InputDeviceId = yeti.Id;

        var plan = RoutingPlanner.Plan(s, devices, HeadphonesId);

        Assert.Equal(yeti.Id, plan.MicInputId);
        Assert.False(plan.MicSharesHeadset);
        Assert.False(plan.BluetoothCallMode);
    }

    [Fact]
    public void SameGadget_DifferentContainers_WinOverMatchingNames()
    {
        var a = Out("a", "Speakers (USB Audio Device)", "USB", "{11111111-1111-1111-1111-111111111111}");
        var b = In("b", "Microphone (USB Audio Device)", "USB", "{22222222-2222-2222-2222-222222222222}");

        Assert.False(RoutingPlanner.SameGadget(a, b));
    }

    [Fact]
    public void SameGadget_TheSharedInternalContainer_NeverMeansSameGadget()
    {
        // Realtek USB and VB-Audio all report the "internally connected" container on this machine,
        // so the names decide; and two real devices with different containers differ despite equal names.
        Assert.False(RoutingPlanner.SameGadget(Realtek, CableOut));
        Assert.False(RoutingPlanner.SameGadget(LgHd, LgUltra));
        Assert.False(RoutingPlanner.SameContainer(Internal, Internal));
        Assert.False(RoutingPlanner.SameContainer("{00000000-0000-0000-0000-000000000000}", "{00000000-0000-0000-0000-000000000000}"));
        Assert.True(RoutingPlanner.SameContainer(CrusherContainer, "8A0595A4-3DA3-5247-AC3C-A0DD01591456"));
        Assert.False(RoutingPlanner.SameContainer("not a guid", "not a guid"));
    }

    [Fact]
    public void Plan_UsbHeadsetSharingItsMic_IsNotBluetoothCallMode()
    {
        var usbPhones = Out("{0.0.0.00000000}.{a5a5a5a5-0000-0000-0000-0000000000a5}", "Headphones (Arctis 7)", "USB", "{a7a7a7a7-1111-2222-3333-444444444444}");
        var usbMic = In("{0.0.1.00000000}.{a5a5a5a5-0000-0000-0000-0000000000a6}", "Microphone (Arctis 7)", "USB", "{a7a7a7a7-1111-2222-3333-444444444444}");
        var devices = RealMachine();
        devices.Add(usbPhones);
        devices.Add(usbMic);
        var s = UserSettings();
        s.InputDeviceId = usbMic.Id;
        s.HeadsetDeviceId = usbPhones.Id;

        var plan = RoutingPlanner.Plan(s, devices, usbPhones.Id);

        Assert.True(plan.MicSharesHeadset);
        Assert.False(plan.BluetoothCallMode);
    }

    [Theory]
    [InlineData("Headset (Crusher ANC 2)", "Crusher ANC 2")]
    [InlineData("Line (Voicemod Virtual Audio Device (WDM))", "Voicemod Virtual Audio Device (WDM)")]
    [InlineData("Speakers", null)]
    [InlineData("Speakers ()", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParenthesizedName_Extracts(string? name, string? expected)
    {
        Assert.Equal(expected, RoutingPlanner.ParenthesizedName(name));
    }

    // ---------------------------------------------------------------- picks and overrides

    [Fact]
    public void Plan_NothingPicked_NeedsSetup_AndStillFindsTheCable()
    {
        var plan = RoutingPlanner.Plan(new AppSettings(), RealMachine(), HeadphonesId);

        Assert.True(plan.NeedsSetup);
        Assert.Null(plan.MicInputId);
        Assert.Null(plan.HeadsetId);
        Assert.False(plan.HeadsetMissing);
        Assert.Equal(CableInId, plan.VirtualMicOutputId);
        Assert.Equal(HeadphonesId, plan.FallbackOutputId); // the current default is real
        Assert.Null(plan.DesiredWindowsDefaultOutputId);
    }

    [Fact]
    public void Plan_MicSetToCableOutput_IsRejected()
    {
        var s = UserSettings();
        s.InputDeviceId = CableOutId;

        var plan = RoutingPlanner.Plan(s, RealMachine(), HeadphonesId);

        Assert.Null(plan.MicInputId);
        Assert.Contains(plan.Warnings, w => w.Contains("CABLE Output") && w.Contains("virtual microphone itself"));
    }

    [Fact]
    public void Plan_VirtualMicOutputOverride_IsHonoured_UnlessItIsTheHeadset()
    {
        var s = UserSettings();
        s.OutputDeviceId = null;
        Assert.Equal(CableInId, RoutingPlanner.Plan(s, RealMachine(), HeadphonesId).VirtualMicOutputId);

        s.OutputDeviceId = Cable16Id;
        Assert.Equal(Cable16Id, RoutingPlanner.Plan(s, RealMachine(), HeadphonesId).VirtualMicOutputId);

        s.OutputDeviceId = HeadphonesId;
        Assert.Equal(CableInId, RoutingPlanner.Plan(s, RealMachine(), HeadphonesId).VirtualMicOutputId);
    }

    [Fact]
    public void Plan_NoCable_HasNoVirtualMicOutput()
    {
        var devices = RealMachine();
        devices.Remove(CableIn);
        devices.Remove(Cable16);
        var s = UserSettings();
        s.OutputDeviceId = null;

        var plan = RoutingPlanner.Plan(s, devices, HeadphonesId);

        Assert.Null(plan.VirtualMicOutputId);
        Assert.Null(plan.VirtualMicCaptureId);
    }

    [Fact]
    public void Plan_OnlyThe16chEndpointLeft_IsStillTheFeed()
    {
        var devices = RealMachine();
        devices.Remove(CableIn);
        var s = UserSettings();
        s.OutputDeviceId = null;

        Assert.Equal(Cable16Id, RoutingPlanner.Plan(s, devices, HeadphonesId).VirtualMicOutputId);
    }

    [Fact]
    public void Plan_MonitorOverride_IsHonoured_ButNeverTheCableOrTheInlet()
    {
        var s = UserSettings();
        s.MonitorDeviceId = LgHdId;
        Assert.Equal(LgHdId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).MonitorOutputId);

        s.MonitorDeviceId = CableInId;
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).MonitorOutputId);

        s.MonitorDeviceId = Cable16Id;
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).MonitorOutputId);

        s.MonitorDeviceId = DummyId;
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).MonitorOutputId);

        s.MonitorDeviceId = null;
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).MonitorOutputId);
    }

    [Fact]
    public void Plan_RouterOutputOverride_IsHonoured_ButNeverTheInletOrTheCable()
    {
        var s = UserSettings(eqAll: true);
        s.SystemEqOutputDeviceId = LgHdId;
        Assert.Equal(LgHdId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).RouterOutputId);

        s.SystemEqOutputDeviceId = DummyId;
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).RouterOutputId);

        s.SystemEqOutputDeviceId = CableInId;
        Assert.Equal(HeadphonesId, RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId).RouterOutputId);
    }

    [Fact]
    public void Plan_HeadsetSetToTheCable_IsNeverUsed()
    {
        foreach (var cable in new[] { CableInId, Cable16Id })
        {
            var s = UserSettings(eqAll: true);
            s.HeadsetDeviceId = cable;

            var plan = RoutingPlanner.Plan(s, WithVoicemod(), HeadphonesId, Apo(cable));

            Assert.Null(plan.HeadsetId);
            Assert.False(plan.RoutingActive);
            Assert.False(plan.ApoActive);
            Assert.NotEqual(cable, plan.MonitorOutputId);
            Assert.NotEqual(cable, plan.TargetWindowsDefaultOutputId);
        }
    }

    [Fact]
    public void Plan_DoesNotModifyTheSettings()
    {
        var s = UserSettings();
        string Snapshot(AppSettings x) => string.Join("|", x.InputDeviceId, x.HeadsetDeviceId, x.EqAllAudio, x.OutputDeviceId,
            x.MonitorDeviceId, x.MonitorEnabled, x.SystemEqEnabled, x.SystemEqSourceDeviceId, x.SystemEqOutputDeviceId);
        string before = Snapshot(s);

        RoutingPlanner.Plan(s, RealMachine(), CableInId);
        RoutingPlanner.Plan(s, CrusherDisconnected(WithVoicemod()), DummyId, Apo(HeadphonesId, HeadsetMicId));
        RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadphonesId));

        Assert.Equal(before, Snapshot(s));
    }

    // ---------------------------------------------------------------- renamed cable endpoints (rename-proof)

    private const string RenamedFeedName = "Audio Controller (internal) (VB-Audio Virtual Cable)";
    private const string RenamedMicName = "Audio Controller Mic (VB-Audio Virtual Cable)";

    /// <summary>The current machine after the user renamed the VB-Audio endpoints in Windows.</summary>
    private static List<AudioDeviceInfo> RenamedMachine(bool rename16 = false)
    {
        var devices = RealMachine();
        Replace(devices, CableIn, Out(CableInId, RenamedFeedName, "ROOT", Internal));
        Replace(devices, CableOut, In(CableOutId, RenamedMicName, "ROOT", Internal));
        if (rename16)
        {
            Replace(devices, Cable16, Out(Cable16Id, "Spare (VB-Audio Virtual Cable)", "ROOT", Internal));
        }

        return devices;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_RenamedCable_IsStillTheVirtualMic(bool rename16)
    {
        var s = UserSettings();
        s.OutputDeviceId = null; // found automatically

        var plan = RoutingPlanner.Plan(s, RenamedMachine(rename16), HeadphonesId);

        Assert.Equal(CableInId, plan.VirtualMicOutputId);
        Assert.Equal(CableOutId, plan.VirtualMicCaptureId);
        Assert.Equal(HeadsetMicId, plan.MicInputId);
        Assert.Null(plan.InletId); // never the renamed cable or its 16ch endpoint
    }

    [Fact]
    public void Plan_RenamedCable_SavedFeedIsKept()
    {
        var s = UserSettings();
        s.OutputDeviceId = Cable16Id; // an older Advanced choice of the 16-channel endpoint

        Assert.Equal(Cable16Id, RoutingPlanner.Plan(s, RenamedMachine(rename16: true), HeadphonesId).VirtualMicOutputId);
    }

    [Theory]
    [InlineData(CableInId)]
    [InlineData(Cable16Id)]
    public void Plan_RenamedCableAsWindowsDefault_ShowsTheMisrouteBanner(string defaultId)
    {
        var plan = RoutingPlanner.Plan(UserSettings(), RenamedMachine(rename16: true), defaultId);

        Assert.True(plan.WindowsDefaultIsVirtualMic);
        Assert.Equal(HeadphonesId, plan.TargetWindowsDefaultOutputId);
    }

    [Fact]
    public void Choices_RenamedCable_IsNeverOffered()
    {
        var mics = RoutingPlanner.MicrophoneChoices(RenamedMachine(rename16: true)).Select(d => d.Id).ToList();
        var headsets = RoutingPlanner.HeadsetChoices(RenamedMachine(rename16: true)).Select(d => d.Id).ToList();

        Assert.Equal(new[] { HeadsetMicId }, mics);
        Assert.DoesNotContain(CableInId, headsets);
        Assert.DoesNotContain(Cable16Id, headsets);
        Assert.Equal(4, headsets.Count);
    }

    [Fact]
    public void Plan_MicSetToTheRenamedVirtualMic_IsRejected_WithItsNewName()
    {
        var s = UserSettings();
        s.InputDeviceId = CableOutId;

        var plan = RoutingPlanner.Plan(s, RenamedMachine(), HeadphonesId);

        Assert.Null(plan.MicInputId);
        Assert.Contains(plan.Warnings, w => w.Contains("\"Audio Controller Mic\""));
    }

    [Fact]
    public void Plan_ApoOnTheRenamedCable_GetsAnInfoNote()
    {
        var plan = RoutingPlanner.Plan(UserSettings(), RenamedMachine(), HeadphonesId, Apo(HeadphonesId, CableInId));

        Assert.Single(plan.ApoInfoNotes);
        Assert.Contains(RenamedFeedName, plan.ApoInfoNotes[0]);
    }

    // ---------------------------------------------------------------- migration

    /// <summary>The user's real pre-1.4 settings, as measured.</summary>
    private static AppSettings LegacyUserSettings() => new()
    {
        InputDeviceId = HeadsetMicId,
        OutputDeviceId = CableInId,
        MonitorDeviceId = HeadphonesId,
        MonitorEnabled = true,
        SystemEqEnabled = false,
    };

    [Fact]
    public void Migrate_UsersRealSettings_HeadsetFromMonitor()
    {
        var s = LegacyUserSettings();

        bool changed = RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId);

        Assert.True(changed);
        Assert.Equal(HeadphonesId, s.HeadsetDeviceId);
        Assert.Null(s.EqAllAudio);                   // decided by the plan (off: System EQ was off)
        Assert.Equal(HeadsetMicId, s.InputDeviceId); // untouched
        Assert.Equal(CableInId, s.OutputDeviceId);
    }

    [Fact]
    public void Migrate_UsersRealSettings_ThenApoOnTheHeadphones_EqEverythingIsOfferedButOff()
    {
        // The user's real upgrade: System EQ was off and the EQ gains were tuned for the soundboard. The first launch
        // must not silently re-EQ (and pre-amp by up to −24 dB) all PC audio on the Crusher.
        var s = LegacyUserSettings();
        RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId);

        var plan = RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadphonesId));
        Assert.Equal(EqAllMechanism.EqualizerApo, plan.EqAllMechanism);
        Assert.False(plan.EqAllRequested);
        Assert.False(plan.ApoActive);
        Assert.Equal(RoutingPlanner.ApoAvailableText, plan.EqAllStatusText);
        Assert.False(RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadsetMicId)).ApoActive);
    }

    [Fact]
    public void Migrate_LegacySystemEqOn_ThenApoOnTheHeadphones_EqEverythingDefaultsOn()
    {
        var s = LegacyUserSettings();
        s.SystemEqEnabled = true; // had opted in to equalizing everything before 1.4
        RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId);

        Assert.True(RoutingPlanner.Plan(s, RealMachine(), HeadphonesId, Apo(HeadphonesId)).ApoActive);
    }

    [Fact]
    public void Migrate_RunsOnce()
    {
        var s = LegacyUserSettings();
        RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId);
        s.HeadsetDeviceId = LgHdId; // the user picked another one afterwards

        Assert.False(RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId));
        Assert.Equal(LgHdId, s.HeadsetDeviceId);
    }

    [Fact]
    public void Migrate_SystemEqOutput_WinsOverTheMonitor()
    {
        var s = LegacyUserSettings();
        s.SystemEqOutputDeviceId = LgUltraId;

        RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId);

        Assert.Equal(LgUltraId, s.HeadsetDeviceId);
    }

    [Fact]
    public void Migrate_SavedHeadsetCounts_EvenWhileDisconnected()
    {
        var s = LegacyUserSettings();

        RoutingPlanner.MigrateSettings(s, CrusherDisconnected(), LgHdId);

        Assert.Equal(HeadphonesId, s.HeadsetDeviceId);
    }

    [Fact]
    public void Migrate_SavedVirtualOrUnknownDevices_AreSkipped()
    {
        var s = new AppSettings
        {
            SystemEqOutputDeviceId = DummyId,                                           // virtual
            MonitorDeviceId = "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}", // not on this machine
        };

        RoutingPlanner.MigrateSettings(s, WithVoicemod(), LgHdId);

        Assert.Equal(LgHdId, s.HeadsetDeviceId); // the real Windows default
    }

    [Fact]
    public void Migrate_DefaultOnTheCable_FallsBackToTheFirstBluetoothDevice()
    {
        var s = new AppSettings();

        RoutingPlanner.MigrateSettings(s, RealMachine(), CableInId);

        Assert.Equal(HeadphonesId, s.HeadsetDeviceId);
    }

    [Fact]
    public void Migrate_DefaultOnTheRenamedCable_FallsBackToTheFirstBluetoothDevice()
    {
        var s = new AppSettings();

        RoutingPlanner.MigrateSettings(s, RenamedMachine(rename16: true), Cable16Id);

        Assert.Equal(HeadphonesId, s.HeadsetDeviceId);
    }

    [Fact]
    public void Migrate_NoBluetooth_FallsBackToTheFirstRealDeviceByName()
    {
        var devices = RealMachine();
        devices.Remove(Headphones);
        var s = new AppSettings();

        RoutingPlanner.MigrateSettings(s, devices, CableInId);

        Assert.Equal(RealtekId, s.HeadsetDeviceId);
    }

    [Fact]
    public void Migrate_NoRealPlaybackDevice_ChangesNothing()
    {
        var devices = new List<AudioDeviceInfo> { Dummy, CableIn, Cable16, CableOut };
        var s = new AppSettings();

        Assert.False(RoutingPlanner.MigrateSettings(s, devices, CableInId));
        Assert.Null(s.HeadsetDeviceId);
        Assert.Null(s.EqAllAudio);
    }

    [Fact]
    public void Migrate_ExistingHeadset_IsLeftAlone()
    {
        var s = UserSettings(eqAll: false);
        s.HeadsetDeviceId = LgHdId;

        Assert.False(RoutingPlanner.MigrateSettings(s, RealMachine(), HeadphonesId));
        Assert.Equal(LgHdId, s.HeadsetDeviceId);
        Assert.False(s.EqAllAudio);
    }

    [Fact]
    public void AppSettings_NewFields_Defaults()
    {
        var s = new AppSettings();

        Assert.Null(s.HeadsetDeviceId);
        Assert.Null(s.EqAllAudio);     // never chosen: the plan decides (on only for a pre-1.4 System EQ user)
        Assert.False(s.EqAutoPreamp);  // no automatic level change: a slider only changes its own sound (since 1.6)
        Assert.True(s.MonitorEnabled); // "Hear soundboard locally" is on for new users
    }

    [Fact]
    public void Plan_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => RoutingPlanner.Plan(null!, RealMachine(), null));
        Assert.Throws<ArgumentNullException>(() => RoutingPlanner.Plan(new AppSettings(), null!, null));
        Assert.Throws<ArgumentNullException>(() => RoutingPlanner.MigrateSettings(null!, RealMachine(), null));
    }
}
