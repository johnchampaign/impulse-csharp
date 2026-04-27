using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

// Rulebook p.20: "Discard a card in order to either: Command one fleet for
// one move OR Build one ship at home."
//
// The Command sub-action follows the same path-walking semantics as the
// regular Command action — exploration on transport-onto / cruiser-through
// face-down cards, on-arrival activation of the destination card or
// Sector Core, and battle on cruiser passage / move-onto.
public sealed class BasicCommonTechHandler : IEffectHandler
{
    private readonly EffectRegistry? _registry;

    public BasicCommonTechHandler(EffectRegistry? registry = null)
    {
        _registry = registry;
    }

    private enum Stage
    {
        Start,
        AwaitingDiscard,
        AwaitingChoice,
        AwaitingFleet,
        AwaitingCount,
        AwaitingPath,
        ExecutingPath,
        AwaitingActivation,
        AwaitingSectorCoreColor,
        AwaitingPlacement,
        Done,
    }

    private sealed class State
    {
        public Stage Stage;
        public ShipLocation? Origin;
        public int ChosenCount = 1;
        public IReadOnlyList<ShipLocation>? Path;
        public int PathStepIndex;
        public NodeId? ExplorationNode;
        public BattleState? Battle;
        public IEffectHandler? ActivationHandler;
        public EffectContext? ActivationCtx;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;
        var p = g.Player(ctx.ActivatingPlayer);

        // Battle in progress? Forward to resolver.
        if (st.Battle is not null)
        {
            bool done = BattleResolver.Step(g, ctx, st.Battle);
            if (done)
            {
                st.Battle = null;
                st.Stage = Stage.Done;
                ctx.IsComplete = true;
                return true;
            }
            return true;
        }

        // Sector Core color resume.
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
            int coreBoost = (gems + st.ChosenCount) / 2;
            int points = 1 + coreBoost;
            g.Log.Write($"  → Sector Core activated as {color}: {gems} gem(s) + {st.ChosenCount} transport(s) → +{points} prestige");
            Scoring.AddPrestige(g, ctx.ActivatingPlayer, points, PrestigeSource.SectorCoreActivatedByTransports, g.Log);
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }

        // Activation sub-effect in progress? Forward.
        if (st.Stage == Stage.AwaitingActivation && st.ActivationHandler is not null && st.ActivationCtx is not null)
        {
            var subCtx = st.ActivationCtx;
            subCtx.Paused = false;
            st.ActivationHandler.Execute(g, subCtx);
            return MirrorActivation(ctx, st, subCtx);
        }

        // Exploration card pick in progress?
        if (st.ExplorationNode is not null)
        {
            if (ctx.PendingChoice is SelectHandCardRequest answered && answered.ChosenCardId is { } picked)
            {
                ctx.PendingChoice = null;
                Mechanics.FinishExploration(g, ctx.ActivatingPlayer, st.ExplorationNode.Value, picked, g.Log);
                st.ExplorationNode = null;
                // Fall through to ExecutingPath.
            }
            else
            {
                ctx.IsComplete = true;
                return false;
            }
        }

