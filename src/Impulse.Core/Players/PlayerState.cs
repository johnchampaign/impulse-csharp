namespace Impulse.Core.Players;

// Mutable container; rules code goes through Mechanics.* helpers (slice B+).
public sealed class PlayerState
{
    public required PlayerId Id { get; init; }
    public required Race Race { get; init; }
    public required PlayerColor Color { get; init; }

    public List<int> Hand { get; init; } = new();
    public List<int> Plan { get; init; } = new();
    public List<int>? NextPlan { get; set; }
    public List<int> Minerals { get; init; } = new();
    public required TechSlots Techs { get; set; }
    public int ShipsAvailable { get; set; } = 12;
    public int Prestige { get; set; }
}
