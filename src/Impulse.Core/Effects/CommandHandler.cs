using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum CommandBoostTarget { MaxFleetSize, MoveCount, FleetCount }

// Per-card command parameters. Ship-type filter constrains which origins
// are legal (Transport→nodes only, Cruiser→gates only, Either→both).
// `MaxFleetSize` is the upper bound on how many ships move together; player
// chooses any count from 1..MaxFleetSize (capped by ships at origin).
// FleetCount > 1 (multi-fleet "apiece" cards) supported: each fleet gets
// its own origin → count → path → execute cycle, with battle/exploration/
// activation per fleet. Same-destination convergence (rulebook p.31 "must
// move to the same card") is enforced via ConvergenceSet narrowing — see
// CompatNodes() and the LegalOrigins / TransitionToPath filters.
// BoostTarget identifies which [N] in the card text gets boosted.
public sealed record CommandParams(
    int MaxFleetSize,
    int MoveCount,
    BuildShipFilter ShipType,
    CommandBoostTarget BoostTarget,
    int FleetCount = 1)
{
    public int FleetSize => MaxFleetSize;
}

public sealed class CommandHandler : IEffectHandler
{
    private readonly EffectRegistry _registry;
    private readonly IReadOnlyDictionary<int, CommandParams> _byCardId;

    public CommandHandler(EffectRegistry registry, IReadOnlyDictionary<int, CommandParams> byCardId)
    {
        _registry = registry;
        _byCardId = byCardId;
    }

    private enum Stage { Start, AwaitingFleet, AwaitingCount, AwaitingPath, ExecutingPath, AwaitingActivation, AwaitingSectorCoreColor, Done }

    private sealed class State
    {
        public Stage Stage;
        public ShipLocation? Origin;
        public int ChosenCount;
        public BattleState? Battle;
        public IReadOnlyList<ShipLocation>? Path;
        public int PathStepIndex;
        public NodeId? ExplorationNode; // non-null while awaiting a face-up pick
        public IEffectHandler? ActivationHandler;
        public EffectContext? ActivationCtx;
        // Multi-fleet support (rulebook p.31): "Each must move to the same
        // card." After fleet 1 chooses its destination we narrow the set of
        // valid destination cards (nodes) to those compatible with its end
        // location: a transport ending OnNode → {that node}; a cruiser
        // ending OnGate → {both gate endpoints}. Subsequent fleets must end
        // at a location compatible with this set (transport on the node, or
        // cruiser on any gate touching it). Each fleet narrows further by
        // intersection.
        public int FleetIndex;
        public int TotalFleets;
        public HashSet<(int, int)> UsedOrigins = new();
        public HashSet<NodeId>? ConvergenceSet;
        // Multi-fleet "to same card" activation deferral (rulebook p.29 +
        // designer ruling 2016-12-26 by Chris Cieslik / Asmadi):
        //   "The move-two-fleets card should move them both together to
        //    the same card, activating it only once."
        // We collect the bonus-gem contributions from each arriving
        // transport fleet but defer the actual activation call until
        // every fleet has finished moving. This also ensures any cruiser
        // fleets in the same command resolve their movement (battles,
        // patrol-through transport destruction) BEFORE the activation
        // sees the resulting board state.
        public NodeId? PendingActivationNode;
        public ShipLocation? PendingActivationLoc;
        public int PendingActivationBonus;
    }

