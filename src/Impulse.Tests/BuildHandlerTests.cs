using Impulse.Core;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class BuildHandlerTests
{
    private static (GameState g, EffectRegistry r) Bootstrap(int seed = 1)
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        BuildRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(2, seed,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true),
            r);
        return (g, r);
    }

    private static EffectContext NewCtx(PlayerId pid) => new()
    {
        ActivatingPlayer = pid,
        Source = new EffectSource.ImpulseCard(0),
    };

    private static void Resolve(GameState g, IEffectHandler h, EffectContext ctx, Action<ChoiceRequest> answer)
    {
        while (!ctx.IsComplete)
        {
            ctx.Paused = false;
            h.Execute(g, ctx);
            if (ctx.IsComplete) break;
            if (ctx.Paused && ctx.PendingChoice is not null) answer(ctx.PendingChoice);
            else break;
        }
    }

    [Fact]
    public void Transport_at_home_legal_only_home_node()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var h = new BuildHandler(1, BuildShipFilter.TransportOnly, BuildLocationKind.Home);
        var ctx = NewCtx(p1);
        Resolve(g, h, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            Assert.Single(req.LegalLocations);
            Assert.IsType<ShipLocation.OnNode>(req.LegalLocations[0]);
            req.Chosen = req.LegalLocations[0];
        });
        var home = g.Map.HomeNodeIds[p1];
        Assert.Equal(1, g.ShipPlacements.Count(sp => sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == home));
        Assert.Equal(11, g.Player(p1).ShipsAvailable);
    }

    [Fact]
    public void Cruiser_at_home_legal_only_home_gates()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var h = new BuildHandler(1, BuildShipFilter.CruiserOnly, BuildLocationKind.Home);
        var ctx = NewCtx(p1);
        Resolve(g, h, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            Assert.All(req.LegalLocations, l => Assert.IsType<ShipLocation.OnGate>(l));
            Assert.NotEmpty(req.LegalLocations);
            req.Chosen = req.LegalLocations[0];
        });
        Assert.Equal(1, g.ShipPlacements.Count(sp => sp.Owner == p1 && sp.Location is ShipLocation.OnGate));
    }

    [Fact]
    public void Build_at_occupied_requires_existing_ship()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        // No ships placed in setup. Occupied build should noop.
        var h = new BuildHandler(1, BuildShipFilter.TransportOnly, BuildLocationKind.Occupied);
        var ctx = NewCtx(p1);
        h.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.DoesNotContain(g.ShipPlacements, sp => sp.Owner == p1);
    }

    [Fact]
    public void Build_at_occupied_offers_only_owned_locations()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var home1 = g.Map.HomeNodeIds[p1];
        var home2 = g.Map.HomeNodeIds[p2];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home1)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(home2)));

        var h = new BuildHandler(1, BuildShipFilter.TransportOnly, BuildLocationKind.Occupied);
        var ctx = NewCtx(p1);
        Resolve(g, h, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            Assert.Single(req.LegalLocations);
            req.Chosen = req.LegalLocations[0];
        });
    }

    [Fact]
    public void Pool_exhaustion_caps_build_count()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        g.Player(p1).ShipsAvailable = 1; // only one ship left
        var h = new BuildHandler(2, BuildShipFilter.Either, BuildLocationKind.Home);
        var ctx = NewCtx(p1);
        Resolve(g, h, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            req.Chosen = req.LegalLocations[0];
        });
        Assert.Equal(0, g.Player(p1).ShipsAvailable);
        Assert.Equal(1, g.ShipPlacements.Count(sp => sp.Owner == p1));
    }

    [Fact]
    public void Pool_empty_at_start_is_immediate_noop()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        g.Player(p1).ShipsAvailable = 0;
        var h = new BuildHandler(1, BuildShipFilter.TransportOnly, BuildLocationKind.Home);
        var ctx = NewCtx(p1);
        h.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Null(ctx.PendingChoice);
    }

    [Fact]
    public void Two_ships_at_occupied_places_both_at_same_location()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var h = new BuildHandler(2, BuildShipFilter.Either, BuildLocationKind.Occupied);
        var ctx = NewCtx(p1);
        Resolve(g, h, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            req.Chosen = req.LegalLocations[0];
        });
        Assert.Equal(3, g.ShipPlacements.Count(sp => sp.Owner == p1));
    }

    [Fact]
    public void Build_n_ship_home_and_each_occupied_is_registered()
    {
        var r = new EffectRegistry();
        BuildRegistrations.RegisterAll(r);
        Assert.True(r.IsRegistered("build_n_ship_home_and_each_occupied"));
    }

    [Fact]
    public void Build_home_and_each_occupied_color_builds_at_matching_nodes()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        // Find two non-home nodes; force one to face-up Yellow card and the
        // other to a non-yellow color. Place a transport at each so they're
        // "occupied", plus one face-up Yellow node with no transport (should
        // be ignored: not occupied).
        var nonHomes = g.Map.Nodes
            .Where(n => !n.IsHome && !n.IsSectorCore)
            .Select(n => n.Id)
            .Take(3)
            .ToList();
        int yellowCardId = g.CardsById.Values.First(c => c.Color == Impulse.Core.Cards.CardColor.Yellow).Id;
        int otherCardId = g.CardsById.Values.First(c => c.Color != Impulse.Core.Cards.CardColor.Yellow).Id;
        g.NodeCards[nonHomes[0]] = new NodeCardState.FaceUp(yellowCardId);
        g.NodeCards[nonHomes[1]] = new NodeCardState.FaceUp(otherCardId);
        g.NodeCards[nonHomes[2]] = new NodeCardState.FaceUp(yellowCardId); // unoccupied yellow

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(nonHomes[0])));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(nonHomes[1])));
        g.Player(p1).ShipsAvailable = 12;

        var handler = new BuildHomeAndEachOccupiedHandler(BuildHomeAndEachOccupiedHandler.ByCardId);
        var ctx = new EffectContext
        {
            ActivatingPlayer = p1,
            Source = new EffectSource.ImpulseCard(32), // c32 → Yellow filter
        };
        // Per-match prompts: pick the home node for the home build, then
        // pick the match node (transport) for the yellow-occupied sector.
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            // Pick a node from the legal list — prefer the home if offered,
            // otherwise the first node (which is the match node).
            var pick = req.LegalLocations.OfType<ShipLocation.OnNode>()
                .FirstOrDefault(n => n.Node == home);
            req.Chosen = pick ?? req.LegalLocations.First(l => l is ShipLocation.OnNode);
        });

        // Should have built 1 at home + 1 at the yellow-occupied node
        // (skipping the non-yellow occupied and the unoccupied yellow).
        int atHome = g.ShipPlacements.Count(sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == home);
        int atYellow = g.ShipPlacements.Count(sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == nonHomes[0]);
        int atOther = g.ShipPlacements.Count(sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == nonHomes[1]);
        Assert.Equal(1, atHome);
        Assert.Equal(2, atYellow); // original transport + 1 built
        Assert.Equal(1, atOther);  // original transport, no build
    }

    [Fact]
    public void Cruiser_at_occupied_offers_gates_of_transport_nodes()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        // Transport at home → cruiser may build on any gate of home.
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var h = new BuildHandler(1, BuildShipFilter.CruiserOnly, BuildLocationKind.Occupied);
        var ctx = NewCtx(p1);
        SelectShipPlacementRequest? captured = null;
        Resolve(g, h, ctx, choice =>
        {
            captured = (SelectShipPlacementRequest)choice;
            captured.Chosen = captured.LegalLocations[0];
        });
        Assert.NotNull(captured);
        Assert.All(captured!.LegalLocations, l => Assert.IsType<ShipLocation.OnGate>(l));
        var homeGateIds = g.Map.AdjacencyByNode[home].Select(x => x.Id).ToHashSet();
        Assert.All(captured.LegalLocations,
            l => Assert.Contains(((ShipLocation.OnGate)l).Gate, homeGateIds));
    }

    [Fact]
    public void Cruiser_alone_does_not_unlock_more_locations()
    {
        // A cruiser on a gate is patrol, not occupation. With no transport
        // anywhere, the player has no occupied nodes for build purposes.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var someGate = g.Map.AdjacencyByNode[home].First().Id;
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(someGate)));

        var h = new BuildHandler(1, BuildShipFilter.CruiserOnly, BuildLocationKind.Occupied);
        var ctx = NewCtx(p1);
        h.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Null(ctx.PendingChoice);
    }

    [Fact]
    public void Ships_at_occupied_offers_node_and_its_gates()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var h = new BuildHandler(2, BuildShipFilter.Either, BuildLocationKind.Occupied);
        var ctx = NewCtx(p1);
        SelectShipPlacementRequest? captured = null;
        Resolve(g, h, ctx, choice =>
        {
            captured = (SelectShipPlacementRequest)choice;
            captured.Chosen = captured.LegalLocations[0];
        });
        Assert.NotNull(captured);
        // Should include the node itself plus all of its gates.
        var hasNode = captured!.LegalLocations.Any(l => l is ShipLocation.OnNode n && n.Node == home);
        var gateCount = captured.LegalLocations.Count(l => l is ShipLocation.OnGate);
        var expectedGates = g.Map.AdjacencyByNode[home].Count();
        Assert.True(hasNode);
        Assert.Equal(expectedGates, gateCount);
    }

    [Fact]
    public void All_other_build_families_registered()
    {
        var r = new EffectRegistry();
        BuildRegistrations.RegisterAll(r);
        Assert.True(r.IsRegistered("build_n_transport_at_home"));
        Assert.True(r.IsRegistered("build_n_cruiser_at_home"));
        Assert.True(r.IsRegistered("build_n_transport_at_occupied"));
        Assert.True(r.IsRegistered("build_n_cruiser_at_occupied"));
        Assert.True(r.IsRegistered("build_n_ships_at_occupied"));
    }
}
