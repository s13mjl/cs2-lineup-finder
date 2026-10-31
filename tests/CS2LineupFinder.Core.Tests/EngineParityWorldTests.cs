using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// A world that answers a hull sweep the way the engine does.
/// </summary>
/// <remarks>
/// The css_lf_probe rows of 2026-10-17 settled a question the old mock could not:
/// on a hull sweep the engine fills EndPos and HitPoint with the SAME surface
/// contact point, while Fraction already carries the hull inflation. The old mock
/// instead reported the swept origin directly (box grown by radius), which is why
/// the unit suite passed while every real throw landed 30-160u short.
/// </remarks>
public sealed class EngineParityWorld : IWorldGeometry
{
    private readonly float _floor;

    /// <summary>Creates a world whose only geometry is a horizontal floor.</summary>
    /// <param name="floor">Surface height.</param>
    public EngineParityWorld(float floor) => _floor = floor;

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
    {
        Vec3 dir = direction.Normalized();
        if (MathF.Abs(dir.Z) < 1e-6f)
        {
            return new TraceHit(false, origin + (dir * maxDistance), Vec3.UnitZ, 1f);
        }

        float t = (_floor - origin.Z) / dir.Z;
        if (t < 0f || t > maxDistance)
        {
            return new TraceHit(false, origin + (dir * maxDistance), Vec3.UnitZ, 1f);
        }

        Vec3 contact = origin + (dir * t);
        return new TraceHit(true, contact, Vec3.UnitZ, t / maxDistance);
    }

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
    {
        Vec3 dir = direction.Normalized();
        float len = maxDistance;

        if (radius <= 1e-6f)
        {
            return TraceRay(origin, direction, maxDistance, ignoreEntityIndex);
        }

        // A start already inside the hull-grown box is a zero-fraction contact,
        // exactly what the slab method in MockWorldGeometry reports: after a
       // bounce the simulator puts the hull centre one skin below the surface,
        // so the very next sweep starts penetrated and must not miss, or the
        // grenade falls out of the world instead of settling on the floor.
        if (origin.Z <= _floor + radius)
        {
            return new TraceHit(true, origin, Vec3.UnitZ, 0f);
        }

        if (dir.Z >= -1e-6f)
        {
            return new TraceHit(false, origin + (dir * len), Vec3.UnitZ, 1f);
        }

        // Engine semantics, measured by css_lf_probe on 2026-10-17: the reported
        // position is the point the swept-shape ORIGIN (the centre) stops at, and
        // the fraction already carries the hull inflation, so
        // start + dir * fraction * len == position exactly. The live probe row
        // start=(1272.211,-654.824,-155.969) end=(...-227.969) frac=0.112
        // endpos=(1272.211,-654.824,-164.037) proves it: start + frac*delta is
        // -164.033 against a printed -164.037, the 0.004u gap being the row's
        // three-decimal rounding. The surface point itself sits one radius
        // further along the normal, which is why the hull centre resting on this
        // floor reports surface - 1.968 in the telemetry.
        // The centre travels toward the plane offset radius along the surface
        // normal, not radius backward along the ray: the backward-along-ray
        // form is only correct for a vertical drop and sinks the centre
        // 2*(1-|dir.Z|) into the floor on oblique approaches (the 2026-10-19
        // engine-parity replay rested 0.33u low on exactly that error). Both
        // captures rest with the centre at surface + 1.9688 on horizontal
        // floors regardless of approach angle, and the live probe identity
        // start + frac*delta == endpos still holds.
        // The centre-plane crossing is the ONLY hit test: the old surface-plane
        // guard (t computed against _floor) ran first and vetoed hops where the
        // surface intersection lies beyond the hop while the centre crossing
        // lies inside it, so oblique approaches punched through and the
        // grenade only stopped via the start-inside branch below the plane.
        float stopDistance = (_floor + radius - origin.Z) / dir.Z;
        if (stopDistance <= 0f || stopDistance > len)
        {
            return new TraceHit(false, origin + (dir * len), Vec3.UnitZ, 1f);
        }

        return new TraceHit(true, origin + (dir * stopDistance), Vec3.UnitZ, stopDistance / len);
    }

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop) => TraceRay(position, new Vec3(0f, 0f, -1f), maxDrop);
}

