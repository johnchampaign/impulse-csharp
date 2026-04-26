using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class CommandHandlerTests
{
    private static (GameState g, EffectRegistry r) Bootstrap()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true,
                InitialHandSize: 0),
            r);
        return (g, r);
    }

    private static EffectContext Ctx(PlayerId pid, int sourceCardId) => new()
    {
        ActivatingPlayer = pid,
        Source = new EffectSource.ImpulseCard(sourceCardId),
    };

    [Fact]
    public void Cruiser_only_card_excludes_transport_origins()
    {
        // Regression: c31 "Command one Cruiser for [1] move." with only a
        // transport on the board must NOT offer the transport's node as a
        // legal origin.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31); // Cruiser-only
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete); // no legal origin → noop
        Assert.Null(ctx.PendingChoice);
    }

    [Fact]
    public void Transport_only_card_excludes_cruiser_origins()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var someGate = g.Map.AdjacencyByNode[home].First().Id;
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(someGate)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35); // Transport-only
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Null(ctx.PendingChoice);
    }

    [Fact]
    public void Either_card_offers_both_ship_types()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var someGate = g.Map.AdjacencyByNode[home].First().Id;
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(someGate)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 4); // Either, fleet 1, 1 move
        handler.Execute(g, ctx);
        var req = (SelectFleetRequest)ctx.PendingChoice!;
        Assert.Equal(2, req.LegalLocations.Count);
        Assert.Contains(req.LegalLocations, l => l is ShipLocation.OnNode);
        Assert.Contains(req.LegalLocations, l => l is ShipLocation.OnGate);
    }

    [Fact]
    public void Fleet_command_with_one_ship_qualifies_and_skips_count_prompt()
    {
        // "Up to 2" interpretation: one ship at origin is enough; max=1 so
        // count prompt is skipped (only one option).
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 13); // up to 2 ships
        // Origin
        handler.Execute(g, ctx);
        var fleetReq = (SelectFleetRequest)ctx.PendingChoice!;
        fleetReq.Chosen = fleetReq.LegalLocations[0];
        ctx.Paused = false;
        // Count prompt skipped (only 1 ship at origin → max=1)
        handler.Execute(g, ctx);
        Assert.IsType<DeclareMoveRequest>(ctx.PendingChoice);
    }

    [Fact]
    public void Fleet_command_prompts_for_count_when_choice_available()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 13); // up to 2
        handler.Execute(g, ctx);
        var fleetReq = (SelectFleetRequest)ctx.PendingChoice!;
        fleetReq.Chosen = fleetReq.LegalLocations[0];
        ctx.Paused = false;
        handler.Execute(g, ctx);

        var sizeReq = (SelectFleetSizeRequest)ctx.PendingChoice!;
        Assert.Equal(1, sizeReq.Min);
        Assert.Equal(2, sizeReq.Max);
    }

    [Fact]
    public void Fleet_command_caps_count_at_ships_present()
    {
        // Card allows up to 2; only 1 ship present → max=1, no prompt.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 13);
        handler.Execute(g, ctx);
        var fleetReq = (SelectFleetRequest)ctx.PendingChoice!;
        fleetReq.Chosen = fleetReq.LegalLocations[0];
        ctx.Paused = false;
        handler.Execute(g, ctx);
        // Should be DeclareMoveRequest, not SelectFleetSizeRequest
        Assert.IsType<DeclareMoveRequest>(ctx.PendingChoice);
    }

    [Fact]
    public void Fleet_command_moves_chosen_count()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 13); // up to 2
        // Origin
        handler.Execute(g, ctx);
        ((SelectFleetRequest)ctx.PendingChoice!).Chosen =
            ((SelectFleetRequest)ctx.PendingChoice!).LegalLocations[0];
        ctx.Paused = false;
        // Count
        handler.Execute(g, ctx);
        ((SelectFleetSizeRequest)ctx.PendingChoice!).Chosen = 1; // move just 1
        ctx.Paused = false;
        // Path
        handler.Execute(g, ctx);
        var path = (DeclareMoveRequest)ctx.PendingChoice!;
        path.ChosenPath = path.LegalPaths[0];
        ctx.Paused = false;
        // Apply
        handler.Execute(g, ctx);

        Assert.True(ctx.IsComplete);
        var atHome = g.ShipPlacements.Count(sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == home);
        Assert.Equal(2, atHome);
        var atDest = g.ShipPlacements.Count(sp =>
            sp.Owner == p1 && Mechanics.LocationsEqual(sp.Location, path.ChosenPath![0]));
        Assert.Equal(1, atDest);
    }

    [Fact]
    public void Multi_fleet_card_prompts_for_each_fleet()
    {
        // c75 base FleetCount=2. Two distinct origins → two pick prompts.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var gateA = g.Map.AdjacencyByNode[home].First();
        var gateB = g.Map.AdjacencyByNode[home].Skip(1).First();
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(gateA.Id)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(gateB.Id)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 75);

        // Fleet 1
        handler.Execute(g, ctx);
        var fleet1 = (SelectFleetRequest)ctx.PendingChoice!;
        Assert.Equal(2, fleet1.LegalLocations.Count);
        fleet1.Chosen = new ShipLocation.OnGate(gateA.Id);
        ctx.Paused = false;
        handler.Execute(g, ctx);
        var path1 = (DeclareMoveRequest)ctx.PendingChoice!;
        path1.ChosenPath = path1.LegalPaths[0];
        ctx.Paused = false;
        handler.Execute(g, ctx);

        // Fleet 2 — origin pick, gateA must be excluded.
        var fleet2 = (SelectFleetRequest)ctx.PendingChoice!;
        Assert.DoesNotContain(fleet2.LegalLocations,
            l => l is ShipLocation.OnGate og && og.Gate == gateA.Id);
        Assert.Contains(fleet2.LegalLocations,
            l => l is ShipLocation.OnGate og && og.Gate == gateB.Id);
        fleet2.Chosen = new ShipLocation.OnGate(gateB.Id);
        ctx.Paused = false;
        handler.Execute(g, ctx);
        // If fleet 1's destination merged onto gateB, a size prompt fires.
        if (ctx.PendingChoice is SelectFleetSizeRequest size2)
        {
            size2.Chosen = 1;
            ctx.Paused = false;
            handler.Execute(g, ctx);
        }
        var path2 = (DeclareMoveRequest)ctx.PendingChoice!;
        path2.ChosenPath = path2.LegalPaths[0];
        ctx.Paused = false;
        handler.Execute(g, ctx);

        Assert.True(ctx.IsComplete);
    }

    [Fact]
    public void Multi_fleet_card_constrains_second_fleet_to_first_destination_card()
    {
        // c75 (FleetCount=2, Either, 1 move). Set up two cruisers at separate
        // gates; verify fleet 2's legal paths only end at gates touching the
        // node card fleet 1 chose to converge on.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var gateA = g.Map.AdjacencyByNode[home].First();
        var gateB = g.Map.AdjacencyByNode[home].Skip(1).First();
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(gateA.Id)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(gateB.Id)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 75);

        // Fleet 1 → pick gateA, declare a path.
        handler.Execute(g, ctx);
        var f1 = (SelectFleetRequest)ctx.PendingChoice!;
        f1.Chosen = new ShipLocation.OnGate(gateA.Id);
        ctx.Paused = false;
        handler.Execute(g, ctx);
        var p1path = (DeclareMoveRequest)ctx.PendingChoice!;
        p1path.ChosenPath = p1path.LegalPaths[0];
        var firstEndGate = (ShipLocation.OnGate)p1path.ChosenPath[^1];
        var firstEndGateInfo = g.Map.Gate(firstEndGate.Gate);
        var compatNodes = new HashSet<NodeId> { firstEndGateInfo.EndpointA, firstEndGateInfo.EndpointB };
        ctx.Paused = false;
        handler.Execute(g, ctx);

        if (ctx.PendingChoice is SelectFleetSizeRequest size2)
        {
            size2.Chosen = 1;
            ctx.Paused = false;
            handler.Execute(g, ctx);
        }
        // Fleet 2 origin pick.
        var f2 = (SelectFleetRequest)ctx.PendingChoice!;
        f2.Chosen = new ShipLocation.OnGate(gateB.Id);
        ctx.Paused = false;
        handler.Execute(g, ctx);
        if (ctx.PendingChoice is SelectFleetSizeRequest size3)
        {
            size3.Chosen = 1;
            ctx.Paused = false;
            handler.Execute(g, ctx);
        }
        // Fleet 2's path prompt. Every legal path's end-gate must touch one
        // of fleet 1's compatible nodes.
        var p2path = (DeclareMoveRequest)ctx.PendingChoice!;
        Assert.NotEmpty(p2path.LegalPaths);
        foreach (var path in p2path.LegalPaths)
        {
            var endGate = (ShipLocation.OnGate)path[^1];
            var info = g.Map.Gate(endGate.Gate);
            Assert.True(compatNodes.Contains(info.EndpointA) || compatNodes.Contains(info.EndpointB),
                $"path end {endGate.Gate} touches neither {string.Join(",", compatNodes)}");
        }
    }

    [Fact]
    public void All_command_cards_have_params()
    {
        var cards = CardDataLoader.LoadAll();
        var commands = cards.Where(c => c.ActionType == CardActionType.Command).ToList();
        var paramIds = CommandRegistrations.ByCardId.Keys.ToHashSet();
        var missing = commands.Where(c => !paramIds.Contains(c.Id)).ToList();
        Assert.Empty(missing);
    }
}
