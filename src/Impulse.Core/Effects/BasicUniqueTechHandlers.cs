using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

// Each BasicUnique tech is the race-specific starter on the Command Center.
// Texts confirmed against raza1.jpg–raza6.jpg.

// Piscesish (Blue): "Draw one size one card from the deck."
public sealed class PiscesishTechHandler : IEffectHandler
{
    public bool Execute(GameState g, EffectContext ctx)
    {
        if (!Mechanics.EnsureDeckCanDraw(g, g.Log)) { ctx.IsComplete = true; return false; }
        int drawn = g.Deck[0];
        g.Deck.RemoveAt(0);
        var c = g.CardsById[drawn];
        var p = g.Player(ctx.ActivatingPlayer);
        if (c.Size == 1)
        {
            if (p.Hand.Count >= Mechanics.HandLimit)
            {
                g.Discard.Add(drawn);
                g.Log.Write($"  → Piscesish: drew #{drawn} → discard (hand limit {Mechanics.HandLimit})");
                g.Log.EmitReveal(drawn, RevealOutcome.Discarded, "hand full");
            }
            else
            {
                p.Hand.Add(drawn);
                g.Log.Write($"  → Piscesish: drew #{drawn} ({c.Color}/{c.Size}) → hand");
                g.Log.EmitReveal(drawn, RevealOutcome.Kept);
            }
        }
        else
        {
            g.Discard.Add(drawn);
            g.Log.Write($"  → Piscesish: drew #{drawn} ({c.Color}/{c.Size}) → discard (need size 1)");
            g.Log.EmitReveal(drawn, RevealOutcome.Discarded, "need size 1");
        }
        ctx.IsComplete = true;
        return true;
    }
}

// Ariek (Green): "Command one fleet for one move. It must end the move
// occupying or patrolling the Sector Core."
public sealed class AriekTechHandler : IEffectHandler
{
    private enum Stage { Start, AwaitingFleet, AwaitingPath, Executing, AwaitingSectorCoreColor, Done }
    private sealed class State
    {
        public Stage Stage;
        public ShipLocation? Origin;
        public IReadOnlyList<ShipLocation>? Path;
        public int PathStepIndex;
        public NodeId? ExplorationNode;
        public BattleState? Battle;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;
        var coreId = g.Map.SectorCoreNodeId;
        var coreGates = g.Map.AdjacencyByNode[coreId].Select(g => g.Id).ToHashSet();

        // Battle in progress? Forward to resolver.
        if (st.Battle is not null)
        {
            bool done = BattleResolver.Step(g, ctx, st.Battle);
            if (done) { st.Battle = null; ctx.IsComplete = true; return true; }
            return true;
        }

        // Exploration card pick in progress?
        if (st.ExplorationNode is not null)
        {
            if (ctx.PendingChoice is SelectHandCardRequest answered && answered.ChosenCardId is { } picked)
            {
                ctx.PendingChoice = null;
                Mechanics.FinishExploration(g, ctx.ActivatingPlayer, st.ExplorationNode.Value, picked, g.Log);
                st.ExplorationNode = null;
                // Fall through to Executing.
            }
            else
            {
                ctx.IsComplete = true;
                return false;
            }
        }

