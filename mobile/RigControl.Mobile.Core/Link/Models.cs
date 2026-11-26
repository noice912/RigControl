namespace RigControl.Mobile.Core.Link;

/// <summary>A PC this phone is linked to (stored on the phone after pairing).</summary>
/// <param name="Id">The PC's device id (protocol: base64url SHA-256 of its signing key); also its relay room.</param>
/// <param name="Name">The name the PC sent when pairing.</param>
/// <param name="Relay">The relay host the PC uses (from the pairing QR code or typed in).</param>
/// <param name="Online">Whether the PC is connected to the relay right now.</param>
/// <param name="LastSeen">When the PC was last connected, if ever.</param>
/// <param name="Problem">Why the PC can't be reached from this phone, when the link itself is broken (removed on the
/// PC, keys lost on the phone, relay refusing); null normally.</param>
public sealed record LinkedPc(string Id, string Name, string Relay, bool Online, DateTimeOffset? LastSeen, string? Problem = null);

/// <summary>What the PC sends as a "snapshot" (docs/protocols/link.md, PC -> phone). Every value may be missing.</summary>
public sealed record PcSnapshot
{
    public DateTimeOffset TakenAt { get; init; }
    public double? CpuTempC { get; init; }
    public double? GpuTempC { get; init; }
    /// <summary>AIO / liquid temperature, when the PC has a sensor for it.</summary>
    public double? LiquidTempC { get; init; }
    /// <summary>CPU load, 0-100.</summary>
    public double? CpuLoad { get; init; }
    /// <summary>GPU load, 0-100.</summary>
    public double? GpuLoad { get; init; }
    public IReadOnlyList<FanReading> Fans { get; init; } = [];
    public RecordingState Recording { get; init; } = new();
    public LightingState Lighting { get; init; } = new();
    /// <summary>What is playing on the PC (music or game), as one line, if anything.</summary>
    public string? NowPlaying { get; init; }
    /// <summary>The custom Discord statuses made on the PC, which the phone may switch on or off.</summary>
    public IReadOnlyList<CustomStatus> CustomStatuses { get; init; } = [];
}

public sealed record CustomStatus(string Id, string Label, bool On);

public sealed record FanReading(string Name, int Rpm);

public sealed record RecordingState
{
    public bool IsRecording { get; init; }
    public DateTimeOffset? RecordingSince { get; init; }
    /// <summary>Instant replay is running, so "Save replay" can work.</summary>
    public bool ReplayBufferOn { get; init; }
}

public sealed record LightingState
{
    /// <summary>The lights are on (an effect or a colour), as opposed to switched off from RigControl.</summary>
    public bool On { get; init; }
    /// <summary>The effect that runs now (an id from <see cref="Effects"/>), or null when none runs.</summary>
    public string? Effect { get; init; }
    /// <summary>The effects the PC offers.</summary>
    public IReadOnlyList<LightingEffect> Effects { get; init; } = [];
}

public sealed record LightingEffect(string Id, string Name);

/// <summary>The PC's answer to a "do" request.</summary>
public sealed record ActionResult(bool Ok, string? Error = null)
{
    public static readonly ActionResult Success = new(true);
    public static ActionResult Fail(string error) => new(false, error);
}

/// <summary>A pairing request: the code shown on the PC and the relay it uses.</summary>
public sealed record PairingRequest(PairingCode Code, string Relay, string PhoneName);

/// <param name="Ok">Linked.</param>
/// <param name="Pc">The linked PC when <paramref name="Ok"/>.</param>
/// <param name="Error">Why it didn't work, as a sentence to show.</param>
/// <param name="ReplaceId">
/// Set when the code led to a PC with the id of one already linked here but with different keys or a different relay:
/// nothing was changed, and <see cref="Error"/> is the question to ask ("... Replace it?"). Only
/// <see cref="ILinkService.ConfirmReplaceAsync"/> with this id replaces the old link; <see cref="ILinkService.DiscardReplace"/>
/// keeps it.
/// </param>
public sealed record PairResult(bool Ok, LinkedPc? Pc, string? Error, string? ReplaceId = null)
{
    public static PairResult Success(LinkedPc pc) => new(true, pc, null);
    public static PairResult Fail(string error) => new(false, null, error);

    public static PairResult NeedsReplace(string replaceId, string question) => new(false, null, question, replaceId);

    /// <summary>True when the person must confirm replacing an already linked PC (see <see cref="ReplaceId"/>).</summary>
    public bool NeedsConfirm => ReplaceId is not null;
}
