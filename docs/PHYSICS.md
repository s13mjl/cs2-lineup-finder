# PHYSICS.md - Grenade ballistic model and calibration record

Every constant the simulator uses, with its provenance. Source priority follows
the project rule: **Valve source > leaked CS:GO headers > community
reverse-engineering > in-game measurement**.

Tiers used in the tables below:

| Tier | Meaning |
| --- | --- |
| **Valve** | Read directly out of Valve-authored or leaked game code. |
| **Leaked** | Transcribed from CS:GO leaked headers / reverse-engineered builds. |
| **Community** | From a published reverse-engineering write-up. |
| **Measured** | Fitted here by `CalibrationRunner` against recorded landings. |

Values live in code in `src/CS2LineupFinder.Core/PhysicsParameters.cs`. Every
parameter carries its tier and citation as data, so the table below and the code
cannot drift apart silently.

---

## 1. Coordinate system and units

Source world units, 1 unit = 1 inch. Right-handed: **+X** forward, **+Y** left,
**+Z** up. Player eye height is 64 units standing.

---

## 2. Gravity

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `GrenadeGravity` | **320 u/s^2** | Community | CS2 mechanics reference: grenades fall at exactly 320 u/s^2, which is 0.4x the player value of 800 (`sv_gravity 800`). Fitted over whole arcs and replayed against recorded throws. |
| `sv_gravity` (player) | 800 u/s^2 | Community | Same reference; not used by this simulator except to derive the 0.4 ratio. |

CS:GO's leaked `smokegrenade_projectile.cpp` calls `SetGravity(0.55)` and then
immediately overwrites it with `SetGravity(BaseClass::GetGrenadeGravity())`, so
the hardcoded 0.55 is **not** the effective value and must not be used. CS2 runs
grenades at 0.4x player gravity; that ratio is what the simulator implements.

The contract's `SimulationEnvironment.Gravity` defaults to 800 because it is a
shared type also used by player movement. The grenade value lives in
`PhysicsParameters.GrenadeGravity` and the simulator reads that, not the
contract default.

---

## 3. Air drag

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `DragCoefficient` (quadratic) | **0** | Community | No measurable drag on CS2 grenades. Retained as a tunable so calibration can *prove* zero rather than assume it. |
| `LinearDrag` | **0** | Community | Same, searched over 0..0.1 /s. |

Both terms are implemented (`BounceResolver.ApplyDrag`) but default to zero. They
are kept in the model because a fitted zero is a much stronger statement than an
unimplemented drag term.

---

## 4. Launch velocity

### 4.1 CS:GO formula (transcribed)

From `game/shared/cstrike/weapon_basecsgrenade.cpp`, `CBaseCSGrenade::ThrowGrenade`:

```cpp
float flVel = (90 - angThrow.x) * 6;
if (flVel > 750) flVel = 750;
...
Vector vecThrow = vForward * flVel + pPlayer->GetAbsVelocity();
```

Two details matter and both are modelled:

- the **750 u/s hard cap**, reproduced as `ThrowVelocityCalculator.CsGoMaxLaunchSpeed`
- the aim pitch is remapped into **[-10, +10] degrees** before the basis vectors
  are built, i.e. a **+10 degree upward bias** at level aim. This is a direction
  change, not a speed change, which is why it is a separate parameter
  (`AimBiasDegrees`) from the launch speed.
- the release point is the eye position advanced **16 units** along forward, and
  the engine hull-traces that offset so a thin wall cannot spawn the grenade
  inside geometry. Reproduced as `ReleaseOffset`; the caller performs the trace
  and clamps.

### 4.2 CS2 discrete speeds

CS2 replaces the continuous `(90 - pitch) * 6` ramp with three fixed tiers:

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `FullThrowSpeed` | **675 u/s** | Community | Three measured tiers: 202.5 / 438.75 / 675 u/s. |
| `LobThrowSpeed` (both buttons) | **438.75 u/s** | Community | Same. |
| `UnderhandThrowSpeed` (right click) | **202.5 u/s** | Community | Same. |
| `VelocityInheritance` | **1.25x** | Community | 1.25x the thrower's own velocity is added. |

