using Impulse.Core.Cards;
using Impulse.Core.Engine;

namespace Impulse.Core.Effects;

// Per rulebook (R/G/B/Y filter tooltip): "if a non-matching card is drawn,
// it would be discarded." So Draw cards: draw N from top, keep matching to
// hand, discard non-matching.
public sealed record DrawOp(int Count, int? SizeFilter, IReadOnlySet<CardColor>? ColorFilter);

// BoostOpIndex: which op's count gets the mineral boost. -1 = no boost.
public sealed record DrawParams(IReadOnlyList<DrawOp> Ops, int BoostOpIndex = 0);

public sealed class DrawHandler : IEffectHandler
{
    private readonly IReadOnlyDictionary<int, DrawParams> _byCardId;

    public DrawHandler(IReadOnlyDictionary<int, DrawParams> byCardId)
    {
        _byCardId = byCardId;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → draw: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }

        var p = g.Player(ctx.ActivatingPlayer);
        int boost = Boost.FromSource(g, ctx);
        for (int opIdx = 0; opIdx < prms.Ops.Count; opIdx++)
        {
            var op = prms.Ops[opIdx];
            int effectiveCount = op.Count + (opIdx == prms.BoostOpIndex ? boost : 0);
            for (int i = 0; i < effectiveCount; i++)
            {
                if (!Mechanics.EnsureDeckCanDraw(g, g.Log))
                {
                    g.Log.Write($"  → deck empty; draw aborts");
                    ctx.IsComplete = true;
                    return true;
                }
                int cardId = g.Deck[0];
                g.Deck.RemoveAt(0);
                var c = g.CardsById[cardId];
                // Size filter is "up to N" per rulebook p.21.
                bool sizeOk = op.SizeFilter is null || c.Size <= op.SizeFilter;
                bool colorOk = op.ColorFilter is null || op.ColorFilter.Contains(c.Color);
                if (sizeOk && colorOk)
                {
                    if (p.Hand.Count >= Mechanics.HandLimit)
                    {
                        g.Discard.Add(cardId);
                        g.Log.Write($"{ctx.ActivatingPlayer} drew #{cardId} ({c.Color}/{c.Size}) → discard (hand limit {Mechanics.HandLimit})");
                        g.Log.EmitReveal(cardId, RevealOutcome.Discarded, "hand full");
                    }
                    else
                    {
                        p.Hand.Add(cardId);
                        g.Log.Write($"{ctx.ActivatingPlayer} drew #{cardId} ({c.Color}/{c.Size}) → hand");
                        g.Log.EmitReveal(cardId, RevealOutcome.Kept);
                    }
                }
                else
                {
                    g.Discard.Add(cardId);
                    g.Log.Write($"{ctx.ActivatingPlayer} drew #{cardId} ({c.Color}/{c.Size}) → discard ({Describe(op)})");
                    g.Log.EmitReveal(cardId, RevealOutcome.Discarded, Describe(op));
                }
            }
        }
        ctx.IsComplete = true;
        return true;
    }

    private static int SourceCardId(EffectSource src) => src switch
    {
        EffectSource.ImpulseCard ic => ic.CardId,
        EffectSource.PlanCard pc => pc.CardId,
        EffectSource.TechEffect te => te.CardId ?? 0,
        EffectSource.MapActivation ma => ma.CardId,
        _ => 0,
    };

    private static string Describe(DrawOp op)
    {
        var parts = new List<string>();
        if (op.SizeFilter is { } s) parts.Add($"size {s}");
        if (op.ColorFilter is { } col) parts.Add($"color {string.Join("/", col)}");
        return parts.Count == 0 ? "no filter" : string.Join(", ", parts);
    }
}

public static class DrawRegistrations
{
    private static readonly IReadOnlySet<CardColor> RY = new HashSet<CardColor> { CardColor.Red, CardColor.Yellow };
    private static readonly IReadOnlySet<CardColor> RG = new HashSet<CardColor> { CardColor.Red, CardColor.Green };
    private static readonly IReadOnlySet<CardColor> BR = new HashSet<CardColor> { CardColor.Blue, CardColor.Red };
    private static readonly IReadOnlySet<CardColor> GY = new HashSet<CardColor> { CardColor.Green, CardColor.Yellow };
    private static readonly IReadOnlySet<CardColor> BY = new HashSet<CardColor> { CardColor.Blue, CardColor.Yellow };
    private static readonly IReadOnlySet<CardColor> BG = new HashSet<CardColor> { CardColor.Blue, CardColor.Green };

    public static readonly Dictionary<int, DrawParams> ByCardId = new()
    {
        // c1/etc: "Draw one [literal]; then draw [1] of color X/Y." Boost the second op.
        [1]   = new(new[] { new DrawOp(1, null, null), new DrawOp(1, null, RY) }, BoostOpIndex: 1),
        [3]   = new(new[] { new DrawOp(1, null, null), new DrawOp(1, null, RG) }, BoostOpIndex: 1),
        [80]  = new(new[] { new DrawOp(1, null, null), new DrawOp(1, null, BR) }, BoostOpIndex: 1),
        [87]  = new(new[] { new DrawOp(1, null, null), new DrawOp(1, null, GY) }, BoostOpIndex: 1),
        [103] = new(new[] { new DrawOp(1, null, null), new DrawOp(1, null, BY) }, BoostOpIndex: 1),
        [108] = new(new[] { new DrawOp(1, null, null), new DrawOp(1, null, BG) }, BoostOpIndex: 1),
        // Single-op cards: boost the only op.
        [5]   = new(new[] { new DrawOp(2, null, null) }),
        [66]  = new(new[] { new DrawOp(3, 1,    null) }),
        [90]  = new(new[] { new DrawOp(3, 1,    null) }),
    };

    private static readonly string[] Families =
    {
        "draw_one_then_one_of_two_colors",
        "draw_n_from_deck",
        "draw_n_size_from_deck",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new DrawHandler(ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
