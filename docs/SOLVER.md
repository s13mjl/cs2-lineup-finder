# SOLVER.md — inverse lineup search in `src/CS2LineupFinder.Math`

This document is the specification and the tuning record for `LineupSolver`: the code that
turns *"I want this smoke to land in that circle"* into concrete view angles. Everything the
solver assumes about angles, gravity and budgets is written down here, with the reason for
each number, so a later change can be argued against a stated rationale instead of folklore.

Code: `src/CS2LineupFinder.Math/`. Tests: `tests/CS2LineupFinder.Math.Tests/`.
Constants that the *forward* simulator uses are **not** here — see `docs/PHYSICS.md`.

---

## 1. Where this layer sits

```
contracts/            frozen vocabulary: Vec3, GroundZone, LineupRequest,
   ^                  AngleCandidate, SolverOutcome, ITrajectorySimulator, ...
   |                  (nobody below this line may invent types)
src/CS2LineupFinder.Math/     <- this layer. Pure System.Math, zero NuGet.
   ^
   |                  src/CS2LineupFinder.Core/ (forward simulator) and
   +----------------  plugins/CS2LineupFinder/ (CounterStrikeSharp shell)
                      both depend on the contract, never the reverse.
```

The layer has **one** reference: `contracts/CS2LineupFinder.Abstractions.csproj`. No
`CounterStrikeSharp`, no `CSSharp`, no math package. That is what lets the whole search be
unit tested against a mock world — the 59-test suite runs in 75 ms and a 913-probe solve
inside it costs about 2 ms — and what lets Core stay free of inverse-search policy.

The cost of that purity is a seam: the solver cannot call the simulator directly, because
`ITrajectorySimulator` is implemented by a layer above. So the solver consumes

```csharp
public interface IImpactEvaluator
{
    int EvaluationCount { get; }
    bool TryEvaluate(double yawDegrees, double elevationDegrees, out Vec3 impact);
}
```

and `SimulatorImpactEvaluator` is the adapter that turns one probe into exactly one
`ITrajectorySimulator.Simulate` call. Tests hand the solver an analytic mock instead.

---

## 2. Vocabulary: the brief versus the frozen contract

The solver brief and `contracts/` were written independently. The brief's names are kept in
the left column because that is how the task was specified; the right column is what the code
actually says. Nothing else changed.

| Brief | Frozen contract used instead | Note |
| --- | --- | --- |
| `Vector3` | `Contracts.Vec3` | `float` components, already carries `+ - * Dot Cross Length Length2D Distance Normalized`. math-core deliberately does **not** redeclare them. |
| `QAngle(Pitch, Yaw, Roll)` | `Math.QAngle(Pitch, Yaw, Roll)` | declared here, because `contracts/` has no angle type. |
| `ZoneShape` | `Contracts.GroundZone` (`Circle` / `Rectangle`) | judged through `GroundZone.Contains`. |
| `SolveResult` | `Contracts.SolverOutcome` + `LineupSolution[]` | `LineupSolution` already carries yaw, pitch, release point, impact point, distance-to-centre and step count. |
| `SolveLineup(simulator, origin, zone, type, mode, button)` | `LineupSolver.SolveAsync(request, evaluator, profile, ct)` and the `(..., ITrajectorySimulator, IWorldGeometry, GrenadeProfile, ct)` overload | origin / zone / type / mode / button all travel inside `LineupRequest`; the release speed needs a `GrenadeProfile`, which the brief did not mention. |
| `IsSuccess` | `SolverOutcome.Status == SolverStatus.Success` | |
| "iteration count, error" | `LineupSolution.Steps`, `.TargetDistance`, `.Note` | `Note` spells out the split: coarse probes, refined seeds, simplex iterations, total evaluations. |
| "optimal yaw/pitch, 1 decimal" | `LineupSolution.Yaw`, `.Pitch` rounded to 0.1° | |
| "up to 5 alternatives sorted by error" | `SolverOutcome.Results`, `SolverOptions.MaxAlternatives = 5` | |

One deviation from the brief deserves flagging: **a non-success status still carries a
best-effort result.** The brief implies failure means an empty answer; `contracts/` documents
`Results` as "empty unless `Success`", which would throw away the most useful diagnostic the
search produces. So `NoSolution` and `Timeout` outcomes carry exactly one entry — the closest
landing found — alongside a `Message` that states the miss distance in units. `Success` is
still the only status that may carry several entries. Consumers that branch on `Status` are
unaffected; consumers that read `Results` unconditionally get a better error.