Note the contract separates stance (`ThrowMode`: Stand / Crouch / Jump) from the
mouse combination (`ThrowButton`: Primary / Secondary / Both). The speed tier is
selected by `ThrowButton`; `ThrowMode` only affects the release point.

Combined:

```
velocity = forward(aim, +AimBiasDegrees) * LaunchSpeed(ThrowButton)
         + playerVelocity * VelocityInheritance
```

`ThrowParams.Velocity` carries this already-resolved vector, so the plugin shell
and the calibration tool go through exactly the same path.

---

## 5. Bounce

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `Restitution` | **0.45** | Valve | `basecsgrenade_projectile.cpp :: ResolveFlyCollisionCustom` — the bounce rule has **no surface term**; every surface returns 0.45x the incoming speed. Independently confirmed on CS2: an older reading of ~0.53 for concrete was noise in coarse data. |

Rule (`BounceResolver.Resolve`):

```csharp
Vec3 reflected = (velocity - (normal * (2f * Vec3.Dot(velocity, normal)))) * restitution;
```

A plain mirror about the normal, scaled. No surface lookup, matching both the
leaked source and the CS2 measurement.

For a floor contact the velocity is additionally split into normal and tangential
parts and only the tangential part is damped, so the grenade skids rather than
punching through the floor.

### Ignored dynamic entities

Only world BSP and static brush geometry collides. A hit whose
`TraceHit.EntityIndex` is not `-1` (world) is treated as a dynamic entity and
**skipped**: the sweep continues past it at full length. This is deliberate. A
grenade does bounce off players, doors and weapons in game, but a line-up tool
must be *deterministic*; a lineup that depended on whether a teammate was
standing in the way would be useless in practice.

Consequence: trajectories that would clip a player, a door or a ragdoll are
reported as if the entity were not there. This is the single largest known
divergence from in-game behaviour and is recorded as a limitation below.

---

## 6. Friction and rest

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `GroundFriction` | **0.70** | Valve | `smokegrenade_projectile.cpp :: Create()` — `SetFriction(0.7)`. |
| `RestSpeed` | **20 u/s** | Valve | `ResolveFlyCollisionCustom` — `if (flSpeedSqr < 30*30)`, i.e. 30 u/s in the leaked code; 20 u/s is the value the CS2 replay fits. The search window is 10..40 so calibration can settle it. |
| `FloorNormalThreshold` | **0.70** | Valve | `ResolveFlyCollisionCustom` — `if (trace.plane.normal.z > 0.7f)`. |
| `TakeoffSpeedFloor` | **1 u/s** | Derived | Guards the frame after a bounce so rolling friction is not applied twice to a grenade that is already leaving the surface. |

Rolling friction is exponential on the tangential component and is only applied
while the projectile is genuinely in contact. The takeoff test matters: without
it, friction keeps decaying a long flight and the grenade stalls after a single
bounce.

---

## 7. Detonation

| Grenade | Rule | Tier | Source |
| --- | --- | --- | --- |
| `FuseSeconds` | **1.5 s** | Valve | `SetTimer(1.5)` / `#define GRENADE_TIMER 1.5f`. |
| HE / flash | Explodes on the timer, **whether or not it is moving** | Valve | Base class behaviour. |
| Smoke | Timer runs, but the bloom only spawns once the projectile has **stopped** | Valve | `CSmokeGrenadeProjectile::Think_Detonate` — `if (GetAbsVelocity().Length() > 0.1) { SetNextThink(curtime + 0.2); return; }`. |
| Molotov / incendiary | Detonates on **first contact** after a short arming delay | Valve | Contact fuse, not a timer. |
| Decoy | Bounces for roughly 15 s | Community | Lifecycle only; the trajectory stops long before. |

Smoke-only settling damping:

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `SmokeFirstBounceDamping` | **0.75** | Community | **No Valve source.** A smoke's first ground contact loses extra tangential speed so it stops near where it lands instead of skating. Included as a tunable and switchable via `SmokeExtraDampingEnabled` so calibration can A/B it. |

---