        if (st.Stage == Stage.Start)
        {
            // Legal origins: any of player's ship locations from which the
            // Sector Core (occupy via transport) or a Sector Core gate
            // (patrol via cruiser) is reachable in 1 move.
            var legal = LegalOrigins(g, ctx.ActivatingPlayer, coreId, coreGates);
            if (legal.Count == 0)
            {
                g.Log.Write($"  → Ariek: no fleet can reach Sector Core in 1 move");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new SelectFleetRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalLocations = legal,
                Prompt = "Select a fleet (must end move on/patrolling Sector Core).",
            };
            st.Stage = Stage.AwaitingFleet;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingFleet)
        {
            var req = (SelectFleetRequest)ctx.PendingChoice!;
            var origin = req.Chosen ?? throw new InvalidOperationException("fleet not chosen");
            ctx.PendingChoice = null;
            st.Origin = origin;

            var paths = Movement.EnumeratePaths(g, ctx.ActivatingPlayer, origin, 1)
                .Where(path => EndsAtCore(path[^1], coreId, coreGates))
                .ToList();
            if (paths.Count == 0)
            {
                g.Log.Write($"  → Ariek: no legal Sector Core path from origin");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new DeclareMoveRequest
            {
                Player = ctx.ActivatingPlayer,
                Origin = origin,
                MaxMoves = 1,
                LegalPaths = paths,
                Prompt = "Move toward the Sector Core.",
            };
            st.Stage = Stage.AwaitingPath;
            ctx.Paused = true;
            return true;
        }

        if (st.Stage == Stage.AwaitingPath)
        {
            var req = (DeclareMoveRequest)ctx.PendingChoice!;
            var path = req.ChosenPath ?? throw new InvalidOperationException("path not chosen");
            ctx.PendingChoice = null;
            st.Path = path;
            st.PathStepIndex = 0;
            st.Stage = Stage.Executing;
        }

        if (st.Stage == Stage.Executing)
        {
            var path = st.Path!;
            var here = st.PathStepIndex == 0 ? st.Origin! : path[st.PathStepIndex - 1];
            while (st.PathStepIndex < path.Count)
            {
                var step = path[st.PathStepIndex];
                // Exploration trigger first (rulebook p.29: ships passing
                // onto/through a face-down card explore it).
                NodeId? exploreNode = null;
                if (here is ShipLocation.OnNode && step is ShipLocation.OnNode stepNode)
                {
                    if (g.NodeCards.TryGetValue(stepNode.Node, out var s1) && s1 is NodeCardState.FaceDown)
                        exploreNode = stepNode.Node;
                }
                else if (here is ShipLocation.OnGate fromG && step is ShipLocation.OnGate toG)
                {
                    var pass = Movement.SharedNode(g.Map, fromG.Gate, toG.Gate);
                    if (pass is { } pn && g.NodeCards.TryGetValue(pn, out var s2) && s2 is NodeCardState.FaceDown)
                        exploreNode = pn;
                }
                if (exploreNode is { } explNode)
                {
                    Mechanics.StartExploration(g, ctx.ActivatingPlayer, explNode, g.Log);
                    st.ExplorationNode = explNode;
                    var p = g.Player(ctx.ActivatingPlayer);
                    ctx.PendingChoice = new SelectHandCardRequest
                    {
                        Player = ctx.ActivatingPlayer,
                        LegalCardIds = p.Hand.ToList(),
                        AllowNone = false,
                        Prompt = $"Exploring {explNode}: place a card from your hand face-up.",
                    };
                    ctx.Paused = true;
                    return true;
                }
                if (here is ShipLocation.OnGate fromGate && step is ShipLocation.OnGate toGate)
                {
                    var pass = Movement.SharedNode(g.Map, fromGate.Gate, toGate.Gate);
                    if (pass is { } passageNode &&
                        Movement.IsPatrolledByEnemy(g, ctx.ActivatingPlayer, passageNode))
                    {
                        var defender = DefenderChoice.Resolve(g, ctx,
                            CommandHandler.FindPatrollers(g, ctx.ActivatingPlayer, passageNode),
                            $"Multiple players patrol {passageNode} — choose who to fight.");
                        if (defender is null) return true; // paused for choice
                        st.Battle = CommandHandler.SetupBattlePatrolThrough(g, ctx, fromGate, toGate, passageNode, attackerCount: 1, defender.Value);
                        if (BattleResolver.Step(g, ctx, st.Battle))
                        { st.Battle = null; ctx.IsComplete = true; return true; }
                        return true;
                    }
                    if (Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, toGate.Gate))
                    {
                        var defender = DefenderChoice.Resolve(g, ctx,
                            CommandHandler.FindEnemiesOnGate(g, ctx.ActivatingPlayer, toGate.Gate),
                            $"Multiple players have cruisers on {toGate.Gate} — choose who to fight.");
                        if (defender is null) return true; // paused for choice
                        st.Battle = CommandHandler.SetupBattleMoveOnto(g, ctx, fromGate, toGate, attackerCount: 1, defender.Value);
                        if (BattleResolver.Step(g, ctx, st.Battle))
                        { st.Battle = null; ctx.IsComplete = true; return true; }
                        return true;
                    }
                    if (pass is { } pn2)
                        DestroyEnemyTransportsAt(g, ctx.ActivatingPlayer, pn2);
                    if (g.IsGameOver) { ctx.IsComplete = true; return true; }
                }
                Mechanics.MoveShip(g, ctx.ActivatingPlayer, here, step, g.Log);
                here = step;
                st.PathStepIndex++;
            }
            // Sector Core activation when transport ends on the core: prompt
            // for color choice (rulebook p.27).
            if (here is ShipLocation.OnNode endNode && endNode.Node == coreId &&
                !(st.Origin is ShipLocation.OnNode o && o.Node == coreId))
            {
                var minerals = g.Player(ctx.ActivatingPlayer).Minerals;
                var options = new[] { CardColor.Red, CardColor.Blue, CardColor.Green, CardColor.Yellow }
                    .Select(c =>
                    {
                        int gems = minerals.Where(id => g.CardsById[id].Color == c).Sum(id => g.CardsById[id].Size);
                        int b = (gems + 1) / 2;
                        return $"{c}: {gems} gem(s) → +{1 + b} prestige";
                    })
                    .ToList();
                ctx.PendingChoice = new SelectFromOptionsRequest
                {
                    Player = ctx.ActivatingPlayer,
                    Options = options,
                    Prompt = "Sector Core: choose mineral color for boost (+1 arriving transport).",
                };
                st.Stage = Stage.AwaitingSectorCoreColor;
                ctx.Paused = true;
                return true;
            }
            ctx.IsComplete = true;
            return true;
        }

