using Impulse.Bench;
using Impulse.Core.Controllers;
using Impulse.Core.Engine;

// Headless AI tournament + replay analyzer. Pure CLI; no UI.
//
// MODE 1 — tournament (default):
//   dotnet run --project src/Impulse.Bench
//   dotnet run --project src/Impulse.Bench -- --games 500
//   dotnet run --project src/Impulse.Bench -- --policies CoreRush,Greedy,Greedy,Greedy
//
// MODE 2 — replay-analyzer (analyze a played game's log):
//   dotnet run --project src/Impulse.Bench -- --replay <path-to-log>
//   dotnet run --project src/Impulse.Bench -- --replay-dir <dir>
//   dotnet run --project src/Impulse.Bench -- --replay-dir %TEMP%
//
// Tournament flags:
//   --games N         Number of games to run (default 200).
//   --players N       Seats per game, 2..6 (default 4).
//   --seed N          Base seed; per-game seed = baseSeed + i (default 9999).
//   --max-turns N     Cutoff to abandon stuck games (default 200).
//   --policies LIST   Fixed comma-separated policy assignment to seats.
//
// Replay flags:
//   --replay FILE     Analyze one log file.
//   --replay-dir DIR  Analyze every impulse-game-*.log + impulse-last-game.log
//                     in DIR (default: %TEMP%). Aggregates across all games.
//   --human-seat N    Which seat was the human player (default 1).
//   --csv FILE        Also dump per-decision CSV rows to FILE.

string? replayFile = null;
string? replayDir = null;
int humanSeat = 1;
string? csvOutPath = null;

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
        case "--replay": replayFile = Next(); break;
        case "--replay-dir": replayDir = Next(); break;
        case "--human-seat": humanSeat = int.Parse(Next()); break;
        case "--csv": csvOutPath = Next(); break;
        case "-h":
        case "--help":
            Console.WriteLine("See top of Program.cs for usage.");
            return 0;
        default:
            Console.Error.WriteLine($"unknown flag: {a}");
            return 2;
    }
}
// ----- Replay-analyzer mode -----
if (replayFile is not null || replayDir is not null)
{
    var paths = new List<string>();
    if (replayFile is not null) paths.Add(replayFile);
    if (replayDir is not null)
    {
        // impulse-game-<timestamp>.log + impulse-last-game.log
        paths.AddRange(Directory.GetFiles(replayDir, "impulse-game-*.log"));
        var last = Path.Combine(replayDir, "impulse-last-game.log");
        if (File.Exists(last)) paths.Add(last);
    }
    paths = paths.Distinct().OrderBy(p => p).ToList();

    if (paths.Count == 0)
    {
        Console.Error.WriteLine("No log files found.");
        return 2;
    }

    var analyses = new List<ReplayAnalyzer.GameAnalysis>();
    var csv = csvOutPath is not null ? new System.Text.StringBuilder() : null;
    bool csvHeader = false;
    foreach (var p in paths)
    {
        ReplayAnalyzer.GameAnalysis a;
        try { a = ReplayAnalyzer.Analyze(p, humanSeat); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ! failed to analyze {Path.GetFileName(p)}: {ex.Message}");
            continue;
        }
        analyses.Add(a);
        Console.WriteLine(ReplayAnalyzer.FormatHumanReadable(a));
        if (csv is not null)
        {
            var rows = ReplayAnalyzer.FormatCsv(a);
            if (!csvHeader) { csv.Append(rows); csvHeader = true; }
            else
            {
                // Skip header line on subsequent games.
                using var rdr = new StringReader(rows);
                rdr.ReadLine(); // header
                string? line;
                while ((line = rdr.ReadLine()) is not null) csv.AppendLine(line);
            }
        }
    }

    if (analyses.Count > 1)
    {
        var agg = ReplayAnalyzer.Aggregate(analyses);
        Console.WriteLine();
        Console.WriteLine(ReplayAnalyzer.FormatAggregate(agg));
    }

    if (csv is not null && csvOutPath is not null)
    {
        File.WriteAllText(csvOutPath, csv.ToString());
        Console.WriteLine($"CSV written: {csvOutPath}");
    }

    return 0;
}

// ----- Tournament mode -----
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
