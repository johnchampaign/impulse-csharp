using Impulse.Core.Players;

namespace Impulse.Core.Engine;

public abstract record EffectSource
{
    public sealed record ImpulseCard(int CardId) : EffectSource;
    public sealed record PlanCard(int CardId) : EffectSource;
    // CardId is set when the tech is a Researched card (lets handlers look up
    // their per-card params). Null for Basic techs.
    public sealed record TechEffect(TechSlot Slot, int? CardId = null) : EffectSource;
    // The activated card on a node when transports end movement there
    // (rulebook p.27). Boost calculation includes the just-moved transports
    // as bonus matching gems.
    public sealed record MapActivation(Map.NodeId Node, int CardId) : EffectSource;
}

public sealed class EffectContext
{
    public required PlayerId ActivatingPlayer { get; init; }
    public required EffectSource Source { get; init; }
    public ChoiceRequest? PendingChoice { get; set; }
    public bool Paused { get; set; }
    public object? HandlerState { get; set; }
    public bool IsComplete { get; set; }
    // For sector-map activation: number of transports that JUST moved onto
    // the activated card. Each counts as a +1 matching gem for boost.
    public int TransportBonusGems { get; init; } = 0;
    // How many sector-map activations are stacked above this effect.
    // 0 for the outermost effect (e.g. an Impulse card or Plan card),
    // increments by 1 each time an activation forwards to a sub-effect.
    // Capped (see CommandHandler) to prevent unbounded chains where
    // transports landing on a Command card move more transports onto
    // another Command card, repeating.
    public int ActivationDepth { get; init; } = 0;
    // Set non-null while a handler is awaiting the attacker's choice of
    // defender among multiple eligible enemies (rulebook p.29: "If multiple
    // players patrol the same card, the player moving ships can choose who
    // to fight"). The handler stores the candidate list here when it sets
    // PendingChoice, and consumes it on resume.
    public List<PlayerId>? PendingDefenderCandidates { get; set; }
}
