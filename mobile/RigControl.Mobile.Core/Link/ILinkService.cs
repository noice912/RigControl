namespace RigControl.Mobile.Core.Link;

/// <summary>
/// Everything the phone app needs from the link to its PCs (docs/protocols/link.md). The screens only talk to this,
/// so the real implementation on top of RigControl.Link (pairing, sealing, relay client) replaces
/// <see cref="FakeLinkService"/> without touching them.
/// </summary>
public interface ILinkService
{
    /// <summary>
    /// Pairs with a PC using the code it shows (typed in, or from its QR code). Failures come back as
    /// <see cref="PairResult.Error"/> sentences (wrong or expired code, relay unreachable...), not as exceptions.
    /// <paramref name="progress"/> gets short steps to show while it works.
    /// </summary>
    Task<PairResult> PairAsync(PairingRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// After a <see cref="PairResult.NeedsConfirm"/> result, the person chose to replace the old link: stores the new
    /// keys and replaces the entry. Fails when <paramref name="replaceId"/> is unknown or already used.
    /// </summary>
    Task<PairResult> ConfirmReplaceAsync(string replaceId, CancellationToken cancellationToken = default);

    /// <summary>After a <see cref="PairResult.NeedsConfirm"/> result, the person kept the old link: the new keys are dropped.</summary>
    void DiscardReplace(string replaceId);

    /// <summary>The PCs this phone is linked to, with their last known online state.</summary>
    Task<IReadOnlyList<LinkedPc>> GetPcsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects to a PC through its relay. Every call needs a matching <see cref="DisconnectAsync"/> (the connection
    /// is shared and closes after the last one). Waits a few seconds for the relay; when it can't be reached the
    /// connection keeps retrying (with growing pauses) and requests fail with <see cref="LinkUnavailableException"/>.
    /// </summary>
    Task ConnectAsync(string pcId, CancellationToken cancellationToken = default);

    /// <summary>Gives back one <see cref="ConnectAsync"/> (the PC stays linked).</summary>
    Task DisconnectAsync(string pcId);

    /// <summary>
    /// For the PCs list: keeps every linked PC connected and checks every so often whether it is online, raising
    /// <see cref="PcPresenceChanged"/> when that changes. Runs until <paramref name="cancellationToken"/> ends.
    /// </summary>
    Task WatchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the PC for its current state ("get" {"what":"snapshot"}). Throws <see cref="LinkUnavailableException"/>
    /// when the PC can't be asked right now.
    /// </summary>
    Task<PcSnapshot> GetSnapshotAsync(string pcId, CancellationToken cancellationToken = default);

    /// <summary>Asks the PC to do one of the allowed actions ("do"). An offline PC gives a failed result.</summary>
    Task<ActionResult> DoAsync(string pcId, PcAction action, CancellationToken cancellationToken = default);

    /// <summary>Forgets a PC on this phone (its keys are deleted here).</summary>
    Task ForgetAsync(string pcId);

    /// <summary>Raised for presence changes and "event"/"snapshot" messages from a PC. May be raised on any thread.</summary>
    event EventHandler<LinkEvent>? EventReceived;
}

public abstract record LinkEvent(string PcId);

/// <summary>The PC connected to or left the relay.</summary>
public sealed record PcPresenceChanged(string PcId, bool Online, DateTimeOffset At) : LinkEvent(PcId);

/// <summary>The PC sent a fresh snapshot without being asked (e.g. after an action).</summary>
public sealed record PcSnapshotReceived(string PcId, PcSnapshot Snapshot) : LinkEvent(PcId);

/// <summary>Any other "event" message from the PC, e.g. "Replay saved".</summary>
public sealed record PcNotice(string PcId, string Kind, string Text) : LinkEvent(PcId);
