using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Top-level player decisions that advance phases.
public abstract record PlayerAction
{
    public sealed record PlaceImpulse(int CardIdFromHand) : PlayerAction;
    public sealed record SkipTech : PlayerAction;
    public sealed record UseTech(TechSlot Slot) : PlayerAction;
    public sealed record UseImpulseCard : PlayerAction;
    public sealed record SkipImpulseCard : PlayerAction;
    public sealed record SkipPlan : PlayerAction;
    public sealed record UsePlan : PlayerAction;
}
