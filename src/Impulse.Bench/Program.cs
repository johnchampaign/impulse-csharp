using Impulse.Core.Controllers;
using Impulse.Core.Engine;

// Headless AI tournament. Pure CLI; no log files, no UI.
//
// Usage:
//   dotnet run --project src/Impulse.Bench
//   dotnet run --project src/Impulse.Bench -- --games 500
//   dotnet run --project src/Impulse.Bench -- --games 200 --players 4 --seed 9999
//   dotnet run --project src/Impulse.Bench -- --policies CoreRush,Greedy,Greedy,Greedy
//
// Flags:
//   --games N         Number of games to run (default 200).
//   --players N       Seats per game, 2..6 (default 4).
//   --seed N          Base seed; per-game seed = baseSeed + i (default 9999).
//   --max-turns N     Cutoff to abandon stuck games (default 200).
//   --policies LIST   Fixed comma-separated policy assignment to seats.
//                     Length must equal --players. Examples:
//                       Greedy,Greedy,Greedy,Greedy   (homogeneous)
//                       CoreRush,Warrior,Munchkin,Refine
//                     If omitted, each seat is sampled randomly from all
//                     five policies for each game.

int games = 200;
int playerCount = 4;
int baseSeed = 9999;
int maxTurns = 200;
IReadOnlyList<AiPolicy>? seatPolicies = null;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i]
        : throw new ArgumentException($"missing value after {a}");
    switch (a)
    {
        case "--games": games = int.Parse(Next()); break;
        case "--players": playerCount = int.Parse(Next()); break;
        case "--seed": baseSeed = int.Parse(Next()); break;
        case "--max-turns": maxTurns = int.Parse(Next()); break;
        case "--policies":
            seatPolicies = Next().Split(',')
                .Select(p => Enum.Parse<AiPolicy>(p.Trim(), ignoreCase: true))
                .ToArray();
            break;
        case "-h":
        case "--help":
            Console.WriteLine("See top of Program.cs for usage.");
            return 0;
        default:
            Console.Error.WriteLine($"unknown flag: {a}");
            return 2;
    }
}
if (seatPolicies is not null && seatPolicies.Count != playerCount)
{
    Console.Error.WriteLine(
        $"--policies length {seatPolicies.Count} doesn't match --players {playerCount}");
    return 2;
}

Console.WriteLine($"Running {games} games, {playerCount} players, base seed {baseSeed}, max {maxTurns} turns…");
var sw = System.Diagnostics.Stopwatch.StartNew();
var summary = HeadlessTournament.Run(
    games: games,
    playerCount: playerCount,
    seatPolicies: seatPolicies,
    policyPool: seatPolicies is null ? Enum.GetValues<AiPolicy>() : null,
    baseSeed: baseSeed,
    maxTurns: maxTurns);
sw.Stop();

Console.WriteLine();
Console.WriteLine(summary);
Console.WriteLine();
Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s ({games / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} games/s)");
return 0;
