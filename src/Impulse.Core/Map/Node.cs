using Impulse.Core.Players;

namespace Impulse.Core.Map;

public sealed record Node(
    NodeId Id,
    int AxialQ,        // hex axial coordinate, for layout only
    int AxialR,
    bool IsHome,
    PlayerId? Owner,
    bool IsSectorCore);
