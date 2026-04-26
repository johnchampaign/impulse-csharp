using Impulse.Core;
using Impulse.Core.Fixture;

namespace Impulse.Tests;

public class FixtureBuilderTests
{
    [Fact]
    public void Builds_four_player_mid_game_without_throwing()
    {
        var g = FixtureBuilder.BuildFourPlayerMidGame();
        Assert.Equal(4, g.Players.Count);
        Assert.Equal(GamePhase.AddImpulse, g.Phase);
        Assert.True(g.Impulse.Count > 0);
        Assert.True(g.ShipPlacements.Count > 0);
        Assert.True(g.Deck.Count > 0);
    }

    [Fact]
    public void Card_zones_are_disjoint()
    {
        var g = FixtureBuilder.BuildFourPlayerMidGame();
        var seen = new HashSet<int>();
        foreach (var p in g.Players)
        {
            foreach (var id in p.Hand) Assert.True(seen.Add(id), $"duplicate {id} in hand");
            foreach (var id in p.Plan) Assert.True(seen.Add(id), $"duplicate {id} in plan");
            foreach (var id in p.Minerals) Assert.True(seen.Add(id), $"duplicate {id} in minerals");
        }
        foreach (var id in g.Impulse) Assert.True(seen.Add(id), $"duplicate {id} in impulse");
        foreach (var id in g.Deck) Assert.True(seen.Add(id), $"duplicate {id} in deck");
        Assert.Equal(108, seen.Count);
    }
}
