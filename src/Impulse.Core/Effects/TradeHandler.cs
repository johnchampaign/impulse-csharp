using Impulse.Core.Cards;
using Impulse.Core.Engine;

namespace Impulse.Core.Effects;

public enum TradeSource { Hand, Deck }

// Trade per rulebook: "Trade allows you to discard cards from your hand or
// the deck to score points. A traded card is worth points equal to its size."
//
// Hand source (e.g. c11 "Trade [1][R] card from your hand"): player MAY
// discard up to MaxCount cards from hand matching the (size, color) filter,
// scoring `card.Size` prestige per traded card.
//
// Deck source (e.g. c95 "Trade [1] size one card from the deck"): draw
// MaxCount from top; each matching card is traded (discard + score size);
// each non-matching card is discarded with no score.
public enum TradeBoostTarget { Count, Size }

public sealed record TradeParams(int MaxCount, int? SizeFilter, CardColor? ColorFilter, TradeSource Source, TradeBoostTarget BoostTarget);

public sealed class TradeHandler : IEffectHandler
{
    private readonly IReadOnlyDictionary<int, TradeParams> _byCardId;

    public TradeHandler(IReadOnlyDictionary<int, TradeParams> byCardId)
    {
        _byCardId = byCardId;
    }

    private sealed class State { public int Remaining; }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → trade: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        int effectiveMax = prms.BoostTarget == TradeBoostTarget.Count ? prms.MaxCount + boost : prms.MaxCount;
        int? effectiveSize = prms.BoostTarget == TradeBoostTarget.Size && prms.SizeFilter is { } s
            ? s + boost : prms.SizeFilter;
        var st = (State?)ctx.HandlerState ?? new State { Remaining = effectiveMax };
        ctx.HandlerState = st;

        // Resume after a hand pick (or decline)
        if (ctx.PendingChoice is SelectHandCardRequest answered)
        {
            ctx.PendingChoice = null;
            if (answered.ChosenCardId is null)
            {
                // Player declined → done trading
                g.Log.Write($"  → {ctx.ActivatingPlayer} stops trading");
                ctx.IsComplete = true;
                return true;
            }
            int cardId = answered.ChosenCardId.Value;
            var traded = g.CardsById[cardId];
            Mechanics.DiscardFromHand(g, ctx.ActivatingPlayer, cardId, g.Log);
            Scoring.AddPrestige(g, ctx.ActivatingPlayer, traded.Size, PrestigeSource.TradedCardIcons, g.Log);
            st.Remaining--;
            if (g.IsGameOver) { ctx.IsComplete = true; return true; }
        }

        if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }

        if (prms.Source == TradeSource.Hand)
        {
            var legal = p.Hand
                .Where(id => Matches(g.CardsById[id], effectiveSize, prms.ColorFilter))
                .ToList();
            if (legal.Count == 0)
            {
                g.Log.Write($"  → {ctx.ActivatingPlayer} no eligible card to trade");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new SelectHandCardRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalCardIds = legal,
                AllowNone = true,
                Prompt = $"Trade a card matching {Describe(effectiveSize, prms.ColorFilter)}, or DONE to stop ({st.Remaining} remaining).",
            };
            ctx.Paused = true;
            return false;
        }

        // Deck source: automatic draw-and-evaluate, no prompts.
        for (int i = 0; i < effectiveMax && Mechanics.EnsureDeckCanDraw(g, g.Log); i++)
        {
            int cardId = g.Deck[0];
            g.Deck.RemoveAt(0);
            var c = g.CardsById[cardId];
            g.Discard.Add(cardId);
            if (Matches(c, effectiveSize, prms.ColorFilter))
            {
                g.Log.Write($"{ctx.ActivatingPlayer} draws #{cardId} ({c.Color}/{c.Size}) → traded ({c.Size} pts)");
                g.Log.EmitReveal(cardId, RevealOutcome.Scored, $"+{c.Size} prestige");
                Scoring.AddPrestige(g, ctx.ActivatingPlayer, c.Size, PrestigeSource.TradedCardIcons, g.Log);
                if (g.IsGameOver) { ctx.IsComplete = true; return true; }
            }
            else
            {
                g.Log.Write($"{ctx.ActivatingPlayer} draws #{cardId} ({c.Color}/{c.Size}) → discard (filter mismatch)");
                g.Log.EmitReveal(cardId, RevealOutcome.Discarded, Describe(effectiveSize, prms.ColorFilter));
            }
        }
        ctx.IsComplete = true;
        return true;
    }

    // Size filter is "up to N" per rulebook p.21.
    private static bool Matches(Card c, int? size, CardColor? color) =>
        (size is null || c.Size <= size) &&
        (color is null || c.Color == color);

    private static string Describe(int? size, CardColor? color)
    {
        var parts = new List<string>();
        if (size is { } s) parts.Add($"size up to {s}");
        if (color is { } col) parts.Add($"color {col}");
        return parts.Count == 0 ? "any" : string.Join(", ", parts);
    }

    private static int SourceCardId(EffectSource src) => src switch
    {
        EffectSource.ImpulseCard ic => ic.CardId,
        EffectSource.PlanCard pc => pc.CardId,
        EffectSource.TechEffect te => te.CardId ?? 0,
        EffectSource.MapActivation ma => ma.CardId,
        _ => 0,
    };
}

public static class TradeRegistrations
{
    public static readonly Dictionary<int, TradeParams> ByCardId = new()
    {
        // [N][color] cards: boost the count.
        [11] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Red,    Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        [16] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Blue,   Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        [17] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Green,  Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        [28] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Yellow, Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        [36] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Yellow, Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        [53] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Blue,   Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        // c68 "Trade two size [1]" → boost size.
        [68] = new(MaxCount: 2, SizeFilter: 1,    ColorFilter: null,             Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Size),
        // c83 "Trade [1] size three" → boost count.
        [83] = new(MaxCount: 1, SizeFilter: 3,    ColorFilter: null,             Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        [84] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Red,    Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
        // c95 "Trade [1] size one card from the deck" → boost count.
        [95] = new(MaxCount: 1, SizeFilter: 1,    ColorFilter: null,             Source: TradeSource.Deck, BoostTarget: TradeBoostTarget.Count),
        [99] = new(MaxCount: 1, SizeFilter: null, ColorFilter: CardColor.Green,  Source: TradeSource.Hand, BoostTarget: TradeBoostTarget.Count),
    };

    private static readonly string[] Families =
    {
        "trade_n_color_from_hand",
        "trade_n_size_from_hand",
        "trade_n_size_from_deck",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new TradeHandler(ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
