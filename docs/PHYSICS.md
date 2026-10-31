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
| `GrenadeGravity` | **320 u/s^2** | Leaked | basecsgh.h L66: `GetGrenadeGravity() { return 0.4f; }` under "Constants for all CS Grenades", so every grenade projectile falls at 0.4 x sv_gravity 800 = 320 u/s^2. memorin.app's CS2 measurement agrees. |
| `sv_gravity` (player) | 800 u/s^2 | Community | Standard Source player gravity, still community-sourced; the simulator only needs the 0.4 ratio, which is now leak-sourced. |

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
| `DragCoefficient` (quadratic) | **0** | Measured | CalibrationRunner searched 0..0.02 against recorded landings and kept 0; no measurable drag on CS2 grenades. |
| `LinearDrag` | **0** | Measured | Same, searched over 0..0.1 /s. |

Both terms are implemented (`BounceResolver.ApplyDrag`) but default to zero. They
are kept in the model because a fitted zero is a much stronger statement than an
unimplemented drag term.

---

## 4. Launch velocity

### 4.1 CS:GO formula (transcribed)

From `game/shared/cstrike15/weapon_basecsgrenade.cpp`, `CBaseCSGrenade::ThrowGrenade` (leak mirrored in docs/evidence/):

```cpp
float flVel = clamp((kBaseVelocity * 0.9f), 15, 750);   // L586
...
Vector vecThrow = vForward * flVel + (pPlayer->GetAbsVelocity() * 1.25);   // L614
```

Two details matter and both are modelled:

- the **750 u/s hard cap**, reproduced as `ThrowVelocityCalculator.CsGoMaxLaunchSpeed`
- the aim pitch is remapped by the leaked formula
  `angThrow[PITCH] -= 10.0f * (90.0f - fabsf(angThrow[PITCH])) / 90.0f`
  (weapon_basecsgrenade.cpp L580) before the basis vectors are built — a
  **+10 degree upward bias at level aim**, lerping to zero at the +/-90 degree
  extremes. This is a direction change, not a speed change, which is why it is
  a separate parameter (`AimBiasDegrees`, 10) from the launch speed.
  `ThrowVelocityCalculator.ApplyAimBias` implements the full pitch-dependent
  remap and `UndoAimBias` inverts it to 1.4e-14 degrees over [-89, 89].
  The 2026-10-15 CS2 telemetry shows launch pitches of 67.7 up / 46.7 down for
  throws whose view angles were not recorded, so those two points cannot judge
  the level-aim bias; a recorded-landing replay sweep across bias values shows
  no single constant dominating all five plane-replayed samples (each sample's
  optimum scatters between 0 and 10, consistent with per-sample map-geometry
  error, not a bias misread). The leak constant 10 therefore stays, and the
  sweep ships as a diagnostic (`AimBiasSweepDiagnostic`) for future
  same-geometry A/B data.
- the release point is the eye position advanced **16 units** along forward, and
  the engine hull-traces that offset so a thin wall cannot spawn the grenade
  inside geometry. Reproduced as `ReleaseOffset`; the caller performs the trace
  and clamps.

### 4.2 CS2 discrete speeds

CS2 replaces the continuous `(90 - pitch) * 6` ramp with three fixed tiers.
The base speed 750 is backed by Valve's own CS2 data: the released
`weapons.vdata` (game/csgo/pak01_dir/scripts/weapons.vdata, distributed via
the GameTracking-CS2 repository) sets `m_flThrowVelocity = 750.0` on every
grenade prefab, including `weapon_smokegrenade_prefab` (L4321), HE (L409) and
flashbang (L3936). The leak chain then parses and applies it:
`cs_weapon_parse.cpp` L844 reads the key into `m_fThrowVelocity`, and
`wpncsg.cpp` L586 clamps `flVel = clamp(kBaseVelocity * 0.9, 15, 750)`, so
the full-power tier is 750 * 0.9 = 675 u/s. The tier ratios still come from the
leak (`GRENADE_SECONDARY_DAMPENING 0.3f` L82, `flIdealThrowStrength = 0.5f`
L361, `Lerp(strength, 0.3, 1)` L593) and remain pending telemetry
confirmation; memorin.app's independent CS2 measurements match all three.

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `FullThrowSpeed` | **675 u/s** | Valve | CS2 weapons.vdata `m_flThrowVelocity = 750.0` (smoke L4321) x leaked wpncsg.cpp L586 0.9 factor = 675. |
| `LobThrowSpeed` (both buttons) | **438.75 u/s** | Leaked | L593 `Lerp(strength, 0.3, 1)` at strength 0.65 (L361 0.5 + 0.15 transition tail): 675 * 0.65. |
| `UnderhandThrowSpeed` (right click) | **202.5 u/s** | Leaked | L82 `GRENADE_SECONDARY_DAMPENING 0.3` at full strength: 675 * 0.3. |
| `VelocityInheritance` | **1.25x** | Valve | weapon_basecsgrenade.cpp L614 (cstrike15 leak, mirrored in docs/evidence/): `vecThrow = vForward * flVel + (pPlayer->GetAbsVelocity() * 1.25)`. CS:GO and CS2 share the constant; telemetry-002 row 0 (678.63 u/s vs the 675 full-throw constant) confirms it live in CS2. |

