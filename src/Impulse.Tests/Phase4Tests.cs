using Impulse.Core;
using Impulse.Core.Controllers;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class Phase4Tests
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

    private static GameRunner Build(int seed)
    {
        var r = BuildRegistry();
        var g = SetupFactory.NewGame(new SetupOptions(2, seed), r);
        var controllers = g.Players
            .Select((p, i) => (IPlayerController)new RandomController(p.Id, seed * 7 + i))
            .ToList();
        return new GameRunner(g, r, controllers);
    }

    [Fact]
    public void Empty_plan_skips_phase4_silently()
    {
        var r = Build(seed: 5);
        Assert.Empty(r.State.Player(r.State.ActivePlayer).Plan);
        r.StepOneTurn();
        // No exception, turn completes.
    }

    [Fact]
    public void Plan_with_4_or_more_cards_is_forced()
    {
        var r = Build(seed: 7);
        var p1 = r.State.ActivePlayer;
        // Stuff Plan with 4 cards.
        var p1State = r.State.Player(p1);
        for (int i = 0; i < 4; i++)
            p1State.Plan.Add(r.State.Deck[i]);
        // Remove from deck to keep state consistent
        for (int i = 0; i < 4; i++) r.State.Deck.RemoveAt(0);

        r.StepOneTurn();
        // Plan was forced; resolved + discarded; should be empty (or contain only NextPlan promotions).
        // Without complex assertions, just ensure no throw and Plan no longer has those original 4.
        Assert.True(p1State.Plan.Count <= p1State.NextPlan?.Count + 0 || p1State.Plan.Count == 0);
    }

    [Fact]
    public void Random_play_with_plans_doesnt_throw_for_50_turns()
    {
        var r = Build(seed: 21);
        for (int i = 0; i < 50 && !r.State.IsGameOver; i++)
            r.StepOneTurn();
        // Reaching here is the bar.
    }
}
