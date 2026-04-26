using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

public enum PrestigeSource
{
    SectorCoreGatesPatrolled,
    SectorCoreActivatedByTransports,
    TradedCardIcons,
    Refining,
    BattleWon,
    ShipsDestroyed,
}

public static class Scoring
{
    public const int WinThreshold = 20;

    public static void AddPrestige(GameState g, PlayerId pid, int amount, PrestigeSource source, GameLog log)
    {
        if (amount <= 0) return;
        var p = g.Player(pid);
        p.Prestige += amount;
        log.Write($"+{amount} prestige ({source}) → {pid} total {p.Prestige}");
        if (!g.IsGameOver && p.Prestige >= WinThreshold)
        {
            g.IsGameOver = true;
            g.Phase = GamePhase.GameOver;
            log.Write($"=== {pid} wins (prestige {p.Prestige}) ===");
        }
    }

    // Phase 5 (rulebook p.12): "Score 1 point for each of your fleets that
    // patrols the Sector Core." A fleet = same-owner ships at one location;
    // multiple cruisers on the same gate count as one fleet. Transport
    // activation is a per-action event, not a Phase-5 source.
    public static void RunPhase5(GameState g, PlayerId pid, GameLog log)
    {
        var coreId = g.Map.SectorCoreNodeId;
        var coreGates = g.Map.AdjacencyByNode[coreId].Select(gate => gate.Id).ToHashSet();

        int patrolledFleets = g.ShipPlacements
            .Where(sp => sp.Owner == pid &&
                         sp.Location is ShipLocation.OnGate og &&
                         coreGates.Contains(og.Gate))
            .Select(sp => ((ShipLocation.OnGate)sp.Location).Gate)
            .Distinct()
            .Count();
        if (patrolledFleets > 0)
            AddPrestige(g, pid, patrolledFleets, PrestigeSource.SectorCoreGatesPatrolled, log);
    }
}
