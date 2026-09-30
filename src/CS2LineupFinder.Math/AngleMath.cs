using CS2LineupFinder.Contracts;
using static System.Math;

namespace CS2LineupFinder.Math;

/// <summary>
/// Angle / direction conversions using the Source engine convention. The view basis is
/// <c>R = Rz(yaw) * Ry(pitch)</c> applied to the +X axis, which yields
/// <c>forward = (cos p * cos y, cos p * sin y, -sin p)</c>. Pitch is therefore a rotation
/// about +Y and yaw a rotation about +Z, and a negative pitch looks up.
/// </summary>
/// <remarks>
/// Trigonometry is imported with <c>using static</c> on purpose: the enclosing namespace
/// is named <c>CS2LineupFinder.Math</c>, so a bare <c>Math.Cos</c> would bind to the
/// namespace rather than to <c>System.Math</c>.
/// </remarks>
public static class AngleMath
{
    /// <summary>Degrees to radians.</summary>
    public const double Deg2Rad = PI / 180d;

    /// <summary>Radians to degrees.</summary>
    public const double Rad2Deg = 180d / PI;

    /// <summary>Wraps an angle to the half-open interval (-180, 180].</summary>
    /// <param name="degrees">Angle in degrees, any magnitude.</param>
    /// <returns>The wrapped angle.</returns>
    public static double NormalizeYaw(double degrees)
    {
        var wrapped = degrees % 360d;
        if (wrapped > 180d)
        {
            wrapped -= 360d;
        }
        else if (wrapped <= -180d)
        {
            wrapped += 360d;
        }

        return wrapped;
    }

    /// <summary>Clamps a pitch to the engine's +/-<see cref="QAngle.MaxPitch"/> range.</summary>
    /// <param name="degrees">Pitch in degrees.</param>
    /// <returns>The clamped pitch.</returns>
    public static double ClampPitch(double degrees) => Clamp(degrees, -QAngle.MaxPitch, QAngle.MaxPitch);

    /// <summary>
    /// Converts an engine pitch to a ballistic elevation. The two differ only in sign:
    /// the engine looks up with a negative pitch, while the ballistic formulae measure a
    /// positive elevation above the horizon.
    /// </summary>
    /// <param name="pitchDegrees">Engine pitch in degrees.</param>
    /// <returns>Elevation in degrees, positive points up.</returns>
    public static double ElevationFromPitch(double pitchDegrees) => -pitchDegrees;

    /// <summary>Converts a ballistic elevation back to an engine pitch. See <see cref="ElevationFromPitch"/>.</summary>
    /// <param name="elevationDegrees">Elevation in degrees, positive points up.</param>
    /// <returns>Engine pitch in degrees, negative points up.</returns>
    public static double PitchFromElevation(double elevationDegrees) => -elevationDegrees;

    /// <summary>Smallest signed difference from <paramref name="from"/> to <paramref name="to"/> on a circle.</summary>
    /// <param name="from">Start angle in degrees.</param>
    /// <param name="to">End angle in degrees.</param>
    /// <returns>The signed difference in degrees, -180..180.</returns>
    public static double AngleDifference(double from, double to) => NormalizeYaw(to - from);

    /// <summary>Builds the engine forward vector for a view angle.</summary>
    /// <param name="angle">View angle in degrees.</param>
    /// <returns>Unit length forward direction. Roll is ignored: it rotates about the forward axis.</returns>
    public static Vec3 ForwardFromAngle(QAngle angle)
    {
        var pitch = angle.Pitch * Deg2Rad;
        var yaw = angle.Yaw * Deg2Rad;
        var cp = Cos(pitch);
        var sp = Sin(pitch);
        return new Vec3(
            (float)(cp * Cos(yaw)),
            (float)(cp * Sin(yaw)),
            (float)(-sp));
    }

    /// <summary>Builds the engine right vector for a view angle.</summary>
    /// <param name="angle">View angle in degrees.</param>
    /// <returns>Unit length right direction, always horizontal.</returns>
    public static Vec3 RightFromAngle(QAngle angle)
    {
        var yaw = angle.Yaw * Deg2Rad;
        return new Vec3((float)(-Sin(yaw)), (float)Cos(yaw), 0f);
    }

    /// <summary>Builds the engine up vector for a view angle.</summary>
    /// <param name="angle">View angle in degrees.</param>
    /// <returns>Unit length up direction.</returns>
    public static Vec3 UpFromAngle(QAngle angle)
    {
        var pitch = angle.Pitch * Deg2Rad;
        var yaw = angle.Yaw * Deg2Rad;
        var cp = Cos(pitch);
        var sp = Sin(pitch);
        return new Vec3(
            (float)(Cos(yaw) * sp),
            (float)(Sin(yaw) * sp),
            (float)cp);
    }

    /// <summary>Resolves a throw direction from yaw and ballistic elevation.</summary>
    /// <param name="yawDegrees">Yaw in degrees.</param>
    /// <param name="elevationDegrees">Elevation above the horizon in degrees, positive points up.</param>
    /// <returns>Unit length forward direction.</returns>
    public static Vec3 DirectionFromElevation(double yawDegrees, double elevationDegrees) =>
        ForwardFromAngle(new QAngle(PitchFromElevation(elevationDegrees), yawDegrees));

    /// <summary>
    /// Recovers the view angle that looks along a direction, the equivalent of the engine's
    /// <c>VectorAngles</c>. The input does not need to be normalized; a degenerate vector
    /// yields <see cref="QAngle.Zero"/>.
    /// </summary>
    /// <param name="direction">Direction vector in world units.</param>
    /// <returns>View angle with roll set to zero.</returns>
    public static QAngle AngleFromDirection(Vec3 direction)
    {
        var x = (double)direction.X;
        var y = (double)direction.Y;
        var z = (double)direction.Z;
        var horizontal = Sqrt((x * x) + (y * y));
        if (horizontal < 1e-12 && Abs(z) < 1e-12)
        {
            return QAngle.Zero;
        }

        return new QAngle(Atan2(-z, horizontal) * Rad2Deg, Atan2(y, x) * Rad2Deg);
    }

    /// <summary>Recovers the view angle that points from one world position to another.</summary>
    /// <param name="from">Observer position.</param>
    /// <param name="to">Observed position.</param>
    /// <returns>View angle with roll set to zero.</returns>
    public static QAngle AngleTo(Vec3 from, Vec3 to) => AngleFromDirection(to - from);
}
