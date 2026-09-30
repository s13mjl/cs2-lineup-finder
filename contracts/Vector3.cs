// <copyright file="Vector3.cs" company="CS2LineupFinder">
// Shared contract primitive. Owned by contracts/ so that Core does not need to
// reference src/CS2LineupFinder.Math/ (which is owned by another agent branch).
// When CS2LineupFinder.Math ships its own Vector3, delete this file and repoint
// the `using` in the Core project.
// </copyright>

using System;
using System.Globalization;

namespace CS2LineupFinder.Contracts;

/// <summary>
/// Immutable 3-component vector in Source-engine world units (1 unit = 1 inch,
/// CS2 player eye height 64 units). Right-handed, +X forward, +Y left, +Z up.
/// </summary>
public readonly struct Vector3 : IEquatable<Vector3>
{
    public static readonly Vector3 Zero = new(0f, 0f, 0f);
    public static readonly Vector3 UnitX = new(1f, 0f, 0f);
    public static readonly Vector3 UnitY = new(0f, 1f, 0f);
    public static readonly Vector3 UnitZ = new(0f, 0f, 1f);

    public Vector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3 operator -(Vector3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vector3 operator *(float s, Vector3 a) => a * s;

    public static Vector3 operator /(Vector3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);

    public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);

    /// <summary>Euclidean length, in world units.</summary>
    public float Length => (float)Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>Squared length. Prefer this when only comparing against a threshold.</summary>
    public float LengthSquared => (X * X) + (Y * Y) + (Z * Z);

    public static float Dot(Vector3 a, Vector3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vector3 Cross(Vector3 a, Vector3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    /// <summary>
    /// Returns this vector scaled to unit length, or <see cref="Zero"/> when the
    /// input is shorter than <paramref name="epsilon"/>.
    /// </summary>
    public Vector3 Normalized(float epsilon = 1e-6f)
    {
        float len = Length;
        return len <= epsilon ? Zero : this / len;
    }

    public bool IsZero(float epsilon = 1e-6f) => LengthSquared <= epsilon * epsilon;

    /// <summary>
    /// Reflects this velocity about <paramref name="normal"/> and scales the
    /// result by <paramref name="restitution"/>. This is the single bounce rule
    /// used by the simulator; see docs/PHYSICS.md.
    /// </summary>
    public Vector3 Reflect(Vector3 normal, float restitution)
        => (this - (normal * (2f * Dot(this, normal)))) * restitution;

    public bool Equals(Vector3 other)
        => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is Vector3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
}