namespace AudioController.Core.Models;

/// <summary>A single sound button on a board.</summary>
public sealed class SoundSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New sound";

    /// <summary>Hex color for the button background, e.g. "#4F8EF7".</summary>
    public string ColorHex { get; set; } = "#4F8EF7";

    /// <summary>Optional global hotkey gesture, e.g. "Ctrl+Alt+D1". Null = none.</summary>
    public string? Hotkey { get; set; }

    /// <summary>Playback volume, 0.0 to 2.0 (shown as 0–200%).</summary>
    public float Volume { get; set; } = 1.0f;

    public PlayMode PlayMode { get; set; } = PlayMode.OneShot;

    /// <summary>Id of the encrypted sound file in the vault (sounds\{id}.enc).</summary>
    public Guid SoundId { get; set; }

    public double DurationSeconds { get; set; }

    public string OriginalFileName { get; set; } = "";

    public long OriginalSizeBytes { get; set; }
}
