using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class ResearchTests
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
    public void Research_size_one_from_hand_replaces_basic_tech()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Player(p1).Hand.Add(s1);

        var handler = new ResearchHandler(BuildRegistry(), ResearchRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 2); // Research size-1 from hand

        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectHandCardRequest h: h.ChosenCardId = s1; break;
                case SelectTechSlotRequest ts: ts.Chosen = TechSlot.Left; break;
            }
        });

        // Card removed from hand, tech slot replaced.
        Assert.DoesNotContain(s1, g.Player(p1).Hand);
        Assert.IsType<Tech.Researched>(g.Player(p1).Techs.Left);
        Assert.Equal(s1, ((Tech.Researched)g.Player(p1).Techs.Left).CardId);
    }

    [Fact]
    public void Research_color_filter_excludes_other_colors()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blue = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        var red = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red);
        g.Deck.Remove(blue); g.Deck.Remove(red);
        g.Player(p1).Hand.Add(blue);
        g.Player(p1).Hand.Add(red);

        var handler = new ResearchHandler(BuildRegistry(), ResearchRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 74); // research [1][B] from hand

        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectHandCardRequest h:
                    Assert.Single(h.LegalCardIds);
                    Assert.Equal(blue, h.LegalCardIds[0]);
                    h.ChosenCardId = blue;
                    break;
                case SelectTechSlotRequest ts: ts.Chosen = TechSlot.Right; break;
            }
        });

        Assert.IsType<Tech.Researched>(g.Player(p1).Techs.Right);
    }

    [Fact]
    public void Research_replacing_researched_tech_discards_old_card()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var firstResearched = 100;
        // Pre-set Right slot to a Researched tech.
        g.Player(p1).Techs = new TechSlots(g.Player(p1).Techs.Left, new Tech.Researched(firstResearched));

        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Player(p1).Hand.Add(s1);

        var handler = new ResearchHandler(BuildRegistry(), ResearchRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 2);
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectHandCardRequest h: h.ChosenCardId = s1; break;
                case SelectTechSlotRequest ts: ts.Chosen = TechSlot.Right; break;
            }
        });

        Assert.Contains(firstResearched, g.Discard); // old researched tech sent to discard
        Assert.IsType<Tech.Researched>(g.Player(p1).Techs.Right);
        Assert.Equal(s1, ((Tech.Researched)g.Player(p1).Techs.Right).CardId);
    }

    [Fact]
    public void Research_from_deck_match_goes_to_slot_nonmatch_discards()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        // Stack so first draw is s2 (no match), second is s1 (match).
        g.Deck.Remove(s2); g.Deck.Remove(s1);
        g.Deck.Insert(0, s2);
        g.Deck.Insert(1, s1);

        var handler = new ResearchHandler(BuildRegistry(), ResearchRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 63); // size=1, count=2, deck

        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectTechSlotRequest ts: ts.Chosen = TechSlot.Left; break;
            }
        });

        Assert.Contains(s2, g.Discard);
        Assert.IsType<Tech.Researched>(g.Player(p1).Techs.Left);
        Assert.Equal(s1, ((Tech.Researched)g.Player(p1).Techs.Left).CardId);
    }

    [Fact]
    public void All_research_cards_have_params()
    {
        var cards = CardDataLoader.LoadAll();
        var research = cards.Where(c => c.ActionType == CardActionType.Research).ToList();
        var paramIds = ResearchRegistrations.ByCardId.Keys.ToHashSet();
        var missing = research.Where(c => !paramIds.Contains(c.Id)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void C18_then_execute_runs_the_researched_card()
    {
        // c18: "Research one size [1] card from your hand. Then Execute it."
        // Researching c1 (Draw 2 from deck) should research+execute it,
        // meaning the deck shrinks by 2 from the Draw effect.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var executeTarget = 1; // c1, size 1, Draw effect.
        g.Deck.Remove(executeTarget);
        g.Player(p1).Hand.Add(executeTarget);
        int deckBefore = g.Deck.Count;

        var handler = new ResearchHandler(BuildRegistry(), ResearchRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 18);
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectHandCardRequest h: h.ChosenCardId = executeTarget; break;
                case SelectTechSlotRequest ts: ts.Chosen = TechSlot.Left; break;
            }
        });

        // Research happened.
        Assert.IsType<Tech.Researched>(g.Player(p1).Techs.Left);
        Assert.Equal(executeTarget, ((Tech.Researched)g.Player(p1).Techs.Left).CardId);
        // Then Execute happened: deck drew 2.
        Assert.Equal(deckBefore - 2, g.Deck.Count);
    }
}
