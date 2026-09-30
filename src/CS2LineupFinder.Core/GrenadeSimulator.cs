// <copyright file="GrenadeSimulator.cs" company="CS2LineupFinder">
// Forward ballistic simulator for CS2 throwables.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core;

/// <summary>
/// Forward-integrates a grenade until it comes to rest or detonates, sweeping
/// the world through an <see cref="IRaycaster"/> as it goes.
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
public sealed class GrenadeSimulator : ITrajectorySimulator
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
    public TrajectoryResult Simulate(ThrowParams parameters, IRaycaster raycaster)
        => SimulateCore(parameters, raycaster, out _);

    /// <inheritdoc />
    public TrajectoryResult SimulateWithDiagnostics(
        ThrowParams parameters,
        IRaycaster raycaster,
        out SimulationDiagnostics diagnostics)
        => SimulateCore(parameters, raycaster, out diagnostics);

    private TrajectoryResult SimulateCore(ThrowParams parameters, IRaycaster raycaster, out SimulationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(raycaster);

        List<TrajectoryPoint> path = new(256);

        Vector3 position = parameters.Origin;
        Vector3 velocity = ResolveLaunchVelocity(parameters);

        float dt = ResolveTimestep(parameters);
        int substeps = Math.Max(1, (int)MathF.Round(_parameters.SubstepsPerTick.Value));
        float subDt = dt / substeps;
        float sampleInterval = _parameters.SampleInterval.Value;
        float maxFlight = _parameters.MaxFlightSeconds.Value;
        float fuse = _parameters.FuseSeconds.Value;
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
        Vector3 lastNormal = Vector3.UnitZ;

        path.Add(new TrajectoryPoint(position, velocity, 0f, 0));

        while (time < maxFlight)
        {
            for (int i = 0; i < substeps; i++)
            {
                substepCount++;
                Vector3 before = position;

                // Exact kinematic step. Under constant acceleration the position
                // update is p += v*dt + a*dt^2/2, which reproduces the analytic
                // parabola to float precision. Plain semi-implicit Euler (updating
                // v first, then p += v*dt) is only first-order and drifts about a
                // unit over a long throw, which breaks the 0.5 unit acceptance
                // bound, so the half-step form is used instead.
                Vector3 accel = Vector3.UnitZ * -_parameters.GrenadeGravity.Value;
                Vector3 target = position + (velocity * subDt) + (accel * (0.5f * subDt * subDt));

                velocity += accel * subDt;
                BounceResolver.ApplyDrag(ref velocity, subDt, _parameters);

                // Sweep in adaptive hops; every hop consults the raycaster, so a
                // grenade cannot pass through geometry between samples.
                TraceResult hit = Sweep(raycaster, position, target, velocity.Length, ref stepsTaken);

                if (hit.Hit && hit.IsWorldGeometry)
                {
                    Vector3 normal = hit.PlaneNormal.Normalized();
                    if (normal.IsZero())
                    {
                        normal = Vector3.UnitZ;
                    }

                    lastNormal = normal;
                    onGround = normal.Z > _parameters.FloorNormalThreshold.Value;

                    velocity = BounceResolver.Resolve(
                        velocity, normal, onGround, parameters.GrenadeType, _parameters, ref smokeFirstBounceDone);
                    position = hit.EndPosition + (normal * SurfaceSkin);
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
                    position = hit.EndPosition;
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
            bool takingOff = Vector3.Dot(velocity, lastNormal) > TakeoffSpeedFloor;

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
    private TraceResult Sweep(IRaycaster raycaster, Vector3 from, Vector3 to, float speed, ref int stepsTaken)
    {
        Vector3 delta = to - from;
        float distance = delta.Length;

        if (distance <= 1e-4f)
        {
            return TraceResult.Miss(from, to);
        }

        Vector3 direction = delta / distance;

        // One sweep is enough when the move already fits inside a single hop.
        float hop = BounceResolver.StepDistance(speed, _parameters);
        if (distance <= hop)
        {
            stepsTaken++;
            return raycaster.TraceRay(from, to, TraceMask.AllStatic);
        }

        // Divide the move into equal hops so the last one lands exactly on `to`.
        int hops = (int)MathF.Ceiling(distance / hop);
        float actualHop = distance / hops;

        Vector3 cursor = from;
        for (int i = 0; i < hops; i++)
        {
            Vector3 next = cursor + (direction * actualHop);
            stepsTaken++;

            TraceResult trace = raycaster.TraceRay(cursor, next, TraceMask.AllStatic);
            if (trace.Hit && trace.IsWorldGeometry)
            {
                return trace;
            }

            // A non-world hit (a dynamic entity we deliberately ignore) must not
            // stall the walk, so always advance by the full planned hop.
            cursor = next;
        }

        return TraceResult.Miss(from, to);
    }


    /// <summary>
    /// The velocity the flight starts with. When the caller supplies a
    /// <see cref="ThrowParams.Mode"/> the launch speed is re-derived from that mode
    /// so calibration can isolate speed from aim; otherwise the supplied velocity
    /// is used verbatim.
    /// </summary>
    private Vector3 ResolveLaunchVelocity(ThrowParams parameters)
    {
        if (parameters.Mode is null)
        {
            return parameters.Velocity;
        }

        Vector3 direction = parameters.Velocity.IsZero()
            ? Vector3.UnitX
            : parameters.Velocity.Normalized();

        float speed = ThrowVelocityCalculator.LaunchSpeed(parameters.Mode.Value, _parameters)
            * Math.Clamp(parameters.ThrowStrength, 0f, 1f);

        return direction * speed;
    }

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
        Vector3 position,
        Vector3 normal,
        int bounceCount,
        float time,
        ThrowParams parameters,
        int stepsTaken,
        int substeps,
        float apex,
        float pathLength,
        out SimulationDiagnostics diagnostics)
    {
        float horizontal = new Vector2(position.X - parameters.Origin.X, position.Y - parameters.Origin.Y).Length;

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
}

