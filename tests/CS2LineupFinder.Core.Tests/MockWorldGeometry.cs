// <copyright file="MockWorldGeometry.cs" company="CS2LineupFinder">
// Test double for IWorldGeometry. Deliberately map-free: every world is built
// from analytic primitives so the suite runs with no .bsp and no game install.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// An axis-aligned box world. Any ray that enters a box stops at the box face
/// nearest the entry point, which is what the engine reports for a plane hit.
/// </summary>
public sealed class MockWorldGeometry : IWorldGeometry
{
    private readonly List<Box> _boxes = new();

    /// <summary>Every trace issued, for assertions on the adaptive step behaviour.</summary>
    public int TraceCount { get; private set; }

    /// <summary>Longest trace requested, in units.</summary>
    public float LongestTrace { get; private set; }

    /// <summary>Shortest trace requested, in units.</summary>
    public float ShortestTrace { get; private set; } = float.MaxValue;

    /// <summary>Adds a solid box. Contacts on it report entity index -1 (world).</summary>
    public MockWorldGeometry AddBox(Vec3 min, Vec3 max)
    {
        _boxes.Add(new Box(min, max));
        return this;
    }

    /// <summary>Adds a horizontal floor at the given Z spanning the whole world.</summary>
    public MockWorldGeometry AddInfiniteFloor(float height)
    {
        _boxes.Add(new Box(
            new Vec3(-1e7f, -1e7f, height - 4096f),
            new Vec3(1e7f, 1e7f, height)));
        return this;
    }

    /// <summary>A world with no geometry at all: every trace misses.</summary>
    public static MockWorldGeometry Empty() => new();

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
    {
        TraceCount++;
        if (maxDistance > LongestTrace)
        {
            LongestTrace = maxDistance;
        }

        if (maxDistance < ShortestTrace)
        {
            ShortestTrace = maxDistance;
        }

        if (maxDistance <= 1e-6f)
        {
            return new TraceHit(false, origin, Vec3.Zero, 1f);
        }

        Vec3 dir = direction.Normalized();
        float bestFraction = float.MaxValue;
        Vec3 bestNormal = Vec3.Zero;

        foreach (Box box in _boxes)
        {
            if (!box.Intersect(origin, dir, maxDistance, out float fraction, out Vec3 normal))
            {
                continue;
            }

            if (fraction < bestFraction)
            {
                bestFraction = fraction;
                bestNormal = normal;
            }
        }

        if (bestFraction == float.MaxValue)
        {
            return new TraceHit(false, origin, Vec3.Zero, 1f);
        }

        // Back off a hair so the contact is reported just short of the face.
        Vec3 contact = origin + (dir * MathF.Max(0f, (bestFraction * maxDistance) - 0.01f));
        return new TraceHit(true, contact, bestNormal, bestFraction, -1);
    }

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
        => TraceRay(origin, direction, maxDistance, ignoreEntityIndex);

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop)
    {
        float drop = position.Z + maxDrop;
        TraceHit hit = TraceRay(position, new Vec3(0f, 0f, -1f), maxDrop);
        _ = drop;
        return hit;
    }
}

/// <summary>Slab-method AABB, reporting the face normal of the entry point.</summary>
internal readonly struct Box
{
    private readonly Vec3 _min;
    private readonly Vec3 _max;

    public Box(Vec3 min, Vec3 max)
    {
        _min = min;
        _max = max;
    }

    public bool Intersect(Vec3 origin, Vec3 dir, float maxDistance, out float fraction, out Vec3 normal)
    {
        fraction = 1f;
        normal = Vec3.Zero;

        float tMin = 0f;
        float tMax = maxDistance;
        int enterAxis = -1;
        int enterSign = 0;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = Component(origin, axis);
            float d = Component(dir, axis);
            float lo = Component(_min, axis);
            float hi = Component(_max, axis);

            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi)
                {
                    return false;
                }

                continue;
            }

            float inv = 1f / d;
            float t1 = (lo - o) * inv;
            float t2 = (hi - o) * inv;
            int sign = -1;

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
                sign = 1;
            }

            if (t1 > tMin)
            {
                tMin = t1;
                enterAxis = axis;
                enterSign = sign;
            }

            if (t2 < tMax)
            {
                tMax = t2;
            }

            if (tMin > tMax)
            {
                return false;
            }
        }

        // Starting inside the box is reported as a miss so a resting grenade
        // does not immediately re-collide with the floor it is sitting on.
        if (enterAxis < 0)
        {
            return false;
        }

        fraction = tMin / maxDistance;
        normal = enterAxis switch
        {
            0 => new Vec3(enterSign, 0f, 0f),
            1 => new Vec3(0f, enterSign, 0f),
            _ => new Vec3(0f, 0f, enterSign),
        };

        return true;
    }

    private static float Component(Vec3 v, int axis)
        => axis switch
        {
            0 => v.X,
            1 => v.Y,
            _ => v.Z,
        };
}
