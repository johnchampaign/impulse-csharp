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
    public void Multi_fleet_allows_mixing_transport_and_cruiser_to_same_card()
    {
        // Reproduces the user-reported bug: c75 (FleetCount=2, Either, 1 move).
        // Transport adjacent to Sector Core + Cruiser on a Sector-Core gate.
        // Both should be able to move "to the same card" (the Sector Core):
        // transport → SC node, cruiser → a different SC gate.
        // Rulebook p.29 explicitly allows mixing one transport + one cruiser.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var sc = g.Map.SectorCoreNodeId;
        var scGates = g.Map.AdjacencyByNode[sc].ToList();
        // Pick two distinct SC gates. The cruiser starts on gateC1, will move
        // through SC to gateC2.
        var gateC1 = scGates[0];
        var gateC2 = scGates[1];
        // Transport adjacent to SC: take the OTHER endpoint of gateC1 (non-SC).
        var gateC1Info = g.Map.Gate(gateC1.Id);
        var transportNode = gateC1Info.EndpointA == sc ? gateC1Info.EndpointB : gateC1Info.EndpointA;

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(transportNode)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(gateC2.Id)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 75);

        // Fleet 1: pick transport, move to SC.
        handler.Execute(g, ctx);
        var f1 = (SelectFleetRequest)ctx.PendingChoice!;
        Assert.Contains(f1.LegalLocations, l => l is ShipLocation.OnNode n && n.Node == transportNode);
        f1.Chosen = new ShipLocation.OnNode(transportNode);
        ctx.Paused = false;
        handler.Execute(g, ctx);
        if (ctx.PendingChoice is SelectFleetSizeRequest size1)
        {
            size1.Chosen = 1;
            ctx.Paused = false;
            handler.Execute(g, ctx);
        }
        var p1Path = (DeclareMoveRequest)ctx.PendingChoice!;
        var pathToSc = p1Path.LegalPaths.First(p => p[^1] is ShipLocation.OnNode n && n.Node == sc);
        p1Path.ChosenPath = pathToSc;
        ctx.Paused = false;
        handler.Execute(g, ctx);

        // Drain any intermediate prompts (e.g. SC mineral-color choice
        // when transport lands on Sector Core, or SelectFleetSize) until
        // the next SelectFleetRequest for fleet 2.
        while (ctx.PendingChoice is not null && ctx.PendingChoice is not SelectFleetRequest)
        {
            switch (ctx.PendingChoice)
            {
                case SelectFleetSizeRequest sz: sz.Chosen = sz.Min; break;
                case SelectFromOptionsRequest opt: opt.Chosen = 0; break;
                default: throw new Xunit.Sdk.XunitException($"Unexpected prompt: {ctx.PendingChoice.GetType().Name}");
            }
            ctx.Paused = false;
            handler.Execute(g, ctx);
        }
        var f2 = (SelectFleetRequest)ctx.PendingChoice!;
        Assert.True(
            f2.LegalLocations.Any(l => l is ShipLocation.OnGate g2 && g2.Gate == gateC2.Id),
            $"Cruiser on SC gate {gateC2.Id} should be a legal fleet-2 origin after fleet 1 converged on SC. Legal: [{string.Join(",", f2.LegalLocations.Select(Mechanics.LocStr))}]");
    }

    [Fact]
    public void Patrol_through_with_multiple_patrollers_prompts_attacker_to_choose()
    {
        // Rulebook p.29: "If multiple players patrol the same card, the
        // player moving ships can choose who to fight." Reproduce: P1 has a
        // cruiser on a gate of node X, two enemies (P2 and P3) patrol X via
        // different gates. P1 commands their cruiser through X (toward an
        // unoccupied gate). The handler must prompt P1 with both P2 and P3
        // as options, NOT silently pick the first.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        // Find a node with 3+ gates so we can place P1, P2, P3, and a target.
        var node = g.Map.Nodes.First(n =>
            !n.IsHome && !n.IsSectorCore &&
            g.Map.AdjacencyByNode[n.Id].Count() >= 3);
        var gates = g.Map.AdjacencyByNode[node.Id].ToList();
        // P1 cruiser on gates[0]; P2 cruiser on gates[1]; P3 cruiser on gates[2].
        // Need at least 2 enemies — game has 2 players in Bootstrap, so add a
        // 3rd enemy by mocking a placement for player 3 (Bootstrap creates
        // 2 players; for this test we just need 2 enemies, so use P2 + a
        // synthetic third placement for P2 won't work — we need distinct
        // owners. Re-bootstrap with 3 players.)
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        g = SetupFactory.NewGame(
            new SetupOptions(3, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true,
                InitialHandSize: 0),
            r);
        var p3 = new PlayerId(3);
        node = g.Map.Nodes.First(n =>
            !n.IsHome && !n.IsSectorCore &&
            g.Map.AdjacencyByNode[n.Id].Count() >= 3);
        gates = g.Map.AdjacencyByNode[node.Id].ToList();
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(gates[0].Id)));
        g.ShipPlacements.Add(new(new PlayerId(2), new ShipLocation.OnGate(gates[1].Id)));
        g.ShipPlacements.Add(new(p3, new ShipLocation.OnGate(gates[2].Id)));

        // c75 is "command 2 fleets up to 12, 1 move apiece" — but we want a
        // single-fleet 1-move command. Use c31 (cruiser 1 fleet, 1 move).
        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);

        // Resolve: pick P1's cruiser as the fleet, declare path through node.
        // Drain prompts until we hit either a SelectFromOptionsRequest
        // (defender choice — the bug fix) or completion.
        SelectFromOptionsRequest? defenderPrompt = null;
        bool completed = false;
        for (int safety = 0; safety < 20 && !completed; safety++)
        {
            handler.Execute(g, ctx);
            if (ctx.IsComplete) { completed = true; break; }
            if (ctx.PendingChoice is null) break;
            switch (ctx.PendingChoice)
            {
                case SelectFleetRequest f:
                    f.Chosen = new ShipLocation.OnGate(gates[0].Id);
                    break;
                case SelectFleetSizeRequest sz:
                    sz.Chosen = sz.Min;
                    break;
                case DeclareMoveRequest dm:
                    // Pick a path that passes through `node` (any path of
                    // length 1 ending on gates[1] or gates[2] will pass
                    // through node since gates[0] also touches node).
                    var path = dm.LegalPaths.FirstOrDefault(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og &&
                        (og.Gate == gates[1].Id || og.Gate == gates[2].Id));
                    Assert.NotNull(path);
                    dm.ChosenPath = path;
                    break;
                case SelectFromOptionsRequest opt:
                    defenderPrompt = opt;
                    completed = true; // we got the prompt — stop
                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected: {ctx.PendingChoice.GetType().Name}");
            }
            ctx.Paused = false;
        }

        Assert.NotNull(defenderPrompt);
        // Both P2 and P3 should be options.
        Assert.Equal(2, defenderPrompt!.Options.Count);
        Assert.Contains("P2", string.Join(" ", defenderPrompt.Options));
        Assert.Contains("P3", string.Join(" ", defenderPrompt.Options));
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
