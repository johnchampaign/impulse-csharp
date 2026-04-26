using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Controllers;

public enum AiPolicy { Greedy, Warrior, CoreRush, Munchkin, Refine }

// Scored AI: each policy biases legal-action selection via a per-action
// Score function. Choice prompts use lightweight heuristics that fall back
// to random for everything not explicitly biased.
public sealed class PolicyController : IPlayerController
{
    private readonly Random _rng;
    public PlayerId Seat { get; }
    public AiPolicy Policy { get; }

    public PolicyController(PlayerId seat, int seed, AiPolicy policy)
    {
        Seat = seat;
        Policy = policy;
        _rng = new Random(seed);
    }

    public PlayerAction PickAction(GameState g, IReadOnlyList<PlayerAction> legal)
    {
        // Score each, take the best (random tiebreak).
        var scored = legal.Select(a => (Action: a, Score: ScoreAction(g, a))).ToList();
        int max = scored.Max(s => s.Score);
        var best = scored.Where(s => s.Score == max).Select(s => s.Action).ToList();
        return best[_rng.Next(best.Count)];
    }

    private int ScoreAction(GameState g, PlayerAction a)
    {
        switch (a)
        {
            case PlayerAction.PlaceImpulse pi:
                return ScoreCard(g.CardsById[pi.CardIdFromHand]);
            case PlayerAction.UseImpulseCard:
                return 2;        // generally do things rather than skip
            case PlayerAction.SkipImpulseCard:
                return 0;
            case PlayerAction.UseTech:
                return 2;
            case PlayerAction.SkipTech:
                return 0;
            case PlayerAction.UsePlan:
                return 2;
            case PlayerAction.SkipPlan:
                return 0;
            default:
                return 1;
        }
    }

    // The card-type bias is the heart of each policy.
    private int ScoreCard(Card c) => Policy switch
    {
        AiPolicy.Greedy => 1, // random
        AiPolicy.Warrior => c.ActionType switch
        {
            CardActionType.Sabotage => 6,
            CardActionType.Command => 4,
            CardActionType.Build => 3,
            _ => 1,
        },
        AiPolicy.CoreRush => c.ActionType switch
        {
            CardActionType.Command => 6,
            CardActionType.Build => 4,
            _ => 1,
        },
        AiPolicy.Munchkin => c.ActionType switch
        {
            CardActionType.Sabotage => 6,
            CardActionType.Command => 3,
            _ => 1,
        },
        AiPolicy.Refine => c.ActionType switch
        {
            CardActionType.Mine => 6,
            CardActionType.Refine => 5,
            CardActionType.Trade => 3,
            _ => 1,
        },
        _ => 1,
    };

    public void AnswerChoice(GameState g, ChoiceRequest request)
    {
        switch (request)
        {
            case SelectFleetRequest f:
                f.Chosen = ChooseFleet(g, f);
                break;
            case DeclareMoveRequest m:
                m.ChosenPath = ChoosePath(g, m);
                break;
            case SelectHandCardRequest h:
                h.ChosenCardId = ChooseHandCard(g, h);
                break;
            case SelectShipPlacementRequest sp:
                sp.Chosen = ChoosePlacement(g, sp);
                break;
            case SelectMineralCardRequest m:
                // Refine prefers highest-size to maximize per-gem yield.
                m.ChosenCardId = Policy == AiPolicy.Refine
                    ? m.LegalCardIds.OrderByDescending(id => g.CardsById[id].Size).First()
                    : m.LegalCardIds[_rng.Next(m.LegalCardIds.Count)];
                break;
            case SelectFleetSizeRequest fs:
                // Warriors and core-rushers send the maximum.
                fs.Chosen = (Policy == AiPolicy.Warrior || Policy == AiPolicy.CoreRush)
                    ? fs.Max
                    : fs.Min + _rng.Next(fs.Max - fs.Min + 1);
                break;
            case SelectTechSlotRequest ts:
                ts.Chosen = _rng.Next(2) == 0 ? TechSlot.Left : TechSlot.Right;
                break;
            case SelectFromOptionsRequest opt:
                opt.Chosen = _rng.Next(opt.Options.Count);
                break;
            case SelectSabotageTargetRequest sab:
                sab.Chosen = ChooseSabotageTarget(g, sab);
                break;
            default:
                throw new NotSupportedException($"unknown choice {request.GetType().Name}");
        }
    }

    private ShipLocation ChooseFleet(GameState g, SelectFleetRequest f)
    {
        // Warrior: prefer fleet adjacent to or co-located with enemies.
        // CoreRush: prefer fleet closest to Sector Core.
        if (Policy == AiPolicy.Warrior)
        {
            var ranked = f.LegalLocations
                .OrderByDescending(loc => EnemyProximityScore(g, loc))
                .ToList();
            return BestRandom(ranked, loc => EnemyProximityScore(g, loc));
        }
        if (Policy == AiPolicy.CoreRush)
        {
            return BestRandom(f.LegalLocations, loc => -DistanceToCore(g, loc));
        }
        return f.LegalLocations[_rng.Next(f.LegalLocations.Count)];
    }

