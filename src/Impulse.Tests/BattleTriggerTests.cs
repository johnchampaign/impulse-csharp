using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class BattleTriggerTests
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
    public void Cruiser_into_enemy_cruiser_gate_triggers_battle_and_resolves()
    {
        // Set up: P1 cruiser at gateA, P2 cruiser at gateB, gateA→gateB legal move.
        // No reinforcements possible (no Plan/Impulse/Tech anchors). Both draw 1
        // card per cruiser. Defender wins ties.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(targetGate.Id)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31); // 1 cruiser, 1 move
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break; // no reinforcements
            }
        });

        // Battle resolved without ending game.
        Assert.False(g.IsGameOver);
        Assert.True(ctx.IsComplete);
        // Either P1 won (moved to targetGate, P2 destroyed) or P2 won (P1 destroyed).
        // Total cruisers on the field is 1 (one survivor).
        int cruisersOnField = g.ShipPlacements.Count(sp => sp.Location is ShipLocation.OnGate);
        Assert.Equal(1, cruisersOnField);
    }

    [Fact]
    public void Cruiser_through_patrolled_card_resolves_battle()
    {
        // Rulebook p.29: a cruiser cannot move through a patrolled card
        // except to start a battle. The "battle" must end on a gate that
        // contains an enemy cruiser — there is no rulebook concept of
        // moving to an empty gate and being "redirected" to the patroller.
        // So this test moves directly to the patroller's gate.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var enemyPatrolGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(enemyPatrolGate.Id)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == enemyPatrolGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break;
            }
        });

        Assert.False(g.IsGameOver);
        Assert.True(ctx.IsComplete);
    }

    [Fact]
    public void Defender_winning_destroys_attacker_fleet_and_scores()
    {
        // Stack the deck so defender's draws beat attacker's. Both have 1 cruiser.
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(targetGate.Id)));

        // Per-cruiser draw order: defender draws first, then attacker.
        // Stack deck: [size3 (defender's), size1 (attacker's)] → defender 3 vs attacker 1.
        var s3 = g.Deck.First(id => g.CardsById[id].Size == 3);
        g.Deck.Remove(s3);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Deck.Insert(0, s3);
        g.Deck.Insert(1, s1);

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break;
            }
        });

        // Defender won: P1's cruiser destroyed; P2's stays.
        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnGate);
        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnGate og && og.Gate == targetGate.Id);
        // P2 scored: +1 battle won, +1 ship destroyed = 2 prestige.
        Assert.Equal(2, g.Player(p2).Prestige);
    }

    [Fact]
    public void Attacker_winning_destroys_defender_and_moves_to_battle_gate()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(targetGate.Id)));

        // Defender draws first → size1; attacker draws second → size3. Attacker 3 vs defender 1.
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        var s3 = g.Deck.First(id => g.CardsById[id].Size == 3);
        g.Deck.Remove(s3);
        g.Deck.Insert(0, s1);
        g.Deck.Insert(1, s3);

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break;
            }
        });

        // P1 won: P2's cruiser destroyed; P1 moved to targetGate.
        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnGate);
        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p1 && sp.Location is ShipLocation.OnGate og && og.Gate == targetGate.Id);
        Assert.Equal(2, g.Player(p1).Prestige); // +1 win, +1 destroyed
    }

    [Fact]
    public void Defender_wins_ties()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(targetGate.Id)));

        // Both draw size-2 → tie. Defender wins.
        var s2a = g.Deck.First(id => g.CardsById[id].Size == 2);
        g.Deck.Remove(s2a);
        var s2b = g.Deck.First(id => g.CardsById[id].Size == 2);
        g.Deck.Remove(s2b);
        g.Deck.Insert(0, s2a);
        g.Deck.Insert(1, s2b);

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break;
            }
        });

        // Tie → defender wins → P1 destroyed, P2 keeps gate.
        Assert.DoesNotContain(g.ShipPlacements, sp => sp.Owner == p1);
        Assert.Equal(2, g.Player(p2).Prestige);
    }

    [Fact]
    public void Cruiser_through_unpatrolled_card_does_not_trigger_battle()
    {
        var (g, _) = Bootstrap();
        var p1 = new PlayerId(1);
        var p1Home = g.Map.HomeNodeIds[p1];
        var startGate = g.Map.AdjacencyByNode[p1Home].First();
        var passageNode = startGate.EndpointA == p1Home ? startGate.EndpointB : startGate.EndpointA;
        var targetGate = g.Map.AdjacencyByNode[passageNode]
            .First(gate => gate.Id != startGate.Id);

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == targetGate.Id);
                    break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
            }
        });

        Assert.False(g.IsGameOver);
        Assert.True(ctx.IsComplete);
    }

    [Fact]
    public void Patrol_through_battle_destroys_third_party_transports_on_passage_node()
    {
        // Rulebook p.28: "A Cruiser fleet that moves through a card containing
        // enemy Transports destroys them all... if movement results in a
        // battle, the Transports are only destroyed if you win."
        //
        // Scenario: P1 (attacker) moves cruiser through node X to fight P2's
        // patrolling cruiser. P3 (bystander, not in the battle) has transports
        // on node X. When P1 wins the battle, P3's transports MUST also be
        // destroyed — they were on the card P1's cruiser passed through.
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(3, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true,
                InitialHandSize: 0),
            r);
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p3 = new PlayerId(3);

        // Pick a passage node with ≥3 gates so we have 3 distinct positions.
        var passageNode = g.Map.Nodes.First(n =>
            !n.IsHome && !n.IsSectorCore &&
            g.Map.AdjacencyByNode[n.Id].Count() >= 3).Id;
        var gates = g.Map.AdjacencyByNode[passageNode].ToList();
        var startGate = gates[0];   // P1 cruiser starts here
        var targetGate = gates[1];  // P1 cruiser tries to move here (through passage)
        var p2PatrolGate = gates[2]; // P2 cruiser patrols passage via this gate

        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(p2PatrolGate.Id)));
        // P3 has 2 transports on the passage node — uninvolved bystander.
        g.ShipPlacements.Add(new(p3, new ShipLocation.OnNode(passageNode)));
        g.ShipPlacements.Add(new(p3, new ShipLocation.OnNode(passageNode)));

        // Stack the deck so P1 (attacker) wins: P2 first draw size 1, P1 first
        // draw size 3.
        var s3 = g.Deck.First(id => g.CardsById[id].Size == 3);
        g.Deck.Remove(s3);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        // BattleResolver draws defender first then attacker.
        g.Deck.Insert(0, s1);
        g.Deck.Insert(1, s3);

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31); // c31: cruiser, 1 fleet, 1 move
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == p2PatrolGate.Id);
                    break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break;
            }
        });

        // P1 wins → P3's bystander transports on passage node ALL destroyed.
        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p3 && sp.Location is ShipLocation.OnNode n && n.Node == passageNode);
        // P2's cruiser also destroyed (the battle).
        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnGate);
        // P1 scores: +1 battle win + 1 (P2 cruiser) + 2 (P3 transports) = 4.
        Assert.Equal(4, g.Player(p1).Prestige);
    }

    [Fact]
    public void Patrol_through_battle_lost_does_not_destroy_third_party_transports()
    {
        // Mirror of above: P1 LOSES the patrol-through battle. P3's transports
        // on the passage node must SURVIVE (rulebook: "destroyed only if you
        // win the battle").
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        var g = SetupFactory.NewGame(
            new SetupOptions(3, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true,
                InitialHandSize: 0),
            r);
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p3 = new PlayerId(3);
        var passageNode = g.Map.Nodes.First(n =>
            !n.IsHome && !n.IsSectorCore &&
            g.Map.AdjacencyByNode[n.Id].Count() >= 3).Id;
        var gates = g.Map.AdjacencyByNode[passageNode].ToList();
        var startGate = gates[0];
        var targetGate = gates[1];
        var p2PatrolGate = gates[2];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnGate(startGate.Id)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnGate(p2PatrolGate.Id)));
        g.ShipPlacements.Add(new(p3, new ShipLocation.OnNode(passageNode)));

        // Stack: P2 (defender) draws size 3, P1 draws size 1 → P2 wins.
        var s3 = g.Deck.First(id => g.CardsById[id].Size == 3);
        g.Deck.Remove(s3);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Deck.Insert(0, s3);
        g.Deck.Insert(1, s1);

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 31);
        RunToCompletion(handler, g, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = new ShipLocation.OnGate(startGate.Id); break;
                case SelectFleetSizeRequest fs: fs.Chosen = fs.Min; break;
                case DeclareMoveRequest m:
                    m.ChosenPath = m.LegalPaths.First(p =>
                        p.Count == 1 && p[0] is ShipLocation.OnGate og && og.Gate == p2PatrolGate.Id);
                    break;
                case SelectHandCardRequest h: h.ChosenCardId = null; break;
            }
        });

        // P1 lost → P3's transport SURVIVES on passage node.
        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p3 && sp.Location is ShipLocation.OnNode n && n.Node == passageNode);
    }
}
