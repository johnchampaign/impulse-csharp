using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum MineSource { Hand, Deck }
public enum MineBoostTarget { Count, Size }

public sealed record MineParams(int Count, int? Size, CardColor? Color, MineSource Source, MineBoostTarget BoostTarget);

public sealed class MineHandler : IEffectHandler
{
    private readonly IReadOnlyDictionary<int, MineParams> _byCardId;

    public MineHandler(IReadOnlyDictionary<int, MineParams> byCardId)
    {
        _byCardId = byCardId;
    }

    private sealed class State { public int Remaining; }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int cardId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(cardId, out var prms))
        {
            g.Log.Write($"  → mine: no params for #{cardId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        int effectiveCount = prms.BoostTarget == MineBoostTarget.Count ? prms.Count + boost : prms.Count;
        int? effectiveSize = prms.BoostTarget == MineBoostTarget.Size && prms.Size is { } s
            ? s + boost : prms.Size;
        var st = (State?)ctx.HandlerState ?? new State { Remaining = effectiveCount };
        ctx.HandlerState = st;

        // Resume after a hand pick
        if (ctx.PendingChoice is SelectHandCardRequest answered && answered.ChosenCardId is not null)
        {
            Mechanics.MoveCardFromHandToMinerals(g, ctx.ActivatingPlayer, answered.ChosenCardId.Value, g.Log);
            st.Remaining--;
            ctx.PendingChoice = null;
        }

        if (st.Remaining <= 0) { ctx.IsComplete = true; return true; }

        if (prms.Source == MineSource.Hand)
        {
            var legal = p.Hand.Where(id => Matches(g.CardsById[id], effectiveSize, prms.Color)).ToList();
            if (legal.Count == 0)
            {
                g.Log.Write($"  → {ctx.ActivatingPlayer} no eligible card in hand to mine");
                ctx.IsComplete = true;
                return false;
            }
            ctx.PendingChoice = new SelectHandCardRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalCardIds = legal,
                Prompt = $"Mine a card matching {Describe(effectiveSize, prms.Color)}.",
            };
            ctx.Paused = true;
            return false;
        }

        // Deck source: draw `Count` from top of deck. Each matching card is
        // mined (to minerals); each non-matching card is discarded.
        // Per rulebook: "if a non-matching card is drawn, it would be discarded."
        for (int i = 0; i < effectiveCount && Mechanics.EnsureDeckCanDraw(g, g.Log); i++)
        {
            int drawn = g.Deck[0];
            g.Deck.RemoveAt(0);
            var c = g.CardsById[drawn];
            if (Matches(c, effectiveSize, prms.Color))
            {
                p.Minerals.Add(drawn);
                g.Log.Write($"{ctx.ActivatingPlayer} draws #{drawn} ({c.Color}/{c.Size}) → minerals");
                g.Log.EmitReveal(drawn, RevealOutcome.Mined);
            }
            else
            {
                g.Discard.Add(drawn);
                g.Log.Write($"{ctx.ActivatingPlayer} draws #{drawn} ({c.Color}/{c.Size}) → discard (filter mismatch)");
                g.Log.EmitReveal(drawn, RevealOutcome.Discarded, Describe(effectiveSize, prms.Color));
            }
        }
        ctx.IsComplete = true;
        return true;
    }

    // Size filter is "up to N" per rulebook p.21.
    private static bool Matches(Card c, int? size, CardColor? color) =>
        (size is null || c.Size <= size) &&
        (color is null || c.Color == color);

    private static string Describe(int? size, CardColor? color) =>
        (size is { } s ? $"size up to {s}" : null) is { } a
            ? (color is { } col ? $"{a} + color {col}" : a)
            : (color is { } col2 ? $"color {col2}" : "any");

    private static int SourceCardId(EffectSource src) => src switch
    {
        EffectSource.ImpulseCard ic => ic.CardId,
        EffectSource.PlanCard pc => pc.CardId,
        EffectSource.TechEffect te => te.CardId ?? 0,
        EffectSource.MapActivation ma => ma.CardId,
        _ => 0,
    };
}

public static class MineRegistrations
{
    // BoostTarget identifies which [N] in the card text gets boosted:
    //  - "Mine [N] cards / size X" → BT: Count
    //  - "Mine X cards size [N]"  → BT: Size
    //  - "Mine [N][color] card"   → BT: Count
    public static readonly Dictionary<int, MineParams> ByCardId = new()
    {
        [7]   = new(Count: 1, Size: 1,    Color: null,            Source: MineSource.Hand, BoostTarget: MineBoostTarget.Size),
        [19]  = new(Count: 1, Size: 2,    Color: null,            Source: MineSource.Deck, BoostTarget: MineBoostTarget.Count),
        [23]  = new(Count: 1, Size: null, Color: CardColor.Red,    Source: MineSource.Hand, BoostTarget: MineBoostTarget.Count),
        [29]  = new(Count: 1, Size: null, Color: CardColor.Blue,   Source: MineSource.Hand, BoostTarget: MineBoostTarget.Count),
        [58]  = new(Count: 1, Size: 1,    Color: null,            Source: MineSource.Hand, BoostTarget: MineBoostTarget.Size),
        [61]  = new(Count: 2, Size: 1,    Color: null,            Source: MineSource.Deck, BoostTarget: MineBoostTarget.Size),
        [72]  = new(Count: 1, Size: null, Color: CardColor.Yellow, Source: MineSource.Hand, BoostTarget: MineBoostTarget.Count),
        [73]  = new(Count: 1, Size: 1,    Color: null,            Source: MineSource.Deck, BoostTarget: MineBoostTarget.Size),
        [85]  = new(Count: 1, Size: null, Color: CardColor.Green,  Source: MineSource.Hand, BoostTarget: MineBoostTarget.Count),
        [92]  = new(Count: 1, Size: 2,    Color: null,            Source: MineSource.Hand, BoostTarget: MineBoostTarget.Count),
        [93]  = new(Count: 2, Size: 1,    Color: null,            Source: MineSource.Hand, BoostTarget: MineBoostTarget.Size),
        [102] = new(Count: 1, Size: 1,    Color: null,            Source: MineSource.Deck, BoostTarget: MineBoostTarget.Size),
    };

    private static readonly string[] Families =
    {
        "mine_size_n_from_hand",
        "mine_n_color_from_hand",
        "mine_size_n_from_deck",
        "mine_n_size_from_deck",
        "mine_n_size_from_hand",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new MineHandler(ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
