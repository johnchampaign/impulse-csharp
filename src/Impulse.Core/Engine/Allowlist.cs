using Impulse.Core.Cards;

namespace Impulse.Core.Engine;

// Slice B: deck restricted to families that have a registered handler.
// Cards outside the allowlist are pulled from deck/impulse/hands at setup
// (engine doc §10).
public static class Allowlist
{
    public static IReadOnlyList<Card> Filter(
        IReadOnlyList<Card> cards, EffectRegistry registry) =>
        cards.Where(c => registry.IsRegistered(c.EffectFamily)).ToList();
}
