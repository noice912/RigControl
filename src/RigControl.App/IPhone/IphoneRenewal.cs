using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using RigControl.Core.Logging;
using RigControl.iPhone.Device;
using RigControl.iPhone.Helper;

namespace RigControl.App.IPhone;

/// <summary>
/// Runs in the main app. Free Apple IDs sign apps for 7 days, so when an iPhone that has RigControl Mobile is
/// connected and its signature has a day or less left (day 6), the iPhone installer is started WITHOUT administrator
/// rights to sign it again with the saved Apple sign-in. This class only reads the installer's status file (nothing
/// secret) and asks Apple's USB service which iPhones are connected; it never signs in or signs anything itself.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class IphoneRenewal : IAsyncDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NotAgainWithin = TimeSpan.FromHours(3);

    private readonly ILog? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reminded = new(StringComparer.Ordinal);

    public IphoneRenewal(ILog? log)
    {
        _log = log;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var devices = new DeviceService();
        // Check at start, every 10 minutes, and whenever an iPhone is plugged in.
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in devices.WatchAsync(_stop.Token))
                    if (e.Kind == DeviceEventKind.Connected) await CheckAsync(devices).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Apple Devices not installed or stopped, or anything else: the timer below still checks.
            }
        });
        while (!_stop.IsCancellationRequested)
        {
            await CheckAsync(devices).ConfigureAwait(false);
            try { await Task.Delay(CheckEvery, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        await watch.ConfigureAwait(false);
    }

    private async Task CheckAsync(DeviceService devices)
    {
        try
        {
            var installed = IphoneInstaller.LoadStatus(IphoneHelper.Paths);
            var now = DateTimeOffset.UtcNow;
            var due = installed.Where(a => IphoneInstaller.RenewalDue(IphoneHelper.Paths, a.Udid, now)).ToList();
            if (due.Count == 0) return;
            // Only the list of connected iPhones from Apple's service: no pairing keys, no sessions in this (elevated) process.
            var connected = await new UsbMuxClient().ListDevicesAsync(_stop.Token).ConfigureAwait(false);
            foreach (var app in due)
            {
                if (!connected.Any(d => d.Udid == app.Udid)) continue;
                lock (_started)
                {
                    // An app signed with the person's own certificate, or installed pre-signed, can only be renewed by
                    // them (a new profile): the window opens once per installed app to say so, not every 3 hours.
                    if (app.Source != InstalledApp.AppleIdSource)
                    {
                        if (!_reminded.Add(app.Udid + "|" + app.Expires.ToString("O"))) continue;
                    }
                    else
                    {
                        if (_started.TryGetValue(app.Udid, out var last) && now - last < NotAgainWithin) continue;
                        _started[app.Udid] = now;
                    }
                }
                _log?.Info($"iPhone: RigControl Mobile on {app.DeviceName} expires {app.Expires:u}; starting the installer to sign it again.");
                var exe = Environment.ProcessPath;
                if (exe is not null)
                    RigControl.Presence.Launcher.StartProgram(exe, IphoneHelper.Switch + " " + IphoneHelper.RenewSwitch);
                return; // one window at a time
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log?.Info("iPhone renewal check skipped: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (Exception) { /* never break the app's shutdown */ }
        _stop.Dispose();
    }
}