        if (st.Stage == Stage.AwaitingSectorCoreColor)
        {
            var creq = (SelectFromOptionsRequest)ctx.PendingChoice!;
            int chosen = creq.Chosen ?? 0;
            ctx.PendingChoice = null;
            // Display order: Red, Blue, Green, Yellow (NOT the CardColor
            // enum order, which is Blue=0, Yellow=1, Red=2, Green=3).
            var displayOrder = new[] { CardColor.Red, CardColor.Blue, CardColor.Green, CardColor.Yellow };
            var color = displayOrder[Math.Clamp(chosen, 0, displayOrder.Length - 1)];
            var minerals = g.Player(ctx.ActivatingPlayer).Minerals;
            int gems = minerals.Where(id => g.CardsById[id].Color == color).Sum(id => g.CardsById[id].Size);
            int coreBoost = (gems + 1) / 2;
            int points = 1 + coreBoost;
            g.Log.Write($"  → Sector Core activated as {color}: {gems} gem(s) + 1 transport → +{points} prestige");
            Scoring.AddPrestige(g, ctx.ActivatingPlayer, points, PrestigeSource.SectorCoreActivatedByTransports, g.Log);
            ctx.IsComplete = true;
            return true;
        }
        return false;
    }

    private static IReadOnlyList<ShipLocation> LegalOrigins(
        GameState g, PlayerId mover, NodeId coreId, IReadOnlySet<GateId> coreGates)
    {
        var result = new List<ShipLocation>();
        var seen = new HashSet<(int, int)>();
        foreach (var sp in g.ShipPlacements.Where(s => s.Owner == mover))
        {
            var key = sp.Location switch
            {
                ShipLocation.OnNode n => (0, n.Node.Value),
                ShipLocation.OnGate gateLoc => (1, gateLoc.Gate.Value),
                _ => (-1, 0),
            };
            if (!seen.Add(key)) continue;
            var paths = Movement.EnumeratePaths(g, mover, sp.Location, 1);
            if (paths.Any(path => EndsAtCore(path[^1], coreId, coreGates)))
                result.Add(sp.Location);
        }
        return result;
    }

