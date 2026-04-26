using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class MapFactoryTests
{
    private static readonly IReadOnlyList<PlayerId> FourSeats =
        new[] { new PlayerId(1), new PlayerId(2), new PlayerId(3), new PlayerId(4) };

    [Fact]
    public void Builds_19_nodes_for_3_4_5_4_3_hex()
    {
        var map = MapFactory.Build(FourSeats);
        Assert.Equal(19, map.Nodes.Count);
    }

    [Fact]
    public void Has_exactly_one_sector_core()
    {
        var map = MapFactory.Build(FourSeats);
        var cores = map.Nodes.Where(n => n.IsSectorCore).ToList();
        Assert.Single(cores);
        Assert.Equal(map.SectorCoreNodeId, cores[0].Id);
    }

    [Fact]
    public void Assigns_one_home_per_seat()
    {
        var map = MapFactory.Build(FourSeats);
        Assert.Equal(FourSeats.Count, map.HomeNodeIds.Count);
        var homes = map.Nodes.Where(n => n.IsHome).ToList();
        Assert.Equal(FourSeats.Count, homes.Count);
        Assert.All(homes, h => Assert.NotNull(h.Owner));
    }

    [Fact]
    public void Gates_are_undirected_and_unique()
    {
        var map = MapFactory.Build(FourSeats);
        var pairs = map.Gates
            .Select(g => g.EndpointA.Value < g.EndpointB.Value
                ? (g.EndpointA, g.EndpointB)
                : (g.EndpointB, g.EndpointA))
            .ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void Adjacency_lookup_includes_each_gate_twice()
    {
        var map = MapFactory.Build(FourSeats);
        var totalEntries = map.Nodes.Sum(n => map.AdjacencyByNode[n.Id].Count());
        Assert.Equal(map.Gates.Count * 2, totalEntries);
    }

    [Fact]
    public void Sector_core_is_connected_to_six_neighbors()
    {
        var map = MapFactory.Build(FourSeats);
        var coreGates = map.AdjacencyByNode[map.SectorCoreNodeId].ToList();
        Assert.Equal(6, coreGates.Count);
    }

    [Fact]
    public void Map_is_connected()
    {
        var map = MapFactory.Build(FourSeats);
        var visited = new HashSet<NodeId> { map.Nodes[0].Id };
        var frontier = new Queue<NodeId>();
        frontier.Enqueue(map.Nodes[0].Id);
        while (frontier.Count > 0)
        {
            var n = frontier.Dequeue();
            foreach (var g in map.AdjacencyByNode[n])
            {
                var other = g.EndpointA == n ? g.EndpointB : g.EndpointA;
                if (visited.Add(other)) frontier.Enqueue(other);
            }
        }
        Assert.Equal(map.Nodes.Count, visited.Count);
    }

    [Fact]
    public void Supports_two_through_six_seats()
    {
        for (int n = 2; n <= 6; n++)
        {
            var seats = Enumerable.Range(1, n).Select(i => new PlayerId(i)).ToArray();
            var map = MapFactory.Build(seats);
            Assert.Equal(n, map.HomeNodeIds.Count);
        }
    }
}
