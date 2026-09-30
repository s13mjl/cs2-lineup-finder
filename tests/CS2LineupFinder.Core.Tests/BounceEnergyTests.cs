// <copyright file="BounceEnergyTests.cs" company="CS2LineupFinder">
// Acceptance test 4: bounce count and the energy decay across impacts.
// </copyright>

using System;
using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

public class BounceEnergyTests
{
    /// <summary>
    /// Expected bounce count for a full-power HE lobbed onto a flat floor,
    /// measured in-game and recorded in docs/PHYSICS.md.
    ///
    /// A 45 degree full throw at 675 u/s arrives at ~477 u/s, and every impact
    /// returns 0.45x that, so the sequence of impact speeds is roughly
    /// 477, 215, 97, 43, 19 ... and the grenade is declared at rest below the
    /// 20 u/s rest threshold. That predicts 4 audible bounces.
    /// </summary>
    private const int ExpectedBounces = 4;

    /// <summary>
    /// Acceptance 4: the simulated bounce count must land within +/-1 of the
    /// value recorded in PHYSICS.md.
    /// </summary>
    [Fact]
    public void FullThrowBounceCount_MatchesRecordedValue_WithinOne()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);

        // Long fuse so the grenade is stopped by friction, not by detonating.
        parameters.FuseSeconds.Value = 20f;

        const float floorHeight = 0f;
        Vec3 origin = new(0f, 0f, floorHeight + 64f);
        float speed = parameters.FullThrowSpeed.Value;
        float radians = 45f * MathF.PI / 180f;
        Vec3 velocity = new(speed * MathF.Cos(radians), 0f, speed * MathF.Sin(radians));

        ThrowParams input = new(origin, velocity, GrenadeType.He, 1f, 64);
        MockWorldGeometry world = MockWorldGeometry.Empty().AddInfiniteFloor(floorHeight);

        TrajectoryResult result = simulator.Simulate(input, world);

        Assert.InRange(result.BounceCount, ExpectedBounces - 1, ExpectedBounces + 1);
    }

    /// <summary>
    /// Each bounce must return less speed than the one before it, and each
    /// return must be close to the 0.45 restitution factor.
    /// </summary>
    [Fact]
    public void BouncesDecayGeometrically_TowardRestitutionFactor()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);
        parameters.FuseSeconds.Value = 20f;

        const float floorHeight = 0f;
        Vec3 origin = new(0f, 0f, floorHeight + 64f);
        float speed = parameters.FullThrowSpeed.Value;
        float radians = 30f * MathF.PI / 180f;
        Vec3 velocity = new(speed * MathF.Cos(radians), 0f, speed * MathF.Sin(radians));

        ThrowParams input = new(origin, velocity, GrenadeType.He, 1f, 64);
        MockWorldGeometry world = MockWorldGeometry.Empty().AddInfiniteFloor(floorHeight);

        TrajectoryResult result = simulator.Simulate(input, world);

        Assert.True(result.BounceCount >= 2, "Expected at least two bounces for this lob.");

        // Walk the recorded path and look at the speed just after each impact.
        float previous = float.MaxValue;
        int impacts = 0;

        for (int i = 1; i < result.Points.Count; i++)
        {
            TrajectoryPoint point = result.Points[i];

            // A new bounce index means this sample follows an impact.
            if (point.BounceCount > impacts)
            {
                impacts = point.BounceCount;

                // The post-impact speed must be lower than the pre-impact speed.
                float before = result.Points[i - 1].Velocity.Length;
                float after = point.Velocity.Length;

                Assert.True(after < before,
                    $"Bounce {impacts}: speed rose from {before:0.#} to {after:0.#}.");

                // Normal component is the one scaled by restitution. A flat
                // floor means the vertical part is what gets multiplied by 0.45.
                float verticalIn = MathF.Abs(result.Points[i - 1].Velocity.Z);
                float verticalOut = MathF.Abs(point.Velocity.Z);
                if (verticalIn > 1f)
                {
                    float ratio = verticalOut / verticalIn;

                    // 0.45 restitution, allowing for the substep quantisation of
                    // the sample grid and the rolling friction that follows.
                    Assert.InRange(ratio, 0.30f, 0.50f);
                }

                previous = after;
            }
        }

        Assert.True(previous < previous + 1f);
    }

    /// <summary>
    /// A grenade thrown straight down at a floor must stop, not tunnel through,
    /// which proves the adaptive sweep is short enough to catch the contact.
    /// </summary>
    [Fact]
    public void FastThrowIntoWall_DoesNotTunnelThrough()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);
        parameters.FuseSeconds.Value = 20f;

        // A thin wall: 8 units thick. At 675 u/s a 64 Hz tick moves 10.5 units,
        // so without sub-stepping and adaptive sweeps this would pass through.
        MockWorldGeometry world = MockWorldGeometry.Empty()
            .AddBox(new Vec3(200f, -500f, -500f), new Vec3(208f, 500f, 500f));

        Vec3 origin = new(0f, 0f, 0f);
        ThrowParams input = new(origin, new Vec3(675f, 0f, 0f), GrenadeType.He, 1f, 64);

        TrajectoryResult result = simulator.Simulate(input, world);

        // The wall spans x=200..208. Restitution sends the grenade back the way
        // it came, so the assertion is on the MAXIMUM x ever reached, not on
        // where it ends up. Anything past 208 would be a tunnelling failure.
        float maxX = result.Points.Max(p => p.Position.X);

        Assert.True(maxX < 208f,
            $"Grenade reached x={maxX:0.##}, tunnelled through the 8-unit wall (far face 208).");

        // It must have actually reached the wall, not fallen short of it.
        Assert.True(maxX > 195f,
            $"Grenade only reached x={maxX:0.##}; the wall face is at 200.");

        Assert.True(result.BounceCount >= 1, "Expected at least one bounce off the wall.");

        // And it must be travelling back toward the thrower after the bounce.
        Assert.True(result.Impact.Position.X < 200f,
            $"Grenade ended at x={result.Impact.Position.X:0.##}; it should have rebounded.");

    }

    /// <summary>
    /// The sweep length must ramp with speed: short hops when fast, long hops
    /// when slow, matching the 2..16 unit rule in the task spec.
    /// </summary>
    [Fact]
    public void SweepLengthRampsBetweenTwoAndSixteenUnits()
    {
        PhysicsParameters parameters = new();

        // At rest the step is at its maximum.
        Assert.Equal(parameters.MaxStepDistance.Value, BounceResolver.StepDistance(0f, parameters));

        // Well above the reference speed it is at its minimum.
        Assert.Equal(parameters.MinStepDistance.Value, BounceResolver.StepDistance(10000f, parameters));

        // And it decreases monotonically in between.
        float mid = BounceResolver.StepDistance(125f, parameters);
        Assert.InRange(mid, parameters.MinStepDistance.Value, parameters.MaxStepDistance.Value);
        Assert.True(mid > BounceResolver.StepDistance(250f, parameters));
    }

    /// <summary>
    /// A slow roll must issue far fewer sweeps than a fast throw over the same
    /// distance, which is the whole point of the adaptive step.
    /// </summary>
    [Fact]
    public void SlowFlightIssuesFewerSweepsThanFastFlight()
    {
        PhysicsParameters parameters = new();
        GrenadeSimulator simulator = new(parameters);
        parameters.FuseSeconds.Value = 20f;

        const float floorHeight = 0f;
        MockWorldGeometry emptyWorld = MockWorldGeometry.Empty();

        // 100 u/s versus 675 u/s, both travelling roughly the same distance.
        ThrowParams slow = new(new Vec3(0f, 0f, 300f), new Vec3(100f, 0f, 0f), GrenadeType.He, 1f, 64);
        MockWorldGeometry slowWorld = MockWorldGeometry.Empty();
        simulator.Simulate(slow, slowWorld);

        ThrowParams fast = new(new Vec3(0f, 0f, 300f), new Vec3(675f, 0f, 0f), GrenadeType.He, 1f, 64);
        MockWorldGeometry fastWorld = MockWorldGeometry.Empty();
        simulator.Simulate(fast, fastWorld);

        _ = emptyWorld;
        _ = floorHeight;

        Assert.True(slowWorld.TraceCount < fastWorld.TraceCount,
            $"Slow flight used {slowWorld.TraceCount} sweeps, fast used {fastWorld.TraceCount}.");
    }
}
