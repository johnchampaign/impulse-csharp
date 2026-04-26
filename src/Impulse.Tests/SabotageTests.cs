using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class SabotageTests
{
    private static EffectRegistry BuildRegistry()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        BuildRegistrations.RegisterAll(r);
        MineRegistrations.RegisterAll(r);
        RefineRegistrations.RegisterAll(r);
        DrawRegistrations.RegisterAll(r);
        TradeRegistrations.RegisterAll(r);
        PlanRegistrations.RegisterAll(r);
        ResearchRegistrations.RegisterAll(r);
        SabotageRegistrations.RegisterAll(r);
        return r;
    }

    private static GameState NewGame() =>
        SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true,
                InitialHandSize: 0),
            BuildRegistry());

    private static EffectContext Ctx(PlayerId pid, int sourceCardId) => new()
    {
        ActivatingPlayer = pid,
        Source = new EffectSource.ImpulseCard(sourceCardId),
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

    private static void StackDeck(GameState g, params int[] ids)
    {
        foreach (var id in ids) g.Deck.Remove(id);
        for (int i = 0; i < ids.Length; i++) g.Deck.Insert(i, ids[i]);
    }

    [Fact]
    public void No_legal_targets_completes_silently()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 15); // 1 bomb, any fleet type
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Null(ctx.PendingChoice);
    }

    [Fact]
    public void Sabotage_destroys_enemy_transport_with_size_two_hit()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        // P1 occupies home with a transport.
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(p1Home)));
        // P2 also has a transport on the same home node (to be sabotaged).
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home)));

        // Stack deck so the bomb reveals a size-2 (hit).
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        StackDeck(g, s2);

        int p2BeforeAvailable = g.Player(p2).ShipsAvailable;
        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 15); // 1 bomb, any
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectSabotageTargetRequest)choice;
            req.Chosen = req.LegalTargets.First(t => t.Owner == p2);
        });

        Assert.Equal(p2BeforeAvailable + 1, g.Player(p2).ShipsAvailable);
        Assert.Equal(1, g.Player(p1).Prestige); // +1 ship destroyed
        Assert.DoesNotContain(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnNode n && n.Node == p1Home);
    }

    [Fact]
    public void Sabotage_size_one_misses()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home)));

        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        StackDeck(g, s1);

        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 15);
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectSabotageTargetRequest)choice;
            req.Chosen = req.LegalTargets.First(t => t.Owner == p2);
        });

        Assert.Equal(0, g.Player(p1).Prestige);
        Assert.Contains(g.ShipPlacements, sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnNode n && n.Node == p1Home);
    }

    [Fact]
    public void No_overkill_score()
    {
        // 4 hits on a 2-ship fleet = 2 destroyed, 2 prestige.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home)));

        // Use c24 (3 bombs, transport target), but c24 only has 3 bombs. Let me just use 3 size-2 cards stacked. Then add another by using c45 (2 bombs). Use c9 for cruisers. Hmm.
        // For overkill testing use c24 with 3 bombs. 3 bombs x size-2 = 3 hits, but only 2 ships → 2 destroyed.
        var s2a = g.Deck.First(id => g.CardsById[id].Size == 2);
        var s2b = g.Deck.First(id => g.CardsById[id].Size == 2 && id != s2a);
        var s2c = g.Deck.First(id => g.CardsById[id].Size == 2 && id != s2a && id != s2b);
        StackDeck(g, s2a, s2b, s2c);

        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 24); // 3 bombs, transport
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectSabotageTargetRequest)choice;
            req.Chosen = req.LegalTargets.First(t => t.Owner == p2);
        });

        Assert.Equal(2, g.Player(p1).Prestige);
        Assert.Equal(0, g.ShipPlacements.Count(sp =>
            sp.Owner == p2 && sp.Location is ShipLocation.OnNode n && n.Node == p1Home));
    }

    [Fact]
    public void Cruiser_only_card_excludes_transport_targets()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home))); // transport

        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 9); // 3 bombs, cruiser only
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete); // no legal targets
    }

    [Fact]
    public void Sabotage_targets_must_be_in_controlled_card()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        // Place enemy transport on a node P1 doesn't patrol or occupy.
        var faraway = g.Map.Nodes.First(n =>
            !n.IsHome && n.Id != g.Map.SectorCoreNodeId);
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(faraway.Id)));

        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 15);
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Null(ctx.PendingChoice);
    }

    [Fact]
    public void All_sabotage_cards_have_params()
    {
        var cards = CardDataLoader.LoadAll();
        var sab = cards.Where(c => c.ActionType == CardActionType.Sabotage).ToList();
        var paramIds = SabotageRegistrations.ByCardId.Keys.ToHashSet();
        Assert.Empty(sab.Where(c => !paramIds.Contains(c.Id)));
    }
}
