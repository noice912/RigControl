using System.Diagnostics;
using AudioController.Core.Abstractions;
using AudioController.Core.Models;

namespace AudioController.Core.Routing;

/// <summary>How a routing pass may move Windows' default output.</summary>
public enum DefaultOutputMode
{
    /// <summary>
    /// Device scans (startup, Windows notifications, window activation): apply the plan's
    /// <see cref="RoutingPlan.DesiredWindowsDefaultOutputId"/>. A switch to the inlet only happens when the routing
    /// situation changed (startup, headset back, new inlet), so a default the user picks by hand is not fought.
    /// </summary>
    Passive,

    /// <summary>The user just changed something (route switch, headset, inlet, "Fix it"): go to the plan's target.</summary>
    Explicit,
}

/// <summary>
/// Owns Windows' default playback device on behalf of the routing plan: decides when to switch it, switches it
/// off the UI thread, guards against fighting another program over it, and puts it back on the headset when the
/// app exits so PC audio is never left on an inlet nobody is listening to.
/// </summary>
/// <remarks>
/// <see cref="ChooseChange"/>, <see cref="ObserveDevices"/> and <see cref="IsAutomaticDefault"/> run on the UI thread;
/// <see cref="TrySwitch"/> and <see cref="RestoreForExit"/> run on any thread and are serialized, so a switch that is
/// still in flight when the app exits is undone.
/// </remarks>
public sealed class RoutingCoordinator
{
    /// <summary>
    /// How many times inside <see cref="LoopWindow"/> the app re-does a switch that something else undid before it
    /// gives up (another program keeps changing the default back).
    /// </summary>
    public const int LoopLimit = 4;

    /// <summary>The window <see cref="LoopLimit"/> counts in.</summary>
    public static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(30);

    private readonly IDeviceManager _devices;
    private readonly TimeProvider _time;
    private readonly object _switchLock = new();
    private readonly Queue<long> _undoneSwitches = new();
    private readonly HashSet<string> _switchedTo = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastSwitchedTo;
    private string? _lastKey;
    private string? _observedDefault;
    private string? _windowsPickedDefault;
    private bool _exiting;

