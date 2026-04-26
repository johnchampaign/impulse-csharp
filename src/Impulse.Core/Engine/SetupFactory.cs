using Impulse.Core.Cards;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

public sealed record SetupOptions(
    int PlayerCount,
    int Seed,
    int InitialHandSize = 5,
    // Rulebook p.4: "2 ships on the card standing up (Transports), and 1
    // ship on its side on the gate facing the Sector Core (a Cruiser)."
    int InitialTransportsAtHome = 2,
    int InitialCruisersAtHomeGate = 1,
    // Tests that aren't exercising exploration set this to true to skip the
    // face-down deal so movement doesn't trigger exploration prompts.
    bool AllNodesFaceUp = false);

public static class SetupFactory
{
    public static GameState NewGame(SetupOptions opts, EffectRegistry registry)
    {
        if (opts.PlayerCount < 2 || opts.PlayerCount > 6)
            throw new ArgumentOutOfRangeException(nameof(opts.PlayerCount));

        var rng = new Random(opts.Seed);
        var seats = Enumerable.Range(1, opts.PlayerCount).Select(i => new PlayerId(i)).ToList();
        var map = MapFactory.Build(seats);

        var allCards = CardDataLoader.LoadAll();
        var deckCards = Allowlist.Filter(allCards, registry);
        var byId = allCards.ToDictionary(c => c.Id);

        // Shuffle deck
        var deck = deckCards.Select(c => c.Id).ToList();
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        // Players — shuffle the 6-race pool so each game's seat→race
        // assignment is random but deterministic for the seed.
        var racePool = Races.All.ToList();
        for (int i = racePool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (racePool[i], racePool[j]) = (racePool[j], racePool[i]);
        }
        var players = new List<PlayerState>();
        for (int i = 0; i < seats.Count; i++)
        {
            var race = racePool[i];
            players.Add(new PlayerState
            {
                Id = seats[i],
                Race = race,
                Color = race.Color,
                Techs = new TechSlots(
                    Tech.BasicCommon.Instance,
                    new Tech.BasicUnique(race)),
                ShipsAvailable = 12,
            });
        }

        var g = new GameState
        {
            Map = map,
            CardsById = byId,
            Players = players,
            Deck = deck,
            ActivePlayer = seats[0],
            Phase = GamePhase.Setup,
            Rng = rng,
        };

        // Deal initial hands
        foreach (var p in players)
            for (int i = 0; i < opts.InitialHandSize && g.Deck.Count > 0; i++)
            {
                p.Hand.Add(g.Deck[0]);
                g.Deck.RemoveAt(0);
            }

        // Populate per-node card state. Sector Core is special (no card),
        // home corners are face-up, all others are face-down with a card
        // from the deck.
        foreach (var node in map.Nodes)
        {
            if (node.IsSectorCore)
            {
                g.NodeCards[node.Id] = NodeCardState.SectorCore.Instance;
                continue;
            }
            if (g.Deck.Count == 0) continue;
            int cardId = g.Deck[0];
            g.Deck.RemoveAt(0);
            g.NodeCards[node.Id] = (node.IsHome || opts.AllNodesFaceUp)
                ? new NodeCardState.FaceUp(cardId)
                : new NodeCardState.FaceDown(cardId);
        }

        // Place initial ships at homes per rulebook p.4. The starting cruiser
        // goes on the gate "facing the Sector Core" — i.e. the gate from the
        // home node whose other endpoint sits closest (axial-distance) to
        // the Sector Core.
        var coreNode = map.Node(map.SectorCoreNodeId);
        foreach (var p in players)
        {
            var home = map.HomeNodeIds[p.Id];
            var homeGates = map.AdjacencyByNode[home].ToList();
            for (int i = 0; i < opts.InitialTransportsAtHome; i++)
            {
                g.ShipPlacements.Add(new ShipPlacement(p.Id, new ShipLocation.OnNode(home)));
                p.ShipsAvailable--;
            }
            // Sort gates by axial-distance of the OTHER endpoint to the core.
            var coreFacing = homeGates
                .Select(gate =>
                {
                    var otherId = gate.EndpointA == home ? gate.EndpointB : gate.EndpointA;
                    var other = map.Node(otherId);
                    int dq = other.AxialQ - coreNode.AxialQ;
                    int dr = other.AxialR - coreNode.AxialR;
                    int dist = (Math.Abs(dq) + Math.Abs(dq + dr) + Math.Abs(dr)) / 2;
                    return (gate, dist);
                })
                .OrderBy(t => t.dist)
                .Select(t => t.gate)
                .ToList();
            for (int i = 0; i < opts.InitialCruisersAtHomeGate && i < coreFacing.Count; i++)
            {
                g.ShipPlacements.Add(new ShipPlacement(p.Id, new ShipLocation.OnGate(coreFacing[i].Id)));
                p.ShipsAvailable--;
            }
        }

        g.Phase = GamePhase.AddImpulse;
        g.Log.Write($"=== game start: {opts.PlayerCount} players, seed {opts.Seed}, deck {g.Deck.Count} ===");
        return g;
    }
}
