using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Slice B movement legality: transports walk node→adjacent node; cruisers
// walk gate→gate sharing a node. No exploration (all nodes treated as
// explored), no battle (any contact with enemy ships filters the path out).
public static class Movement
{
    public static IReadOnlyList<ShipLocation> Neighbors(SectorMap map, ShipLocation loc)
    {
        switch (loc)
        {
            case ShipLocation.OnNode n:
                return map.AdjacencyByNode[n.Node]
                    .Select(g => (ShipLocation)new ShipLocation.OnNode(
                        g.EndpointA == n.Node ? g.EndpointB : g.EndpointA))
                    .ToList();
            case ShipLocation.OnGate gateLoc:
                var gate = map.Gate(gateLoc.Gate);
                var endpoints = new[] { gate.EndpointA, gate.EndpointB };
                return endpoints
                    .SelectMany(nid => map.AdjacencyByNode[nid])
                    .Where(g => g.Id != gateLoc.Gate)
                    .Select(g => (ShipLocation)new ShipLocation.OnGate(g.Id))
                    .Distinct()
                    .ToList();
            default:
                return Array.Empty<ShipLocation>();
        }
    }

    // Enumerate paths of length 1..maxMoves. Restriction model (rulebook):
    //   - Transport: cannot move ONTO a card patrolled by an enemy
    //     (enemy cruiser on a gate touching the destination node). Transports
    //     CAN coexist with enemy transports on a card.
    //   - Cruiser: pathing returns ALL gate-to-gate moves; battle and
    //     patrol-through enforcement happen at execution time inside
    //     CommandHandler / tech handlers (search "IsPatrolledByEnemy" in
    //     this directory). Path enumeration intentionally does not pre-filter
    //     because the player needs to see all reachable gates to choose.
    public static IReadOnlyList<IReadOnlyList<ShipLocation>> EnumeratePaths(
        GameState g, PlayerId mover, ShipLocation origin, int maxMoves)
    {
        var results = new List<IReadOnlyList<ShipLocation>>();
        var current = new List<ShipLocation>();
        Walk(g, mover, origin, maxMoves, current, results);
        return results;
    }

    private static void Walk(
        GameState g, PlayerId mover, ShipLocation here, int remaining,
        List<ShipLocation> current, List<IReadOnlyList<ShipLocation>> results)
    {
        if (remaining == 0) return;
        foreach (var step in Neighbors(g.Map, here))
        {
            if (IsBlockedFor(g, mover, step)) continue;

            // Cruiser-through-patrol filter (rulebook p.29): a cruiser move
            // gate→gate passing through an enemy-patrolled card is only
            // legal if it "starts a battle" — i.e. the destination gate
            // itself has an enemy cruiser to attack. Without this filter,
            // path enumeration offers empty gates as destinations even
            // though execution-time patrol-through interception will
            // redirect the fleet to whichever gate the patroller is on,
            // which is confusing UX (player clicks gate X, fleet ends up
            // at gate Y because Y was where the actual enemy lived).
            bool battleEndsTheStep = false;
            if (here is ShipLocation.OnGate fromGate && step is ShipLocation.OnGate toGate)
            {
                var passage = SharedNode(g.Map, fromGate.Gate, toGate.Gate);
                if (passage is { } passNode &&
                    IsPatrolledByEnemy(g, mover, passNode) &&
                    !HasEnemyCruiserOnGate(g, mover, toGate.Gate))
                {
                    // Passage is patrolled, but no enemy on the destination
                    // gate — illegal traversal under the patrolbook rule.
                    continue;
                }
                // If we're stepping ONTO an enemy-cruiser gate, this step
                // resolves a battle and terminates the path.
                if (HasEnemyCruiserOnGate(g, mover, toGate.Gate))
                    battleEndsTheStep = true;
            }

            current.Add(step);
            results.Add(current.ToList());
            // Don't continue exploring past a battle-resolving step: the
            // battle interrupts movement, so any further steps would be
            // unreachable in practice.
            if (!battleEndsTheStep)
                Walk(g, mover, step, remaining - 1, current, results);
            current.RemoveAt(current.Count - 1);
        }
    }

    public static bool IsBlockedFor(GameState g, PlayerId mover, ShipLocation dest) => dest switch
    {
        ShipLocation.OnNode n => IsPatrolledByEnemy(g, mover, n.Node),
        ShipLocation.OnGate => false, // cruiser gates are always enumerable; battle/patrol enforcement happens at execution time in CommandHandler
        _ => false,
    };

    // Returns the node both gates share, or null if disjoint.
    public static NodeId? SharedNode(SectorMap map, GateId fromGate, GateId toGate)
    {
        if (fromGate == toGate) return null;
        var a = map.Gate(fromGate);
        var b = map.Gate(toGate);
        if (a.EndpointA == b.EndpointA || a.EndpointA == b.EndpointB) return a.EndpointA;
        if (a.EndpointB == b.EndpointA || a.EndpointB == b.EndpointB) return a.EndpointB;
        return null;
    }

    public static bool HasEnemyCruiserOnGate(GameState g, PlayerId mover, GateId gate) =>
        g.ShipPlacements.Any(sp =>
            sp.Owner != mover &&
            sp.Location is ShipLocation.OnGate og && og.Gate == gate);

    public static bool IsPatrolledByEnemy(GameState g, PlayerId mover, NodeId node)
    {
        foreach (var gate in g.Map.AdjacencyByNode[node])
            if (g.ShipPlacements.Any(sp =>
                    sp.Owner != mover &&
                    sp.Location is ShipLocation.OnGate og &&
                    og.Gate == gate.Id))
                return true;
        return false;
    }

    public static bool PlayerOccupiesNode(GameState g, PlayerId player, NodeId node) =>
        g.ShipPlacements.Any(sp =>
            sp.Owner == player &&
            sp.Location is ShipLocation.OnNode n && n.Node == node);

    public static bool PlayerPatrolsNode(GameState g, PlayerId player, NodeId node)
    {
        foreach (var gate in g.Map.AdjacencyByNode[node])
            if (g.ShipPlacements.Any(sp =>
                    sp.Owner == player &&
                    sp.Location is ShipLocation.OnGate og &&
                    og.Gate == gate.Id))
                return true;
        return false;
    }

    public static bool PlayerControlsNode(GameState g, PlayerId player, NodeId node) =>
        PlayerOccupiesNode(g, player, node) || PlayerPatrolsNode(g, player, node);
}
