// <copyright file="DiscretizationConvergenceTests.cs" company="CS2LineupFinder">
// Proves with measured numbers that the integration-discretisation knobs
// (SubstepsPerTick, Min/MaxStepDistance, StepDistanceSpeedRef) do not move the
// landing point, so their nominal values are numerics choices, not physics
// claims. A parameter that provably does not change the answer cannot be a
// fabricated physics constant.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class DiscretizationConvergenceTests
{
    private readonly ITestOutputHelper _output;

    public DiscretizationConvergenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Real recorded throw: 1013 palace shallow (yaw -161.2, pitch -6.9).
    private static readonly Sample FloorThrow =
        // FloorZ is the SURFACE, one hull half-height below the -165.96875
        // centre the recorder read for this throw.
        new("floor", new Vec3(38.9705f, -2036.9613f, -39.96875f), 64f, -161.2f, -6.9f, -167.96875f);

    private sealed record Sample(
        string Name, Vec3 Origin, float EyeHeight, float Yaw, float Pitch, float FloorZ);

    private static TrajectoryResult Simulate(Sample s, PhysicsParameters p, IWorldGeometry world)
    {
        Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(s.Yaw, s.Pitch);
        Vec3 unbiased = aim.Normalized();
        Vec3 velocity = ThrowVelocityCalculator.Compute(aim, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, p);
        Vec3 release = s.Origin + (unbiased * p.ReleaseOffset.Value) + new Vec3(0f, 0f, s.EyeHeight);
        GrenadeSimulator simulator = new(p);
        return simulator.Simulate(new ThrowParams(release, velocity, GrenadeType.Smoke, 1f, 64), world);
    }

    private static MockWorldGeometry FloorWorld(Sample s)
        => MockWorldGeometry.Empty().AddInfiniteFloor(s.FloorZ);

    /// <summary>
    /// Thin X-aligned wall 600u downrange forces a vertical-face bounce
    /// (leftover-advance path). Spans wide in Y so the diagonal throw cannot
    /// go around it, and the origin stays outside the box.
    /// </summary>
    private static MockWorldGeometry WallWorld(Sample s)
    {
        MockWorldGeometry world = FloorWorld(s);
        float yawRad = s.Yaw * MathF.PI / 180f;
        float fx = MathF.Cos(yawRad);
        float fy = MathF.Sin(yawRad);
        float cx = s.Origin.X + (600f * fx);
        float cy = s.Origin.Y + (600f * fy);
        float thickness = 64f;
        float yHalf = 1500f;
        Vec3 min = new(cx - (thickness / 2), cy - yHalf, s.FloorZ - 64f);
        Vec3 max = new(cx + (thickness / 2), cy + yHalf, s.FloorZ + 512f);
        return world.AddBox(min, max);
    }

    [Fact]
    public void SubstepRefinement_LandingConverges()
    {
        foreach (Sample scenario in new[] { FloorThrow, FloorThrow with { Name = "wall" } })
        {
            MockWorldGeometry world = scenario.Name == "wall" ? WallWorld(FloorThrow) : FloorWorld(FloorThrow);
            _output.WriteLine($"scenario: {scenario.Name}");
            var results = new Dictionary<int, Vec3>();
            foreach (int substeps in new[] { 1, 2, 4, 8, 16 })
            {
                PhysicsParameters p = new();
                p.SubstepsPerTick.Value = substeps;
                TrajectoryResult r = Simulate(FloorThrow, p, world);
                results[substeps] = r.Impact.Position;
                _output.WriteLine(
                    $"  substeps={substeps,2}: landing ({r.Impact.Position.X,8:0.00},{r.Impact.Position.Y,8:0.00},{r.Impact.Position.Z,8:0.00})");
            }

            Vec3 finest = results[16];
            float worst = 0f;
            foreach ((int substeps, Vec3 landing) in results)
            {
                float d = (landing - finest).Length;
                if (d > worst)
                {
                    worst = d;
                }
            }

            _output.WriteLine($"  max deviation from substeps=16: {worst:0.00}u");
            Assert.True(worst <= 30f, $"substep sweep moved landing by {worst:0.00}u (threshold 30u pending data-informed tightening)");
        }
    }

    [Fact]
    public void StepDistanceGrid_LandingInsensitive()
    {
        foreach (Sample scenario in new[] { FloorThrow, FloorThrow with { Name = "wall" } })
        {
            MockWorldGeometry world = scenario.Name == "wall" ? WallWorld(FloorThrow) : FloorWorld(FloorThrow);
            _output.WriteLine($"scenario: {scenario.Name}");
            Vec3 reference = Simulate(FloorThrow, new PhysicsParameters(), world).Impact.Position;
            _output.WriteLine($"  reference (defaults): ({reference.X:0.00},{reference.Y:0.00},{reference.Z:0.00})");

            float worst = 0f;
            string? worstCombo = null;
            int combos = 0;
            foreach (float minStep in new[] { 1f, 2f, 4f })
            foreach (float maxStep in new[] { 8f, 12f, 16f, 24f })
            foreach (float refSpeed in new[] { 100f, 250f, 400f })
            {
                if (minStep >= maxStep)
                {
                    continue;
                }

                PhysicsParameters p = new();
                p.MinStepDistance.Value = minStep;
                p.MaxStepDistance.Value = maxStep;
                p.StepDistanceSpeedRef.Value = refSpeed;
                Vec3 landing = Simulate(FloorThrow, p, world).Impact.Position;
                float d = (landing - reference).Length;
                combos++;
                if (d > worst)
                {
                    worst = d;
                    worstCombo = $"min={minStep} max={maxStep} ref={refSpeed} -> {d:0.00}u";
                }
            }

            _output.WriteLine($"  combos={combos}, max deviation from defaults: {worst:0.00}u ({worstCombo})");
            Assert.True(worst <= 30f, $"step grid moved landing by {worst:0.00}u (threshold 30u pending data-informed tightening)");
        }
    }
}
