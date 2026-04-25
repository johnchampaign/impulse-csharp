# Impulse.Core — Model Doc (v1)

Port of Carl Chudyk's *Impulse* (2013) from rulebook only. Sibling to the
Innovation port; conventions inherited (rulebook citations, scripted-controller
tests, deterministic seeded RNG, paused-context idiom, turn-boundary state
codec) but **no shared engine code**.

Citations: `// Impulserules p.<n>`. Project: `Impulse.Core` (net10.0),
`Impulse.Wpf` (net10.0-windows), `Impulse.Tests`.

This doc is types + responsibilities, no code. Five sections:
(1) Card model, (2) Race + Tech, (3) Map, (4) Sector Core + scoring,
(5) Tokens.

---

## 1. Card model

Single canonical deck of **108 cards** (Vassal count). Cards have no unique
title — multiple physical cards share an effect. The registry keys by
`EffectFamily` slug; one handler serves every card in a family.

### `Card` (immutable record)

| Field | Type | Notes |
|---|---|---|
| `Id` | `int` | 1..109 with one gap (Vassal numbering preserved). |
| `ActionType` | `CardActionType` | `Command \| Build \| Plan \| Research \| Mine \| Refine \| Trade \| Sabotage \| Explore \| Battle` (rulebook actions, p.6–17). |
| `Color` | `CardColor` | `Blue \| Yellow \| Red \| Green`. Mineral matching is equality (locked). |
| `Size` | `int` | 1..3, the white-box number used for matching, mining, refining, etc. |
| `BoostNumber` | `int` | Non-null. Same number as `Size` on the Vassal cards but stored as a separate field so future card sets can diverge. |
| `EffectFamily` | `string` | Slug like `command_one_transport_n_moves`. Registry key. |
| `EffectText` | `string` | Verbatim rulebook text. Inline color references kept as text (`TRADE [1] [B] card from your hand`). Rendered in UI; not parsed. |

Loaded from `Data/cards.tsv` (Windows-1252; `CodePagesEncodingProvider`
registered at startup). TSV authoring runs as a separate Python+vision pass
over `_vmod/images/c*.jpg`; this doc treats the schema as fixed.

**Sector Core cards** and **Command Center cards** live in separate
embedded files (`sector_core.tsv`, `command_centers.tsv` — TBD), not in
`cards.tsv`. They are not drawable, not shuffled into the deck, never enter
hands or Plans.

### `CardEffect` registration

`CardRegistry` maps `EffectFamily → IEffectHandler`. The handler signature
mirrors Innovation:

- `bool Execute(GameState g, PlayerState actor, EffectContext ctx)`
- Returns `true` if the effect made progress (drew, moved, built, scored,
  destroyed, etc.); `false` for no-op or user-declined.
- Pause/resume via `ctx.PendingChoice` + `ctx.Paused = true`; multi-stage
  state stashed in `ctx.HandlerState` (typed `object?`).

Card *zones* a card can occupy: `Deck, Hand, Impulse, Plan, TechSlot,
Mineral, ScorePile (none — Impulse has no scoring zone), Discard,
FaceDownOnNode (exploration)`. Encoded as `CardLocation` enum + per-zone
indices on `GameState`.

---

## 2. Race + Tech model

Six races, color-coded to the six player colors (Blue, Green, Purple, Red,
White, Yellow — Vassal PlayerRoster, locked). Confirmed: races differ
**only** in their unique Basic Tech. Once both Basic Techs are covered,
races are mechanically identical.

### `Race` (immutable record)

```
Race { Id, Name, Color (PlayerColor), BasicUniqueTech (TechDefinition) }
```

Six instances baked in. Race name is flavor only — no abilities.

### `TechDefinition`

