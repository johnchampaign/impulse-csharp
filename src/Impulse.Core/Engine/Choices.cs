using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Mid-effect input requests. Handler creates request, controller fills Chosen.
public abstract record ChoiceRequest
{
    public string Prompt { get; init; } = "";
    public bool Cancelled { get; set; }
    // If non-null, the engine asks this player's controller instead of
    // ctx.ActivatingPlayer. Used for battle defender-reinforcement prompts
    // where the active effect's player is the attacker but the prompt is
    // directed at the defender.
    public PlayerId? PromptPlayer { get; init; }
}

public sealed record SelectFleetRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required IReadOnlyList<ShipLocation> LegalLocations { get; init; }
    public ShipLocation? Chosen { get; set; }
}

public sealed record DeclareMoveRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required ShipLocation Origin { get; init; }
    public required int MaxMoves { get; init; }
    // Path is a sequence of locations ending wherever the fleet stops.
    // Length 1..MaxMoves; index i = i-th step destination.
    public required IReadOnlyList<IReadOnlyList<ShipLocation>> LegalPaths { get; init; }
    public IReadOnlyList<ShipLocation>? ChosenPath { get; set; }
}

public sealed record SelectHandCardRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required IReadOnlyList<int> LegalCardIds { get; init; }
    // If true, controller may set ChosenCardId = null to decline (e.g. stop
    // trading). UI surfaces a DONE button.
    public bool AllowNone { get; init; }
    public int? ChosenCardId { get; set; }
}

public sealed record SelectShipPlacementRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required IReadOnlyList<ShipLocation> LegalLocations { get; init; }
    public ShipLocation? Chosen { get; set; }
}

public sealed record SelectMineralCardRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required IReadOnlyList<int> LegalCardIds { get; init; }
    public int? ChosenCardId { get; set; }
}

public sealed record SelectFleetSizeRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required int Min { get; init; }
    public required int Max { get; init; }
    public int? Chosen { get; set; }
}

public sealed record SelectTechSlotRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public TechSlot? Chosen { get; set; }
}

public sealed record SelectFromOptionsRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public int? Chosen { get; set; }
}

public sealed record SabotageTarget(PlayerId Owner, ShipLocation Location);

public sealed record SelectSabotageTargetRequest : ChoiceRequest
{
    public required PlayerId Player { get; init; }
    public required IReadOnlyList<SabotageTarget> LegalTargets { get; init; }
    public SabotageTarget? Chosen { get; set; }
}
