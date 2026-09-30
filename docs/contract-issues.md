# Contract issues and convergence log

This file is the out-of-band channel for anything that has to change in
`contracts/`. Per `PROJECT_LAYOUT.md`, a layer never edits another layer's
directory; it records the request here and the integration owner applies it
during the merge window.

## Status legend

| Status | Meaning |
| --- | --- |
| applied | Change is on `feat/plugin-shell` and every layer compiles against it. |
| requested | Needs an owner decision; a workaround is in place. |
| open | Unresolved, tracked for the merge window. |

## Applied during integration

### C-01 `Vec3` was missing the members `GrenadeSimulator` uses

* **Affected layer:** physics-sim, plugin-shell
* **Symptom:** `src/CS2LineupFinder.Core` failed to compile with `CS0117 'Vec3'
  does not contain 'UnitX'`, `CS1061 'Vec3' does not contain 'IsZero'`.
* **Resolution (applied):** `contracts/Geometry.cs` gained `UnitX`/`UnitY`/`UnitZ`,
  `DefaultEpsilon`, `IsZero(epsilon)` and `Reflect(normal, restitution)`, and
  `Normalized(epsilon)` became tolerant of a zero-length input. The simulator now
  compiles with zero warnings against the canonical contract.

### C-02 `TraceHit` naming did not match the engine trace surface

* **Affected layer:** physics-sim, plugin-shell
* **Symptom:** simulator code reads `hit.EndPosition` / `hit.PlaneNormal`, CSSharp
  exposes `TraceResult.EndPos` / `Normal`, and the contract exposed
  `Position` / `Normal` only.
* **Resolution (applied):** `TraceHit` now carries `Hit`, `EndPosition` and
  `PlaneNormal` as alias properties of `DidHit`, `Position` and `Normal`. Both
  vocabularies work, no call site had to change.

### C-03 `GroundZone` had no geometric helpers

* **Affected layer:** plugin-shell
* **Symptom:** the plugin had to know how to test a landing point and how to
  re-centre a circle, which duplicates shape logic in two layers.
* **Resolution (applied):** `GroundZone` gained `HalfWidth`, `HalfHeight`,
  `WithCenter(position)` and `Contains(point)`. `Contains` deliberately ignores Z
  so a grenade resting on a raised surface still counts.

### C-04 `ThrowParams` carried no stance

* **Affected layer:** physics-sim
* **Symptom:** `GrenadeSimulator` reads `parameters.Mode` when building the release
  vector; the class had no such member.
* **Resolution (applied, needs review):** added `ThrowParams.Mode`
  (`ThrowMode`, `init`-only, default `Stand`). This is the only edit made to the
  physics-owned slice of `contracts/`; it is additive and does not change any
  existing initializer.

### C-05 `GrenadeType` / `ThrowMode` enum member names

* **Affected layer:** physics-sim, math-core
* **Symptom:** calibration code used `GrenadeType.HeGrenade` and
  `ThrowMode.FullThrow`, which the canonical contract does not define.
* **Resolution (applied by the physics owner in `7008d29`):** the canonical
  vocabulary is `GrenadeType.{Smoke,Flash,Molotov,He}` and
  `ThrowMode.{Stand,Crouch,Jump}`; `ThrowButton.{Primary,Secondary,Both}` selects
  the throw strength tier. Documented in `contracts/README.md`.

## Process notes for the merge window

### P-01 A stray `git add -A` swept a parallel agent's working tree

An early integration commit (`758db73`) captured files that belonged to
`feat/physics-sim` while they were still in flight, which temporarily removed
`contracts/Vector3.cs` and `contracts/IRaycaster.cs` from the physics agent's
working tree. The commit was unwound with `git reset --mixed` and every affected
path was restored from `54362f4`. No work was lost.

**Rule adopted:** stage with explicit paths only. `git add -A`, `git add .`,
`git clean` and `git checkout --` on a shared tree are forbidden for the rest of
this cycle.

### P-02 `contracts/` ownership is split

`ITrajectorySimulator.cs` is the physics-sim slice of `contracts/`; everything
else is integration-owned. Both slices now live in the same namespace and no
type is declared twice. `contracts/README.md` carries the owner table.

### P-03 Who owns the interface definitions from here

`contracts/` is frozen as of this branch. The next interface change should be a
single commit on `main` during the merge window, with an entry added above.

## Requested (not applied)

### C-06 Split `contracts/` into one project per slice - requested

`CS2LineupFinder.Abstractions.csproj` compiles both the integration slice and the
physics slice, while the plugin additionally globs `contracts/*.cs` into its own
assembly to stay a single DLL.
* **Suggested change:** once `Math` and `Core` both ship, either reference
  `CS2LineupFinder.Abstractions` from the plugin instead of globbing the sources,
  or promote the contracts project to a NuGet-style shared project. Until then the
  glob is what keeps the plugin a single drop-in `CS2LineupFinder.dll`.