Note the contract separates stance (`ThrowMode`: Stand / Crouch / Jump) from the
mouse combination (`ThrowButton`: Primary / Secondary / Both). The speed tier is
selected by `ThrowButton`; `ThrowMode` only affects the release point.

### 4.4 Thrower-velocity inheritance (1.25x)

The launch velocity is not just the aim vector times the tier speed: the
thrower's own velocity is added on top, scaled by 1.25. This is visible in the
leak itself (`weapon_basecsgrenade.cpp` L614, quoted in section 4.1) and was
confirmed live in CS2 by telemetry-002 row 0: a stationary thrower's full
launch measured 678.63 u/s against the 675 full-throw constant, with the
excess tracking the player's own drift.

**Why this mattered.** The 2026-10-18 calibration batch was first read as
stationary-thrower samples replaying to 0.0-11.6 u against 75.9 u / 132.8 u
for two "moving" palace throws. That attribution was wrong: the 1015 throw's
projectile attached at **675.4 u/s**, i.e. a stationary full throw, and the
75.9 / 132.8 u came from the speed-only R4 embedded rule over-scaling a q=2
contact on the palace lower shelf (section 5). The q=1 gate took those two
samples to 4.6 u / 1.8 u and the mean from 30.8 u to 5.5 u. The
live `!lf_find` path used to carry the raw player velocity without the 1.25
factor, so a genuinely moving throw landed short along the aim axis by
roughly 0.25 x player speed x flight time. The solver now bakes the
inheritance into the launch velocity exactly once
(`EvaluateCandidate` / `SimulatorImpactEvaluator.TryEvaluate`). New
calibration samples now really do record the projectile's own launch position
and velocity (`LandingSample.LaunchPosition` / `LaunchVelocity`); the event
handler previously cleared those fields before passing them to the
calibrator, so every recorded sample fell back to the synthesised aim + speed
launch.

### 4.3 Discretisation knobs are measured, not guessed

`SubstepsPerTick`, `MinStepDistance`, `MaxStepDistance` and
`StepDistanceSpeedRef` describe our integrator, not the engine, so they were
never physics claims. All were settled by measurement in
`DiscretizationConvergenceTests` (2026-10-14),
replaying the recorded 1013 palace shallow throw on an analytic floor and
against a vertical wall that exercises the leftover-advance bounce path:

| Check | Floor scenario | Wall scenario |
| --- | --- | --- |
| Step-distance grid (min 1/2/4 x max 8/12/16/24 x ref 100/250/400, 36 combos) | max landing shift **0.98u** | max landing shift **0.17u** |
| Substep refinement 1 -> 16 (total spread) | 13.46u, monotone | 4.61u, monotone |
| Default 4 substeps vs converged | ~2.5u | ~0.9u |
| Default 8 substeps vs converged | ~0.1u | ~0.1u |

Consequence: the three step-distance values cannot meaningfully move the
landing (they are graded `Measured` with these numbers), and the integration
default was raised from 4 to 8 substeps per tick, which cuts the
discretisation error by about 25x at roughly double the raycast cost. The
search timeout, not the integrator, remains the accuracy budget. The flight-time
cap received the same treatment in `FlightCapIrrelevanceTests`: no simulated
grenade flight can reach it, so its value is landing-invariant too.

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
| `Restitution` | **0.45** | Valve | basecsgh.h L68: `GetGrenadeElasticity() { return 0.45f; }`, applied unconditionally by `ResolveFlyCollisionCustom` (`flTotalElasticity = 0.45 x surface 1.0`, clamped to 0.9) — the bounce rule has **no surface term**; every surface returns 0.45x the incoming speed. The telemetry analyser recovers 0.45 from flat-floor captures; an older reading of ~0.53 for concrete was noise in coarse data. |

