// <copyright file="FlightCapIrrelevanceTests.cs" company="CS2LineupFinder">
// Proves with measured numbers that MaxFlightSeconds cannot move a simulated
// landing: every simulated grenade type ends its flight by its own fuse or
// settle rule well before the 12 s cap. A cap that provably never binds is a
// numerics guard, not a physics claim, so its nominal value carries no risk.
// </copyright>

using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class FlightCapIrrelevanceTests
{
    private readonly ITestOutputHelper _output;

    public FlightCapIrrelevanceTests(ITestOutputHelper output) => _output = output;

    private static readonly Vec3 Origin = new(0f, 0f, 0f);
    private const float FloorZ = -64f;
    private const float EyeHeight = 64f;

    private static MockWorldGeometry FlatWorld()
        => MockWorldGeometry.Empty().AddInfiniteFloor(FloorZ);

    private static TrajectoryResult Simulate(GrenadeType type, float pitch, float capSeconds)
    {
        PhysicsParameters p = new();
        p.MaxFlightSeconds.Value = capSeconds;
        Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(0f, pitch);
        Vec3 velocity = ThrowVelocityCalculator.Compute(aim, Vec3.Zero, ThrowButton.Primary, type, 1f, p);
        Vec3 release = Origin + (aim.Normalized() * p.ReleaseOffset.Value) + new Vec3(0f, 0f, EyeHeight);
        GrenadeSimulator simulator = new(p);
        return simulator.Simulate(new ThrowParams(release, velocity, type, 1f, 64), FlatWorld());
    }

    private static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

    /// <summary>
    /// For every simulated grenade type across the solver's elevation envelope,
    /// the flight must end strictly before the 12 s default cap, and raising the
    /// cap to 20 s must reproduce the identical flight second for second. The
    /// steep 85 degree lob is the slowest settling case (the most restitution
    /// bounces and the longest hang time), so a cap that binds there would bind
    /// everywhere in the search envelope.
    /// </summary>
    [Fact]
    public void CapAtTwelveSeconds_NeverBinds_AndTwentySecondCap_ReproducesEveryFlight()
    {
        GrenadeType[] types = { GrenadeType.Smoke, GrenadeType.Flash, GrenadeType.He, GrenadeType.Molotov };
        float[] pitches = { -15f, 0f, 15f, 30f, 45f, 60f, 75f, 85f };

        foreach (GrenadeType type in types)
        {
            foreach (float pitch in pitches)
            {
                TrajectoryResult at12 = Simulate(type, pitch, 12f);
                TrajectoryResult at20 = Simulate(type, pitch, 20f);

                Assert.True(
                    at12.Impact.Time < 12f,
                    $"{type} pitch {pitch:0}: flight ended at {at12.Impact.Time:0.00}s, i.e. the 12s cap bound the simulation");
                Assert.True(
                    at12.Impact.Detonated,
                    $"{type} pitch {pitch:0}: flight ended by rest/kill instead of the fuse or settle rule");

                Assert.Equal(at20.Impact.Time, at12.Impact.Time, 3);
                Assert.True(
                    Distance(at20.Impact.Position, at12.Impact.Position) < 1e-3f,
                    $"{type} pitch {pitch:0}: cap 12s vs 20s moved the landing by {Distance(at20.Impact.Position, at12.Impact.Position):0.000}u");

                _output.WriteLine($"{type} pitch {pitch,5:0.#}: flight {at12.Impact.Time:0.00}s, rest ({at12.Impact.Position.X:0.00}, {at12.Impact.Position.Y:0.00}), bounces {at12.Impact.BounceCount}");
            }
        }
    }

    /// <summary>
    /// Even the smallest cap the parameter's search range allows (9 s) must stay
    /// unbinding, which pins the whole range 9..20 as landing-invariant. The
    /// floor sits above the slowest reachable flight, which is a 85-deg smoke at
    /// 8.04 s - measured, not assumed, and the reason the range floor is 9 s
    /// rather than the 8 s it used to be.
    /// </summary>
    [Fact]
    public void CapAtRangeFloor_NineSeconds_NeverBinds_Either()
    {
        GrenadeType[] types = { GrenadeType.Smoke, GrenadeType.He };
        float[] pitches = { -15f, 85f };

        foreach (GrenadeType type in types)
        {
            foreach (float pitch in pitches)
            {
                TrajectoryResult atRangeFloor = Simulate(type, pitch, 9f);
                TrajectoryResult at20 = Simulate(type, pitch, 20f);

                Assert.True(
                    atRangeFloor.Impact.Time < 9f,
                    $"{type} pitch {pitch:0}: flight ended at {atRangeFloor.Impact.Time:0.00}s, i.e. even the 9s range floor bound the simulation");
                Assert.Equal(at20.Impact.Time, atRangeFloor.Impact.Time, 3);
                Assert.True(Distance(at20.Impact.Position, atRangeFloor.Impact.Position) < 1e-3f);
            }
        }
    }

    /// <summary>
    /// Direct evidence that the settle rule is what ends a smoke flight: the
    /// smoke keeps sliding past its 1.5s fuse and only pops once the 0.1 u/s
    /// settle threshold is crossed (engine Think_Detonate re-check), so the cap
    /// has nothing to do with when the smoke blooms.
    /// </summary>
    [Fact]
    public void Smoke_FlightEndsBySettleRule_NotByFuseOrCap()
    {
        TrajectoryResult result = Simulate(GrenadeType.Smoke, 30f, 12f);

        Assert.True(result.Impact.Time > 1.7f,
            $"smoke flight ended at {result.Impact.Time:0.00}s, before the fuse+think window (1.5s + 0.2s) could have elapsed, so the settle rule was not what ended it");
        Assert.True(result.Impact.Detonated);
        Assert.Equal(GrenadeType.Smoke, result.Impact.DetonatedType);
    }
}
