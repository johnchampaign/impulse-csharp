using Impulse.Core.Controllers;
using Impulse.Core.Effects;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Self-play tournament harness. Runs N games headlessly between AI
// policies and aggregates outcomes — the foundation for evaluating any
// AI improvement (better heuristics, lookahead, MCTS, etc.) without
// having to play games by hand. Pure in-memory; no UI, no log files.
public static class HeadlessTournament
{
    public sealed record SeatResult(PlayerId Id, AiPolicy Policy, int Prestige, bool IsWinner);

    public sealed record GameResult(
        int Seed,
        int TurnsPlayed,
        bool ReachedThreshold,
        IReadOnlyList<SeatResult> Seats);

    public sealed record Summary(
        int Games,
        int PlayerCount,
        IReadOnlyDictionary<AiPolicy, int> Wins,
        IReadOnlyDictionary<AiPolicy, double> AveragePrestige,
        IReadOnlyDictionary<AiPolicy, int> Appearances)
    {
        public override string ToString()
        {
            var lines = new List<string>
            {
                $"Tournament: {Games} games, {PlayerCount} players each",
                "Policy           Wins  WinRate  AvgPrestige  Games",
                "----------------------------------------------",
            };
            foreach (var policy in Wins.Keys.OrderByDescending(p => Wins[p]))
            {
                int wins = Wins[policy];
                int games = Appearances[policy];
                double avg = AveragePrestige[policy];
                double rate = games == 0 ? 0 : (double)wins / games;
                lines.Add($"{policy,-15} {wins,5} {rate,7:P1}  {avg,11:F1}  {games,5}");
            }
            return string.Join("\n", lines);
        }
    }

    // Run `games` headless games. Each game's seat assignment is one of:
    //  - A fixed `seatPolicies` list (length must equal playerCount).
    //  - Random sampling from `policyPool` if seatPolicies is null.
    public static Summary Run(
        int games,
        int playerCount,
        IReadOnlyList<AiPolicy>? seatPolicies = null,
        IReadOnlyList<AiPolicy>? policyPool = null,
        int baseSeed = 12345,
        int maxTurns = 200)
    {
        if (seatPolicies is not null && seatPolicies.Count != playerCount)
            throw new ArgumentException($"seatPolicies length {seatPolicies.Count} != playerCount {playerCount}");
        var pool = policyPool ?? Enum.GetValues<AiPolicy>();
        var sampler = new Random(baseSeed ^ 0x5A5A5A5A);

        var wins = Enum.GetValues<AiPolicy>().ToDictionary(p => p, _ => 0);
        var prestigeSum = Enum.GetValues<AiPolicy>().ToDictionary(p => p, _ => 0L);
        var appearances = Enum.GetValues<AiPolicy>().ToDictionary(p => p, _ => 0);
        var results = new List<GameResult>();

        for (int g = 0; g < games; g++)
        {
            int seed = baseSeed + g;
            // Build seat policies for this game.
            var policies = seatPolicies ?? Enumerable.Range(0, playerCount)
                .Select(_ => pool[sampler.Next(pool.Count)])
                .ToArray();

            var result = RunOne(seed, playerCount, policies, maxTurns);
            results.Add(result);
            foreach (var seat in result.Seats)
            {
                appearances[seat.Policy]++;
                prestigeSum[seat.Policy] += seat.Prestige;
                if (seat.IsWinner) wins[seat.Policy]++;
            }
        }

        var avgPrestige = prestigeSum.ToDictionary(
            kv => kv.Key,
            kv => appearances[kv.Key] == 0 ? 0.0 : (double)kv.Value / appearances[kv.Key]);
        return new Summary(games, playerCount, wins, avgPrestige, appearances);
    }

    // Run a single game from a fresh setup, all seats AI, return the result.
    public static GameResult RunOne(int seed, int playerCount, IReadOnlyList<AiPolicy> seatPolicies, int maxTurns = 200)
    {
        var registry = new EffectRegistry();
        CommandRegistrations.RegisterAll(registry);
        BuildRegistrations.RegisterAll(registry);
        MineRegistrations.RegisterAll(registry);
        RefineRegistrations.RegisterAll(registry);
        DrawRegistrations.RegisterAll(registry);
        TradeRegistrations.RegisterAll(registry);
        PlanRegistrations.RegisterAll(registry);
        ResearchRegistrations.RegisterAll(registry);
        SabotageRegistrations.RegisterAll(registry);
        ExecuteRegistrations.RegisterAll(registry);

        var g = SetupFactory.NewGame(new SetupOptions(playerCount, seed), registry);
        // Suppress the log to keep tournament runs cheap and silent.
        g.Log.Suppressed = true;

        var controllers = new List<IPlayerController>();
        for (int i = 0; i < playerCount; i++)
        {
            var seatId = g.Players[i].Id;
            // Per-seat sub-seed so each policy's RNG is reproducible.
            controllers.Add(new PolicyController(seatId, seed: seed * 31 + i, policy: seatPolicies[i]));
        }
        var runner = new GameRunner(g, registry, controllers);
        runner.RunUntilDone(maxTurns: maxTurns);

        int maxPrestige = g.Players.Max(p => p.Prestige);
        bool reached = g.IsGameOver && maxPrestige >= Scoring.WinThreshold;
        var seats = g.Players
            .Select(p => new SeatResult(
                p.Id,
                seatPolicies[p.Id.Value - 1],
                p.Prestige,
                p.Prestige == maxPrestige))
            .ToList();
        return new GameResult(seed, g.CurrentTurn, reached, seats);
    }
}
