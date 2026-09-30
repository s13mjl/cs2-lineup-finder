using CS2LineupFinder.Contracts;
using static System.Math;

namespace CS2LineupFinder.Math;

/// <summary>
/// Closed-form ballistics used to seed the inverse search. Everything here assumes a
/// vacuum: no air drag and no world geometry. That is deliberate - the analytic solution
/// only has to be good enough to put the coarse grid on the right bearing, the forward
/// simulator then decides whether the throw actually clears the map.
/// </summary>
/// <remarks>
/// Angles are <em>elevations</em>: positive points up. Engine pitch is the negation of
/// that, see <see cref="AngleMath.PitchFromElevation(double)"/>.
/// </remarks>
public static class Ballistics
{
    /// <summary>Horizontal distance travelled before the projectile returns to its release height.</summary>
    /// <param name="speed">Release speed in units per second.</param>
    /// <param name="elevationDegrees">Elevation above the horizon in degrees.</param>
    /// <param name="gravity">Downward acceleration in units per second squared.</param>
    /// <returns>Range in units, <c>R = v^2 * sin(2*theta) / g</c>.</returns>
    public static double LevelRange(double speed, double elevationDegrees, double gravity) =>
        (speed * speed * Sin(2d * elevationDegrees * AngleMath.Deg2Rad)) / gravity;

    /// <summary>Farthest a throw of this speed can reach on the release plane.</summary>
    /// <param name="speed">Release speed in units per second.</param>
    /// <param name="gravity">Downward acceleration in units per second squared.</param>
    /// <returns>Range in units at a 45 degree elevation.</returns>
    public static double MaxLevelRange(double speed, double gravity) => (speed * speed) / gravity;

    /// <summary>
    /// Farthest a throw can reach the plane <paramref name="deltaZ"/> above the release point.
    /// This is the envelope of the vacuum trajectories, i.e. the range at which
    /// <see cref="SolveElevations"/> starts returning no solution.
    /// </summary>
    /// <param name="speed">Release speed in units per second.</param>
    /// <param name="gravity">Downward acceleration in units per second squared.</param>
    /// <param name="deltaZ">Target height minus release height, units.</param>
    /// <returns>Maximum horizontal range in units, or 0 when the plane is above the ceiling.</returns>
    public static double MaxRange(double speed, double gravity, double deltaZ)
    {
        var under = (speed * speed) - (2d * gravity * deltaZ);
        return under <= 0d ? 0d : (speed / gravity) * Sqrt(under);
    }

    /// <summary>
    /// Solves the two vacuum elevations that carry a throw from the release point to a point
    /// at horizontal range <paramref name="range"/> and height difference <paramref name="deltaZ"/>.
    /// </summary>
    /// <param name="range">Horizontal distance to cover, units.</param>
    /// <param name="deltaZ">Target height minus release height, units, positive is above.</param>
    /// <param name="speed">Release speed in units per second.</param>
    /// <param name="gravity">Downward acceleration in units per second squared.</param>
    /// <param name="lowElevation">Receives the flat trajectory in degrees when one exists.</param>
    /// <param name="highElevation">Receives the lofted trajectory in degrees when one exists.</param>
    /// <returns>How many elevations were written: 0 when the point is out of reach, 1 on the
    /// range limit, 2 for the usual low and high pair.</returns>
    public static int SolveElevations(
        double range,
        double deltaZ,
        double speed,
        double gravity,
        out double lowElevation,
        out double highElevation)
    {
        lowElevation = double.NaN;
        highElevation = double.NaN;

        if (range <= 0d || speed <= 0d || gravity <= 0d)
        {
            return 0;
        }

        // tan(theta) = (v^2 +/- sqrt(v^4 - g * (g*R^2 + 2*dz*v^2))) / (g*R)
        // Written as multiplications rather than Pow(v, 4) so a target exactly on the
        // envelope evaluates to a discriminant of zero rather than a rounding artefact.
        var speedSquared = speed * speed;
        var discriminant = (speedSquared * speedSquared)
            - (gravity * ((gravity * range * range) + (2d * deltaZ * speedSquared)));
        if (discriminant < 0d)
        {
            return 0;
        }

        var root = Sqrt(discriminant);
        var denominator = gravity * range;
        var low = Atan((speedSquared - root) / denominator) * AngleMath.Rad2Deg;
        var high = Atan((speedSquared + root) / denominator) * AngleMath.Rad2Deg;

        if (root <= 1e-9)
        {
            lowElevation = low;
            return 1;
        }

        lowElevation = Min(low, high);
        highElevation = Max(low, high);
        return 2;
    }