/// <summary>
/// What the engine hull trace reports, and what the simulator must still do with it.
/// </summary>
public class EngineParityWorldTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class.</summary>
    /// <param name="output">xUnit output sink.</param>
    public EngineParityWorldTests(ITestOutputHelper output) => _output = output;

    private const float Floor = -167.9688f;

    // telemetry-001.csv t=0.0000 row.
    private static readonly Vec3 LaunchPos = new(17.7950f, -2046.3620f, 21.8836f);
    private static readonly Vec3 LaunchVel = new(-628.1757f, -241.1344f, -58.6144f);
    private static readonly Vec3 ActualImpact = new(-909.1882f, -2402.1820f, -165.9688f);

    /// <summary>
    /// A hull sweep reports the swept-shape origin (the centre) where it stopped,
    /// one radius short of the surface, and the fraction already carries that
    /// inflation - which is what the live css_lf_probe rows of 2026-10-17 show.
    /// The old version of this test asserted the opposite, that the hull reports
    /// the surface point, and that assumption is what made every unit test pass
    /// while real throws landed short.
    /// </summary>
    [Fact]
    public void HullSweep_ReportsTheSweptOrigin_WithTheFractionInflated()
    {
        EngineParityWorld world = new(Floor);
        Vec3 from = new Vec3(0f, 0f, Floor + 64f);
        Vec3 down = new Vec3(0f, 0f, -1f);

        TraceHit line = world.TraceRay(from, down, 256f);
        TraceHit hull = world.TraceSphere(from, down, 256f, 2f);

        Log(line.Position.Z, line.Fraction, hull.Position.Z, hull.Fraction);

        // The line has no hull, so its centre reaches the surface itself.
        Assert.True(MathF.Abs(line.Position.Z - Floor) <= 0.05f);

        // The hull stops the centre one radius short of that surface, and the
        // fraction is inflated by the same radius: start + dir*fraction*len is
        // exactly the reported position, which is the engine's own identity.
        Assert.True(MathF.Abs(hull.Position.Z - (Floor + 2f)) <= 0.05f);
        Assert.True(MathF.Abs(hull.Fraction * 256f - 62f) <= 0.05f);
        Vec3 implied = from + (down * (hull.Fraction * 256f));
        Assert.True((implied - hull.Position).Length <= 0.05f);
    }

    /// <summary>
    /// On a world that answers like the engine, the simulator still has to reproduce
    /// the recorded resting centre. The old mock passed this only because it reported
    /// the swept origin, which the engine does not.
    /// </summary>
    [Fact]
    public void Simulator_ReproducesRecordedRestingCentre_OnEngineSemantics()
    {
        EngineParityWorld world = new(Floor);

        TrajectoryResult result = new GrenadeSimulator(new PhysicsParameters()).Simulate(
            new ThrowParams(LaunchPos, LaunchVel, GrenadeType.Smoke, 1f, 64),
            world);

        for (int i = System.Math.Max(0, result.Points.Count - 8); i < result.Points.Count; i++)
        {
            TrajectoryPoint p = result.Points[i];
            _output.WriteLine("pt " + i + " t=" + p.Time.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
                + " p(" + p.Position.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + "," + p.Position.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + "," + p.Position.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + ") v(" + p.Velocity.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + "," + p.Velocity.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + "," + p.Velocity.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + ") b" + p.BounceCount);
        }

        _output.WriteLine(
            "recorded z " + ActualImpact.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + " | simulated z " + result.Impact.Position.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + " | err " + MathF.Abs(result.Impact.Position.Z - ActualImpact.Z).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(
            MathF.Abs(result.Impact.Position.Z - ActualImpact.Z) <= 0.25f,
            "expected " + ActualImpact.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + ", got " + result.Impact.Position.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Log(float lineZ, float lineFrac, float hullZ, float hullFrac)
    {
        _output.WriteLine(
            "surface z " + Floor.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + " | line z " + lineZ.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + " frac " + lineFrac.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + " | hull z " + hullZ.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            + " frac " + hullFrac.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
    }
}
