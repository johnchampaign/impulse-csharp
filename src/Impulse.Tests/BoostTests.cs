using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class BoostTests
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
        ExecuteRegistrations.RegisterAll(r);
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

    [Fact]
    public void Boost_calculates_floor_div_two_per_color()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        // Plant 3 yellow size-1 minerals = 3 yellow gems.
        var yellows = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Yellow && g.CardsById[id].Size == 1)
            .Take(3).ToList();
        foreach (var id in yellows) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }

        Assert.Equal(1, Boost.FromMinerals(g, p1, CardColor.Yellow)); // 3/2 = 1
        Assert.Equal(0, Boost.FromMinerals(g, p1, CardColor.Red));    // none
    }

    [Fact]
    public void Boost_counts_card_size_as_gem_count()
    {
        // Two size-2 yellow cards = 4 gems → boost 2.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var yellows = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Yellow && g.CardsById[id].Size == 2)
            .Take(2).ToList();
        Assert.Equal(2, yellows.Count);
        foreach (var id in yellows) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }

        Assert.Equal(2, Boost.FromMinerals(g, p1, CardColor.Yellow));
    }

    [Fact]
    public void Command_fleet_size_boosted_by_yellow_minerals()
    {
        // c4 is Yellow "[1] ship fleet for one move". With 2 yellow gems → boost 1.
        // Player has 3 transports at home → max fleet = min(1+1=2, 3) = 2.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var home = g.Map.HomeNodeIds[p1];
        for (int i = 0; i < 3; i++)
            g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(home)));
        // 2 yellow gems
        var yellows = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Yellow && g.CardsById[id].Size == 1)
            .Take(2).ToList();
        foreach (var id in yellows) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }

        var handler = new CommandHandler(new EffectRegistry(), CommandRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 4);
        SelectFleetSizeRequest? sizeReq = null;
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFleetRequest f: f.Chosen = f.LegalLocations[0]; break;
                case SelectFleetSizeRequest fs: sizeReq = fs; fs.Chosen = fs.Min; break;
                case DeclareMoveRequest m: m.ChosenPath = m.LegalPaths[0]; break;
            }
        });
        Assert.NotNull(sizeReq);
        Assert.Equal(2, sizeReq!.Max);
    }

    [Fact]
    public void Mine_size_filter_uses_up_to_semantics_with_boost()
    {
        // c7 (Red): "Mine one size [1] card from your hand." With 2 red gems
        // → boost +1, size filter becomes ≤2. So a size-2 hand card is now eligible.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var size2NonRed = g.Deck.First(id => g.CardsById[id].Size == 2 && g.CardsById[id].Color != CardColor.Red);
        g.Deck.Remove(size2NonRed);
        g.Player(p1).Hand.Add(size2NonRed);
        // 2 red gems
        var reds = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Red && g.CardsById[id].Size == 1)
            .Take(2).ToList();
        foreach (var id in reds) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }

        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 7);
        SelectHandCardRequest? captured = null;
        Resolve(g, handler, ctx, choice =>
        {
            captured = (SelectHandCardRequest)choice;
            captured.ChosenCardId = size2NonRed;
        });
        Assert.NotNull(captured);
        Assert.Contains(size2NonRed, captured!.LegalCardIds); // size 2 became eligible with boost
        Assert.Contains(size2NonRed, g.Player(p1).Minerals);
    }

    [Fact]
    public void Sabotage_bombs_boosted_by_red_minerals()
    {
        // c15 (Red): "Sabotage a fleet with [1] bomb." With 2 red gems → 2 bombs.
        // Set up a target and stack the deck with two size-2 cards (both hits).
        var g = NewGame();
        var p1 = new PlayerId(1);
        var p2 = new PlayerId(2);
        var p1Home = g.Map.HomeNodeIds[p1];
        g.ShipPlacements.Add(new(p1, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home)));
        g.ShipPlacements.Add(new(p2, new ShipLocation.OnNode(p1Home)));

        // 2 red gems
        var reds = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Red && g.CardsById[id].Size == 1)
            .Take(2).ToList();
        foreach (var id in reds) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }

        // Stack deck with 2 size-2 cards (both hits).
        var hits = g.Deck.Where(id => g.CardsById[id].Size == 2).Take(2).ToList();
        foreach (var id in hits) g.Deck.Remove(id);
        for (int i = 0; i < hits.Count; i++) g.Deck.Insert(i, hits[i]);

        var handler = new SabotageHandler(SabotageRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 15);
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectSabotageTargetRequest)choice;
            req.Chosen = req.LegalTargets.First(t => t.Owner == p2);
        });
        // 2 bombs both hit, both ships destroyed → +2 prestige.
        Assert.Equal(2, g.Player(p1).Prestige);
    }

    [Fact]
    public void Refine_per_gem_rate_boosted()
    {
        // c46 is a GREEN card that refines YELLOW minerals: "Refine one [Y]
        // mineral card for [1] point per gem." Boost matches the card's
        // frame color (Green), not the target filter ([Y]).
        // 2 green size-1 minerals → 2 green gems → boost +1 → rate 2.
        // Refine a size-3 yellow → 3 * 2 = 6 prestige.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var twoGreens = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Green && g.CardsById[id].Size == 1)
            .Take(2).ToList();
        var size3Yellow = g.Deck.First(id =>
            g.CardsById[id].Color == CardColor.Yellow && g.CardsById[id].Size == 3);
        foreach (var id in twoGreens) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }
        g.Deck.Remove(size3Yellow);
        g.Player(p1).Minerals.Add(size3Yellow);

        var handler = new RefineHandler(RefineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 46);
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectMineralCardRequest)choice;
            req.ChosenCardId = size3Yellow;
        });
        Assert.Equal(6, g.Player(p1).Prestige);
    }

    [Fact]
    public void Build_count_boosted_by_minerals()
    {
        // c10 (Blue): "Build [1] Transport at home." With 2 blue gems → +1, builds 2.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blues = g.Deck
            .Where(id => g.CardsById[id].Color == CardColor.Blue && g.CardsById[id].Size == 1)
            .Take(2).ToList();
        foreach (var id in blues) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }

        // c10 build_n_transport_at_home — params: count=1, TransportOnly, Home.
        var handler = new BuildHandler(1, BuildShipFilter.TransportOnly, BuildLocationKind.Home);
        var ctx = new EffectContext
        {
            ActivatingPlayer = p1,
            Source = new EffectSource.ImpulseCard(10), // Blue card
        };
        int beforeAvail = g.Player(p1).ShipsAvailable;
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectShipPlacementRequest)choice;
            req.Chosen = req.LegalLocations[0];
        });
        Assert.Equal(beforeAvail - 2, g.Player(p1).ShipsAvailable); // 2 ships built
    }

    [Fact]
    public void No_minerals_means_no_boost()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        Assert.Equal(0, Boost.FromMinerals(g, p1, CardColor.Red));
        Assert.Equal(0, Boost.FromMinerals(g, p1, CardColor.Blue));
    }

    [Fact]
    public void Basic_tech_source_has_no_boost()
    {
        // BasicCommon/BasicUnique have no card color → no minerals can match.
        var g = NewGame();
        var p1 = new PlayerId(1);
        // Plant any minerals.
        var any = g.Deck.Take(3).ToList();
        foreach (var id in any) { g.Deck.Remove(id); g.Player(p1).Minerals.Add(id); }
        var techSource = new EffectSource.TechEffect(TechSlot.Left); // CardId null
        Assert.Equal(0, Boost.FromSource(g, p1, techSource));
    }
}
