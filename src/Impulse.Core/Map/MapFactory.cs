using Impulse.Core.Players;

namespace Impulse.Core.Map;

// Slice A: 3-4-5-4-3 hex (19 nodes), center = Sector Core, gates between
// every pair of axial-adjacent nodes. Per-player-count home assignments are
// flavor and TBD against rulebook p.5; the graph is fixed.
public static class MapFactory
{
    private static readonly (int q, int r)[] HexAxial =
    {
        // Row 1 (3 hexes)
        (0, -2), (1, -2), (2, -2),
        // Row 2 (4 hexes)
        (-1, -1), (0, -1), (1, -1), (2, -1),
        // Row 3 (5 hexes) — center row, middle is Sector Core
        (-2, 0), (-1, 0), (0, 0), (1, 0), (2, 0),
        // Row 4 (4 hexes)
        (-2, 1), (-1, 1), (0, 1), (1, 1),
        // Row 5 (3 hexes)
        (-2, 2), (-1, 2), (0, 2),
    };

    private static readonly (int dq, int dr)[] HexNeighbors =
    {
        (1, 0), (-1, 0),
        (0, 1), (0, -1),
        (1, -1), (-1, 1),
    };

    public static SectorMap Build(IReadOnlyList<PlayerId> seats)
    {
        var nodes = new List<Node>(HexAxial.Length);
        var sectorCoreId = default(NodeId);
        var homeAssignments = new Dictionary<PlayerId, NodeId>();

        // Outer-ring corner positions of the 3-4-5-4-3 hex.
        // TL=(0,-2), TR=(2,-2), ML=(-2,0), MR=(2,0), BL=(-2,2), BR=(0,2).
        var TL = (q: 0, r: -2);
        var TR = (q: 2, r: -2);
        var ML = (q: -2, r: 0);
        var MR = (q: 2, r: 0);
        var BL = (q: -2, r: 2);
        var BR = (q: 0, r: 2);
        // Per-player-count home assignments per rulebook p.5 setup map.
        var perCount = new Dictionary<int, (int q, int r)[]>
        {
            [2] = new[] { ML, MR },
            [3] = new[] { ML, TR, BR },
            [4] = new[] { TL, TR, BL, BR },
            [5] = new[] { TL, TR, ML, BL, BR },
            [6] = new[] { TL, TR, ML, MR, BL, BR },
        };
        var cornerAxials = perCount.TryGetValue(seats.Count, out var arr) ? arr
            : new[] { TL, TR, ML, MR, BL, BR };

        for (int i = 0; i < HexAxial.Length; i++)
        {
            var (q, r) = HexAxial[i];
            var id = new NodeId(i + 1);
            bool isCore = q == 0 && r == 0;
            if (isCore) sectorCoreId = id;

            int cornerIdx = Array.IndexOf(cornerAxials, (q, r));
            PlayerId? owner = null;
            bool isHome = false;
            if (cornerIdx >= 0 && cornerIdx < seats.Count)
            {
                owner = seats[cornerIdx];
                isHome = true;
                homeAssignments[seats[cornerIdx]] = id;
            }

            nodes.Add(new Node(id, q, r, isHome, owner, isCore));
        }

        var gates = new List<Gate>();
        int gateId = 1;
        var seen = new HashSet<(NodeId, NodeId)>();
        foreach (var n in nodes)
        {
            foreach (var (dq, dr) in HexNeighbors)
            {
                int nq = n.AxialQ + dq;
                int nr = n.AxialR + dr;
                var neighbor = nodes.FirstOrDefault(x => x.AxialQ == nq && x.AxialR == nr);
                if (neighbor is null) continue;
                var key = n.Id.Value < neighbor.Id.Value
                    ? (n.Id, neighbor.Id) : (neighbor.Id, n.Id);
                if (!seen.Add(key)) continue;
                gates.Add(new Gate(new GateId(gateId++), key.Item1, key.Item2));
            }
        }

        return new SectorMap
        {
            Nodes = nodes,
            Gates = gates,
            SectorCoreNodeId = sectorCoreId,
            HomeNodeIds = homeAssignments,
        };
    }
}
