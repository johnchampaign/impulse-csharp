# Impulse.Core — Engine Doc (v1)

Companion to [core-model.md](core-model.md). This doc covers the runtime:
turn loop, `EffectContext` shape, action/choice DTOs, registry, sub-state
machines, logging, state codec, and the first-5 mechanics rollout order.

Citations: `// Impulserules p.<n>`. No code in this doc; types and
responsibilities only.

---

## 1. Turn loop — six phases

Per locked decisions, every turn is a six-phase machine driven by
`TurnManager`. Phases run in fixed order; some are mostly automatic, some
prompt the active player.

| Phase | Name            | Driver / inputs                               | Notes |
|------:|-----------------|-----------------------------------------------|-------|
| 1     | `AddImpulse`    | Active player picks one card from hand → bottom of shared Impulse track. | Single `PlaceImpulseAction`. Skipped only on first turn (per rulebook setup). |
| 2     | `UseTech`       | Optional. Active player invokes 0 or 1 of their two techs. | `UseTechAction { slot: Left/Right }` or `SkipTechAction`. Tech effect may pause for choices. |
| 3     | `ResolveImpulse`| Walk shared Impulse track from `[0]` forward; cursor on `GameState.ImpulseCursor`. Each card: active player chooses use or skip. | Per-card `UseImpulseCardAction` / `SkipImpulseCardAction`. Cursor advances when the current card resolves or is skipped. |
| 4     | `UsePlan`       | Optional. Active player resolves their entire Plan in order, or delays. Plan with ≥ 4 cards cannot be delayed (locked). | `UsePlanAction` / `DelayPlanAction`. Cards added mid-resolution go to `PlayerState.NextPlan` (locked). |
| 5     | `Score`         | Automatic. Award end-of-turn prestige (Sector Core gates patrolled, transports activating Sector Core). | No prompts. `Scoring.RunPhase5(g, activePlayer)`. |
| 6     | `Cleanup`       | Automatic. Trim oldest cards from Impulse top until length ≤ cap; draw 2 (per Piscesish race-card "Turn Phases" panel: *"Draw two, trim Impulse"*). Verify cap value from p.20–21 rules text. | No prompts. Advances `ActivePlayer` and `CurrentTurn`. |

`GamePhase` enum: `Setup, AddImpulse, UseTech, ResolveImpulse, UsePlan,
Score, Cleanup, GameOver`.

`TurnManager.Step()` is the single entry point. It dispatches based on
`g.Phase`:
- If a prompt is needed, sets `g.PendingChoice` and returns (engine
  pauses).
- If no prompt, advances state and may transition to the next phase.
- On phase transition, **always check `g.IsGameOver` first**; never reset
  phase past a game-end signal.

### Continuous win check