    private static bool EndsAtCore(ShipLocation loc, NodeId coreId, IReadOnlySet<GateId> coreGates) =>
        loc switch
        {
            ShipLocation.OnNode n => n.Node == coreId,
            ShipLocation.OnGate gateLoc => coreGates.Contains(gateLoc.Gate),
            _ => false,
        };

    private static void DestroyEnemyTransportsAt(GameState g, PlayerId mover, NodeId node)
    {
        var victims = g.ShipPlacements
            .Where(sp => sp.Owner != mover &&
                         sp.Location is ShipLocation.OnNode n && n.Node == node)
            .ToList();
        foreach (var v in victims)
        {
            Mechanics.DestroyShipAt(g, v.Owner, v.Location, mover, g.Log);
            if (g.IsGameOver) return;
        }
    }
}

// Herculese (Purple): "Command one Cruiser for one move through an
// unexplored card." The passage node must be face-down at path-choice time;
// it gets explored mid-move (player places a card from hand face-up there).
// Patrol/battle rules still apply.
public sealed class HerculeseTechHandler : IEffectHandler
{
    private enum Stage { Start, AwaitingFleet, AwaitingPath, Executing, Done }
    private sealed class State
    {
        public Stage Stage;
        public ShipLocation? Origin;
        public IReadOnlyList<ShipLocation>? Path;
        public int PathStepIndex;
        public Map.NodeId? ExplorationNode;
        public BattleState? Battle;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;

        if (st.Battle is not null)
        {
            bool done = BattleResolver.Step(g, ctx, st.Battle);
            if (done) { st.Battle = null; ctx.IsComplete = true; return true; }
            return true;
        }

        if (st.ExplorationNode is not null)
        {
            if (ctx.PendingChoice is SelectHandCardRequest answered && answered.ChosenCardId is { } picked)
            {
                ctx.PendingChoice = null;
                Mechanics.FinishExploration(g, ctx.ActivatingPlayer, st.ExplorationNode.Value, picked, g.Log);
                st.ExplorationNode = null;
                // Fall through to Executing.
            }
            else { ctx.IsComplete = true; return false; }
        }

        if (st.Stage == Stage.Start)
        {
            var legal = LegalCruiserOrigins(g, ctx.ActivatingPlayer);
            if (legal.Count == 0)
            {
                g.Log.Write($"  → Herculese: no cruiser can move through an unexplored card");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new SelectFleetRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalLocations = legal,
                Prompt = "Select a Cruiser to move through an unexplored card.",
            };
            st.Stage = Stage.AwaitingFleet;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingFleet)
        {
            var req = (SelectFleetRequest)ctx.PendingChoice!;
            var origin = req.Chosen ?? throw new InvalidOperationException("fleet not chosen");
            ctx.PendingChoice = null;
            st.Origin = origin;

            var paths = Movement.EnumeratePaths(g, ctx.ActivatingPlayer, origin, 1)
                .Where(path => PathPassesUnexplored(g, origin, path))
                .ToList();
            if (paths.Count == 0)
            {
                g.Log.Write($"  → Herculese: no path through unexplored card from {Mechanics.LocStr(origin)}");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new DeclareMoveRequest
            {
                Player = ctx.ActivatingPlayer,
                Origin = origin,
                MaxMoves = 1,
                LegalPaths = paths,
                Prompt = "Choose a gate; passage must be face-down.",
            };
            st.Stage = Stage.AwaitingPath;
            ctx.Paused = true;
            return true;
        }

        if (st.Stage == Stage.AwaitingPath)
        {
            var req = (DeclareMoveRequest)ctx.PendingChoice!;
            var path = req.ChosenPath ?? throw new InvalidOperationException("path not chosen");
            ctx.PendingChoice = null;
            st.Path = path;
            st.PathStepIndex = 0;
            st.Stage = Stage.Executing;
        }

        if (st.Stage == Stage.Executing)
        {
            return ContinuePath(g, ctx, st);
        }
        return false;
    }

