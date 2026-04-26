using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum ExecuteSource { Hand, Deck }

public sealed record ExecuteParams(ExecuteSource Source, int SizeFilter);

// Rulebook p.35: "Execute will let you perform an Action Card's text once
// and then discard it without placing it on the Impulse to be shared with
// others. Alternately, you can activate one of your Techs."
//
// This is the only meta-action: it invokes another card's or tech's effect.
// Implementation maintains a sub-EffectContext inside HandlerState; outer
// Execute forwards engine pause/resume between outer and sub.
public sealed class ExecuteHandler : IEffectHandler
{
    private readonly EffectRegistry _registry;
    private readonly IReadOnlyDictionary<int, ExecuteParams> _byCardId;

    public ExecuteHandler(EffectRegistry registry, IReadOnlyDictionary<int, ExecuteParams> byCardId)
    {
        _registry = registry;
        _byCardId = byCardId;
    }

    private enum Stage { Start, AwaitingSourceChoice, AwaitingHandPick, AwaitingTechSlot, RunningSub, Done }
    private sealed class State
    {
        public Stage Stage;
        public IEffectHandler? SubHandler;
        public EffectContext? SubCtx;
        public int? CardToDiscardOnComplete; // null for tech path
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → execute: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;
        var p = g.Player(ctx.ActivatingPlayer);

        // Boost: per rulebook, [N] on Execute cards is the size filter; boost
        // raises the cap so larger cards become eligible.
        int boost = Boost.FromSource(g, ctx);
        int effectiveSize = prms.SizeFilter + boost;

        if (st.Stage == Stage.Start)
        {
            var options = new List<string>
            {
                prms.Source == ExecuteSource.Hand
                    ? $"Execute size up to {effectiveSize} from hand"
                    : $"Execute size up to {effectiveSize} from deck",
                "Execute one of your techs",
            };
            ctx.PendingChoice = new SelectFromOptionsRequest
            {
                Player = ctx.ActivatingPlayer,
                Options = options,
                Prompt = "Execute what?",
            };
            st.Stage = Stage.AwaitingSourceChoice;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingSourceChoice)
        {
            var req = (SelectFromOptionsRequest)ctx.PendingChoice!;
            int chosen = req.Chosen ?? 0;
            ctx.PendingChoice = null;

            if (chosen == 1)
            {
                // Tech path
                ctx.PendingChoice = new SelectTechSlotRequest
                {
                    Player = ctx.ActivatingPlayer,
                    Prompt = "Choose a tech to execute.",
                };
                st.Stage = Stage.AwaitingTechSlot;
                ctx.Paused = true;
                return true;
            }

            // Card path. Size filter is "up to N" per rulebook p.21.
            if (prms.Source == ExecuteSource.Hand)
            {
                var legal = p.Hand.Where(id => g.CardsById[id].Size <= effectiveSize).ToList();
                if (legal.Count == 0)
                {
                    g.Log.Write($"  → execute: no eligible size≤{effectiveSize} card in hand");
                    ctx.IsComplete = true;
                    return true;
                }
                ctx.PendingChoice = new SelectHandCardRequest
                {
                    Player = ctx.ActivatingPlayer,
                    LegalCardIds = legal,
                    AllowNone = true,
                    Prompt = $"Execute a card size up to {effectiveSize} from hand, or DONE.",
                };
                st.Stage = Stage.AwaitingHandPick;
                ctx.Paused = true;
                return true;
            }

            // Deck source: draw top, check filter
            if (!Mechanics.EnsureDeckCanDraw(g, g.Log))
            {
                g.Log.Write($"  → execute: deck empty");
                ctx.IsComplete = true;
                return true;
            }
            int drawn = g.Deck[0];
            g.Deck.RemoveAt(0);
            var dc = g.CardsById[drawn];
            if (dc.Size > effectiveSize)
            {
                g.Discard.Add(drawn);
                g.Log.Write($"  → drew #{drawn} ({dc.Color}/{dc.Size}) — discard (need size ≤ {effectiveSize})");
                g.Log.EmitReveal(drawn, RevealOutcome.Discarded, $"need size ≤ {effectiveSize}");
                ctx.IsComplete = true;
                return true;
            }
            g.Log.EmitReveal(drawn, RevealOutcome.Kept, "→ execute");
            return BeginCardSubEffect(g, ctx, st, drawn, removeFromHand: false);
        }

