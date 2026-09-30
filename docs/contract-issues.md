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

