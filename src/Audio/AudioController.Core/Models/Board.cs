namespace AudioController.Core.Models;

/// <summary>A named collection of sound slots. Unlimited boards, unlimited slots.</summary>
public sealed class Board
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New board";

    public List<SoundSlot> Slots { get; set; } = new();
}
