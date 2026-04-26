using Impulse.Core;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Tests;

public class ScoringTests
{
    private static GameState NewGame() =>
        SetupFactory.NewGame(
            new SetupOptions(2, Seed: 1, InitialTransportsAtHome: 0, InitialCruisersAtHomeGate: 0, AllNodesFaceUp: true),
            BuildRegistry());

    private static EffectRegistry BuildRegistry()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        return r;
    }

    [Fact]
    public void AddPrestige_records_win_at_threshold()
    {
        var g = NewGame();
        Scoring.AddPrestige(g, new PlayerId(1), 20, PrestigeSource.BattleWon, g.Log);
        Assert.True(g.IsGameOver);
        Assert.Equal(GamePhase.GameOver, g.Phase);
        Assert.Equal(20, g.Player(new PlayerId(1)).Prestige);
    }

    [Fact]
    public void AddPrestige_zero_or_negative_is_noop()
    {
        var g = NewGame();
        Scoring.AddPrestige(g, new PlayerId(1), 0, PrestigeSource.Refining, g.Log);
        Scoring.AddPrestige(g, new PlayerId(1), -3, PrestigeSource.Refining, g.Log);
        Assert.Equal(0, g.Player(new PlayerId(1)).Prestige);
        Assert.False(g.IsGameOver);
    }

    [Fact]
    public void Phase5_awards_for_cruisers_on_sector_core_gates()
    {
        var g = NewGame();
        var coreGates = g.Map.AdjacencyByNode[g.Map.SectorCoreNodeId].Take(2).ToList();
        var p1 = new PlayerId(1);
        foreach (var gate in coreGates)
            g.ShipPlacements.Add(new ShipPlacement(p1, new ShipLocation.OnGate(gate.Id)));

        Scoring.RunPhase5(g, p1, g.Log);
        Assert.Equal(2, g.Player(p1).Prestige);
    }

    [Fact]
    public void Phase5_does_not_score_transports_on_sector_core()
    {
        // Rulebook p.13: Phase 5 scores cruiser-fleet patrols only.
        // Transport activation is a per-action event (CommandHandler).
        var g = NewGame();
        var p1 = new PlayerId(1);
        g.ShipPlacements.Add(new ShipPlacement(p1, new ShipLocation.OnNode(g.Map.SectorCoreNodeId)));

        Scoring.RunPhase5(g, p1, g.Log);
        Assert.Equal(0, g.Player(p1).Prestige);
    }

    [Fact]
    public void Phase5_counts_each_gate_as_one_fleet_regardless_of_cruiser_count()
    {
        var g = NewGame();
        var coreGate = g.Map.AdjacencyByNode[g.Map.SectorCoreNodeId].First();
        var p1 = new PlayerId(1);
        // Two cruisers on the same gate = one fleet = one point.
        g.ShipPlacements.Add(new ShipPlacement(p1, new ShipLocation.OnGate(coreGate.Id)));
        g.ShipPlacements.Add(new ShipPlacement(p1, new ShipLocation.OnGate(coreGate.Id)));

        Scoring.RunPhase5(g, p1, g.Log);
        Assert.Equal(1, g.Player(p1).Prestige);
    }
}
