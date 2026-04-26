using Impulse.Core.Cards;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Rulebook p.22: "You can power up any action by boosting the boxed number
// on a card... if you have Mineral cards in your Command Center matching
// the color of the action card, the number is boosted by 1 for every 2
// gems you have of that color. These are not spent; you keep them."
//
// FAQ p.45: "the level of a boost is calculated before the action begins."
public static class Boost
{
    // Total gems = sum of matching-color minerals' sizes (each icon = 1 gem).
    public static int FromMinerals(GameState g, PlayerId pid, CardColor color)
    {
        var p = g.Player(pid);
        int gems = p.Minerals
            .Where(id => g.CardsById[id].Color == color)
            .Sum(id => g.CardsById[id].Size);
        return gems / 2;
    }

    // Source's underlying card id, or null for Basic techs.
    public static int? CardIdOf(EffectSource source) => source switch
    {
        EffectSource.ImpulseCard ic => ic.CardId,
        EffectSource.PlanCard pc => pc.CardId,
        EffectSource.TechEffect te => te.CardId,
        EffectSource.MapActivation ma => ma.CardId,
        _ => null,
    };

    // Boost using the activating player's minerals matching the source card's
    // color, plus any TransportBonusGems on the context (set when activating
    // a sector-map card with just-moved transports). Total gems / 2.
    public static int FromSource(GameState g, EffectContext ctx)
    {
        var cardId = CardIdOf(ctx.Source);
        int matchingGems = 0;
        if (cardId is { } cid && g.CardsById.TryGetValue(cid, out var card))
        {
            matchingGems = g.Player(ctx.ActivatingPlayer).Minerals
                .Where(id => g.CardsById[id].Color == card.Color)
                .Sum(id => g.CardsById[id].Size);
        }
        return (matchingGems + ctx.TransportBonusGems) / 2;
    }

    // Legacy overload — no transport bonus. Kept for callers that don't have
    // an EffectContext at hand (tests).
    public static int FromSource(GameState g, PlayerId pid, EffectSource source)
    {
        var cardId = CardIdOf(source);
        if (cardId is null) return 0;
        if (!g.CardsById.TryGetValue(cardId.Value, out var card)) return 0;
        return FromMinerals(g, pid, card.Color);
    }
}
