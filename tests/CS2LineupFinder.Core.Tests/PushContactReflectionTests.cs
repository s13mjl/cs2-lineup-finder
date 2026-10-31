// <copyright file="PushContactReflectionTests.cs" company="CS2LineupFinder">
// The palace lower-shelf edge: a first bounce on the shelf top followed by a
// second contact against its steep face inside the same tick. The old push
// path moved the projectile to the face but kept the first bounce's velocity,
// so the velocity still pointed into the face and the grenade stuck on the
// shelf. The 1015 sample's live replay rested 12 u low and 132.8 u short.
// </copyright>

using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// A deterministic world for the two-contact sequence: the first sweep hits a
/// floor-like face, the push sweep hits a vertical face, and every later sweep
/// misses. The scripted normals isolate the push response from trace geometry.
/// </summary>
internal sealed class ScriptedShelfEdgeWorld : IWorldGeometry
{
    private int _calls;

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
        => new(false, origin + (direction.Normalized() * maxDistance), Vec3.UnitZ, 1f);

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
    {
        Vec3 dir = direction.Normalized();
        return _calls++ switch
        {
            0 => new TraceHit(
                true,
                origin + (dir * (maxDistance * 0.25f)),
                Vec3.UnitZ,
                0.25f),
            1 => new TraceHit(
                true,
                origin + (dir * (maxDistance * 0.5f)),
                new Vec3(-1f, 0f, 0f),
                0.5f),
            _ => new TraceHit(false, origin + (dir * maxDistance), Vec3.UnitZ, 1f),
        };
    }

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop)
        => new(false, position + new Vec3(0f, 0f, -maxDrop), Vec3.UnitZ, 1f);
}

public sealed class PushContactReflectionTests
{
    private readonly ITestOutputHelper _output;

    public PushContactReflectionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SteepPushContact_ReflectsTheVelocity_InsteadOfLeavingItInTheFace()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(
                new Vec3(0f, 0f, 0f),
                new Vec3(400f, 0f, -100f),
                GrenadeType.Smoke,
                1f,
                64),
            new ScriptedShelfEdgeWorld());

        TrajectoryPoint bounce = result.Points.First(p => p.BounceCount > 0);
        _output.WriteLine($"first bounce tick-end p=({bounce.Position.X:0.###},{bounce.Position.Y:0.###},{bounce.Position.Z:0.###}) " +
            $"v=({bounce.Velocity.X:0.###},{bounce.Velocity.Y:0.###},{bounce.Velocity.Z:0.###})");

        // The first bounce alone leaves x positive. The vertical push face must
        // flip it: the old push path kept x at roughly +180 u/s, i.e. straight
        // into the face, which is the shelf-edge stick.
        Assert.True(
            bounce.Velocity.X < -1f,
            $"the steep push face must reflect the velocity, got vx={bounce.Velocity.X:0.###}");
    }
}
