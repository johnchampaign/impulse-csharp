using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class MineRefineTests
{
    private static EffectRegistry BuildRegistry()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        BuildRegistrations.RegisterAll(r);
        MineRegistrations.RegisterAll(r);
        RefineRegistrations.RegisterAll(r);
        return r;
    }

    private static GameState NewGame(int seed = 1) =>
        SetupFactory.NewGame(
            new SetupOptions(2, seed,
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
    public void Mine_size_one_from_hand_filters_by_size()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        // Find one size-1 and one size-2 card to plant in hand.
        var size1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        var size2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        g.Deck.Remove(size1); g.Deck.Remove(size2);
        g.Player(p1).Hand.Add(size1);
        g.Player(p1).Hand.Add(size2);

        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 7); // c7: size=1, hand
        SelectHandCardRequest? captured = null;
        Resolve(g, handler, ctx, choice =>
        {
            captured = (SelectHandCardRequest)choice;
            captured.ChosenCardId = captured.LegalCardIds[0];
        });
        Assert.NotNull(captured);
        Assert.Single(captured!.LegalCardIds);
        Assert.Equal(size1, captured.LegalCardIds[0]);
        Assert.Contains(size1, g.Player(p1).Minerals);
        Assert.DoesNotContain(size1, g.Player(p1).Hand);
    }

    [Fact]
    public void Mine_color_red_from_hand_filters_by_color()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var red = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red);
        var blue = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        g.Deck.Remove(red); g.Deck.Remove(blue);
        g.Player(p1).Hand.Add(red);
        g.Player(p1).Hand.Add(blue);

        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 23); // c23: red, hand
        SelectHandCardRequest? captured = null;
        Resolve(g, handler, ctx, choice =>
        {
            captured = (SelectHandCardRequest)choice;
            captured.ChosenCardId = captured.LegalCardIds[0];
        });
        Assert.Single(captured!.LegalCardIds);
        Assert.Equal(red, captured.LegalCardIds[0]);
    }

    [Fact]
    public void Mine_from_deck_keeps_top_card_when_matching()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        // Stack a size-1 on top so it's kept.
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(s1);
        g.Deck.Insert(0, s1);

        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 73); // c73: size=1, deck
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Single(g.Player(p1).Minerals);
        Assert.Equal(s1, g.Player(p1).Minerals[0]);
    }

    [Fact]
    public void Mine_count_two_from_deck_evaluates_both_top_cards()
    {
        // c61: draw 2 from top. Stack a size-1 + size-2: minerals get the size-1,
        // size-2 is discarded.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        g.Deck.Remove(s1); g.Deck.Remove(s2);
        g.Deck.Insert(0, s1);
        g.Deck.Insert(1, s2);

        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 61);
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Single(g.Player(p1).Minerals);
        Assert.Equal(s1, g.Player(p1).Minerals[0]);
        Assert.Contains(s2, g.Discard);
    }

    [Fact]
    public void Mine_no_eligible_in_hand_is_noop()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var size3 = g.Deck.First(id => g.CardsById[id].Size == 3);
        g.Deck.Remove(size3);
        g.Player(p1).Hand.Add(size3);
        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 7); // size=1
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Empty(g.Player(p1).Minerals);
    }

    [Fact]
    public void Refine_per_gem_awards_size_prestige()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var size3yellow = g.Deck.First(id => g.CardsById[id].Size == 3 && g.CardsById[id].Color == CardColor.Yellow);
        g.Deck.Remove(size3yellow);
        g.Player(p1).Minerals.Add(size3yellow);

        var handler = new RefineHandler(RefineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 46); // c46: yellow, per-gem
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectMineralCardRequest)choice;
            Assert.Single(req.LegalCardIds);
            req.ChosenCardId = req.LegalCardIds[0];
        });
        Assert.Equal(3, g.Player(p1).Prestige);
        Assert.Empty(g.Player(p1).Minerals);
        Assert.Contains(size3yellow, g.Discard);
    }

    [Fact]
    public void Refine_color_filter_excludes_other_colors()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blue = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        g.Deck.Remove(blue);
        g.Player(p1).Minerals.Add(blue);

        var handler = new RefineHandler(RefineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 46); // yellow only
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Equal(0, g.Player(p1).Prestige);
        Assert.Single(g.Player(p1).Minerals); // unchanged
    }

    [Fact]
    public void Refine_flat_mode_awards_fixed_points()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var size1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(size1);
        g.Player(p1).Minerals.Add(size1);

        var handler = new RefineHandler(RefineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 106); // flat 2
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectMineralCardRequest)choice;
            req.ChosenCardId = req.LegalCardIds[0];
        });
        Assert.Equal(2, g.Player(p1).Prestige); // not size-1
    }

    [Fact]
    public void Refine_can_trigger_win()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        g.Player(p1).Prestige = 18;
        var size3 = g.Deck.First(id => g.CardsById[id].Size == 3);
        g.Deck.Remove(size3);
        g.Player(p1).Minerals.Add(size3);

        var handler = new RefineHandler(RefineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 43); // any color, per-gem
        Resolve(g, handler, ctx, choice =>
        {
            var req = (SelectMineralCardRequest)choice;
            req.ChosenCardId = req.LegalCardIds[0];
        });
        Assert.True(g.IsGameOver);
        Assert.Equal(GamePhase.GameOver, g.Phase);
    }

    [Fact]
    public void Cancel_choice_restarts_effect()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var size1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        g.Deck.Remove(size1);
        g.Player(p1).Hand.Add(size1);

        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 7);

        // First call: handler pauses with prompt
        handler.Execute(g, ctx);
        Assert.True(ctx.Paused);
        Assert.NotNull(ctx.PendingChoice);

        // User cancels
        ctx.PendingChoice!.Cancelled = true;
        // Engine should reset state (mimicking RunEffectToCompletion behavior)
        ctx.PendingChoice = null;
        ctx.HandlerState = null;
        ctx.Paused = false;

        // Second call after reset: handler should re-prompt (HandlerState was null)
        handler.Execute(g, ctx);
        Assert.True(ctx.Paused);
        Assert.NotNull(ctx.PendingChoice);
        Assert.Equal(0, g.Player(p1).Minerals.Count); // nothing committed
    }
}
