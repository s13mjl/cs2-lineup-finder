// <copyright file="BouncePaddingTests.cs" company="CS2LineupFinder">
// Bounce padding, transcribed from the leaked ResolveFlyCollisionCustom and
// validated against two recorded CS2 contacts.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Sdk;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// The engine pads a fast floor bounce by how squarely the reflected velocity
/// leaves the floor: alongDist = dot(normalize(v_post), n), applied when the
/// reflected speed exceeds sqrt(96000) and alongDist &gt; 0.5. It is gated on a
/// floor-like normal, so wall contacts never pad. Evidence: telemetry-002
/// contact 8 (0.3367 measured, 0.3357 predicted) and the 2026-10-31 landing
/// sample #4 (0.236 measured, 0.2392 predicted). The retired embedded-clip
/// scale was a coincidental fit to the former contact.
/// </summary>
public sealed class BouncePaddingTests
{
    [Fact]
    public void Resolve_FastSteepFloorBounce_PadsLikeTheEngine()
    {
        // telemetry-002 contact 8, straight from the CSV with the first-half
        // gravity quantum applied to vz. The engine turns these into
        // (-102.4455, 118.3834, 176.3) - a whole-vector ratio of 0.3367.
        PhysicsParameters parameters = new();
        bool smokeBounceDone = false;
        Vec3 input = new(-304.5064f, 351.8798f, -531.4596f);

        Vec3 output = BounceResolver.Resolve(
            input,
            Vec3.UnitZ,
            onGround: false,
            GrenadeType.Smoke,
            parameters,
            ref smokeBounceDone);

        Assert.Equal(-102.45f, output.X, 1);
        Assert.Equal(118.38f, output.Y, 1);
        Assert.Equal(178.82f, output.Z, 1);

        float ratio = output.Length / input.Length;
        Assert.InRange(ratio, 0.334f, 0.338f);
    }

    [Fact]
    public void Resolve_SlowFloorBounce_KeepsThePlainRestitution()
    {
        // Reflected speed 0.45 x 187.7 = 84.5 sits below sqrt(96000): no pad.
        PhysicsParameters parameters = new();
        bool smokeBounceDone = false;

        Vec3 output = BounceResolver.Resolve(
            new Vec3(100f, 50f, -150f),
            Vec3.UnitZ,
            onGround: false,
            GrenadeType.Smoke,
            parameters,
            ref smokeBounceDone);

        Assert.Equal(45f, output.X, 2);
        Assert.Equal(67.5f, output.Z, 1);
    }

    [Fact]
    public void Resolve_ShallowFastFloorBounce_IsNotPadded()
    {
        // Reflected speed 0.45 x 752.4 = 338.6 exceeds the threshold, but the
        // reflected velocity leaves the floor at alongDist ~0.4, so the engine
        // keeps the plain restitution.
        PhysicsParameters parameters = new();
        bool smokeBounceDone = false;

        Vec3 output = BounceResolver.Resolve(
            new Vec3(690f, 0f, -300f),
            Vec3.UnitZ,
            onGround: false,
            GrenadeType.Smoke,
            parameters,
            ref smokeBounceDone);

        Assert.Equal(310.5f, output.X, 1);
        Assert.Equal(135f, output.Z, 1);
    }

    [Fact]
    public void Resolve_FastWallContact_IsNeverPadded()
    {
        // The leak gates padding on trace.plane.normal.z > 0.7; a vertical
        // face keeps the plain elasticity however fast and aligned it is.
        PhysicsParameters parameters = new();
        bool smokeBounceDone = false;

        Vec3 output = BounceResolver.Resolve(
            new Vec3(-700f, -50f, -200f),
            Vec3.UnitX,
            onGround: false,
            GrenadeType.Smoke,
            parameters,
            ref smokeBounceDone);

        Assert.Equal(315f, output.X, 1);
        Assert.Equal(-22.5f, output.Y, 1);
    }

    [Fact]
    public void Simulator_ReproducesTheRecordedSampleFourFirstBounce()
    {
        // Landing sample #4 (2026-10-31, de_mirage): a 764 u/s floor impact.
        // telemetry-012 records the post-bounce velocity (-42.9, -13.1, 176.2)
        // - a ratio of 0.236. Before padding existed the simulator bounced it
        // at 0.45, over-rolled by ~160 u and missed the real landing by 157 u.
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-165.96875f);

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(
                new Vec3(53.8464f, -2057.0800f, 49.4879f),
                new Vec3(-179.8488f, -54.9852f, 643.2715f),
                GrenadeType.Smoke,
                1f,
                64),
            world);

        TrajectoryPoint first = Contact(result, 1);

        // The invariant the engine enforces is the whole-vector ratio: the
        // recorded contact returns 236.6/764.3 = 0.236, the padded law 0.239.
        float ratio = first.Velocity.Length / 764.3f;
        Assert.InRange(ratio, 0.233f, 0.242f);
        Assert.Equal(-43.0f, first.Velocity.X, 0);
        Assert.Equal(-13.1f, first.Velocity.Y, 0);
    }

    private static TrajectoryPoint Contact(TrajectoryResult result, int bounce)
    {
        foreach (TrajectoryPoint point in result.Points)
        {
            if (point.BounceCount == bounce)
            {
                return point;
            }
        }

        throw new XunitException($"no trajectory point tagged with bounce {bounce}");
    }
}