    public bool Execute(GameState g, EffectContext ctx)
    {
        int sourceId = SourceCardId(ctx.Source);
        if (!_byCardId.TryGetValue(sourceId, out var prms))
        {
            g.Log.Write($"  → command: no params for #{sourceId}; noop");
            ctx.IsComplete = true;
            return false;
        }

        var st = (State?)ctx.HandlerState ?? new State { Stage = Stage.Start };
        ctx.HandlerState = st;

        // Sector Core color resume.
        if (st.Stage == Stage.AwaitingSectorCoreColor)
        {
            var creq = (SelectFromOptionsRequest)ctx.PendingChoice!;
            int chosen = creq.Chosen ?? 0;
            ctx.PendingChoice = null;
            // Index into the display order, NOT the enum (which has a
            // different declaration order: Blue=0, Yellow=1, Red=2, Green=3).
            var displayOrder = new[] { CardColor.Red, CardColor.Blue, CardColor.Green, CardColor.Yellow };
            var color = displayOrder[Math.Clamp(chosen, 0, displayOrder.Length - 1)];
            var minerals = g.Player(ctx.ActivatingPlayer).Minerals;
            int gems = minerals.Where(id => g.CardsById[id].Color == color).Sum(id => g.CardsById[id].Size);
            int coreBoost = (gems + st.ChosenCount) / 2;
            int points = 1 + coreBoost;
            g.Log.Write($"  → Sector Core activated as {color}: {gems} gem(s) + {st.ChosenCount} transport(s) → +{points} prestige");
            Scoring.AddPrestige(g, ctx.ActivatingPlayer, points, PrestigeSource.SectorCoreActivatedByTransports, g.Log);
            return CompleteFleet(g, ctx, st);
        }

        // Activation sub-effect in progress? Forward.
        if (st.Stage == Stage.AwaitingActivation && st.ActivationHandler is not null && st.ActivationCtx is not null)
        {
            var subCtx = st.ActivationCtx;
            subCtx.Paused = false;
            st.ActivationHandler.Execute(g, subCtx);
            return MirrorActivation(g, ctx, st, subCtx);
        }

        // Battle in progress? Forward to resolver until done. Per rulebook
        // p.31, a battle ends that fleet's movement — but if the card has
        // more fleets to command, we continue with the next.
        if (st.Battle is not null)
        {
            bool done = BattleResolver.Step(g, ctx, st.Battle);
            if (done)
            {
                st.Battle = null;
                return CompleteFleet(g, ctx, st);
            }
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
                // Fall through to ExecutingPath continuation below.
            }
            else
            {
                // Should always have a pending choice here — defensive.
                ctx.IsComplete = true;
                return false;
            }
        }

        // Boost: applied to the correct knob per rulebook p.22.
        int boost = Boost.FromSource(g, ctx);
        int effectiveMaxFleet = prms.BoostTarget == CommandBoostTarget.MaxFleetSize
            ? prms.MaxFleetSize + boost : prms.MaxFleetSize;
        int effectiveMoves = prms.BoostTarget == CommandBoostTarget.MoveCount
            ? prms.MoveCount + boost : prms.MoveCount;
        int effectiveFleetCount = prms.BoostTarget == CommandBoostTarget.FleetCount
            ? prms.FleetCount + boost : prms.FleetCount;

        if (st.Stage == Stage.Start)
        {
            st.TotalFleets = effectiveFleetCount;
            g.Log.Write($"  → command #{sourceId} ({DescribeShipType(prms.ShipType)} {st.TotalFleets} fleet(s) up to {effectiveMaxFleet}, {effectiveMoves} move(s)" +
                        (boost > 0 ? $", +{boost} boost" : "") + ")");
            var legal = LegalOrigins(g, ctx.ActivatingPlayer, prms, st.UsedOrigins,
                convergenceSet: null, effectiveMoves: effectiveMoves);
            g.Log.Write($"  → command: {legal.Count} legal origin(s) for fleet 1/{st.TotalFleets}");
            if (legal.Count == 0)
            {
                ctx.IsComplete = true;
                return false;
            }
            ctx.PendingChoice = new SelectFleetRequest
            {
                Player = ctx.ActivatingPlayer,
                LegalLocations = legal,
                Prompt = $"Select a {DescribeShipType(prms.ShipType)} fleet (1/{st.TotalFleets}) to Command.",
            };
            st.Stage = Stage.AwaitingFleet;
            ctx.Paused = true;
            return false;
        }