---

## 3. Angles: the Source basis, and why pitch is negative when you look up

Source view angles are a `Rz(yaw) · Ry(pitch)` rotation applied to the `+X` axis. Pitch
rotates about **Y**, yaw about **Z**. That composition gives

```
forward = ( cos(pitch)·cos(yaw),  cos(pitch)·sin(yaw),  -sin(pitch) )
right   = ( -sin(yaw),             cos(yaw),             0 )
up      = ( cos(yaw)·sin(pitch),   sin(yaw)·sin(pitch),  cos(pitch) )
```

and its inverse, the engine's `AngleVectors`/`VectorAngles` pair:

```
pitch = atan2(-dz, sqrt(dx² + dy²))      yaw = atan2(dy, dx)
```

Three consequences the solver leans on, each pinned by `AngleMathTests`:

```
                       +Z (up)

             pitch = -45°  \  |  /  pitch = +45°
                 (looks up)  \ | /    (looks down)
                              \|/
        +Y (left)  ──────────── O ────────────► +X (forward, yaw = 0°)
                              /|\
                             / | \
                    the yaw compass, seen from above:
                    yaw grows anticlockwise from +X towards +Y,
                    so +Y is 90°, -X is 180°, -Y is -90°

  * A POSITIVE pitch looks DOWN, so every lineup this solver reports carries a NEGATIVE
    `LineupSolution.Pitch`: the search window is elevations 5°..85°, i.e. pitches
    -85°..-5°. That is a deliberate scope limit, not an accident — a throw aimed at a
    zone *below* the thrower (off a bridge, onto a lower site) needs a positive pitch and
    is out of scope for this version. §11 says what to change if that case arrives.
  * Roll spins the frame about `forward`, so it can never move `forward`. It is carried in
    `QAngle` for completeness and ignored by the search, which is correct: no lineup in
    this game depends on the player's roll.
  * Pitch is clamped to ±89° (`QAngle.MaxPitch`); the engine refuses anything steeper, so
    the solver validates its elevation window against that constant instead of 90°.
```

Because the search reads better in "degrees above the horizon" than in "negative pitch",
`AngleMath.ElevationFromPitch(p) == -p` and the two are used as follows:

| Plane | Symbol | Range | Where it appears |
| --- | --- | --- | --- |
| search | `elevation` | 5° .. 85°, **positive = up** | `IImpactEvaluator.TryEvaluate`, `SolverOptions`, `Ballistics` |
| contract | `pitch` | -89° .. 0° for real lineups | `AngleCandidate`, `LineupSolution`, `QAngle` |

The sign flip happens at exactly two places: `DirectionFromElevation` on the way in, and
`ToSolution`/`ToCandidate` on the way out. Grepping for `PitchFromElevation` finds both.

---

## 4. The analytic model, and what it is trusted to do

The seed and the reach test use vacuum ballistics with no drag and no bounces:

```
R           = v² · sin(2θ) / g            level range for elevation θ
D           = v⁴ - g·(g·R² + 2·Δz·v²)     the launch-envelope discriminant
θ           = atan( (v² ± √D) / (g·R) )    low arc (-) and high arc (+), Δz ≠ 0 handled
t           = (v·sin θ + √(v²sin²θ - 2g·Δz)) / g      time the descending arc crosses the plane
R_max(Δz)   = (v/g) · √(v² - 2g·Δz)        furthest horizontal point at height Δz above release
```

This model is **not** the answer. Real CS2 throws use grenade gravity (`320 u/s²` per
`docs/PHYSICS.md` §2), lose energy on every bounce, and slide. The model is trusted for two
things only, and both are things where being approximately right is enough:

1. **Where to look.** A zone at 300 units needs a different search window than a zone at 40.
   The analytic bearing and the analytic pair of arcs put the coarse grid in the right
   neighbourhood, which is the entire reason stage 1 needs only ~840 probes instead of ~2000.
2. **When to give up early.** `R_max` is a hard upper bound for any projectile with the same
   `v` and `g`: bounces and drag can only shorten the throw, never lengthen it. Beyond it,
   every candidate is guaranteed to miss, so the solver returns `NoSolution` after **zero**
   simulations rather than burning 2000 of them.

