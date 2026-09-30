namespace CS2LineupFinder.Contracts;

/// <summary>
/// Engine independent 3D vector using Source units (1 unit = 1 inch).
/// Kept as a value type so simulation code never allocates per sample.
/// </summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    /// <summary>Creates a vector from its components.</summary>
    /// <param name="x">X component (east/west axis).</param>
    /// <param name="y">Y component (north/south axis).</param>
    /// <param name="z">Z component (up axis).</param>
    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>X component.</summary>
    public float X { get; }

    /// <summary>Y component.</summary>
    public float Y { get; }

    /// <summary>Z component.</summary>
    public float Z { get; }

    /// <summary>The all-zero vector.</summary>
    public static Vec3 Zero => new(0f, 0f, 0f);

    /// <summary>Unit vector along the +X axis.</summary>
    public static readonly Vec3 UnitX = new(1f, 0f, 0f);

    /// <summary>Unit vector along the +Y axis.</summary>
    public static readonly Vec3 UnitY = new(0f, 1f, 0f);

    /// <summary>Unit vector along the +Z axis, i.e. straight up.</summary>
    public static readonly Vec3 UnitZ = new(0f, 0f, 1f);

    /// <summary>Default tolerance used by <see cref="IsZero"/> and <see cref="Normalized"/>.</summary>
    public const float DefaultEpsilon = 1e-6f;

    /// <summary>Gets a component by index: 0 = X, 1 = Y, 2 = Z.</summary>
    /// <param name="index">Component index.</param>
    /// <returns>The requested component.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an index outside 0..2.</exception>
    public float this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Vec3 index must be 0, 1 or 2."),
    };

    /// <summary>Horizontal length (ignores Z).</summary>
    public float Length2D => MathF.Sqrt((X * X) + (Y * Y));

    /// <summary>Euclidean length.</summary>
    public float Length => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>Squared euclidean length, cheaper for comparisons.</summary>
    public float LengthSquared => (X * X) + (Y * Y) + (Z * Z);

    /// <summary>Adds two vectors component wise.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>The component wise sum.</returns>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Subtracts two vectors component wise.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>The component wise difference.</returns>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Scales a vector.</summary>
    /// <param name="a">Vector to scale.</param>
    /// <param name="scalar">Scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Vec3 operator *(Vec3 a, float scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);

    /// <summary>Scales a vector.</summary>
    /// <param name="scalar">Scale factor.</param>
    /// <param name="a">Vector to scale.</param>
    /// <returns>The scaled vector.</returns>
    public static Vec3 operator *(float scalar, Vec3 a) => a * scalar;

    /// <summary>Divides a vector by a scalar.</summary>
    /// <param name="a">Vector to divide.</param>
    /// <param name="scalar">Divisor, must not be zero.</param>
    /// <returns>The scaled vector.</returns>
    public static Vec3 operator /(Vec3 a, float scalar) => new(a.X / scalar, a.Y / scalar, a.Z / scalar);

    /// <summary>Negates a vector.</summary>
    /// <param name="a">Vector to negate.</param>
    /// <returns>The negated vector.</returns>
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    /// <summary>Component wise equality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when all components match exactly.</returns>
    public static bool operator ==(Vec3 left, Vec3 right) => left.Equals(right);

    /// <summary>Component wise inequality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when any component differs.</returns>
    public static bool operator !=(Vec3 left, Vec3 right) => !left.Equals(right);

    /// <summary>Dot product.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>The scalar dot product.</returns>
    public static float Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>Cross product.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>The vector cross product.</returns>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    /// <summary>Euclidean distance between two points.</summary>
    /// <param name="a">First point.</param>
    /// <param name="b">Second point.</param>
    /// <returns>The distance.</returns>
    public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

    /// <summary>Returns a unit length copy, or <see cref="Zero"/> for a degenerate vector.</summary>
    /// <param name="epsilon">Length at or below which the vector counts as degenerate.</param>
    /// <returns>The normalized vector.</returns>
    public Vec3 Normalized(float epsilon = DefaultEpsilon)
    {
        var length = Length;
        return length <= epsilon ? Zero : this / length;
    }

    /// <summary>Tests whether the vector is shorter than <paramref name="epsilon"/>.</summary>
    /// <param name="epsilon">Length at or below which the vector counts as zero.</param>
    /// <returns><see langword="true"/> when the vector is degenerate.</returns>
    public bool IsZero(float epsilon = DefaultEpsilon) => LengthSquared <= epsilon * epsilon;

    /// <summary>
    /// Reflects this vector about <paramref name="normal"/> and scales the result,
    /// which is the single bounce rule the grenade simulation uses.
    /// </summary>
    /// <param name="normal">Unit length surface normal of the contact, e.g. <see cref="UnitZ"/> for a flat floor.</param>
    /// <param name="restitution">Fraction of the velocity kept by the bounce, 0 stops dead, 1 keeps all energy.</param>
    /// <returns>The reflected and damped vector.</returns>
    public Vec3 Reflect(Vec3 normal, float restitution)
        => (this - (normal * (2f * Dot(this, normal)))) * restitution;

    /// <summary>Linear interpolation between two points.</summary>
    /// <param name="a">Start point.</param>
    /// <param name="b">End point.</param>
    /// <param name="t">Interpolation factor, 0 returns <paramref name="a"/>.</param>
    /// <returns>The interpolated point.</returns>
    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + ((b - a) * t);

    /// <inheritdoc />
    public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###}, {Z:0.###})");
}

/// <summary>Shape of the landing zone the grenade must land inside.</summary>
public enum GroundZoneType
{
    /// <summary>Circular zone defined by <see cref="GroundZone.Radius"/>.</summary>
    Circle = 0,

