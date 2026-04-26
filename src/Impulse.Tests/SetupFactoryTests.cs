using Impulse.Core;
using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Map;

namespace Impulse.Tests;

public class SetupFactoryTests
{
    private static EffectRegistry CommandOnly()
    {
        var r = new EffectRegistry();
        CommandRegistrations.RegisterAll(r);
        return r;
    }

    [Fact]
    public void Builds_four_player_game_in_AddImpulse_phase()
    {
        var g = SetupFactory.NewGame(new SetupOptions(4, Seed: 42), CommandOnly());
        Assert.Equal(GamePhase.AddImpulse, g.Phase);
        Assert.Equal(4, g.Players.Count);
        Assert.Equal(new(1), g.ActivePlayer);
    }

    [Fact]
    public void Initial_hands_filled_to_size()
    {
        var g = SetupFactory.NewGame(new SetupOptions(4, Seed: 1, InitialHandSize: 5), CommandOnly());
        Assert.All(g.Players, p => Assert.Equal(5, p.Hand.Count));
    }

    [Fact]
    public void Deck_filtered_to_command_only()
    {
        var g = SetupFactory.NewGame(new SetupOptions(2, Seed: 1), CommandOnly());
        var seen = g.Deck
            .Concat(g.Players.SelectMany(p => p.Hand))
            .Select(id => g.CardsById[id]);
        Assert.All(seen, c => Assert.StartsWith("command_", c.EffectFamily));
    }

    [Fact]
    public void Initial_ships_at_homes_and_pool_decremented()
    {
        var opts = new SetupOptions(3, Seed: 1, InitialTransportsAtHome: 3, InitialCruisersAtHomeGate: 1);
        var g = SetupFactory.NewGame(opts, CommandOnly());
        foreach (var p in g.Players)
        {
            var home = g.Map.HomeNodeIds[p.Id];
            var transports = g.ShipPlacements.Count(sp =>
                sp.Owner == p.Id && sp.Location is ShipLocation.OnNode on && on.Node == home);
            var cruisers = g.ShipPlacements.Count(sp =>
                sp.Owner == p.Id && sp.Location is ShipLocation.OnGate);
            Assert.Equal(3, transports);
            Assert.Equal(1, cruisers);
            Assert.Equal(12 - 4, p.ShipsAvailable);
        }
    }

    [Fact]
    public void Same_seed_yields_identical_decks()
    {
        var a = SetupFactory.NewGame(new SetupOptions(4, Seed: 7), CommandOnly());
        var b = SetupFactory.NewGame(new SetupOptions(4, Seed: 7), CommandOnly());
        Assert.Equal(a.Deck, b.Deck);
    }
}