`D` is computed as `v²·v²` rather than `Pow(v, 4)` on purpose: at a target exactly on the
envelope the discriminant must come out as exactly zero, which yields *one* arc. `Pow` drifts
by a ulp and flips that into either "no solution" or two nearly identical arcs. There is a
test for it (`BallisticsTests.SolveElevationsCollapsesToASingleArcOnTheEnvelope`).

> **Integration requirement.** The early give-up in (2) is only as sound as the `v` and `g`
> it is handed. The solver reads them as
> `speed = GrenadeProfile.SpeedFor(request.Button)` and
> `gravity = SimulationEnvironment.Gravity × GrenadeProfile.GravityScale`. Those two numbers
> **must** match what Core's integrator uses, or the reach test can declare a reachable zone
> unreachable. `contracts/` defaults `SimulationEnvironment.Gravity` to the *player* 800 while
> grenades fly at 320, so a `GrenadeProfile` must arrive with `GravityScale = 0.4` (or the
> request must carry `Gravity = 320`). The failure message quotes both numbers back — "at 400
> u/s and gravity 800 u/s²" — so a mismatch is visible the moment it bites.

---

## 5. The two-stage strategy

```
                       ┌────────────────────────────────────────────────┐
  LineupRequest        │                SearchBudget                    │
  + GrenadeProfile ──► │   2000 evaluations  AND  3 seconds, shared      │
                       │   every stage asks before it probes            │
                       └───────┬───────────────────────────┬────────────┘
                               │                           │
                    STAGE 1a: seeds                STAGE 1b: coarse grid
                    ≤ 2 probes                     31 × 27 = 837 probes
                    analytic low + high arc        bearing ±30° step 2°
                               │                   elevation 5..83° step 3°
                               └───────────┬───────┘
                                           ▼
                                     pool of hits
                              (yaw, elevation, cost, impact)
                                           │
                          rank by cost, keep 5 spatially distinct
                                           │
                                           ▼
                              STAGE 2: Nelder-Mead, 5 descents
                              each ≤ 60 iterations on the same budget
                                           │
                                           ▼
                    filter: GroundZone.Contains(impact)      ← success is a hit, not a near miss
                    sort:   by distance to zone centre
                    dedupe: drop angles that describe the same throw
                    cap:    min(5, request.MaxCandidates)
                                           │
                                           ▼
                                     SolverOutcome
```

Stated as two stages, not one loop, for two reasons the tests exploit: the coarse stage can be
examined without the fine stage (`TakeDistinct` on the pool), and each stage's budget can be
tuned and argued about separately.

### 5.1 Stage 1a — analytic seeds

```
count = Ballistics.SolveElevations(range, deltaZ, speed, gravity, out low, out high)
if count >= 1: probe(bearing, low)                 # flat arc
if count >= 2: probe(bearing, high)                # lofted arc over an obstacle
```

At 200 units with `v = 400, g = 800` the target sits on the envelope, `count == 1` and only
one seed is produced. This stage is essentially free (≤ 2 probes) and frequently already lands
inside the zone before the grid starts.

### 5.2 Stage 1b — the coarse grid

```
for yi in -15..+15:                   # 31 columns: bearing ± 30°, step 2°
    for ei in  0..26:                 # 27 rows: 5° + ei × 3°, last row 83°
        probe(bearing + yi·2°, 5° + ei·3°)
        if budget exhausted: stop both loops
```

```
 elevation                    one column per 2° of bearing; every third row is drawn
   83 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o     the top row the grid samples
   77 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o
   68 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o
   59 | o o o o o o o o o o o o o o o ★ o o o o o o o o o o o o o o o     analytic HIGH seed lands here
   50 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o
   41 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o
   32 | o o o o o o o o o o o o o o o ★ o o o o o o o o o o o o o o o     analytic LOW seed lands here
   23 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o
   14 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o
    5 | o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o o     the bottom row
      +--------------------------------------------------------------->  yaw
        -30°    -20°    -10°   bearing 0°   +10°    +20°    +30°

  Worked example: zone 180 units out, level with the release point, v = 400, g = 800.
  The closed form wants 32.1° and 57.9°; the grid's 27 rows offer 32° and 59°, so both
  arcs start stage 2 within 1.1° of an answer. o = one forward simulation, ★ = a probe
  the analytic seeds actually select.
```

