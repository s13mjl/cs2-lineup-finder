// <copyright file="ITrajectorySimulator.cs" company="CS2LineupFinder">
// Forward-simulation contract. Implemented by src/CS2LineupFinder.Core/ and
// consumed by plugins/CS2LineupFinder/.
//
// This file is the one slice of contracts/ owned by the physics-sim layer. It
// is interfaces + DTOs only: no logic, no engine types. Everything else in
// contracts/ (Vec3, GrenadeType, ThrowMode, ThrowButton, GroundZone,
// SimulationEnvironment, IWorldGeometry, GrenadeProfile, StanceProfile,
// ILineupSolver) is owned by the integration layer and is NOT redeclared here.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CS2LineupFinder.Contracts;

/// <summary>One fully resolved throw, ready to simulate.</summary>
public sealed class ThrowParams
{
    /// <summary>Creates throw parameters.</summary>
    /// <param name="origin">Release point in world units.</param>
    /// <param name="velocity">Initial velocity in u/s, already resolved.</param>
    /// <param name="grenadeType">Grenade family being thrown.</param>
    /// <param name="throwStrength">Release speed scalar, 0..1.</param>
    /// <param name="gameTickRate">Server tick rate.</param>
    public ThrowParams(Vec3 origin, Vec3 velocity, GrenadeType grenadeType, float throwStrength, int gameTickRate)
    {
        Origin = origin;
        Velocity = velocity;
        GrenadeType = grenadeType;
        ThrowStrength = throwStrength;
        GameTickRate = gameTickRate;
    }

    /// <summary>Release point in world units.</summary>
    public Vec3 Origin { get; }

    /// <summary>
    /// Initial velocity in u/s, already resolved as
    /// <c>Forward * LaunchSpeed + PlayerVelocity * VelocityInheritance</c>.
    /// </summary>
    public Vec3 Velocity { get; }

    /// <summary>Grenade family being thrown.</summary>
    public GrenadeType GrenadeType { get; }

    /// <summary>
    /// Stance the throw is executed from. Carried so the simulator can derive the
    /// release point and the throw strength the caller resolved.
    /// </summary>
    public ThrowMode Mode { get; init; }

    /// <summary>Release speed scalar, 0..1, where 1 is a normal release.</summary>
    public float ThrowStrength { get; }

    /// <summary>Server tick rate. Competitive CS2 runs 64.</summary>
    public int GameTickRate { get; }

    /// <summary>Physics profile from weapon VData, when the caller resolved one.</summary>
    public GrenadeProfile? Profile { get; init; }

    /// <summary>Optional zone test. When null, <c>IsInZone</c> is always false.</summary>
    public Func<Vec3, bool>? ZoneTest { get; init; }

    /// <summary>Entity index to ignore while tracing, typically the thrower.</summary>
    public int IgnoreEntityIndex { get; init; } = -1;
}

/// <summary>One sampled point along the flight path.</summary>
public readonly struct TrajectoryPoint
{
    /// <summary>Creates a trajectory sample.</summary>
    /// <param name="position">World position at this instant.</param>
    /// <param name="velocity">World velocity at this instant.</param>
    /// <param name="time">Seconds since release.</param>
    /// <param name="bounceCount">Bounces recorded by this sample.</param>
    public TrajectoryPoint(Vec3 position, Vec3 velocity, float time, int bounceCount)
    {
        Position = position;
        Velocity = velocity;
        Time = time;
        BounceCount = bounceCount;
    }

    /// <summary>World position at this instant.</summary>
    public Vec3 Position { get; }

    /// <summary>World velocity at this instant.</summary>
    public Vec3 Velocity { get; }

    /// <summary>Seconds since release.</summary>
    public float Time { get; }

    /// <summary>Bounces recorded by this sample.</summary>
    public int BounceCount { get; }
}

/// <summary>Where and how the projectile finally came to rest.</summary>
public readonly struct FinalImpact
{
    /// <summary>Creates a final impact record.</summary>
    /// <param name="position">Rest or detonation position.</param>
    /// <param name="collisionNormal">Surface normal of the last contact.</param>
    /// <param name="bounceCount">Bounces that occurred.</param>
    /// <param name="time">Seconds since release.</param>
    /// <param name="detonated">True when the grenade went off rather than settling.</param>
    /// <param name="detonatedType">Grenade family that detonated.</param>
    public FinalImpact(Vec3 position, Vec3 collisionNormal, int bounceCount, float time, bool detonated, GrenadeType detonatedType)
    {
        Position = position;
        CollisionNormal = collisionNormal;
        BounceCount = bounceCount;
        Time = time;
        Detonated = detonated;
        DetonatedType = detonatedType;
    }

    /// <summary>Rest or detonation position in world units.</summary>
    public Vec3 Position { get; }

    /// <summary>Surface normal of the last contact, straight up for a flat floor.</summary>
    public Vec3 CollisionNormal { get; }

    /// <summary>Number of bounces recorded during flight.</summary>
    public int BounceCount { get; }

    /// <summary>Seconds since release.</summary>
    public float Time { get; }

    /// <summary>True when the grenade went off rather than coming to rest.</summary>
    public bool Detonated { get; }

    /// <summary>Grenade family that detonated.</summary>
    public GrenadeType DetonatedType { get; }
}

