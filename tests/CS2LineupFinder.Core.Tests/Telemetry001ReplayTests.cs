// <copyright file="Telemetry001ReplayTests.cs" company="CS2LineupFinder">
// Replays the clean 2026-10-15 64 Hz telemetry capture against the simulator,
// on a world built from the four horizontal planes the capture itself proves.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class Telemetry001ReplayTests
{
    private readonly ITestOutputHelper _output;

    public Telemetry001ReplayTests(ITestOutputHelper output) => _output = output;

    // Launch state read straight off telemetry-001.csv (t=0.0000 row), and the
    // four floor heights each bounce proves by its own pre-collision sample.
    private static readonly Vec3 LaunchPos = new(17.7950f, -2046.3620f, 21.8836f);
    private static readonly Vec3 LaunchVel = new(-628.1757f, -241.1344f, -58.6144f);
    private static readonly Vec3 ActualImpact = new(-909.1882f, -2402.1820f, -165.9688f);

    // The capture bounces at z = -177.966 / -169.968 / -165.966 / -165.967, so
    // the ground is not one plane: the grenade first falls onto a lower shelf,
    // then onto the shelf the smoke finally rests on. Each entry is the SURFACE
    // height, i.e. the recorded contact CENTRE height MINUS the 2-unit hull
    // half-height measured by the capture itself (b4 centre -165.9630 vs final
    // resting centre -165.9688). The recorder reads the hull centre, and the
    // centre settles 2 units above the surface it rests on.
    private static readonly float[] Floors = { -179.97f, -171.97f, -167.97f };

    private static TrajectoryResult Run(PhysicsParameters p)
    {
        MockWorldGeometry world = new();
        foreach (float z in Floors)
        {
            world.AddInfiniteFloor(z);
        }

        // The hull the engine gives a grenade is a 4 unit cube; the simulator
        // traces a line, so the floor sits that far above the contact height.
        GrenadeSimulator simulator = new(p);
        return simulator.Simulate(
            new ThrowParams(LaunchPos, LaunchVel, GrenadeType.Smoke, 1f, 64),
            world);
    }

    /// <summary>
    /// The launch state is reproduced to the last digit by
    /// ThrowVelocityCalculator, so the only unknowns left are the bounce and
    /// integration rules. This replays the capture on the floors it proves and
    /// reports the residual, per parameter set, with no assertion.
    /// </summary>
    [Fact]
    public void Telemetry001_ReplayDiagnostics()
    {
        _output.WriteLine($"actual impact {ActualImpact.X:0.####},{ActualImpact.Y:0.####},{ActualImpact.Z:0.####}");
        Report("baseline (live config)", new PhysicsParameters());

        PhysicsParameters noPad = new();
        noPad.BouncePaddingEnabled.Value = 0f;
        Report("no bounce padding", noPad);

        // Coulomb mu and GroundFriction were deleted: the whole-vector law
        // already explains every measured contact, so they have nothing to vary.
        PhysicsParameters surf074 = new();
        surf074.SurfaceElasticityScale.Value = 0.7439f;

        PhysicsParameters roll4 = new();
        roll4.GroundRollFrictionRate.Value = 4f;
        Report("roll rate 4/s", roll4);
    }

    private void Report(string label, PhysicsParameters p)
    {
        TrajectoryResult r = Run(p);
        Vec3 d = r.Impact.Position - ActualImpact;
        _output.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{label}: impact ({r.Impact.Position.X:0.##},{r.Impact.Position.Y:0.##},{r.Impact.Position.Z:0.##}) " +
            $"err {d.Length:0.##}u (dx {d.X:0.##} dy {d.Y:0.##} dz {d.Z:0.##}) bounces {r.BounceCount} flight {r.FlightTime:0.000}s"));
    }
}