The grid answers one question only: *which neighbourhoods contain a landing at all.* Its
resolution is deliberately coarser than the acceptance threshold — a coarse hit is often 3–6
units off, and that is fine because stage 2 exists.

Why a grid at all, instead of descending straight from the analytic seed: a seed aims at the
zone *centre* over *open* ground. Put a wall in the way and the seed's cost is a plateau of
identical "stopped at the wall" landings, which gives a gradient-free descent no information
and gradient-based intuition no valid direction. A grid that spans ±30° of bearing and
5°–83° of elevation is the only cheap way to discover *that another arc exists at all* — over
the wall, round the corner, off a bounce. `GoesOverAnObstacleInsteadOfReportingTheBlockedArc`
is exactly this scenario: with a wall at 90 units, every accepted elevation is above 45°,
while on open ground the best answer is below 45°.

### 5.3 Stage 2 — Nelder-Mead refinement

Only the 5 cheapest *spatially distinct* coarse hits are refined. Two hits count as the same
neighbourhood when they are within 1.5° of bearing **and** 2° of elevation of each other;
without that rule a 2°-spaced grid contributes five adjacent samples of one arc and the solver
spends four of its five descents converging on an answer it already had.

Each descent runs a 3-vertex simplex on the cost function *"distance from the landing point to
the zone centre"*:

```
      seed ★ ──────── +1° yaw            initial triangle: seed,
                   \  |                  seed+½·yawStep, seed+½·elevStep
                    \ |
                     \ ▪  +1.5° elevation

  per iteration:   sort best/second/worst
                   reflect the worst through the centroid of the other two  (×1)
                     better than best   → also try expanding that ray       (×2)
                     between           → accept the reflection
                     worse             → contract toward the centroid      (×0.5)
                     still worse       → shrink the whole simplex onto best (×0.5, 2 probes)

  stop when:   cost spread across the simplex < 0.5 units      → converged
               simplex edge length < 0.05°                     → converged
               60 iterations reached                           → give up, keep the best
               shared budget refuses a probe                   → cut off, keep the best
```

Nelder-Mead is the right algorithm here because the objective is a **black box that returns
only a number**. There is no gradient — the simulator gives no derivative — and finite
differences are unreliable: the cost surface is a set of flat plateaus (a blocked arc returns
the *same* landing for a range of angles) with steep cliffs where a bounce changes. Coordinate
descent would stall on the first plateau; the simplex keeps a two-dimensional footprint and
can slide off it. A 3-vertex simplex is also the smallest thing that can move in 2-D, which
matters when one vertex costs a full simulation.

The cost is deliberately *distance to zone centre*, not *inside/outside*. A boolean would make
every coarse hit equally good, throw away the ranking the alternatives list is built from, and
leave the descent with nothing to descend.

Success and best are then two different tests, on purpose. `GroundZone.Contains` is evaluated
on the horizontal plane and **ignores `Z`**, so a grenade resting on a crate or a shelf above
the zone still counts as a hit. The cost function does not ignore `Z`, so among the hits that
all satisfy the zone the descent still prefers the one at the zone's own height. That is why
`ReportsTheLowAndHighArcAsAlternatives` asserts both `zone.Contains(impact)` and an ordering by
`TargetDistance`: one is the acceptance gate, the other is the ranking.

---

## 6. Budget, timeout and the arithmetic behind 2000

`SearchBudget` is a single object shared by every stage of one solve, so the stages cannot
collectively overrun. `TryConsume()` is the only way to obtain permission to probe, and it
checks the count and the clock together.

```
  stage 1a   seeds                       ≤     2
  stage 1b   31 columns × 27 rows        =   837
  stage 2    5 descents × (3 + 60 × 4)   ≤  1215      (4 probes per iteration at worst:
                                                       reflect + contract + 2 shrinks)
  ─────────────────────────────────────────────────
  nominal worst case                         2054      > 2000, by design
```

The nominal worst case is *allowed* to exceed the ceiling, because the ceiling is what stops
it: the final descent is cut off by `CutOffByBudget` and the search reports the best it holds.
Making the nominal worst case fit exactly instead would mean shrinking the grid or the
iteration cap, i.e. paying search quality for a number that the shared budget already
guarantees. What the tests assert is the invariant that matters: **`SimulateCount ≤ 2000` and
`LineupSolver` never probes after the budget refuses** (`NeverExceedsTwoThousandEvaluations`,
`HonoursATighterEvaluationBudget`).