    private IReadOnlyList<ShipLocation> ChoosePath(GameState g, DeclareMoveRequest m)
    {
        if (Policy == AiPolicy.CoreRush)
        {
            return BestRandom(m.LegalPaths, path => -DistanceToCore(g, path[^1]));
        }
        if (Policy == AiPolicy.Warrior)
        {
            return BestRandom(m.LegalPaths, path => EnemyProximityScore(g, path[^1]));
        }
        return m.LegalPaths[_rng.Next(m.LegalPaths.Count)];
    }

    private int? ChooseHandCard(GameState g, SelectHandCardRequest h)
    {
        if (h.LegalCardIds.Count == 0)
            return h.AllowNone ? null : 0;
        // Refine doesn't want to discard mineral-friendly cards (size 1's
        // would be mined, larger trades give points). Prefer largest.
        if (Policy == AiPolicy.Refine)
        {
            var ordered = h.LegalCardIds.OrderByDescending(id => g.CardsById[id].Size).ToList();
            return ordered[0];
        }
        return h.LegalCardIds[_rng.Next(h.LegalCardIds.Count)];
    }

    private ShipLocation ChoosePlacement(GameState g, SelectShipPlacementRequest sp)
    {
        if (Policy == AiPolicy.CoreRush)
        {
            return BestRandom(sp.LegalLocations, loc => -DistanceToCore(g, loc));
        }
        if (Policy == AiPolicy.Warrior)
        {
            // Prefer gates (cruisers) over nodes (transports).
            var gates = sp.LegalLocations.Where(l => l is ShipLocation.OnGate).ToList();
            if (gates.Count > 0) return gates[_rng.Next(gates.Count)];
        }
        return sp.LegalLocations[_rng.Next(sp.LegalLocations.Count)];
    }

    private SabotageTarget ChooseSabotageTarget(GameState g, SelectSabotageTargetRequest sab)
    {
        // Warrior + Munchkin: pick highest-prestige opponent's fleet.
        if (Policy == AiPolicy.Warrior || Policy == AiPolicy.Munchkin)
        {
            return sab.LegalTargets
                .OrderByDescending(t => g.Player(t.Owner).Prestige)
                .ThenBy(_ => _rng.Next())
                .First();
        }
        return sab.LegalTargets[_rng.Next(sab.LegalTargets.Count)];
    }

    // Heuristics
    private int EnemyProximityScore(GameState g, ShipLocation loc)
    {
        var opponentShips = g.ShipPlacements.Where(sp => sp.Owner != Seat).ToList();
        // 2 = same location, 1 = adjacent (shares a node/gate), 0 = elsewhere.
        int score = 0;
        foreach (var es in opponentShips)
        {
            if (Mechanics.LocationsEqual(es.Location, loc)) { score = Math.Max(score, 2); continue; }
            if (Adjacent(g, loc, es.Location)) score = Math.Max(score, 1);
        }
        return score;
    }

    private static bool Adjacent(GameState g, ShipLocation a, ShipLocation b)
    {
        switch (a)
        {
            case ShipLocation.OnNode na:
                if (b is ShipLocation.OnGate gb)
                {
                    var gate = g.Map.Gate(gb.Gate);
                    return gate.EndpointA == na.Node || gate.EndpointB == na.Node;
                }
                return false;
            case ShipLocation.OnGate ga:
                if (b is ShipLocation.OnNode nb)
                {
                    var gate = g.Map.Gate(ga.Gate);
                    return gate.EndpointA == nb.Node || gate.EndpointB == nb.Node;
                }
                if (b is ShipLocation.OnGate gb2)
                {
                    var g1 = g.Map.Gate(ga.Gate);
                    var g2 = g.Map.Gate(gb2.Gate);
                    return g1.EndpointA == g2.EndpointA || g1.EndpointA == g2.EndpointB ||
                           g1.EndpointB == g2.EndpointA || g1.EndpointB == g2.EndpointB;
                }
                return false;
            default:
                return false;
        }
    }

    // Hex (axial) distance from `loc` to the Sector Core.
    private static int DistanceToCore(GameState g, ShipLocation loc)
    {
        var core = g.Map.Node(g.Map.SectorCoreNodeId);
        switch (loc)
        {
            case ShipLocation.OnNode n:
                {
                    var node = g.Map.Node(n.Node);
                    return AxialDistance(node.AxialQ, node.AxialR, core.AxialQ, core.AxialR);
                }
            case ShipLocation.OnGate gloc:
                {
                    var gate = g.Map.Gate(gloc.Gate);
                    var a = g.Map.Node(gate.EndpointA);
                    var b = g.Map.Node(gate.EndpointB);
                    int da = AxialDistance(a.AxialQ, a.AxialR, core.AxialQ, core.AxialR);
                    int db = AxialDistance(b.AxialQ, b.AxialR, core.AxialQ, core.AxialR);
                    return Math.Min(da, db);
                }
        }
        return 99;
    }

    private static int AxialDistance(int q1, int r1, int q2, int r2)
    {
        int dq = q1 - q2, dr = r1 - r2;
        return (Math.Abs(dq) + Math.Abs(dq + dr) + Math.Abs(dr)) / 2;
    }

    private T BestRandom<T>(IReadOnlyList<T> items, Func<T, int> score)
    {
        int max = items.Max(score);
        var best = items.Where(i => score(i) == max).ToList();
        return best[_rng.Next(best.Count)];
    }
}
