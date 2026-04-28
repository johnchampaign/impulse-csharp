using Impulse.Core.Cards;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

public sealed class GameRunner
{
    private readonly GameState _g;
    private readonly EffectRegistry _registry;
    private readonly Dictionary<PlayerId, IPlayerController> _controllers;

    public GameState State => _g;
    public int ImpulseCap { get; init; } = 3;
    public int CleanupDraw { get; init; } = 2;

    // Encoded state hook: returns a one-line codec string for the current
    // GameState (closure provides the seed/playerCount/AI policies the
    // engine doesn't know about). When set, the runner emits a `[state
    // turn=N] <encoded>` line at the start of every turn so a save/restore
    // is always available from the log.
    public Func<GameState, string?>? StateEncoder { get; init; }

    public GameRunner(GameState g, EffectRegistry registry, IEnumerable<IPlayerController> controllers)
    {
        _g = g;
        _registry = registry;
        _controllers = controllers.ToDictionary(c => c.Seat);
    }

    public void RunUntilDone(int maxTurns = 1000)
    {
        if (!_g.HomePicksDone) RunHomePicks();
        for (int t = 0; t < maxTurns && !_g.IsGameOver; t++)
        {
            StepOneTurn();
        }
    }

    // Rulebook p.3: "Draw 5 cards plus the card on your Home location, and
    // choose one of the six to place face-up there." SetupFactory pre-deals
    // a default home card (face-up); here we let each player either keep
    // it or swap with one from hand.
    private void RunHomePicks()
    {
        foreach (var p in _g.Players)
        {
            var homeId = _g.Map.HomeNodeIds[p.Id];
            if (_g.NodeCards.TryGetValue(homeId, out var st) && st is NodeCardState.FaceUp fu)
            {
                int currentHomeCard = fu.CardId;
                // Temporarily move the home card into hand so the UI's hand
                // strip shows all 6 cards (the human picks via clicking a hand
                // card; clicking the map hex isn't wired for this prompt).
                p.Hand.Add(currentHomeCard);
                _g.NodeCards.Remove(homeId);
                var req = new SelectHandCardRequest
                {
                    Player = p.Id,
                    LegalCardIds = p.Hand.ToList(),
                    AllowNone = false,
                    Prompt = $"Pick one of your 6 cards to place face-up on Home ({homeId}). " +
                             $"Default was #{currentHomeCard}.",
                };
                _controllers[p.Id].AnswerChoice(_g, req);
                int picked = req.ChosenCardId ?? currentHomeCard;
                if (!p.Hand.Remove(picked))
                    throw new InvalidOperationException($"home pick: hand missing #{picked}");
                _g.NodeCards[homeId] = new NodeCardState.FaceUp(picked);
                _g.Log.Write($"  → {p.Id} home {homeId} = #{picked}");
            }
        }
        _g.HomePicksDone = true;
    }

    public void StepOneTurn()
    {
        if (_g.IsGameOver) return;
        if (StateEncoder is { } enc)
        {
            var s = enc(_g);
            if (!string.IsNullOrEmpty(s))
                _g.Log.Write($"[state turn={_g.CurrentTurn} active={_g.ActivePlayer.Value}] {s}");
        }
        Phase1_AddImpulse();
        if (_g.IsGameOver) return;
        Phase2_UseTech();
        if (_g.IsGameOver) return;
        Phase3_ResolveImpulse();
        if (_g.IsGameOver) return;
        Phase4_UsePlan();
        if (_g.IsGameOver) return;
        Phase5_Score();
        if (_g.IsGameOver) return;
        Phase6_Cleanup();
    }

    private void Phase1_AddImpulse()
    {
        _g.Phase = GamePhase.AddImpulse;
        var p = _g.Player(_g.ActivePlayer);
        if (p.Hand.Count == 0)
        {
            _g.Log.Write($"— {_g.ActivePlayer} turn {_g.CurrentTurn} phase {_g.Phase} (skipped: empty hand)");
            return;
        }
        _g.Log.Write($"— {_g.ActivePlayer} turn {_g.CurrentTurn} phase {_g.Phase}");
        var legal = p.Hand
            .Select(id => (PlayerAction)new PlayerAction.PlaceImpulse(id))
            .ToList();
        var action = (PlayerAction.PlaceImpulse)_controllers[_g.ActivePlayer].PickAction(_g, legal);
        Mechanics.PlaceOnImpulse(_g, _g.ActivePlayer, action.CardIdFromHand, _g.Log);
    }