    private static bool ContinuePath(GameState g, EffectContext ctx, State st)
    {
        var path = st.Path!;
        var here = st.PathStepIndex == 0 ? st.Origin! : path[st.PathStepIndex - 1];
        while (st.PathStepIndex < path.Count)
        {
            var step = path[st.PathStepIndex];
            if (here is ShipLocation.OnGate fromGate && step is ShipLocation.OnGate toGate)
            {
                var pass = Movement.SharedNode(g.Map, fromGate.Gate, toGate.Gate);
                // Exploration trigger first (passage was face-down at choice time).
                if (pass is { } pn && g.NodeCards.TryGetValue(pn, out var s1) && s1 is NodeCardState.FaceDown)
                {
                    Mechanics.StartExploration(g, ctx.ActivatingPlayer, pn, g.Log);
                    st.ExplorationNode = pn;
                    var p = g.Player(ctx.ActivatingPlayer);
                    ctx.PendingChoice = new SelectHandCardRequest
                    {
                        Player = ctx.ActivatingPlayer,
                        LegalCardIds = p.Hand.ToList(),
                        AllowNone = false,
                        Prompt = $"Exploring {pn}: place a card from your hand face-up.",
                    };
                    ctx.Paused = true;
                    return true;
                }
                // After exploration, check patrol/battle on the (now face-up) card.
                if (pass is { } passageNode &&
                    Movement.IsPatrolledByEnemy(g, ctx.ActivatingPlayer, passageNode))
                {
                    var defender = DefenderChoice.Resolve(g, ctx,
                        CommandHandler.FindPatrollers(g, ctx.ActivatingPlayer, passageNode),
                        $"Multiple players patrol {passageNode} — choose who to fight.");
                    if (defender is null) return true; // paused for choice
                    st.Battle = CommandHandler.SetupBattlePatrolThrough(g, ctx, fromGate, toGate, passageNode, attackerCount: 1, defender.Value);
                    if (BattleResolver.Step(g, ctx, st.Battle))
                    { st.Battle = null; ctx.IsComplete = true; return true; }
                    return true;
                }
                if (Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, toGate.Gate))
                {
                    var defender = DefenderChoice.Resolve(g, ctx,
                        CommandHandler.FindEnemiesOnGate(g, ctx.ActivatingPlayer, toGate.Gate),
                        $"Multiple players have cruisers on {toGate.Gate} — choose who to fight.");
                    if (defender is null) return true; // paused for choice
                    st.Battle = CommandHandler.SetupBattleMoveOnto(g, ctx, fromGate, toGate, attackerCount: 1, defender.Value);
                    if (BattleResolver.Step(g, ctx, st.Battle))
                    { st.Battle = null; ctx.IsComplete = true; return true; }
                    return true;
                }
                if (pass is { } pn3)
                    DestroyEnemyTransportsAt(g, ctx.ActivatingPlayer, pn3);
                if (g.IsGameOver) { ctx.IsComplete = true; return true; }
            }
            Mechanics.MoveShip(g, ctx.ActivatingPlayer, here, step, g.Log);
            here = step;
            st.PathStepIndex++;
        }
        ctx.IsComplete = true;
        return true;
    }