Prestige ≥ 20 ends the game *immediately* (locked: mid-effect, on
opponents' turns). All prestige flows through `Scoring.AddPrestige`,
which sets `g.IsGameOver = true; g.Phase = GameOver` on threshold cross.
Every callsite that resumes after a handler returns must guard:

```
if (g.IsGameOver) return;
```

(Inherited gotcha from Innovation; see CLAUDE.md.)

---

## 2. `EffectContext`

Transient, per-effect-invocation state. Lives on `GameState.PendingEffect`
while an effect is in flight; cleared when the effect completes.

| Field                | Type                          | Purpose |
|----------------------|-------------------------------|---------|
| `ActivatingPlayer`   | `PlayerId`                    | Who triggered the effect (active player, except for Sabotage targets reacting). |
| `Source`             | `EffectSource`                | Discriminated: `ImpulseCard(id) \| PlanCard(id) \| Tech(slot) \| BattleReinforcement`. For logging + nested-frame tracking. |
| `PendingChoice`      | `ChoiceRequest?`              | Set by handlers to pause for input. |
| `Paused`             | `bool`                        | Paired with `PendingChoice`. Engine returns control when true. |
| `HandlerState`       | `object?`                     | Multi-stage handler scratchpad (typed cast inside handler). Stage enum or sentinel string. |
| `NestedFrames`       | `Stack<NestedEffectFrame>`    | For effects that invoke another effect (e.g. Basic Common tech → Command). Save/restore `HandlerState`. |
| `PendingBattle`      | `BattleState?`                | Sub-state machine; see §5. |
| `PendingExploration` | `ExplorationState?`           | Sub-state machine; see §5. |
| `IsComplete`         | `bool`                        | Handler signals "done"; engine pops the frame. |

### Pause/resume idiom (inherited from Innovation)

```
// First entry: post prompt, pause.
if (ctx.PendingChoice is null) {
    ctx.PendingChoice = new SelectNodeRequest { ... };
    ctx.Paused = true;
    return false;
}

// Resume: read answer, null PendingChoice, apply.
var req = (SelectNodeRequest)ctx.PendingChoice;
ctx.PendingChoice = null;
ApplyMove(g, req.ChosenNodeId);
return true;
```

**Gotcha (locked):** never consume `ctx.PendingChoice` without nulling it,
or the next iteration sees stale data.

### Nested frames

When a handler invokes another card's / tech's effect (e.g. Basic Common
tech *"Command one fleet for one move OR Build one ship at home"* invoking
Command-family handler), the engine pushes a `NestedEffectFrame` onto
`ctx.NestedFrames` carrying the **outer** `HandlerState`. The nested
handler runs with a fresh `HandlerState`. On nested completion, the engine
restores the outer `HandlerState`. Outer and nested handlers must not see
each other's scratchpad. (Inherited gotcha.)

---

## 3. Action and choice DTOs

Two layers, mirroring Innovation's `PlayerAction` / `ChoiceRequest` split.

### `PlayerAction` — top-level, advances phases

Sealed abstract; one subtype per phase decision.

| Action                  | Phase             | Carries |
|-------------------------|-------------------|---------|
| `PlaceImpulseAction`    | `AddImpulse`      | `int cardIdFromHand` |
| `UseTechAction`         | `UseTech`         | `TechSlot slot` |
| `SkipTechAction`        | `UseTech`         | — |
| `UseImpulseCardAction`  | `ResolveImpulse`  | — (acts on cursor card) |
| `SkipImpulseCardAction` | `ResolveImpulse`  | — |
| `UsePlanAction`         | `UsePlan`         | — |
| `DelayPlanAction`       | `UsePlan`         | — (illegal if Plan ≥ 4 cards) |

`TurnManager.ApplyAction(action)` validates legality against current
phase + game state, applies it, and may set `g.PendingEffect`. Illegal
actions throw `InvalidActionException` — not silently dropped.

### `ChoiceRequest` — mid-effect, doesn't advance phase

Sealed abstract. Listed by the kinds of input they collect:

| Request                        | Used by |
|--------------------------------|---------|
| `SelectHandCardRequest`        | Discard, Trade, Reinforce, Mine input. Filterable by size/color predicate. |
| `SelectHandCardSubsetRequest`  | Effects taking N hand cards at once. |
| `SelectNodeRequest`            | Move destination, Build location, Sabotage target. |
| `SelectGateRequest`            | Cruiser placement, patrol target. |
| `SelectFleetRequest`           | "Choose one of your fleets to move/command." |
| `DeclareMoveRequest`           | **Full path declared up front** (locked) before any exploration flips. |
| `BattleReinforcementRequest`   | Repeated within a battle; tracked by `BattleState`. |
| `SelectTechSlotRequest`        | Research target slot. |
| `SelectImpulseCardRequest`     | Effects referencing "the last card on the impulse" (per rulebook FAQ p.42 — bottom-most card). |
| `YesNoRequest`                 | Optional triggers. |
| `SelectPrestigeAmountRequest`  | Refine, where amount depends on minerals spent. |

Each request carries a `Chosen…` field populated by the controller before
the engine resumes. Validity (size/color predicates, legal-target sets) is
computed by the handler and embedded in the request, so the UI just renders
allowed options.

### `IPlayerController`

Synchronous, one method per top-level decision and per `ChoiceRequest`
subtype. AI controllers compute and return; `HumanController` forwards to
`IUserPromptSink` and blocks on a `TaskCompletionSource` from the WPF click
handler. Same shape as Innovation.

`GameRunner` owns one controller per seat, drives `TurnManager.Step()`,
and exposes `OnStepCompleted` / `OnChoiceResolved` events.

---

## 4. Registry

`EffectRegistry` is the single source of effect-handler dispatch. Three
populated tables, all keyed by string slug:

| Table              | Key format                            | Source |
|--------------------|---------------------------------------|--------|
| `CardEffects`      | `EffectFamily` slug from `cards.tsv`  | One handler per family; many cards may share. |
| `TechEffects`      | `tech_basic_common`, `tech_basic_unique_<race>` | Two cases for `Tech.BasicCommon` / `BasicUnique`. |
| `SectorCoreEffects`| TBD slug per Sector Core card         | From `sector_core.tsv`. |

`Researched` techs **don't** get their own table entry — `Tech.Researched(cardId)`
dispatches into `CardEffects` by looking up the originating card's
`EffectFamily`. One handler, two callsites. Wrap-the-card-as-tech is just a
location change.

`EffectRegistry.Resolve(effectKey) → IEffectHandler` caches handler
instances (handlers are stateless; per-invocation state lives in
`EffectContext.HandlerState`).

`CardRegistrations.RegisterAll(registry, cards)` is the bootstrap call,
mirroring Innovation. Registrations live in
`Innovation.Core/Cards/*Handler.cs`-style files (`Impulse.Core/Effects/…`).

---

## 5. Sub-state machines

Two effects are big enough to warrant their own state object on
`EffectContext`. Both honor pause/resume.

### Battle (rulebook p.34, race-card "BATTLE RULES" panel)

Triggered when a moving Cruiser fleet ends on a gate occupied by another
player's Cruisers. Four steps (per race-card panel; not 8 — earlier
sketch was speculative):