    /// <param name="devices">Where defaults are read and set.</param>
    /// <param name="time">Clock for the loop guard (tests pass a fake one); defaults to <see cref="TimeProvider.System"/>.</param>
    public RoutingCoordinator(IDeviceManager devices, TimeProvider? time = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// True after automatic switching stopped because something kept undoing the app's switch. Cleared by the next
    /// explicit action and whenever the routing situation changes (headset gone/back, new inlet, routing on/off).
    /// </summary>
    public bool LoopGuardTripped { get; private set; }

    /// <summary>Every device this app made the Windows default output this session (snapshot).</summary>
    public IReadOnlySet<string> SwitchedDefaults
    {
        get
        {
            lock (_switchLock)
            {
                return new HashSet<string>(_switchedTo, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The device this app last made the Windows default output (by <see cref="TrySwitch"/> or the exit restore), or null.</summary>
    public string? LastSwitchedDefault
    {
        get
        {
            lock (_switchLock)
            {
                return _lastSwitchedTo;
            }
        }
    }

    /// <summary>True once this app moved the Windows default output itself (see <see cref="TrySwitch"/>).</summary>
    public bool HasSwitchedDefault
    {
        get
        {
            lock (_switchLock)
            {
                return _switchedTo.Count > 0;
            }
        }
    }

    /// <summary>
    /// UI thread, once per successful device scan (before planning). Notices when Windows itself picked a new default
    /// output because the previous default device went away (unplugged, Bluetooth off): such a default is nobody's
    /// choice (see <see cref="IsAutomaticDefault"/>). Any other change of the default (the user, another program, this
    /// app) clears that note.
    /// </summary>
    public void ObserveDevices(IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        string? current = devices.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.IsDefault)?.Id;
        if (SameId(current, _observedDefault) || (current is null && _observedDefault is null))
        {
            return;
        }

        bool previousGone = _observedDefault is not null
                            && Active(devices, _observedDefault) is null;
        _windowsPickedDefault = previousGone ? current : null;
        _observedDefault = current;
    }

    /// <summary>
    /// True when <paramref name="currentDefaultId"/> is a default nobody chose by hand: the device this app last made
    /// the default, or the one Windows picked itself after the previous default went away (see
    /// <see cref="ObserveDevices"/>). Passed to <see cref="RoutingPlanner.Plan"/>, which only moves an inlet default
    /// with routing off when it is automatic — a default the user picked is never fought.
    /// </summary>
    public bool IsAutomaticDefault(string? currentDefaultId)
    {
        if (string.IsNullOrWhiteSpace(currentDefaultId))
        {
            return false;
        }

        return SameId(currentDefaultId, LastSwitchedDefault) || SameId(currentDefaultId, _windowsPickedDefault);
    }

    /// <summary>
    /// UI thread. The default-output change to make for <paramref name="plan"/> (null = none).
    /// </summary>
    /// <remarks>
    /// Loop guard: only a Passive pass that re-does a switch someone else undid counts — the app had made the plan's
    /// desired device the default, the default is now elsewhere, and the routing situation did not change. Rescue
    /// switches after a situation change (headset gone/back) and switches that failed never count. After
    /// <see cref="LoopLimit"/> such re-dos inside <see cref="LoopWindow"/> the next one is refused and
    /// <see cref="LoopGuardTripped"/> is set until the situation changes or the user acts explicitly.
    /// </remarks>
    public string? ChooseChange(RoutingPlan plan, string? currentDefaultId, DefaultOutputMode mode)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string key = string.Join("|", plan.RoutingActive, plan.InletId, plan.HeadsetId, plan.HeadsetMissing, plan.FallbackOutputId);
        bool situationChanged = !string.Equals(key, _lastKey, StringComparison.OrdinalIgnoreCase);
        _lastKey = key;

        if (situationChanged || mode == DefaultOutputMode.Explicit)
        {
            // A new situation (or a user action) is not a fight: start counting afresh.
            LoopGuardTripped = false;
            _undoneSwitches.Clear();
        }

        if (mode == DefaultOutputMode.Explicit)
        {
            string? target = plan.TargetWindowsDefaultOutputId;
            return target is not null && !SameId(target, currentDefaultId) ? target : null;
        }

        string? desired = plan.DesiredWindowsDefaultOutputId;
        if (desired is null || SameId(desired, currentDefaultId))
        {
            return null;
        }

        // While routing, only (re)claim the default when the situation changed: never fight a manual choice.
        if (plan.RoutingActive && SameId(desired, plan.InletId) && !situationChanged)
        {
            return null;
        }

        if (LoopGuardTripped)
        {
            return null;
        }

        // Someone moved the default off the app's own switch to this very device, and nothing else changed: a fight.
        if (!situationChanged && SameId(desired, LastSwitchedDefault))
        {
            long now = _time.GetTimestamp();
            while (_undoneSwitches.Count > 0 && _time.GetElapsedTime(_undoneSwitches.Peek(), now) > LoopWindow)
            {
                _undoneSwitches.Dequeue();
            }

            if (_undoneSwitches.Count >= LoopLimit)
            {
                LoopGuardTripped = true;
                return null;
            }

            _undoneSwitches.Enqueue(now);
        }

        return desired;
    }

    /// <summary>
    /// Any thread. Makes <paramref name="deviceId"/> the Windows default output (Console + Multimedia), and the
    /// communications device too when <paramref name="communications"/> is set. Returns null on success, else a
    /// readable error — also once the app is exiting (nothing is switched then). Never throws.
    /// </summary>
    public string? TrySwitch(string deviceId, bool communications)
    {
        lock (_switchLock)
        {
            if (_exiting)
            {
                return "Audio Controller is closing, so it no longer changes the Windows default output.";
            }

            try
            {
                _devices.SetDefaultDevice(deviceId, DeviceRole.Default);
                _switchedTo.Add(deviceId);
                _lastSwitchedTo = deviceId;
                if (communications)
                {
                    _devices.SetDefaultDevice(deviceId, DeviceRole.Communications);
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>
    /// Exit (window closing / app exit / session end / crash): only when this app had switched the Windows default
    /// onto an inlet, any default role (output or communications) still on one of those inlets is moved to
    /// <paramref name="headsetId"/> — or, when it is gone, to <paramref name="fallbackId"/> or the first connected real
    /// playback device. A default the user (or another program) put somewhere is never touched. Blocks at most
    /// <paramref name="timeout"/>. Never throws.
    /// </summary>
    /// <param name="final">
    /// True for the real exit (app exit, crash): afterwards no further switches happen (<see cref="TrySwitch"/> reports
    /// an error). False for a Windows logoff / shutdown that another program can still cancel: the default is restored
    /// but the app keeps working normally if the session goes on.
    /// </param>
    /// <returns>What was done, for diagnostics.</returns>
    public string RestoreForExit(IEnumerable<string?> inletIds, string? headsetId, string? fallbackId, TimeSpan timeout, bool final = true)
    {
        ArgumentNullException.ThrowIfNull(inletIds);
        var candidates = inletIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList();
        try
        {
            var work = Task.Run(() =>
            {
                lock (_switchLock)
                {
                    if (final)
                    {
                        _exiting = true;
                    }

                    // Only inlets the app itself switched Windows to; never undo a choice the app did not make.
                    var inlets = candidates.Where(_switchedTo.Contains).ToList();
                    if (inlets.Count == 0)
                    {
                        return _switchedTo.Count == 0
                            ? "the app never switched the Windows default: nothing to restore"
                            : "the app never switched the Windows default to an inlet: nothing to restore";
                    }

                    var devices = _devices.GetDevices();
                    var output = devices.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.IsDefault);
                    var comms = devices.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.IsDefaultCommunications);
                    bool outputOnInlet = output is not null && inlets.Any(i => SameId(i, output.Id));
                    bool commsOnInlet = comms is not null && inlets.Any(i => SameId(i, comms.Id));
                    if (!outputOnInlet && !commsOnInlet)
                    {
                        return $"default is \"{output?.FriendlyName ?? "(none)"}\": nothing to restore";
                    }

                    string? target = Active(devices, headsetId) ?? Active(devices, fallbackId)
                                     ?? RoutingPlanner.HeadsetChoices(devices).FirstOrDefault()?.Id;
                    if (target is null)
                    {
                        return "no real playback device to restore to";
                    }

                    if (outputOnInlet)
                    {
                        _devices.SetDefaultDevice(target, DeviceRole.Default);
                        _lastSwitchedTo = target;
                    }

                    if (commsOnInlet)
                    {
                        _devices.SetDefaultDevice(target, DeviceRole.Communications);
                    }

                    string name = devices.First(d => SameId(d.Id, target)).FriendlyName;
                    return $"restored the Windows default to \"{name}\"";
                }
            });

            return work.Wait(timeout) ? work.Result : "timed out restoring the Windows default";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Restoring the Windows default failed: " + ex);
            return "restore failed: " + (ex.GetBaseException().Message);
        }
    }

    private static string? Active(IReadOnlyList<AudioDeviceInfo> devices, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : devices.FirstOrDefault(d => d.Kind == DeviceKind.Playback && d.State == DeviceActivity.Active && SameId(d.Id, id))?.Id;

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
