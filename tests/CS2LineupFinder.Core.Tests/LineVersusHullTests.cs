// <copyright file="LineVersusHullTests.cs" company="CS2LineupFinder">
// Quantifies how much of a replay error comes from the swept shape alone, by
// replaying the same launch state on the same world twice: once with the hull
// sweep the source now issues, once with the line sweep the deployed Core.dll
// still issues (deployed_core.txt: the main Sweep calls world.TraceRay and
// ProjectileHullRadius has zero hits).
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// An <see cref="IWorldGeometry"/> wrapper that answers sphere sweeps with line
/// traces, reproducing exactly what the deployed build does. Everything else is
/// forwarded untouched, so the only variable between the two runs of a replay is
/// the swept shape.
/// </summary>
public sealed class LineOnlyWorldGeometry : IWorldGeometry
{
    private readonly IWorldGeometry _inner;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">World to forward line traces to.</param>
    public LineOnlyWorldGeometry(IWorldGeometry inner) => _inner = inner;

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
        => _inner.TraceRay(origin, direction, maxDistance, ignoreEntityIndex);

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
        => _inner.TraceRay(origin, direction, maxDistance, ignoreEntityIndex);

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop)
        => _inner.ProbeGround(position, maxDrop);
}

public class LineVersusHullTests
{
    private readonly ITestOutputHelper _output;

    public LineVersusHullTests(ITestOutputHelper output) => _output = output;

    // telemetry-001 launch state and the floors that capture itself proves.
    private static readonly Vec3 LaunchPos = new(17.7950f, -2046.3620f, 21.8836f);
    private static readonly Vec3 LaunchVel = new(-628.1757f, -241.1344f, -58.6144f);
    private static readonly Vec3 ActualImpact = new(-909.1882f, -2402.1820f, -165.9688f);
    private static readonly float[] Floors = { -179.97f, -171.97f, -167.97f };

    private static MockWorldGeometry BuildWorld()
    {
        MockWorldGeometry world = new();
        foreach (float z in Floors)
        {
            world.AddInfiniteFloor(z);
        }

        return world;
    }

    private static TrajectoryResult Run(IWorldGeometry world)
        => new GrenadeSimulator(new PhysicsParameters()).Simulate(
            new ThrowParams(LaunchPos, LaunchVel, GrenadeType.Smoke, 1f, 64),
            world);

    /// <summary>
    /// The two runs share the launch state and the world, so any gap between
    /// their landings is caused by the swept shape and nothing else. Reports
    /// both errors against the recorded impact, and the gap between them.
    /// </summary>
    [Fact]
    public void SweptShape_IsTheOnlyVariableBetweenTheTwoRuns()
    {
        TrajectoryResult hull = Run(BuildWorld());
        TrajectoryResult line = Run(new LineOnlyWorldGeometry(BuildWorld()));

        Vec3 hullError = hull.Impact.Position - ActualImpact;
        Vec3 lineError = line.Impact.Position - ActualImpact;
        Vec3 gap = line.Impact.Position - hull.Impact.Position;

        _output.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"hull : ({hull.Impact.Position.X:0.##},{hull.Impact.Position.Y:0.##},{hull.Impact.Position.Z:0.##}) " +
            $"err {hullError.Length:0.##}u bounces {hull.BounceCount} flight {hull.FlightTime:0.000}s"));
        _output.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"line : ({line.Impact.Position.X:0.##},{line.Impact.Position.Y:0.##},{line.Impact.Position.Z:0.##}) " +
            $"err {lineError.Length:0.##}u bounces {line.BounceCount} flight {line.FlightTime:0.000}s"));
        _output.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"gap  : ({gap.X:0.##},{gap.Y:0.##},{gap.Z:0.##}) length {gap.Length:0.##}u"));

        // The line run must sit below the hull run: a point reaches a plane only
        // once the centre has crossed it, so it always penetrates further.
        Assert.True(line.Impact.Position.Z < hull.Impact.Position.Z, "line sweep rests below the hull sweep");

        // The two are genuinely different trajectories, not the same one twice.
        Assert.True(gap.Length > 0.1f, "the swept shape changes the landing");
    }

    /// <summary>
    /// Repeats the comparison for every launch speed in the sweep so the gap is
    /// a distribution rather than one number, and prints the horizontal part on
    /// its own because that is the part a player feels as a miss.
    /// </summary>
    [Fact]
    public void SweptShape_GapAcrossLaunchSpeeds()
    {
        float[] speeds = { 400f, 500f, 600f, 675.4f, 750f, 850f };

        foreach (float speed in speeds)
        {
            Vec3 direction = LaunchVel.Normalized();
            Vec3 velocity = direction * speed;

            TrajectoryResult hull = new GrenadeSimulator(new PhysicsParameters()).Simulate(
                new ThrowParams(LaunchPos, velocity, GrenadeType.Smoke, 1f, 64),
                BuildWorld());
            TrajectoryResult line = new GrenadeSimulator(new PhysicsParameters()).Simulate(
                new ThrowParams(LaunchPos, velocity, GrenadeType.Smoke, 1f, 64),
                new LineOnlyWorldGeometry(BuildWorld()));

            Vec3 gap = line.Impact.Position - hull.Impact.Position;
            Vec3 flat = new Vec3(gap.X, gap.Y, 0f);

            _output.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"speed {speed,6:0}: gap {gap.Length,7:0.##}u (flat {flat.Length,7:0.##}u, dz {gap.Z,6:0.##}) " +
                $"hull err {Vec3.Distance(hull.Impact.Position, ActualImpact),6:0.##}u " +
                $"line err {Vec3.Distance(line.Impact.Position, ActualImpact),6:0.##}u " +
                $"bounces {hull.BounceCount}/{line.BounceCount}"));
        }
    }
}
