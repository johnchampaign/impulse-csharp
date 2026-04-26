using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core;

public enum GamePhase
{
    Setup,
    AddImpulse,
    UseTech,
    ResolveImpulse,
    UsePlan,
    Score,
    Cleanup,
    GameOver,
}

public sealed class GameState
{
    public required SectorMap Map { get; init; }
    public required IReadOnlyDictionary<int, Card> CardsById { get; init; }
    public required IReadOnlyList<PlayerState> Players { get; init; }

    public List<int> Deck { get; init; } = new();
    public List<int> Discard { get; init; } = new();
    public List<int> Impulse { get; init; } = new();
    public int ImpulseCursor { get; set; }

    public List<ShipPlacement> ShipPlacements { get; init; } = new();

    // Per-node face-down/face-up state. Set at setup; mutated by exploration.
    // Sector Core entry stays SectorCore.Instance throughout the game.
    public Dictionary<NodeId, NodeCardState> NodeCards { get; init; } = new();

    public int CurrentTurn { get; set; } = 1;
    public PlayerId ActivePlayer { get; set; }
    public GamePhase Phase { get; set; } = GamePhase.Setup;
    public bool IsGameOver { get; set; }
    // True once the runner has run the per-player initial home-card pick.
    public bool HomePicksDone { get; set; }

    public Random Rng { get; init; } = new();
    public GameLog Log { get; init; } = new();

    // Mid-effect state. Null when not in an effect.
    public EffectContext? PendingEffect { get; set; }

    // True while Phase 4 is walking the Plan. Plan-action handlers add to
    // PlayerState.NextPlan instead of Plan when this flag is set
    // (rulebook p.37: "If you are using your Plan and are required to add
    // more cards to it, they go into a new Plan").
    public bool IsResolvingPlan { get; set; }

    // The plan card currently being resolved during Phase 4 — set after the
    // card is removed from Plan, cleared after the effect completes. UI
    // shows this as the cursor card so the player sees what they're acting on.
    public int? CurrentlyResolvingPlanCardId { get; set; }

    public PlayerState Player(PlayerId id) =>
        Players.First(p => p.Id == id);

    public int NextPlayerIndex()
    {
        int i = Players.ToList().FindIndex(p => p.Id == ActivePlayer);
        return (i + 1) % Players.Count;
    }
}