Rule (`BounceResolver.Resolve`):

```csharp
Vec3 reflected = (velocity - (normal * (2f * Vec3.Dot(velocity, normal)))) * restitution;
```

A plain mirror about the normal, scaled. No surface lookup, matching both the
leaked source and the CS2 measurement.

For a floor contact the velocity is additionally split into normal and tangential
parts so the simulator can keep the tangential component intact (measured below).

Measured on CS2 (2026-10-15, two live smoke throws, full per-tick telemetry,
telemetry-001/002 in the plugin data folder): the bounce scale applies to the
whole vector exactly as the leak describes — the total-speed ratio equals the
vertical ratio at every contact, and the tangential component carries through
bit-exact between bounces:

| Capture | Contact | total-speed ratio | vertical ratio | horizontal ratio |
| --- | --- | --- | --- | --- |
| #1 (370 states @ 64 Hz) | bounce 1 (712 u/s impact) | 0.252 | 0.252 | 0.255 |
| #1 | bounce 2 | 0.443 | 0.442 | 0.450 |
| #1 | bounce 3 | 0.434 | 0.431 | 0.450 |
| #1 | settle (35.9 u/s pre-contact, post-bounce 16.2 below 20 threshold) | 0.000 | 0.000 | 0.000 |
| #2 (164 states @ 64 Hz) | bounce 1 (705 u/s impact) | 0.335 | 0.333 | 0.336 |
| #2 | bounces 2-5 | 0.457 / 0.460 / 0.472 / 0.500 | matching vertical | **0.450 at every one** |
| #2 | settle (20.5 u/s pre-contact, post-bounce 9.2 below 20 threshold) | 0.000 | 0.000 | 0.000 |

The sub-0.45 first-bounce readings are fully explained by the steep-impact
bounce padding in `ResolveFlyCollisionCustom`: for a post-bounce speed above
`sqrt(96000)` (~310 u/s) with `alongDist > 0.5`, the whole reflected vector is
multiplied by `(1 - alongDist) + 0.5`. Recomputing every first contact with the
leak formula closes the loop to sampling accuracy:

| Capture | expected vz (0.45 mirror, then padding) | measured vz | expected vt | measured vt |
| --- | --- | --- | --- | --- |
| #1 bounce 1 | 169.4 (pad 0.566, along 0.934) | 167.4 | 64.9 | 64.8 |
| #2 bounce 1 | 178.3 (pad 0.749, along 0.751) | 176.3 | 156.9 | 156.6 |
| #1 bounce 2-3, #2 bounce 2-4 (no padding) | 0.45 x incoming | exact match | exact match | exact match |

The ~2 u/s residual on the normal component is the half-tick gravity phase
between the recorded pre-impact sample and the true contact instant, not a
physics term. **There is no separate tangential loss term in CS2**: after
padding, the horizontal ratio is exactly the restitution at every bounce, and
the padding formula from the leaked source reproduces both first contacts.

#### Embedded clip-brush contacts (R4 + q=1 gate, 2026-10-23)

A contact that begins embedded in a PlayerClip/NpcClip/StaticLevel brush
(engine IsAllSolid=1, measured with the live tracer) is additionally scaled by
ClipEmbeddedElasticityScale = 0.7476252 (= 0.336431361 / 0.45, 7-digit
agreement on both tangential components of telemetry-002 row 8), **iff the
contact lands in the FIRST half of its tick (q=1) and the post-restitution
reflected speed exceeds EmbeddedScaleSpeedThreshold = sqrt(96000) ~ 309.8387
u/s**. The constant is the speed threshold of ResolveFlyCollisionCustom in the
CS:GO leak (basecsg.cpp); its ROLE as the gate for the embedded scale is a
hypothesis confirmed by live replay, not by the leak itself. The q=1 gate was
added after the palace counterexample below.

Decision table over every embedded contact captured so far (reflected speed is
0.45 times the incident speed; a mirror preserves length):

