using Impulse.Core.Cards;
using Impulse.Core.Engine;

namespace Impulse.Core.Effects;

public enum PlanFromDeckBoostTarget { Count, Size }

public abstract record PlanParams
{
    // Up to MaxCount cards from hand matching the filter, each chosen by the
    // player. Player may stop early. Boost adds to MaxCount.
    public sealed record FromHand(int MaxCount, int? SizeFilter, CardColor? ColorFilter) : PlanParams;

    // Up to MaxCount cards from hand, each must be a different color from
    // any card already picked in this action. Boost adds to MaxCount.
    public sealed record DifferentColorsFromHand(int MaxCount) : PlanParams;

    // Draw Count from top of deck. Matching cards go to Plan; non-matching
    // are discarded (per rulebook p.23 "from the deck" rule). Boost adds to
    // either Count or Size depending on which is in brackets on the card.
    public sealed record FromDeck(int Count, int? SizeFilter, CardColor? ColorFilter,
        PlanFromDeckBoostTarget BoostTarget = PlanFromDeckBoostTarget.Count) : PlanParams;

    // Draw 1 from top unconditionally (added to Plan), then draw `ThenCount`
    // more from top with color filter = the first card's color. Boost adds
    // to ThenCount.
    public sealed record CardThenSameColorFromDeck(int ThenCount) : PlanParams;
}

public sealed class PlanHandler : IEffectHandler
{
    private readonly IReadOnlyDictionary<int, PlanParams> _byCardId;

    public PlanHandler(IReadOnlyDictionary<int, PlanParams> byCardId)
    {
        _byCardId = byCardId;
    }

