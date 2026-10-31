using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

// <copyright file="GrenadeSimulator.cs" company="CS2LineupFinder">
// Forward ballistic simulator for CS2 throwables.
// </copyright>

namespace CS2LineupFinder.Core;

/// <summary>
/// Forward-integrates a grenade until it comes to rest or detonates, sweeping
/// the world through an <see cref="IWorldGeometry"/> as it goes.
///
/// Integration is one exact-parabola step per 64 Hz tick: the 2026-10-19
/// telemetry forensics (7 world contacts, 2 rests, telemetry-001/-002) show
/// the engine integrates the full-tick parabola, reflects the tick-boundary
/// velocity minus q gravity quanta (q = half-ticks elapsed at the contact),
/// coasts the reflected velocity through the rest of the tick at constant
/// speed, and adds the remaining gravity quantum along world -Z at the tick
/// boundary - a measured engine structure, not a numerics choice. Sweeps are
/// adaptive (2 units when fast, up to 16 when slow) so a thin wall is never
/// tunnelled through and long rolls do not flood the raycaster.
///
/// Known limitation: dynamic entities (players, doors, weapons, ragdolls) are
/// ignored; only world BSP and static brush geometry collides. See the
/// Limitations section of docs/PHYSICS.md.
/// </summary>
public sealed partial class GrenadeSimulator : ITrajectorySimulator
{
    /// <summary>
    /// Half-height of the projectile hull the engine sweeps with, in units.
    ///
    /// <para>
    /// Measured from the 2026-10-15 telemetry, not assumed: every recorded
    /// resting centre sits at <c>surface - 1.968</c>. Two independent captures
    /// (telemetry-001 resting at -165.9688, telemetry-002 at -221.9688) and the
    /// five recorded de_mirage landings all repeat that same offset, so the hull
    /// is a 4-unit cube and its centre is the value the recorder reads off
    /// <c>AbsOrigin</c>.
    /// </para>
    ///
    /// <para>
    /// A zero-radius line sweep instead reports contact when the centre reaches
    /// the surface, i.e. two units early on every bounce. On an infinite plane
    /// that is invisible; on a real map it clips step edges and ledges the real
    /// grenade clears, which is what turned a verified-correct launch into a
    /// 39.29u landing miss.
    /// </para>
    /// </summary>
    private const float ProjectileHullRadius = 2f;

    /// <summary>
    /// Speed (u/s) along the contact normal above which the projectile counts as
    /// having left the surface. A bounce always exceeds it, so the first frame
    /// after a contact never has rolling friction applied to it twice.
    /// </summary>
   private const float TakeoffSpeedFloor = 1f;

   /// <summary>
   /// A smoke only detonates once essentially stopped: CS:GO
   /// smokegrenade_projectile.cpp Think_Detonate waits for speed &lt;= 0.1 u/s
   /// before blooming. Below this the 20 u/s generic RestSpeed must not pop
   /// the grenade, otherwise rolling smokes detonate early and undershoot.
   /// </summary>
   private const float SmokeSettleSpeed = 0.1f;

    /// <summary>
    /// How far below the start (or the zone centre, for acceptance) a trajectory
    /// may fall before it is declared out of the world. Real throws always stay
    /// far above this; it exists so a trace failure can never waste the whole
    /// flight-time budget simulating thousands of units of vacuum flight.
    /// </summary>
    private const float KillDropUnits = 512f;

   /// <summary>World up, matching the contract coordinate convention.</summary>
    private static readonly Vec3 Up = new(0f, 0f, 1f);

    private readonly PhysicsParameters _parameters;
    private readonly List<SimulationContact> _contacts = new();

    public GrenadeSimulator(PhysicsParameters? parameters = null)
    {
        _parameters = parameters ?? new PhysicsParameters();
    }

    /// <summary>The live parameter set. Mutating it changes subsequent simulations.</summary>
    public PhysicsParameters Parameters => _parameters;

    /// <summary>
    /// Every world contact of the most recent <see cref="Simulate"/> call, in
    /// order. Diagnostic only: it lets a live replay name the surface where a
    /// trajectory stopped instead of guessing from its final position.
    /// </summary>
    public IReadOnlyList<SimulationContact> LastContacts => _contacts;

    /// <summary>
    /// Safety net so a pathological raycaster that always reports a hit cannot
    /// spin forever. Real throws settle in well under this.
    /// </summary>
    public int MaxBounceCount { get; set; } = 32;

