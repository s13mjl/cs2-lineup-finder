// <copyright file="RecordedLandingLineVersusHullTests.cs" company="CS2LineupFinder">
// Replays the recorded calibration sample the 39.29 u error came from, twice on
// the same world: once through the hull sweep the source issues, once through the
// line sweep the deployed Core.dll still issues (deployed_core.txt: the main Sweep
// calls world.TraceRay and ProjectileHullRadius has zero hits). The launch state
// is rebuilt from the sample itself - release position, recorded aim angles, and
// the speed tier - so neither run gets an advantage the other lacks.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class RecordedLandingLineVersusHullTests
{
    private readonly ITestOutputHelper _output;

    public RecordedLandingLineVersusHullTests(ITestOutputHelper output) => _output = output;

    // landings-20261015.json sample 1, read verbatim.
    private static readonly Vec3 ReleasePosition = new(27.610249f, -2042.5939f, 22.760439f);
    private const float AimYaw = -159f;
    private const float AimPitch = -13.1f;
    private static readonly Vec3 ActualImpact = new(-909.1882f, -2402.1824f, -165.96875f);

    // The capture settles on a shelf whose surface the capture itself proves: the
    // resting hull centre is -165.96875, so the surface is 2 units below it.
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

    private static TrajectoryResult Run(IWorldGeometry world, PhysicsParameters parameters)
    {
        // Rebuild the launch vector the recorded aim implies: forward from the
        // recorded yaw/pitch, the engine up-bias, the primary throw speed, and no
        // inherited player velocity because the recorder logged a standing throw.
        float yawRadians = AimYaw * MathF.PI / 180f;
        float pitchRadians = AimPitch * MathF.PI / 180f;
        Vec3 forward = new(
            MathF.Cos(pitchRadians) * MathF.Cos(yawRadians),
            MathF.Cos(pitchRadians) * MathF.Sin(yawRadians),
            MathF.Sin(pitchRadians));

        Vec3 velocity = ThrowVelocityCalculator.Compute(
            forward, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, parameters);

        return new GrenadeSimulator(parameters).Simulate(
            new ThrowParams(ReleasePosition, velocity, GrenadeType.Smoke, 1f, 64),
            world);
    }

    /// <summary>
    /// The two runs differ only in the swept shape, so the gap between their
    /// landings is the whole line-versus-hull contribution for this sample. If the
    /// gap is far below the recorded 39.29 u, the line semantics cannot be what
    /// produced that error.
    /// </summary>
    [Fact]
    public void RecordedSample_SweptShapeContributionIsSmallerThanTheRecordedError()
    {
        TrajectoryResult hull = Run(BuildWorld(), new PhysicsParameters());
        TrajectoryResult line = Run(new LineOnlyWorldGeometry(BuildWorld()), new PhysicsParameters());

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

        Assert.True(gap.Length > 0.1f, "the swept shape changes the landing");

        // The recorded error is 39.29 u. The swept shape can only account for the
        // gap, so if the gap is a fraction of that, the line semantics deployed in
        // Core.dll cannot be what produced the recorded error on its own.
        Assert.True(
            gap.Length < 39.29f * 0.5f,
            $"the swept shape gap {gap.Length:0.##}u must be well under the recorded 39.29 u error");
    }
}
