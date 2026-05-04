using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum ResearchSource { Hand, Deck, Plan }
public enum ResearchBoostTarget { Count, Size }

public sealed record ResearchParams(
    int Count,
    int? SizeFilter,
    CardColor? ColorFilter,
    ResearchSource Source,
    ResearchBoostTarget BoostTarget,
    bool ThenExecute = false);

public sealed class ResearchHandler : IEffectHandler
{
    private readonly EffectRegistry _registry;
    private readonly IReadOnlyDictionary<int, ResearchParams> _byCardId;

    public ResearchHandler(EffectRegistry registry, IReadOnlyDictionary<int, ResearchParams> byCardId)
    {
        _registry = registry;
        _byCardId = byCardId;
    }

    private sealed class State
    {
        public int Remaining;
        public int? PickedCardId;
        public IEffectHandler? SubHandler;
        public EffectContext? SubCtx;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → research: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        int effectiveCount = prms.BoostTarget == ResearchBoostTarget.Count ? prms.Count + boost : prms.Count;
        int? effectiveSize = prms.BoostTarget == ResearchBoostTarget.Size && prms.SizeFilter is { } s
            ? s + boost : prms.SizeFilter;
        var st = (State?)ctx.HandlerState ?? new State { Remaining = effectiveCount };
        ctx.HandlerState = st;

        // c18 "Then Execute it": once the researched card's effect begins,
        // forward all engine pause/resume cycles to it until it completes.
        if (st.SubCtx is not null && st.SubHandler is not null)
        {
            st.SubCtx.Paused = false;
            st.SubHandler.Execute(g, st.SubCtx);
            if (st.SubCtx.IsComplete)
            {
                ctx.PendingChoice = null;
                ctx.IsComplete = true;
                st.SubHandler = null;
                st.SubCtx = null;
                return true;
            }
            if (st.SubCtx.Paused && st.SubCtx.PendingChoice is not null)
            {
                ctx.PendingChoice = st.SubCtx.PendingChoice;
                ctx.Paused = true;
                return true;
            }
            ctx.IsComplete = true;
            return false;
        }

        // Resume after picking a hand card.
        if (ctx.PendingChoice is SelectHandCardRequest handAns)
        {
            ctx.PendingChoice = null;
            if (handAns.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            st.PickedCardId = handAns.ChosenCardId.Value;
            // Prompt for slot. Hand-source: card stays in hand if skipped.
            ctx.PendingChoice = new SelectTechSlotRequest
            {
                Player = ctx.ActivatingPlayer,
                IncomingCardId = st.PickedCardId,
                AllowSkip = true,
                Prompt = $"Choose a tech slot to overwrite with #{st.PickedCardId}, or SKIP to keep the card in hand.",
            };
            ctx.Paused = true;
            return false;
        }

        // Resume after picking a tech slot.
        if (ctx.PendingChoice is SelectTechSlotRequest slotAns)
        {
            ctx.PendingChoice = null;
            int cardId = st.PickedCardId!.Value;
            // Skip: undo the pick and stop researching for this slot.
            // - Hand source: card stays in hand (it was never removed).
            // - Deck source: card was already drawn, so it's discarded.
            if (slotAns.Chosen is null)
            {
                if (prms.Source == ResearchSource.Deck)
                {
                    g.Discard.Add(cardId);
                    g.Log.Write($"  → research skipped — #{cardId} discarded");
                }
                else if (prms.Source == ResearchSource.Plan)
                {
                    g.Log.Write($"  → research skipped — #{cardId} stays in plan");
                }
                else
                {
                    g.Log.Write($"  → research skipped — #{cardId} stays in hand");
                }
                st.PickedCardId = null;
                st.Remaining--;
                if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }
                // Fall through to draw/prompt the next research slot.
            }
            else
            {
            var slot = slotAns.Chosen.Value;

            // Move card from its source to the tech slot.
            if (prms.Source == ResearchSource.Hand)
            {
                if (!p.Hand.Remove(cardId))
                    throw new InvalidOperationException($"hand missing #{cardId}");
            }
            else if (prms.Source == ResearchSource.Plan)
            {
                // List.Remove preserves the order of remaining items, which
                // matches "leaving the other cards in the same order" (c101).
                if (!p.Plan.Remove(cardId))
                    throw new InvalidOperationException($"plan missing #{cardId}");
            }
            // (deck source: card was already removed when drawn)

            Mechanics.ResearchInto(g, ctx.ActivatingPlayer, slot, cardId, g.Log);

            st.PickedCardId = null;
            st.Remaining--;

            if (prms.ThenExecute)
            {
                // Spin up a sub-effect for the just-researched card. The
                // researched card is now in the tech slot; we execute its
                // effect once. Source = TechEffect with the card id so
                // per-card-param handlers can resolve params.
                var card = g.CardsById[cardId];
                var sub = _registry.Resolve(card.EffectFamily);
                if (sub is null)
                {
                    g.Log.Write($"  → ThenExecute: no handler for #{cardId} ({card.EffectFamily}); skip");
                    ctx.IsComplete = true;
                    return true;
                }
                g.Log.Write($"  → ThenExecute: running #{cardId}");
                var subCtx = new EffectContext
                {
                    ActivatingPlayer = ctx.ActivatingPlayer,
                    Source = new EffectSource.TechEffect(slot, cardId),
                };
                st.SubHandler = sub;
                st.SubCtx = subCtx;
                sub.Execute(g, subCtx);
                if (subCtx.IsComplete)
                {
                    ctx.IsComplete = true;
                    st.SubHandler = null;
                    st.SubCtx = null;
                    return true;
                }
                if (subCtx.Paused && subCtx.PendingChoice is not null)
                {
                    ctx.PendingChoice = subCtx.PendingChoice;
                    ctx.Paused = true;
                    return true;
                }
                ctx.IsComplete = true;
                return false;
            }
            // Loop continues for further deck-draws, or completes for hand source.
            }
        }