| Capture | Row | embedded | q | reflected speed | engine eH | verdict |
| --- | --- | --- | --- | --- | --- | --- |
| telemetry-002 | 8 | yes | 1 | 317.9 u/s | 0.3364314 | scaled |
| telemetry-001 | 65 | no | - | 154.8 u/s | 0.45 | not scaled |
| telemetry-001 | 128 | yes (first of flight) | - | 153.9 u/s | 0.4499998 | not scaled |
| telemetry-001 | 152/162 | no | - | low | 0.45 | not scaled |
| telemetry-002 | 97/136/154 | yes | - | 101.6 / 46.6 / 21.5 u/s | 0.45 | not scaled |
| 1015 palace (`landings-20261015.json`) | first live contact | yes | 2 (fraction 0.758) | 343.1 u/s | 0.46 (measured 762.5 -> 344.2) | **not scaled** |

An earlier "first embedded contact only" rule (R2, deployed 2026-10-19 13:59)
was FALSIFIED by telemetry-001 row 128: that row is the FIRST embedded contact
of its flight, incident speed 342.0 u/s, yet the engine records 0.4499998 -
because the post-restitution speed 0.45 x 342.0 = 153.9 falls below the
threshold. R2 over-scaled that contact and produced the 40.77 u live-replay
gap at tick 128. R4 (speed only) then over-scaled the 1015 palace contact:
the engine kept 0.46 because that contact is q=2, while R4 reduced it to
0.336 and left the simulated grenade on the palace lower shelf. The live
calibration replay fell from 132.8 u to **1.8 u** (1014: 75.9 -> 4.6 u, mean
30.8 -> 5.5 u) once q=1 was required. Both telemetry tick replays remain
0.019 / 0.023 u, and the synthetic T2/T1/R4 tests pin the gate (<c>EmbeddedScaleRuleTests</c>).

Sampling incident (2026-10-15, resolved): both captures recorded every state
twice because OnTick fired twice per engine tick (742 raw rows = 2 x 370 real
states). The duplicated time axis halved the JSON gravity fit to 160 u/s^2 and
doubled ImpactTime; the physics constants above were unaffected because they
derive from velocity ratios and per-state differences, never from timestamps.
The plugin now guards OnTick with the engine tick counter and the recorder
additionally drops same-tick duplicates, so future captures are 64 Hz single
samples.

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
| `GroundFriction` | **1.00** | Measured | CS2 telemetry 2026-10-15: tangential retention between bounces is exactly 1.0 (horizontal bounce ratio 0.450 = restitution, tangential speed bit-exact during flight). The leak's `SetFriction(0.7)` in `smokegrenade_projectile.cpp :: Create()` is a dead write in the same block as the overridden `SetGravity(0.55)` (see section 7), so 0.7 has no effect on flight. |
| `RestSpeed` | **20 u/s** | Leaked | basecsg.cpp L349: `const float kSleepVelocity = 20.0f`. Older comments claiming 30*30 were a transcription error; the leak's literal value is 20. |
| `FloorNormalThreshold` | **0.70** | Valve | `ResolveFlyCollisionCustom` — `if (trace.plane.normal.z > 0.7f)`. |
| `TakeoffSpeedFloor` | **1 u/s** | Derived | Guards the frame after a bounce so rolling friction is not applied twice to a grenade that is already leaving the surface. |

Rolling friction is exponential on the tangential component and is only applied
while the projectile is genuinely in contact. The takeoff test matters: without
it, friction keeps decaying a long flight and the grenade stalls after a single
bounce.

**The roll rate is measured to be zero.** The 2026-10-15 CS2 telemetry shows the
tangential component constant to the recorded digit through every inter-bounce
flight (254.550 u/s held for 134 ticks, 156.562 u/s held for 110 ticks) — the
ground phase between bounces is flight with gravity, not a friction regime, and
all speed loss happens at contacts through the restitution scale. The CS:GO
leak's `SetFriction(0.2)` (basecsgh.h L67 `GetGrenadeFriction`) feeds vphysics
surface properties, not this motion. `GroundRollFrictionRate` ships as
`Measured` 0.0: the model applies no rolling/sliding decay, and rest comes from
restitution decay to the 20 u/s sleep threshold.

---

## 7. Detonation

