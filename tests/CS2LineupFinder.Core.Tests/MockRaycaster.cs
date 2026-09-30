// <copyright file="MockRaycaster.cs" company="CS2LineupFinder">
// Test doubles for IRaycaster. Deliberately map-free: every world is built from
// analytic primitives so the suite runs with no .bsp and no game install.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// An axis-aligned box world. Any sweep that enters a box stops at the box face
/// nearest the entry point, which is what the engine reports for a plane hit.
/// </summary>
public sealed class MockRaycaster : IRaycaster
{
    private readonly List<Box> _boxes = new();

    /// <summary>Every sweep issued, for assertions on the adaptive step behaviour.</summary>
    public int TraceCount { get; private set; }

    /// <summary>Longest sweep requested, in units. Used to check the step ramp.</summary>
    public float LongestTrace { get; private set; }

    /// <summary>Shortest sweep requested, in units.</summary>
    public float ShortestTrace { get; private set; } = float.MaxValue;

    /// <summary>Adds a solid box. Contacts on it count as world geometry.</summary>
    public MockRaycaster AddBox(Vector3 min, Vector3 max)
    {
        _boxes.Add(new Box(min, max));
        return this;
    }

    /// <summary>Adds a solid box from a centre and half-extents.</summary>
    public MockRaycaster AddBoxCentered(Vector3 center, Vector3 halfExtents)
        => AddBox(center - halfExtents, center + halfExtents);

    /// <summary>
    /// Adds a horizontal floor at <paramref name="height"/> spanning the whole
    /// world, so the simulator lands on it with a +Z normal.
    /// </summary>
    public MockRaycaster AddInfiniteFloor(float height)
    {
        _boxes.Add(new Box(
            new Vector3(-1e7f, -1e7f, height - 4096f),
            new Vector3(1e7f, 1e7f, height)));
        return this;
    }

    /// <summary>A world with no geometry at all: every sweep misses.</summary>
    public static MockRaycaster Empty() => new();

    /// <inheritdoc />
    public TraceResult TraceRay(Vector3 start, Vector3 end, TraceMask mask)
    {
        Vector3 delta = end - start;
        float distance = delta.Length;

        TraceCount++;
        if (distance > LongestTrace)
        {
            LongestTrace = distance;
        }

        if (distance < ShortestTrace)
        {
            ShortestTrace = distance;
        }

        if (distance <= 1e-6f)
        {
            return TraceResult.Miss(start, end);
        }

        Vector3 dir = delta / distance;
        float bestFraction = float.MaxValue;
        Vector3 bestNormal = Vector3.Zero;
        TraceMask bestContents = TraceMask.None;

        foreach (Box box in _boxes)
        {
            if (!box.Intersect(start, dir, distance, out float fraction, out Vector3 normal))
            {
                continue;
            }

            if (fraction < bestFraction)
            {
                bestFraction = fraction;
                bestNormal = normal;
                bestContents = TraceMask.Solid;
            }
        }

        if (bestFraction == float.MaxValue)
        {
            return TraceResult.Miss(start, end);
        }

        // Back off a hair so the contact is reported just short of the face.
        Vector3 contact = start + (dir * MathF.Max(0f, (bestFraction * distance) - 0.01f));

        return new TraceResult(true, start, contact, bestNormal, bestFraction, bestContents, isWorldGeometry: true);
    }

    /// <inheritdoc />
    public TraceResult TraceHull(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, TraceMask mask)
        => TraceRay(start, end, mask);
}

/// <summary>Slab-method AABB, reporting the face normal of the entry point.</summary>
internal readonly struct Box
{
    private readonly Vector3 _min;
    private readonly Vector3 _max;

    public Box(Vector3 min, Vector3 max)
    {
        _min = min;
        _max = max;
    }

    public bool Intersect(Vector3 origin, Vector3 dir, float maxDistance, out float fraction, out Vector3 normal)
    {
        fraction = 1f;
        normal = Vector3.Zero;

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
                // Parallel to this slab: either always inside or never.
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
            0 => new Vector3(enterSign, 0f, 0f),
            1 => new Vector3(0f, enterSign, 0f),
            _ => new Vector3(0f, 0f, enterSign),
        };

        return true;
    }

    private static float Component(Vector3 v, int axis)
        => axis switch
        {
            0 => v.X,
            1 => v.Y,
            _ => v.Z,
        };
}
