using Impulse.Core.Effects;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Core.Controllers;

// 1-ply lookahead controller. For each legal PlayerAction the engine
// offers, clones the GameState, applies the action, simulates a few
// more turns with all seats using a baseline policy, and scores the
// resulting position. Picks the action with the highest expected score.
//
// Mid-effect ChoiceRequest answers are still delegated to the wrapped
// "self" policy — lookahead only kicks in at top-level PickAction calls
// (place-impulse, use/skip impulse card, use/skip tech, use/skip plan).
//
// Cost: roughly `legal_actions × sim_turns × turns/game` policy steps
// per real decision. With the baseline PolicyController at ~86 games/s,
// expect the lookahead AI to run ~5-10x slower in tournament. Still
// bench-friendly at default settings.
public sealed class LookaheadController : IPlayerController
{
    private readonly Random _rng;
    private readonly PolicyController _selfPolicy;
    private readonly AiPolicy _opponentPolicy;
    private readonly int _simTurns;
    private readonly int _seed;

    public PlayerId Seat { get; }

    public LookaheadController(
        PlayerId seat,
        int seed,
        AiPolicy myPolicy,
        AiPolicy opponentPolicy = AiPolicy.Greedy,
        int simTurns = 4)
    {
        Seat = seat;
        _seed = seed;
        _rng = new Random(seed);
        _selfPolicy = new PolicyController(seat, seed, myPolicy);
        _opponentPolicy = opponentPolicy;
        _simTurns = simTurns;
    }

    public PlayerAction PickAction(GameState g, IReadOnlyList<PlayerAction> legal)
    {
        if (legal.Count == 1) return legal[0];

        // Clone + simulate every candidate. Pick the action with the highest
        // resulting evaluation score (random tiebreak among equals).
        int bestScore = int.MinValue;
        var bestActions = new List<PlayerAction>();
        foreach (var action in legal)
        {
            int score = SimulateScore(g, action);
            if (score > bestScore)
            {
                bestScore = score;
                bestActions.Clear();
                bestActions.Add(action);
            }
            else if (score == bestScore)
            {
                bestActions.Add(action);
            }
        }
        return bestActions[_rng.Next(bestActions.Count)];
    }

    private int SimulateScore(GameState g, PlayerAction firstAction)
    {
        var clone = g.Clone();
        var registry = SharedRegistry.Get();

        // Controllers: the seat under consideration uses a one-shot
        // scripted-then-policy controller (returns `firstAction` exactly
        // once at the next PickAction; defers to baseline thereafter).
        // Other seats run the baseline opponent policy.
        var controllers = new List<IPlayerController>(clone.Players.Count);
        foreach (var p in clone.Players)
        {
            int subSeed = _seed * 1009 + p.Id.Value;
            if (p.Id == Seat)
            {
                var inner = new PolicyController(p.Id, subSeed, _opponentPolicy);
                controllers.Add(new ScriptedThenPolicyController(p.Id, firstAction, inner));
            }
            else
            {
                controllers.Add(new PolicyController(p.Id, subSeed, _opponentPolicy));
            }
        }

        var runner = new GameRunner(clone, registry, controllers);
        runner.RunUntilDone(maxTurns: _simTurns);

        return ScorePosition(clone);
    }

    // Position evaluation for the simulating seat. Pure prestige delta is
    // the dominant signal; small material bonuses break ties between
    // prestige-equal positions and discourage the AI from sacrificing too
    // many ships for a marginal gain.
    private int ScorePosition(GameState g)
    {
        int myPrestige = g.Player(Seat).Prestige;
        int maxOpp = g.Players
            .Where(p => p.Id != Seat)
            .Select(p => p.Prestige)
            .DefaultIfEmpty(0)
            .Max();
        int prestigeDelta = myPrestige - maxOpp;

        int myShips = g.ShipPlacements.Count(sp => sp.Owner == Seat);
        int maxOppShips = g.Players
            .Where(p => p.Id != Seat)
            .Select(p => g.ShipPlacements.Count(sp => sp.Owner == p.Id))
            .DefaultIfEmpty(0)
            .Max();

        // Heuristic weights: prestige is by far the biggest term so the
        // simulator strongly prefers actually scoring points; minor terms
        // for ships, hand, minerals to break ties.
        return 100 * prestigeDelta
             + 5 * (myShips - maxOppShips)
             + g.Player(Seat).Hand.Count
             + g.Player(Seat).Minerals.Count;
    }

    public void AnswerChoice(GameState g, ChoiceRequest request) =>
        _selfPolicy.AnswerChoice(g, request);

    private sealed class ScriptedThenPolicyController : IPlayerController
    {
        private PlayerAction? _first;
        private readonly IPlayerController _inner;
        public PlayerId Seat { get; }
        public ScriptedThenPolicyController(PlayerId seat, PlayerAction first, IPlayerController inner)
        {
            Seat = seat;
            _first = first;
            _inner = inner;
        }
        public PlayerAction PickAction(GameState g, IReadOnlyList<PlayerAction> legal)
        {
            if (_first is { } a)
            {
                _first = null;
                // Defensive: if the runner offers a different legal set than
                // we anticipated (race conditions with cloned state), fall
                // back to the inner policy.
                if (legal.Any(x => x.Equals(a))) return a;
                return _inner.PickAction(g, legal);
            }
            return _inner.PickAction(g, legal);
        }
        public void AnswerChoice(GameState g, ChoiceRequest request) =>
            _inner.AnswerChoice(g, request);
    }

    // EffectRegistry is expensive to construct repeatedly. Cache one
    // shared instance for all simulations across this process.
    private static class SharedRegistry
    {
        private static readonly EffectRegistry _instance = Build();
        public static EffectRegistry Get() => _instance;
        private static EffectRegistry Build()
        {
            var r = new EffectRegistry();
            CommandRegistrations.RegisterAll(r);
            BuildRegistrations.RegisterAll(r);
            MineRegistrations.RegisterAll(r);
            RefineRegistrations.RegisterAll(r);
            DrawRegistrations.RegisterAll(r);
            TradeRegistrations.RegisterAll(r);
            PlanRegistrations.RegisterAll(r);
            ResearchRegistrations.RegisterAll(r);
            SabotageRegistrations.RegisterAll(r);
            ExecuteRegistrations.RegisterAll(r);
            return r;
        }
    }
}