1. **Defender** places reinforcements face down, **then attacker**.
2. **Reveal**. Each reinforcement counts only if `(size + color)` matches
   a card in that player's Plan, Impulse, or Techs (locked: union; **not
   Minerals**, p.34). Bluffs return to hand.
3. **Draw one card per Cruiser in fight**, add face-up to reinforcements.
4. **Most total icons wins** (defender wins ties). Losing fleet entirely
   destroyed. Winner: +1 prestige, +1 per ship destroyed. All used cards
   discarded.

`BattleState`:
```
{
    Attacker, Defender : PlayerId
    GateId             : GateId
    AttackerShips, DefenderShips : int        // cruiser counts at start
    AttackerReinforcements, DefenderReinforcements : List<int>  // card ids
    Step               : BattleStep enum
}
```

`BattleStep`: `DefenderReinforcing → AttackerReinforcing → Revealing →
DrawingPerCruiser → Resolving → Done`. Each step either prompts via
`BattleReinforcementRequest` or runs automatically.

A battle **ends a fleet's movement** (p.34). Multi-fleet Command cards
that converge: all fleets resolve into one battle, not several.

### Exploration

Triggered when a declared move path crosses face-down territory. Per
locked decision: **full path declared first** via `DeclareMoveRequest`,
*then* face-down cards flip in path order.

`ExplorationState`:
```
{
    Mover         : PlayerId
    DeclaredPath  : IReadOnlyList<NodeId>  // including start; flips happen at unexplored nodes
    Cursor        : int                    // index into DeclaredPath
    FleetType     : Transport | Cruiser
}
```

