// <copyright file="HullRadiusParityTests.cs" company="CS2LineupFinder">
// Isolates the swept-hull question the 39.29u landing error turned on.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// The engine gives a grenade a 4-unit hull, so its recorded <c>AbsOrigin</c> is
/// the hull centre. Measured, not assumed: both telemetry captures came to rest
/// with their recorded origin at <c>surface + 1.968</c> (telemetry-001 at
/// -165.9688 on a -167.9688 surface, telemetry-002 at -221.9688 on a -223.9688
/// surface), so the hull is a 4-unit cube and the recorder reads the centre.
///
/// <para>
/// A zero-radius line sweep instead reports contact when the CENTRE reaches the
/// surface, i.e. two units early on every bounce. On an infinite plane that is
/// invisible; on a real map the 2-unit-early bounce clips ledges and step edges
/// the real grenade clears. That is what turned a verified-correct launch into a
/// 39.29u landing miss, and it is why <see cref="GrenadeSimulator"/> sweeps with
/// a hull instead of a line.
/// </para>
/// </summary>
public class HullRadiusParityTests
{
    private readonly ITestOutputHelper _output;

    public HullRadiusParityTests(ITestOutputHelper output) => _output = output;

    // telemetry-001.csv t=0.0000 row: the state one tick after release.
    private static readonly Vec3 LaunchPos = new(17.7950f, -2046.3620f, 21.8836f);
    private static readonly Vec3 LaunchVel = new(-628.1757f, -241.1344f, -58.6144f);

    // The real grenade came to rest with its centre at z = -165.9688, so the
    // surface it rests on is 2 units BELOW that: the recorder reads the centre.
    private const float RestingCentreZ = -165.9688f;
    private const float SurfaceZ = RestingCentreZ - 2f;

    /// <summary>
    /// Direct measurement of the two trace semantics on the same surface. A line
    /// sweep reports the surface point; a 2-unit hull sweep reports where the
    /// hull CENTRE stops, two units above it. This is the whole parity question,
    /// measured without going through the simulator.
    /// </summary>
    [Fact]
    public void LineAndHullSweepsReportDifferentDepthsOnTheSameSurface()
    {
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(SurfaceZ);

        // Straight down from well above the surface.
        Vec3 from = new Vec3(0f, 0f, SurfaceZ + 64f);
        Vec3 down = new Vec3(0f, 0f, -1f);

        TraceHit line = world.TraceRay(from, down, 256f);
        TraceHit hull = world.TraceSphere(from, down, 256f, 2f);

        _output.WriteLine($"surface              z = {SurfaceZ:0.####}");
        _output.WriteLine($"line  reported point z = {line.Position.Z:0.####}");
        _output.WriteLine($"hull  reported point z = {hull.Position.Z:0.####}");

        Assert.True(line.DidHit);
        Assert.True(hull.DidHit);

        // The line sweep stops with the centre ON the surface; the hull sweep
        // stops with the centre one hull half-height above it.
        Assert.True(
            MathF.Abs(line.Position.Z - SurfaceZ) <= 0.05f,
            $"line sweep should report the surface at {SurfaceZ:0.####}, got {line.Position.Z:0.####}");
        Assert.True(
            MathF.Abs(hull.Position.Z - (SurfaceZ + 2f)) <= 0.05f,
            $"hull sweep should report surface + hull half-height = {SurfaceZ + 2f:0.####}, got {hull.Position.Z:0.####}");
    }

    /// <summary>
    /// With the surface placed where the capture proves it is, the simulator
    /// reproduces the recorded resting centre to the 0.05 unit skin. This is the
    /// same experiment that failed when the surface was two units too high.
    /// </summary>
    [Fact]
    public void HullSweep_ReproducesTheRecordedRestingCentre()
    {
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(SurfaceZ);

        TrajectoryResult result = new GrenadeSimulator(new PhysicsParameters()).Simulate(
            new ThrowParams(LaunchPos, LaunchVel, GrenadeType.Smoke, 1f, 64),
            world);

        _output.WriteLine($"recorded resting centre z = {RestingCentreZ:0.####}");
        _output.WriteLine($"simulated resting centre z = {result.Impact.Position.Z:0.####}");

        Assert.True(
            MathF.Abs(result.Impact.Position.Z - RestingCentreZ) <= 0.25f,
            $"simulator should rest at {RestingCentreZ:0.####}, got {result.Impact.Position.Z:0.####}");
    }
}

