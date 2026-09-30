# CS2 Lineup Finder — Project Layout

Grenade line-up finder ("瞄点预测") for CS2 practice / run-map servers, built as a
CounterStrikeSharp (CSSharp) plugin.

The repository is split into four layers. Each layer has exactly one owner and a
hard boundary that is enforced by project references.

```
contracts/                        interfaces only, no logic  -> owner: integration
src/CS2LineupFinder.Math/         math + inverse solver      -> owner: math-core
src/CS2LineupFinder.Core/         forward trajectory sim     -> owner: physics-sim
plugins/CS2LineupFinder/          CSSharp entry point        -> owner: plugin-shell
tests/                            unit tests per layer       -> owner: same as layer
docs/                             user + contributor docs    -> owner: plugin-shell
```

## Dependency direction

```
contracts  <-  src/CS2LineupFinder.Math   (pure math, no game/engine types)
contracts  <-  src/CS2LineupFinder.Core   (game-agnostic physics + geometry)
contracts  <-  plugins/CS2LineupFinder    (CSSharp: commands, beams, persistence)
```

`plugins/` never references `src/` types directly from its own source files, it
only talks to the `contracts/` interfaces. That keeps the plugin compilable and
testable while `Math` and `Core` are still in flight.

## Directory detail

| Path | Contents |
| --- | --- |
| `contracts/` | DTOs + interfaces shared by all layers. Frozen per integration cycle. |
| `src/CS2LineupFinder.Math/` | Vector math, trajectory fitting, inverse (yaw/pitch) solver. Must not reference CSSharp. |
| `src/CS2LineupFinder.Core/` | Forward ballistic simulation, world tracing, weapon VData access. |
| `plugins/CS2LineupFinder/` | `BasePlugin` implementation, `css_lf_*` commands, chat aliases, menus, beam rendering, JSON persistence, `config/config.toml`. |
| `plugins/CS2LineupFinder/data/` | Map point catalogue + saved line-ups (`data/lineups/*.json`). Created at runtime, git-ignored. |
| `tests/` | One test project per layer; the plugin test project also contains stubs. |
| `.github/workflows/build.yml` | `dotnet build` (warnings as errors) + `dotnet test` + artifact upload. |

## Rules

1. A layer may only depend on `contracts/` plus its own private helpers.
2. No file outside your own directory may be edited. Contract changes are
   requested through `docs/contract-issues.md`.
3. Every public API in `contracts/` is documented; the contracts project builds
   with `TreatWarningsAsErrors` and CS1591 enabled, so an undocumented member
   breaks the build.
4. Stubs live next to their consumer and carry a `// TODO: replace with real
   Core` / `Math` marker so they can be found with a single `rg`.

## Merge order

`main` receives the branches in this order, which matches the dependency
direction: `feat/math-core` -> `feat/physics-sim` -> `feat/plugin-shell`.
See `docs/CONTRIBUTING.md`.
