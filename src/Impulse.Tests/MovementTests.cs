using Impulse.Core;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class MovementTests
{
    private static GameState FreshGame(int seed = 1) =>
        SetupFactory.NewGame(
            new SetupOptions(2, Seed: seed,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true),
            BuildRegistry());

    private static EffectRegistry BuildRegistry()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        return r;
    }

    [Fact]
    public void Transport_neighbors_are_adjacent_nodes()
    {
        var g = FreshGame();
        var home = g.Map.HomeNodeIds[new PlayerId(1)];
        var origin = (ShipLocation)new ShipLocation.OnNode(home);
        var ns = Movement.Neighbors(g.Map, origin);
        Assert.NotEmpty(ns);
        Assert.All(ns, l => Assert.IsType<ShipLocation.OnNode>(l));
    }

    [Fact]
    public void Cruiser_neighbors_are_other_gates_sharing_an_endpoint()
    {
        var g = FreshGame();
        var home = g.Map.HomeNodeIds[new PlayerId(1)];
        var anyGate = g.Map.AdjacencyByNode[home].First();
        var origin = (ShipLocation)new ShipLocation.OnGate(anyGate.Id);
        var ns = Movement.Neighbors(g.Map, origin);
        Assert.NotEmpty(ns);
        Assert.All(ns, l => Assert.IsType<ShipLocation.OnGate>(l));
        Assert.DoesNotContain(ns, l => ((ShipLocation.OnGate)l).Gate == anyGate.Id);
    }

    [Fact]
    public void Transport_can_move_through_enemy_transports()
    {
        // Rulebook: transports coexist with enemy transports on the same card;
        // only enemy patrol blocks movement.
        var g = FreshGame();
        var p1Home = g.Map.HomeNodeIds[new PlayerId(1)];
        var firstNeighbor = ((ShipLocation.OnNode)Movement.Neighbors(g.Map, new ShipLocation.OnNode(p1Home))[0]).Node;
        g.ShipPlacements.Add(new ShipPlacement(new PlayerId(2), new ShipLocation.OnNode(firstNeighbor)));

        var paths = Movement.EnumeratePaths(
            g, new PlayerId(1), new ShipLocation.OnNode(p1Home), maxMoves: 1);
        Assert.Contains(paths, path =>
            path.Count == 1 &&
            path[0] is ShipLocation.OnNode n && n.Node == firstNeighbor);
    }

    [Fact]
    public void Transport_blocked_by_enemy_patrol()
    {
        // Enemy cruiser on a gate touching the destination node = patrol.
        // Transport cannot move onto that node.
        var g = FreshGame();
        var p1Home = g.Map.HomeNodeIds[new PlayerId(1)];
        var firstNeighbor = ((ShipLocation.OnNode)Movement.Neighbors(g.Map, new ShipLocation.OnNode(p1Home))[0]).Node;
        var gateOfNeighbor = g.Map.AdjacencyByNode[firstNeighbor].First().Id;
        g.ShipPlacements.Add(new ShipPlacement(new PlayerId(2), new ShipLocation.OnGate(gateOfNeighbor)));

        var paths = Movement.EnumeratePaths(
            g, new PlayerId(1), new ShipLocation.OnNode(p1Home), maxMoves: 1);
        Assert.DoesNotContain(paths, path =>
            path.Count == 1 &&
            path[0] is ShipLocation.OnNode n && n.Node == firstNeighbor);
    }

    [Fact]
    public void Cruiser_movement_unrestricted_in_slice_c2()
    {
        // Slice C2: cruiser-vs-cruiser through patrolled territory is a battle
        // concern (C6). For now cruisers move freely.
        var g = FreshGame();
        var home = g.Map.HomeNodeIds[new PlayerId(1)];
        var anyGate = g.Map.AdjacencyByNode[home].First().Id;
        var anyOtherGate = g.Map.AdjacencyByNode[home].Skip(1).First().Id;
        // Enemy cruiser on a different gate.
        g.ShipPlacements.Add(new ShipPlacement(new PlayerId(2), new ShipLocation.OnGate(anyOtherGate)));

        var paths = Movement.EnumeratePaths(
            g, new PlayerId(1), new ShipLocation.OnGate(anyGate), maxMoves: 2);
        Assert.NotEmpty(paths);
    }

    [Fact]
    public void Path_enumeration_includes_partial_paths()
    {
        var g = FreshGame();
        var home = g.Map.HomeNodeIds[new PlayerId(1)];
        var paths = Movement.EnumeratePaths(g, new PlayerId(1),
            new ShipLocation.OnNode(home), maxMoves: 2);
        Assert.Contains(paths, p => p.Count == 1);
        Assert.Contains(paths, p => p.Count == 2);
    }
}