Static description of a tech effect. Same shape as `Card` minus deck
metadata (no Size/BoostNumber/Color — techs aren't dealt or boosted).

```
TechDefinition { Slug, DisplayText, Handler (IEffectHandler) }
```

Two singletons + six race-bound + 108 card-derived (every researched card
becomes a tech) — but the registry doesn't pre-build researched ones; a
researched tech wraps the originating `Card.Id` and reuses the card's
handler.

### `Tech` (discriminated union, sum of three cases)

| Case | Carries | Source |
|---|---|---|
| `BasicCommon` | — (singleton) | Same for all races. Text: *"Discard a card in order to either: Command one fleet for one move OR Build one ship at home."* (raza1–6 left slot, confirmed.) |
| `BasicUnique` | `Race` | Race's right-slot starter, e.g. Piscesish: *"Draw one size one card from the deck."* |
| `Researched` | `int` cardId | Wraps a card's effect. Card moves from hand to tech slot via Research; original card object is consumed. |

### `TechSlots`

Per-player, fixed pair `(Left, Right)`. Both always populated (locked).
Research overwrites a chosen slot; covered tech is **discarded permanently**
(rulebook p.20: *"Once covered up, Basic Techs are gone!"*). No revert.

`PlayerState.TechLeft : Tech` and `TechRight : Tech`. At setup:
`Left = BasicCommon`, `Right = BasicUnique(race)`.

---

## 3. Map model

Static for v1. Per-player-count *starting positions* exist but are flavor
for setup only (per user); the underlying graph is one fixed `SectorMap`.

### `Node`

```
Node { Id, IsHome (bool), Owner (PlayerId? — only set if IsHome) }
```

Nodes hold ships (transports/cruisers), face-down exploration cards (see
below), and may be a player's home. The map's center node is the **Sector
Core** (special; see §4).

### `Gate`

Pure connector (locked: no per-gate icons, no hidden fields).

```
Gate { Id, EndpointA (NodeId), EndpointB (NodeId) }
```

A cruiser sitting on a gate patrols both endpoints (used by Sabotage target
set, locked).

### `SectorMap`

```
SectorMap {
    Nodes : IReadOnlyList<Node>
    Gates : IReadOnlyList<Gate>
    SectorCoreNodeId : NodeId
    HomeNodeIds : IReadOnlyDictionary<PlayerId, NodeId>
    AdjacencyByNode : ILookup<NodeId, Gate>
}
```

Built once at game start by `MapFactory.Build(playerCount, seats)`. The
factory chooses home assignments per player count but the graph itself is
constant. Encoded literally from Vassal `map.jpg` topology.

### Exploration

Outer-ring nodes start with face-down cards. A move through unexplored
territory triggers exploration: the **full path is declared up front**
(locked) via `DeclareMoveRequest`, *then* face-down cards flip in path order.
Sub-machine `PendingExploration` on `EffectContext` walks the declared path.

---

## 4. Sector Core + scoring

### Sector Core node

Center node of the map. Activated when transports occupy it; gates radiating
from it are *patrolled* when a player has cruisers on those gates. Both
contribute to prestige.

### `Prestige`

Single integer per player (locked: no per-source breakdown stored).

```
PlayerState.Prestige : int    // 0..20+, win check on every mutation
```

Win condition: `Prestige >= 20`. Checked **continuously** including
mid-effect and on opponents' turns (locked). All mutations go through
`Scoring.AddPrestige(g, player, amount, source)` which:

1. Adds the amount.
2. Logs `+N prestige (source) → P# total M` via `GameLog`.
3. Sets `g.IsGameOver = true` and `Phase = GameOver` if the threshold is
   crossed.

`source` is a `PrestigeSource` enum — for **logging only**, not stored on
`PlayerState`. Six sources (memory):
`TradedCardIcons, Refining, BattleWon, ShipsDestroyed,
SectorCoreGatesPatrolled, SectorCoreActivatedByTransports`.

The prestige-track board from the physical game is *not* modeled; it's
just an int + UI readout.

### Scoring phase (Phase 5)

End-of-turn-only sources fire here:
- `SectorCoreGatesPatrolled` — count this player's cruisers on Sector Core
  gates, award 1 each (TBD verify).
- `SectorCoreActivatedByTransports` — if this player has transports on the
  Sector Core node, award N (TBD verify).

Mid-turn sources (`TradedCardIcons`, `Refining`, `BattleWon`,
`ShipsDestroyed`) are awarded inline by their handlers via
`Scoring.AddPrestige`.

The **`if (IsGameOver) return;`** guard from Innovation applies before any
phase reset that follows an effect — see §6 of CLAUDE.md (to be written).

---

## 5. Tokens

### Ship pool

Per locked decisions: single `Available` counter per player, starts at 12.
Transports vs. cruisers determined by **location**, not separate caps:

- A ship on a node is a **transport**.
- A ship on a gate is a **cruiser**.

```
PlayerState.ShipsAvailable : int      // off-map reserve, starts at 12
```

On-map ships are derived by querying `GameState.ShipPlacements` (a flat
list of `(PlayerId, Location)` where `Location` is a `NodeId | GateId`
union). Total per player = `ShipsAvailable + on-map count` and must be ≤ 12
at all times (the 13th physical ship is the prestige-track marker, not
modeled).

**Elimination**: when on-map count == 0, the player is eliminated. Checked
after every ship-removing event (battle, sabotage, exploration loss).

### Impulse track

Shared FIFO queue (locked).

```
GameState.Impulse : List<int>    // card ids, [0] = oldest = top
```

- Phase 1: append newest to bottom.
- Phase 3: walk from `[0]` forward (cursor on `GameState`).
- Phase 6: trim from `[0]` (oldest first).

### Plans

Per-player, ordered list of card ids. Re-entry creates a *new* Plan
resolved on the player's NEXT Phase 4 (locked).

```
PlayerState.Plan        : List<int>
PlayerState.NextPlan    : List<int>?    // populated mid-Plan-resolution
```

Hand cap = 10. Plan force-use threshold ≥ 4 cards (locked).

### Initiative marker

Deferred (per user). Will appear in a v1.1 addendum.

### Other tokens

- **Minerals**: cards tucked under left side of Command Center; not a
  separate token type — they're `Card` references with location
  `CardLocation.Mineral`.
- **Face-down exploration cards**: `Card` references with location
  `CardLocation.FaceDownOnNode`, indexed by `NodeId`.

---

## Open items for v1.1

- Initiative marker mechanics (deferred per user).
- Exact Phase 5 prestige formulas for Sector Core gates / transports
  (verify rulebook p.5 & p.18).
- Team mode (locked: deferred from v1).
- Sector Core card list and Command Center card list (separate TSVs).

## Not in this doc (intentional)

- Turn-loop state machine and `EffectContext` shape — those are the
  *engine* doc, written next once this model signs off.
- Battle 8-step sub-machine — engine doc.
- Action/Choice DTOs (`PlayerAction`, `ChoiceRequest` subtypes) — engine
  doc.
- Logging conventions — engine doc.