A typical level field solve costs 913 evaluations, of which 838 are stage 1:

```
838 coarse + 5 refined seeds, 41 simplex iterations, 913 evaluations, error 0.0u
```

That string is `LineupSolution.Note`, produced by `ToSolution`, and it is the number to quote
when a tuning change is argued about.

**Timeout.** `_options.Timeout` is 3 seconds of wall clock measured by one `Stopwatch` opened
when the budget is created. It is checked before every probe, so the overshoot is one
simulation, not a loop. When it expires mid-search, the outcome is `Success` if a hit is
already banked (`Message` says the budget expired before the search finished) and `Timeout`
otherwise, with the closest miss attached. The clock is not a fairness mechanism — this layer
runs on the caller's thread — it is the plugin shell's guarantee that a lineup request cannot
stall a game tick for longer than 3 s of search.

---

## 7. Every default, and why it is that value

| Knob | Default | Justification |
| --- | --- | --- |
| `MaxEvaluations` | **2000** | From the brief, and it lands on a real structural line: stage 1 is a fixed 839 probes (2 seeds + 837 grid), so 2000 leaves 1161 for stage 2 — enough for four full descents and a partial fifth. The budget therefore always lets the grid finish and only ever truncates the tail of the refinement, which is the part where truncation costs the least (§6). |
| `Timeout` | **3 s** | From the brief. 2000 probes at ~1.5 ms per simulated throw is exactly 3 s, so the two ceilings agree about what a full search costs. Against the analytic mock the same 913 probes take ~2 ms, which is why the clock is a plugin-thread safety net rather than the limiter that normally binds. |
| `YawSpanDegrees` | **±30°** | The analytic bearing is computed over open ground at the zone *centre*. Bounce paths and obstacle deflections move the answer, and 30° is wide enough to include the standard banked/rebound lineups while costing only 31 columns. A wider span multiplies the grid linearly. |
| `YawStepDegrees` | **2°** | Yaw is the cheap axis. At the ~150-unit throws this layer targets, 1° of bearing moves the landing 2.6 units laterally, so consecutive columns are 5.2 units apart and the nearest column to any point is within 2.6 units: no zone of a realistic radius falls between two columns. The same value is reused as the "same neighbourhood" gap when stage 2 picks its seeds. |
| `MinElevationDegrees` | **5°** | Sub-5° throws are flat tosses whose landing is decided by skipping and sliding *after* first contact rather than by the arc, so the cost surface is discontinuous and the simplex has nothing to descend. It is also the most angle-sensitive end of the curve: `dR/dθ = (2v²/g)·cos(2θ)` is largest in magnitude near 0° (≈6.5 units per degree at `v=400, g=800`) and vanishes at 45°. |
| `MaxElevationDegrees` | **85°** | `QAngle.MaxPitch` is 89°, and a 45°+ arc is exactly what "drop it on my head over this box" lineups need. 85° leaves headroom so the simplex is **clamped** at the window edge instead of being refused — `Scorer.TryProbe` clamps elevation into the window precisely so an over-eager reflection still buys a valid probe. The top *grid row* is 83° because `5 + 26×3 = 83`; 85° is the ceiling refinement may still reach. |
| `ElevationStepDegrees` | **3°** | Elevation is the sensitive axis: near the flat arc 1° moves the landing ≈4 units at 200 units of range, so successive rows land ~12 units apart along the range axis and the nearest row to any target is within 6 units — inside the 8-unit circle the acceptance fixtures use. Coarser and stage 1 starts missing the arc that clears an obstacle; finer and the grid eats the budget (a 2° step is 41 rows × 31 columns = 1271 probes, leaving 729 for five descents). |
| `RefineTopK` | **5** | Equal to `MaxAlternatives`, and there is a reason: the alternatives list is only credible if the reported arcs were each *refined*, not merely sampled. Fewer than 5 and a zone reachable by both a flat and a lofted arc may only report one of them, because both need to be seeds of their own descent. |
| `MaxRefineIterations` | **60** | On clean vacuum ground a descent converges in about 8 iterations (41 across the five descents of the analytic acceptance test), so 60 is headroom for the pathological case where the simplex has to walk off a blocked-arc plateau before the tolerance is reachable. With 5 descents this bounds stage 2 at 1215 probes, which is still inside the truncating budget of §6. |
| `ConvergenceUnits` | **0.5 units** | From the brief. Half a unit is a 60th of a player's 32-unit hull, i.e. far below anything a human can perceive as a miss — and it is the tighter of the two stopping rules wherever the cost surface has a real slope. |
| `ConvergenceDegrees` | **0.05°** | The second stopping rule. Output is rounded to 0.1°, so once the simplex is under 0.05° wide every remaining vertex reports the *same* angle; continuing to probe would burn budget to produce identical numbers. It exists to stop the descent on flat plateaus where the cost spread never gets under 0.5 units. |
| `MaxAlternatives` | **5** | From the brief. Five is also roughly the number of genuinely different ways one window is reachable (flat, lofted, both banks, one bounce), so a larger cap starts reporting the same throw twice under a different name. |
| `AlternativeYawSeparation` | **1.5°** | The dedupe is an **AND** rule: a pair collapses only if it is within 1.5° of yaw *and* 2° of elevation. 1.5° is below the 2° grid spacing (so two grid samples of one arc collapse) but above the 0.1° reporting precision (so two genuinely different refinements survive). |
| `AlternativeElevationSeparation` | **2°** | The two arcs that reach one zone are tens of degrees apart in elevation (25.8° for the 180-unit fixture: 32.1° and 57.9°), which is why `ReportsTheLowAndHighArcAsAlternatives` can assert a spread over 10°. A 2° gate drops near-duplicates without touching that separation. |
| `request.MaxCandidates` | **64** (contract default) | The solver caps its report at `min(MaxAlternatives, MaxCandidates)`, so a caller asking for 3 alternatives gets 3, and the candidate stream in §8 can be told to stop early. |

