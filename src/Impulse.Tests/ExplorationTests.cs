using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class ExplorationTests
{
    private static (GameState g, EffectRegistry r) Bootstrap()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0,
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
    public void Setup_populates_node_card_state()
    {
        var (g, _) = Bootstrap();
        // Sector Core entry exists.
        Assert.IsType<NodeCardState.SectorCore>(g.NodeCards[g.Map.SectorCoreNodeId]);
        // Each home is face-up.
        foreach (var (pid, nodeId) in g.Map.HomeNodeIds)
            Assert.IsType<NodeCardState.FaceUp>(g.NodeCards[nodeId]);
        // At least one non-home, non-sector-core is face-down.
        Assert.Contains(g.NodeCards.Values, s => s is NodeCardState.FaceDown);
    }

    [Fact]
    public void Transport_onto_face_down_triggers_exploration()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        // Find a face-down neighbor.
        var fdNeighbor = g.Map.AdjacencyByNode[home]
            .Select(gate => gate.EndpointA == home ? gate.EndpointB : gate.EndpointA)
            .First(n => g.NodeCards[n] is NodeCardState.FaceDown);
        var fdState = (NodeCardState.FaceDown)g.NodeCards[fdNeighbor];
        int originalFdCard = fdState.CardId;

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        // Plant a different size-1 card in hand to use as the face-up replacement.
        var replacement = g.Deck.First(id => id != originalFdCard);
        g.Deck.Remove(replacement);
        g.Player(p1).Hand.Add(replacement);

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35); // 1 transport, 1 move
        SelectHandCardRequest? handReq = null;
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f:
                    f.Chosen = new ShipLocation.OnNode(home);
                    break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnNode n && n.Node == fdNeighbor);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h:
                    handReq = h;
                    h.ChosenCardId = replacement; // place the planted card
                    break;
            }
        });

        Assert.NotNull(handReq);
        // Original face-down card was added to hand temporarily, so legal list
        // should include both replacement and original.
        Assert.Contains(originalFdCard, handReq!.LegalCardIds);
        // After: node is face-up with the chosen replacement.
        Assert.IsType<NodeCardState.FaceUp>(g.NodeCards[fdNeighbor]);
        Assert.Equal(replacement, ((NodeCardState.FaceUp)g.NodeCards[fdNeighbor]).CardId);
        // Original card stays in hand (it was added during exploration; only
        // `replacement` was removed when placed face-up).
        Assert.Contains(originalFdCard, g.Player(p1).Hand);
        Assert.DoesNotContain(replacement, g.Player(p1).Hand);
        // Transport ended its movement on the explored node.
        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == fdNeighbor);
    }

    [Fact]
    public void Player_can_place_back_the_just_picked_up_card()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var fdNeighbor = g.Map.AdjacencyByNode[home]
            .Select(gate => gate.EndpointA == home ? gate.EndpointB : gate.EndpointA)
            .First(n => g.NodeCards[n] is NodeCardState.FaceDown);
        var fdState = (NodeCardState.FaceDown)g.NodeCards[fdNeighbor];
        int originalFdCard = fdState.CardId;

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnNode(home); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnNode n && n.Node == fdNeighbor);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = originalFdCard; break;
            }
        });

        // Same card placed back face-up.
        Assert.IsType<NodeCardState.FaceUp>(g.NodeCards[fdNeighbor]);
        Assert.Equal(originalFdCard, ((NodeCardState.FaceUp)g.NodeCards[fdNeighbor]).CardId);
        // Hand size returns to 0 (added 1, placed 1 back).
        Assert.Empty(g.Player(p1).Hand);
    }

    [Fact]
    public void Cruiser_passing_through_face_down_explores()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[home].First();
        var passageNode = startGate.EndpointA == home ? startGate.EndpointB : startGate.EndpointA;
        // Skip if passage is sector core or home (won't be face-down).
        if (g.NodeCards[passageNode] is not NodeCardState.FaceDown fd) return;
        int originalCard = fd.CardId;
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        var replacement = g.Deck.First(id => id != originalCard);
        g.Deck.Remove(replacement);
        g.Player(p1).Hand.Add(replacement);

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
                case SelectHandCardRequest h: h.ChosenCardId = replacement; break;
            }
        });

        Assert.IsType<NodeCardState.FaceUp>(g.NodeCards[passageNode]);
    }

    [Fact]
    public void Transport_ending_on_explored_card_activates_it()
    {
        // Rulebook p.29: "When moving a Transport fleet to explore, the
        // card they end their movement on will be activated."
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        RefineRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0,
                InitialHandSize: 0),
            r);
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var fdNeighbor = g.Map.AdjacencyByNode[home]
            .Select(gate => gate.EndpointA == home ? gate.EndpointB : gate.EndpointA)
            .First(n => g.NodeCards[n] is NodeCardState.FaceDown);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        // Plant card #43 (refine, no minerals → noop completes immediately) in hand.
        if (!g.Deck.Contains(43)) { /* card 43 already in deck per setup */ }
        g.Deck.Remove(43);
        g.Player(p1).Hand.Add(43);

        var handler = new CommandHandler(r, CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnNode(home); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnNode n && n.Node == fdNeighbor);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = 43; break;
            }
        });

        Assert.True(ctx.IsComplete);
        Assert.IsType<NodeCardState.FaceUp>(g.NodeCards[fdNeighbor]);
        Assert.Equal(43, ((NodeCardState.FaceUp)g.NodeCards[fdNeighbor]).CardId);
        // Activation fired on the just-placed card.
        Assert.Contains(g.Log.Lines, line => line.Contains("activating #43"));
    }

    [Fact]
    public void Transport_onto_sector_core_activates_with_color_choice()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0,
                InitialHandSize: 0,
                AllNodesFaceUp: true),
            r);
        var p1 = new PlayerId(1);
        var coreId = g.Map.SectorCoreNodeId;
        // Find a node adjacent to sector core to launch from.
        var launch = g.Map.AdjacencyByNode[coreId]
            .Select(gate => gate.EndpointA == coreId ? gate.EndpointB : gate.EndpointA)
            .First();
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(launch)));

        // Stack 3 red minerals (any size) → gems = sum of sizes; with 1
        // arriving transport, boost = (gems + 1) / 2; score = 1 + boost.
        var reds = g.Deck.Where(id => g.CardsById[id].Color == CardColor.Red).Take(3).ToList();
        int redGems = reds.Sum(id => g.CardsById[id].Size);
        foreach (var id in reds) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }
        int expectedBoost = (redGems + 1) / 2;
        int expectedScore = 1 + expectedBoost;

        int prestigeBefore = g.Player(p1).Prestige;
        var handler = new CommandHandler(r, CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35); // 1 transport, 1 move
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnNode(launch); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnNode n && n.Node == coreId);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                // Display order is Red, Blue, Green, Yellow → Red is index 0.
                case SelectFromOptionsRequest opt: opt.Chosen = 0; break;
            }
        });

        Assert.True(ctx.IsComplete);
        Assert.Equal(prestigeBefore + expectedScore, g.Player(p1).Prestige);
        Assert.Contains(g.Log.Lines, line => line.Contains("Sector Core activated as Red"));
    }

    [Fact]
    public void Transport_onto_face_up_card_triggers_activation()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        RefineRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0,
                InitialHandSize: 0),
            r);

        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        var neighbor = g.Map.AdjacencyByNode[home]
            .Select(gate => gate.EndpointA == home ? gate.EndpointB : gate.EndpointA)
            .First(n => n != g.Map.SectorCoreNodeId);
        // Force the neighbor face-up with refine card #43 so transport
        // arrival activates a known handler. Player has zero minerals,
        // so refine completes immediately ("no matching mineral").
        g.NodeCards[neighbor] = new NodeCardState.FaceUp(43);
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));

        var handler = new CommandHandler(r, CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 35); // 1 transport, 1 move
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnNode(home); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths
                        .First(p => p.Count == 1 &&
                                    p[0] is ShipLocation.OnNode n && n.Node == neighbor);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
            }
        });

        Assert.True(ctx.IsComplete);
        Assert.Contains(g.Log.Lines, line => line.Contains("activating #43"));
        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnNode n && n.Node == neighbor);
    }

}
