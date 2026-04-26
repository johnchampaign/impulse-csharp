using Impulse.Core.Cards;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Fixture;

// Hand-built mid-game GameState for slice A's UI iteration. Deterministic
// (no RNG), four seats, every panel populated with something so the WPF
// shell has data to render in every region.
public static class FixtureBuilder
{
    public static GameState BuildFourPlayerMidGame()
    {
        var cards = CardDataLoader.LoadAll();
        var byId = cards.ToDictionary(c => c.Id);

        var seats = new[]
        {
            new PlayerId(1), new PlayerId(2), new PlayerId(3), new PlayerId(4),
        };
        var map = MapFactory.Build(seats);

        var races = new[] { Races.Piscesish, Races.Ariek, Races.Herculese, Races.Draconians };
        var players = new List<PlayerState>();
        for (int i = 0; i < seats.Length; i++)
        {
            var race = races[i];
            players.Add(new PlayerState
            {
                Id = seats[i],
                Race = race,
                Color = race.Color,
                Techs = new TechSlots(
                    Tech.BasicCommon.Instance,
                    new Tech.BasicUnique(race)),
                ShipsAvailable = 12 - 3,
                Prestige = i switch { 0 => 12, 1 => 7, 2 => 4, _ => 9 },
            });
        }

        // Deal a few cards to each hand and plan, deterministic by id.
        // Using small ids so panels show low numbers.
        AssignCards(players[0].Hand, byId, 1, 2, 4, 8, 11);
        AssignCards(players[0].Plan, byId, 23, 25);
        AssignCards(players[0].Minerals, byId, 7);

        AssignCards(players[1].Hand, byId, 3, 5, 12);
        AssignCards(players[1].Plan, byId, 14);

        AssignCards(players[2].Hand, byId, 6, 9, 15, 16);

        AssignCards(players[3].Hand, byId, 10, 17, 18);
        AssignCards(players[3].Plan, byId, 19, 20);

        var impulse = new List<int> { 22, 24, 26, 27 };
        var assigned = new HashSet<int>(
            players.SelectMany(p => p.Hand.Concat(p.Plan).Concat(p.Minerals))
                   .Concat(impulse));
        var deck = cards.Where(c => !assigned.Contains(c.Id)).Select(c => c.Id).ToList();

        // Place a few ships across the map so transports/cruisers show on screen.
        var ships = new List<ShipPlacement>();
        AddShips(ships, seats[0], map, transportsOnHomeAdjacent: 2, cruisersOnHomeGate: 1);
        AddShips(ships, seats[1], map, transportsOnHomeAdjacent: 1, cruisersOnHomeGate: 2);
        AddShips(ships, seats[2], map, transportsOnHomeAdjacent: 1, cruisersOnHomeGate: 1);
        AddShips(ships, seats[3], map, transportsOnHomeAdjacent: 2, cruisersOnHomeGate: 0);

        return new GameState
        {
            Map = map,
            CardsById = byId,
            Players = players,
            Deck = deck,
            Impulse = impulse,
            ImpulseCursor = 1,
            ShipPlacements = ships,
            CurrentTurn = 5,
            ActivePlayer = seats[0],
            Phase = GamePhase.AddImpulse,
        };
    }

    private static void AssignCards(List<int> dest, Dictionary<int, Card> byId, params int[] ids)
    {
        foreach (var id in ids)
            if (byId.ContainsKey(id))
                dest.Add(id);
    }

    private static void AddShips(
        List<ShipPlacement> ships, PlayerId owner, SectorMap map,
        int transportsOnHomeAdjacent, int cruisersOnHomeGate)
    {
        var home = map.HomeNodeIds[owner];
        var adjacencyGates = map.AdjacencyByNode[home].ToList();
        if (adjacencyGates.Count == 0) return;

        for (int i = 0; i < transportsOnHomeAdjacent && i < adjacencyGates.Count; i++)
        {
            var gate = adjacencyGates[i];
            var other = gate.EndpointA == home ? gate.EndpointB : gate.EndpointA;
            ships.Add(new ShipPlacement(owner, new ShipLocation.OnNode(other)));
        }
        for (int i = 0; i < cruisersOnHomeGate && i < adjacencyGates.Count; i++)
        {
            ships.Add(new ShipPlacement(owner, new ShipLocation.OnGate(adjacencyGates[i].Id)));
        }
    }
}