### C-07 `SolverOutcome.Message` for timeouts - open

The plugin distinguishes a user timeout from a solver timeout by combining its own
`CancellationTokenSource` with the status returned by `SolveLineupAsync`. If `Core`
reports `SolverStatus.Cancelled` for an internal budget expiry, the plugin cannot
tell the two apart to give a better message. No behaviour depends on it today.

### C-08 The contracts DLL and the plugin's compiled-in contracts are different types - requested

`CS2LineupFinder.Core.csproj` builds against
`contracts/CS2LineupFinder.Abstractions.csproj` through a `ProjectReference`, while
the plugin compiles `contracts/*.cs` into its own `CS2LineupFinder.dll` so it stays
a single drop-in file. Both assemblies declare
`CS2LineupFinder.Contracts.ITrajectorySimulator`, but they are two distinct types
to the runtime, so `typeof(ITrajectorySimulator).IsAssignableFrom(type)` is false
for every real implementation and a `Core.dll` dropped next to the plugin resolves
to the built-in stub. This is the failure mode C-06 predicts, and it is the one
that looks like success from the server console.

Verified on this branch rather than inferred: the release `CS2LineupFinder.Core.dll`
carries a `CS2LineupFinder.Abstractions` metadata reference and the release
`CS2LineupFinder.dll` carries none. `CoreLoader` already detects the situation and
names the file and the reason in the log, but no log line can make the two types
interchangeable.

* **Options, both of which are `main`-level decisions:**
  1. the plugin references `CS2LineupFinder.Abstractions` instead of globbing the
     sources, and ships the DLL beside the plugin (two deployed files, not one);
  2. `Core` and `Math` compile `contracts/*.cs` in exactly as the plugin does, and
     the `ProjectReference` stays a compile-time-only convenience.
* **Not applied:** either change crosses a layer boundary and contradicts an
  assumption the other two branches were written against.

### C-09 `LineupSolution.Pitch` documents the opposite of the agreed convention - requested

`contracts/LineupTypes.cs` says of `Pitch`: "negative values look up, positive
values look down". `contracts/README.md` says positive `pitch` looks **up**, and
`GrenadeSimulator.DirectionFromAngles` and the plugin's stub both implement
positive = up. The README and the code agree, so the XML comment is the odd one
out - but it is the first thing a `Math` implementer reads when writing a solver.

* **Suggested change:** make the `Pitch` summary say "positive values look up,
  matching `contracts/README.md` and `GrenadeSimulator.DirectionFromAngles`", and
  note that the engine's own angle convention is its mirror image. The plugin
  converts at the display boundary only (`CommandParser.RoundPlayerPitch`), so a
  solver must not pre-negate anything.

### C-10 CounterStrikeSharp API facts that constrain every layer - open

Two points recorded so the next agent does not have to rediscover them.

* **`V113+` is not a package version.** The task brief asks for "CSSharp V113+",
  which is the game build number, not the NuGet version. The package that ships a
  `lib/net8.0` target is `CounterStrikeSharp.API`; `1.0.368` is the last such
  release - `1.0.369` and later ship `lib/net10.0` only (`1.0.376` verified in the
  local package cache). A `net8.0` plugin still loads into a .NET 10 host, so
  `1.0.368` is the correct choice for this project and the pin is deliberate.
* **There is no managed trace API in 1.0.368.** `TraceLine`/`TraceShape` on
  `CCSPlayerPawn` and the `TraceResult` type arrive in 1.0.369, which needs the
  .NET 10 runtime. The plugin therefore reaches tracing through
  `ITraceBackend`, with a reflective backend that binds to those members when the
  host has them and a null backend that reports the absence, which is what
  `css_lf_zone`'s "cannot trace" fallback path is for.

### C-11 `ThrowParams.Velocity` is not usable as a speed - open

`ThrowParams.Velocity` is documented as "Initial velocity in u/s, already
resolved", and `ThrowVelocityCalculator.Compute` builds it as
`forward * LaunchSpeed + playerVelocity * VelocityInheritance`. The plugin's stub
passes `normalized(direction) * SpeedFor(button)` instead and reads the length back,
so the two agree on direction and disagree on speed: the stub ignores velocity
inheritance and the aim bias, and its gravity is the 800 u/s^2 player value rather
than the 320 u/s^2 `PhysicsParameters.GrenadeGravity` (documented in
`docs/PHYSICS.md`). A real `Core` simulator will therefore answer a request with a
different arc than the stub, which is expected, but it means the stub's numbers
cannot be used as expected values in any test that later runs against Core.

* **Suggested change:** say in the doc comment that only the direction of
  `Velocity` is contractual for the simulator, or make `Compute` the only way a
  caller builds the vector so stub, `Core` and tests agree by construction.