`TryValidate` refuses nonsense before any probing: non-positive steps, an elevation window
outside 0..89°, a non-positive budget. A bad tuning surfaces as `InvalidRequest` with the
offending rule in `Message`, not as a search that silently never converges.

---

## 8. The contract surface: `EnumerateCandidatesAsync`

The frozen `ILineupSolver` does not own the search — Core's
`ITrajectorySimulator.SolveLineupAsync(request, solver, world, ct)` drives it, pulling
candidates and simulating them until one lands. So this layer implements that method too, and
it is deliberately **pure**: no evaluation, no simulator call, no budget. It ranks the same
analytic grid by *predicted* error and streams the cheapest `request.MaxCandidates` of them.

That asymmetry is the point. The physics layer pays one simulation per candidate and can stop
at the first hit, so the candidate *order* is the whole product: seeds first (they are the two
arcs the closed form would use on open ground), then the grid by predicted distance to centre.
`EnumeratesAnalyticCandidatesForThePhysicsLayerToSimulate` pins it, and constructing the solver
without an `IVDataProvider` throws `InvalidOperationException` naming that dependency, because
a candidate stream without a release speed would be an empty list mistaken for "no lineup
exists".

Two entry points, one search:

| Caller | Entry point | Simulations |
| --- | --- | --- |
| plugin shell via Core | `EnumerateCandidatesAsync` | driven by Core, ≤ `MaxCandidates` |
| anything holding an `IImpactEvaluator` (or the simulator + world directly) | `SolveAsync` | owned here, ≤ 2000, ≤ 3 s |

---

## 9. Outcomes, verbatim

| `Status` | `Results` | `Message` |
| --- | --- | --- |
| `Success` | 1..5, best first, every impact satisfies `GroundZone.Contains` | `null`, or the expired-time-budget note |
| `NoSolution` | 0 or 1 best-effort entry | either the vacuum-reach refusal (0 probes) or "closest landing ... missed the zone centre by N units" |
| `Timeout` | 0 or 1 best-effort entry | closest-miss text, clock exhausted |
| `InvalidRequest` | empty | the failed precondition, e.g. "A circular zone needs a positive Radius." |
| `Cancelled` | empty | "The search was cancelled." — an `OperationCanceledException` from the caller's token |
| `Failed` | empty | "The simulator raised an unexpected error: ..." — an evaluator throw is caught here so a Core bug cannot take the plugin down |

A cancellation is observed at every elevation row of stage 1 and before every descent, so the
worst-case latency after `CancellationToken.Cancel()` is one probe.

---

## 10. Acceptance map

