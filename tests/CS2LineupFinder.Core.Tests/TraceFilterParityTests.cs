// <copyright file="TraceFilterParityTests.cs" company="CS2LineupFinder">
// The tracer and the simulator must answer "does this hit stop a grenade?" the
// same way. They used to use different tests, and every static brush entity with
// a positive engine index - func_brush, doors, breakables - was discarded by the
// simulator after the tracer had already reported it, so grenades flew through
// them. These tests pin the two answers together.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// A world whose hits carry a designer name and a positive entity index, i.e. what
/// the reflective trace reports for a static brush entity. Everything else is the
/// analytic box world.
/// </summary>
public sealed class NamedEntityWorldGeometry : IWorldGeometry
{
    private readonly MockWorldGeometry _inner;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">World that answers the geometry.</param>
    /// <param name="designerName">Designer name every contact reports.</param>
    /// <param name="entityIndex">Entity index every contact reports.</param>
    public NamedEntityWorldGeometry(MockWorldGeometry inner, string? designerName, int entityIndex)
    {
        _inner = inner;
        DesignerName = designerName;
        EntityIndex = entityIndex;
    }

    /// <summary>Designer name every contact reports.</summary>
    public string? DesignerName { get; }

    /// <summary>Entity index every contact reports.</summary>
    public int EntityIndex { get; }

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
        => Tag(_inner.TraceRay(origin, direction, maxDistance, ignoreEntityIndex));

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
        => Tag(_inner.TraceSphere(origin, direction, maxDistance, radius, ignoreEntityIndex));

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop)
        => Tag(_inner.ProbeGround(position, maxDrop));

    private TraceHit Tag(TraceHit hit) => hit.DidHit
        ? new TraceHit(true, hit.Position, hit.Normal, hit.Fraction, EntityIndex, DesignerName)
        : hit;
}

public class TraceFilterParityTests
{
    private readonly ITestOutputHelper _output;

    public TraceFilterParityTests(ITestOutputHelper output) => _output = output;

    private static readonly Vec3 LaunchPos = new(17.7950f, -2046.3620f, 21.8836f);
    private static readonly Vec3 LaunchVel = new(-628.1757f, -241.1344f, -58.6144f);

    private static TrajectoryResult Run(string? designerName, int entityIndex)
    {
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-167.97f);

        return new GrenadeSimulator(new PhysicsParameters()).Simulate(
            new ThrowParams(LaunchPos, LaunchVel, GrenadeType.Smoke, 1f, 64),
            new NamedEntityWorldGeometry(world, designerName, entityIndex));
    }

    /// <summary>
    /// The rule the plugin tracer already applies, exercised directly: a func_brush
    /// is solid to a grenade, a player is not.
    /// </summary>
    [Fact]
    public void SharedRule_MatchesWhatTheTracerAlreadyApplies()
    {
        Assert.True(TraceFilters.StopsProjectile("func_brush"));
        Assert.True(TraceFilters.StopsProjectile("func_door"));
        Assert.True(TraceFilters.StopsProjectile("func_breakable"));
        Assert.True(TraceFilters.StopsProjectile("prop_physics"));

        Assert.False(TraceFilters.StopsProjectile("player"));
        Assert.False(TraceFilters.StopsProjectile("smokegrenade_projectile"));
        Assert.False(TraceFilters.StopsProjectile("hegrenade_projectile"));
        Assert.False(TraceFilters.StopsProjectile("molotov_projectile"));

        // Dropped items and corpses are not solid to a grenade. Stopping on
        // them made the live trace stick where the real throw rolled on.
        Assert.False(TraceFilters.StopsProjectile("weapon_ak47"));
        Assert.False(TraceFilters.StopsProjectile("weapon_smokegrenade"));
        Assert.False(TraceFilters.StopsProjectile("item_assaultsuit"));
        Assert.False(TraceFilters.StopsProjectile("prop_ragdoll"));
        Assert.False(TraceFilters.StopsProjectile("chicken"));
        Assert.False(TraceFilters.StopsProjectile("hostage_entity"));

        // The world itself has no class name; the tracer never filters it out.
        Assert.True(TraceFilters.StopsProjectile(null));
    }

    /// <summary>
    /// A grenade dropped onto a floor reported as a func_brush with a positive
    /// entity index must land on it. Under the discarded rule the floor was not
    /// world geometry, so the sweep was ignored and the grenade fell forever.
    /// </summary>
    [Fact]
    public void FuncBrushFloor_StopsTheGrenade()
    {
        TrajectoryResult result = Run("func_brush", 513);

        _output.WriteLine(
            $"func_brush (index 513): bounces {result.BounceCount}, rest z {result.Impact.Position.Z:0.##}, " +
            $"flight {result.FlightTime:0.###}s");

        Assert.True(result.BounceCount > 0, "the grenade must bounce on the brush floor");
        Assert.True(
            MathF.Abs(result.Impact.Position.Z - (-165.9688f)) <= 0.5f,
            $"the grenade must rest on the brush floor, got z {result.Impact.Position.Z:0.##}");
    }

    /// <summary>
    /// The same floor reported as world BSP (index -1, no class name) still stops
    /// the grenade, which is the behaviour that was already correct.
    /// </summary>
    [Fact]
    public void WorldBspFloor_StopsTheGrenade()
    {
        TrajectoryResult result = Run(null, -1);

        _output.WriteLine(
            $"world BSP (index -1): bounces {result.BounceCount}, rest z {result.Impact.Position.Z:0.##}, " +
            $"flight {result.FlightTime:0.###}s");

        Assert.True(result.BounceCount > 0, "the grenade must bounce on world BSP");
        Assert.True(
            MathF.Abs(result.Impact.Position.Z - (-165.9688f)) <= 0.5f,
            $"the grenade must rest on world BSP, got z {result.Impact.Position.Z:0.##}");
    }

    /// <summary>
    /// Both labels describe the same physical floor, so the two runs must agree to
    /// the last bounce. That equivalence is the whole point of sharing the rule.
    /// </summary>
    [Fact]
    public void BrushAndBspLandings_AreIdentical()
    {
        TrajectoryResult brush = Run("func_brush", 513);
        TrajectoryResult bsp = Run(null, -1);

        Vec3 gap = brush.Impact.Position - bsp.Impact.Position;

        _output.WriteLine($"gap between the two landings: {gap.Length:0.####}u");

        Assert.True(
            gap.Length <= 0.01f,
            $"the same floor must land the grenade the same way, got a {gap.Length:0.##}u gap");
    }

    /// <summary>
    /// An entity the thrower asked to be ignored must never stop the grenade, no
    /// matter what its class name says - that is the grenade itself in flight.
    /// </summary>
    [Fact]
    public void IgnoredEntity_NeverStopsTheGrenade()
    {
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-167.97f);

        TrajectoryResult result = new GrenadeSimulator(new PhysicsParameters()).Simulate(
            new ThrowParams(LaunchPos, LaunchVel, GrenadeType.Smoke, 1f, 64) { IgnoreEntityIndex = 513 },
            new NamedEntityWorldGeometry(world, "func_brush", 513));

        _output.WriteLine(
            $"everything ignored (index 513): bounces {result.BounceCount}, rest z {result.Impact.Position.Z:0.##}");

        // With the only geometry ignored there is nothing to hit, so the grenade
        // keeps falling until the kill plane ends it.
        Assert.True(result.BounceCount == 0, "an ignored entity must not stop the grenade");
    }
}