    private sealed class State { public int Remaining; public List<int> PickedSoFar = new(); }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → plan: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }

        return prms switch
        {
            PlanParams.FromHand fh                        => HandlePlanFromHand(g, ctx, fh),
            PlanParams.DifferentColorsFromHand dch        => HandlePlanDifferentColors(g, ctx, dch),
            PlanParams.FromDeck fd                        => HandlePlanFromDeck(g, ctx, fd),
            PlanParams.CardThenSameColorFromDeck ctsc     => HandlePlanCardThenSameColor(g, ctx, ctsc),
            _                                             => Bail(ctx),
        };
    }

    private static bool Bail(EffectContext ctx) { ctx.IsComplete = true; return false; }

    private static bool HandlePlanFromHand(GameState g, EffectContext ctx, PlanParams.FromHand fh)
    {
        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        int effectiveMax = fh.MaxCount + boost;
        var st = (State?)ctx.HandlerState ?? new State { Remaining = effectiveMax };
        ctx.HandlerState = st;

        if (ctx.PendingChoice is SelectHandCardRequest answered)
        {
            ctx.PendingChoice = null;
            if (answered.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            int picked = answered.ChosenCardId.Value;
            p.Hand.Remove(picked);
            Mechanics.AddCardToPlan(g, ctx.ActivatingPlayer, picked, g.Log);
            st.Remaining--;
        }

        if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }

        var legal = p.Hand
            .Where(id => Matches(g.CardsById[id], fh.SizeFilter, fh.ColorFilter))
            .ToList();
        if (legal.Count == 0) { ctx.IsComplete = true; return true; }

        ctx.PendingChoice = new SelectHandCardRequest
        {
            Player = ctx.ActivatingPlayer,
            LegalCardIds = legal,
            AllowNone = true,
            Prompt = $"Plan a card matching {Describe(fh.SizeFilter, fh.ColorFilter)} ({st.Remaining} remaining)" +
                     (boost > 0 ? $" [+{boost} boost]" : "") + ", or DONE.",
        };
        ctx.Paused = true;
        return false;
    }

    private static bool HandlePlanDifferentColors(GameState g, EffectContext ctx, PlanParams.DifferentColorsFromHand prms)
    {
        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        var st = (State?)ctx.HandlerState ?? new State { Remaining = prms.MaxCount + boost };
        ctx.HandlerState = st;

        if (ctx.PendingChoice is SelectHandCardRequest answered)
        {
            ctx.PendingChoice = null;
            if (answered.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            int picked = answered.ChosenCardId.Value;
            p.Hand.Remove(picked);
            st.PickedSoFar.Add(picked);
            Mechanics.AddCardToPlan(g, ctx.ActivatingPlayer, picked, g.Log);
            st.Remaining--;
        }

        if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }

        var usedColors = st.PickedSoFar
            .Select(id => g.CardsById[id].Color)
            .ToHashSet();
        var legal = p.Hand
            .Where(id => !usedColors.Contains(g.CardsById[id].Color))
            .ToList();
        if (legal.Count == 0) { ctx.IsComplete = true; return true; }

        ctx.PendingChoice = new SelectHandCardRequest
        {
            Player = ctx.ActivatingPlayer,
            LegalCardIds = legal,
            AllowNone = true,
            Prompt = $"Plan a card of a NEW color ({st.Remaining} remaining), or DONE.",
        };
        ctx.Paused = true;
        return false;
    }

    private static bool HandlePlanFromDeck(GameState g, EffectContext ctx, PlanParams.FromDeck fd)
    {
        int boost = Boost.FromSource(g, ctx);
        int effectiveCount = fd.BoostTarget == PlanFromDeckBoostTarget.Count ? fd.Count + boost : fd.Count;
        int? effectiveSize = fd.BoostTarget == PlanFromDeckBoostTarget.Size && fd.SizeFilter is { } s
            ? s + boost : fd.SizeFilter;
        for (int i = 0; i < effectiveCount && Mechanics.EnsureDeckCanDraw(g, g.Log); i++)
        {
            int cardId = g.Deck[0];
            g.Deck.RemoveAt(0);
            var c = g.CardsById[cardId];
            if (Matches(c, effectiveSize, fd.ColorFilter))
            {
                Mechanics.AddCardToPlan(g, ctx.ActivatingPlayer, cardId, g.Log);
                g.Log.EmitReveal(cardId, RevealOutcome.Kept, "→ Plan");
            }
            else
            {
                g.Discard.Add(cardId);
                g.Log.Write($"  → drew #{cardId} ({c.Color}/{c.Size}) — discard ({Describe(effectiveSize, fd.ColorFilter)})");
                g.Log.EmitReveal(cardId, RevealOutcome.Discarded, Describe(effectiveSize, fd.ColorFilter));
            }
        }
        ctx.IsComplete = true;
        return true;
    }

    private static bool HandlePlanCardThenSameColor(GameState g, EffectContext ctx, PlanParams.CardThenSameColorFromDeck prms)
    {
        if (!Mechanics.EnsureDeckCanDraw(g, g.Log)) { ctx.IsComplete = true; return true; }
        int boost = Boost.FromSource(g, ctx);
        int effectiveThen = prms.ThenCount + boost;

        int firstId = g.Deck[0];
        g.Deck.RemoveAt(0);
        var first = g.CardsById[firstId];
        Mechanics.AddCardToPlan(g, ctx.ActivatingPlayer, firstId, g.Log);
        g.Log.EmitReveal(firstId, RevealOutcome.Kept, "→ Plan (anchor)");

        var anchorColor = first.Color;
        for (int i = 0; i < effectiveThen && Mechanics.EnsureDeckCanDraw(g, g.Log); i++)
        {
            int cardId = g.Deck[0];
            g.Deck.RemoveAt(0);
            var c = g.CardsById[cardId];
            if (c.Color == anchorColor)
            {
                Mechanics.AddCardToPlan(g, ctx.ActivatingPlayer, cardId, g.Log);
                g.Log.EmitReveal(cardId, RevealOutcome.Kept, $"→ Plan (matches {anchorColor})");
            }
            else
            {
                g.Discard.Add(cardId);
                g.Log.Write($"  → drew #{cardId} ({c.Color}/{c.Size}) — discard (need {anchorColor})");
                g.Log.EmitReveal(cardId, RevealOutcome.Discarded, $"need {anchorColor}");
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

public static class PlanRegistrations
{
    public static readonly Dictionary<int, PlanParams> ByCardId = new()
    {
        [8]  = new PlanParams.FromHand(MaxCount: 2, SizeFilter: null, ColorFilter: CardColor.Blue),
        [14] = new PlanParams.FromHand(MaxCount: 2, SizeFilter: null, ColorFilter: CardColor.Yellow),
        [25] = new PlanParams.FromHand(MaxCount: 2, SizeFilter: null, ColorFilter: CardColor.Red),
        [34] = new PlanParams.DifferentColorsFromHand(MaxCount: 2),
        // c49 "Plan two size [1] cards from the deck." → boost the size filter.
        [49] = new PlanParams.FromDeck(Count: 2, SizeFilter: 1, ColorFilter: null, BoostTarget: PlanFromDeckBoostTarget.Size),
        [50] = new PlanParams.FromHand(MaxCount: 2, SizeFilter: 1, ColorFilter: null),
        [51] = new PlanParams.CardThenSameColorFromDeck(ThenCount: 2),
        [59] = new PlanParams.FromHand(MaxCount: 2, SizeFilter: null, ColorFilter: CardColor.Green),
    };

    private static readonly string[] Families =
    {
        "plan_n_color_from_hand",
        "plan_n_different_colors",
        "plan_n_size_from_deck",
        "plan_n_size_from_hand",
        "plan_card_then_n_same_color_from_deck",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new PlanHandler(ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