| Criterion from the brief | Test |
| --- | --- |
| Source angle conventions both ways | `AngleMathTests.NegativePitchLooksUp`, `.PositivePitchLooksDown`, `.YawRotatesAboutTheVerticalAxis`, `.RollDoesNotMoveTheForwardVector`, `.AngleFromDirectionInvertsForwardFromAngle`, `.TheViewBasisStaysOrthonormal` |
| Vector maths available, not redeclared | `AngleMathTests.ContractVectorSuppliesTheOperationsTheSolverReliesOn` |
| Closed-form projectile maths | `BallisticsTests` (`LevelRangePeaksAtFortyFiveDegrees`, `SolveElevationsReturnsBothArcsAndPredictsTheRequestedRange`, `SolveElevationsCollapsesToASingleArcOnTheEnvelope`, `SolveElevationsHasNoSolutionBeyondMaximumReach`, `MaximumReachShrinksWhenTheTargetIsAboveTheThrower`, `PredictImpact*`) |
| Analytic case: substituted angles land < 1 unit from centre | `LineupSolverTests.SolvesTheAnalyticThrowToUnderAUnit` |
| Same landing reached by several angle sets | `LineupSolverTests.ReportsTheLowAndHighArcAsAlternatives` |
| Mocked obstacle: the solver must go around/over it | `LineupSolverTests.GoesOverAnObstacleInsteadOfReportingTheBlockedArc` |
| Unreachable zone: clear error, no wasted work | `LineupSolverTests.ReportsUnreachableZonesWithoutSpendingTheBudget` (asserts 0 simulations) |
| ≤ 2000 forward calls | `LineupSolverTests.NeverExceedsTwoThousandEvaluations`, `.HonoursATighterEvaluationBudget` |
| 3-second timeout returns the current best | `LineupSolverTests.ReturnsTheBestSoFarWhenTheClockRunsOut` |
| One probe = exactly one `Simulate`, angles resolved correctly | `SimulatorImpactEvaluatorTests.OneProbeIsExactlyOneForwardSimulation`, `.ResolvesTheAnglePairIntoAForwardThrow`, `.LandsWhereTheClosedFormPredicts` |
| Bad requests, cancellation, a throwing simulator | `LineupSolverTests.RejectsRequestsThatCannotBeSolved`, `.RespectsTheCancellationRequestedByTheCaller`, `.SurfacesASimulatorThatRefusesToAnswer`, `SimulatorImpactEvaluatorTests.RejectsMissingCollaborators` |
| Zero NuGet, zero engine references | `CS2LineupFinder.Math.csproj` has one `ProjectReference` and no `PackageReference`; `TreatWarningsAsErrors` is on and the build is clean |

---

## 11. Tuning cookbook

| Symptom | First change | Why |
| --- | --- | --- |
| Reports "unreachable" for a zone that clearly is | Check `Gravity` × `GravityScale` against the simulator | §4's integration requirement; the message quotes both numbers |
| Misses jump-throw / bounce lineups | widen `YawSpanDegrees` to 45 | the rebound bearing is not near the direct bearing |
| Misses a steep drop behind cover | raise `MaxElevationDegrees` toward 89, keep `ElevationStepDegrees` | the lofted arc lives at the top of the window |
| A zone **below** the thrower is refused | not a knob — `TryValidate` pins the window at 0..89° elevation | positive-pitch search is a feature, not a re-tune: the envelope maths already handles `Δz < 0` (`MaxRange` *grows* as the target drops), so what has to change is the window bound and the reporting sign, not the algorithm |
| Success reported but the throw is unreproducible in game | tighten `ConvergenceUnits` to 0.1, `ConvergenceDegrees` to 0.02 | refinement stopped on a plateau; the reported angles sit where the cost is flat, so a 0.1° mistake in game moves it |
| Too slow for a live request | drop `RefineTopK` to 3 | stage 2 is five descents of up to 243 probes each, so cutting two of them saves ~490; the 837-probe grid is fixed, and widening `ElevationStepDegrees` to 4 only claws back 186 of it |
| Alternatives list is one arc repeated | raise `AlternativeYawSeparation` / `AlternativeElevationSeparation` | the dedupe gate is what makes the list mean "different throws" |

Anything not in this table: change `SolverOptions`, not `LineupSolver`. The search has no
hard-coded degrees, units or thresholds — every number in §7 is a property, and
`TryValidate` is the guard.
