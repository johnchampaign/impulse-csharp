using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum SabotageTargetFilter { Transport, Cruiser, Either }

// Rulebook p.36: "Sabotage allows you to destroy enemy ships without
// fighting them head-on. You can only Sabotage ships on cards that you
// patrol with Cruisers or occupy with Transports. For each bomb targeting
// a fleet, reveal 1 card from the deck. For each size 2+ card revealed,
// destroy 1 ship in that fleet, and score a point. Do not score points
// for overkill."
public sealed record SabotageParams(int Bombs, SabotageTargetFilter Target);

public sealed class SabotageHandler : IEffectHandler
{
    private readonly IReadOnlyDictionary<int, SabotageParams> _byCardId;

    public SabotageHandler(IReadOnlyDictionary<int, SabotageParams> byCardId)
    {
        _byCardId = byCardId;
    }

    private enum Stage { Start, AwaitingTarget, Done }
    private sealed class State { public Stage Stage; }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → sabotage: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;

        int boost = Boost.FromSource(g, ctx);
        int effectiveBombs = prms.Bombs + boost;

        if (st.Stage == Stage.Start)
        {
            var legal = LegalTargets(g, ctx.ActivatingPlayer, prms.Target);
            if (legal.Count == 0)
            {
                g.Log.Write($"  → sabotage: no legal target for {ctx.ActivatingPlayer}");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new SelectSabotageTargetRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalTargets = legal,
                Prompt = $"Choose enemy fleet to sabotage with {effectiveBombs} bomb(s)" +
                         (boost > 0 ? $" (+{boost} boost)" : "") + ".",
            };
            st.Stage = Stage.AwaitingTarget;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingTarget)
        {
            var req = (SelectSabotageTargetRequest)ctx.PendingChoice!;
            var target = req.Chosen ?? throw new InvalidOperationException("target not chosen");
            ctx.PendingChoice = null;
            if (target.Owner == ctx.ActivatingPlayer)
                throw new InvalidOperationException(
                    $"Sabotage cannot target own fleet (player {ctx.ActivatingPlayer} at {target.Location})");

            int hits = 0;
            for (int i = 0; i < effectiveBombs; i++)
            {
                if (!Mechanics.EnsureDeckCanDraw(g, g.Log))
                {
                    g.Log.Write($"  → bomb {i + 1}: deck empty, abort");
                    break;
                }
                int drawn = g.Deck[0];
                g.Deck.RemoveAt(0);
                var c = g.CardsById[drawn];
                g.Discard.Add(drawn);
                if (c.Size >= 2)
                {
                    hits++;
                    g.Log.Write($"  → bomb {i + 1}: drew #{drawn} ({c.Color}/{c.Size}) — HIT");
                    g.Log.EmitReveal(drawn, RevealOutcome.Scored, $"hit (size {c.Size})");
                }
                else
                {
                    g.Log.Write($"  → bomb {i + 1}: drew #{drawn} ({c.Color}/{c.Size}) — miss");
                    g.Log.EmitReveal(drawn, RevealOutcome.Discarded, "miss (size 1)");
                }
            }

            // Destroy up to `hits` ships of the target fleet. No overkill —
            // points are awarded only per actual destruction (DestroyShipAt
            // emits +1 prestige per ship to the attacker).
            int fleetSize = g.ShipPlacements.Count(sp =>
                sp.Owner == target.Owner &&
                Mechanics.LocationsEqual(sp.Location, target.Location));
            int toDestroy = Math.Min(hits, fleetSize);
            g.Log.Write($"  → Sabotage: {hits} hit(s), destroying {toDestroy}/{fleetSize} ship(s) of {target.Owner}");
            for (int i = 0; i < toDestroy; i++)
            {
                Mechanics.DestroyShipAt(g, target.Owner, target.Location, ctx.ActivatingPlayer, g.Log);
                if (g.IsGameOver) break;
            }
            ctx.IsComplete = true;
            return true;
        }
        return false;
    }

    private static IReadOnlyList<SabotageTarget> LegalTargets(GameState g, PlayerId mover, SabotageTargetFilter filter)
    {
        var result = new List<SabotageTarget>();

        if (filter != SabotageTargetFilter.Cruiser)
        {
            // Transport-fleet targets: nodes the player controls (patrol or
            // occupy) where some enemy has transports.
            foreach (var node in g.Map.Nodes)
            {
                if (!Movement.PlayerControlsNode(g, mover, node.Id)) continue;
                var enemyOwners = g.ShipPlacements
                    .Where(sp => sp.Owner != mover &&
                                 sp.Location is ShipLocation.OnNode n && n.Node == node.Id)
                    .Select(sp => sp.Owner)
                    .Distinct();
                foreach (var owner in enemyOwners)
                    result.Add(new SabotageTarget(owner, new ShipLocation.OnNode(node.Id)));
            }
        }

        if (filter != SabotageTargetFilter.Transport)
        {
            // Cruiser-fleet targets: gates with enemy cruisers, where the
            // player controls at least one adjacent card.
            foreach (var gate in g.Map.Gates)
            {
                bool controlsEither =
                    Movement.PlayerControlsNode(g, mover, gate.EndpointA) ||
                    Movement.PlayerControlsNode(g, mover, gate.EndpointB);
                if (!controlsEither) continue;
                var enemyOwners = g.ShipPlacements
                    .Where(sp => sp.Owner != mover &&
                                 sp.Location is ShipLocation.OnGate go && go.Gate == gate.Id)
                    .Select(sp => sp.Owner)
                    .Distinct();
                foreach (var owner in enemyOwners)
                    result.Add(new SabotageTarget(owner, new ShipLocation.OnGate(gate.Id)));
            }
        }

        return result;
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

public static class SabotageRegistrations
{
    public static readonly Dictionary<int, SabotageParams> ByCardId = new()
    {
        [9]   = new(Bombs: 3, Target: SabotageTargetFilter.Cruiser),
        [15]  = new(Bombs: 1, Target: SabotageTargetFilter.Either),
        [24]  = new(Bombs: 3, Target: SabotageTargetFilter.Transport),
        [45]  = new(Bombs: 2, Target: SabotageTargetFilter.Either),
        [79]  = new(Bombs: 2, Target: SabotageTargetFilter.Cruiser),
        [104] = new(Bombs: 2, Target: SabotageTargetFilter.Transport),
    };

    private static readonly string[] Families =
    {
        "sabotage_cruiser_fleet_n_bombs",
        "sabotage_fleet_n_bombs",
        "sabotage_transport_fleet_n_bombs",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new SabotageHandler(ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
