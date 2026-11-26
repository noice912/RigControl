using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigControl.Mobile.Core.DeepLinks;
using RigControl.Mobile.Core.Link;
using RigControl.Mobile.Services;

namespace RigControl.Mobile.ViewModels;

/// <summary>
/// Link a PC: scan the QR code the PC shows (Settings > Phone on the PC), or type its 20-character code.
/// A rigcontrol://pair link or a scanned code only fills the fields; linking always waits for a tap on Link.
/// </summary>
public sealed partial class LinkPcViewModel : ObservableObject, IQueryAttributable
{
    private readonly ILinkService _link;
    private readonly AppSettings _settings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
    public partial string CodeText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
    public partial string RelayText { get; set; }

    [ObservableProperty] public partial string CodeHint { get; set; } = CodeHintFor("");
    [ObservableProperty] public partial bool CodeLooksWrong { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>What linking is doing right now (shown under the Link button).</summary>
    [ObservableProperty] public partial string? ProgressText { get; set; }

    /// <summary>Said when the relay address is the "not set in this build" placeholder.</summary>
    [ObservableProperty] public partial string? RelayNote { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
    public partial bool Busy { get; set; }

    /// <summary>The relay field is shown when no QR code filled it in (or the person wants to see it).</summary>
    [ObservableProperty] public partial bool ShowRelay { get; set; }

    public LinkPcViewModel(ILinkService link, AppSettings settings)
    {
        _link = link;
        _settings = settings;
        // A typed code goes to the relay used last; before the first link, to the one built into the app.
        RelayText = settings.Relay.Length > 0 ? settings.Relay : MobileDefaults.RelayHost;
        ShowRelay = settings.Relay.Length == 0;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        Error = null;
        if (query.TryGetValue("code", out var c) && c is string code)
            CodeText = PairingCode.Group(code);
        if (query.TryGetValue("relay", out var r) && r is string relay)
            RelayText = relay;
        if (query.TryGetValue("scanned", out var s) && s is string scanned)
        {
            if (DeepLink.Parse(scanned) is DeepLink.Pair pair)
            {
                CodeText = pair.Code.ToString();
                RelayText = pair.Relay;
            }
            else
            {
                Error = "That QR code isn't a RigControl pairing code. On the PC, open Settings > Phone > Link a phone.";
            }
        }
        query.Clear(); // so coming back to this screen doesn't apply them again
    }

    /// <summary>Shown whenever the relay in use is not the one built into the app, even while the relay field is hidden.</summary>
    [ObservableProperty] public partial string? OtherRelayNote { get; set; }

    partial void OnRelayTextChanged(string value)
    {
        RelayNote = MobileDefaults.IsPlaceholder(value)
            ? "This build has no relay address built in. Scan the PC's QR code (it carries the address), or type the relay address your PC uses."
            : null;
        OtherRelayNote = MobileDefaults.OtherRelayNote(value);
    }

    partial void OnCodeTextChanged(string value)
    {
        var typed = PairingCode.Normalize(value, out var bad);
        CodeLooksWrong = bad || typed.Length > PairingCode.Length;
        CodeHint = bad ? "Only letters and digits from the code on the PC." : CodeHintFor(typed);
    }

    private static string CodeHintFor(string typed) =>
        typed.Length switch
        {
            0 => "20 characters, in 5 groups of 4, as the PC shows them.",
            PairingCode.Length => "Code complete.",
            > PairingCode.Length => "That is longer than a pairing code.",
            _ => $"{typed.Length} of {PairingCode.Length} characters.",
        };

    private bool CanLink() =>
        !Busy && PairingCode.TryParse(CodeText, out _) && RelayHost.TryNormalize(RelayText, out _);

    [RelayCommand(CanExecute = nameof(CanLink))]
    private async Task Link()
    {
        if (!PairingCode.TryParse(CodeText, out var code) || !RelayHost.TryNormalize(RelayText, out var relay))
            return;
        Busy = true;
        Error = null;
        ProgressText = "Starting...";
        try
        {
            var progress = new Progress<string>(step => ProgressText = step);
            var result = await _link.PairAsync(new PairingRequest(code, relay, _settings.PhoneName), progress);
            if (result.NeedsConfirm)
            {
                // The code led to a PC with the id of one linked here (same or other keys/relay): only an explicit
                // "Replace" changes the old link.
                bool replace;
                try
                {
                    replace = await Shell.Current.DisplayAlertAsync("Already linked", result.Error, "Replace", "Keep the old one");
                }
                catch
                {
                    // No answer: the waiting keys are dropped, nothing changes.
                    _link.DiscardReplace(result.ReplaceId!);
                    throw;
                }
                result = replace
                    ? await _link.ConfirmReplaceAsync(result.ReplaceId!)
                    : Kept(result.ReplaceId!);
            }
            if (!result.Ok || result.Pc is null)
            {
                Error = result.Error ?? "Linking didn't work. Show a new code on the PC and try again.";
                return;
            }
            _settings.Relay = relay;
            CodeText = "";
            await Shell.Current.GoToAsync("..");
            await Shell.Current.GoToAsync(AppShell.PcRoute, new Dictionary<string, object> { ["id"] = result.Pc.Id });
        }
        catch (Exception ex)
        {
            Error = "Linking didn't work: " + ex.Message;
        }
        finally
        {
            Busy = false;
            ProgressText = null;
        }
    }

    private PairResult Kept(string replaceId)
    {
        _link.DiscardReplace(replaceId);
        return PairResult.Fail("Nothing was changed: the PC already linked here stays as it was.");
    }

    [RelayCommand]
    private async Task Scan()
    {
        Error = null;
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            Error = "RigControl needs the camera only to read the PC's QR code. You can type the code instead.";
            return;
        }
        await Shell.Current.GoToAsync(AppShell.ScanRoute);
    }

    [RelayCommand]
    private void EditRelay() => ShowRelay = true;
}