    private void Phase2_UseTech()
    {
        _g.Phase = GamePhase.UseTech;
        var p = _g.Player(_g.ActivePlayer);

        var legal = new List<PlayerAction>
        {
            new PlayerAction.SkipTech(),
            new PlayerAction.UseTech(TechSlot.Left),
            new PlayerAction.UseTech(TechSlot.Right),
        };
        var action = _controllers[_g.ActivePlayer].PickAction(_g, legal);
        if (action is PlayerAction.SkipTech) return;
        var use = (PlayerAction.UseTech)action;
        var tech = p.Techs[use.Slot];
        _g.Log.Write($"  Phase2: {_g.ActivePlayer} uses tech {use.Slot} ({tech.GetType().Name})");

        switch (tech)
        {
            case Tech.Researched r:
                {
                    var card = _g.CardsById[r.CardId];
                    var handler = _registry.Resolve(card.EffectFamily);
                    if (handler is null)
                    {
                        _g.Log.Write($"  tech #{r.CardId} ({card.EffectFamily}) — no handler, skip");
                        return;
                    }
                    var ctx = new EffectContext
                    {
                        ActivatingPlayer = _g.ActivePlayer,
                        // Pass the card id so per-card-param handlers can resolve params.
                        Source = new EffectSource.TechEffect(use.Slot, r.CardId),
                    };
                    _g.PendingEffect = ctx;
                    RunEffectToCompletion(handler, ctx);
                    _g.PendingEffect = null;
                    break;
                }
            case Tech.BasicCommon:
                {
                    var ctx = new EffectContext
                    {
                        ActivatingPlayer = _g.ActivePlayer,
                        Source = new EffectSource.TechEffect(use.Slot),
                    };
                    _g.PendingEffect = ctx;
                    RunEffectToCompletion(new Effects.BasicCommonTechHandler(_registry), ctx);
                    _g.PendingEffect = null;
                    break;
                }
            case Tech.BasicUnique bu:
                {
                    IEffectHandler? handler = bu.Race.Id switch
                    {
                        1 => new Effects.PiscesishTechHandler(),
                        2 => new Effects.AriekTechHandler(),
                        3 => new Effects.HerculeseTechHandler(),
                        4 => new Effects.DraconiansTechHandler(),
                        5 => new Effects.TriangulumnistsTechHandler(),
                        6 => new Effects.CaelumnitesTechHandler(),
                        _ => null,
                    };
                    if (handler is null)
                    {
                        _g.Log.Write($"  → BasicUnique({bu.Race.Name}) effect deferred (requires exploration)");
                        return;
                    }
                    var ctx = new EffectContext
                    {
                        ActivatingPlayer = _g.ActivePlayer,
                        Source = new EffectSource.TechEffect(use.Slot),
                    };
                    _g.PendingEffect = ctx;
                    RunEffectToCompletion(handler, ctx);
                    _g.PendingEffect = null;
                    break;
                }
        }
    }

    private void Phase3_ResolveImpulse()
    {
        _g.Phase = GamePhase.ResolveImpulse;
        _g.ImpulseCursor = 0;
        while (_g.ImpulseCursor < _g.Impulse.Count && !_g.IsGameOver)
        {
            var cardId = _g.Impulse[_g.ImpulseCursor];
            var card = _g.CardsById[cardId];
            var handler = _registry.Resolve(card.EffectFamily);
            if (handler is null)
            {
                _g.Log.Write($"  cursor=[{_g.ImpulseCursor}] #{cardId} ({card.EffectFamily}) — no handler, auto-skip");
                _g.ImpulseCursor++;
                continue;
            }

            var legal = new List<PlayerAction>
            {
                new PlayerAction.UseImpulseCard(),
                new PlayerAction.SkipImpulseCard(),
            };
            var action = _controllers[_g.ActivePlayer].PickAction(_g, legal);
            if (action is PlayerAction.SkipImpulseCard)
            {
                _g.Log.Write($"  cursor=[{_g.ImpulseCursor}] #{cardId} skipped by {_g.ActivePlayer}");
                _g.ImpulseCursor++;
                continue;
            }

            _g.Log.Write($"  cursor=[{_g.ImpulseCursor}] #{cardId} used by {_g.ActivePlayer} ({card.ActionType})");
            var ctx = new EffectContext
            {
                ActivatingPlayer = _g.ActivePlayer,
                Source = new EffectSource.ImpulseCard(cardId),
            };
            _g.PendingEffect = ctx;
            RunEffectToCompletion(handler, ctx);
            _g.PendingEffect = null;
            _g.ImpulseCursor++;
        }
    }