        if (st.Stage == Stage.AwaitingFleet)
        {
            var req = (SelectFleetRequest)ctx.PendingChoice!;
            ctx.PendingChoice = null;
            // SKIP path: player declined to move this fleet (req.AllowSkip
            // and req.Chosen == null). For multi-fleet commands, advance to
            // the next fleet (or fire deferred activation / end if last).
            // For single-fleet commands, the action ends with no movement.
            if (req.Chosen is null)
            {
                g.Log.Write($"  → command: fleet {st.FleetIndex + 1}/{st.TotalFleets} skipped");
                return CompleteFleet(g, ctx, st);
            }
            var origin = req.Chosen;
            st.Origin = origin;

            int shipsHere = Mechanics.CountShipsAt(g, ctx.ActivatingPlayer, origin);
            int maxPick = Math.Min(effectiveMaxFleet, shipsHere);

            if (maxPick <= 1)
            {
                st.ChosenCount = 1;
                st.Stage = Stage.AwaitingPath;
                return TransitionToPath(g, ctx, origin, effectiveMoves);
            }

            ctx.PendingChoice = new SelectFleetSizeRequest
            {
                Player = ctx.ActivatingPlayer,
                Min = 1,
                Max = maxPick,
                Prompt = $"How many ships to move? (1–{maxPick})",
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
            st.Stage = Stage.AwaitingPath;
            return TransitionToPath(g, ctx, st.Origin!, effectiveMoves);
        }

        if (st.Stage == Stage.AwaitingPath)
        {
            var req = (DeclareMoveRequest)ctx.PendingChoice!;
            var path = req.ChosenPath ?? throw new InvalidOperationException("path not chosen");
            ctx.PendingChoice = null;
            // Empty path = "stay" (no movement). Skip convergence narrowing
            // and let CompleteFleet finalize this fleet without movement.
            if (path.Count == 0)
            {
                g.Log.Write($"  → fleet stays at {Mechanics.LocStr(st.Origin!)}");
                return CompleteFleet(g, ctx, st);
            }
            st.Path = path;
            st.PathStepIndex = 0;
            st.Stage = Stage.ExecutingPath;
            // Multi-fleet convergence: narrow the convergence set by the
            // chosen path's endpoint compatibility.
            if (st.TotalFleets > 1)
            {
                var endCompat = CompatNodes(g, path[^1]);
                st.ConvergenceSet = st.ConvergenceSet is null
                    ? endCompat
                    : new HashSet<NodeId>(st.ConvergenceSet.Intersect(endCompat));
            }
        }

        if (st.Stage == Stage.ExecutingPath)
        {
            return ContinuePath(g, ctx, st);
        }

        return false;
    }

    private bool ContinuePath(GameState g, EffectContext ctx, State st)
    {
        var path = st.Path!;
        // Walk path from current step. May suspend for exploration or battle.
        var here = st.PathStepIndex == 0 ? st.Origin! : path[st.PathStepIndex - 1];

        while (st.PathStepIndex < path.Count)
        {
            var step = path[st.PathStepIndex];

            // Determine exploration trigger: transport landing on face-down,
            // or cruiser passing through face-down.
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

            // Battle / passage destruction (cruiser movement only).
            if (here is ShipLocation.OnGate fromGate && step is ShipLocation.OnGate toGate)
            {
                var pass = Movement.SharedNode(g.Map, fromGate.Gate, toGate.Gate);
                if (pass is { } passageNode &&
                    Movement.IsPatrolledByEnemy(g, ctx.ActivatingPlayer, passageNode))
                {
                    var defender = DefenderChoice.Resolve(g, ctx,
                        FindPatrollers(g, ctx.ActivatingPlayer, passageNode),
                        $"Multiple players patrol {passageNode} — choose who to fight.");
                    if (defender is null) return true; // paused for choice
                    st.Battle = SetupBattlePatrolThrough(g, ctx, fromGate, toGate, passageNode, st.ChosenCount, defender.Value);
                    if (BattleResolver.Step(g, ctx, st.Battle))
                    { st.Battle = null; return CompleteFleet(g, ctx, st); }
                    return true;
                }
                if (Movement.HasEnemyCruiserOnGate(g, ctx.ActivatingPlayer, toGate.Gate))
                {
                    var defender = DefenderChoice.Resolve(g, ctx,
                        FindEnemiesOnGate(g, ctx.ActivatingPlayer, toGate.Gate),
                        $"Multiple players have cruisers on {toGate.Gate} — choose who to fight.");
                    if (defender is null) return true; // paused for choice
                    st.Battle = SetupBattleMoveOnto(g, ctx, fromGate, toGate, st.ChosenCount, defender.Value);
                    if (BattleResolver.Step(g, ctx, st.Battle))
                    { st.Battle = null; return CompleteFleet(g, ctx, st); }
                    return true;
                }
                if (pass is { } pn2)
                    DestroyEnemyTransportsOn(g, ctx.ActivatingPlayer, pn2);
                if (g.IsGameOver) { st.Stage = Stage.Done; ctx.IsComplete = true; return true; }
            }

            for (int s = 0; s < st.ChosenCount; s++)
                Mechanics.MoveShip(g, ctx.ActivatingPlayer, here, step, g.Log);
            here = step;
            st.PathStepIndex++;
        }
        // Single-fleet commands activate immediately. Multi-fleet commands
        // defer activation until ALL fleets have finished moving — see
        // PendingActivationNode docstring on State for rationale.
        if (st.TotalFleets > 1)
        {
            return CaptureDeferredActivationAndComplete(g, ctx, st, here);
        }
        return TryStartActivation(g, ctx, st, here);
    }

