using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Core.Text;

namespace RigControl.Mobile.ViewModels;

/// <summary>
/// One PC: temperatures, fans, loads, recording and the lighting quick controls (decision D1). Asks the PC for a fresh
/// snapshot every few seconds while the screen is open, and stops when it closes.
/// </summary>
public sealed partial class PcDetailViewModel : ObservableObject, IQueryAttributable, IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    private const string Dash = "–";

    private readonly ILinkService _link;
    private CancellationTokenSource? _poll;
    private DateTimeOffset? _lastSeen;
    private bool _connectionMessage;
    private bool _applying; // true while a snapshot is copied in, so the switch and picker don't send actions back

    public string PcId { get; private set; } = "";

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool Online { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    [ObservableProperty] public partial string CpuTemp { get; set; } = Dash;
    [ObservableProperty] public partial string GpuTemp { get; set; } = Dash;
    [ObservableProperty] public partial string LiquidTemp { get; set; } = Dash;
    [ObservableProperty] public partial double CpuLoad { get; set; }
    [ObservableProperty] public partial double GpuLoad { get; set; }
    [ObservableProperty] public partial string CpuLoadText { get; set; } = Dash;
    [ObservableProperty] public partial string GpuLoadText { get; set; } = Dash;
    [ObservableProperty] public partial string? NowPlaying { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string RecordingText { get; set; } = "";
    [ObservableProperty] public partial string RecordButtonText { get; set; } = "Start recording";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveReplayCommand))]
    public partial bool ReplayOn { get; set; }

    [ObservableProperty] public partial bool LightsOn { get; set; }
    [ObservableProperty] public partial LightingEffect? SelectedEffect { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand), nameof(SaveReplayCommand))]
    public partial bool Busy { get; set; }

    public ObservableCollection<FanItem> Fans { get; } = [];
    public ObservableCollection<LightingEffect> Effects { get; } = [];

    public PcDetailViewModel(ILinkService link)
    {
        _link = link;
        _link.EventReceived += OnLinkEvent;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && id is string s)
            PcId = s;
    }

    /// <summary>The screen is showing: connect and keep the readings fresh.</summary>
    public async Task StartAsync()
    {
        Stop();
        var cts = _poll = new CancellationTokenSource();
        var pcId = PcId;
        var connected = false;
        try
        {
            var pc = (await _link.GetPcsAsync(cts.Token)).FirstOrDefault(p => p.Id == pcId);
            if (pc is null)
            {
                ShowMessage("This PC is no longer linked.", error: true);
                return;
            }
            Name = pc.Name;
            _lastSeen = pc.LastSeen;
            SetOnline(pc.Online);
            if (pc.Problem is { } problem)
                ShowMessage(problem, error: true, connection: true);
            await _link.ConnectAsync(pcId, cts.Token);
            connected = true;
            while (!cts.IsCancellationRequested)
            {
                await LoadSnapshot(cts.Token);
                await Task.Delay(PollEvery, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (LinkUnavailableException ex)
        {
            ShowMessage(ex.Message, error: true, connection: true);
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't reach the PC: " + ex.Message, error: true, connection: true);
        }
        finally
        {
            // Every successful connect is given back once; a cancelled one gave itself back.
            if (connected)
                _ = _link.DisconnectAsync(pcId);
        }
    }

    /// <summary>The screen closed: stop asking (the running loop then closes its connection).</summary>
    public void Stop()
    {
        if (_poll is null)
            return;
        _poll.Cancel();
        _poll.Dispose();
        _poll = null;
    }

    private async Task LoadSnapshot(CancellationToken ct)
    {
        try
        {
            Apply(await _link.GetSnapshotAsync(PcId, ct));
            _lastSeen = DateTimeOffset.UtcNow;
            SetOnline(true);
            if (_connectionMessage)
                Message = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LinkUnavailableException ex)
        {
            SetOnline(false);
            ShowMessage(ex.Message, error: true, connection: true);
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't read the PC: " + ex.Message, error: true, connection: true);
        }
    }

    private void SetOnline(bool online)
    {
        Online = online;
        Status = LastSeen.Describe(online, _lastSeen, DateTimeOffset.UtcNow);
    }

    private void Apply(PcSnapshot s)
    {
        _applying = true;
        try
        {
            HasData = true;
            CpuTemp = Temp(s.CpuTempC);
            GpuTemp = Temp(s.GpuTempC);
            LiquidTemp = Temp(s.LiquidTempC);
            CpuLoad = (s.CpuLoad ?? 0) / 100.0;
            GpuLoad = (s.GpuLoad ?? 0) / 100.0;
            CpuLoadText = Percent(s.CpuLoad);
            GpuLoadText = Percent(s.GpuLoad);
            NowPlaying = string.IsNullOrWhiteSpace(s.NowPlaying) ? null : s.NowPlaying;

            SyncList(Fans, s.Fans.Select(f => new FanItem(f.Name, f.Rpm.ToString("N0", CultureInfo.CurrentCulture) + " rpm")).ToList());

            IsRecording = s.Recording.IsRecording;
            ReplayOn = s.Recording.ReplayBufferOn;
            RecordButtonText = IsRecording ? "Stop recording" : "Start recording";
            RecordingText = IsRecording
                ? "Recording" + (s.Recording.RecordingSince is { } since ? " for " + Elapsed(DateTimeOffset.UtcNow - since) : "")
                : ReplayOn ? "Not recording. Instant replay is on." : "Not recording. Instant replay is off.";

            if (!Effects.Select(e => e.Id).SequenceEqual(s.Lighting.Effects.Select(e => e.Id)))
            {
                Effects.Clear();
                foreach (var effect in s.Lighting.Effects)
                    Effects.Add(effect);
            }
            LightsOn = s.Lighting.On;
            SelectedEffect = Effects.FirstOrDefault(e => e.Id == s.Lighting.Effect);
        }
        finally
        {
            _applying = false;
        }
    }

    private static void SyncList(ObservableCollection<FanItem> target, List<FanItem> items)
    {
        // Same fans: update in place so the list doesn't flicker every few seconds.
        if (target.Count == items.Count && target.Select(f => f.Name).SequenceEqual(items.Select(f => f.Name)))
        {
            for (var i = 0; i < items.Count; i++)
                target[i].Speed = items[i].Speed;
            return;
        }
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static string Temp(double? c) => c is null ? Dash : c.Value.ToString("0", CultureInfo.CurrentCulture) + " °C";
    private static string Percent(double? p) => p is null ? Dash : p.Value.ToString("0", CultureInfo.CurrentCulture) + " %";

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(0, t.Seconds)} s";

    private bool CanRecord() => !Busy;
    private bool CanSaveReplay() => !Busy && ReplayOn;

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private Task ToggleRecording() => Do(IsRecording ? PcAction.StopRecording() : PcAction.StartRecording());

    [RelayCommand(CanExecute = nameof(CanSaveReplay))]
    private Task SaveReplay() => Do(PcAction.SaveReplay(), "Replay saved on the PC.");

    partial void OnLightsOnChanged(bool value)
    {
        if (!_applying)
            _ = Do(value ? PcAction.LightsOn() : PcAction.LightsOff());
    }

    partial void OnSelectedEffectChanged(LightingEffect? value)
    {
        if (!_applying && value is not null)
            _ = Do(PcAction.SetEffect(value.Id));
    }

    [RelayCommand]
    private async Task Forget()
    {
        var sure = await Shell.Current.DisplayAlertAsync("Forget this PC?",
            $"This phone will no longer see or control {Name}. To link it again, show a new code on the PC.",
            "Forget", "Cancel");
        if (!sure)
            return;
        Stop();
        await _link.ForgetAsync(PcId);
        await Shell.Current.GoToAsync("..");
    }

    private async Task Do(PcAction action, string? done = null)
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            var result = await _link.DoAsync(PcId, action);
            if (result.Ok)
            {
                if (done is not null)
                    ShowMessage(done, error: false);
                await LoadSnapshot(CancellationToken.None);
            }
            else
            {
                await LoadSnapshot(CancellationToken.None); // put the switch and picker back to what the PC has
                ShowMessage(result.Error ?? "The PC said no.", error: true);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't reach the PC: " + ex.Message, error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <param name="connection">The message is about reaching the PC, so the next good snapshot clears it.</param>
    private void ShowMessage(string text, bool error, bool connection = false)
    {
        Message = text;
        MessageIsError = error;
        _connectionMessage = connection;
    }

    private void OnLinkEvent(object? sender, LinkEvent e)
    {
        if (e.PcId != PcId)
            return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (e)
            {
                case PcSnapshotReceived snap:
                    Apply(snap.Snapshot);
                    break;
                case PcPresenceChanged p:
                    _lastSeen = p.At;
                    SetOnline(p.Online);
                    break;
                case PcNotice n:
                    ShowMessage(n.Text, error: false);
                    break;
            }
        });
    }

    public void Dispose()
    {
        Stop();
        _link.EventReceived -= OnLinkEvent;
    }
}

public sealed partial class FanItem(string name, string speed) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Speed { get; set; } = speed;
}
