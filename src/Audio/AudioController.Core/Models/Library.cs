namespace AudioController.Core.Models;

/// <summary>Everything stored in library.dat (serialized to JSON, then AES-GCM encrypted).</summary>
public sealed class Library
{
    public int SchemaVersion { get; set; } = 1;

    public List<Board> Boards { get; set; } = new();

    public AppSettings Settings { get; set; } = new();

    /// <summary>The user's own Effects presets ("Save as…" in the Effects panel). Built-in presets are not stored.</summary>
    public List<FxPreset> UserPresets { get; set; } = new();
}
