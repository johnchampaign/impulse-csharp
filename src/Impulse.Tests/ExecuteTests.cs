using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class ExecuteTests
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
    public void Execute_hand_card_runs_its_effect_and_discards_it()
    {
        // c89: Execute one size [1] card from your hand. Pick c1 (Draw, size 1):
        // verify the Draw effect runs (deck shrinks by 2) and c1 ends up in discard.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var registry = BuildRegistry();
        var executeTarget = 1; // c1: size 1, Draw 1 + 1 of R/Y.
        g.Deck.Remove(executeTarget);
        g.Player(p1).Hand.Add(executeTarget);
        int deckBefore = g.Deck.Count;

        var handler = registry.Resolve("execute_size_n_from_hand_or_tech")!;
        var ctx = Ctx(p1, sourceCardId: 89);
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFromOptionsRequest opt: opt.Chosen = 0; break;
                case SelectHandCardRequest hand: hand.ChosenCardId = hand.LegalCardIds[0]; break;
            }
        });

        Assert.True(ctx.IsComplete);
        Assert.Contains(executeTarget, g.Discard);
        Assert.DoesNotContain(executeTarget, g.Player(p1).Hand);
        Assert.Equal(deckBefore - 2, g.Deck.Count);
    }

    [Fact]
    public void Execute_deck_size_match_runs_and_discards()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var registry = BuildRegistry();
        // c82: Execute one size [1] card from the deck.
        // Pick a size-1 card we know what it is. Use c5 (Draw 2) — size 3 not 1, won't work.
        // Actually we need size-1 card whose effect can run without further input.
        // c10 is "Build [1] Transport at home" size 1. It needs a placement choice.
        // c5 (Draw 2 cards from the deck) is size 3, not 1.
        // c1 (Draw one card... + one of [R] or [Y]) is size 1.
        // Stack the deck so top is c1.
        g.Deck.Remove(1);
        g.Deck.Insert(0, 1);

        var handler = registry.Resolve("execute_size_n_from_deck_or_tech")!;
        var ctx = Ctx(p1, sourceCardId: 82);
        int handBefore = g.Player(p1).Hand.Count;
        Resolve(g, handler, ctx, choice =>
        {
            if (choice is SelectFromOptionsRequest opt) opt.Chosen = 0;
        });
        Assert.True(ctx.IsComplete);
        Assert.Contains(1, g.Discard);                       // executed card discarded
        Assert.True(g.Player(p1).Hand.Count > handBefore);   // Draw effect added to hand
    }

    [Fact]
    public void Execute_deck_nonmatch_discards_without_running()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var registry = BuildRegistry();
        // c82 wants size 1; stack a size-2 card on top.
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        g.Deck.Remove(s2);
        g.Deck.Insert(0, s2);

        var handler = registry.Resolve("execute_size_n_from_deck_or_tech")!;
        var ctx = Ctx(p1, sourceCardId: 82);
        int handBefore = g.Player(p1).Hand.Count;
        Resolve(g, handler, ctx, choice =>
        {
            if (choice is SelectFromOptionsRequest opt) opt.Chosen = 0;
        });
        Assert.True(ctx.IsComplete);
        Assert.Contains(s2, g.Discard);
        Assert.Equal(handBefore, g.Player(p1).Hand.Count);   // no effect ran
    }

    [Fact]
    public void Execute_a_tech_runs_that_techs_effect()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var registry = BuildRegistry();
        // Set Right slot to a Researched #7 (Mine size-1 from hand).
        g.Player(p1).Techs = new TechSlots(g.Player(p1).Techs.Left, new Tech.Researched(7));
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Player(p1).Hand.Add(s1);

        var handler = registry.Resolve("execute_size_n_from_hand_or_tech")!;
        var ctx = Ctx(p1, sourceCardId: 89);
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFromOptionsRequest opt:
                    opt.Chosen = 1; // tech path
                    break;
                case SelectTechSlotRequest ts:
                    ts.Chosen = TechSlot.Right;
                    break;
                case SelectHandCardRequest hand:
                    hand.ChosenCardId = s1; // Mine this card
                    break;
            }
        });
        Assert.True(ctx.IsComplete);
        Assert.Contains(s1, g.Player(p1).Minerals);
    }

    [Fact]
    public void Execute_pause_resume_through_sub_handler_works()
    {
        // Mine sub-handler prompts for a hand pick. Engine pauses; controller
        // answers via outer ctx; sub resumes via mirrored reference. Verify
        // outer Execute doesn't lose state across the round-trip.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var registry = BuildRegistry();
        var executeTarget = 23; // Mine [1][R] from your hand
        var redCard = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red && id != executeTarget);
        var size1Other = g.Deck.First(id => g.CardsById[id].Size == 1 && id != executeTarget && id != redCard);
        g.Deck.Remove(executeTarget);
        g.Deck.Remove(redCard);
        g.Deck.Remove(size1Other);
        g.Player(p1).Hand.Add(executeTarget);
        g.Player(p1).Hand.Add(redCard);
        g.Player(p1).Hand.Add(size1Other);

        var handler = registry.Resolve("execute_size_n_from_hand_or_tech")!;
        // c89 wants size 1; c23's size is 1 so OK.
        Assert.Equal(1, g.CardsById[executeTarget].Size);
        var ctx = Ctx(p1, sourceCardId: 89);
        Resolve(g, handler, ctx, choice =>
        {
            switch (choice)
            {
                case SelectFromOptionsRequest opt: opt.Chosen = 0; break;
                case SelectHandCardRequest hand when hand.LegalCardIds.Contains(executeTarget):
                    hand.ChosenCardId = executeTarget; break;
                case SelectHandCardRequest hand:
                    // Mine sub-handler asks for a red card
                    hand.ChosenCardId = hand.LegalCardIds.First(id => g.CardsById[id].Color == CardColor.Red);
                    break;
            }
        });
        Assert.Contains(redCard, g.Player(p1).Minerals);
        Assert.Contains(executeTarget, g.Discard);
    }

    [Fact]
    public void All_execute_cards_have_params()
    {
        var cards = CardDataLoader.LoadAll();
        var execs = cards.Where(c => c.ActionType == CardActionType.Execute).ToList();
        var paramIds = ExecuteRegistrations.ByCardId.Keys.ToHashSet();
        Assert.Empty(execs.Where(c => !paramIds.Contains(c.Id)));
    }
}
