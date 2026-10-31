// <copyright file="CalibrationWorld.cs" company="CS2LineupFinder">
// Analytic world used only by the calibration harness.
// </copyright>

using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core.Calibration;

/// <summary>
/// A minimal collision world for offline calibration: either nothing at all, or
/// a single infinite horizontal floor.
///
/// The simulator itself knows nothing about this type. It exists so that a set
/// of measured open-field throws can be fitted without shipping any map
/// geometry, which keeps the calibration tool runnable in CI. Lineup work on
/// real maps still goes through the plugin shell's engine-backed
/// <see cref="IWorldGeometry"/>.
/// </summary>
public sealed class CalibrationWorld : IWorldGeometry
{
    private readonly float? _floorHeight;

    private CalibrationWorld(float? floorHeight)
    {
        _floorHeight = floorHeight;
    }

    /// <summary>A world with no geometry: every trace misses.</summary>
    public static CalibrationWorld Empty() => new(null);

    /// <summary>A world containing one infinite floor at the given Z.</summary>
    public static CalibrationWorld WithFloor(float height) => new(height);

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
    {
        if (_floorHeight is null)
        {
            return new TraceHit(false, origin, Vec3.Zero, 1f);
        }

        return TraceFloor(origin, direction, maxDistance);
    }

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
    {
        if (_floorHeight is null)
        {
            return new TraceHit(false, origin, Vec3.Zero, 1f);
        }

        // The projectile hull is small relative to flight distances, so treating
        // the sphere as a ray is accurate to well under a unit.
        return TraceFloor(origin, direction, maxDistance);
    }

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop)
    {
        if (_floorHeight is null)
        {
            return new TraceHit(false, position, Vec3.Zero, 1f);
        }

        float floor = _floorHeight.Value;
        float drop = position.Z - floor;

        if (drop < 0f)
        {
            return new TraceHit(false, position, Vec3.Zero, 1f);
        }

        float travelled = MathF.Min(drop, maxDrop);
        return new TraceHit(true, new Vec3(position.X, position.Y, floor), new Vec3(0f, 0f, 1f), drop <= 0f ? 1f : travelled / drop);
    }

    /// <summary>Intersects a ray with the infinite floor plane.</summary>
    private TraceHit TraceFloor(Vec3 origin, Vec3 direction, float maxDistance)
    {
        float floor = _floorHeight!.Value;
        // The simulator contract hands over a NORMALIZED direction plus a
        // separate maxDistance, so distances along the ray are plain lengths.
        // The old form divided by the raw z component and compared against 1,
        // which is the fraction convention of a different call shape and made
        // every sweep shorter than the remaining drop miss the floor entirely.
        Vec3 dir = direction.Normalized();
        float dz = dir.Z;

        // Only a downward sweep can reach a horizontal floor.
        if (dz > -1e-6f || origin.Z <= floor)
        {
            return new TraceHit(false, origin, Vec3.Zero, 1f);
        }

        float distance = (floor - origin.Z) / dz;

        // The floor is only reached within this sweep when the contact lands
        // inside [0, maxDistance].
        if (distance < 0f || distance > maxDistance)
        {
            return new TraceHit(false, origin, Vec3.Zero, 1f);
        }

        Vec3 position = origin + (dir * distance);
        position = new Vec3(position.X, position.Y, floor);

        return new TraceHit(true, position, new Vec3(0f, 0f, 1f), distance / maxDistance);
    }
}