    private void RunEffectToCompletion(IEffectHandler handler, EffectContext ctx)
    {
        while (!ctx.IsComplete && !_g.IsGameOver)
        {
            ctx.Paused = false;
            handler.Execute(_g, ctx);
            if (ctx.IsComplete) break;
            if (ctx.Paused && ctx.PendingChoice is not null)
            {
                var asker = ctx.PendingChoice.PromptPlayer ?? ctx.ActivatingPlayer;
                _controllers[asker].AnswerChoice(_g, ctx.PendingChoice);
                if (ctx.PendingChoice.Cancelled)
                {
                    _g.Log.Write($"  ↺ {asker} cancelled; restarting effect");
                    ctx.PendingChoice = null;
                    ctx.HandlerState = null;
                }
            }
            else
            {
                _g.Log.Write($"  ! handler returned without pause or completion");
                break;
            }
        }
    }

    private void Phase4_UsePlan()
    {
        _g.Phase = GamePhase.UsePlan;
        var p = _g.Player(_g.ActivePlayer);
        if (p.Plan.Count == 0) return;

        // Force-use if Plan has ≥ 4 cards (rulebook p.20).
        bool forced = p.Plan.Count >= 4;
        if (!forced)
        {
            var legal = new List<PlayerAction>
            {
                new PlayerAction.UsePlan(),
                new PlayerAction.SkipPlan(),
            };
            var action = _controllers[_g.ActivePlayer].PickAction(_g, legal);
            if (action is PlayerAction.SkipPlan)
            {
                _g.Log.Write($"  Plan delayed by {_g.ActivePlayer} ({p.Plan.Count} cards)");
                return;
            }
        }
        else
        {
            _g.Log.Write($"  Plan forced (≥4 cards) for {_g.ActivePlayer}");
        }

        _g.IsResolvingPlan = true;
        try
        {
            // Walk plan in order, prompting USE/SKIP per card; the entire Plan
            // discards at the end regardless. Each card is removed before its
            // effect runs so that mid-effect references see consistent state.
            // CurrentlyResolvingPlanCardId is set BEFORE the Use/Skip prompt
            // (and stays set through Skip) so the UI can show the player which
            // card they are deciding on — without it, the card briefly
            // disappears between the Plan list (already removed) and the
            // resolving slot (not yet set).
            while (p.Plan.Count > 0 && !_g.IsGameOver)
            {
                int cardId = p.Plan[0];
                p.Plan.RemoveAt(0);
                _g.Discard.Add(cardId);
                _g.CurrentlyResolvingPlanCardId = cardId;
                var card = _g.CardsById[cardId];
                var handler = _registry.Resolve(card.EffectFamily);
                if (handler is null)
                {
                    _g.Log.Write($"  plan #{cardId} ({card.EffectFamily}) — no handler, auto-skip");
                    _g.CurrentlyResolvingPlanCardId = null;
                    continue;
                }

                var legal = new List<PlayerAction>
                {
                    new PlayerAction.UseImpulseCard(),
                    new PlayerAction.SkipImpulseCard(),
                };
                var action = _controllers[_g.ActivePlayer].PickAction(_g, legal);
                if (action is PlayerAction.SkipImpulseCard)
                {
                    _g.Log.Write($"  plan #{cardId} skipped by {_g.ActivePlayer}");
                    _g.CurrentlyResolvingPlanCardId = null;
                    continue;
                }

                _g.Log.Write($"  plan #{cardId} used by {_g.ActivePlayer} ({card.ActionType})");
                var ctx = new EffectContext
                {
                    ActivatingPlayer = _g.ActivePlayer,
                    Source = new EffectSource.PlanCard(cardId),
                };
                _g.PendingEffect = ctx;
                RunEffectToCompletion(handler, ctx);
                _g.CurrentlyResolvingPlanCardId = null;
                _g.PendingEffect = null;
            }
        }
        finally
        {
            _g.IsResolvingPlan = false;
        }

        // Promote NextPlan to Plan for the player's next turn.
        if (p.NextPlan is { Count: > 0 } next)
        {
            p.Plan.AddRange(next);
            _g.Log.Write($"  {_g.ActivePlayer} NextPlan ({next.Count}) → Plan for next turn");
        }
        p.NextPlan = null;
    }

    private void Phase5_Score()
    {
        _g.Phase = GamePhase.Score;
        Scoring.RunPhase5(_g, _g.ActivePlayer, _g.Log);
    }

    private void Phase6_Cleanup()
    {
        _g.Phase = GamePhase.Cleanup;
        Mechanics.TrimImpulseTopTo(_g, ImpulseCap, _g.Log);
        if (_g.IsGameOver) return;
        Mechanics.DrawFromDeck(_g, _g.ActivePlayer, CleanupDraw, _g.Log);
        if (_g.IsGameOver) return;

        // Advance turn
        var idx = _g.NextPlayerIndex();
        _g.ActivePlayer = _g.Players[idx].Id;
        if (idx == 0) _g.CurrentTurn++;
        _g.Phase = GamePhase.AddImpulse;
    }
}