        if (st.Stage == Stage.Start)
        {
            if (p.Hand.Count == 0)
            {
                g.Log.Write($"  → BasicCommon tech: {ctx.ActivatingPlayer} hand empty, can't discard");
                ctx.IsComplete = true;
                return false;
            }
            ctx.PendingChoice = new SelectHandCardRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalCardIds = p.Hand.ToList(),
                AllowNone = true,
                Prompt = "Discard a card to use Basic Common, or DONE to cancel.",
            };
            st.Stage = Stage.AwaitingDiscard;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingDiscard)
        {
            var req = (SelectHandCardRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            if (req.ChosenCardId is null) { ctx.IsComplete = true; return true; }
            Mechanics.DiscardFromHand(g, ctx.ActivatingPlayer, req.ChosenCardId.Value, g.Log);

            ctx.PendingChoice = new SelectFromOptionsRequest
            {
                Player = ctx.ActivatingPlayer,
                Options = new[] { "Command one fleet for one move", "Build one ship at home" },
                Prompt = "Choose Basic Common sub-action:",
            };
            st.Stage = Stage.AwaitingChoice;
            ctx.Paused = true;
            return true;
        }

        if (st.Stage == Stage.AwaitingChoice)
        {
            var req = (SelectFromOptionsRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            int chosen = req.Chosen ?? 0;
            if (chosen == 0)
            {
                var legal = LegalCommandOrigins(g, ctx.ActivatingPlayer);
                if (legal.Count == 0)
                {
                    g.Log.Write($"  → BasicCommon: no legal Command origin");
                    ctx.IsComplete = true;
                    return true;
                }
                ctx.PendingChoice = new SelectFleetRequest
                {
                    Player = ctx.ActivatingPlayer,
                    LegalLocations = legal,
                    Prompt = "Select a fleet to command (1 ship, 1 move).",
                };
                st.Stage = Stage.AwaitingFleet;
                ctx.Paused = true;
                return true;
            }
            else
            {
                var home = g.Map.HomeNodeIds[ctx.ActivatingPlayer];
                var legal = new List<ShipLocation>();
                legal.Add(new ShipLocation.OnNode(home));
                foreach (var gate in g.Map.AdjacencyByNode[home])
                    legal.Add(new ShipLocation.OnGate(gate.Id));
                ctx.PendingChoice = new SelectShipPlacementRequest
                {
                    Player = ctx.ActivatingPlayer,
                    LegalLocations = legal,
                    Prompt = "Build 1 ship at home.",
                };
                st.Stage = Stage.AwaitingPlacement;
                ctx.Paused = true;
                return true;
            }
        }

        if (st.Stage == Stage.AwaitingFleet)
        {
            var req = (SelectFleetRequest)ctx.PendingChoice!;
            var origin = req.Chosen ?? throw new InvalidOperationException("fleet not chosen");
            ctx.PendingChoice = null;
            st.Origin = origin;

            int shipsHere = Mechanics.CountShipsAt(g, ctx.ActivatingPlayer, origin);
            if (shipsHere <= 1)
            {
                st.ChosenCount = 1;
                return TransitionToPath(g, ctx, st);
            }
            ctx.PendingChoice = new SelectFleetSizeRequest
            {
                Player = ctx.ActivatingPlayer,
                Min = 1,
                Max = shipsHere,
                Prompt = $"How many ships to move? (1–{shipsHere})",
            };
            st.Stage = Stage.AwaitingCount;
            ctx.Paused = true;
            return true;
        }

        if (st.Stage == Stage.AwaitingCount)
        {
            var req = (SelectFleetSizeRequest)ctx.PendingChoice!;
            int chosen = req.Chosen ?? throw new InvalidOperationException("fleet size not chosen");
            ctx.PendingChoice = null;
            st.ChosenCount = Math.Clamp(chosen, req.Min, req.Max);
            return TransitionToPath(g, ctx, st);
        }

        if (st.Stage == Stage.AwaitingPath)
        {
            var req = (DeclareMoveRequest)ctx.PendingChoice!;
            var path = req.ChosenPath ?? throw new InvalidOperationException("path not chosen");
            ctx.PendingChoice = null;
            st.Path = path;
            st.PathStepIndex = 0;
            st.Stage = Stage.ExecutingPath;
        }

        if (st.Stage == Stage.ExecutingPath)
        {
            return ContinuePath(g, ctx, st);
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

    private static bool TransitionToPath(GameState g, EffectContext ctx, State st)
    {
        var paths = Movement.EnumeratePaths(g, ctx.ActivatingPlayer, st.Origin!, maxMoves: 1);
        if (paths.Count == 0)
        {
            g.Log.Write($"  → BasicCommon: no legal paths from {Mechanics.LocStr(st.Origin!)}");
            ctx.IsComplete = true;
            return true;
        }
        ctx.PendingChoice = new DeclareMoveRequest
        {
            Player = ctx.ActivatingPlayer,
            Origin = st.Origin!,
            MaxMoves = 1,
            LegalPaths = paths,
            Prompt = "Declare destination (1 move).",
        };
        st.Stage = Stage.AwaitingPath;
        ctx.Paused = true;
        return true;
    }

    private bool ContinuePath(GameState g, EffectContext ctx, State st)
    {
        var path = st.Path!;
        var here = st.PathStepIndex == 0 ? st.Origin! : path[st.PathStepIndex - 1];

        while (st.PathStepIndex < path.Count)
        {
            var step = path[st.PathStepIndex];

            // Exploration trigger
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

            // Cruiser passage: battle / destruction.
            if (here is ShipLocation.OnGate fromGate && step is ShipLocation.OnGate toGate)
            {
                var pass = Movement.SharedNode(g.Map, fromGate.Gate, toGate.Gate);
                if (pass is { } passageNode &&
                    Movement.IsPatrolledByEnemy(g, ctx.ActivatingPlayer, passageNode))
                {
                    st.Battle = CommandHandler.SetupBattlePatrolThrough(g, ctx, fromGate, passageNode, st.ChosenCount);
                    if (BattleResolver.Step(g, ctx, st.Battle))
                    {
                        st.Battle = null;
                        st.Stage = Stage.Done;
                        ctx.IsComplete = true;
                        return true;
                    }
                    return true;
                }
                if (Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, toGate.Gate))
                {
                    st.Battle = CommandHandler.SetupBattleMoveOnto(g, ctx, fromGate, toGate, st.ChosenCount);
                    if (BattleResolver.Step(g, ctx, st.Battle))
                    {
                        st.Battle = null;
                        st.Stage = Stage.Done;
                        ctx.IsComplete = true;
                        return true;
                    }
                    return true;
                }
                if (pass is { } pn2)
                    DestroyEnemyTransportsAt(g, ctx.ActivatingPlayer, pn2);
                if (g.IsGameOver) { ctx.IsComplete = true; return true; }
            }

            for (int s = 0; s < st.ChosenCount; s++)
                Mechanics.MoveShip(g, ctx.ActivatingPlayer, here, step, g.Log);
            here = step;
            st.PathStepIndex++;
        }
        return TryStartActivation(g, ctx, st, here);
    }

    private bool TryStartActivation(GameState g, EffectContext ctx, State st, ShipLocation finalLoc)
    {
        if (finalLoc is not ShipLocation.OnNode endNode)
        {
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        if (st.Origin is ShipLocation.OnNode origNode && origNode.Node == endNode.Node)
        {
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        if (!g.NodeCards.TryGetValue(endNode.Node, out var nodeState))
        {
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        if (nodeState is NodeCardState.SectorCore)
        {
            // Rulebook p.27: player chooses mineral color for boost.
            var minerals = g.Player(ctx.ActivatingPlayer).Minerals;
            var options = new[] { CardColor.Red, CardColor.Blue, CardColor.Green, CardColor.Yellow }
                .Select(c =>
                {
                    int gems = minerals.Where(id => g.CardsById[id].Color == c).Sum(id => g.CardsById[id].Size);
                    int b = (gems + st.ChosenCount) / 2;
                    return $"{c}: {gems} gem(s) → +{1 + b} prestige";
                })
                .ToList();
            ctx.PendingChoice = new SelectFromOptionsRequest
            {
                Player = ctx.ActivatingPlayer,
                Options = options,
                Prompt = $"Sector Core: choose mineral color for boost (+{st.ChosenCount} arriving transports).",
            };
            st.Stage = Stage.AwaitingSectorCoreColor;
            ctx.Paused = true;
            return true;
        }
        if (nodeState is not NodeCardState.FaceUp fu)
        {
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        var card = g.CardsById[fu.CardId];
        var sub = _registry?.Resolve(card.EffectFamily);
        if (sub is null)
        {
            g.Log.Write($"  → activate #{fu.CardId} ({card.EffectFamily}): no handler; skip");
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        g.Log.Write($"  → activating #{fu.CardId} on {endNode.Node} (+{st.ChosenCount} bonus gem(s) from arriving transports)");
        var subCtx = new EffectContext
        {
            ActivatingPlayer = ctx.ActivatingPlayer,
            Source = new EffectSource.MapActivation(endNode.Node, fu.CardId),
            TransportBonusGems = st.ChosenCount,
        };
        st.ActivationHandler = sub;
        st.ActivationCtx = subCtx;
        st.Stage = Stage.AwaitingActivation;
        sub.Execute(g, subCtx);
        return MirrorActivation(ctx, st, subCtx);
    }

    private static bool MirrorActivation(EffectContext outer, State st, EffectContext sub)
    {
        if (sub.IsComplete)
        {
            outer.PendingChoice = null;
            st.ActivationHandler = null;
            st.ActivationCtx = null;
            st.Stage = Stage.Done;
            outer.IsComplete = true;
            return true;
        }
        if (sub.Paused && sub.PendingChoice is not null)
        {
            outer.PendingChoice = sub.PendingChoice;
            outer.Paused = true;
            return true;
        }
        outer.IsComplete = true;
        return false;
    }

    private static IReadOnlyList<ShipLocation> LegalCommandOrigins(GameState g, PlayerId mover)
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
            if (Movement.EnumeratePaths(g, mover, sp.Location, 1).Count == 0) continue;
            result.Add(sp.Location);
        }
        return result;
    }

    private static void DestroyEnemyTransportsAt(GameState g, PlayerId mover, NodeId node)
    {
        var victims = g.ShipPlacements
            .Where(sp => sp.Owner != mover &&
                         sp.Location is ShipLocation.OnNode n && n.Node == node)
            .ToList();
        foreach (var v in victims)
        {
            Mechanics.DestroyShipAt(g, v.Owner, v.Location, attackerForPrestige: mover, g.Log);
            if (g.IsGameOver) return;
        }
    }
}
