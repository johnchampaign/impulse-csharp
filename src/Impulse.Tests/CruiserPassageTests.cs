using Impulse.Core;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class CruiserPassageTests
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

    private static void RunToCompletion(IEffectHandler h, GameState g, EffectContext ctx,
        Action<ChoiceRequest> answer)
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
    public void Cruiser_passing_through_card_destroys_enemy_transport_and_scores()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);

        // Pick a P1 home gate to start at; passage node = the non-home endpoint.
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        // Pick another gate sharing the passage node as the move target.
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(passageNode)));

        var initialP2Available = g.Player(p2).ShipsAvailable;

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31); // Cruiser, fleet 1, 1 move
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f:
                    f.Chosen = new ShipLocation.OnGate(startGate.Id);
                    break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs:
                    fs.Chosen = fs.Min;
                    break;
            }
        });

        Assert.True(ctx.IsComplete);
        // Enemy transport gone from board, returned to pool.
        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnNode n && n.Node == passageNode);
        Assert.Equal(initialP2Available + 1, g.Player(p2).ShipsAvailable);
        // P1 scored 1.
        Assert.Equal(1, g.Player(p1).Prestige);
    }

    [Fact]
    public void Cruiser_passage_does_not_destroy_friendly_transport()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode].First(gate => gate.Id != startGate.Id);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(passageNode))); // own transport

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
            }
        });

        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == passageNode);
        Assert.Equal(0, g.Player(p1).Prestige);
    }

    [Fact]
    public void Cruiser_passage_destroys_all_enemy_transports_on_card()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode].First(gate => gate.Id != startGate.Id);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(passageNode)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(passageNode)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(passageNode)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
            }
        });

        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnNode);
        Assert.Equal(3, g.Player(p1).Prestige);
    }

    [Fact]
    public void Transport_movement_does_not_destroy_enemy_transports()
    {
        // Transports moving onto a card with enemy transports just coexist.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var neighbor = ((ShipLocation.OnNode)Movement
            .Neighbors(g.Map, new ShipLocation.OnNode(p1Home))[0]).Node;

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(neighbor)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35); // Transport, fleet 1, 1 move
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnNode(p1Home); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnNode n && n.Node == neighbor);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
            }
        });

        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnNode n && n.Node == neighbor);
        Assert.Equal(0, g.Player(p1).Prestige);
    }
}
