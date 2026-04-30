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
                return ScoreCard(g, g.CardsById[pi.CardIdFromHand]);
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

    // State-aware card scoring. Each policy applies a base bias by
    // CardActionType, then adjusts up or down based on whether the
    // action is actually useful in the current game state — e.g. a
    // Refine card is worthless without minerals, a Mine card without
    // matching size-1 hand cards, a Sabotage without a target.
    private int ScoreCard(GameState g, Card c)
    {
        var me = g.Player(Seat);
        bool meTrails = LeaderId(g) is { } leader && leader != Seat;

        // Per-policy base bias.
        int baseScore = Policy switch
        {
            // Greedy: actually greedy — prefer immediate-prestige actions,
            // ranked by typical points-per-use.
            AiPolicy.Greedy => c.ActionType switch
            {
                CardActionType.Trade => 6,    // +1 per icon, easy points
                CardActionType.Refine => 6,   // direct prestige
                CardActionType.Sabotage => 5, // +1 per ship destroyed
                CardActionType.Command => 4,  // patrols core gates
                CardActionType.Build => 4,    // adds combat material
                CardActionType.Mine => 3,     // sets up future Refine
                CardActionType.Execute => 3,
                CardActionType.Research => 2,
                CardActionType.Plan => 2,     // delayed prestige
                CardActionType.Draw => 1,     // weakest card type
                _ => 1,
            },
            AiPolicy.Warrior => c.ActionType switch
            {
                CardActionType.Sabotage => 7,
                CardActionType.Command => 5,
                CardActionType.Build => 4,    // build cruisers for war
                CardActionType.Trade => 2,
                CardActionType.Refine => 2,
                _ => 1,
            },
            AiPolicy.CoreRush => c.ActionType switch
            {
                CardActionType.Command => 7,
                CardActionType.Build => 5,
                CardActionType.Trade => 2,
                CardActionType.Refine => 2,
                _ => 1,
            },
            AiPolicy.Munchkin => c.ActionType switch
            {
                CardActionType.Sabotage => 7,
                CardActionType.Command => 4,  // for attacking leader
                CardActionType.Build => 3,
                _ => 1,
            },
            AiPolicy.Refine => c.ActionType switch
            {
                CardActionType.Mine => 7,
                CardActionType.Refine => 7,
                CardActionType.Trade => 4,
                CardActionType.Build => 2,
                _ => 1,
            },
            _ => 1,
        };

        // State-aware adjustments — penalise cards that won't fire usefully
        // in the current game state.
        switch (c.ActionType)
        {
            case CardActionType.Refine:
                // Worthless without minerals.
                if (me.Minerals.Count == 0) return 1;
                // Per-gem cards reward higher-size minerals.
                int bestMineralSize = me.Minerals.Max(id => g.CardsById[id].Size);
                baseScore += bestMineralSize - 1;
                break;
            case CardActionType.Mine:
                // Mine cards usually filter on size-1 hand cards.
                int sizeOnes = me.Hand.Count(id => g.CardsById[id].Size == 1);
                if (sizeOnes == 0) baseScore -= 2;
                break;
            case CardActionType.Trade:
                // Trade scores by icons; emptier hand = less to trade.
                if (me.Hand.Count <= 1) baseScore -= 2;
                break;
            case CardActionType.Sabotage:
                // No legal target = no score.
                if (!HasAnyEnemyShip(g)) return 1;
                // Asymmetric upside: failed bombs cost nothing; successful
                // bombs both score prestige and destroy ships. Always good
                // when there's a target. Bonus when there's a fat fleet to
                // hit (more ships destroyed = more prestige scored).
                int biggestFleet = g.ShipPlacements
                    .Where(sp => sp.Owner != Seat)
                    .GroupBy(sp => (sp.Owner, sp.Location switch
                    {
                        ShipLocation.OnNode n => (0, n.Node.Value),
                        ShipLocation.OnGate gateLoc => (1, gateLoc.Gate.Value),
                        _ => (-1, 0),
                    }))
                    .Select(grp => grp.Count())
                    .DefaultIfEmpty(0)
                    .Max();
                baseScore += Math.Min(biggestFleet, 3);
                if (meTrails && (Policy == AiPolicy.Munchkin || Policy == AiPolicy.Warrior))
                    baseScore += 2;
                // Player-count modulation: at low counts each opponent's
                // ship loss is a much bigger fraction of total opposition,
                // so sabotage is more impactful. At 6p the marginal damage
                // to one opponent matters less.
                if (g.Players.Count <= 2) baseScore += 2;
                else if (g.Players.Count >= 5) baseScore -= 1;
                break;
            case CardActionType.Build:
                // Build needs ships available.
                if (me.ShipsAvailable <= 0) return 1;
                break;
            case CardActionType.Command:
                // Command needs ships on the board to move.
                if (g.ShipPlacements.Count(sp => sp.Owner == Seat) == 0) return 1;
                break;
            case CardActionType.Plan:
                // Late-game (somebody is close to winning) prefer immediate
                // prestige over deferred.
                if (g.Players.Any(p => p.Prestige >= Scoring.WinThreshold - 4))
                    baseScore -= 2;
                break;
        }
        return Math.Max(1, baseScore);
    }

    private bool HasAnyEnemyShip(GameState g) =>
        g.ShipPlacements.Any(sp => sp.Owner != Seat);

    // The current leader by prestige (excluding self), or null if all tied.
    // Returns null at 5+ players: the leader rotates often enough that
    // fixed targeting underperforms simple Sector Core scoring. (Bench
    // shows Munchkin's win rate dropping from 39% at 2p to 13% at 6p
    // when always pursuing a leader.)
    private PlayerId? LeaderId(GameState g)
    {
        if (g.Players.Count >= 5) return null;
        var others = g.Players.Where(p => p.Id != Seat).ToList();
        if (others.Count == 0) return null;
        int max = others.Max(p => p.Prestige);
        var top = others.Where(p => p.Prestige == max).ToList();
        return top.Count == 1 ? top[0].Id : null;
    }

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
                ts.Chosen = ChooseTechSlot(g, ts);
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
        // Universal: penalise cruisers sitting on Sector Core gates as
        // origins. Each one earns +1 prestige per turn from Phase 5
        // patrol scoring; moving them off forfeits future income. Only
        // override when no other origin is offered.
        var coreGates = g.Map.AdjacencyByNode[g.Map.SectorCoreNodeId]
            .Select(gate => gate.Id)
            .ToHashSet();
        int CoreGateStickiness(ShipLocation loc) =>
            loc is ShipLocation.OnGate gateLoc && coreGates.Contains(gateLoc.Gate)
                ? -10 : 0;

        if (Policy == AiPolicy.Warrior)
        {
            return BestRandom(f.LegalLocations,
                loc => EnemyProximityScore(g, loc) + CoreGateStickiness(loc));
        }
        if (Policy == AiPolicy.CoreRush)
        {
            return BestRandom(f.LegalLocations,
                loc => -DistanceToCore(g, loc) + CoreGateStickiness(loc));
        }
        if (Policy == AiPolicy.Munchkin)
        {
            if (LeaderId(g) is { } leader)
                return BestRandom(f.LegalLocations,
                    loc => LeaderProximityScore(g, leader, loc) + CoreGateStickiness(loc));
            // No clear leader (e.g. 5+ players): fall back to core-pursuit
            // so Munchkin doesn't degrade to random in big games.
            return BestRandom(f.LegalLocations,
                loc => -DistanceToCore(g, loc) + CoreGateStickiness(loc));
        }
        if (Policy == AiPolicy.Greedy)
        {
            return BestRandom(f.LegalLocations,
                loc => -DistanceToCore(g, loc) + CoreGateStickiness(loc));
        }
        return f.LegalLocations[_rng.Next(f.LegalLocations.Count)];
    }

    // Tech slot choice: prefer to overwrite the BasicUnique (Right) slot
    // since Basic Common is the universally-useful flexible default —
    // matches the rulebook's general advice (p.20-ish).
    private TechSlot ChooseTechSlot(GameState g, SelectTechSlotRequest ts)
    {
        var p = g.Player(Seat);
        // If exactly one slot still holds a Basic-* tech, sacrifice it
        // before overwriting a Researched card we already invested in.
        bool leftIsBasic = p.Techs.Left is Tech.BasicCommon or Tech.BasicUnique;
        bool rightIsBasic = p.Techs.Right is Tech.BasicCommon or Tech.BasicUnique;
        if (leftIsBasic && !rightIsBasic) return TechSlot.Left;
        if (rightIsBasic && !leftIsBasic) return TechSlot.Right;
        // Both basic: prefer Right (BasicUnique). Both researched: arbitrary.
        if (leftIsBasic && rightIsBasic) return TechSlot.Right;
        return _rng.Next(2) == 0 ? TechSlot.Left : TechSlot.Right;
    }

    private IReadOnlyList<ShipLocation> ChoosePath(GameState g, DeclareMoveRequest m)
    {
        // Universal battle-safety adjustment: every policy avoids paths
        // that force us into an unwinnable fight. The origin determines
        // our cruiser count; the path's last location is checked for
        // enemy cruisers (where a battle would actually happen).
        int Safety(IReadOnlyList<ShipLocation> p) => BattleSafetyScore(g, m.Origin, p[^1]);

        if (Policy == AiPolicy.CoreRush)
        {
            return BestRandom(m.LegalPaths,
                path => -DistanceToCore(g, path[^1]) + Safety(path));
        }
        if (Policy == AiPolicy.Warrior)
        {
            // Warrior wants engagement, but only winnable engagement.
            return BestRandom(m.LegalPaths,
                path => EnemyProximityScore(g, path[^1]) + Safety(path));
        }
        if (Policy == AiPolicy.Munchkin)
        {
            if (LeaderId(g) is { } leader)
                return BestRandom(m.LegalPaths,
                    path => LeaderProximityScore(g, leader, path[^1]) + Safety(path));
            return BestRandom(m.LegalPaths,
                path => -DistanceToCore(g, path[^1]) + Safety(path));
        }
        if (Policy == AiPolicy.Greedy)
        {
            return BestRandom(m.LegalPaths,
                path => -DistanceToCore(g, path[^1]) + Safety(path));
        }
        // Refine, defaults: avoid losing battles; otherwise pick at random.
        return BestRandom(m.LegalPaths, path => Safety(path));
    }

    // Score by proximity to the leader's ships — higher score = closer to
    // a potential battle/sabotage target.
    private static int LeaderProximityScore(GameState g, PlayerId leader, ShipLocation loc)
    {
        var leaderShips = g.ShipPlacements.Where(sp => sp.Owner == leader).ToList();
        if (leaderShips.Count == 0) return 0;
        int score = 0;
        foreach (var es in leaderShips)
        {
            if (Mechanics.LocationsEqual(es.Location, loc)) { score = Math.Max(score, 2); continue; }
            if (Adjacent(g, loc, es.Location)) score = Math.Max(score, 1);
        }
        return score;
    }

    // Battle-likelihood score for a path. Positive when the path ends in
    // a winnable fight (our cruisers > theirs), strongly negative when the
    // path forces us into a fight we'll likely lose (defender wins ties,
    // and losing a battle costs us all our cruisers + scores prestige for
    // the winner). Returns 0 when no contact is anticipated.
    //
    // Magnitudes scale with player count: at 6p a battle loss is more
    // damaging because more opponents bank points while you rebuild
    // material. At 2p a winnable battle pays a larger share of the
    // win condition.
    private int BattleSafetyScore(GameState g, ShipLocation origin, ShipLocation finalLoc)
    {
        if (origin is not ShipLocation.OnGate fromGate) return 0;
        if (finalLoc is not ShipLocation.OnGate toGate) return 0;

        int myCruisers = g.ShipPlacements.Count(sp =>
            sp.Owner == Seat &&
            sp.Location is ShipLocation.OnGate og && og.Gate == fromGate.Gate);
        int enemyAtDest = g.ShipPlacements.Count(sp =>
            sp.Owner != Seat &&
            sp.Location is ShipLocation.OnGate og && og.Gate == toGate.Gate);
        if (enemyAtDest == 0) return 0;

        int n = g.Players.Count;
        int winBonus = n <= 2 ? 8 : n <= 4 ? 6 : 4;
        int tiePenalty = n <= 2 ? -8 : n <= 4 ? -10 : -14;
        int routPenalty = n <= 2 ? -12 : n <= 4 ? -14 : -18;

        if (myCruisers > enemyAtDest) return winBonus;
        if (myCruisers == enemyAtDest) return tiePenalty;
        return routPenalty;
    }

    private int? ChooseHandCard(GameState g, SelectHandCardRequest h)
    {
        if (h.LegalCardIds.Count == 0)
            return h.AllowNone ? null : 0;

        // Exploration: when placing a card face-up on a newly-explored
        // sector, the right card depends on who can reach it most easily.
        // If MY home is closer to the sector than any opponent's home,
        // I'll activate it the most → place my BEST card. If an opponent
        // is closer, they'll place a good card there if I don't, so place
        // my WORST card to deny them the slot.
        bool isExploration = h.Prompt.StartsWith("Exploring", StringComparison.Ordinal);
        if (isExploration)
        {
            bool placeBest = ExploringSectorMineToReach(g, h.Prompt) ?? false;
            var ordered = placeBest
                ? h.LegalCardIds
                    .OrderByDescending(id => ScoreCard(g, g.CardsById[id]))
                    .ThenBy(_ => _rng.Next())
                : h.LegalCardIds
                    .OrderBy(id => ScoreCard(g, g.CardsById[id]))
                    .ThenBy(_ => _rng.Next());
            return ordered.First();
        }

        // Refine doesn't want to discard mineral-friendly cards (size 1's
        // would be mined, larger trades give points). Prefer largest.
        if (Policy == AiPolicy.Refine)
        {
            var ordered = h.LegalCardIds.OrderByDescending(id => g.CardsById[id].Size).ToList();
            return ordered[0];
        }
        return h.LegalCardIds[_rng.Next(h.LegalCardIds.Count)];
    }

    // Parse the explored node id from the prompt ("Exploring N5: …") and
    // compare hex distance from that node to my home vs the closest
    // opponent's home. Returns true if the sector is at least as close
    // to me as to any opponent (place best card), false if an opponent
    // is closer (place worst), null if parsing fails.
    private bool? ExploringSectorMineToReach(GameState g, string prompt)
    {
        const string prefix = "Exploring N";
        if (!prompt.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int colon = prompt.IndexOf(':');
        if (colon <= prefix.Length) return null;
        if (!int.TryParse(prompt.AsSpan(prefix.Length, colon - prefix.Length), out int nodeVal))
            return null;

        var explored = g.Map.Node(new NodeId(nodeVal));
        if (!g.Map.HomeNodeIds.TryGetValue(Seat, out var myHomeId)) return null;
        var myHome = g.Map.Node(myHomeId);
        int myDist = AxialDistance(explored.AxialQ, explored.AxialR, myHome.AxialQ, myHome.AxialR);
        int closestOppDist = int.MaxValue;
        foreach (var opp in g.Players)
        {
            if (opp.Id == Seat) continue;
            if (!g.Map.HomeNodeIds.TryGetValue(opp.Id, out var oppHomeId)) continue;
            var oppHome = g.Map.Node(oppHomeId);
            int d = AxialDistance(explored.AxialQ, explored.AxialR, oppHome.AxialQ, oppHome.AxialR);
            if (d < closestOppDist) closestOppDist = d;
        }
        return myDist <= closestOppDist;
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
        // Sabotage scores prestige per ship destroyed (capped at fleet
        // size — no overkill). Prefer the LARGEST enemy fleet at any
        // legal target so each successful bomb pays.
        int FleetSize(SabotageTarget t) => g.ShipPlacements.Count(sp =>
            sp.Owner == t.Owner && Mechanics.LocationsEqual(sp.Location, t.Location));

        // Warrior + Munchkin: tiebreak on highest-prestige owner.
        if (Policy == AiPolicy.Warrior || Policy == AiPolicy.Munchkin)
        {
            return sab.LegalTargets
                .OrderByDescending(t => FleetSize(t))
                .ThenByDescending(t => g.Player(t.Owner).Prestige)
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
