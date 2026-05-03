using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Impulse.Core.Controllers;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Bench;

// Replay analyzer: takes a played game's log file and, for each turn-start
// state where the human seat was active, asks every AI policy what IT would
// have placed on the impulse track. Disagreements between human and AI are
// the data we use to find heuristic gaps.
//
// V1 scope: PlaceImpulse decisions only (Phase 1 of each of the human's
// turns). Higher-fidelity analysis (mid-effect choices like fleet picks or
// path declarations) requires reconstructing intermediate states, which is
// a future extension.
public static class ReplayAnalyzer
{
    public static readonly AiPolicy[] AnalyzedPolicies = new[]
    {
        AiPolicy.Greedy, AiPolicy.CoreRush, AiPolicy.Warrior,
        AiPolicy.Munchkin, AiPolicy.Refine, AiPolicy.Lookahead,
    };

    public sealed record DecisionPoint(
        int Turn,
        int HumanSeat,
        int HumanCardId,
        IReadOnlyList<int> LegalCardIds,
        IReadOnlyDictionary<AiPolicy, int> AiChoices)
    {
        public bool AllAiAgreeWithHuman => AiChoices.Values.All(v => v == HumanCardId);
        public IEnumerable<AiPolicy> DisagreeingPolicies =>
            AiChoices.Where(kv => kv.Value != HumanCardId).Select(kv => kv.Key);
    }

    public sealed record GameAnalysis(
        string LogPath,
        int HumanSeat,
        int PlayerCount,
        IReadOnlyList<DecisionPoint> Decisions,
        IReadOnlyDictionary<int, int> FinalPrestige,
        bool HumanWon,
        int Winner)
    {
        public int HumanPrestige => FinalPrestige.TryGetValue(HumanSeat, out var v) ? v : 0;
    }

    public static GameAnalysis Analyze(string logPath, int humanSeat = 1)
    {
        var lines = File.ReadAllLines(logPath);
        var registry = BuildRegistry();
        var decisions = new List<DecisionPoint>();
        string? lastStateCodec = null;
        int playerCount = 0;

        var stateRx = new Regex(@"^\[state turn=(\d+) active=(\d+)\] (\S+)$");
        var placeRx = new Regex(@"^P(\d+) places #(\d+)");

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var sm = stateRx.Match(line);
            if (!sm.Success) continue;

            int turn = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
            int active = int.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture);
            string codec = sm.Groups[3].Value;
            lastStateCodec = codec;

            if (active != humanSeat) continue;

            // Find the next "P{humanSeat} places #X" line within a small window.
            int placedCard = -1;
            for (int j = i + 1; j < Math.Min(i + 15, lines.Length); j++)
            {
                var pm = placeRx.Match(lines[j]);
                if (pm.Success && int.Parse(pm.Groups[1].Value) == humanSeat)
                {
                    placedCard = int.Parse(pm.Groups[2].Value);
                    break;
                }
            }
            if (placedCard < 0) continue;

            // Decode the snapshot and rebuild the game.
            GameSnapshot snap;
            try { snap = GameSnapshot.DecodeFromString(codec); }
            catch { continue; } // unparseable; skip

            playerCount = snap.PlayerCount;
            var g = SetupFactory.NewGame(
                new SetupOptions(snap.PlayerCount, snap.Seed), registry);
            g.Log.Suppressed = true;
            try { snap.RestoreInto(g); }
            catch { continue; } // race-shuffle mismatch or similar; skip

            var humanPlayer = g.Player(new PlayerId(humanSeat));
            if (!humanPlayer.Hand.Contains(placedCard)) continue; // sanity

            var legal = humanPlayer.Hand
                .Select(id => (PlayerAction)new PlayerAction.PlaceImpulse(id))
                .ToList();
            var aiChoices = new Dictionary<AiPolicy, int>();
            foreach (var policy in AnalyzedPolicies)
            {
                var pc = HeadlessTournament.MakeController(
                    new PlayerId(humanSeat), seed: 42 + turn, policy);
                var action = pc.PickAction(g, legal);
                if (action is PlayerAction.PlaceImpulse pi)
                    aiChoices[policy] = pi.CardIdFromHand;
            }

