using Impulse.Core;
using Impulse.Core.Cards;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class DrawTradeTests
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

    // Stack the deck so the next N draws are exactly the given card ids.
    private static void StackDeck(GameState g, params int[] ids)
    {
        foreach (var id in ids) g.Deck.Remove(id);
        for (int i = 0; i < ids.Length; i++) g.Deck.Insert(i, ids[i]);
    }

    [Fact]
    public void Draw_two_unfiltered_keeps_both()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var top1 = g.Deck[0];
        var top2 = g.Deck[1];
        var handler = new DrawHandler(DrawRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 5); // Draw 2
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Contains(top1, g.Player(p1).Hand);
        Assert.Contains(top2, g.Player(p1).Hand);
    }

    [Fact]
    public void Draw_three_size_one_keeps_only_size_one()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        // Stack deck with size-2, size-1, size-3.
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1 && id != s2);
        var s3 = g.Deck.First(id => g.CardsById[id].Size == 3 && id != s1 && id != s2);
        StackDeck(g, s2, s1, s3);

        var handler = new DrawHandler(DrawRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 66); // Draw 3 size-one
        handler.Execute(g, ctx);
        Assert.Single(g.Player(p1).Hand);
        Assert.Equal(s1, g.Player(p1).Hand[0]);
        Assert.Contains(s2, g.Discard);
        Assert.Contains(s3, g.Discard);
    }

    [Fact]
    public void Trade_size_three_keeps_only_size_three()
    {
        // c83: Trade [1] size three from your hand. With no size-3 in hand,
        // legal list is empty and the effect completes without prompting.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        StackDeck(g, s1);
        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 83);
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Equal(0, g.Player(p1).Prestige);
    }

    [Fact]
    public void Two_step_draw_keeps_first_unconditionally_filters_second()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        // c1: Draw 1 unfiltered, then Draw 1 of R or Y.
        var anyCard = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue); // top1: blue, kept (no filter)
        var nonMatching = g.Deck.First(id => g.CardsById[id].Color == CardColor.Green && id != anyCard); // top2: green, discarded
        StackDeck(g, anyCard, nonMatching);

        var handler = new DrawHandler(DrawRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 1); // Draw 1 + 1 of R/Y
        handler.Execute(g, ctx);
        Assert.Contains(anyCard, g.Player(p1).Hand);
        Assert.Contains(nonMatching, g.Discard);
    }

    [Fact]
    public void Two_step_draw_keeps_matching_color_in_second()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var top1 = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        var matching = g.Deck.First(id => g.CardsById[id].Color == CardColor.Yellow && id != top1);
        StackDeck(g, top1, matching);

        var handler = new DrawHandler(DrawRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 1); // step 2 filter = R or Y
        handler.Execute(g, ctx);
        Assert.Contains(top1, g.Player(p1).Hand);
        Assert.Contains(matching, g.Player(p1).Hand);
    }

    [Fact]
    public void Trade_from_hand_color_filter_excludes_other_colors()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var blue = g.Deck.First(id => g.CardsById[id].Color == CardColor.Blue);
        g.Deck.Remove(blue);
        g.Player(p1).Hand.Add(blue);

        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 11); // Trade [1][R] from hand
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Equal(0, g.Player(p1).Prestige);
        Assert.Contains(blue, g.Player(p1).Hand); // unchanged
    }

    [Fact]
    public void Trade_from_hand_red_card_scores_size_prestige()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var red3 = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red && g.CardsById[id].Size == 3);
        g.Deck.Remove(red3);
        g.Player(p1).Hand.Add(red3);

        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 11); // Trade [1][R] from hand
        SelectHandCardRequest? captured = null;
        while (!ctx.IsComplete)
        {
            ctx.Paused = false;
            handler.Execute(g, ctx);
            if (ctx.IsComplete) break;
            captured = (SelectHandCardRequest)ctx.PendingChoice!;
            captured.ChosenCardId = red3;
        }
        Assert.NotNull(captured);
        Assert.True(captured!.AllowNone);
        Assert.Equal(3, g.Player(p1).Prestige);
        Assert.Contains(red3, g.Discard);
        Assert.DoesNotContain(red3, g.Player(p1).Hand);
    }

    [Fact]
    public void Trade_from_hand_decline_stops_immediately()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var red = g.Deck.First(id => g.CardsById[id].Color == CardColor.Red);
        g.Deck.Remove(red);
        g.Player(p1).Hand.Add(red);

        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 11);
        while (!ctx.IsComplete)
        {
            ctx.Paused = false;
            handler.Execute(g, ctx);
            if (ctx.IsComplete) break;
            var req = (SelectHandCardRequest)ctx.PendingChoice!;
            req.ChosenCardId = null; // decline
        }
        Assert.Equal(0, g.Player(p1).Prestige);
        Assert.Contains(red, g.Player(p1).Hand);
    }

    [Fact]
    public void Trade_two_size_one_from_hand_scores_two()
    {
        // c68: up to 2 size-1 cards from hand, each = 1 point.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1a = g.Deck.First(id => g.CardsById[id].Size == 1);
        var s1b = g.Deck.First(id => g.CardsById[id].Size == 1 && id != s1a);
        g.Deck.Remove(s1a); g.Deck.Remove(s1b);
        g.Player(p1).Hand.Add(s1a);
        g.Player(p1).Hand.Add(s1b);

        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 68);
        while (!ctx.IsComplete)
        {
            ctx.Paused = false;
            handler.Execute(g, ctx);
            if (ctx.IsComplete) break;
            var req = (SelectHandCardRequest)ctx.PendingChoice!;
            req.ChosenCardId = req.LegalCardIds[0];
        }
        Assert.Equal(2, g.Player(p1).Prestige);
        Assert.Empty(g.Player(p1).Hand);
    }

    [Fact]
    public void Trade_from_deck_match_scores_nonmatch_just_discards()
    {
        // c95: Trade [1] size one card from the deck.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        StackDeck(g, s2);
        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 95);
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Equal(0, g.Player(p1).Prestige);
        Assert.Contains(s2, g.Discard);
    }

    [Fact]
    public void Trade_from_deck_match_scores_size_one()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        StackDeck(g, s1);
        var handler = new TradeHandler(TradeRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 95);
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Equal(1, g.Player(p1).Prestige);
        Assert.Contains(s1, g.Discard);
    }

    [Fact]
    public void Mine_from_deck_top_card_match_goes_to_minerals()
    {
        // Verifies the corrected from-deck rule: draw top, match → minerals.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        StackDeck(g, s1);
        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 73); // size=1, deck
        handler.Execute(g, ctx);
        Assert.Contains(s1, g.Player(p1).Minerals);
    }

    [Fact]
    public void Mine_from_deck_nonmatching_top_discards_no_search()
    {
        // Verifies: draw top, non-match → discard. No deeper search.
        var g = NewGame();
        var p1 = new PlayerId(1);
        var s2 = g.Deck.First(id => g.CardsById[id].Size == 2);
        var s1 = g.Deck.First(id => g.CardsById[id].Size == 1);
        // Stack s2 (non-match) then s1 (match). Old impl would skip s2 to find s1;
        // new impl draws s2 first and discards.
        StackDeck(g, s2, s1);
        var handler = new MineHandler(MineRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 73); // size=1 from deck, count=1
        handler.Execute(g, ctx);
        Assert.Empty(g.Player(p1).Minerals);
        Assert.Contains(s2, g.Discard);
        Assert.Contains(s1, g.Deck); // untouched
    }

    [Fact]
    public void Empty_deck_aborts_gracefully()
    {
        var g = NewGame();
        var p1 = new PlayerId(1);
        g.Deck.Clear();
        var handler = new DrawHandler(DrawRegistrations.ByCardId);
        var ctx = Ctx(p1, sourceCardId: 5);
        handler.Execute(g, ctx);
        Assert.True(ctx.IsComplete);
        Assert.Empty(g.Player(p1).Hand);
    }

    [Fact]
    public void All_draw_and_trade_cards_have_params()
    {
        var cards = CardDataLoader.LoadAll();
        var draws = cards.Where(c => c.ActionType == CardActionType.Draw).ToList();
        var trades = cards.Where(c => c.ActionType == CardActionType.Trade).ToList();
        var drawIds = DrawRegistrations.ByCardId.Keys.ToHashSet();
        var tradeIds = TradeRegistrations.ByCardId.Keys.ToHashSet();
        Assert.Empty(draws.Where(c => !drawIds.Contains(c.Id)));
        Assert.Empty(trades.Where(c => !tradeIds.Contains(c.Id)));
    }
}