    /// <inheritdoc />
    public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world)
        => SimulateCore(parameters, world, out _);

    /// <inheritdoc />
    public TrajectoryResult SimulateWithDiagnostics(
        ThrowParams parameters,
        IWorldGeometry world,
        out SimulationDiagnostics diagnostics)
        => SimulateCore(parameters, world, out diagnostics);

    private TrajectoryResult SimulateCore(ThrowParams parameters, IWorldGeometry world, out SimulationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(world);
        List<TrajectoryPoint> path = new(256);
        _contacts.Clear();

        Vec3 position = parameters.Origin;
        Vec3 velocity = ResolveLaunchVelocity(parameters);

        float dt = ResolveTimestep(parameters);
        float quantum = _parameters.GrenadeGravity.Value * dt * 0.5f;
        float sampleInterval = _parameters.SampleInterval.Value;
        float maxFlight = _parameters.MaxFlightSeconds.Value;
        float fuse = _parameters.FuseSeconds.Value;
        int ignoreEntityIndex = parameters.IgnoreEntityIndex;

        float time = 0f;
        float nextSampleTime = 0f;
        float apex = position.Z;
        float pathLength = 0f;

        int bounceCount = 0;
        int stepsTaken = 0;
        int tickCount = 0;
        bool smokeFirstBounceDone = false;
        Vec3 lastNormal = Up;

        // Emergency stop for a regression that lets the grenade fall out of the
        // world: the trajectory below the kill plane is garbage, so finish there
        // instead of simulating thousands of units of vacuum flight.
        float killZ = position.Z - KillDropUnits;
        Vec3 accel = Up * -_parameters.GrenadeGravity.Value;

        path.Add(new TrajectoryPoint(position, velocity, 0f, 0));

        // CS2 grenade integration law, measured tick by tick from
        // telemetry-001 (169 ticks, 4 world floor bounces + rest) and
        // telemetry-002 (330 ticks, 3 world floor bounces + rest): the engine
        // integrates ONE exact parabola per 64 Hz tick, sweeps once, and on a
        // contact reflects the tick-boundary input velocity v + q*2.5 down
        // (q = 1 when the contact lands in the first half of the tick, 2 in
        // the second), travels the remaining tick time at constant speed,
        // then adds the remaining gravity quantum along world -Z at the tick
        // boundary. Free flight consumes the full g*dt = 5 u/s per tick
        // (verified every tick of both captures); the bounce law
        // mirror(v_in, n) * 0.45 + n * 0.0140625 then reproduces all 7 world
        // contacts to CSV precision (see BounceResolver documentation).
        while (time < maxFlight && position.Z > killZ)
        {
            tickCount++;
            Vec3 target = position + (velocity * dt) + (accel * (0.5f * dt * dt));
            Vec3 tickEndVelocity = velocity + (accel * dt);
            BounceResolver.ApplyDrag(ref tickEndVelocity, dt, _parameters);

            TraceHit hit = Sweep(world, position, target, tickEndVelocity.Length, ignoreEntityIndex, ref stepsTaken);

            Vec3 before = position;
            if (hit.DidHit && IsWorldGeometry(hit, ignoreEntityIndex))
            {
                Vec3 normal = hit.Normal.Normalized();
                if (normal.LengthSquared <= 1e-8f)
                {
                    normal = Up;
                }

                lastNormal = normal;

                // q is the number of half-tick gravity quanta consumed before
                // the contact: first-half contacts reflect once (T1 bounces 1-3),
                // second-half contacts twice (T1 bounce 4, all three T2
                // bounces). Sleep is judged on the TICK-START normal speed
                // (T1 rest -10.4645 and T2 rest -19.4388 cancel the whole
                // velocity; all 7 real bounces start >= 20 u/s into the floor).
                float startNormalSpeed = -Vec3.Dot(velocity, normal);
                bool isFloor = normal.Z > _parameters.FloorNormalThreshold.Value;
                if (isFloor && startNormalSpeed < _parameters.RestSpeed.Value)
                {
                    position = hit.Position;
                    velocity = new Vec3(0f, 0f, 0f);
                    bounceCount++;
                    pathLength += (position - before).Length;
                    if (position.Z > apex)
                    {
                        apex = position.Z;
                    }

                    time += dt;
                    tickCount = 0;
                    path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
                    return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, tickCount, apex, pathLength, out diagnostics);
                }

                float q = hit.Fraction <= 0.5f ? 1f : 2f;
                Vec3 inputVelocity = velocity + new Vec3(0f, 0f, -q * quantum);
                // Bounce padding (ResolveFlyCollisionCustom) is applied inside
                // the resolver: a fast floor contact is scaled by how squarely
                // the reflected velocity leaves the floor. The embedded-clip
                // scale this replaced was a coincidental fit to one contact.
                velocity = BounceResolver.Resolve(
                    inputVelocity, normal, false, parameters.GrenadeType, _parameters, ref smokeFirstBounceDone);
                position = hit.Position;
                bounceCount++;
                _contacts.Add(new SimulationContact(
                    hit.Position,
                    normal,
                    bounceCount,
                    hit.ClassName,
                    hit.ContentsRaw,
                    hit.IsEmbedded,
                    hit.Fraction,
                    IsPush: false));

                // The reflected velocity travels the REST of the tick at
                // constant speed from the EXACT contact point - no gravity
                // inside the bounce tick beyond the quantum already spent, and
                // no skin offset: the engine coasts from the contact itself
                // (verified: T1 bounce 1 tick-end matches the CSV to 0.002 u
                // under exactly this rule; a 0.05 u skin offset showed up as a
                // constant +0.057 z error across every subsequent tick).
                float remaining = dt * (1f - hit.Fraction);
                Vec3 pushTo = position + (velocity * remaining);
                TraceHit pushHit = Sweep(world, position, pushTo, velocity.Length, ignoreEntityIndex, ref stepsTaken);
                if (pushHit.DidHit && IsWorldGeometry(pushHit, ignoreEntityIndex))
                {
                    // Immediate re-contact (corner, crevice). A floor-like
                    // contact keeps the measured behaviour: place at the
                    // second contact and let the residual quantum below act
                    // along world -Z (every captured contact is a floor).
                    //
                    // A steep face is different: leaving the first bounce's
                    // velocity untouched keeps it pointing into the face, so
                    // the next tick hits the same face again and the grenade
                    // stops on the ledge it should have cleared. That is the
                    // palace lower-shelf edge: the 1015 sample's live replay
                    // rests 12 u low on the shelf and 132.8 u short. Reflect
                    // at the second normal instead, exactly like any other
                    // wall contact, and coast the unspent part of the tick.
                    Vec3 pushNormal = pushHit.Normal.Normalized();
                    if (pushNormal.LengthSquared <= 1e-8f)
                    {
                        pushNormal = Up;
                    }

                    lastNormal = pushNormal;
                    _contacts.Add(new SimulationContact(
                        pushHit.Position,
                        pushNormal,
                        bounceCount,
                        pushHit.ClassName,
                        pushHit.ContentsRaw,
                        pushHit.IsEmbedded,
                        pushHit.Fraction,
                        IsPush: true));
                    if (pushNormal.Z <= _parameters.FloorNormalThreshold.Value)
                    {
                        velocity = BounceResolver.Resolve(
                            velocity,
                            pushNormal,
                            false,
                            parameters.GrenadeType,
                            _parameters,
                            ref smokeFirstBounceDone);
                        float unspent = remaining * (1f - pushHit.Fraction);
                        position = pushHit.Position + (velocity * unspent);
                    }
                    else
                    {
                        position = pushHit.Position;
                    }
                }
                else
                {
                    position = pushTo;
                }

                // Tick boundary: the unspent (2 - q) gravity quanta are added
                // along world -Z, NOT reflected about the contact normal -
                // this is what makes C(q) = 0.45*q*2.5 - (2-q)*2.5 +
                // 0.0140625 land exactly on the measured deficits of both
                // captures (-1.3609375 / +2.2640625).
                velocity += new Vec3(0f, 0f, -(2f - q) * quantum);
                BounceResolver.ApplyDrag(ref velocity, dt, _parameters);

                // A molotov pops on contact; smokes settle through the same
                // sleep check next tick. Keep the contact-path fuse test.
                if (DetonationModel.Evaluate(parameters.GrenadeType, time, velocity.Length, false, true, fuse).Detonated
                    || bounceCount >= MaxBounceCount
                    || IsSmokeSettled(parameters.GrenadeType, velocity))
                {
                    pathLength += (position - before).Length;
                    if (position.Z > apex)
                    {
                        apex = position.Z;
                    }

                    time += dt;
                    tickCount = 0;
                    path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
                    return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, tickCount, apex, pathLength, out diagnostics);
                }
            }
            else
            {
                // Free flight: the full parabola is the measured per-tick
                // motion of both captures, end velocities included.
                position = target;
                velocity = tickEndVelocity;
            }

            pathLength += (position - before).Length;
            if (position.Z > apex)
            {
                apex = position.Z;
            }

            time += dt;

            // Record on the tick grid so the path lines up with a 64 Hz recording.
            if (time >= nextSampleTime)
            {
                path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
                nextSampleTime += sampleInterval;
                nextSampleTime = MathF.Max(nextSampleTime, time);
            }

            bool fuseDone = DetonationModel.Evaluate(
                parameters.GrenadeType, time, velocity.Length, IsSmokeSettled(parameters.GrenadeType, velocity), false, fuse).Detonated;

            if (fuseDone)
            {
                path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
                return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, tickCount, apex, pathLength, out diagnostics);
            }

        }

        path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
        return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, tickCount, apex, pathLength, out diagnostics);
    }

    /// <summary>
    /// Sweeps <paramref name="from"/> to <paramref name="to"/> in adaptive hops and
    /// returns the first world hit, or a miss covering the whole distance.
    /// <paramref name="speed"/> sizes the hop: a fast grenade is checked every
    /// couple of units, a slow one can travel 16 units per sweep.
    /// </summary>
    private TraceHit Sweep(IWorldGeometry world, Vec3 from, Vec3 to, float speed, int ignoreEntityIndex, ref int stepsTaken)
    {
        Vec3 delta = to - from;
        float distance = delta.Length;

        if (distance <= 1e-4f)
        {
            return new TraceHit(false, to, Vec3.Zero, 1f);
        }

        Vec3 direction = delta / distance;
        float hop = BounceResolver.StepDistance(speed, _parameters);
        float radius = ProjectileHullRadius;

        // One sweep is enough when the move already fits inside a single hop.
        if (distance <= hop)
        {
            stepsTaken++;
            return world.TraceSphere(from, direction, distance, radius, ignoreEntityIndex);
        }

        // Divide the move into equal hops so the last one lands exactly on `to`.
        int hops = (int)MathF.Ceiling(distance / hop);
        float actualHop = distance / hops;

        Vec3 cursor = from;
        for (int i = 0; i < hops; i++)
        {
            Vec3 next = cursor + (direction * actualHop);
            stepsTaken++;

            TraceHit trace = world.TraceSphere(cursor, direction, actualHop, radius, ignoreEntityIndex);
            if (trace.DidHit && IsWorldGeometry(trace, ignoreEntityIndex))
            {
                // The trace fraction is relative to THIS hop; every consumer
                // needs the fraction of the whole from->to move, so rescale
                // before returning. Without this, the post-bounce coast time
                // is computed against the wrong denominator and every bounce
                // lands systematically short of the telemetry.
                float wholeFraction = ((float)i + trace.Fraction) / hops;
                return new TraceHit(
                    trace.DidHit,
                    trace.Position,
                    trace.Normal,
                    wholeFraction,
                    trace.EntityIndex,
                    trace.ClassName,
                    trace.ContentsRaw,
                    trace.IsEmbedded);
            }

            // A dynamic entity we deliberately ignore must not stall the walk,
            // so always advance by the full planned hop.
            cursor = next;
        }

        return new TraceHit(false, to, Vec3.Zero, 1f);
    }

    /// <summary>
    /// True when a hit should stop the grenade.
    /// </summary>
    /// <remarks>
    /// Deliberately the same question <see cref="Contracts.TraceFilters.StopsProjectile"/>
    /// answers, so the tracer and the simulator cannot disagree about what is solid.
    ///
    /// The previous rule was "entity index at or below zero, or the entity we were
    /// told to ignore", which is not the same question. The tracer already steps
    /// past players and other grenades and hands back everything else - including
    /// func_brush and doors, whose engine index is positive - so this test then
    /// threw those hits away and the grenade flew straight through static brush
    /// geometry. Deciding by designer name, once, fixes both sides at the same time.
    ///
    /// The world itself reports -1 for index and no class name, so the name test
    /// below accepts it. Engine index 0 (worldspawn) counts as world geometry too:
    /// the reflective Source 2 trace reports static brushes through worldspawn
    /// instead of -1, and excluding it made every bounce pass through the floor.
    /// </remarks>
    /// <param name="hit">Sweep result to classify.</param>
    /// <param name="ignoreEntityIndex">Entity the caller asked to skip, or -1.</param>
    /// <returns><see langword="true"/> when the contact stops the grenade.</returns>
    private static bool IsWorldGeometry(TraceHit hit, int ignoreEntityIndex)
    {
        // Only compare against a real entity index. -1 means "nothing to skip",
        // and it is also the index the engine reports for the world itself, so a
        // bare comparison would make every world contact look like the ignored
        // entity and drop the grenade through the floor.
        if (ignoreEntityIndex >= 0 && hit.EntityIndex == ignoreEntityIndex)
        {
            return false;
        }

        // No class name means world BSP, which the tracer never filters out.
        if (string.IsNullOrEmpty(hit.ClassName))
        {
            return true;
        }

        return Contracts.TraceFilters.StopsProjectile(hit.ClassName);
    }

    /// <summary>
    /// The velocity the flight starts with. The caller supplies it already
    /// resolved (aim direction at the mode speed plus the inherited share of the
    /// thrower velocity), so this is a pass-through kept as a seam.
    /// </summary>
    private static Vec3 ResolveLaunchVelocity(ThrowParams parameters) => parameters.Velocity;

    /// <summary>
    /// True when a smoke has decayed to the engine's bloom threshold. Non-smoke
    /// projectiles never settle here; their generic RestSpeed applies instead.
    /// </summary>
    private static bool IsSmokeSettled(GrenadeType type, Vec3 velocity)
        => type == GrenadeType.Smoke
            && velocity.LengthSquared <= SmokeSettleSpeed * SmokeSettleSpeed;

    /// <summary>
    /// Fixed integration step. Tied to the sample interval so one tick is one
    /// recorded sample, and clamped so a bogus tick rate cannot destabilise it.
    /// </summary>
    private float ResolveTimestep(ThrowParams parameters)
    {
        int tickRate = parameters.GameTickRate > 0 ? parameters.GameTickRate : 64;
        float dt = 1f / tickRate;

        // Never integrate a coarser step than the sample interval asks for.
        float sample = _parameters.SampleInterval.Value;
        return Math.Min(dt, sample) > 0f ? Math.Min(dt, sample) : 1f / 64f;
    }

    /// <summary>Assembles the result and diagnostics at every exit point.</summary>
    private TrajectoryResult Finish(
        List<TrajectoryPoint> path,
        Vec3 position,
        Vec3 normal,
        int bounceCount,
        float time,
        ThrowParams parameters,
        int stepsTaken,
        int substeps,
        float apex,
        float pathLength,
        out SimulationDiagnostics diagnostics)
    {
        float horizontal = new Vec3(position.X - parameters.Origin.X, position.Y - parameters.Origin.Y, 0f).Length;

        diagnostics = new SimulationDiagnostics(
            apex - parameters.Origin.Z,
            horizontal,
            pathLength,
            stepsTaken,
            substeps);

        FinalImpact impact = new(
            position,
            normal,
            bounceCount,
            time,
            true,
            parameters.GrenadeType);

        bool inZone = parameters.ZoneTest?.Invoke(position) ?? false;
        return new TrajectoryResult(path, impact, inZone);
    }

    private const int MaxFallbackResults = 5;

    /// <inheritdoc />
    public async Task<SolverOutcome> SolveLineupAsync(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(world);

        Stopwatch stopwatch = Stopwatch.StartNew();

        List<LineupSolution> hits = new();
        List<LineupSolution> fallback = new();
        int evaluated = 0;

        try
        {
            var profile = GrenadeProfiles.Get(request.GrenadeType);
            await foreach (AngleCandidate candidate in solver.EnumerateCandidatesAsync(request, profile, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (evaluated >= request.MaxCandidates)
                {
                    break;
                }

                evaluated++;

                LineupSolution? solution = EvaluateCandidate(request, candidate, world, out bool inZone);

                if (solution is null)
                {
                    continue;
                }

                if (inZone)
                {
                    hits.Add(solution);
                }
                else
                {
                    fallback.Add(solution);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return SolverOutcome.Failure(SolverStatus.Cancelled, "Line-up search was cancelled.", stopwatch.Elapsed);
        }

        stopwatch.Stop();

        if (hits.Count > 0)
        {
            hits.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));
            return SolverOutcome.Success(hits, stopwatch.Elapsed);
        }

        // Nothing landed in the zone. Returning the closest few is more useful to
        // the player than an empty list, but the status has to say so. Every miss
        // is kept so the reported "closest" really is the closest; capping the
        // pool at the first five candidates once reported misses hundreds of
        // units away while a near hit sat later in the stream.
        fallback.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));
        if (fallback.Count > MaxFallbackResults)
        {
            fallback.RemoveRange(MaxFallbackResults, fallback.Count - MaxFallbackResults);
        }

        string message = evaluated == 0
            ? "The inverse solver produced no candidates."
            : $"No candidate landed inside the zone after {evaluated} simulations.";

        return new SolverOutcome
        {
            Status = SolverStatus.NoSolution,
            Results = fallback,
            Elapsed = stopwatch.Elapsed,
            Message = message,
        };
    }

    /// <inheritdoc />
    public ILineupSearch? BeginLineupSearch(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(world);

        return new SlicedSearch(this, request, solver, world);
    }

    /// <summary>
    /// A search driven by <see cref="ILineupSearch.Step"/> on the game thread. The
    /// candidate list is pure math, so it is materialised up front; the expensive
    /// forward simulations, which issue the collision sweeps the engine requires
    /// the game thread for, are consumed a bounded wall-clock slice at a time.
    /// </summary>
    private sealed class SlicedSearch : ILineupSearch
    {
        private readonly GrenadeSimulator _owner;
        private readonly LineupRequest _request;
        private readonly ILineupSolver _solver;
        private readonly IWorldGeometry _world;
        private readonly GrenadeProfile _profile;
        private readonly IEnumerator<AngleCandidate> _candidates;
        private readonly List<LineupSolution> _hits = new();
        private readonly List<LineupSolution> _fallback = new();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private int _evaluated;
        private bool _finished;
        private SolverOutcome? _final;
        private Refinement? _refinement;

        /// <inheritdoc />
        public int Evaluated => _evaluated;

        internal SlicedSearch(GrenadeSimulator owner, LineupRequest request, ILineupSolver solver, IWorldGeometry world)
        {
            _owner = owner;
            _request = request;
            _solver = solver;
            _world = world;
            _profile = GrenadeProfiles.Get(request.GrenadeType);

            // Materialising the candidates is pure math with no engine calls, so it
            // is bounded by MaxCandidates and safe to do in one go.
            var enumerated = new List<AngleCandidate>();
            var enumerator = solver.EnumerateCandidatesAsync(request, GrenadeProfiles.Get(request.GrenadeType)).GetAsyncEnumerator();
            try
            {
                // The shipped enumerators complete synchronously (they only await
                // Task.CompletedTask), so this drain never actually suspends. If a
                // future solver yields asynchronously, the search simply uses the
                // candidates gathered so far rather than blocking the game thread.
                while (enumerated.Count < request.MaxCandidates)
                {
                    var moveNext = enumerator.MoveNextAsync();
                    if (!moveNext.IsCompleted)
                    {
                        break;
                    }

                    if (!moveNext.Result)
                    {
                        break;
                    }

                    enumerated.Add(enumerator.Current);
                }
            }
            finally
            {
                _ = enumerator.DisposeAsync();
            }

            _candidates = enumerated.GetEnumerator();
        }

        /// <inheritdoc />
        public SolverOutcome? Step(double budgetMilliseconds)
        {
            if (_finished)
            {
                return _final;
            }

            var slice = Stopwatch.StartNew();
            while (slice.Elapsed.TotalMilliseconds < budgetMilliseconds && !_finished)
            {
                if (_refinement is not null)
                {
                    _refinement.RunSlice(budgetMilliseconds, slice);
                    if (_refinement.Done)
                    {
                        CollectRefinement();
                    }

                    continue;
                }

                if (_evaluated >= _request.MaxCandidates || !_candidates.MoveNext())
                {
                    _candidates.Dispose();
                    var seeds = BestMisses(4);
                    _refinement = seeds.Count == 0 ? null : new Refinement(this, seeds);
                    if (_refinement is null)
                    {
                        Finish();
                    }

                    continue;
                }

                _evaluated++;
                LineupSolution? solution = _owner.EvaluateCandidate(_request, _candidates.Current, _world, out bool inZone);
                if (solution is null)
                {
                    continue;
                }

                if (inZone)
                {
                    _hits.Add(solution);
                }
                else
                {
                    _fallback.Add(solution);
                }
            }

            return _finished ? _final : null;
        }

        /// <inheritdoc />
        public SolverOutcome FinishNow()
        {
            if (!_finished)
            {
                _candidates.Dispose();
                Finish();
            }

            return _final!;
        }

        private void Finish()
        {
            _finished = true;
            _stopwatch.Stop();
            _final = BuildOutcome();
        }

        /// <summary>
        /// The closest misses, spread over distinct bearings. Refining a single
        /// seed only helps if that seed's basin holds the solution; a bounce
        /// landing can sit in a completely different direction from the vacuum
        /// ranking's top pick, so the refinement gets several starts.
        /// </summary>
        private List<LineupSolution> BestMisses(int count)
        {
            List<LineupSolution>? pool = _fallback.Count > 0 ? _fallback : _hits;
            if (pool is null || pool.Count == 0)
            {
                return new List<LineupSolution>();
            }

            var ranked = new List<LineupSolution>(pool);
            ranked.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));

            var picked = new List<LineupSolution>(count);
            foreach (var candidate in ranked)
            {
                var distinct = true;
                foreach (var chosen in picked)
                {
                    var yawGap = MathF.Abs(MathF.IEEERemainder(candidate.Yaw - chosen.Yaw, 360f));
                    if (yawGap < 10f)
                    {
                        distinct = false;
                        break;
                    }
                }

                if (distinct)
                {
                    picked.Add(candidate);
                }

                if (picked.Count >= count)
                {
                    break;
                }
            }

            return picked;
        }

        private void CollectRefinement()
        {
            Refinement refinement = _refinement!;
            _refinement = null;
            if (refinement.BestSolution is null)
            {
                Finish();
                return;
            }

            if (refinement.BestInZone)
            {
                _hits.Add(refinement.BestSolution);
            }
            else
            {
                _fallback.Add(refinement.BestSolution);
            }

            Finish();
        }

        private SolverOutcome BuildOutcome()
        {
            if (_hits.Count > 0)
            {
                _hits.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));
                AnnotateNeighbourSpread(_hits);
                return SolverOutcome.Success(_hits, _stopwatch.Elapsed);
            }

            _fallback.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));
            if (_fallback.Count > MaxFallbackResults)
            {
                _fallback.RemoveRange(MaxFallbackResults, _fallback.Count - MaxFallbackResults);
            }

            AnnotateNeighbourSpread(_fallback);

            string message = _evaluated == 0
                ? "The inverse solver produced no candidates."
                : $"No candidate landed inside the zone after {_evaluated} simulations.";

            if (_fallback.Count > 0)
            {
                LineupSolution closest = _fallback[0];
                message += string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $" Closest: yaw {closest.Yaw:0.0} pitch {closest.Pitch:0.0} landed {closest.TargetDistance:0.#}u away at " +
                    $"({closest.ImpactPosition.X:0}, {closest.ImpactPosition.Y:0}, {closest.ImpactPosition.Z:0}).");
            }

            return new SolverOutcome
            {
                Status = SolverStatus.NoSolution,
                Results = _fallback,
                Elapsed = _stopwatch.Elapsed,
                Message = message,
            };
        }

        /// <summary>Angular window that counts as "the same aim, nudged".</summary>
        private const float NeighbourYawWindowDegrees = 1.5f;

        /// <summary>Elevation counterpart of <see cref="NeighbourYawWindowDegrees"/>.</summary>
        private const float NeighbourPitchWindowDegrees = 2f;

        /// <summary>
        /// Stamps the reported results with how far their nearest evaluated
        /// neighbours land. Branchy landings - a step edge, a multi-bounce
        /// rollout - put neighbours a platform away while the in-zone distance
        /// still looks perfect, so the host can warn before the player throws.
        /// Costs no simulations: every evaluated candidate is already in the
        /// hit and miss pools.
        /// </summary>
        private void AnnotateNeighbourSpread(List<LineupSolution> ranked)
        {
            int limit = Math.Min(ranked.Count, MaxFallbackResults);
            for (var i = 0; i < limit; i++)
            {
                if (NeighbourSpread(ranked[i]) is { } spread)
                {
                    ranked[i] = ranked[i] with { NeighbourSpread = spread };
                }
            }
        }

        private float? NeighbourSpread(LineupSolution target)
        {
            float? spread = null;
            int neighbours = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                List<LineupSolution> pool = pass == 0 ? _hits : _fallback;
                foreach (LineupSolution other in pool)
                {
                    if (ReferenceEquals(other, target))
                    {
                        continue;
                    }

                    float yawGap = MathF.Abs(MathF.IEEERemainder(other.Yaw - target.Yaw, 360f));
                    float pitchGap = MathF.Abs(other.Pitch - target.Pitch);

                    // A re-evaluation of the very same aim carries no stability
                    // information; only genuinely nudged aims do.
                    if (yawGap < 0.01f && pitchGap < 0.01f)
                    {
                        continue;
                    }

                    if (yawGap > NeighbourYawWindowDegrees || pitchGap > NeighbourPitchWindowDegrees)
                    {
                        continue;
                    }

                    neighbours++;
                    float dx = other.ImpactPosition.X - target.ImpactPosition.X;
                    float dy = other.ImpactPosition.Y - target.ImpactPosition.Y;
                    float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                    if (spread is null || distance > spread.Value)
                    {
                        spread = distance;
                    }
                }
            }

            // One neighbour is the edge of the grid, not a stability reading.
            return neighbours >= 2 ? spread : null;
        }

        /// <summary>
        /// Amoeba descent around the best coarse miss. The coarse grid only samples
        /// every three degrees of elevation, so a five unit zone routinely sits
        /// between two samples; this walks the remaining distance with a handful of
        /// extra forward simulations. Vertices start unscored (NaN cost) so the
        /// descent can be suspended between ticks.
        /// </summary>
        private sealed class Refinement
        {
            private const double YawStartStep = 2d;
            private const double ElevationStartStep = 3d;
            private const double AngleToleranceDegrees = 0.05d;
            private const int MaxIterations = 40;
            private const int MaxEvaluations = 60;
            private const double MinElevationDegrees = -89d;
            private const double MaxElevationDegrees = 89d;

            private readonly SlicedSearch _search;
            private readonly double _bias;
            private readonly List<Vertex[]> _simplices = new();
            private int _active;
            private int _iterations;
            private int _evaluations;

            internal LineupSolution? BestSolution { get; private set; }

            internal bool BestInZone { get; private set; }

            internal Refinement(SlicedSearch search, IReadOnlyList<LineupSolution> seeds)
            {
                _search = search;
                _bias = search._profile.AimBiasDegrees;
                foreach (var seed in seeds)
                {
                    // Seed.Pitch is the aim pitch; the descent moves in physical
                    // release elevation, which the engine reaches by raising the
                    // aim through the pitch-dependent bias (physical elevation =
                    // aim + bias*(90-|aim|)/90 in the contract convention).
                    double elevation = ElevationFromAimPitch(seed.Pitch, _bias);
                    _simplices.Add(new[]
                    {
                        new Vertex(seed.Yaw, elevation, seed.TargetDistance),
                        new Vertex(Normalize(seed.Yaw + YawStartStep), elevation, double.NaN),
                        new Vertex(seed.Yaw, elevation + ElevationStartStep, double.NaN),
                    });
                }
            }

            internal bool Done => _active >= _simplices.Count;

            /// <summary>
            /// Retires the current seed and moves to the next one. The evaluation
            /// budget is per seed: sharing one counter across the whole refinement
            /// let the first seed spend all of it, so later seeds - often the ones
            /// whose basins hold bounce line-ups - were skipped without a single
            /// simulation.
            /// </summary>
            private void AdvanceSeed()
            {
                _iterations = 0;
                _evaluations = 0;
                _active++;
            }

            internal void RunSlice(double budgetMilliseconds, Stopwatch slice)
            {
                while (slice.Elapsed.TotalMilliseconds < budgetMilliseconds
                    && _iterations < MaxIterations
                    && _active < _simplices.Count)
                {
                    var vertices = _simplices[_active];
                    var scored = true;
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        if (double.IsNaN(vertices[i].Cost))
                        {
                            vertices[i] = vertices[i] with
                            {
                                Cost = Score(vertices[i].Yaw, vertices[i].Elevation),
                            };
                            scored = false;
                            break;
                        }
                    }

                    if (!scored)
                    {
                        continue;
                    }

                    if (_evaluations >= MaxEvaluations)
                    {
                        AdvanceSeed();
                        continue;
                    }

                    Array.Sort(vertices, static (a, b) => a.Cost.CompareTo(b.Cost));
                    _simplices[_active] = vertices;
                    double spread = Math.Max(
                        Math.Max(Math.Abs(vertices[1].Yaw - vertices[0].Yaw), Math.Abs(vertices[1].Elevation - vertices[0].Elevation)),
                        Math.Max(Math.Abs(vertices[2].Yaw - vertices[0].Yaw), Math.Abs(vertices[2].Elevation - vertices[0].Elevation)));
                    if (spread < AngleToleranceDegrees)
                    {
                        AdvanceSeed();
                        continue;
                    }

                    double sumYaw = vertices[0].Yaw + vertices[1].Yaw + vertices[2].Yaw;
                    double sumElevation = vertices[0].Elevation + vertices[1].Elevation + vertices[2].Elevation;
                    double reflectYaw = Normalize(sumYaw - (2d * vertices[2].Yaw));
                    double reflectElevation = Math.Clamp(
                        sumElevation - (2d * vertices[2].Elevation),
                        MinElevationDegrees,
                        MaxElevationDegrees);
                    double reflectCost = Score(reflectYaw, reflectElevation);
                    if (reflectCost >= vertices[0].Cost && reflectCost < vertices[1].Cost)
                    {
                        vertices[2] = new Vertex(reflectYaw, reflectElevation, reflectCost);
                    }
                    else if (reflectCost < vertices[0].Cost)
                    {
                        double expandYaw = Normalize(sumYaw - (3d * vertices[2].Yaw));
                        double expandElevation = Math.Clamp(
                            sumElevation - (3d * vertices[2].Elevation),
                            MinElevationDegrees,
                            MaxElevationDegrees);
                        double expandCost = Score(expandYaw, expandElevation);
                        vertices[2] = expandCost < reflectCost
                            ? new Vertex(expandYaw, expandElevation, expandCost)
                            : new Vertex(reflectYaw, reflectElevation, reflectCost);
                    }
                    else
                    {
                        double contractYaw = Normalize((sumYaw + vertices[2].Yaw) / 4d);
                        double contractElevation = Math.Clamp(
                            (sumElevation + vertices[2].Elevation) / 4d,
                            MinElevationDegrees,
                            MaxElevationDegrees);
                        double contractCost = Score(contractYaw, contractElevation);
                        if (contractCost < vertices[2].Cost)
                        {
                            vertices[2] = new Vertex(contractYaw, contractElevation, contractCost);
                        }
                        else
                        {
                            for (var i = 1; i < vertices.Length; i++)
                            {
                                vertices[i] = new Vertex(
                                    (vertices[0].Yaw + vertices[i].Yaw) / 2d,
                                    (vertices[0].Elevation + vertices[i].Elevation) / 2d,
                                    double.NaN);
                            }
                        }
                    }

                    _simplices[_active] = vertices;
                    _iterations++;
                    if (_iterations >= MaxIterations)
                    {
                        AdvanceSeed();
                    }
                }
            }

            /// <summary>One forward simulation; also records every landing it produces.</summary>
            private double Score(double yaw, double elevation)
            {
                if (_evaluations >= MaxEvaluations)
                {
                    return double.PositiveInfinity;
                }

                _search._owner.RecordRefinementProbe(_active);
                _evaluations++;
                var candidate = new AngleCandidate
                {
                    Yaw = (float)Math.Round(Normalize(yaw), 1),

                    // Aim pitch = exact inverse of the pitch-dependent bias,
                    // clamped to the +/-89 degree engine window.
                    Pitch = (float)Math.Clamp(
                        Math.Round(AimPitchFromElevation(elevation, _bias), 1),
                        -89f,
                        89f),
                };

                LineupSolution? solution = _search._owner.EvaluateCandidate(
                    _search._request, candidate, _search._world, out bool inZone);
                if (solution is null)
                {
                    return double.PositiveInfinity;
                }

                if (inZone)
                {
                    _search._hits.Add(solution);
                }
                else
                {
                    _search._fallback.Add(solution);
                }

                if (BestSolution is null || solution.TargetDistance < BestSolution.TargetDistance)
                {
                    BestSolution = solution;
                    BestInZone = inZone;
                }

                return solution.TargetDistance;
            }

            private static double Normalize(double degrees)
            {
                var wrapped = degrees % 360f;
                if (wrapped > 180f)
                {
                    wrapped -= 360f;
                }
                else if (wrapped <= -180f)
                {
                    wrapped += 360f;
                }

                return wrapped;
            }

            /// <summary>Forward bias: physical elevation the aim pitch produces.</summary>
            private static double ElevationFromAimPitch(double aimPitch, double bias)
            {
                var effective = bias * (90d - Math.Abs(aimPitch)) / 90d;
                return aimPitch + effective;
            }

            /// <summary>
            /// Exact inverse of <see cref="ElevationFromAimPitch"/>; see
            /// Math.LineupSolver.AimPitchFromElevation for the derivation.
            /// </summary>
            private static double AimPitchFromElevation(double elevation, double bias)
            {
                if (Math.Abs(bias) < 1e-9)
                {
                    return elevation;
                }

                var delta = elevation - bias;
                return delta >= 0d ? delta * 90d / (90d - bias) : delta * 90d / (90d + bias);
            }

            private readonly record struct Vertex(double Yaw, double Elevation, double Cost);
        }
    }

    /// <summary>
    /// Total candidate evaluations since construction, including refinement
    /// probes. Test seam: lets the suite verify the per-seed refinement budget
    /// without reaching into the private search classes.
    /// </summary>
    internal int SimulationCount { get; private set; }

    /// <summary>
    /// Refinement probes grouped by the simplex that ran them. Test seam for
    /// the per-seed budget: before the per-seed budget fix, the first seed
    /// spent the whole shared evaluation budget and later seeds simulated
    /// nothing, so this stayed empty; now every seed that scores at least one
    /// vertex contributes an entry.
    /// </summary>
    internal IReadOnlyList<int> RefinementProbeCountsBySeed => _refinementProbeCounts;

    private readonly List<int> _refinementProbeCounts = new();

    internal void RecordRefinementProbe(int seedIndex)
    {
        while (_refinementProbeCounts.Count <= seedIndex)
        {
            _refinementProbeCounts.Add(0);
        }

        _refinementProbeCounts[seedIndex]++;
    }

    /// <summary>
    /// Simulates one candidate angle and reports the resulting line-up, plus
    /// whether it satisfied the zone test. Every simulated landing is returned
    /// (except world-fall failures) so the fallback ranking and the refinement
    /// seed pool always see the true cost surface.
    /// </summary>
    private LineupSolution? EvaluateCandidate(
        LineupRequest request,
        AngleCandidate candidate,
        IWorldGeometry world,
        out bool inZone)
    {
        inZone = false;
        SimulationCount++;

        ThrowOrigin origin = request.Origin;
        GrenadeProfile profile = GrenadeProfiles.Get(request.GrenadeType);
        Vec3 forward = DirectionFromAngles(candidate.Yaw, candidate.Pitch);

        // Release point: eyes pushed 16 units along the BIASED direction,
        // exactly like the engine's ThrowGrenade emits from vecSrc after the
        // aim-bias remap. The unbiased variant released off line at oblique
        // pitches and coupled aim pitch into the release geometry.
        //
        // Engine parity (weapon_basecsgrenade.cpp ThrowGrenade): the offset is
        // not a fixed 16 units. The engine hull-traces 22 units forward with a
        // +/-2 unit hull and pulls back 6 units from wherever it ends, so in
        // the open the grenade releases from eye + 16u, but against a closer
        // wall it releases from wall - 6u. Without the clamp, a lineup aimed
        // over a thin ledge simulates a release point past the obstruction.
        Vec3 biasedForward = ThrowVelocityCalculator.ApplyAimBias(forward, _parameters);
        const float ReleaseTraceDistance = 22f;
        const float ReleasePullback = 6f;
        const float ReleaseHullRadius = 2f;

        // The engine lowers vecSrc BEFORE the 22-unit release trace for the
        // underhand tiers (weapon_basecsgrenade.cpp: Lerp(strength, -12, 0),
        // so -12 u at strength 0 and -6 u for the both-buttons 0.5 strength).
        // The speed tier was already modelled; omitting the drop put every
        // secondary/both release 12 u / 6 u above the real hand.
        float releaseLower = request.Button switch
        {
            ThrowButton.Secondary => 12f,
            ThrowButton.Both => 6f,
            _ => 0f,
        };
        Vec3 releaseEye = origin.Eyes + new Vec3(0f, 0f, -releaseLower);

        TraceHit releaseTrace = world.TraceSphere(
            releaseEye, biasedForward, ReleaseTraceDistance, ReleaseHullRadius);
        Vec3 release = releaseTrace.DidHit
            ? releaseTrace.Position - (biasedForward * ReleasePullback)
            : releaseEye + (biasedForward * _parameters.ReleaseOffset.Value);

        Vec3 velocity = ThrowVelocityCalculator.Compute(
            forward,
            origin.Velocity,
            request.Button,
            request.GrenadeType,
            1f,
            _parameters);

        // The live server's tick interval, not an assumed 64: a 128-tick host
        // integrates twice as many half-tick gravity quanta per second, and the
        // bounce law consumes them at the recorded rate.
        int tickRate = (int)MathF.Round(1f / request.Environment.TickInterval);
        if (tickRate < 1 || tickRate > 1000)
        {
            tickRate = 64;
        }

        ThrowParams parameters = new(release, velocity, request.GrenadeType, 1f, tickRate)
        {
            Profile = profile,
            IgnoreEntityIndex = -1,
        };

       TrajectoryResult result = Simulate(parameters, world);
        Vec3 impact = result.Impact.Position;

        // A trajectory that fell out of the world (below the throw height by a
        // wide margin) is a simulation failure, not a landing: rejecting it
        // here stops the solver from proposing throws that clip through the
        // floor. Landing height is NOT filtered here: altitude differences are
        // already priced into TargetDistance and every miss must stay visible
        // to the fallback ranking and the refinement seed pool, which the old
        // +/-64u Z-window silently starved (line-ups that only exist after a
        // bounce were reported as "closest 316u" because their nearest coarse
        // neighbours never reached the miss pool).
        float zDrop = request.TargetZone.Center.Z - impact.Z;
        bool outOfWorld = impact.Z < request.Origin.Feet.Z - KillDropUnits;
        if (outOfWorld)
        {
            return null;
        }

        float distance = HorizontalDistance(impact, request.TargetZone.Center);
        inZone = distance <= request.TargetZone.Radius;

       return new LineupSolution
        {
            Yaw = candidate.Yaw,
            Pitch = candidate.Pitch,
            ReleasePosition = release,
            ImpactPosition = impact,
            TargetDistance = distance,
            Steps = result.Points.Count,
            Note = Describe(request),
        };
    }

    /// <summary>Human readable summary of how the line-up is executed.</summary>
    private static string Describe(LineupRequest request)
    {
        string stance = request.ThrowMode switch
        {
            ThrowMode.Crouch => "crouch throw",
            ThrowMode.Jump => "jump throw",
            _ => "standing throw",
        };

        string button = request.Button switch
        {
            ThrowButton.Secondary => "right click",
            ThrowButton.Both => "both buttons",
            _ => "left click",
        };

        return $"{stance} + {button}";
    }

    /// <summary>
    /// Unit direction for a view angle pair. Follows the contract convention:
    /// yaw 0 looks down +X, positive pitch looks up.
    /// </summary>
    internal static Vec3 DirectionFromAngles(float yawDegrees, float pitchDegrees)
    {
        float yaw = yawDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float cosPitch = MathF.Cos(pitch);

        return new Vec3(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            MathF.Sin(pitch)).Normalized();
    }

    /// <summary>Test seam for the internal angle-to-direction helper.</summary>
    internal static Vec3 DirectionFromAnglesForTest(float yawDegrees, float pitchDegrees)
        => DirectionFromAngles(yawDegrees, pitchDegrees);

    /// <summary>Planar distance between two points, ignoring Z.</summary>
    internal static float HorizontalDistance(Vec3 a, Vec3 b)
        => new Vec3(a.X - b.X, a.Y - b.Y, 0f).Length;
}

/// <summary>
/// One world contact of a simulated flight: where the hull centre stopped,
/// the surface normal, the engine contents and whether the contact came from
/// the same-tick push sweep. Diagnostic evidence, not tuning state.
/// </summary>
public readonly record struct SimulationContact(
    Vec3 Position,
    Vec3 Normal,
    int BounceCount,
    string? ClassName,
    ulong ContentsRaw,
    bool IsEmbedded,
    float Fraction,
    bool IsPush);
