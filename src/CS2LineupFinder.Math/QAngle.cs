using System;
using System.Globalization;

namespace CS2LineupFinder.Math;

/// <summary>
/// Source engine view angle in degrees. The engine applies <see cref="Pitch"/> as a
/// rotation about the +Y axis and <see cref="Yaw"/> as a rotation about the +Z axis,
/// so a negative pitch looks up and a positive pitch looks down.
/// </summary>
public readonly struct QAngle : IEquatable<QAngle>
{
    /// <summary>Pitch values are clamped to this magnitude so the basis stays non-degenerate.</summary>
    public const double MaxPitch = 89.0;

    /// <summary>Creates a view angle.</summary>
    /// <param name="pitch">Pitch in degrees, negative looks up.</param>
    /// <param name="yaw">Yaw in degrees.</param>
    /// <param name="roll">Roll in degrees, unused by the solver.</param>
    public QAngle(double pitch, double yaw, double roll = 0d)
    {
        Pitch = pitch;
        Yaw = yaw;
        Roll = roll;
    }

    /// <summary>Pitch in degrees, negative looks up.</summary>
    public double Pitch { get; }

    /// <summary>Yaw in degrees.</summary>
    public double Yaw { get; }

    /// <summary>Roll in degrees.</summary>
    public double Roll { get; }

    /// <summary>The zero angle, looking due +X at the horizon.</summary>
    public static QAngle Zero => new(0d, 0d, 0d);

    /// <summary>Wraps yaw and roll to -180..180 and clamps pitch to +/-<see cref="MaxPitch"/>.</summary>
    /// <returns>A canonical copy of this angle.</returns>
    public QAngle Normalized() =>
        new(AngleMath.ClampPitch(Pitch), AngleMath.NormalizeYaw(Yaw), AngleMath.NormalizeYaw(Roll));

    /// <inheritdoc />
    public bool Equals(QAngle other) =>
        Pitch.Equals(other.Pitch) && Yaw.Equals(other.Yaw) && Roll.Equals(other.Roll);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QAngle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Pitch, Yaw, Roll);

    /// <inheritdoc />
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "pitch {0:0.##} yaw {1:0.##}",
        Pitch,
        Yaw);
}
