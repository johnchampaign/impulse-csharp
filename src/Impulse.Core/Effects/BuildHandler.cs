using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum BuildShipFilter { TransportOnly, CruiserOnly, Either }
public enum BuildLocationKind { Home, Occupied }

// Slice C1: pick one location, place `count` ships there. Ship type is
// derived from the chosen location (node→transport, gate→cruiser);
// `_filter` constrains which kinds of locations are legal.
public sealed class BuildHandler : IEffectHandler
{
    private readonly int _count;
    private readonly BuildShipFilter _filter;
    private readonly BuildLocationKind _locKind;

    public BuildHandler(int count, BuildShipFilter filter, BuildLocationKind locKind)
    {
        _count = count;
        _filter = filter;
        _locKind = locKind;
    }

    private enum Stage { Start, AwaitingPlacement, Done }

    public bool Execute(GameState g, EffectContext ctx)
    {
        var stage = (Stage)(ctx.HandlerState ?? Stage.Start);
        var p = g.Player(ctx.ActivatingPlayer);

        if (p.ShipsAvailable <= 0)
        {
            g.Log.Write($"  → {ctx.ActivatingPlayer} ship pool empty; build noop");
            ctx.IsComplete = true;
            return false;
        }

        // Boost the count by mineral gems matching the card's color.
        int boost = Boost.FromSource(g, ctx);
        int effectiveCount = _count + boost;

        if (stage == Stage.Start)
        {
            var legal = LegalPlacements(g, ctx.ActivatingPlayer);
            if (legal.Count == 0)
            {
                g.Log.Write($"  → {ctx.ActivatingPlayer} no legal Build location; noop");
                ctx.IsComplete = true;
                return false;
            }
            ctx.PendingChoice = new SelectShipPlacementRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalLocations = legal,
                Prompt = $"Pick a location to Build {Describe(effectiveCount)}.",
            };
            ctx.HandlerState = Stage.AwaitingPlacement;
            ctx.Paused = true;
            return false;
        }

        if (stage == Stage.AwaitingPlacement)
        {
            var req = (SelectShipPlacementRequest)ctx.PendingChoice!;
            var loc = req.Chosen ?? throw new InvalidOperationException("placement not chosen");
            ctx.PendingChoice = null;

            int built = 0;
            for (int i = 0; i < effectiveCount && p.ShipsAvailable > 0; i++)
                built += Mechanics.BuildShip(g, ctx.ActivatingPlayer, loc, g.Log);
            if (built < effectiveCount)
                g.Log.Write($"  → {ctx.ActivatingPlayer} pool exhausted ({built}/{effectiveCount} built)");
            ctx.IsComplete = true;
            return true;
        }

        return false;
    }

    private string Describe(int count) => _filter switch
    {
        BuildShipFilter.TransportOnly => $"{count} transport(s)",
        BuildShipFilter.CruiserOnly => $"{count} cruiser(s)",
        _ => $"{count} ship(s)",
    };

    private IReadOnlyList<ShipLocation> LegalPlacements(GameState g, PlayerId pid)
    {
        // Rulebook p.36: "you cannot build a Cruiser on a gate containing
        // another player's Cruisers."
        bool GateBlocked(GateId gid) => Movement.HasEnemyCruiserOnGate(g, pid, gid);

        var result = new List<ShipLocation>();
        if (_locKind == BuildLocationKind.Home)
        {
            var home = g.Map.HomeNodeIds[pid];
            if (_filter != BuildShipFilter.CruiserOnly)
                result.Add(new ShipLocation.OnNode(home));
            if (_filter != BuildShipFilter.TransportOnly)
                foreach (var gate in g.Map.AdjacencyByNode[home])
                    if (!GateBlocked(gate.Id))
                        result.Add(new ShipLocation.OnGate(gate.Id));
        }
        else // Occupied
        {
            // "Occupied" = a node where the player has a transport. Transports
            // build there directly; cruisers build on gates of that node.
            // (Slice C1 interpretation: cruisers do NOT, on their own, count
            // as "occupying" — patrolling != occupying.)
            var transportNodes = g.ShipPlacements
                .Where(s => s.Owner == pid && s.Location is ShipLocation.OnNode)
                .Select(s => ((ShipLocation.OnNode)s.Location).Node)
                .ToHashSet();

            if (_filter != BuildShipFilter.CruiserOnly)
                foreach (var nid in transportNodes)
                    result.Add(new ShipLocation.OnNode(nid));

            if (_filter != BuildShipFilter.TransportOnly)
            {
                var seenGates = new HashSet<int>();
                foreach (var nid in transportNodes)
                    foreach (var gate in g.Map.AdjacencyByNode[nid])
                        if (seenGates.Add(gate.Id.Value) && !GateBlocked(gate.Id))
                            result.Add(new ShipLocation.OnGate(gate.Id));
            }
        }
        return result;
    }
}

