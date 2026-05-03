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
//   dotnet run --project src/Impulse.Bench -- --fetch-public
//      (Pulls the latest logs from johnchampaign/impulse-game-logs and
//       analyzes the full public dataset.)
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
//   --fetch-public    Pull the latest logs from the canonical public dataset
//                     repo (johnchampaign/impulse-game-logs) into a local
//                     cache and analyze them. Subsequent runs do `git pull`
//                     so it's fast.
//   --repo OWNER/NAME Override the public-dataset repo (default
//                     "johnchampaign/impulse-game-logs").
//   --human-seat N    Which seat was the human player (default 1).
//   --csv FILE        Also dump per-decision CSV rows to FILE.

string? replayFile = null;
string? replayDir = null;
bool fetchPublic = false;
string publicRepo = "johnchampaign/impulse-game-logs";
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
        case "--fetch-public": fetchPublic = true; break;
        case "--repo": publicRepo = Next(); fetchPublic = true; break;
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
// ----- Optional: fetch / refresh the public log dataset -----
if (fetchPublic)
{
    try
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Impulse.Bench", "log-cache");
        Directory.CreateDirectory(cacheRoot);
        var slug = publicRepo.Replace('/', '_');
        var localRepo = Path.Combine(cacheRoot, slug);
        // Self-healing fetch: try `git pull` if the cache exists, but if
        // upstream history has been rewritten (e.g. test logs deleted), fall
        // back to a clean re-clone.
        int RunGit(string args, string workingDir = "")
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("git failed to start");
            proc.WaitForExit();
            return proc.ExitCode;
        }

        bool exists = Directory.Exists(Path.Combine(localRepo, ".git"));
        if (exists)
        {
            Console.WriteLine($"Refreshing public dataset cache: {localRepo}");
            // First try a regular pull. If it fails (diverged history,
            // shallow-clone trouble, etc.), nuke the cache and fresh-clone.
            int code = RunGit($"-C \"{localRepo}\" fetch --depth 1 origin main");
            if (code == 0) code = RunGit($"-C \"{localRepo}\" reset --hard origin/main");
            if (code != 0)
            {
                Console.WriteLine("  cache out of sync; re-cloning…");
                try { Directory.Delete(localRepo, recursive: true); } catch { }
                exists = false;
            }
        }
        if (!exists)
        {
            Console.WriteLine($"Cloning public dataset: https://github.com/{publicRepo} → {localRepo}");
            int code = RunGit($"clone --depth 1 https://github.com/{publicRepo}.git \"{localRepo}\"");
            if (code != 0)
            {
                Console.Error.WriteLine($"git clone failed (exit {code}).");
                return 3;
            }
        }
        var logsDir = Path.Combine(localRepo, "logs");
        if (!Directory.Exists(logsDir) || Directory.GetFiles(logsDir, "*.log").Length == 0)
        {
            Console.WriteLine("Public dataset is empty — no logs submitted yet.");
            Console.WriteLine("(Once players click SUBMIT LOGS in the app, files will land");
            Console.WriteLine($" at https://github.com/{publicRepo}/tree/main/logs)");
            return 0;
        }
        // Public dataset filenames are <sha256>.log, not impulse-game-*.log.
        // Build a sibling synthetic dir of impulse-game-*.log symlinks would
        // be overkill; instead, point the analyzer to the logs/ directly and
        // let it iterate everything it finds. The analyzer's file glob in
        // ReplayAnalyzer is path-based; we override below by rewriting
        // replayDir to the cache and stuffing its glob.
        replayDir = logsDir;
        Console.WriteLine($"Public dataset ready: {Directory.GetFiles(logsDir, "*.log").Length} log file(s)");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"--fetch-public failed: {ex.Message}");
        return 3;
    }
}

// ----- Replay-analyzer mode -----
if (replayFile is not null || replayDir is not null)
{
    var paths = new List<string>();
    if (replayFile is not null) paths.Add(replayFile);
    if (replayDir is not null)
    {
        // Public dataset stores logs/<sha>.log; local %TEMP% archives use
        // impulse-game-<timestamp>.log. Accept both patterns plus the
        // canonical impulse-last-game.log.
        paths.AddRange(Directory.GetFiles(replayDir, "*.log"));
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