| Grenade | Rule | Tier | Source |
| --- | --- | --- | --- |
| `FuseSeconds` | **1.5 s** | Valve | `SetTimer(1.5)` / `#define GRENADE_TIMER 1.5f`. |
| HE / flash | Explodes on the timer, **whether or not it is moving** | Valve | Base class behaviour. |
| Smoke | Timer runs, but the bloom only spawns once the projectile has **stopped** | Valve | `CSmokeGrenadeProjectile::Think_Detonate` — `if (GetAbsVelocity().Length() > 0.1) { SetNextThink(curtime + 0.2); return; }`. |
| Molotov / incendiary | Detonates on **first contact** after a short arming delay | Valve | Contact fuse, not a timer. |
| Decoy | Bounces for roughly 15 s | Community | Lifecycle only; the trajectory stops long before. |

Note on a decoy reading of the leak: `smokegrenade_projectile.cpp` Create()
sets `SetGravity(0.55)` and `SetFriction(0.7)`, but a few lines later both are
overwritten by the BaseClass getters (basecsgh.h L66-68: 0.4 / 0.45 — the leak
declares no separate smoke friction constant beyond the shared 0.2 getter).
Those writes are dead code, not CS-specific constants.

Smoke-only settling damping:

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| `SmokeFirstBounceDamping` | **1.00** (off) | Measured | The 2026-10-15 CS2 telemetry shows the smoke's first bounce follows the same full-vector restitution as every other contact (capture #1: 0.252 total / 0.255 horizontal at 712 u/s; capture #2: 0.335 / 0.336 at 705 u/s). The low first-bounce ratios are the steep-impact bounce padding of `ResolveFlyCollisionCustom`, not smoke-specific damping. Smokes do not settle faster than other grenades in CS2; the tunable stays at its neutral 1.0 and `SmokeExtraDampingEnabled` stays 0. |

---

## 8. Integration and stepping

| Parameter | Value | Tier | Source |
| --- | --- | --- | --- |
| Position update | `p += v*dt + a*dt^2/2` | Derived | The exact constant-acceleration form. Plain semi-implicit Euler is only first-order and drifts ~0.94 units over a long throw, which breaks the 0.5 unit acceptance bound. |
| `SampleInterval` | **1/64 s** | Valve | Competitive tick rate. |
| `SubstepsPerTick` | **8** | Measured | Discretisation error at 8 substeps sits ~0.1u from the converged limit on both the floor and wall replay scenarios (DiscretizationConvergenceTests, 2026-10-14). |
| `MinStepDistance` | **2 units** | Measured | Numerics knob: sweeping the value across min 1/2/4 x max 8/12/16/24 x ref 100/250/400 (36 combos) moved the replay landing at most 0.98u (floor) / 0.17u (wall). |
| `MaxStepDistance` | **16 units** | Measured | Numerics knob: same 36-combination sweep, max landing shift 0.98u. |
| `StepDistanceSpeedRef` | **250 u/s** | Measured | Numerics knob: same 36-combination sweep, max landing shift 0.98u. |
| `MaxFlightSeconds` | **12 s** | Measured | Numerics guard, not physics: across smoke/flash/HE/molotov over the full -15..85 deg elevation envelope the slowest flight is a 85-deg smoke at 6.15s, and cap 12s vs 20s reproduces every landing exactly (FlightCapIrrelevanceTests, 2026-10-14). The range floor was raised 6 -> 8 because 6s truncated that throw. The decoy (~15s, community figure) would exceed the cap, but decoy trajectories are not simulated. |
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
| FullThrowSpeed | 675 | 720 | [600, 720] | Valve (weapons.vdata) | yes |
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

`tests/CS2LineupFinder.Core.Tests` — 32 tests, no map files or game install
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
6a. **First-bounce horizontal retention anomaly (CLOSED 2026-10-15).** The
   anomaly was an analysis omission, not engine behaviour: the leak's
   steep-impact bounce padding (`(1 - alongDist) + 0.5` applied to the whole
   reflected vector when post-bounce speed^2 > 96000 and alongDist > 0.5)
   reproduces both fast first contacts to within the half-tick gravity phase
   (~2 u/s on the normal component, tangential near bit-exact). The bounce
   model in section 5 and `BounceResolver.Resolve` need no change; the
   recompute table there is the closing evidence.
7. **CS2-specific, not CS:GO.** The CS:GO continuous formula is retained in
   `ThrowVelocityCalculator.CsGoLaunchSpeed` for comparison but is not the
   default path. The 0.4x gravity ratio and 0.45 restitution are CS:GO leak
   constants (basecsgh.h L66-68) that CS2 measurements (memorin.app; the live
   telemetry analyser, once captures arrive) are expected to confirm.