public static class BuildRegistrations
{
    private static readonly (string Family, int Count, BuildShipFilter Filter, BuildLocationKind Loc)[] Table =
    {
        ("build_n_transport_at_home",     1, BuildShipFilter.TransportOnly, BuildLocationKind.Home),
        ("build_n_cruiser_at_home",       1, BuildShipFilter.CruiserOnly,   BuildLocationKind.Home),
        ("build_n_transport_at_occupied", 1, BuildShipFilter.TransportOnly, BuildLocationKind.Occupied),
        ("build_n_cruiser_at_occupied",   1, BuildShipFilter.CruiserOnly,   BuildLocationKind.Occupied),
        ("build_n_ships_at_occupied",     2, BuildShipFilter.Either,        BuildLocationKind.Occupied),
    };

    public static void RegisterAll(EffectRegistry r)
    {
        foreach (var (family, count, filter, loc) in Table)
            r.Register(family, new BuildHandler(count, filter, loc));
        r.Register("build_n_ship_home_and_each_occupied", new BuildHomeAndEachOccupiedHandler(BuildHomeAndEachOccupiedHandler.ByCardId));
    }
}

// "Build [1] ship at home and at each other [color] you occupy." Per-card
// `ColorFilter` is the [color] in the text (a face-up node card's color);
// boost target is the [1] count, applied uniformly to home + each match.
public sealed record BuildHomeAndEachOccupiedParams(CardColor ColorFilter);

public sealed class BuildHomeAndEachOccupiedHandler : IEffectHandler
{
    public static readonly Dictionary<int, BuildHomeAndEachOccupiedParams> ByCardId = new()
    {
        [32] = new(CardColor.Yellow),
        [52] = new(CardColor.Green),
        [57] = new(CardColor.Blue),
        [62] = new(CardColor.Red),
    };

    private readonly IReadOnlyDictionary<int, BuildHomeAndEachOccupiedParams> _byCardId;

    public BuildHomeAndEachOccupiedHandler(IReadOnlyDictionary<int, BuildHomeAndEachOccupiedParams> byCardId)
    {
        _byCardId = byCardId;
    }

    private enum Stage { Start, AwaitingHomePlacement, AwaitingMatchPlacement, Done }
    private sealed class State
    {
        public Stage Stage;
        public int Count;
        public Queue<NodeId> Pending = new();
        public NodeId? CurrentMatch;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → build-home-and-each: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;
        var p = g.Player(ctx.ActivatingPlayer);

