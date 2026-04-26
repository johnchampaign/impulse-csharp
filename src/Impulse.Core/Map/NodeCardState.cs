namespace Impulse.Core.Map;

// Per-node state on the Sector Map. The Sector Core has no underlying card
// (it's its own special card not in cards.tsv). Other nodes start face-down
// with a card from the deck; the home corners are face-up at setup.
public abstract record NodeCardState
{
    public sealed record SectorCore : NodeCardState
    {
        public static readonly SectorCore Instance = new();
    }
    public sealed record FaceDown(int CardId) : NodeCardState;
    public sealed record FaceUp(int CardId) : NodeCardState;
}