    /// <summary>Axis aligned rectangular zone defined by <see cref="GroundZone.Width"/> and <see cref="GroundZone.Height"/>.</summary>
    Rectangle = 1,
}

/// <summary>
/// The area on the ground the grenade is expected to come to rest (or detonate) inside.
/// All coordinates are in Source world units.
/// </summary>
public sealed record GroundZone
{
    /// <summary>Zone shape.</summary>
    public required GroundZoneType Type { get; init; }

    /// <summary>Center of the zone, projected onto the ground plane.</summary>
    public required Vec3 Center { get; init; }

    /// <summary>Radius in units, used when <see cref="Type"/> is <see cref="GroundZoneType.Circle"/>.</summary>
    public float Radius { get; init; }

    /// <summary>Extent along the local X axis, used when <see cref="Type"/> is <see cref="GroundZoneType.Rectangle"/>.</summary>
    public float Width { get; init; }

    /// <summary>Extent along the local Y axis, used when <see cref="Type"/> is <see cref="GroundZoneType.Rectangle"/>.</summary>
    public float Height { get; init; }

    /// <summary>In-plane rotation of a rectangular zone in degrees, 0 = world axis aligned.</summary>
    public float Yaw { get; init; }

    /// <summary>Extent along local X for <see cref="GroundZoneType.Rectangle"/>, otherwise <see cref="Radius"/>.</summary>
    public float HalfWidth => Type == GroundZoneType.Rectangle ? Width * 0.5f : Radius;

    /// <summary>Extent along local Y for <see cref="GroundZoneType.Rectangle"/>, otherwise <see cref="Radius"/>.</summary>
    public float HalfHeight => Type == GroundZoneType.Rectangle ? Height * 0.5f : Radius;

    /// <summary>Returns a copy of this zone re-centred on <paramref name="position"/>.</summary>
    /// <param name="position">New zone centre, typically a trace hit on the floor.</param>
    /// <returns>The re-centred zone.</returns>
    public GroundZone WithCenter(Vec3 position) => this with { Center = position };

    /// <summary>
    /// Tests whether a world point lies inside the zone. The shape is evaluated on the
    /// horizontal plane and Z is ignored, so a grenade resting on a raised surface still counts.
    /// </summary>
    /// <param name="point">Point to test, normally the grenade's rest position.</param>
    /// <returns><see langword="true"/> when the point is inside the zone.</returns>
    public bool Contains(Vec3 point)
    {
        var flatPoint = new Vec3(point.X, point.Y, 0f);
        var flatCenter = new Vec3(Center.X, Center.Y, 0f);

        if (Type == GroundZoneType.Circle)
        {
            return Vec3.Distance(flatPoint, flatCenter) <= Radius;
        }

        var offset = flatPoint - flatCenter;
        var radians = Yaw * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var localX = (offset.X * cos) + (offset.Y * sin);
        var localY = (-offset.X * sin) + (offset.Y * cos);
        return MathF.Abs(localX) <= Width * 0.5f && MathF.Abs(localY) <= Height * 0.5f;
    }
}

/// <summary>Result of a world ray trace.</summary>
public readonly record struct TraceHit
{
    /// <summary>Creates a trace result.</summary>
    /// <param name="didHit">Whether anything was hit.</param>
    /// <param name="position">Impact point in world space.</param>
    /// <param name="normal">Surface normal at the impact point.</param>
    /// <param name="fraction">Fraction of the trace distance that was travelled, 0..1.</param>
    /// <param name="entityIndex">Engine index of the hit entity, or -1 for world geometry.</param>
    /// <param name="className">Designer name of the hit entity, when available.</param>
    public TraceHit(bool didHit, Vec3 position, Vec3 normal, float fraction, int entityIndex = -1, string? className = null)
    {
        DidHit = didHit;
        Position = position;
        Normal = normal;
        Fraction = fraction;
        EntityIndex = entityIndex;
        ClassName = className;
    }

    /// <summary>Whether the trace hit anything.</summary>
    public bool DidHit { get; }

    /// <summary>Alias of <see cref="DidHit"/>, mirroring the engine trace API.</summary>
    public bool Hit => DidHit;

    /// <summary>Impact point in world space.</summary>
    public Vec3 Position { get; }

    /// <summary>Alias of <see cref="Position"/> for callers that read the impact as the sweep end point.</summary>
    public Vec3 EndPosition => Position;

    /// <summary>Surface normal at the impact point.</summary>
    public Vec3 Normal { get; }

    /// <summary>Alias of <see cref="Normal"/>, mirroring the engine trace API.</summary>
    public Vec3 PlaneNormal => Normal;

    /// <summary>Fraction of the requested distance that was travelled, 0..1.</summary>
    public float Fraction { get; }

    /// <summary>Engine index of the hit entity, or -1 when world geometry was hit.</summary>
    public int EntityIndex { get; }

    /// <summary>Designer name of the hit entity, when available.</summary>
    public string? ClassName { get; }
}

/// <summary>Deterministic constants shared by the forward and inverse simulation.</summary>
public sealed record SimulationEnvironment
{
    /// <summary>Downward acceleration in units per second squared, Source default is 800.</summary>
    public float Gravity { get; init; } = 800f;

    /// <summary>Simulation tick length in seconds, 1/64 on a 64 tick server.</summary>
    public float TickInterval { get; init; } = 1f / 64f;

    /// <summary>Hard cap on integration steps per simulated throw.</summary>
    public int MaxSteps { get; init; } = 4096;

    /// <summary>Air drag coefficient applied per tick, 0 disables drag.</summary>
    public float AirDrag { get; init; }
}