        if (st.Stage == Stage.Start)
        {
            int boost = Boost.FromSource(g, ctx);
            st.Count = 1 + boost;
            if (p.ShipsAvailable <= 0)
            {
                g.Log.Write($"  → {ctx.ActivatingPlayer} ship pool empty; build noop");
                ctx.IsComplete = true;
                return false;
            }
            var home = g.Map.HomeNodeIds[ctx.ActivatingPlayer];
            var legal = new List<ShipLocation> { new ShipLocation.OnNode(home) };
            foreach (var gate in g.Map.AdjacencyByNode[home])
                if (!Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, gate.Id))
                    legal.Add(new ShipLocation.OnGate(gate.Id));
            ctx.PendingChoice = new SelectShipPlacementRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalLocations = legal,
                Prompt = $"Build {st.Count} ship(s) at home (transport on node, or cruiser on a home gate). You'll then be prompted again for each non-home {prms.ColorFilter} sector you occupy.",
            };
            st.Stage = Stage.AwaitingHomePlacement;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingHomePlacement)
        {
            var req = (SelectShipPlacementRequest)ctx.PendingChoice!;
            var loc = req.Chosen ?? throw new InvalidOperationException("placement not chosen");
            ctx.PendingChoice = null;

            // Home placement
            for (int i = 0; i < st.Count && p.ShipsAvailable > 0; i++)
                Mechanics.BuildShip(g, ctx.ActivatingPlayer, loc, g.Log);

            // Find each occupied non-home node whose face-up card matches
            // the color filter. "Occupy" per rulebook p.27 = having a
            // transport on the node (cruisers patrol, they don't occupy).
            var home = g.Map.HomeNodeIds[ctx.ActivatingPlayer];
            var matches = g.ShipPlacements
                .Where(sp => sp.Owner == ctx.ActivatingPlayer &&
                             sp.Location is ShipLocation.OnNode n && n.Node != home)
                .Select(sp => ((ShipLocation.OnNode)sp.Location).Node)
                .Distinct()
                .Where(nid =>
                {
                    if (!g.NodeCards.TryGetValue(nid, out var s)) return false;
                    if (s is not NodeCardState.FaceUp fu) return false;
                    return g.CardsById[fu.CardId].Color == prms.ColorFilter;
                })
                .ToList();
            if (matches.Count == 0)
            {
                g.Log.Write($"  → no other {prms.ColorFilter} sector with your transport — only home gets ships");
                ctx.IsComplete = true;
                return true;
            }
            st.Pending = new Queue<NodeId>(matches);
            return PromptNextMatch(g, ctx, st, prms);
        }

        if (st.Stage == Stage.AwaitingMatchPlacement)
        {
            var req = (SelectShipPlacementRequest)ctx.PendingChoice!;
            var loc = req.Chosen ?? throw new InvalidOperationException("placement not chosen");
            ctx.PendingChoice = null;
            for (int i = 0; i < st.Count && p.ShipsAvailable > 0; i++)
                Mechanics.BuildShip(g, ctx.ActivatingPlayer, loc, g.Log);
            return PromptNextMatch(g, ctx, st, prms);
        }
        return false;
    }

    // Prompt for placement at the next matching occupied sector. Player
    // can build a transport at the node OR a cruiser at any of its gates
    // (excluding gates already containing an enemy cruiser, per p.36).
    private static bool PromptNextMatch(GameState g, EffectContext ctx, State st, BuildHomeAndEachOccupiedParams prms)
    {
        var p = g.Player(ctx.ActivatingPlayer);
        if (st.Pending.Count == 0 || p.ShipsAvailable <= 0)
        {
            ctx.IsComplete = true;
            return true;
        }
        var nid = st.Pending.Dequeue();
        st.CurrentMatch = nid;
        var legal = new List<ShipLocation> { new ShipLocation.OnNode(nid) };
        foreach (var gate in g.Map.AdjacencyByNode[nid])
            if (!Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, gate.Id))
                legal.Add(new ShipLocation.OnGate(gate.Id));
        ctx.PendingChoice = new SelectShipPlacementRequest
        {
            Player = ctx.ActivatingPlayer,
            LegalLocations = legal,
            Prompt = $"Build {st.Count} ship(s) at the {prms.ColorFilter} sector {nid} you occupy " +
                     $"(transport on the node, or cruiser on an adjacent gate). " +
                     $"{st.Pending.Count} more sector(s) after this.",
        };
        st.Stage = Stage.AwaitingMatchPlacement;
        ctx.Paused = true;
        return false;
    }

    private static int SourceCardId(EffectSource src) => src switch
    {
        EffectSource.ImpulseCard ic => ic.CardId,
        EffectSource.PlanCard pc => pc.CardId,
        EffectSource.TechEffect te => te.CardId ?? 0,
        EffectSource.MapActivation ma => ma.CardId,
        _ => 0,
    };
}