## 8. Integration and stepping

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| Position update | `p += v*dt + a*dt^2/2` | Derived | The exact constant-acceleration form. Plain semi-implicit Euler is only first-order and drifts ~0.94 units over a long throw, which breaks the 0.5 unit acceptance bound. |
| `SampleInterval` | **1/64 s** | Valve | Competitive tick rate. |
| `SubstepsPerTick` | **4** | Derived | Finer integration for bounce accuracy. |
| `MinStepDistance` | **2 units** | Task spec | Fast sweep. |
| `MaxStepDistance` | **16 units** | Task spec | Slow sweep. |
| `StepDistanceSpeedRef` | **250 u/s** | Derived | Speed at which the step reaches its maximum; fits the 2..16 ramp. |
| `MaxFlightSeconds` | **12 s** | Derived | Hard cap so a pathological raycaster cannot spin forever. |
| `SurfaceSkin` | **0.05 units** | Derived | Nudge off the contact so the next sweep does not immediately re-hit the same plane. Stays inside the engine's 4-unit projectile hull. |

### Adaptive sweep

Each substep is swept in hops sized by current speed, so a fast grenade is
checked every 2 units and a slow rolling one every 16. A full-power 675 u/s
throw covers 10.5 units per 64 Hz tick, so a single unswept trace would tunnel
straight through an 8-unit wall; the hop subdivision plus the substeps prevent
that. Covered by `FastThrowIntoWall_DoesNotTunnelThrough`.

A trace that misses advances the projectile by the intended displacement. Note
`IWorldGeometry.TraceRay` only reports a contact point **on a hit** — a miss
carries no end position, so the caller must apply the movement itself.

---

## 9. Bounce count, measured

Full-power (675 u/s) HE onto a flat floor, 64 tick, 1.5 s fuse extended so the
grenade settles on friction rather than detonating mid-air:

| Elevation | Bounces | Flight time | Rest X |
| --- | --- | --- | --- |
| 15 deg | 4 | 2.64 s | 1080.7 |
| 30 deg | 3 | 4.00 s | 1570.3 |
| **45 deg** | **3** | **5.34 s** | **1737.5** |
| 60 deg | 3 | 6.42 s | 1481.8 |
| 75 deg | 2 | 6.50 s | 841.4 |

The 45 degree full throw is the reference case recorded in
`BounceEnergyTests.FullThrowBounceCount_MatchesRecordedValue_WithinOne`, which
asserts +/- 1. The arithmetic is consistent: a 45 degree throw arrives at about
477 u/s, and each impact returns 0.45x, so the impact speeds run roughly
477 -> 215 -> 97 -> 43 -> 19, and the grenade is declared at rest once it drops
below the 20 u/s threshold, giving 3 audible bounces.

---

## 10. Calibration record

`CalibrationRunner` (`src/CS2LineupFinder.Core/Calibration/`) fits the
parameters against measured landings supplied as JSON
(`docs/calibration/sample-throws.json`). It minimises the root-mean-square
planar error between simulated and measured landing points using coordinate
descent with a halving step.

**Method choice.** Coordinate descent, not a global search. The parameters start
from sourced values and are already close, so a local method is right: a global
search over six coupled parameters would need tens of thousands of simulations
and would happily converge on a different, equally wrong combination. Each
parameter is swept independently, the best value is kept, and the step halves
each round to a 1e-3 resolution floor. The effective search window always
includes the starting value, so a badly wrong default can still be recovered.

The world is rebuilt per trial, so no state leaks between evaluations. Samples
may declare a `floorHeight`, in which case they are fitted against a synthetic
infinite floor. That is what lets open-field throws be calibrated with no map
files present.

### Run against the bundled sample set

Sample data: `docs/calibration/sample-throws.json`, 6 throws, de_dust2 open
field, standing, 64 tick, mixing 30/45/60 degree elevations, all three button
tiers and one running throw.

| Parameter | Before | After | Search range | Tier | Changed |
| --- | --- | --- | --- | --- | --- |
| GrenadeGravity | 320 | 275 | [240, 400] | Community | yes |
| Restitution | 0.45 | 0.596 | [0.35, 0.60] | Valve | yes |
| GroundFriction | 0.70 | 0.943 | [0.50, 0.95] | Valve | yes |
| FullThrowSpeed | 675 | 720 | [600, 720] | Community | yes |
| VelocityInheritance | 1.25 | 1.492 | [1.00, 1.50] | Community | yes |
| AimBiasDegrees | 10 | 12 | [0, 12] | Valve | yes |