    // Multi-fleet path: a transport landing on a face-up card contributes
    // to the deferred activation; cruisers and transports landing on
    // non-face-up locations contribute nothing. Activation is fired in
    // CompleteFleet once every fleet has resolved.
    private bool CaptureDeferredActivationAndComplete(
        GameState g, EffectContext ctx, State st, ShipLocation finalLoc)
    {
        if (finalLoc is ShipLocation.OnNode endNode &&
            g.NodeCards.TryGetValue(endNode.Node, out var nodeState) &&
            (nodeState is NodeCardState.FaceUp || nodeState is NodeCardState.SectorCore))
        {
            // Skip if origin is the same node (cards started-on don't activate).
            bool startedOnSameNode =
                st.Origin is ShipLocation.OnNode origNode && origNode.Node == endNode.Node;
            if (!startedOnSameNode)
            {
                if (st.PendingActivationNode is null)
                {
                    st.PendingActivationNode = endNode.Node;
                    st.PendingActivationLoc = finalLoc;
                    st.PendingActivationBonus = st.ChosenCount;
                }
                else if (st.PendingActivationNode == endNode.Node)
                {
                    // Convergence — accumulate bonus gems from this fleet.
                    st.PendingActivationBonus += st.ChosenCount;
                }
                // Different node shouldn't happen with rulebook-correct
                // convergence enforcement; if it does, stick with first.
            }
        }
        return CompleteFleet(g, ctx, st);
    }

    // Rulebook p.27/p.29: When transports end on a face-up sector card other
    // than the one they started on, they may activate that card; the just-
    // moved transports count as bonus matching gems for boost.
    private bool TryStartActivation(GameState g, EffectContext ctx, State st, ShipLocation finalLoc)
    {
        if (finalLoc is not ShipLocation.OnNode endNode)
        {
            return CompleteFleet(g, ctx, st);
        }
        // Origin must be a different node (or a gate); cards started-on
        // can't be activated.
        if (st.Origin is ShipLocation.OnNode origNode && origNode.Node == endNode.Node)
        {
            return CompleteFleet(g, ctx, st);
        }
        if (!g.NodeCards.TryGetValue(endNode.Node, out var nodeState))
        {
            return CompleteFleet(g, ctx, st);
        }
        if (nodeState is NodeCardState.SectorCore)
        {
            // Rulebook p.27: player chooses mineral color for boost; score
            // 1 + boost. Prompt with per-color gem counts so the player can
            // make an informed choice.
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
            return CompleteFleet(g, ctx, st);
        }
        var card = g.CardsById[fu.CardId];
        var sub = _registry.Resolve(card.EffectFamily);
        if (sub is null)
        {
            g.Log.Write($"  → activate #{fu.CardId} ({card.EffectFamily}): no handler; skip");
            return CompleteFleet(g, ctx, st);
        }
        // Cap activation chain depth so a series of transports landing on
        // Command cards (each moving more transports onto more Command
        // cards) can't recurse without bound.
        const int MaxActivationDepth = 4;
        if (ctx.ActivationDepth >= MaxActivationDepth)
        {
            g.Log.Write($"  → activate #{fu.CardId}: max chain depth ({MaxActivationDepth}) reached; skip");
            return CompleteFleet(g, ctx, st);
        }
        g.Log.Write($"  → activating #{fu.CardId} on {endNode.Node} (+{st.ChosenCount} bonus gems from arriving transports)");
        var subCtx = new EffectContext
        {
            ActivatingPlayer = ctx.ActivatingPlayer,
            Source = new EffectSource.MapActivation(endNode.Node, fu.CardId),
            TransportBonusGems = st.ChosenCount,
            ActivationDepth = ctx.ActivationDepth + 1,
        };
        st.ActivationHandler = sub;
        st.ActivationCtx = subCtx;
        st.Stage = Stage.AwaitingActivation;
        sub.Execute(g, subCtx);
        return MirrorActivation(g, ctx, st, subCtx);
    }

