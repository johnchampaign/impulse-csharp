namespace Impulse.Core.Map;

// Pure connector. No per-gate icons or hidden fields (locked).
public sealed record Gate(GateId Id, NodeId EndpointA, NodeId EndpointB);
