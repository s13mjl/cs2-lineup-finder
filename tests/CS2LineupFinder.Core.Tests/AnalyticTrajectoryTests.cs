// <copyright file="AnalyticTrajectoryTests.cs" company="CS2LineupFinder">
// Acceptance tests 1-3: the collision-free and single-bounce cases, checked
// against closed-form projectile solutions.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>Closed-form reference solutions used as ground truth.</summary>
internal static class Analytic
{
    /// <summary>
    /// Ideal projectile position at time <paramref name="t"/>:
    /// x = x0 + vx t, y = y0 + vy t, z = z0 + vz t - g t^2 / 2.
    /// </summary>
    public static Vec3 Projectile(Vec3 origin, Vec3 velocity, float gravity, float t)
        => new(
            origin.X + (velocity.X * t),
            origin.Y + (velocity.Y * t),
            origin.Z + (velocity.Z * t) - (0.5f * gravity * t * t));

    /// <summary>
    /// Flat-ground range of a projectile launched at <paramref name="speed"/>
    /// and <paramref name="elevationDegrees"/>, landing <paramref name="drop"/>
    /// units below the launch point. With no drag:
    /// theta = 2 v^2 sin(theta) cos(theta) / g, adjusted for the height drop.
    /// </summary>
    public static float FlatRange(float speed, float elevationDegrees, float gravity, float drop)
    {
        float theta = elevationDegrees * MathF.PI / 180f;
        float vx = speed * MathF.Cos(theta);
        float vz = speed * MathF.Sin(theta);

        // Solve z0 + vz t - g t^2 / 2 = -drop for the positive root.
        float t = (vz + MathF.Sqrt((vz * vz) + (2f * gravity * drop))) / gravity;
        return vx * t;
    }
}

public class AnalyticTrajectoryTests
{
    private const float Tolerance = 0.5f;