    private static bool PathPassesUnexplored(GameState g, ShipLocation origin, IReadOnlyList<ShipLocation> path)
    {
        if (origin is not ShipLocation.OnGate from) return false;
        if (path.Count != 1 || path[0] is not ShipLocation.OnGate to) return false;
        var pass = Movement.SharedNode(g.Map, from.Gate, to.Gate);
        return pass is { } pn && g.NodeCards.TryGetValue(pn, out var s) && s is NodeCardState.FaceDown;
    }

    private static IReadOnlyList<ShipLocation> LegalCruiserOrigins(GameState g, PlayerId mover)
    {
        var result = new List<ShipLocation>();
        var seen = new HashSet<int>();
        foreach (var sp in g.ShipPlacements.Where(s => s.Owner == mover && s.Location is ShipLocation.OnGate))
        {
            int gateId = ((ShipLocation.OnGate)sp.Location).Gate.Value;
            if (!seen.Add(gateId)) continue;
            var paths = Movement.EnumeratePaths(g, mover, sp.Location, 1);
            if (paths.Any(path => PathPassesUnexplored(g, sp.Location, path)))
                result.Add(sp.Location);
        }
        return result;
    }

    private static void DestroyEnemyTransportsAt(GameState g, PlayerId mover, Map.NodeId node)
    {
        var victims = g.ShipPlacements
            .Where(sp => sp.Owner != mover &&
                         sp.Location is ShipLocation.OnNode n && n.Node == node)
            .ToList();
        foreach (var v in victims)
        {
            Mechanics.DestroyShipAt(g, v.Owner, v.Location, mover, g.Log);
            if (g.IsGameOver) return;
        }
    }
}

// Triangulumnists (White): "Build a Cruiser at home on an edge that touches
// an unexplored card." Pick one of the player's home gates whose other
// endpoint is face-down, and place a cruiser there.
public sealed class TriangulumnistsTechHandler : IEffectHandler
{
    private enum Stage { Start, AwaitingPlacement, Done }
    private sealed class State { public Stage Stage; }

    public bool Execute(GameState g, EffectContext ctx)
    {
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;
        var p = g.Player(ctx.ActivatingPlayer);

        if (st.Stage == Stage.Start)
        {
            if (p.ShipsAvailable <= 0)
            {
                g.Log.Write($"  → Triangulumnists: no ships available");
                ctx.IsComplete = true;
                return true;
            }
            var home = g.Map.HomeNodeIds[ctx.ActivatingPlayer];
            var legal = g.Map.AdjacencyByNode[home]
                .Where(gate =>
                {
                    var other = gate.EndpointA == home ? gate.EndpointB : gate.EndpointA;
                    if (!g.NodeCards.TryGetValue(other, out var s) || s is not NodeCardState.FaceDown) return false;
                    // Rulebook p.36: cannot build a Cruiser on a gate with an enemy Cruiser.
                    return !Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, gate.Id);
                })
                .Select(gate => (ShipLocation)new ShipLocation.OnGate(gate.Id))
                .ToList();
            if (legal.Count == 0)
            {
                g.Log.Write($"  → Triangulumnists: no home edge touches an unexplored card");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new SelectShipPlacementRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalLocations = legal,
                Prompt = "Build a Cruiser on a home edge that touches an unexplored card.",
            };
            st.Stage = Stage.AwaitingPlacement;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingPlacement)
        {
            var req = (SelectShipPlacementRequest)ctx.PendingChoice!;
            var loc = req.Chosen ?? throw new InvalidOperationException("placement not chosen");
            ctx.PendingChoice = null;
            Mechanics.BuildShip(g, ctx.ActivatingPlayer, loc, g.Log);
            ctx.IsComplete = true;
            return true;
        }
        return false;
    }
}

// Draconians (Red): "Research one card from your hand. It must match color
// and size with the last card on the Impulse."
public sealed class DraconiansTechHandler : IEffectHandler
{
    private enum Stage { Start, AwaitingPick, AwaitingSlot, Done }
    private sealed class State { public Stage Stage; public int? PickedCardId; }

