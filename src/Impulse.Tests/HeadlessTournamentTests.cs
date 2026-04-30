using Impulse.Core.Controllers;
using Impulse.Core.Engine;

namespace Impulse.Tests;

public class HeadlessTournamentTests
{
    [Fact]
    public void Smoke_one_game_finishes()
    {
        var result = HeadlessTournament.RunOne(
            seed: 1,
            playerCount: 4,
            seatPolicies: new[] { AiPolicy.Greedy, AiPolicy.Warrior, AiPolicy.CoreRush, AiPolicy.Refine });
        Assert.Equal(4, result.Seats.Count);
        // At least one seat should have non-trivial prestige after a 200-turn game.
        Assert.True(result.Seats.Any(s => s.Prestige > 0));
    }

    [Fact]
    public void Clone_preserves_state_for_simulation()
    {
        // Cloning then advancing the clone must not mutate the original.
        var registry = new EffectRegistry();
        Impulse.Core.Effects.CommandRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.BuildRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.MineRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.RefineRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.DrawRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.TradeRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.PlanRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.ResearchRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.SabotageRegistrations.RegisterAll(registry);
        Impulse.Core.Effects.ExecuteRegistrations.RegisterAll(registry);

        var g = SetupFactory.NewGame(new SetupOptions(2, Seed: 42, AllNodesFaceUp: true), registry);
        var origPrestige = g.Players.Sum(p => p.Prestige);
        var origDeckCount = g.Deck.Count;
        var origPlayerCount = g.Players.Count;

        var clone = g.Clone();

        // Mutate the clone — original should be untouched.
        clone.Players[0].Prestige = 99;
        clone.Players[0].Hand.Clear();
        clone.Deck.Clear();
        clone.ShipPlacements.Clear();
        clone.Discard.Add(99);

        Assert.Equal(origPrestige, g.Players.Sum(p => p.Prestige));
        Assert.Equal(origDeckCount, g.Deck.Count);
        Assert.Equal(origPlayerCount, g.Players.Count);
        Assert.NotEqual(clone.Players[0].Prestige, g.Players[0].Prestige);
        Assert.NotSame(clone.Deck, g.Deck);
        Assert.NotSame(clone.Players, g.Players);
    }

    // Baseline win-rate matrix. Tests run the tournament with a small
    // sample so CI stays fast (<10s); the user can manually crank `games`
    // higher when comparing AI changes.
    [Fact]
    public void Baseline_all_policies_4p_smoke()
    {
        var summary = HeadlessTournament.Run(
            games: 8,
            playerCount: 4,
            policyPool: Enum.GetValues<AiPolicy>(),
            baseSeed: 1000,
            maxTurns: 150);

        // Every policy that appeared should have a recorded prestige and
        // game count; smoke check on shape.
        Assert.Equal(8, summary.Games);
        Assert.True(summary.Wins.Values.Sum() > 0, "at least one game decided a winner");
    }

    // For larger benchmarks, use the dedicated console project:
    //   dotnet run --project src/Impulse.Bench
    //   dotnet run --project src/Impulse.Bench -- --games 500
    //   dotnet run --project src/Impulse.Bench -- --policies CoreRush,Greedy,Greedy,Greedy
}
