using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Centralized mutators. Rules code goes through here, never touches lists directly.
public static class Mechanics
{
    public const int HandLimit = 10;

    // Rulebook p.12 line 246: shuffle discard into deck when deck empty.
    // Returns true if at least one card is available to draw after refilling.
    public static bool EnsureDeckCanDraw(GameState g, GameLog log)
    {
        if (g.Deck.Count > 0) return true;
        if (g.Discard.Count == 0) return false;
        RefillDeckFromDiscard(g, log);
        return g.Deck.Count > 0;
    }

    public static void RefillDeckFromDiscard(GameState g, GameLog log)
    {
        if (g.Discard.Count == 0) return;
        log.Write($"  ↻ deck empty — shuffling {g.Discard.Count} discard(s) into deck");
        var ids = g.Discard.ToList();
        // Fisher-Yates with the seeded RNG so replays stay deterministic.
        for (int i = ids.Count - 1; i > 0; i--)
        {
            int j = g.Rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        g.Discard.Clear();
        g.Deck.AddRange(ids);
    }

    public static int DrawFromDeck(GameState g, PlayerId pid, int n, GameLog log)
    {
        var p = g.Player(pid);
        int drawn = 0;
        for (int i = 0; i < n; i++)
        {
            // Rulebook p.16: hand limit is 10 except during exploration.
            // DrawFromDeck is the bulk-draw entry (Cleanup phase, Draw card);
            // the exploration path bypasses this method.
            if (p.Hand.Count >= HandLimit) break;
            if (!EnsureDeckCanDraw(g, log)) break;
            var id = g.Deck[0];
            g.Deck.RemoveAt(0);
            p.Hand.Add(id);
            drawn++;
        }
        if (drawn > 0)
            log.Write($"{pid} draws {drawn} (hand={p.Hand.Count}, deck={g.Deck.Count})");
        return drawn;
    }

    public static void DiscardFromHand(GameState g, PlayerId pid, int cardId, GameLog log)
    {
        var p = g.Player(pid);
        if (!p.Hand.Remove(cardId))
            throw new InvalidOperationException($"{pid} hand does not contain card #{cardId}");
        g.Discard.Add(cardId);
        log.Write($"{pid} discards #{cardId}");
    }

    public static void PlaceOnImpulse(GameState g, PlayerId pid, int cardId, GameLog log)
    {
        var p = g.Player(pid);
        if (!p.Hand.Remove(cardId))
            throw new InvalidOperationException($"{pid} hand does not contain card #{cardId}");
        g.Impulse.Add(cardId);
        var c = g.CardsById[cardId];
        log.Write($"{pid} places #{cardId} ({c.ActionType}/{c.Color}/{c.Size}) at bottom of Impulse");
    }

    public static void TrimImpulseTopTo(GameState g, int cap, GameLog log)
    {
        while (g.Impulse.Count > cap)
        {
            var id = g.Impulse[0];
            g.Impulse.RemoveAt(0);
            g.Discard.Add(id);
            log.Write($"impulse trim #{id} (length now {g.Impulse.Count})");
        }
    }

    public static void MoveShip(GameState g, PlayerId owner, ShipLocation from, ShipLocation to, GameLog log)
    {
        // Find a placement matching owner+from and replace its location.
        for (int i = 0; i < g.ShipPlacements.Count; i++)
        {
            var sp = g.ShipPlacements[i];
            if (sp.Owner == owner && LocationsEqual(sp.Location, from))
            {
                g.ShipPlacements[i] = sp with { Location = to };
                log.Write($"{owner} moves ship {LocStr(from)} → {LocStr(to)}");
                return;
            }
        }
        throw new InvalidOperationException($"no ship of {owner} at {LocStr(from)}");
    }

    public static bool LocationsEqual(ShipLocation a, ShipLocation b) => (a, b) switch
    {
        (ShipLocation.OnNode na, ShipLocation.OnNode nb) => na.Node == nb.Node,
        (ShipLocation.OnGate ga, ShipLocation.OnGate gb) => ga.Gate == gb.Gate,
        _ => false,
    };

    public static string LocStr(ShipLocation l) => l switch
    {
        ShipLocation.OnNode n => n.Node.ToString(),
        ShipLocation.OnGate g => g.Gate.ToString(),
        _ => "?",
    };

    public static int CountShipsAt(GameState g, PlayerId owner, ShipLocation loc) =>
        g.ShipPlacements.Count(sp =>
            sp.Owner == owner && LocationsEqual(sp.Location, loc));

    public static IEnumerable<PlayerId> OwnersAt(GameState g, ShipLocation loc) =>
        g.ShipPlacements
            .Where(sp => LocationsEqual(sp.Location, loc))
            .Select(sp => sp.Owner)
            .Distinct();

    public static void MoveCardFromHandToMinerals(GameState g, PlayerId pid, int cardId, GameLog log)
    {
        var p = g.Player(pid);
        if (!p.Hand.Remove(cardId))
            throw new InvalidOperationException($"{pid} hand does not contain #{cardId}");
        p.Minerals.Add(cardId);
        var c = g.CardsById[cardId];
        log.Write($"{pid} mines #{cardId} ({c.Color}/{c.Size}) hand → minerals");
    }

    public static void MoveCardFromDeckToMinerals(GameState g, PlayerId pid, int cardId, GameLog log)
    {
        var p = g.Player(pid);
        if (!g.Deck.Remove(cardId))
            throw new InvalidOperationException($"deck does not contain #{cardId}");
        p.Minerals.Add(cardId);
        var c = g.CardsById[cardId];
        log.Write($"{pid} mines #{cardId} ({c.Color}/{c.Size}) deck → minerals");
    }

    public static void RefineMineral(GameState g, PlayerId pid, int cardId, int prestige, GameLog log)
    {
        var p = g.Player(pid);
        if (!p.Minerals.Remove(cardId))
            throw new InvalidOperationException($"{pid} minerals does not contain #{cardId}");
        g.Discard.Add(cardId);
        var c = g.CardsById[cardId];
        log.Write($"{pid} refines #{cardId} ({c.Color}/{c.Size}) minerals → discard");
        Scoring.AddPrestige(g, pid, prestige, PrestigeSource.Refining, log);
    }

    // Destroy a single ship at the given location. Returns the ship to its
    // owner's available pool. If `attackerForPrestige` is set, awards +1
    // prestige (ShipsDestroyed source) to the attacker.
    public static bool DestroyShipAt(GameState g, PlayerId owner, ShipLocation loc,
        PlayerId? attackerForPrestige, GameLog log)
    {
        for (int i = 0; i < g.ShipPlacements.Count; i++)
        {
            var sp = g.ShipPlacements[i];
            if (sp.Owner == owner && LocationsEqual(sp.Location, loc))
            {
                g.ShipPlacements.RemoveAt(i);
                g.Player(owner).ShipsAvailable++;
                log.Write($"{owner}'s ship at {LocStr(loc)} destroyed (returned to pool)");
                if (attackerForPrestige is { } att)
                {
                    Scoring.AddPrestige(g, att, 1, PrestigeSource.ShipsDestroyed, log);
                }
                return true;
            }
        }
        return false;
    }

    // Add a card to the player's Plan, redirected to NextPlan if Plan is
    // currently being resolved (rulebook p.37).
    public static void ResearchInto(GameState g, PlayerId pid, TechSlot slot, int cardId, GameLog log)
    {
        var p = g.Player(pid);
        var oldTech = p.Techs[slot];
        var newTechs = slot == TechSlot.Left
            ? new TechSlots(new Tech.Researched(cardId), p.Techs.Right)
            : new TechSlots(p.Techs.Left, new Tech.Researched(cardId));
        p.Techs = newTechs;
        var oldDesc = oldTech switch
        {
            Tech.BasicCommon => "Basic Common (discarded permanently — gone)",
            Tech.BasicUnique bu => $"Basic Unique ({bu.Race.Name}) — discarded permanently",
            Tech.Researched r => $"Researched #{r.CardId} → discard",
            _ => "?",
        };
        log.Write($"{pid} researches #{cardId} into slot {slot} (replaces {oldDesc})");
        if (oldTech is Tech.Researched prevR)
            g.Discard.Add(prevR.CardId);
        // Basic techs are removed permanently; not added to discard (they
        // were never deck cards, per p.35: "Once covered up, Basic Techs are gone!").
    }

    // Begin exploration: move the face-down card at `node` into the player's
    // hand (temporarily exceeding hand cap is allowed per rulebook p.29).
    // The node's NodeCards entry is removed; FinishExploration reinstates a
    // FaceUp entry once the player chooses a card to place.
    public static int StartExploration(GameState g, PlayerId pid, NodeId node, GameLog log)
    {
        if (!g.NodeCards.TryGetValue(node, out var state) || state is not NodeCardState.FaceDown fd)
            throw new InvalidOperationException($"node {node} is not face-down");
        var p = g.Player(pid);
        p.Hand.Add(fd.CardId);
        g.NodeCards.Remove(node);
        var c = g.CardsById[fd.CardId];
        log.Write($"{pid} explores {node}: takes #{fd.CardId} ({c.Color}/{c.Size}) into hand");
        return fd.CardId;
    }

    // Place a card from the player's hand face-up at the explored node.
    public static void FinishExploration(GameState g, PlayerId pid, NodeId node, int placedCardId, GameLog log)
    {
        var p = g.Player(pid);
        if (!p.Hand.Remove(placedCardId))
            throw new InvalidOperationException($"{pid} hand missing #{placedCardId}");
        g.NodeCards[node] = new NodeCardState.FaceUp(placedCardId);
        var c = g.CardsById[placedCardId];
        log.Write($"{pid} places #{placedCardId} ({c.Color}/{c.Size}) face-up at {node}");
    }

    public static void AddCardToPlan(GameState g, PlayerId pid, int cardId, GameLog log)
    {
        var p = g.Player(pid);
        if (g.IsResolvingPlan)
        {
            p.NextPlan ??= new List<int>();
            p.NextPlan.Add(cardId);
            log.Write($"{pid} plans #{cardId} → NextPlan (mid-resolution)");
        }
        else
        {
            p.Plan.Add(cardId);
            log.Write($"{pid} plans #{cardId} → Plan (size now {p.Plan.Count})");
        }
    }

    public static int BuildShip(GameState g, PlayerId owner, ShipLocation loc, GameLog log)
    {
        var p = g.Player(owner);
        if (p.ShipsAvailable <= 0) return 0;
        p.ShipsAvailable--;
        g.ShipPlacements.Add(new ShipPlacement(owner, loc));
        log.Write($"{owner} builds at {LocStr(loc)} (available={p.ShipsAvailable})");
        return 1;
    }
}
