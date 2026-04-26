using Impulse.Core.Players;

namespace Impulse.Core.Map;

public sealed class SectorMap
{
    public required IReadOnlyList<Node> Nodes { get; init; }
    public required IReadOnlyList<Gate> Gates { get; init; }
    public required NodeId SectorCoreNodeId { get; init; }
    public required IReadOnlyDictionary<PlayerId, NodeId> HomeNodeIds { get; init; }

    private ILookup<NodeId, Gate>? _adj;
    public ILookup<NodeId, Gate> AdjacencyByNode =>
        _adj ??= Gates
            .SelectMany(g => new[] { (g.EndpointA, g), (g.EndpointB, g) })
            .ToLookup(t => t.Item1, t => t.Item2);

    public Node Node(NodeId id) => Nodes.First(n => n.Id == id);
    public Gate Gate(GateId id) => Gates.First(g => g.Id == id);
}