        if (st.Stage == Stage.AwaitingHandPick)
        {
            var req = (SelectHandCardRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            if (req.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            return BeginCardSubEffect(g, ctx, st, req.ChosenCardId.Value, removeFromHand: true);
        }

        if (st.Stage == Stage.AwaitingTechSlot)
        {
            var req = (SelectTechSlotRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            var slot = req.Chosen ?? throw new InvalidOperationException("slot not chosen");
            return BeginTechSubEffect(g, ctx, st, slot);
        }

        if (st.Stage == Stage.RunningSub)
        {
            var subCtx = st.SubCtx!;
            var subHandler = st.SubHandler!;
            subCtx.Paused = false;
            subHandler.Execute(g, subCtx);
            var result = MirrorSubState(ctx, subCtx);
            if (subCtx.IsComplete && st.CardToDiscardOnComplete is { } cid)
            {
                g.Discard.Add(cid);
                g.Log.Write($"  → executed #{cid} → discard");
                st.CardToDiscardOnComplete = null;
            }
            return result;
        }

        return false;
    }

    private bool BeginCardSubEffect(GameState g, EffectContext outerCtx, State st, int cardId, bool removeFromHand)
    {
        var card = g.CardsById[cardId];
        var subHandler = _registry.Resolve(card.EffectFamily);
        if (removeFromHand)
        {
            var p = g.Player(outerCtx.ActivatingPlayer);
            p.Hand.Remove(cardId);
        }
        if (subHandler is null)
        {
            g.Log.Write($"  → execute: no handler for #{cardId} ({card.EffectFamily}); discard");
            g.Discard.Add(cardId);
            outerCtx.IsComplete = true;
            return true;
        }

        g.Log.Write($"  → executing #{cardId} ({card.ActionType}/{card.Color}/{card.Size})");
        var subCtx = new EffectContext
        {
            ActivatingPlayer = outerCtx.ActivatingPlayer,
            Source = new EffectSource.ImpulseCard(cardId),
        };
        st.SubHandler = subHandler;
        st.SubCtx = subCtx;
        st.Stage = Stage.RunningSub;
        st.CardToDiscardOnComplete = cardId;

        subHandler.Execute(g, subCtx);
        var result = MirrorSubState(outerCtx, subCtx);
        if (subCtx.IsComplete && st.CardToDiscardOnComplete is { } cid)
        {
            g.Discard.Add(cid);
            g.Log.Write($"  → executed #{cid} → discard");
            st.CardToDiscardOnComplete = null;
        }
        return result;
    }

    private bool BeginTechSubEffect(GameState g, EffectContext outerCtx, State st, TechSlot slot)
    {
        var p = g.Player(outerCtx.ActivatingPlayer);
        var tech = p.Techs[slot];
        IEffectHandler? subHandler;
        int? techCardId = null;
        switch (tech)
        {
            case Tech.Researched r:
                techCardId = r.CardId;
                subHandler = _registry.Resolve(g.CardsById[r.CardId].EffectFamily);
                break;
            case Tech.BasicCommon:
                subHandler = new BasicCommonTechHandler(_registry);
                break;
            case Tech.BasicUnique bu:
                subHandler = bu.Race.Id switch
                {
                    1 => new PiscesishTechHandler(),
                    2 => new AriekTechHandler(),
                    3 => new HerculeseTechHandler(),
                    4 => new DraconiansTechHandler(),
                    5 => new TriangulumnistsTechHandler(),
                    6 => new CaelumnitesTechHandler(),
                    _ => null,
                };
                break;
            default:
                subHandler = null;
                break;
        }

        if (subHandler is null)
        {
            g.Log.Write($"  → execute tech: handler unavailable for {tech.GetType().Name}");
            outerCtx.IsComplete = true;
            return true;
        }

        g.Log.Write($"  → executing tech {slot} ({tech.GetType().Name})");
        var subCtx = new EffectContext
        {
            ActivatingPlayer = outerCtx.ActivatingPlayer,
            Source = new EffectSource.TechEffect(slot, techCardId),
        };
        st.SubHandler = subHandler;
        st.SubCtx = subCtx;
        st.Stage = Stage.RunningSub;
        st.CardToDiscardOnComplete = null; // tech: nothing to discard

        subHandler.Execute(g, subCtx);
        return MirrorSubState(outerCtx, subCtx);
    }

    // Reflect the sub-handler's pause/complete state onto the outer ctx so
    // the engine drives the right next step. PendingChoice is shared by
    // reference so that controller answers reach the sub-handler unchanged.
    private static bool MirrorSubState(EffectContext outer, EffectContext sub)
    {
        if (sub.IsComplete)
        {
            outer.PendingChoice = null;
            outer.IsComplete = true;
            return true;
        }
        if (sub.Paused && sub.PendingChoice is not null)
        {
            outer.PendingChoice = sub.PendingChoice;
            outer.Paused = true;
            return true;
        }
        // Sub returned without progress — bail to avoid a hang.
        outer.IsComplete = true;
        return false;
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

public static class ExecuteRegistrations
{
    public static readonly Dictionary<int, ExecuteParams> ByCardId = new()
    {
        [64] = new(Source: ExecuteSource.Hand, SizeFilter: 2),
        [65] = new(Source: ExecuteSource.Hand, SizeFilter: 2),
        [82] = new(Source: ExecuteSource.Deck, SizeFilter: 1),
        [86] = new(Source: ExecuteSource.Deck, SizeFilter: 1),
        [89] = new(Source: ExecuteSource.Hand, SizeFilter: 1),
        [97] = new(Source: ExecuteSource.Deck, SizeFilter: 1),
    };

    private static readonly string[] Families =
    {
        "execute_size_n_or_tech",
        "execute_size_n_from_deck_or_tech",
        "execute_size_n_from_hand_or_tech",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new ExecuteHandler(r, ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
