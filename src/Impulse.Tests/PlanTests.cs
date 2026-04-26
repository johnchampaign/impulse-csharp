using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class PlanTests
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
    public void Plan_color_from_hand_filters_by_color()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blue = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        var red = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red);
        g.Deck.Remove(blue); g.Deck.Remove(red);
        g.Player(p1).Hand.Add(blue);
        g.Player(p1).Hand.Add(red);

        var handler = new PlanHandler(PlanRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 8); // Plan [2][B]

        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectHandCardRequest)choice;
            // Only blue should be legal.
            Assert.Single(req.LegalCardIds);
            Assert.Equal(blue, req.LegalCardIds[0]);
            req.ChosenCardId = blue;
        });

        Assert.Contains(blue, g.Player(p1).Plan);
        Assert.Contains(red, g.Player(p1).Hand); // unchanged
    }

    [Fact]
    public void Plan_different_colors_excludes_used_colors()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blue1 = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        var blue2 = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue && id != blue1);
        var red = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red);
        g.Deck.Remove(blue1); g.Deck.Remove(blue2); g.Deck.Remove(red);
        g.Player(p1).Hand.Add(blue1);
        g.Player(p1).Hand.Add(blue2);
        g.Player(p1).Hand.Add(red);

        var handler = new PlanHandler(PlanRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 34); // different colors
        int picks = 0;
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectHandCardRequest)choice;
            if (picks == 0)
            {
                req.ChosenCardId = blue1;
            }
            else if (picks == 1)
            {
                Assert.DoesNotContain(blue2, req.LegalCardIds); // blue used
                Assert.Contains(red, req.LegalCardIds);
                req.ChosenCardId = red;
            }
            picks++;
        });

        Assert.Equal(2, g.Player(p1).Plan.Count);
        Assert.Contains(blue1, g.Player(p1).Plan);
        Assert.Contains(red, g.Player(p1).Plan);
    }

    [Fact]
    public void Plan_size_from_deck_top_match_goes_to_plan_nonmatch_discards()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        StackDeck(g, s1, s2);

        var handler = new PlanHandler(PlanRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 49); // Plan two size-1 from deck
        handler.Execute(g, ctx);

        Assert.Contains(s1, g.Player(p1).Plan);
        Assert.Contains(s2, g.Discard);
    }

    [Fact]
    public void Plan_card_then_color_anchor_first_then_filter()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var anchor = g.Deck.First(id => g.CardsById[id].Color == CardColor.Yellow);
        var matching = g.Deck.First(id => g.CardsById[id].Color == CardColor.Yellow && id != anchor);
        var nonMatching = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red);
        StackDeck(g, anchor, matching, nonMatching);

        var handler = new PlanHandler(PlanRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 51);
        handler.Execute(g, ctx);

        Assert.Contains(anchor, g.Player(p1).Plan);
        Assert.Contains(matching, g.Player(p1).Plan);
        Assert.Contains(nonMatching, g.Discard);
    }

    [Fact]
    public void Plan_decline_stops_immediately()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blue = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        g.Deck.Remove(blue);
        g.Player(p1).Hand.Add(blue);

        var handler = new PlanHandler(PlanRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 8);
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectHandCardRequest)choice;
            Assert.True(req.AllowNone);
            req.ChosenCardId = null;
        });
        Assert.Contains(blue, g.Player(p1).Hand);
        Assert.Empty(g.Player(p1).Plan);
    }

    [Fact]
    public void Plan_during_resolution_redirects_to_NextPlan()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        // Plant a card to plan during resolution; Plan card will trigger AddCardToPlan.
        var target = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        g.Deck.Remove(target);
        g.Player(p1).Hand.Add(target);

        g.IsResolvingPlan = true;
        Mechanics.AddCardToPlan(g, p1, target, g.Log);
        g.IsResolvingPlan = false;

        Assert.NotNull(g.Player(p1).NextPlan);
        Assert.Contains(target, g.Player(p1).NextPlan!);
        Assert.DoesNotContain(target, g.Player(p1).Plan);
    }

    [Fact]
    public void All_plan_cards_have_params()
    {
        var cards = CardDataLoader.LoadAll();
        var plans = cards.Where(c => c.ActionType == CardActionType.Plan).ToList();
        var paramIds = PlanRegistrations.ByCardId.Keys.ToHashSet();
        Assert.Empty(plans.Where(c => !paramIds.Contains(c.Id)));
    }
}