    /// <summary>
    /// Acceptance 1: with no geometry, every recorded sample must match the
    /// closed-form projectile solution to within 0.5 units.
    /// </summary>
    [Fact]
    public void FreeFlight_MatchesAnalyticSolution_WithinHalfUnit()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);

        Vec3 origin = new(100f, -250f, 64f);
        Vec3 velocity = new(600f, 120f, 300f);

        // No floor: the grenade is still airborne when the budget runs out.
        ThrowParams input = new(origin, velocity, GrenadeType.He, 1f, 64);
        MockWorldGeometry world = MockWorldGeometry.Empty();

        TrajectoryResult result = simulator.Simulate(input, world);

        Assert.NotEmpty(result.Points);

        float worst = 0f;
        foreach (TrajectoryPoint point in result.Points)
        {
            Vec3 expected = Analytic.Projectile(origin, velocity, parameters.GrenadeGravity.Value, point.Time);
            worst = MathF.Max(worst, (expected - point.Position).Length);
        }

        Assert.True(worst < Tolerance,
            $"Worst free-flight deviation was {worst:0.####} units, tolerance {Tolerance}. " +
            "Semi-implicit Euler should agree with the analytic arc to O(dt^2).");
    }

    /// <summary>
    /// Acceptance 3: a 90 degree vertical throw must return to its release point
    /// to within 1 unit. Launch and landing are at the same height, so the
    /// closed-form return time is 2 vz / g.
    /// </summary>
    [Fact]
    public void VerticalThrow_LandsBackAtOrigin_WithinOneUnit()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);

        // Use a long fuse so the grenade is still in the air at apex and only
        // stops on reaching the floor, not because its fuse expired.
        parameters.FuseSeconds.Value = 20f;

        const float floorHeight = 0f;
        Vec3 origin = new(512f, 384f, floorHeight + 64f);

        // Straight up, full power.
        float speed = parameters.FullThrowSpeed.Value;
        ThrowParams input = new(origin, new Vec3(0f, 0f, speed), GrenadeType.He, 1f, 64);
        MockWorldGeometry world = MockWorldGeometry.Empty().AddInfiniteFloor(floorHeight);

        TrajectoryResult result = simulator.Simulate(input, world);

        // The grenade has no horizontal velocity, so the landing point must sit
        // directly below the release point.
        float horizontalDrift = new Vec3(
            result.Impact.Position.X - origin.X,
            result.Impact.Position.Y - origin.Y,
            0f).Length;

        Assert.True(horizontalDrift < 1f,
            $"Vertical throw drifted {horizontalDrift:0.####} units horizontally, tolerance 1.");

        // And it must not have dropped below the floor.
        Assert.True(result.Impact.Position.Z >= floorHeight - 0.5f,
            $"Projectile sank to z={result.Impact.Position.Z:0.###}, below the floor at {floorHeight}.");

        // The apex should match v^2 / 2g, confirming gravity is being applied
        // with the calibrated value.
        simulator.SimulateWithDiagnostics(input, world, out SimulationDiagnostics diagnostics);
        float expectedApex = (speed * speed) / (2f * parameters.GrenadeGravity.Value);
        Assert.True(MathF.Abs(diagnostics.ApexHeight - expectedApex) < 2f,
            $"Apex {diagnostics.ApexHeight:0.##} vs analytic {expectedApex:0.##}.");
    }

    /// <summary>
    /// Acceptance 2: a 45 degree full-power HE on flat ground must match the
    /// theoretical range to within 5 percent.
    /// </summary>
    [Fact]
    public void FullThrowAt45Degrees_MatchesTheoreticalRange_Within5Percent()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);

        // A real HE fuse is 1.5 s, which expires well before this arc lands, so
        // the fuse is extended here. Without it the grenade detonates in mid air
        // and there is no landing to measure.
        parameters.FuseSeconds.Value = 20f;

        const float floorHeight = 0f;
        const float eyeHeight = 64f;
        Vec3 origin = new(0f, 0f, floorHeight + eyeHeight);

        // 45 degrees up and forward, at the full-power tier.
        float speed = parameters.FullThrowSpeed.Value;
        const float elevation = 45f;
        float radians = elevation * MathF.PI / 180f;

        // Note: the release origin already sits 64 units up, so the grenade has
        // to fall to the floor, which the analytic range accounts for.
        Vec3 velocity = new(
            speed * MathF.Cos(radians),
            0f,
            speed * MathF.Sin(radians));

        ThrowParams input = new(origin, velocity, GrenadeType.He, 1f, 64);
        MockWorldGeometry world = MockWorldGeometry.Empty().AddInfiniteFloor(floorHeight);

        TrajectoryResult result = simulator.Simulate(input, world);

        // Range is the FIRST ground contact, not the final rest point. After
        // landing the grenade still bounces and rolls forward, so the rest
        // position overshoots the ballistic range by a wide margin.
        float measured = FirstLandingX(result);
        float expected = Analytic.FlatRange(speed, elevation, parameters.GrenadeGravity.Value, eyeHeight);

        float errorPercent = MathF.Abs(measured - expected) / expected * 100f;

        Assert.True(errorPercent < 5f,
            $"Measured range {measured:0.##} vs theoretical {expected:0.##} " +
            $"({errorPercent:0.##}% error, tolerance 5%).");
    }
    /// <summary>
    /// X of the first sample that reaches the floor, found by interpolating
    /// between the bracketing samples. The final rest point cannot be used
    /// because the grenade keeps bouncing and rolling after it lands.
    /// </summary>
    /// <summary>
    /// X of the FIRST ground contact, which is the ballistic range.
    /// </summary>
    /// <remarks>
    /// The final rest point cannot be used, because after landing the grenade
    /// still bounces and rolls forward, overshooting the ballistic range by a
    /// wide margin. Likewise a "z &lt;= floorHeight" test is wrong: the sample
    /// taken just after a contact is nudged slightly back along the normal, so
    /// it sits just ABOVE the floor and the next landing is picked up instead.
    /// The first recorded sample carrying a bounce index is the true first impact.
    /// </remarks>
    private static float FirstLandingX(TrajectoryResult result)
    {
        foreach (TrajectoryPoint point in result.Points)
        {
            if (point.BounceCount >= 1)
            {
                return point.Position.X;
            }
        }

        // Never made contact within the budget; fall back to the last sample.
        return result.Points[^1].Position.X;
    }
}