            decisions.Add(new DecisionPoint(
                Turn: turn,
                HumanSeat: humanSeat,
                HumanCardId: placedCard,
                LegalCardIds: humanPlayer.Hand.ToList(),
                AiChoices: aiChoices));
        }

        // Final prestige: decode the last state codec we saw.
        var finalPrestige = new Dictionary<int, int>();
        int winner = 0;
        bool humanWon = false;
        if (lastStateCodec is not null)
        {
            try
            {
                var finalSnap = GameSnapshot.DecodeFromString(lastStateCodec);
                int max = -1;
                foreach (var p in finalSnap.Players)
                {
                    finalPrestige[p.Id] = p.Prestige;
                    if (p.Prestige > max) { max = p.Prestige; winner = p.Id; }
                }
                humanWon = finalPrestige.TryGetValue(humanSeat, out var hp) && hp == max;
            }
            catch { /* leave defaults */ }
        }

        return new GameAnalysis(
            LogPath: logPath,
            HumanSeat: humanSeat,
            PlayerCount: playerCount,
            Decisions: decisions,
            FinalPrestige: finalPrestige,
            HumanWon: humanWon,
            Winner: winner);
    }

    public static string FormatHumanReadable(GameAnalysis analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Replay: {Path.GetFileName(analysis.LogPath)}");
        sb.AppendLine($"  Players: {analysis.PlayerCount}, human seat: P{analysis.HumanSeat}");
        if (analysis.FinalPrestige.Count > 0)
        {
            sb.Append($"  Final prestige: ");
            sb.AppendLine(string.Join(", ",
                analysis.FinalPrestige
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"P{kv.Key}={kv.Value}")));
            sb.AppendLine($"  Winner: P{analysis.Winner} ({(analysis.HumanWon ? "HUMAN WON" : "human lost")})");
        }
        sb.AppendLine($"  PlaceImpulse decisions analyzed: {analysis.Decisions.Count}");
        sb.AppendLine();

        // Per-policy agreement counts.
        sb.AppendLine($"  Agreement (human-vs-AI on PlaceImpulse):");
        foreach (var policy in AnalyzedPolicies)
        {
            int agree = analysis.Decisions
                .Count(d => d.AiChoices.TryGetValue(policy, out var v) && v == d.HumanCardId);
            int total = analysis.Decisions.Count(d => d.AiChoices.ContainsKey(policy));
            double rate = total == 0 ? 0 : 100.0 * agree / total;
            sb.AppendLine($"    {policy,-12} {agree,3}/{total,3} ({rate,5:F1}%)");
        }
        sb.AppendLine();

        // Disagreement detail.
        var divergent = analysis.Decisions.Where(d => !d.AllAiAgreeWithHuman).ToList();
        sb.AppendLine($"  Divergent decisions ({divergent.Count}):");
        foreach (var d in divergent)
        {
            sb.AppendLine($"    Turn {d.Turn}: human played #{d.HumanCardId} (legal: [{string.Join(",", d.LegalCardIds)}])");
            foreach (var (policy, choice) in d.AiChoices.OrderBy(kv => kv.Key.ToString()))
            {
                if (choice == d.HumanCardId) continue;
                sb.AppendLine($"      ✗ {policy} would play #{choice}");
            }
        }
        return sb.ToString();
    }

    public static string FormatCsv(GameAnalysis analysis)
    {
        var sb = new StringBuilder();
        sb.Append("turn,human_seat,human_card,legal_cards,human_won");
        foreach (var p in AnalyzedPolicies) sb.Append($",{p}_choice,{p}_agrees");
        sb.AppendLine();
        foreach (var d in analysis.Decisions)
        {
            sb.Append($"{d.Turn},{d.HumanSeat},{d.HumanCardId},");
            sb.Append(string.Join(';', d.LegalCardIds));
            sb.Append($",{(analysis.HumanWon ? 1 : 0)}");
            foreach (var p in AnalyzedPolicies)
            {
                int choice = d.AiChoices.TryGetValue(p, out var v) ? v : -1;
                sb.Append($",{choice},{(choice == d.HumanCardId ? 1 : 0)}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Aggregate analyzer across many log files (e.g. all of %TEMP%/impulse-game-*.log).
    public sealed record AggregateReport(
        int Games,
        int HumanWins,
        IReadOnlyDictionary<AiPolicy, int> AgreementsInWins,
        IReadOnlyDictionary<AiPolicy, int> DecisionsInWins,
        IReadOnlyDictionary<AiPolicy, int> AgreementsInLosses,
        IReadOnlyDictionary<AiPolicy, int> DecisionsInLosses);

    public static AggregateReport Aggregate(IEnumerable<GameAnalysis> games)
    {
        var agreeWin = AnalyzedPolicies.ToDictionary(p => p, _ => 0);
        var totalWin = AnalyzedPolicies.ToDictionary(p => p, _ => 0);
        var agreeLoss = AnalyzedPolicies.ToDictionary(p => p, _ => 0);
        var totalLoss = AnalyzedPolicies.ToDictionary(p => p, _ => 0);
        int gameCount = 0, winCount = 0;
        foreach (var g in games)
        {
            gameCount++;
            if (g.HumanWon) winCount++;
            foreach (var d in g.Decisions)
            {
                foreach (var policy in AnalyzedPolicies)
                {
                    if (!d.AiChoices.TryGetValue(policy, out var c)) continue;
                    var totals = g.HumanWon ? totalWin : totalLoss;
                    var agrees = g.HumanWon ? agreeWin : agreeLoss;
                    totals[policy]++;
                    if (c == d.HumanCardId) agrees[policy]++;
                }
            }
        }
        return new AggregateReport(
            Games: gameCount,
            HumanWins: winCount,
            AgreementsInWins: agreeWin,
            DecisionsInWins: totalWin,
            AgreementsInLosses: agreeLoss,
            DecisionsInLosses: totalLoss);
    }

    public static string FormatAggregate(AggregateReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Aggregate over {r.Games} games ({r.HumanWins} human wins, {r.Games - r.HumanWins} losses):");
        sb.AppendLine();
        sb.AppendLine($"  Policy        | Agree-in-WIN  | Agree-in-LOSS | Δ (win - loss)");
        sb.AppendLine($"  ------------- | ------------- | ------------- | --------------");
        foreach (var policy in AnalyzedPolicies)
        {
            int aw = r.AgreementsInWins[policy], tw = r.DecisionsInWins[policy];
            int al = r.AgreementsInLosses[policy], tl = r.DecisionsInLosses[policy];
            double winRate = tw == 0 ? 0 : 100.0 * aw / tw;
            double lossRate = tl == 0 ? 0 : 100.0 * al / tl;
            sb.AppendLine($"  {policy,-13} | {aw,3}/{tw,3} ({winRate,5:F1}%) | {al,3}/{tl,3} ({lossRate,5:F1}%) | {winRate - lossRate,+6:F1}%");
        }
        sb.AppendLine();
        sb.AppendLine($"Read: positive Δ means the policy agrees with the human MORE in wins than in losses —");
        sb.AppendLine($"i.e. the policy's PlaceImpulse heuristic aligns with what works. Negative Δ is a signal");
        sb.AppendLine($"that the policy disagrees with winning play more than with losing play.");
        return sb.ToString();
    }

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
}