/// <summary>Full output of one simulation.</summary>
public sealed class TrajectoryResult
{
    /// <summary>Creates a simulation result.</summary>
    /// <param name="points">Sampled path, ordered by time.</param>
    /// <param name="impact">Final impact record.</param>
    /// <param name="isInZone">True when the rest point passed the caller's zone test.</param>
    public TrajectoryResult(IReadOnlyList<TrajectoryPoint> points, FinalImpact impact, bool isInZone)
    {
        Points = points;
        Impact = impact;
        IsInZone = isInZone;
    }

    /// <summary>Sampled path, ordered by time.</summary>
    public IReadOnlyList<TrajectoryPoint> Points { get; }

    /// <summary>Final impact record.</summary>
    public FinalImpact Impact { get; }

    /// <summary>True when the rest point satisfies the caller's zone test.</summary>
    public bool IsInZone { get; }

    /// <summary>Convenience accessor for the final position.</summary>
    public Vec3 EndPosition => Impact.Position;

    /// <summary>Total flight time in seconds.</summary>
    public float FlightTime => Impact.Time;

    /// <summary>Bounces recorded during flight.</summary>
    public int BounceCount => Impact.BounceCount;
}

/// <summary>Extra numbers produced by the diagnostic overload.</summary>
public readonly struct SimulationDiagnostics
{
    /// <summary>Creates a diagnostics record.</summary>
    /// <param name="apexHeight">Highest point above the release point, units.</param>
    /// <param name="horizontalDistance">Planar distance from origin to rest, units.</param>
    /// <param name="pathLength">Total path length including bounces, units.</param>
    /// <param name="stepsTaken">Collision sweeps issued to the world.</param>
    /// <param name="substeps">Physics substeps integrated.</param>
    public SimulationDiagnostics(float apexHeight, float horizontalDistance, float pathLength, int stepsTaken, int substeps)
    {
        ApexHeight = apexHeight;
        HorizontalDistance = horizontalDistance;
        PathLength = pathLength;
        StepsTaken = stepsTaken;
        Substeps = substeps;
    }

    /// <summary>Highest point above the release point, units.</summary>
    public float ApexHeight { get; }

    /// <summary>Planar distance from origin to rest point, units.</summary>
    public float HorizontalDistance { get; }

    /// <summary>Total path length including bounces, units.</summary>
    public float PathLength { get; }

    /// <summary>Collision sweeps issued to the world.</summary>
    public int StepsTaken { get; }

    /// <summary>Physics substeps integrated.</summary>
    public int Substeps { get; }
}

/// <summary>Forward simulation entry point consumed by the plugin shell.</summary>
public interface ITrajectorySimulator
{
    /// <summary>Simulates one throw and returns the path plus the final impact.</summary>
    /// <param name="parameters">Resolved throw to simulate.</param>
    /// <param name="world">World geometry used for collision sweeps.</param>
    /// <returns>The sampled path and the final impact.</returns>
    TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world);

    /// <summary>
    /// Simulates and additionally reports apex, range and path length, which is
    /// what the calibration tool fits against.
    /// </summary>
    /// <param name="parameters">Resolved throw to simulate.</param>
    /// <param name="world">World geometry used for collision sweeps.</param>
    /// <param name="diagnostics">Receives apex, range, path length and step counts.</param>
    /// <returns>The sampled path and the final impact.</returns>
    TrajectoryResult SimulateWithDiagnostics(ThrowParams parameters, IWorldGeometry world, out SimulationDiagnostics diagnostics);

    /// <summary>
    /// Solves for view angles that land the grenade inside
    /// <see cref="LineupRequest.TargetZone"/>, driving the inverse
    /// <see cref="ILineupSolver"/> and the forward simulation together.
    /// </summary>
    /// <param name="request">Request describing origin, stance and target zone.</param>
    /// <param name="solver">Candidate angle provider.</param>
    /// <param name="world">World geometry used for collision sweeps.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>A <see cref="SolverOutcome"/> carrying the ranked solutions.</returns>
    Task<SolverOutcome> SolveLineupAsync(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world,
        CancellationToken cancellationToken = default);
}
