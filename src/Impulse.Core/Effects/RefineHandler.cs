using Impulse.Core.Cards;
using Impulse.Core.Engine;

namespace Impulse.Core.Effects;

public enum RefineMode { PerGem, Flat }
public enum RefineBoostTarget { Count, PerGemRate, FlatPoints }

public sealed record RefineParams(
    int Count,
    CardColor? Color,
    RefineMode Mode,
    int FlatPoints = 0,
    int PerGemRate = 1,
    RefineBoostTarget BoostTarget = RefineBoostTarget.Count);

public sealed class RefineHandler : IEffectHandler
{
    private readonly IReadOnlyDictionary<int, RefineParams> _byCardId;

    public RefineHandler(IReadOnlyDictionary<int, RefineParams> byCardId)
    {
        _byCardId = byCardId;
    }

    private sealed class State { public int Remaining; }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int cardId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(cardId, out var prms))
        {
            g.Log.Write($"  → refine: no params for #{cardId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        int effectiveCount = prms.BoostTarget == RefineBoostTarget.Count ? prms.Count + boost : prms.Count;
        int effectivePerGemRate = prms.BoostTarget == RefineBoostTarget.PerGemRate ? prms.PerGemRate + boost : prms.PerGemRate;
        int effectiveFlatPoints = prms.BoostTarget == RefineBoostTarget.FlatPoints ? prms.FlatPoints + boost : prms.FlatPoints;
        var st = (State?)ctx.HandlerState ?? new State { Remaining = effectiveCount };
        ctx.HandlerState = st;

        // Resume after a mineral pick
        if (ctx.PendingChoice is SelectMineralCardRequest answered && answered.ChosenCardId is not null)
        {
            int prestige = prms.Mode == RefineMode.PerGem
                ? g.CardsById[answered.ChosenCardId.Value].Size * effectivePerGemRate
                : effectiveFlatPoints;
            Mechanics.RefineMineral(g, ctx.ActivatingPlayer, answered.ChosenCardId.Value, prestige, g.Log);
            st.Remaining--;
            ctx.PendingChoice = null;
            if (g.IsGameOver) { ctx.IsComplete = true; return true; }
        }

        if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }

        var legal = p.Minerals
            .Where(id => prms.Color is null || g.CardsById[id].Color == prms.Color)
            .ToList();
        if (legal.Count == 0)
        {
            g.Log.Write($"  → {ctx.ActivatingPlayer} no matching mineral to refine");
            ctx.IsComplete = true;
            return false;
        }

        ctx.PendingChoice = new SelectMineralCardRequest
        {
            Player = ctx.ActivatingPlayer,
            LegalCardIds = legal,
            Prompt = prms.Color is { } col
                ? $"Refine a {col} mineral card."
                : "Refine a mineral card.",
        };
        ctx.Paused = true;
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

public static class RefineRegistrations
{
    public static readonly Dictionary<int, RefineParams> ByCardId = new()
    {
        // c43 "Refine [1] mineral card for one point per gem." → boost the count.
        [43]  = new(Count: 1, Color: null,                 Mode: RefineMode.PerGem, BoostTarget: RefineBoostTarget.Count),
        // c46/47/67/107 "Refine one [color] mineral card for [1] point per gem." → boost the per-gem rate.
        [46]  = new(Count: 1, Color: CardColor.Yellow,     Mode: RefineMode.PerGem, BoostTarget: RefineBoostTarget.PerGemRate),
        [47]  = new(Count: 1, Color: CardColor.Green,      Mode: RefineMode.PerGem, BoostTarget: RefineBoostTarget.PerGemRate),
        [67]  = new(Count: 1, Color: CardColor.Blue,       Mode: RefineMode.PerGem, BoostTarget: RefineBoostTarget.PerGemRate),
        [107] = new(Count: 1, Color: CardColor.Red,        Mode: RefineMode.PerGem, BoostTarget: RefineBoostTarget.PerGemRate),
        // c106 "Refine one mineral card for [2] points." → boost the flat points.
        [106] = new(Count: 1, Color: null,                 Mode: RefineMode.Flat, FlatPoints: 2, BoostTarget: RefineBoostTarget.FlatPoints),
    };

    private static readonly string[] Families =
    {
        "refine_n_mineral_card_per_gem",
        "refine_n_color_mineral_per_gem",
        "refine_n_mineral_card_flat_points",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new RefineHandler(ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
