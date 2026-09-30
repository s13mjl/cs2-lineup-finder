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
/// Integration is semi-implicit Euler at
/// <see cref="PhysicsParameters.SubstepsPerTick"/> substeps per tick. Sweeps are
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
    /// Nudge off the surface after a contact so the next sweep does not start
    /// exactly on the plane and immediately re-hit it. The engine gives the
    /// projectile a 4-unit hull; this stays inside that skin.
    /// </summary>
    private const float SurfaceSkin = 0.05f;

    /// <summary>
    /// Speed (u/s) along the contact normal above which the projectile counts as
    /// having left the surface. A bounce always exceeds it, so the first frame
    /// after a contact never has rolling friction applied to it twice.
    /// </summary>
    private const float TakeoffSpeedFloor = 1f;

    /// <summary>World up, matching the contract coordinate convention.</summary>
    private static readonly Vec3 Up = new(0f, 0f, 1f);

    private readonly PhysicsParameters _parameters;

    public GrenadeSimulator(PhysicsParameters? parameters = null)
    {
        _parameters = parameters ?? new PhysicsParameters();
    }

    /// <summary>The live parameter set. Mutating it changes subsequent simulations.</summary>
    public PhysicsParameters Parameters => _parameters;

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

        Vec3 position = parameters.Origin;
        Vec3 velocity = ResolveLaunchVelocity(parameters);

        float dt = ResolveTimestep(parameters);
        int substeps = Math.Max(1, (int)MathF.Round(_parameters.SubstepsPerTick.Value));
        float subDt = dt / substeps;
        float sampleInterval = _parameters.SampleInterval.Value;
        float maxFlight = _parameters.MaxFlightSeconds.Value;
        float fuse = _parameters.FuseSeconds.Value;
        int ignoreEntityIndex = parameters.IgnoreEntityIndex;
        float restSpeedSq = _parameters.RestSpeed.Value * _parameters.RestSpeed.Value;

        float time = 0f;
        float nextSampleTime = 0f;
        float apex = position.Z;
        float pathLength = 0f;

        int bounceCount = 0;
        int stepsTaken = 0;
        int substepCount = 0;
        bool onGround = false;
        bool smokeFirstBounceDone = false;
        Vec3 lastNormal = Up;

        path.Add(new TrajectoryPoint(position, velocity, 0f, 0));

        while (time < maxFlight)
        {
            for (int i = 0; i < substeps; i++)
            {
                substepCount++;
                Vec3 before = position;

                // Exact kinematic step. Under constant acceleration the position
                // update is p += v*dt + a*dt^2/2, which reproduces the analytic
                // parabola to float precision. Plain semi-implicit Euler (updating
                // v first, then p += v*dt) is only first-order and drifts about a
                // unit over a long throw, which breaks the 0.5 unit acceptance
                // bound, so the half-step form is used instead.
                Vec3 accel = Up * -_parameters.GrenadeGravity.Value;
                Vec3 target = position + (velocity * subDt) + (accel * (0.5f * subDt * subDt));

                velocity += accel * subDt;
                BounceResolver.ApplyDrag(ref velocity, subDt, _parameters);

                // Sweep in adaptive hops; every hop consults the raycaster, so a
                // grenade cannot pass through geometry between samples.
                TraceHit hit = Sweep(world, position, target, velocity.Length, ignoreEntityIndex, ref stepsTaken);

                if (hit.DidHit && IsWorldGeometry(hit, ignoreEntityIndex))
                {
                    Vec3 normal = hit.Normal.Normalized();
                    if (normal.LengthSquared <= 1e-8f)
                    {
                        normal = Up;
                    }

                    lastNormal = normal;
                    onGround = normal.Z > _parameters.FloorNormalThreshold.Value;

                    velocity = BounceResolver.Resolve(
                        velocity, normal, onGround, parameters.GrenadeType, _parameters, ref smokeFirstBounceDone);
                    position = hit.Position + (normal * SurfaceSkin);
                    bounceCount++;

                    // A molotov pops on this contact; check before the rest tests.
                    if (DetonationModel.Evaluate(parameters.GrenadeType, time, velocity.Length, false, true, fuse).Detonated
                        || bounceCount >= MaxBounceCount
                        || velocity.LengthSquared < restSpeedSq)
                    {
                        return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, substepCount, apex, pathLength, out diagnostics);
                    }
                }
                else
                {
                    // A miss (or a hit we deliberately ignore) means nothing
                    // blocked this substep, so the projectile advances the full
                    // step. IWorldGeometry only reports a contact point on a
                    // hit; a miss carries no end position, so the caller has to
                    // apply the intended displacement itself.
                    position = target;
                }

                pathLength += (position - before).Length;
                if (position.Z > apex)
                {
                    apex = position.Z;
                }

                time += subDt;
            }

            // Rolling friction only applies while the projectile is actually in
            // contact. A positive velocity along the contact normal means the
            // grenade is leaving the surface, so friction is skipped and the flag
            // cleared; otherwise the exponential decay would keep eating a long
            // flight and the grenade would stall after a single bounce.
            bool takingOff = Vec3.Dot(velocity, lastNormal) > TakeoffSpeedFloor;

            if (onGround && !takingOff)
            {
                velocity = BounceResolver.ApplyRollingFriction(velocity, lastNormal, dt, _parameters);
            }

            onGround &= !takingOff;

            // Record on the tick grid so the path lines up with a 64 Hz recording.
            if (time >= nextSampleTime)
            {
                path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
                nextSampleTime += sampleInterval;
            }

            bool atRest = velocity.LengthSquared < restSpeedSq;
            bool fuseDone = DetonationModel.Evaluate(
                parameters.GrenadeType, time, velocity.Length, atRest, false, fuse).Detonated;

            if (fuseDone || atRest)
            {
                path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
                return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, substepCount, apex, pathLength, out diagnostics);
            }

        }

        path.Add(new TrajectoryPoint(position, velocity, time, bounceCount));
        return Finish(path, position, lastNormal, bounceCount, time, parameters, stepsTaken, substepCount, apex, pathLength, out diagnostics);
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

        // One sweep is enough when the move already fits inside a single hop.
        if (distance <= hop)
        {
            stepsTaken++;
            return world.TraceRay(from, direction, distance, ignoreEntityIndex);
        }

        // Divide the move into equal hops so the last one lands exactly on `to`.
        int hops = (int)MathF.Ceiling(distance / hop);
        float actualHop = distance / hops;

        Vec3 cursor = from;
        for (int i = 0; i < hops; i++)
        {
            Vec3 next = cursor + (direction * actualHop);
            stepsTaken++;

            TraceHit trace = world.TraceRay(cursor, direction, actualHop, ignoreEntityIndex);
            if (trace.DidHit && IsWorldGeometry(trace, ignoreEntityIndex))
            {
                return trace;
            }

            // A dynamic entity we deliberately ignore must not stall the walk,
            // so always advance by the full planned hop.
            cursor = next;
        }

        return new TraceHit(false, to, Vec3.Zero, 1f);
    }

    /// <summary>
    /// True when the hit was world BSP / static brush geometry rather than a
    /// dynamic entity. The engine reports -1 for the world, so anything else
    /// with a valid index is treated as a dynamic entity and ignored.
    /// </summary>
    private static bool IsWorldGeometry(TraceHit hit, int ignoreEntityIndex)
        => hit.EntityIndex < 0 || hit.EntityIndex == ignoreEntityIndex;

    /// <summary>
    /// The velocity the flight starts with. The caller supplies it already
    /// resolved (aim direction at the mode speed plus the inherited share of the
    /// thrower velocity), so this is a pass-through kept as a seam.
    /// </summary>
    private static Vec3 ResolveLaunchVelocity(ThrowParams parameters) => parameters.Velocity;

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
            await foreach (AngleCandidate candidate in solver.EnumerateCandidatesAsync(request, cancellationToken).ConfigureAwait(false))
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
                else if (fallback.Count < MaxFallbackResults)
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
        // the player than an empty list, but the status has to say so.
        fallback.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));
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

    /// <summary>
    /// Simulates one candidate angle and reports the resulting line-up, plus
    /// whether it satisfied the zone test.
    /// </summary>
    private LineupSolution? EvaluateCandidate(
        LineupRequest request,
        AngleCandidate candidate,
        IWorldGeometry world,
        out bool inZone)
    {
        inZone = false;

        ThrowOrigin origin = request.Origin;
        GrenadeProfile? profile = null;
        Vec3 forward = DirectionFromAngles(candidate.Yaw, candidate.Pitch);

        // Release point: eyes pushed forward along the aim, matching the
        // 16-unit offset the engine applies in ThrowGrenade().
        float releaseOffset = 16f;
        Vec3 release = origin.Eyes + (forward * releaseOffset);

        Vec3 velocity = ThrowVelocityCalculator.Compute(
            forward,
            origin.Velocity,
            request.Button,
            request.GrenadeType,
            1f,
            _parameters);

        ThrowParams parameters = new(release, velocity, request.GrenadeType, 1f, 64)
        {
            Profile = profile,
            IgnoreEntityIndex = -1,
        };

        TrajectoryResult result = Simulate(parameters, world);
        Vec3 impact = result.Impact.Position;

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

    /// <summary>Planar distance between two points, ignoring Z.</summary>
    internal static float HorizontalDistance(Vec3 a, Vec3 b)
        => new Vec3(a.X - b.X, a.Y - b.Y, 0f).Length;
}