    public bool Execute(GameState g, EffectContext ctx)
    {
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;
        var p = g.Player(ctx.ActivatingPlayer);

        if (st.Stage == Stage.Start)
        {
            if (g.Impulse.Count == 0)
            {
                g.Log.Write($"  → Draconians: Impulse is empty, no anchor card");
                ctx.IsComplete = true;
                return true;
            }
            var anchor = g.CardsById[g.Impulse[^1]]; // bottom = last placed
            var legal = p.Hand
                .Where(id => g.CardsById[id].Color == anchor.Color && g.CardsById[id].Size == anchor.Size)
                .ToList();
            if (legal.Count == 0)
            {
                g.Log.Write($"  → Draconians: no hand card matches Impulse anchor ({anchor.Color}/{anchor.Size})");
                ctx.IsComplete = true;
                return true;
            }
            ctx.PendingChoice = new SelectHandCardRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalCardIds = legal,
                AllowNone = true,
                Prompt = $"Research a hand card matching {anchor.Color}/{anchor.Size}, or DONE.",
            };
            st.Stage = Stage.AwaitingPick;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingPick)
        {
            var req = (SelectHandCardRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            if (req.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            st.PickedCardId = req.ChosenCardId.Value;
            ctx.PendingChoice = new SelectTechSlotRequest
            {
                Player = ctx.ActivatingPlayer,
                IncomingCardId = st.PickedCardId,
                AllowSkip = true,
                Prompt = $"Choose a tech slot to overwrite with #{st.PickedCardId}, or SKIP to keep it in hand.",
            };
            st.Stage = Stage.AwaitingSlot;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingSlot)
        {
            var req = (SelectTechSlotRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            int cardId = st.PickedCardId!.Value;
            if (req.Chosen is null)
            {
                // Skip: card stays in hand (it was never removed).
                g.Log.Write($"  → Draconians: research skipped — #{cardId} stays in hand");
                ctx.IsComplete = true;
                return true;
            }
            var slot = req.Chosen.Value;
            p.Hand.Remove(cardId);
            Mechanics.ResearchInto(g, ctx.ActivatingPlayer, slot, cardId, g.Log);
            ctx.IsComplete = true;
            return true;
        }
        return false;
    }
}

// Caelumnites (Yellow): "Mine one card from your hand. It must match color
// and size with the last card on the Impulse."
public sealed class CaelumnitesTechHandler : IEffectHandler
{
    public bool Execute(GameState g, EffectContext ctx)
    {
        var p = g.Player(ctx.ActivatingPlayer);

        if (ctx.PendingChoice is SelectHandCardRequest answered)
        {
            ctx.PendingChoice = null;
            if (answered.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            Mechanics.MoveCardFromHandToMinerals(g, ctx.ActivatingPlayer, answered.ChosenCardId.Value, g.Log);
            ctx.IsComplete = true;
            return true;
        }

        if (g.Impulse.Count == 0)
        {
            g.Log.Write($"  → Caelumnites: Impulse is empty, no anchor card");
            ctx.IsComplete = true;
            return true;
        }
        var anchor = g.CardsById[g.Impulse[^1]];
        var legal = p.Hand
            .Where(id => g.CardsById[id].Color == anchor.Color && g.CardsById[id].Size == anchor.Size)
            .ToList();
        if (legal.Count == 0)
        {
            g.Log.Write($"  → Caelumnites: no hand card matches Impulse anchor ({anchor.Color}/{anchor.Size})");
            ctx.IsComplete = true;
            return true;
        }
        ctx.PendingChoice = new SelectHandCardRequest
        {
            Player = ctx.ActivatingPlayer,
            LegalCardIds = legal,
            AllowNone = true,
            Prompt = $"Mine a hand card matching {anchor.Color}/{anchor.Size}, or DONE.",
        };
        ctx.Paused = true;
        return false;
    }
}
