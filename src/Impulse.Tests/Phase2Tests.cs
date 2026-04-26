using Impulse.Core;
using Impulse.Core.Controllers;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class Phase2Tests
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

    [Fact]
    public void Random_play_with_phase2_doesnt_throw()
    {
        var r = BuildRegistry();
        var g = SetupFactory.NewGame(new SetupOptions(2, Seed: 99), r);
        var controllers = g.Players
            .Select((p, i) => (IPlayerController)new RandomController(p.Id, 99 * 7 + i))
            .ToList();
        var runner = new GameRunner(g, r, controllers);
        for (int i = 0; i < 50 && !g.IsGameOver; i++)
            runner.StepOneTurn();
    }
}