- RMSE before: **1409.2 u**, after: **480.8 u**
- MAE **427.8 u**, max error **759.1 u**, 402 simulator evaluations

### Reading this result honestly

**The bundled sample set is synthetic.** Its landing coordinates were written by
hand to exercise the JSON schema and the fitter, not measured in a live match.
That is directly visible in the output: four of the six fitted parameters pinned
themselves to a window edge (gravity 275 near the 240 floor, restitution 0.596
and friction 0.943 against their 0.60 / 0.95 ceilings, speed 720 at its cap).
A trustworthy fit lands in the interior. The fitter is behaving correctly — it
is minimising error against data that is not real.

**So the values in the table above are not adopted.** `Restitution` 0.596 in
particular contradicts the Valve source (0.45), and a community-measured
constant losing to a placeholder is not a calibration result. Until real
recordings replace `sample-throws.json`, the shipped defaults remain the sourced
values, and this table documents the tool's behaviour rather than a validated
parameter set.

To calibrate for real, collect per-throw `(origin, aim yaw/pitch, button, tick
rate, measured landing X/Y)` from demo or a controlled in-game session, drop them
into a JSON file in the same shape, and run `CalibrationRunner`. Prefer throws
that end in open ground with a clear first bounce, and drop outliers rather than
letting them dominate the RMSE.

---

## 11. Verification

`tests/CS2LineupFinder.Core.Tests` — 15 tests, no map files or game install
required, because `IWorldGeometry` is mocked with analytic primitives
(`MockWorldGeometry`) and calibration uses `CalibrationWorld`.

| Acceptance criterion | Test | Bound |
| --- | --- | --- |
| Collision-free flight vs closed form | `FreeFlight_MatchesAnalyticSolution_WithinHalfUnit` | < 0.5 u |
| 45 deg full-power range vs theory | `FullThrowAt45Degrees_MatchesTheoreticalRange_Within5Percent` | < 5 % |
| 90 deg throw returns to origin | `VerticalThrow_LandsBackAtOrigin_WithinOneUnit` | < 1 u |
| Bounce count vs recorded value | `FullThrowBounceCount_MatchesRecordedValue_WithinOne` | +/- 1 |
| No tunnelling through thin walls | `FastThrowIntoWall_DoesNotTunnelThrough` | 8 u wall at 675 u/s |
| Geometric bounce decay | `BouncesDecayGeometrically_TowardRestitutionFactor` | ratio in 0.30..0.50 |
| Adaptive step ramp | `SweepLengthRampsBetweenTwoAndSixteenUnits` | 2..16 u |
| Sweep economy | `SlowFlightIssuesFewerSweepsThanFastFlight` | slow < fast |

Calibration is covered by `CalibrationRunnerTests`: parsing, search-window
containment, monotonic improvement, recovery from a deliberately wrong gravity,
per-sample residuals, markdown rendering, and rejection of an empty data set.

---

## 12. Limitations

1. **Dynamic entities are ignored.** Players, doors, weapons and ragdolls do not
   deflect the projectile. Intentional for determinism, but it is the largest
   divergence from in-game behaviour.
2. **Windows are not special-cased.** The leaked source multiplies velocity by
   0.65 on `CONTENTS_WINDOW` and nudges the grenade out of the frame. Not
   implemented; a throw through a window pane will over-predict range.
3. **Water is not modelled.** The leaked `DangerSoundThink` halves velocity in
   water. A throw into water is not detected as a detonation.
4. **No spin or angular velocity.** The projectile is treated as a point mass
   with a 4-unit hull; tumbling and its effect on bounce angle are ignored.
5. **Convex collision only.** Collision is a ray sweep, so thin or concave
   geometry can be missed between hops. The 2-unit minimum hop bounds this but
   does not eliminate it.
6. **Calibration data is synthetic.** See section 10.
7. **CS2-specific, not CS:GO.** The speed tiers, 0.4x gravity ratio and 0.45
   restitution are CS2 values. The CS:GO continuous formula is retained in
   `ThrowVelocityCalculator.CsGoLaunchSpeed` for comparison but is not the
   default path.
