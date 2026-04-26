using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class BasicUniqueTechTests
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
        return r;
    }

    private static GameState NewGame() =>
        SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1,
                InitialTransportsAtHome: 0,
                InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true,
                InitialHandSize: 0),
            BuildRegistry());

    private static EffectContext Ctx(PlayerId pid) => new()
    {
        ActivatingPlayer = pid,
        Source = new EffectSource.TechEffect(TechSlot.Right),
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
    public void Piscesish_drew_size_one_goes_to_hand()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Deck.Insert(0, s1);

        var handler = new PiscesishTechHandler();
        handler.Execute(g, Ctx(p1));
        Assert.Contains(s1, g.Player(p1).Hand);
    }

    [Fact]
    public void Piscesish_drew_size_two_discards()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        g.Deck.Remove(s2);
        g.Deck.Insert(0, s2);

        var handler = new PiscesishTechHandler();
        handler.Execute(g, Ctx(p1));
        Assert.Contains(s2, g.Discard);
        Assert.DoesNotContain(s2, g.Player(p1).Hand);
    }

    [Fact]
    public void Draconians_filters_to_hand_cards_matching_impulse_anchor()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var anchor = g.CardsById.Values.First(c => c.Color == CardColor.Yellow && c.Size == 2);
        g.Deck.Remove(anchor.Id);
        g.Impulse.Add(anchor.Id);

        var matching = g.CardsById.Values.First(c =>
            c.Id != anchor.Id && c.Color == CardColor.Yellow && c.Size == 2);
        var nonMatching = g.CardsById.Values.First(c => c.Color != CardColor.Yellow);
        g.Deck.Remove(matching.Id);
        g.Deck.Remove(nonMatching.Id);
        g.Player(p1).Hand.Add(matching.Id);
        g.Player(p1).Hand.Add(nonMatching.Id);

        var handler = new DraconiansTechHandler();
        var ctx = Ctx(p1);
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectHandCardRequest h:
                    Assert.Single(h.LegalCardIds);
                    Assert.Equal(matching.Id, h.LegalCardIds[0]);
                    h.ChosenCardId = matching.Id;
                    break;
                case SelectTechSlotRequest ts:
                    ts.Chosen = TechSlot.Left;
                    break;
            }
        });

        Assert.IsType<Tech.Researched>(g.Player(p1).Techs.Left);
        Assert.Equal(matching.Id, ((Tech.Researched)g.Player(p1).Techs.Left).CardId);
    }

    [Fact]
    public void Caelumnites_mines_only_matching_hand_card()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var anchor = g.CardsById.Values.First(c => c.Color == CardColor.Red && c.Size == 1);
        g.Deck.Remove(anchor.Id);
        g.Impulse.Add(anchor.Id);

        var matching = g.CardsById.Values.First(c =>
            c.Id != anchor.Id && c.Color == CardColor.Red && c.Size == 1);
        var nonMatching = g.CardsById.Values.First(c => c.Color != CardColor.Red);
        g.Deck.Remove(matching.Id);
        g.Deck.Remove(nonMatching.Id);
        g.Player(p1).Hand.Add(matching.Id);
        g.Player(p1).Hand.Add(nonMatching.Id);

        var handler = new CaelumnitesTechHandler();
        var ctx = Ctx(p1);
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectHandCardRequest)choice;
            Assert.Single(req.LegalCardIds);
            req.ChosenCardId = matching.Id;
        });

        Assert.Contains(matching.Id, g.Player(p1).Minerals);
        Assert.Contains(nonMatching.Id, g.Player(p1).Hand);
    }

    [Fact]
    public void Empty_impulse_makes_anchor_techs_noop()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        Assert.Empty(g.Impulse);

        var ctxD = Ctx(p1);
        new DraconiansTechHandler().Execute(g, ctxD);
        Assert.True(ctxD.IsComplete);

        var ctxC = Ctx(p1);
        new CaelumnitesTechHandler().Execute(g, ctxC);
        Assert.True(ctxC.IsComplete);
    }

    [Fact]
    public void Researched_tech_with_card_id_dispatches_correctly()
    {
        // Verify the EffectSource.TechEffect.CardId fix: a Researched mine
        // tech, used in Phase 2, looks up the right per-card params.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Player(p1).Hand.Add(s1);
        // Pretend the player researched #7 (Mine size-1 from hand).
        g.Player(p1).Techs = new TechSlots(g.Player(p1).Techs.Left, new Tech.Researched(7));

        var registry = BuildRegistry();
        var card = g.CardsById[7];
        var handler = registry.Resolve(card.EffectFamily)!;
        var ctx = new EffectContext
        {
            ActivatingPlayer = p1,
            Source = new EffectSource.TechEffect(TechSlot.Right, 7),
        };
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectHandCardRequest)choice;
            req.ChosenCardId = s1;
        });

        Assert.Contains(s1, g.Player(p1).Minerals);
    }
}
