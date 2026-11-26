using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Core.Text;

namespace RigControl.Mobile.ViewModels;

/// <summary>The PCs screen: every linked PC with an online dot and when it was last seen.</summary>
public sealed partial class PcsViewModel : ObservableObject, IDisposable
{
    private readonly ILinkService _link;
    private CancellationTokenSource? _watch;

    public ObservableCollection<PcItem> Pcs { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public PcsViewModel(ILinkService link)
    {
        _link = link;
        _link.EventReceived += OnLinkEvent;
    }

    [RelayCommand]
    private async Task Refresh()
    {
        try
        {
            Error = null;
            var pcs = await _link.GetPcsAsync();
            var now = DateTimeOffset.UtcNow;
            Pcs.Clear();
            foreach (var pc in pcs.OrderByDescending(p => p.Online).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                Pcs.Add(new PcItem(pc, now));
            IsEmpty = Pcs.Count == 0;
        }
        catch (Exception ex)
        {
            Error = "Couldn't load your PCs: " + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>The list is showing: keep the PCs connected so the dots follow them.</summary>
    public void StartWatching()
    {
        StopWatching();
        _watch = new CancellationTokenSource();
        _ = _link.WatchAsync(_watch.Token);
    }

    public void StopWatching()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
    }

    [RelayCommand]
    private Task Open(PcItem? pc) =>
        pc is null ? Task.CompletedTask
            : Shell.Current.GoToAsync(AppShell.PcRoute, new Dictionary<string, object> { ["id"] = pc.Id });

    [RelayCommand]
    private Task Link() => Shell.Current.GoToAsync(AppShell.LinkRoute);

    private void OnLinkEvent(object? sender, LinkEvent e)
    {
        if (e is not PcPresenceChanged presence)
            return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Pcs.FirstOrDefault(p => p.Id == presence.PcId);
            item?.Update(presence.Online, presence.At, DateTimeOffset.UtcNow);
        });
    }

    public void Dispose()
    {
        StopWatching();
        _link.EventReceived -= OnLinkEvent;
    }
}

public sealed partial class PcItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }

    [ObservableProperty]
    public partial bool Online { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    public PcItem(LinkedPc pc, DateTimeOffset now)
    {
        Id = pc.Id;
        Name = pc.Name;
        Online = pc.Online;
        Status = pc.Problem ?? LastSeen.Describe(pc.Online, pc.LastSeen, now);
    }

    public void Update(bool online, DateTimeOffset at, DateTimeOffset now)
    {
        Online = online;
        Status = LastSeen.Describe(online, at, now);
    }
}
