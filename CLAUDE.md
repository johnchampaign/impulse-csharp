# CLAUDE.md — Impulse port

C# port of Carl Chudyk's *Impulse* (2013) from rulebook only. Sibling
project to the user's Innovation port; conventions inherited but **no
shared engine code**.

This file grows incrementally as patterns are established. For now it
points at the design docs.

## Sources of truth

- Rules: `Impulserules.pdf` (44 pages). `Impulse_rules_summary_v1.pdf`
  is a concise companion.
- Card art: `_vmod/images/c*.jpg` (108 cards, Vassal canonical count).
- Race cards: `_vmod/images/raza1.jpg` … `raza6.jpg`.
- Map reference: `VassalImpulseMap.png`.

`Card_Distribution_Prepub_Color.pdf` is **outdated** — ignore in favor
of Vassal's 108.

## Design docs

- [`docs/core-model.md`](docs/core-model.md) — Card, Race+Tech, Map,
  Sector Core+scoring, Tokens.
- [`docs/engine.md`](docs/engine.md) — Turn loop, `EffectContext`,
  action/choice DTOs, registry, sub-state machines, logging, codec,
  determinism, first-5 rollout order.

## Solution layout

```
src/
  Impulse.Core/    Engine, no UI dependencies. net10.0.
                   Embeds Data/cards.tsv (Windows-1252).
  Impulse.Wpf/     WPF shell. net10.0-windows. WinExe.
  Impulse.Tests/   xUnit. References Core only.
```

Build: `dotnet build`. Test: `dotnet test`.

## Conventions (inherited from Innovation, restated for clarity)

- Citations in handler comments use `// Impulserules p.<n>` — NOT VB6
  line numbers (no VB6 source for this game).
- Engine has no `DateTime.Now` and no unseeded `Random`. Single seed on
  `GameState.Rng` plus per-seat controller seeds fully determine a run.
- `Innovation.Core ↔ Innovation.Wpf` boundary applies here too:
  Core knows nothing about WPF, threads, or the dispatcher; Wpf knows
  nothing about card mechanics.
- Pause/resume idiom for handlers: set `ctx.PendingChoice` + `Paused`,
  return `false`. Stash multi-stage state in `ctx.HandlerState`.
- Always go through `Mechanics.*` / `Scoring.AddPrestige` rather than
  mutating piles or prestige directly.
- `if (g.IsGameOver) return;` before any phase reset following an
  effect.
- Mid-effect state codec does not round-trip; only emit `[state]` log
  lines at turn boundaries.

## What NOT to do

- Don't add `DateTime.Now` or unseeded `Random`.
- Don't share types between Core and Wpf beyond DTOs.
- Don't generalize an engine across Innovation and Impulse.
- Don't write documentation files (`*.md`, `README*`) unless asked.
