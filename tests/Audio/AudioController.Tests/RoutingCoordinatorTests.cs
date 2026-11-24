using AudioController.Core.Abstractions;
using AudioController.Core.Devices;
using AudioController.Core.Models;
using AudioController.Core.Routing;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// The code that actually moves Windows' default output (<see cref="RoutingCoordinator"/>), driven the way the app
/// drives it — observe the scan, plan with <see cref="RoutingPlanner"/>, choose, switch — over a fake device manager.
/// Covers the situation-key suppression, the loop guard (what counts, when it trips and clears), "automatic" defaults
/// (only those are ever moved off an inlet), the comms role, and the exit restore.
/// </summary>
public sealed class RoutingCoordinatorTests
{
    private const string Internal = "{00000000-0000-0000-ffff-ffffffffffff}";
    private const string CrusherContainer = "{8a0595a4-3da3-5247-ac3c-a0dd01591456}";

    private const string CableInId = "{0.0.0.00000000}.{5e7ce607-be51-4e86-8e6c-80f7903a033e}";
    private const string HeadphonesId = "{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}";
    private const string RealtekId = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}";
    private const string LgHdId = "{0.0.0.00000000}.{95ae7c56-8bcc-4d6d-9562-c4abde53b4c6}";
    private const string CableAId = "{0.0.0.00000000}.{aaaa0000-0000-0000-0000-00000000000a}";
    private const string CableOutId = "{0.0.1.00000000}.{197f05dd-3634-40fe-900a-a1ab49f285b5}";
    private const string HeadsetMicId = "{0.0.1.00000000}.{bf75d670-a9c6-406d-8aa9-623656457f84}";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AudioDeviceInfo Dev(string id, string name, DeviceKind kind, string enumerator, string? container,
        bool isDefault = false, DeviceActivity state = DeviceActivity.Active) => new()
    {
        Id = id,
        FriendlyName = name,
        Kind = kind,
        State = state,
        IsBluetooth = BluetoothDetector.IsBluetooth(enumerator),
        IsVirtual = VirtualDeviceDetector.IsVirtual(enumerator, name),
        ContainerId = container,
        IsDefault = isDefault,
        IsDefaultCommunications = isDefault,
    };

    /// <summary>The user's machine plus VB-Audio CABLE A (the spare virtual playback device = the router's inlet).</summary>
    private static ListDeviceManager Machine(string defaultId) => new(new List<AudioDeviceInfo>
    {
        Dev(CableInId, "CABLE Input (VB-Audio Virtual Cable)", DeviceKind.Playback, "ROOT", Internal, defaultId == CableInId),
        Dev(HeadphonesId, "Headphones (Crusher ANC 2)", DeviceKind.Playback, "BTHENUM", CrusherContainer, defaultId == HeadphonesId),
        Dev(RealtekId, "Digital Output (Realtek USB2.0 Audio)", DeviceKind.Playback, "USB", Internal, defaultId == RealtekId),
        Dev(LgHdId, "LG FULL HD (NVIDIA High Definition Audio)", DeviceKind.Playback, "HDAUDIO", "{f9f942c5-9991-5135-a31c-0e7a1893fcf3}", defaultId == LgHdId),
        Dev(CableAId, "CABLE-A Input (VB-Audio Cable A)", DeviceKind.Playback, "ROOT", Internal, defaultId == CableAId),
        Dev(CableOutId, "CABLE Output (VB-Audio Virtual Cable)", DeviceKind.Capture, "ROOT", Internal),
        Dev(HeadsetMicId, "Headset (Crusher ANC 2)", DeviceKind.Capture, "BTHHFENUM", CrusherContainer),
    });

    private static AppSettings Settings(bool? eqAll) => new()
    {
        InputDeviceId = HeadsetMicId,
        HeadsetDeviceId = HeadphonesId,
        OutputDeviceId = CableInId,
        MonitorDeviceId = HeadphonesId,
        EqAllAudio = eqAll,
    };

    /// <summary>The headset drops (Bluetooth off) or comes back; Windows' default flags stay as they are.</summary>
    private static void Headset(ListDeviceManager m, bool connected)
    {
        var phones = m.GetDevices().Single(d => d.Id == HeadphonesId);
        m.Replace(ListDeviceManager.With(phones, phones.IsDefault, phones.IsDefaultCommunications,
            connected ? DeviceActivity.Active : DeviceActivity.Unplugged));
    }

    /// <summary>The plan the app would make right now (what MainViewModel.MakePlan does).</summary>
    private static RoutingPlan PlanNow(RoutingCoordinator c, ListDeviceManager m, AppSettings s)
    {
        string? current = m.DefaultId();
        return RoutingPlanner.Plan(s, m.GetDevices(), current, null, c.IsAutomaticDefault(current));
    }

    /// <summary>One routing pass as MainViewModel runs it: observe the scan, plan, choose, switch. Returns the change.</summary>
    private static string? Pass(RoutingCoordinator c, ListDeviceManager m, AppSettings s,
        DefaultOutputMode mode = DefaultOutputMode.Passive, bool expectSwitchToWork = true)
    {
        c.ObserveDevices(m.GetDevices());
        string? current = m.DefaultId();
        var plan = PlanNow(c, m, s);
        string? change = c.ChooseChange(plan, current, mode);
        if (change is not null)
        {
            string? error = c.TrySwitch(change, communications: false);
            if (expectSwitchToWork)
            {
                Assert.Null(error);
            }
        }

        return change;
    }

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    // ---------------------------------------------------------------- never fight a manual choice (finding: OBS user)

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void EqEverythingOff_UsersDeliberateCableADefault_IsNeverMoved(bool? eqAll)
    {
        // CABLE-A feeds OBS on purpose; it is also the router's candidate inlet. Startup, scans, window activations…
        var m = Machine(defaultId: CableAId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll);

        for (int i = 0; i < 6; i++)
        {
            Assert.Null(Pass(c, m, s));
        }

        Assert.Empty(m.DefaultChanges);
        Assert.Equal(CableAId, m.DefaultId());
        Assert.False(c.LoopGuardTripped);
    }

    [Fact]
    public void RouterOn_ClaimsTheInletAtStartup_ThenNeverFightsAManualChoice()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: true);

        Assert.Equal(CableAId, Pass(c, m, s)); // situation changed (startup): route PC audio through the app
        Assert.Equal(CableAId, m.DefaultId());

        m.SetDefaultByOthers(HeadphonesId); // the user bypasses the EQ by hand
        Assert.Null(Pass(c, m, s));         // same situation: not reclaimed
        Assert.Null(Pass(c, m, s));
        Assert.Equal(HeadphonesId, m.DefaultId());
        Assert.Single(m.DefaultChanges);
    }

    [Fact]
    public void RouterOn_HeadsetGoneAndBack_RescuesThenReclaimsTheInlet()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: true);
        Assert.Equal(CableAId, Pass(c, m, s));

        Headset(m, connected: false); // Windows keeps the default on CABLE-A (it still exists)
        Assert.Equal(RealtekId, Pass(c, m, s)); // the app had put it on the inlet: never left in silence

        Headset(m, connected: true);
        Assert.Equal(CableAId, Pass(c, m, s)); // headset back = situation changed: routing resumes
        Assert.Equal(new[] { CableAId, RealtekId, CableAId }, m.DefaultChanges.Select(x => x.Id));
    }

    [Fact]
    public void RoutingTurnedOff_InletTheAppPutTheDefaultOn_IsMovedBack()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: true);
        Assert.Equal(CableAId, Pass(c, m, s));

        s.EqAllAudio = false;
        Assert.Equal(HeadphonesId, Pass(c, m, s)); // even a passive pass: the app put it there
    }

    [Fact]
    public void RoutingOff_UserPicksTheOldInletLater_IsLeftAlone()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: true);
        Assert.Equal(CableAId, Pass(c, m, s));
        s.EqAllAudio = false;
        Assert.Equal(HeadphonesId, Pass(c, m, s, DefaultOutputMode.Explicit)); // the user unticks "EQ everything"

        m.SetDefaultByOthers(CableAId); // later: the user feeds OBS through CABLE-A on purpose
        Assert.Null(Pass(c, m, s));
        Assert.Null(Pass(c, m, s));
        Assert.Equal(CableAId, m.DefaultId());
    }

    [Fact]
    public void WindowsPicksTheInlet_WhenTheHeadsetVanishes_IsRescued()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: false);
        Assert.Null(Pass(c, m, s));

        Headset(m, connected: false);
        m.SetDefaultByOthers(CableAId); // Windows chose CABLE-A itself: nobody listens there

        Assert.False(c.IsAutomaticDefault(CableAId)); // not observed yet
        Assert.Equal(RealtekId, Pass(c, m, s));
    }

    [Fact]
    public void Explicit_GoesToTheTarget_AndNullWhenAlreadyThere()
    {
        var m = Machine(defaultId: CableAId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: false);

        Assert.Equal(HeadphonesId, Pass(c, m, s, DefaultOutputMode.Explicit)); // "Fix it" / a user action
        Assert.Null(Pass(c, m, s, DefaultOutputMode.Explicit));
    }

    // ---------------------------------------------------------------- loop guard

    /// <summary>Headset off and the default on the virtual mic: the always-rescued case another program could fight.</summary>
    private static (ListDeviceManager M, RoutingCoordinator C, AppSettings S) HeadsetOffDefaultOnCable(TimeProvider? time = null)
    {
        var m = Machine(defaultId: CableInId);
        Headset(m, connected: false);
        return (m, new RoutingCoordinator(m, time), Settings(eqAll: false));
    }

    [Fact]
    public void LoopGuard_TripsOnlyAfterSomethingUndoesTheSameSwitchRepeatedly()
    {
        var (m, c, s) = HeadsetOffDefaultOnCable();
        Assert.Equal(RealtekId, Pass(c, m, s)); // the rescue itself (situation changed) never counts

        for (int i = 0; i < RoutingCoordinator.LoopLimit; i++)
        {
            m.SetDefaultByOthers(CableInId); // another program undoes it
            Assert.Equal(RealtekId, Pass(c, m, s));
            Assert.False(c.LoopGuardTripped);
        }

        m.SetDefaultByOthers(CableInId);
        Assert.Null(Pass(c, m, s)); // gives up
        Assert.True(c.LoopGuardTripped);
        Assert.Null(Pass(c, m, s));
        Assert.Equal(1 + RoutingCoordinator.LoopLimit, m.DefaultChanges.Count);
    }

    [Fact]
    public void LoopGuard_ClearsWhenTheSituationChanges()
    {
        var (m, c, s) = HeadsetOffDefaultOnCable();
        Pass(c, m, s);
        for (int i = 0; i <= RoutingCoordinator.LoopLimit; i++)
        {
            m.SetDefaultByOthers(CableInId);
            Pass(c, m, s);
        }

        Assert.True(c.LoopGuardTripped);

        Headset(m, connected: true); // situation changed
        Pass(c, m, s);
        Assert.False(c.LoopGuardTripped);

        Headset(m, connected: false); // gone again, default still on the cable: rescued again
        Assert.Equal(RealtekId, Pass(c, m, s));
    }

    [Fact]
    public void LoopGuard_ClearsOnAnExplicitAction()
    {
        var (m, c, s) = HeadsetOffDefaultOnCable();
        Pass(c, m, s);
        for (int i = 0; i <= RoutingCoordinator.LoopLimit; i++)
        {
            m.SetDefaultByOthers(CableInId);
            Pass(c, m, s);
        }

        Assert.True(c.LoopGuardTripped);
        Assert.Equal(RealtekId, Pass(c, m, s, DefaultOutputMode.Explicit)); // "Fix it"
        Assert.False(c.LoopGuardTripped);
    }

    [Fact]
    public void LoopGuard_AFlappingHeadset_NeverTripsIt()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: false);
        Assert.Null(Pass(c, m, s));

        for (int i = 0; i < 3 * RoutingCoordinator.LoopLimit; i++)
        {
            Headset(m, connected: false);
            m.SetDefaultByOthers(CableInId); // Windows parks the default on the virtual mic
            Assert.Equal(RealtekId, Pass(c, m, s)); // rescued every single time
            Assert.False(c.LoopGuardTripped);

            Headset(m, connected: true);
            m.SetDefaultByOthers(HeadphonesId); // Windows returns it to the headset
            Assert.Null(Pass(c, m, s));
        }

        Assert.All(m.DefaultChanges, x => Assert.Equal(RealtekId, x.Id));
    }

    [Fact]
    public void LoopGuard_FailedSwitches_NeverCount()
    {
        var (m, c, s) = HeadsetOffDefaultOnCable();
        m.FailSetDefault = "IPolicyConfig refused";

        for (int i = 0; i < 3 * RoutingCoordinator.LoopLimit; i++)
        {
            Assert.Equal(RealtekId, Pass(c, m, s, expectSwitchToWork: false)); // retried, never given up on
            Assert.False(c.LoopGuardTripped);
        }

        Assert.Null(c.LastSwitchedDefault);
        m.FailSetDefault = null;
        Assert.Equal(RealtekId, Pass(c, m, s));
        Assert.Equal(RealtekId, m.DefaultId());
    }

    [Fact]
    public void LoopGuard_OnlyCountsInsideItsWindow()
    {
        var time = new FakeTime();
        var (m, c, s) = HeadsetOffDefaultOnCable(time);
        Pass(c, m, s);
        for (int i = 0; i < RoutingCoordinator.LoopLimit; i++)
        {
            m.SetDefaultByOthers(CableInId);
            Assert.Equal(RealtekId, Pass(c, m, s));
        }

        time.Advance(RoutingCoordinator.LoopWindow + TimeSpan.FromSeconds(1));
        m.SetDefaultByOthers(CableInId);
        Assert.Equal(RealtekId, Pass(c, m, s)); // the old undos expired
        Assert.False(c.LoopGuardTripped);
    }

    // ---------------------------------------------------------------- automatic defaults

    [Fact]
    public void IsAutomaticDefault_OnlyTheAppsLastSwitch_OrWindowsOwnPick()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        c.ObserveDevices(m.GetDevices());

        Assert.False(c.IsAutomaticDefault(null));
        Assert.False(c.IsAutomaticDefault(HeadphonesId)); // already there when the app started: the user's

        Assert.Null(c.TrySwitch(CableAId, communications: false));
        Assert.True(c.IsAutomaticDefault(CableAId));
        Assert.True(c.IsAutomaticDefault(CableAId.ToUpperInvariant()));
        Assert.Null(c.TrySwitch(HeadphonesId, communications: false));
        Assert.False(c.IsAutomaticDefault(CableAId)); // superseded by the app's later switch
        Assert.Equal(new[] { CableAId, HeadphonesId }.OrderBy(x => x), c.SwitchedDefaults.OrderBy(x => x));

        // The user changes the default while the old one is still there: a choice, not automatic.
        m.SetDefaultByOthers(LgHdId);
        c.ObserveDevices(m.GetDevices());
        Assert.False(c.IsAutomaticDefault(LgHdId));

        // The default device goes away and Windows picks another one itself: automatic.
        var lg = m.GetDevices().Single(d => d.Id == LgHdId);
        m.Replace(ListDeviceManager.With(lg, isDefault: false, isDefaultCommunications: false, DeviceActivity.Unplugged));
        m.SetDefaultByOthers(CableAId);
        c.ObserveDevices(m.GetDevices());
        Assert.True(c.IsAutomaticDefault(CableAId));

        // … until the default changes again by someone's choice.
        m.SetDefaultByOthers(RealtekId);
        c.ObserveDevices(m.GetDevices());
        Assert.False(c.IsAutomaticDefault(RealtekId));
        Assert.False(c.IsAutomaticDefault(CableAId));
    }

    // ---------------------------------------------------------------- TrySwitch and the comms role

    [Fact]
    public void TrySwitch_SetsTheOutput_AndTheCommsRoleOnlyWhenAsked()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);

        Assert.Null(c.TrySwitch(CableAId, communications: false));
        Assert.Equal(new[] { (CableAId, DeviceRole.Default) }, m.DefaultChanges);
        Assert.Equal(CableAId, m.DefaultId());
        Assert.Equal(HeadphonesId, m.DefaultId(communications: true));

        Assert.Null(c.TrySwitch(RealtekId, communications: true));
        Assert.Equal((RealtekId, DeviceRole.Default), m.DefaultChanges[1]);
        Assert.Equal((RealtekId, DeviceRole.Communications), m.DefaultChanges[2]);
        Assert.Equal(RealtekId, m.DefaultId(communications: true));
        Assert.True(c.HasSwitchedDefault);
        Assert.Equal(RealtekId, c.LastSwitchedDefault);
    }

    [Fact]
    public void TrySwitch_Failure_ReturnsTheError_AndRecordsNothing()
    {
        var m = Machine(defaultId: HeadphonesId);
        m.FailSetDefault = "Element not found.";
        var c = new RoutingCoordinator(m);

        Assert.Equal("Element not found.", c.TrySwitch(CableAId, communications: true));
        Assert.False(c.HasSwitchedDefault);
        Assert.Empty(c.SwitchedDefaults);
        Assert.Null(c.LastSwitchedDefault);
        Assert.Equal(HeadphonesId, m.DefaultId());
    }

    // ---------------------------------------------------------------- exit restore

    private static readonly string?[] Inlets = { CableAId, null, "" };

    [Fact]
    public void RestoreForExit_AppNeverSwitched_LeavesAnInletDefaultAlone()
    {
        var m = Machine(defaultId: CableAId); // the user's OBS setup
        var c = new RoutingCoordinator(m);

        string result = c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);

        Assert.Contains("never switched", result);
        Assert.Empty(m.DefaultChanges);
        Assert.Equal(CableAId, m.DefaultId());
    }

    [Fact]
    public void RestoreForExit_AppSwitchedToADifferentDevice_LeavesTheInletAlone()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        Assert.Null(c.TrySwitch(RealtekId, communications: false)); // a fallback rescue, not an inlet
        m.SetDefaultByOthers(CableAId);

        string result = c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);

        Assert.Contains("never switched the Windows default to an inlet", result);
        Assert.Equal(CableAId, m.DefaultId());
    }

    [Fact]
    public void RestoreForExit_StillOnTheInletTheAppSwitchedTo_RestoresTheHeadset_BothRoles()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        Assert.Null(c.TrySwitch(CableAId, communications: true));
        m.DefaultChanges.Clear();

        string result = c.RestoreForExit(Inlets, HeadphonesId, RealtekId, Timeout);

        Assert.Contains("Headphones (Crusher ANC 2)", result);
        Assert.Equal(new[] { (HeadphonesId, DeviceRole.Default), (HeadphonesId, DeviceRole.Communications) }, m.DefaultChanges);
        Assert.Equal(HeadphonesId, m.DefaultId());
        Assert.Equal(HeadphonesId, m.DefaultId(communications: true));
    }

    [Fact]
    public void RestoreForExit_OnlyTheCommsRoleLeftOnTheInlet_MovesOnlyThatRole()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        Assert.Null(c.TrySwitch(CableAId, communications: true));
        m.SetDefaultByOthers(LgHdId); // the user moved the output role only
        m.DefaultChanges.Clear();

        c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);

        Assert.Equal(new[] { (HeadphonesId, DeviceRole.Communications) }, m.DefaultChanges);
        Assert.Equal(LgHdId, m.DefaultId());
        Assert.Equal(HeadphonesId, m.DefaultId(communications: true));
    }

    [Fact]
    public void RestoreForExit_UserMovedEverythingOffTheInlet_NothingToRestore()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        Assert.Null(c.TrySwitch(CableAId, communications: true));
        m.SetDefaultByOthers(LgHdId);
        m.SetDefaultByOthers(LgHdId, DeviceRole.Communications);
        m.DefaultChanges.Clear();

        string result = c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);

        Assert.Contains("nothing to restore", result);
        Assert.Empty(m.DefaultChanges);
    }

    [Fact]
    public void RestoreForExit_HeadsetGone_UsesTheFallback_ThenTheFirstRealDevice()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        Assert.Null(c.TrySwitch(CableAId, communications: false));
        Headset(m, connected: false);

        c.RestoreForExit(Inlets, HeadphonesId, LgHdId, Timeout, final: false);
        Assert.Equal(LgHdId, m.DefaultId());

        m.SetDefaultByOthers(CableAId);
        Assert.Null(c.TrySwitch(CableAId, communications: false)); // not final: still switching
        c.RestoreForExit(Inlets, HeadphonesId, fallbackId: null, Timeout);
        Assert.Equal(RealtekId, m.DefaultId()); // the first connected real playback device
    }

    [Fact]
    public void RestoreForExit_Final_StopsAllFurtherSwitches_AndSaysSo()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        Assert.Null(c.TrySwitch(CableAId, communications: false));
        c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);
        m.DefaultChanges.Clear();

        string? error = c.TrySwitch(CableAId, communications: false);

        Assert.NotNull(error); // never a silent "success" for a switch that did not happen
        Assert.Contains("closing", error);
        Assert.Empty(m.DefaultChanges);
        Assert.Equal(HeadphonesId, m.DefaultId());
    }

    [Fact]
    public void RestoreForExit_NotFinal_ALogoffSomeoneCancels_LeavesTheAppWorking()
    {
        var m = Machine(defaultId: HeadphonesId);
        var c = new RoutingCoordinator(m);
        var s = Settings(eqAll: true);
        Assert.Equal(CableAId, Pass(c, m, s));

        // Session ending: restore, but another program vetoes the logoff and the session goes on.
        c.RestoreForExit(Inlets, HeadphonesId, null, Timeout, final: false);
        Assert.Equal(HeadphonesId, m.DefaultId());

        // The user re-ticks "EQ everything": the explicit action really switches again.
        Assert.Equal(CableAId, Pass(c, m, s, DefaultOutputMode.Explicit));
        Assert.Equal(CableAId, m.DefaultId());

        // The real exit still restores.
        c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);
        Assert.Equal(HeadphonesId, m.DefaultId());
        Assert.NotNull(c.TrySwitch(CableAId, communications: false));
    }

    [Fact]
    public void RestoreForExit_DeviceListFails_NeverThrows()
    {
        var c = new RoutingCoordinator(new ListDeviceManager(null));
        Assert.Null(c.TrySwitch(CableAId, communications: false));

        string result = c.RestoreForExit(Inlets, HeadphonesId, null, Timeout);

        Assert.StartsWith("restore failed", result);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new RoutingCoordinator(null!));
        var c = new RoutingCoordinator(Machine(HeadphonesId));
        Assert.Throws<ArgumentNullException>(() => c.ChooseChange(null!, null, DefaultOutputMode.Passive));
        Assert.Throws<ArgumentNullException>(() => c.ObserveDevices(null!));
    }
}