    private bool MirrorActivation(GameState g, EffectContext outer, State st, EffectContext sub)
    {
        if (sub.IsComplete)
        {
            outer.PendingChoice = null;
            st.ActivationHandler = null;
            st.ActivationCtx = null;
            return CompleteFleet(g, outer, st);
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

    private bool CompleteFleet(GameState g, EffectContext ctx, State st)
    {
        // Rulebook p.31: "you cannot move the same fleet twice." Exclude
        // both the origin (those ships are gone now) and the destination
        // (those ships are the same fleet that just moved). This prevents
        // the same ships from being commanded again as a later fleet just
        // because they're at a new location.
        static (int, int) KeyOf(ShipLocation l) => l switch
        {
            ShipLocation.OnNode n => (0, n.Node.Value),
            ShipLocation.OnGate gateLoc => (1, gateLoc.Gate.Value),
            _ => (-1, 0),
        };
        if (st.Origin is { } o)
            st.UsedOrigins.Add(KeyOf(o));
        if (st.Path is { Count: > 0 } pth)
            st.UsedOrigins.Add(KeyOf(pth[^1]));
        st.FleetIndex++;
        if (g.IsGameOver)
        {
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        if (st.FleetIndex >= st.TotalFleets)
        {
            // All fleets done. If a multi-fleet activation was deferred,
            // fire it now using the accumulated bonus from every transport
            // fleet that converged on the activation card. Cruiser fleets
            // have already resolved (their battles / patrol-through
            // transport destruction completed inside ContinuePath), so the
            // activation sees the final post-movement board state.
            if (st.PendingActivationNode is not null && st.PendingActivationLoc is { } pendingLoc)
            {
                st.ChosenCount = st.PendingActivationBonus;
                st.PendingActivationNode = null;
                st.PendingActivationLoc = null;
                st.PendingActivationBonus = 0;
                return TryStartActivation(g, ctx, st, pendingLoc);
            }
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        // Reset per-fleet state and prompt for next origin.
        st.Origin = null;
        st.ChosenCount = 0;
        st.Path = null;
        st.PathStepIndex = 0;
        st.Battle = null;
        st.ExplorationNode = null;
        st.ActivationHandler = null;
        st.ActivationCtx = null;

        int sourceId = SourceCardId(ctx.Source);
        var prms = _byCardId[sourceId];
        // Snapshot effective moves (boost is fixed at action start per FAQ).
        int boost = Boost.FromSource(g, ctx);
        int effectiveMoves = prms.BoostTarget == CommandBoostTarget.MoveCount
            ? prms.MoveCount + boost : prms.MoveCount;
        var legal = LegalOrigins(g, ctx.ActivatingPlayer, prms, st.UsedOrigins,
            convergenceSet: st.ConvergenceSet, effectiveMoves: effectiveMoves);
        if (legal.Count == 0)
        {
            g.Log.Write($"  → command: no legal origins for fleet {st.FleetIndex + 1}/{st.TotalFleets} (convergence: [{(st.ConvergenceSet is null ? "none" : string.Join(",", st.ConvergenceSet))}])");
            // No more fleets can move, but if an earlier fleet queued a
            // deferred activation we still need to fire it. Otherwise the
            // player loses the SectorCore / face-up activation they paid
            // for by getting transports onto the destination card.
            if (st.PendingActivationNode is not null && st.PendingActivationLoc is { } pendingLoc)
            {
                st.ChosenCount = st.PendingActivationBonus;
                st.PendingActivationNode = null;
                st.PendingActivationLoc = null;
                st.PendingActivationBonus = 0;
                return TryStartActivation(g, ctx, st, pendingLoc);
            }
            st.Stage = Stage.Done;
            ctx.IsComplete = true;
            return true;
        }
        ctx.PendingChoice = new SelectFleetRequest
        {
            Player = ctx.ActivatingPlayer,
            LegalLocations = legal,
            Prompt = $"Select fleet {st.FleetIndex + 1}/{st.TotalFleets}.",
        };
        st.Stage = Stage.AwaitingFleet;
        ctx.Paused = true;
        return true;
    }

    private bool TransitionToPath(GameState g, EffectContext ctx, ShipLocation origin, int effectiveMoves)
    {
        var st = (State)ctx.HandlerState!;
        var paths = Movement.EnumeratePaths(g, ctx.ActivatingPlayer, origin, effectiveMoves);
        // Multi-fleet convergence: subsequent fleets must end at a location
        // compatible with the convergence set established by prior fleets.
        if (st.ConvergenceSet is { } cset)
            paths = paths.Where(p => CompatNodes(g, p[^1]).Overlaps(cset)).ToList();
        if (paths.Count == 0)
        {
            g.Log.Write($"  → no legal paths from {Mechanics.LocStr(origin)}");
            return CompleteFleet(g, ctx, st);
        }
        string prompt = $"Declare a path of up to {effectiveMoves} move(s)";
        if (st.ConvergenceSet is { } cs2)
            prompt += $" — must converge on card(s) [{string.Join(", ", cs2)}]";
        ctx.PendingChoice = new DeclareMoveRequest
        {
            Player = ctx.ActivatingPlayer,
            Origin = origin,
            MaxMoves = effectiveMoves,
            LegalPaths = paths,
            Prompt = prompt + ".",
        };
        ctx.Paused = true;
        return true;
    }

    // Compatibility set: which "card"(s) (= node ids) a location is associated
    // with. A transport on N → {N}; a cruiser on gate G → {gate.EndpointA,
    // gate.EndpointB} (gate touches two cards; either can serve as the
    // convergence card).
    private static HashSet<NodeId> CompatNodes(GameState g, ShipLocation loc) =>
        loc switch
        {
            ShipLocation.OnNode n => new HashSet<NodeId> { n.Node },
            ShipLocation.OnGate gloc => new HashSet<NodeId>
            {
                g.Map.Gate(gloc.Gate).EndpointA,
                g.Map.Gate(gloc.Gate).EndpointB,
            },
            _ => new HashSet<NodeId>(),
        };

    internal static BattleState SetupBattleMoveOnto(GameState g, EffectContext ctx,
        ShipLocation.OnGate fromGate, ShipLocation.OnGate toGate, int attackerCount,
        PlayerId defender)
    {
        return new BattleState
        {
            Attacker = ctx.ActivatingPlayer,
            Defender = defender,
            BattleGate = toGate.Gate,
            AttackerOrigin = fromGate,
            AttackerCruiserCount = attackerCount,
        };
    }

    internal static BattleState SetupBattlePatrolThrough(GameState g, EffectContext ctx,
        ShipLocation.OnGate fromGate, ShipLocation.OnGate toGate,
        NodeId passageNode, int attackerCount, PlayerId defender)
    {
        // Battle happens at `toGate` — the destination the player actually
        // chose. The path filter in Movement.Walk guarantees toGate already
        // contains an enemy cruiser when the passage is patrolled, so this
        // is a real attack. PassageNode is tracked separately so transports
        // on the traversed card get destroyed when the attacker wins
        // (rulebook p.28).
        //
        // Defender choice (multi-patroller scenarios) and toGate are
        // independent concerns: with the path-filter rule, the player picks
        // WHICH enemy gate to attack by clicking that gate; the defender
        // candidates list is "all enemies on toGate" not "all enemies
        // patrolling the passage." Net effect: clicking gate Y with enemy
        // P5 on it always battles P5 at Y, never some other patroller.
        return new BattleState
        {
            Attacker = ctx.ActivatingPlayer,
            Defender = defender,
            BattleGate = toGate.Gate,
            AttackerOrigin = fromGate,
            AttackerCruiserCount = attackerCount,
            PassageNode = passageNode,
        };
    }

    // Find all distinct enemy players who patrol `passageNode` (have a
    // cruiser on any gate touching it).
    internal static List<PlayerId> FindPatrollers(GameState g, PlayerId mover, NodeId passageNode)
    {
        var result = new HashSet<PlayerId>();
        foreach (var gate in g.Map.AdjacencyByNode[passageNode])
            foreach (var sp in g.ShipPlacements)
                if (sp.Owner != mover &&
                    sp.Location is ShipLocation.OnGate og && og.Gate == gate.Id)
                    result.Add(sp.Owner);
        return result.OrderBy(p => p.Value).ToList();
    }

    // Find all distinct enemy players with a cruiser on the given gate.
    internal static List<PlayerId> FindEnemiesOnGate(GameState g, PlayerId mover, GateId gate)
    {
        return g.ShipPlacements
            .Where(sp => sp.Owner != mover &&
                         sp.Location is ShipLocation.OnGate og && og.Gate == gate)
            .Select(sp => sp.Owner)
            .Distinct()
            .OrderBy(p => p.Value)
            .ToList();
    }


    private static void DestroyEnemyTransportsOn(GameState g, PlayerId mover, NodeId node)
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

    private static IReadOnlyList<ShipLocation> LegalOrigins(GameState g, PlayerId mover, CommandParams prms,
        IReadOnlySet<(int, int)>? exclude = null,
        IReadOnlySet<NodeId>? convergenceSet = null,
        int? effectiveMoves = null)
    {
        var result = new List<ShipLocation>();
        var seen = new HashSet<(int, int)>();
        int moves = effectiveMoves ?? prms.MoveCount;

        foreach (var sp in g.ShipPlacements.Where(s => s.Owner == mover))
        {
            bool isNode = sp.Location is ShipLocation.OnNode;
            // Ship-type filter
            if (isNode && prms.ShipType == BuildShipFilter.CruiserOnly) continue;
            if (!isNode && prms.ShipType == BuildShipFilter.TransportOnly) continue;

            var key = sp.Location switch
            {
                ShipLocation.OnNode n => (0, n.Node.Value),
                ShipLocation.OnGate gateLoc => (1, gateLoc.Gate.Value),
                _ => (-1, 0),
            };
            if (!seen.Add(key)) continue;
            if (exclude is not null && exclude.Contains(key)) continue;

            // Need ≥ 1 matching ship at this location (player can move
            // any count up to prms.MaxFleetSize, capped by ships present).
            int count = Mechanics.CountShipsAt(g, mover, sp.Location);
            if (count < 1) continue;

            // Must have at least one legal step (filtered by convergence set
            // for multi-fleet "same destination card").
            var paths = Movement.EnumeratePaths(g, mover, sp.Location, moves);
            if (convergenceSet is not null)
                paths = paths.Where(p => CompatNodes(g, p[^1]).Overlaps(convergenceSet)).ToList();
            if (paths.Count == 0) continue;

            result.Add(sp.Location);
        }
        return result;
    }

    private static string DescribeShipType(BuildShipFilter f) => f switch
    {
        BuildShipFilter.TransportOnly => "Transport",
        BuildShipFilter.CruiserOnly => "Cruiser",
        _ => "ship",
    };

    private static int SourceCardId(EffectSource src) => src switch
    {
        EffectSource.ImpulseCard ic => ic.CardId,
        EffectSource.PlanCard pc => pc.CardId,
        EffectSource.TechEffect te => te.CardId ?? 0,
        EffectSource.MapActivation ma => ma.CardId,
        _ => 0,
    };
}

public static class CommandRegistrations
{
    // Per-card params. ShipType reuses BuildShipFilter (same three-state set).
    // FleetCount>1 cards (c75, c98) move multiple fleets, each independently
    // chosen. Same-destination "apiece" rule is enforced (see ConvergenceSet
    // narrowing in CommandHandler.Execute / TransitionToPath).
    public static readonly Dictionary<int, CommandParams> ByCardId = new()
    {
        [4]   = new(MaxFleetSize:1, MoveCount: 1, ShipType: BuildShipFilter.Either, BoostTarget: CommandBoostTarget.MaxFleetSize),
        [6]   = new(MaxFleetSize:2, MoveCount: 1, ShipType: BuildShipFilter.CruiserOnly, BoostTarget: CommandBoostTarget.MaxFleetSize),
        // BoostTarget points at the [N] in each card's text:
        //   "[N] ship/cruiser/transport fleet for one/two move(s)" → MaxFleetSize
        //   "(one|two) ship/cruiser/transport fleet for [N] move(s)" → MoveCount
        //   "one cruiser/transport for [N] move"                    → MoveCount
        //   "[N] fleet[s]" (multi-fleet)                            → FleetCount
        //
        // For "[N] fleet" cards, MaxFleetSize is set to 12 (ships-at-origin cap).
        [4]   = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MaxFleetSize),
        [6]   = new(MaxFleetSize:2,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MaxFleetSize),
        [12]  = new(MaxFleetSize:12, MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.FleetCount,    FleetCount:1),
        [13]  = new(MaxFleetSize:2,  MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MoveCount),
        [26]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MaxFleetSize),
        [31]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MoveCount),
        [35]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MoveCount),
        [38]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MoveCount),
        [39]  = new(MaxFleetSize:2,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MoveCount),
        [40]  = new(MaxFleetSize:1,  MoveCount:2, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MaxFleetSize),
        [41]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MaxFleetSize),
        [42]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MaxFleetSize),
        [44]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MoveCount),
        [55]  = new(MaxFleetSize:2,  MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MoveCount),
        [56]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MaxFleetSize),
        [69]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MoveCount),
        [70]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.CruiserOnly,   BoostTarget:CommandBoostTarget.MoveCount),
        [71]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MaxFleetSize),
        [75]  = new(MaxFleetSize:12, MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.FleetCount,    FleetCount:2),
        [78]  = new(MaxFleetSize:12, MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.FleetCount,    FleetCount:1),
        [81]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MoveCount),
        [88]  = new(MaxFleetSize:2,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MoveCount),
        [91]  = new(MaxFleetSize:1,  MoveCount:2, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MaxFleetSize),
        [94]  = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.TransportOnly, BoostTarget:CommandBoostTarget.MoveCount),
        [98]  = new(MaxFleetSize:12, MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.FleetCount,    FleetCount:2),
        [100] = new(MaxFleetSize:1,  MoveCount:1, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MaxFleetSize),
        [105] = new(MaxFleetSize:1,  MoveCount:2, ShipType:BuildShipFilter.Either,        BoostTarget:CommandBoostTarget.MaxFleetSize),
    };

    private static readonly string[] Families =
    {
        "command_n_ship_fleet_one_move",
        "command_n_cruiser_fleet_one_move",
        "command_n_transport_fleet_one_move",
        "command_one_cruiser_n_moves",
        "command_one_transport_n_moves",
        "command_n_fleets_one_move_same_card",
        "command_two_ship_fleet_n_moves",
        "command_two_cruiser_fleet_n_moves",
        "command_two_transport_fleet_n_moves",
        "command_n_ship_fleet_two_moves",
        "command_n_transport_fleet_two_moves",
    };

    public static void RegisterAll(EffectRegistry r)
    {
        var handler = new CommandHandler(r, ByCardId);
        foreach (var f in Families) r.Register(f, handler);
    }
}
