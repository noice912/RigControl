namespace AudioController.Core.Equalizer;

/// <summary>
/// What the app knows about Equalizer APO on this PC (read by <c>AudioController.Audio.EqualizerApoDetector</c>):
/// whether it is installed, where, and which audio endpoints it is attached to. Pure data.
/// </summary>
public sealed record EqualizerApoState
{
    /// <summary>Equalizer APO is not installed (or could not be read).</summary>
    public static EqualizerApoState NotInstalled { get; } = new();

    /// <summary>Installed: its registry key, install folder and config folder exist.</summary>
    public bool Installed { get; init; }

    /// <summary>The install folder (holds DeviceSelector.exe), e.g. <c>C:\Program Files\EqualizerAPO</c>.</summary>
    public string? InstallPath { get; init; }

    /// <summary>The config folder (holds config.txt), e.g. <c>C:\Program Files\EqualizerAPO\config</c>.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// Windows endpoint ids (<c>{0.0.0.00000000}.{guid}</c> playback, <c>{0.0.1.00000000}.{guid}</c> recording) whose
    /// FxProperties name Equalizer APO's pre-mix or post-mix APO, i.e. the devices ticked in its Device Selector.
    /// </summary>
    public IReadOnlyList<string> AttachedEndpointIds { get; init; } = Array.Empty<string>();

    /// <summary>True when Equalizer APO is installed and attached to <paramref name="endpointId"/>.</summary>
    public bool IsAttachedTo(string? endpointId) =>
        Installed && endpointId is not null
                  && AttachedEndpointIds.Any(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Structural equality including the attachment list (records compare lists by reference).</summary>
    public bool SameAs(EqualizerApoState? other) =>
        other is not null && Installed == other.Installed
                          && string.Equals(InstallPath, other.InstallPath, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(ConfigPath, other.ConfigPath, StringComparison.OrdinalIgnoreCase)
                          && AttachedEndpointIds.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                                                .SequenceEqual(other.AttachedEndpointIds.OrderBy(i => i, StringComparer.OrdinalIgnoreCase),
                                                               StringComparer.OrdinalIgnoreCase);
}