Sub-machine walks `DeclaredPath` one node at a time. At each unexplored
node: flip the face-down card, resolve any flip effect, decide whether
movement continues (e.g. movement may halt on hostile flips per rulebook).
Cursor advances; `Done` when end of path or forced halt.

---

## 6. Logging

`GameLog` is static, append-only, mirrors Innovation's. Default file
`%TEMP%/impulse-last-game.log`. `OnLine` event for UI mirroring.

Conventions:

- `[state] <codec>` — turn-boundary snapshot. Emitted by `TurnManager`
  after `Cleanup` completes, before advancing `ActivePlayer`. **Not
  mid-effect** (codec doesn't round-trip; locked).
- `— P1 turn 7 phase AddImpulse` — phase banner.
- `P1 places c42 (Command/Blue/2) on Impulse` — top-level action.
- `Effect: P1 uses Tech Left (basic_common) — discard c17, command fleet @N5` — effect start.
- `  → handler CommandOneTransportNMovesHandler on P1` — handler trace.
- `    = paused awaiting SelectNodeRequest` — pause point.
- `+1 prestige (BattleWon) → P1 total 12` — scoring event.
- Battle: `Battle @G3: P1 (2 cruisers) vs P2 (1 cruiser)` … `P1 wins, P2 loses 1 ship`.

`GameLog.P(int)`, `GameLog.C(GameState, int)`, `GameLog.N(NodeId)`,
`GameLog.G(GateId)` formatting helpers.

`Pause()` / `Resume()` suppress logs during AI rollouts.

---

## 7. State codec

`GameStateCodec` (base64). Captures: decks, Impulse track, hands,
plans+next-plans, tech slots, mineral piles, ship placements,
ShipsAvailable, prestige, achievements (none — Impulse has no
achievements), turn/phase/active-player.

**Does NOT capture**: `EffectContext`, `PendingChoice`, `HandlerState`,
`PendingBattle`, `PendingExploration`, `NestedFrames`. Round-trip is safe
**only at turn boundaries** (i.e. `Phase ∈ {AddImpulse}` at top of turn,
post-Cleanup of previous turn).

`Copy state` UI button warns if `Phase != AddImpulse` or
`PendingEffect != null`. Load dialog refuses such snapshots and points the
user at `[state]` log lines.

---

## 8. Determinism

Engine has no `DateTime.Now`, no unseeded `Random`. Game seed → derived
per-seat seeds for AI controllers (same scheme as Innovation's
`MainWindow.xaml.cs` ctor). One seed + scripted human inputs = one game.

Exploration deck shuffle, initial card placements on outer-ring nodes,
and the main deck shuffle all draw from `g.Rng` (single seeded `Random`
on `GameState`).

---

## 9. WPF integration notes

(Relevant constraints; full WPF doc later.)

- Game loop on background `Task`. UI marshals via `Dispatcher`.
- AI top-level steps gate on a Continue button (`TaskCompletionSource<bool>`).
- **Continue gate skips when `_runner.IsResolvingChoice == true`**
  (locked: AI mid-effect choices shouldn't demand clicks).
- Initial setup must not re-run on a loaded state (guard:
  `g.Phase == Setup`).

---

## 10. Rollout order — UI-first, then play-frequency

Goal: lock look-and-feel **before** any rules code, then make the game
playable end-to-end as early as possible against a restricted deck, then
grow the deck additively. Each post-A slice ends green-tests + runnable
WPF build.

**Testing discipline:** tests land alongside code from slice A onward,
not deferred. Slice A still has tests — for the `Card`/TSV loader,
`SectorMap` factory, and any pure-data plumbing the UI consumes (data
integrity, not behavior). Slice B onward adds behavioral tests for every
new code path: turn-loop transitions, scoring sources, each card-family
handler, sub-machine steps. No slice merges with new untested code
paths.

### Slice A — UI shell, no game logic

WPF `MainWindow` with the full visual layout:

- Hex map rendered from `SectorMap`, ships drawn at nodes/gates, home
  markers, Sector Core highlight.
- Active player's hand panel (cards rendered from `Card` records, art
  optional — text + color band acceptable).
- Shared Impulse track (FIFO, `[0]` at top).
- Per-player Plan stack + tech-slot pair + prestige bar.
- Log pane (mirrors `GameLog`).
- Phase indicator + turn/active-player banner.

Renders a hand-built `GameState` fixture loaded from a fixed seed so
layout iteration is deterministic. Every button is a no-op (or logs "TODO").
**No `TurnManager`, no `EffectRegistry`, no controllers.** Goal: pin
colors, spacing, click affordances, log readability. Bar: "it boots and
looks right."

### Slice B — playable skeleton, one card family

End-to-end 6-phase loop with a deck filtered to an **allowlist** of
`EffectFamily` slugs. Allowlist starts at every Command family
(`command_*`). Cards outside the allowlist are removed from the deck,
the Impulse track, and starting hands at load time — the game is
strictly smaller, never broken.

- Phase 1 (place to Impulse), 5 (score), 6 (trim/draw): real.
- Phase 3: cursor walks the track; only Command cards are usable, others
  auto-skip. `DeclareMoveRequest` + path resolution over explored
  territory only.
- Phase 2 (Tech), 4 (Plan): immediate-skip.
- Battle: stubbed — moving onto an enemy-occupied node/gate is treated
  as illegal (filtered out of move-legality at request-construction time)
  rather than triggering combat.
- Exploration: stubbed — outer ring starts face-up.
- Controllers: `HumanController` + `RandomController` both work.

Bar: scripted-controller test plays a full game to prestige ≥ 20 or
deck-out without throwing; a human can play a turn through the WPF UI.

### Slice C+ — additive card families, in play-frequency order

Each slice adds one family to the allowlist. Order driven by "what does
the early game most need":

| # | Family | Rationale |
|---|---|---|
| C1 | `build_*` | Replaces the unrealistic full-fleet starting position; you need to make ships before Command matters much. |
| C2 | `mine_*`, `refine_*` | First prestige source beyond Sector Core; gets the score bar moving. |
| C3 | `trade_*`, `draw_*` | Hand-shaping; lets the player steer. |
| C4 | `plan_*` (Phase 4 turns on) | Multi-card combos, big swing turn. |
| C5 | Exploration sub-machine | Lights up outer ring; required before mid-game movement is interesting. |
| C6 | Battle sub-machine | Combat at gates; unblocks aggressive Command + Sabotage. |
| C7 | `sabotage_*` | Needs Battle's ship-destruction path. |
| C8 | `research_*`, `execute_*`, Phase 2 Tech turns on | Tech overwrite, recursive effect invocation, nested-frame save/restore. |

Each Cn slice: add registrations, extend the allowlist, add tests for
that family's representative cards (one per distinct slug). The deck
grows monotonically; older tests stay green.

### Why this ordering

UI-first delays the first green test to slice B (vs. the older
test-driven plan that started at setup). The trade is iteration speed on
look-and-feel against a few weeks of rules-only progress invisible to
the user. Play-frequency ordering after that means the game is *playable
in some form* every slice — not just *correct in some sub-system*.

---

## 11. Open items / verify against rulebook

- Phase 6 exact draw count + Impulse trim cap (race card says "Draw two,
  trim Impulse"; rulebook trim length TBD).
- Phase 5 exact prestige formula for Sector Core gates and transport
  activation (p.5/p.18/p.27).
- Initial Impulse seed at game start (if any).
- Sabotage exact mechanics — locked target set is known
  (`OccupySet ∪ PatrolSet`), but resolution (cost, ship destruction count)
  TBD.
- Refine exact formula (gems → prestige rate).
- Initiative marker — deferred per user.

---

## Documented rule interpretations

Where the rulebook is silent or ambiguous, this section records the
engine's interpretation as a deliberate design choice — not a bug —
along with what evidence (rulebook text, designer ruling, BGG thread)
supports it.

### Multi-fleet Command: per-fleet sequential declaration

Cards like c75 / c98 ("Command [N] fleets, each must move to the same
card") let the player command multiple fleets in one action.

**The engine resolves fleets sequentially**: pick fleet 1's origin,
declare its full path, execute it (with battles, exploration, and
transport destruction inline), then pick fleet 2's origin, declare its
path with full knowledge of fleet 1's outcome, and so on.

Implications visible to the player:
- The result of one fleet's movement can change the legal options for
  the next: e.g., a cruiser fleet that destroys a patrolling enemy
  unblocks subsequent transport movement to that card.
- Mid-path exploration on fleet 1 can reveal information that
  influences fleet 2's path.
- The player's choice of fleet order matters strategically.

**Why this interpretation:**
- p.31 ("you must declare your entire movement before [exploring]")
  applies *per fleet*, not across all fleets in a multi-fleet command.
- p.29 ("If moving Transports, all move together and activate the card
  once") describes convergence, not predeclaration. The "once"
  clause is now implemented (commit `70648db`).
- p.31 ("each fleet could move to a different gate") implies a
  per-fleet decision.
- No rulebook text mandates predeclaration of all fleet paths up front.

**The strict alternative** would predeclare every fleet's path before
any execution, with explorations resolved during the combined
execution phase. That's not implemented. Open questions if we ever
switch to it: how to handle fleet 1's predeclared path becoming
invalid after fleet 2 (or some upstream effect) changes the board?
The natural answer would be "abort fleet 1's path at the first
illegal step," but it's awkward. The sequential interpretation has
neither this complexity nor the player-information question.

If a designer ruling or community consensus arrives that locks in the
strict interpretation, switching is a contained refactor — replace
the per-fleet `SelectFleetRequest → DeclareMoveRequest` loop with a
single up-front `DeclareAllFleetsRequest` returning all paths, then
execute them in order.

### Multi-defender battle: attacker chooses (rulebook p.29 explicit)

When a cruiser passes through a card patrolled by multiple enemies, or
moves onto a gate shared by multiple enemies' cruisers, the rulebook
explicitly says "the player moving ships can choose who to fight."
Engine prompts via `SelectFromOptionsRequest` (`DefenderChoice.Resolve`).
See commit `821402b`. Default-pick fallback when only one candidate
exists; no prompt.

### Patrol-through battle: destination must contain enemy

A cruiser path step gate→gate via a card patrolled by enemy is only
enumerated as a legal path if the destination gate has an enemy cruiser
to fight. Empty-gate destinations through patrolled passages are not
offered. There is no "redirect" — the engine never moves a fleet to a
gate the player didn't explicitly select.

Rationale: rulebook p.29 says "Enemy Cruisers cannot move through
cards you patrol except to start a battle." A battle requires an enemy
to fight. The destination IS the battle gate; there is no rulebook
notion of attacking-enemy-on-different-gate-via-mid-passage. See
commit `9b11067`.

### Patrol-through transport destruction: ALL non-attacker transports

When a cruiser wins a battle that traversed a patrolled card, ALL
non-attacker transports on the passage card are destroyed (not just
the defender's). BGG community ruling 2017-06-23 thread 1801001
confirms this: "all transports that are traversed over will be
eliminated. That includes transports belonging to a third player that
isn't participating in the battle." See commit `3107052`.

---

## What this doc deliberately omits

- Card-family handler list — generated from `cards.tsv` during the TSV
  authoring pass.
- Sector Core / Command Center card lists — separate TSVs, separate doc.
- WPF panel layout, tile rendering, click-to-act mapping — WPF doc.
- AI heuristic design — controller doc.