    /// <summary>Time at which the descending projectile crosses a horizontal plane.</summary>
    /// <param name="elevationDegrees">Elevation above the horizon in degrees.</param>
    /// <param name="speed">Release speed in units per second.</param>
    /// <param name="gravity">Downward acceleration in units per second squared.</param>
    /// <param name="deltaZ">Plane height minus release height, units.</param>
    /// <returns>Seconds since release, or <see cref="double.NaN"/> when the plane is never crossed.</returns>
    public static double TimeToPlane(
        double elevationDegrees,
        double speed,
        double gravity,
        double deltaZ)
    {
        var radians = elevationDegrees * AngleMath.Deg2Rad;
        var vertical = speed * Sin(radians);
        var discriminant = (vertical * vertical) - (2d * gravity * deltaZ);
        if (discriminant < 0d)
        {
            return double.NaN;
        }

        return (vertical + Sqrt(discriminant)) / gravity;
    }

    /// <summary>
    /// Where an unobstructed throw crosses the plane <paramref name="targetZ"/>. This mirrors
    /// <see cref="SolveElevations"/> and is what the coarse grid and the unit tests reason about.
    /// </summary>
    /// <param name="origin">Release point in world units.</param>
    /// <param name="yawDegrees">Bearing in degrees.</param>
    /// <param name="elevationDegrees">Elevation above the horizon in degrees.</param>
    /// <param name="speed">Release speed in units per second.</param>
    /// <param name="gravity">Downward acceleration in units per second squared.</param>
    /// <param name="targetZ">Height of the plane the throw is measured against.</param>
    /// <returns>The predicted impact point, or <see cref="Vec3.Zero"/> when the plane is unreachable.</returns>
    public static Vec3 PredictImpact(
        Vec3 origin,
        double yawDegrees,
        double elevationDegrees,
        double speed,
        double gravity,
        double targetZ)
    {
        var time = TimeToPlane(elevationDegrees, speed, gravity, targetZ - origin.Z);
        if (double.IsNaN(time))
        {
            return Vec3.Zero;
        }

        var range = speed * Cos(elevationDegrees * AngleMath.Deg2Rad) * time;
        var radians = yawDegrees * AngleMath.Deg2Rad;
        return new Vec3(
            (float)(origin.X + (Cos(radians) * range)),
            (float)(origin.Y + (Sin(radians) * range)),
            (float)targetZ);
    }

    /// <summary>Picks the release speed a grenade profile stores for a button combination.</summary>
    /// <param name="profile">Grenade physics profile.</param>
    /// <param name="button">Mouse combination that released the throw.</param>
    /// <returns>Release speed in units per second.</returns>
    public static double SpeedFor(GrenadeProfile profile, ThrowButton button) => button switch
    {
        ThrowButton.Primary => profile.PrimaryThrowVelocity,
        ThrowButton.Secondary => profile.SecondaryThrowVelocity,
        ThrowButton.Both => profile.BothButtonsThrowVelocity,
        _ => profile.PrimaryThrowVelocity,
    };

    /// <summary>Effective downward acceleration for a profile inside an environment.</summary>
    /// <param name="environment">Gravity and integrator settings.</param>
    /// <param name="profile">Grenade physics profile, may be null to mean "engine default".</param>
    /// <returns>Gravity in units per second squared.</returns>
    public static double GravityFor(SimulationEnvironment environment, GrenadeProfile? profile)
    {
        var scale = profile?.GravityScale ?? 1d;
        return environment.Gravity * scale;
    }
}
