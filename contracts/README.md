# contracts — shared interfaces

This folder is the **only** place where the three layers meet. It contains
interfaces and DTOs, never logic, and it must stay free of CSSharp (and any other
engine) types so that `Math`, `Core` and unit tests can all use it.

| File | Owner | Purpose |
| --- | --- | --- |
| `Geometry.cs` | integration | `Vec3` primitive, `GroundZone` landing area, `TraceHit`, `SimulationEnvironment`. |
| `Enums.cs` | integration | `GrenadeType`, `ThrowMode`, `ThrowButton`, `SolverStatus` vocabulary shared by every layer. |
| `LineupTypes.cs` | integration | `ThrowOrigin`, `LineupRequest`, `LineupSolution`, `SolverOutcome`, `GrenadeProfile`, `StanceProfile`. |
| `ILineupSolver.cs` | integration | Inverse solver contract implemented by `Math`, consumed by `Core`. |
| `ITrajectorySimulator.cs` | physics-sim | Forward simulation (`Simulate`, `SimulateWithDiagnostics`) + `SolveLineupAsync` entry point used by the plugin. |
| `IWorldGeometry.cs` | integration | Ray/trace abstraction that keeps `Core` free of engine types. |
| `IVDataProvider.cs` | integration | Grenade physics profile lookup (`weapon_*.vdata`). |
| `IMovementProvider.cs` | integration | Player stance / throw-origin prediction inputs. |

## Conventions every layer must honour

* **Angles.** `yaw = 0` looks down `+X`, positive `yaw` turns towards `+Y`,
  positive `pitch` looks **up**. Yaw values returned to players are normalized
  to `-180..180`. This matches CS2's `QAngle` after the +90° eye/entity offset
  is applied and is the convention `GrenadeSimulator.DirectionFromAngles` uses.
* **Units.** World units (1 unit = 1 inch), seconds, degrees. Right-handed,
  `+Z` up.
* **Degeneracy.** `Vec3.Normalized()` and `Vec3.IsZero()` treat a length at or
  below `Vec3.DefaultEpsilon` as zero; tracers must return a miss rather than
  throw.

## Adapter seams

`TraceHit` exposes both `Position`/`Normal` and the engine-flavoured
`EndPosition`/`PlaneNormal` aliases so an engine trace implementation and the
simulator can each use the name they know. `GroundZone.Contains` ignores Z, so a
grenade resting on a raised surface still counts as inside the area.

## Compiling against the contract without owning it

The project `CS2LineupFinder.Abstractions.csproj` compiles these files so a
consumer can reference them with a normal `ProjectReference`:

```xml
<ProjectReference Include="..\..\contracts\CS2LineupFinder.Abstractions.csproj" />
```

When `Math` / `Core` ship their own contract project, only this reference path
needs to be updated — no source file in a plugin changes.

## Change policy

`contracts/` is frozen for the duration of a feature branch. If an interface is
wrong, add an entry to `docs/contract-issues.md` (symptom, affected layer,
suggested change) instead of editing the file, and the integration owner applies
the change during the merge window.
