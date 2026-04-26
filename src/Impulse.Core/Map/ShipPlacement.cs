using Impulse.Core.Players;

namespace Impulse.Core.Map;

public abstract record ShipLocation
{
    public sealed record OnNode(NodeId Node) : ShipLocation;
    public sealed record OnGate(GateId Gate) : ShipLocation;
}

public sealed record ShipPlacement(PlayerId Owner, ShipLocation Location);