        if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }

        // Begin or continue: hand source prompts for hand pick; plan source
        // prompts for plan pick (UI shows the player's Plan as the source);
        // deck source draws the next top card.
        if (prms.Source == ResearchSource.Hand || prms.Source == ResearchSource.Plan)
        {
            var pool = prms.Source == ResearchSource.Hand ? p.Hand : p.Plan;
            var legal = pool
                .Where(id => Matches(g.CardsById[id], effectiveSize, prms.ColorFilter))
                .ToList();
            if (legal.Count == 0) { ctx.IsComplete = true; return true; }
            string fromWhat = prms.Source == ResearchSource.Hand ? "hand" : "Plan";
            ctx.PendingChoice = new SelectHandCardRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalCardIds = legal,
                AllowNone = true,
                Prompt = $"Research a card from your {fromWhat} matching {Describe(effectiveSize, prms.ColorFilter)} ({st.Remaining} remaining), or DONE.",
            };
            ctx.Paused = true;
            return false;
        }

        // Deck source: draw and decide.
        if (!Mechanics.EnsureDeckCanDraw(g, g.Log)) { ctx.IsComplete = true; return true; }
        int drawn = g.Deck[0];
        g.Deck.RemoveAt(0);
        var c = g.CardsById[drawn];
        if (Matches(c, effectiveSize, prms.ColorFilter))
        {
            // Match → prompt slot. Card sits in limbo (not in any zone) until placed.
            st.PickedCardId = drawn;
            g.Log.EmitReveal(drawn, RevealOutcome.Kept, "→ research");
            ctx.PendingChoice = new SelectTechSlotRequest
            {
                Player = ctx.ActivatingPlayer,
                IncomingCardId = drawn,
                AllowSkip = true,
                Prompt = $"Choose a tech slot to overwrite with #{drawn}, or SKIP to discard.",
            };
            ctx.Paused = true;
            return false;
        }
        else
        {
            g.Discard.Add(drawn);
            g.Log.Write($"  → drew #{drawn} ({c.Color}/{c.Size}) — discard ({Describe(effectiveSize, prms.ColorFilter)})");
            g.Log.EmitReveal(drawn, RevealOutcome.Discarded, Describe(effectiveSize, prms.ColorFilter));
            st.Remaining--;
            // Loop within Execute by re-entering — but we'd need to call ourselves.
            // Simpler: return and let engine call us again. But engine only re-enters on pause.
            // So we set Paused=false, IsComplete=false isn't valid. We must complete or loop here.
            // Loop here:
            return Execute(g, ctx);
        }
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

public static class ResearchRegistrations
{
    public static readonly Dictionary<int, ResearchParams> ByCardId = new()
    {
        // "Research one size [1] card" → boost size.
        [2]   = new(Count: 1, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Size),
        [18]  = new(Count: 1, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Size, ThenExecute: true),
        [54]  = new(Count: 1, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Size),
        [77]  = new(Count: 1, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Size),
        [101] = new(Count: 1, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Plan, BoostTarget: ResearchBoostTarget.Size),
        // "Research [1][color] card" → boost count.
        [20]  = new(Count: 1, SizeFilter: null, ColorFilter: CardColor.Green, Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Count),
        [60]  = new(Count: 1, SizeFilter: null, ColorFilter: CardColor.Yellow,Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Count),
        [74]  = new(Count: 1, SizeFilter: null, ColorFilter: CardColor.Blue,  Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Count),
        [76]  = new(Count: 1, SizeFilter: null, ColorFilter: CardColor.Red,   Source: ResearchSource.Hand, BoostTarget: ResearchBoostTarget.Count),
        // "Research two size [1] cards from the deck" → boost size.
        [63]  = new(Count: 2, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Deck, BoostTarget: ResearchBoostTarget.Size),
        [96]  = new(Count: 2, SizeFilter: 1,    ColorFilter: null,            Source: ResearchSource.Deck, BoostTarget: ResearchBoostTarget.Size),
    };

    private static readonly string[] Families =
    {
        "research_size_n_from_hand",
        "research_size_n_from_hand_then_execute",
        "research_n_color_from_hand",
        "research_n_size_from_deck",
        "research_n_from_plan_keep_order",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new ResearchHandler(r, ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
