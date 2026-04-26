# Impulse

A C# / WPF implementation of Carl Chudyk's *Impulse* (2013), a 4X game of
exploration, mining, research, and combat played out on a hex sector map.

Single-player against AI opponents (greedy, warrior, core-rush, munchkin,
or refine policies); 2 to 6 seats with one human and any mix of AI seats.
Faithful to the base-game rules; no expansions.

## Disclaimer

This is a non-commercial fan implementation, released for personal and
educational use. *Impulse* is designed by **Carl Chudyk** and published by
**Asmadi Games**; the game's design, rules, card text, and trademarks belong
to them. This repository is **not affiliated with, endorsed by, or sponsored
by Asmadi Games or Carl Chudyk**.

If you enjoy the game, please **buy a copy** from Asmadi or your local game
store. The physical game is the canonical experience and supports the
designer.

## Build & run

Requires the .NET 10 SDK and Windows (the UI is WPF).

```
dotnet build
dotnet test
dotnet run --project src/Impulse.Wpf
```

On first launch you'll be prompted for the number of players (2–6) and each
AI seat's policy (Random / Greedy / Warrior / CoreRush / Munchkin / Refine).
Your selections are remembered between runs.

## Project layout

```
src/
  Impulse.Core/       Engine. No UI dependencies.
  Impulse.Wpf/        WPF shell.
  Impulse.Tests/      xUnit tests.
docs/
  core-model.md       Card, Race, Map, Sector Core, scoring.
  engine.md           Turn loop, EffectContext, registry, codec, rollout.
```

`CLAUDE.md` at the repo root documents the project's conventions, the
pause/resume idiom for interactive effects, and known gotchas hard-won from
porting the rules cold (no VB6 reference for this one).

## What works

- All 108 base-game cards across the seven action types (Build, Command,
  Mine, Refine, Trade, Draw, Plan, Research, Sabotage, Execute).
- All six races and their basic-unique techs (Piscesish, Ariek, Herculese,
  Draconians, Triangulumnists, Caelumnites).
- 2–6 player games with one human and any mix of AI seats; per-player-count
  home corner placement matching the rulebook.
- Sector-map activation: transports activate face-up cards on arrival;
  bonus matching gems from the just-moved fleet apply to the boost.
- Sector Core activation with player-chosen mineral color for boost.
- Battle resolution: defender-first reinforcements (with bluff cards),
  per-cruiser deck draws, icon comparison, defender-wins-ties.
- Exploration: multi-step movements declare the full path first, then
  explore face-down cards one at a time as they're traversed.
- Multi-fleet `[N] fleets apiece` cards with same-destination convergence.
- Plan / Research / Execute meta-effects with sub-effect forwarding.
- Per-turn game-state codec: a base64 string at the top of every turn in
  the log file. Paste it into Load State to resume from any turn boundary.
- Per-game log file at `%TEMP%/impulse-last-game.log`.

## What's not (yet)

- No expansions or variant rules.
- Online multiplayer is not implemented.
- AI policies are heuristic, not strategic — they're stronger than random
  but won't plan more than one action ahead.

## Contributing

Bug reports and pull requests are welcome. Before opening a PR, please:

1. Read `CLAUDE.md` — it documents the conventions (handler pause/resume
   pattern, rulebook citations in comments, scripted-controller test idiom).
2. Add a unit test that reproduces the bug or covers the new behaviour.
3. Make sure `dotnet test` passes.

If you're fixing a card-rules bug, citing the relevant rulebook page in your
comment (as `Rulebook p.<n>`) is appreciated — it makes future rule audits
much easier.

## Credits

- **Carl Chudyk** — designer of *Impulse*.
- **Asmadi Games** — publisher.

## License

MIT — see [LICENSE](LICENSE). The MIT license applies to the source code in
this repository. It does **not** grant any rights to *Impulse* itself, its
rules, card text, artwork, or trademarks, which belong to Asmadi Games.
