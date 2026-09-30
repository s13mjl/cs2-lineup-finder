// <copyright file="ITrajectorySimulator.cs" company="CS2LineupFinder">
// Shared contract consumed by the plugin shell and implemented in
// src/CS2LineupFinder.Core/. Keep this file free of any implementation detail.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CS2LineupFinder.Contracts;

/// <summary>Throwable item classes understood by the simulator.</summary>
public enum GrenadeType
{
    Unknown = 0,

    /// <summary>Smoke grenade. Fuses at 1.5 s but only smokes once at rest.</summary>
    Smoke = 1,

    /// <summary>High-explosive grenade. Fuses at 1.5 s regardless of motion.</summary>
    HeGrenade = 2,

    /// <summary>Flashbang. Fuses at 1.5 s.</summary>
    Flashbang = 3,

    /// <summary>Molotov / incendiary. Detonates on first contact after its arm time.</summary>
    Molotov = 4,

    /// <summary>Decoy. Bounces for ~15 s before exploding.</summary>
    Decoy = 5,
}

/// <summary>How the throw was released. Determines the launch speed tier.</summary>
public enum ThrowMode
{
    /// <summary>Left mouse only: full power, 675 u/s.</summary>
    FullThrow = 0,

    /// <summary>Both buttons: 438.75 u/s high arc.</summary>
    Lob = 1,

    /// <summary>Right mouse only: 202.5 u/s short toss.</summary>
    Underhand = 2,
}

/// <summary>Inputs required to simulate one throw.</summary>
public sealed class ThrowParams
{
    public ThrowParams(Vector3 origin, Vector3 velocity, GrenadeType grenadeType, float throwStrength, int gameTickRate)
    {
        Origin = origin;
        Velocity = velocity;
        GrenadeType = grenadeType;
        ThrowStrength = throwStrength;
        GameTickRate = gameTickRate;
    }

    /// <summary>Release point, typically <c>AbsOrigin + ViewOffset + Forward * 16</c>.</summary>
    public Vector3 Origin { get; }

    /// <summary>
    /// Initial velocity in u/s. This is the *already resolved* launch velocity:
    /// <c>Forward * LaunchSpeed(Mode) + PlayerVelocity * VelocityInheritance</c>.
    /// See <c>ThrowVelocityCalculator</c> and docs/PHYSICS.md.
    /// </summary>
    public Vector3 Velocity { get; }

    public GrenadeType GrenadeType { get; }

    /// <summary>
    /// Scalar 0..1 release speed from the button hold. 1.0 is a normal release.
    /// It scales the authored throw velocity, see docs/PHYSICS.md.
    /// </summary>
    public float ThrowStrength { get; }

    /// <summary>Server tick rate. CS2 competitive servers run 64.</summary>
    public int GameTickRate { get; }

    /// <summary>
    /// Optional throw mode. When set, <see cref="Velocity"/> is re-derived from it,
    /// which lets callers calibrate against a pure mode-based launch speed.
    /// </summary>
    public ThrowMode? Mode { get; init; }

    /// <summary>Zone volume test hook supplied by the plugin shell. Null disables the check.</summary>
    public Func<Vector3, bool>? ZoneTest { get; init; }
}

/// <summary>One sampled point along the flight path.</summary>
public readonly struct TrajectoryPoint
{
    public TrajectoryPoint(Vector3 position, Vector3 velocity, float time, int bounceCount)
    {
        Position = position;
        Velocity = velocity;
        Time = time;
        BounceCount = bounceCount;
    }

    public Vector3 Position { get; }

    public Vector3 Velocity { get; }

    /// <summary>Seconds elapsed since release.</summary>
    public float Time { get; }

    /// <summary>How many bounces had occurred by this sample.</summary>
    public int BounceCount { get; }
}
/// <summary>Immutable 2-component vector, used for planar (XZ/YZ-independent) distance.</summary>
public readonly struct Vector2
{
    public Vector2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }

    public float Y { get; }

    /// <summary>Euclidean length, used for horizontal range in world units.</summary>
    public float Length => (float)Math.Sqrt((X * X) + (Y * Y));

    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", X, Y);
}

/// <summary>Where and how the projectile finally came to rest.</summary>
public readonly struct FinalImpact
{
    public FinalImpact(Vector3 position, Vector3 collisionNormal, int bounceCount, float time, bool detonated, GrenadeType detonatedType)
    {
        Position = position;
        CollisionNormal = collisionNormal;
        BounceCount = bounceCount;
        Time = time;
        Detonated = detonated;
        DetonatedType = detonatedType;
    }

    public Vector3 Position { get; }

    /// <summary>Surface normal of the last contact. <see cref="Vector3.UnitZ"/> for a flat floor.</summary>
    public Vector3 CollisionNormal { get; }

    public int BounceCount { get; }

    /// <summary>Seconds elapsed since release.</summary>
    public float Time { get; }

    /// <summary>True if the grenade went off mid-flight rather than coming to rest.</summary>
    public bool Detonated { get; }

    /// <summary>Which effect plays at the rest point.</summary>
    public GrenadeType DetonatedType { get; }
}

/// <summary>Full output of one simulation.</summary>
public sealed class TrajectoryResult
{
    public TrajectoryResult(IReadOnlyList<TrajectoryPoint> points, FinalImpact impact, bool isInZone)
    {
        Points = points;
        Impact = impact;
        IsInZone = isInZone;
    }

    public IReadOnlyList<TrajectoryPoint> Points { get; }

    public FinalImpact Impact { get; }

    /// <summary>True when the rest point satisfies the caller's zone test.</summary>
    public bool IsInZone { get; }

    /// <summary>Convenience: final resting position.</summary>
    public Vector3 EndPosition => Impact.Position;

    /// <summary>Total flight time in seconds.</summary>
    public float FlightTime => Impact.Time;

    /// <summary>Number of bounces recorded during flight.</summary>
    public int BounceCount => Impact.BounceCount;
}

/// <summary>Extra numbers produced by the diagnostic overload.</summary>
public readonly struct SimulationDiagnostics
{
    public SimulationDiagnostics(float apexHeight, float horizontalDistance, float pathLength, int stepsTaken, int substeps)
    {
        ApexHeight = apexHeight;
        HorizontalDistance = horizontalDistance;
        PathLength = pathLength;
        StepsTaken = stepsTaken;
        Substeps = substeps;
    }

    /// <summary>Highest Z reached during flight, in units above the release point.</summary>
    public float ApexHeight { get; }

    /// <summary>Planar distance from origin to rest point, in units.</summary>
    public float HorizontalDistance { get; }

    /// <summary>Total 3D path length including bounces, in units.</summary>
    public float PathLength { get; }

    /// <summary>Number of collision sweeps issued to the raycaster.</summary>
    public int StepsTaken { get; }

    /// <summary>Number of physics substeps integrated.</summary>
    public int Substeps { get; }
}

/// <summary>Simulation entry point consumed by the plugin shell.</summary>
public interface ITrajectorySimulator
{
    /// <summary>Simulates one throw, returning the sampled path plus the final impact.</summary>
    TrajectoryResult Simulate(ThrowParams parameters, IRaycaster raycaster);

    /// <summary>
    /// Simulates and additionally reports apex / range / path length, which is
    /// what <c>CalibrationRunner</c> fits against.
    /// </summary>
    TrajectoryResult SimulateWithDiagnostics(ThrowParams parameters, IRaycaster raycaster, out SimulationDiagnostics diagnostics);
}