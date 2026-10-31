// <copyright file="EmbeddedScaleRuleTests.cs" company="CS2LineupFinder">
// R4 embedded-scale rule, simulated end to end: the reduced elasticity applies
// if and only if the contact begins embedded AND the post-restitution speed
// exceeds EmbeddedScaleSpeedThreshold (sqrt(96000), CS:GO leak
// ResolveFlyCollisionCustom). Evidence: telemetry-002 row 8 (embedded,
// 0.45 x 706.4 = 317.9 u/s -> eH 0.3364314, SCALED) versus telemetry-001 row
// 128 (embedded FIRST contact, 0.45 x 342.0 = 153.9 u/s -> eH 0.4499998, NOT
// scaled) and telemetry-002 rows 97/136/154 (embedded, all below the
// threshold, NOT scaled). The earlier "first embedded contact only" rule (R2)
// was falsified by telemetry-001 row 128.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Sdk;

namespace CS2LineupFinder.Core.Tests;

public sealed class EmbeddedScaleRuleTests
{
    // 0.45 (base elasticity) x 0.7476252 (measured embedded scale) - both
    // transcribed from PhysicsParameters, never recomputed here.
    private const float EmbeddedElasticity = 0.45f * 0.7476252f;

    [Fact]
    public void FastEmbeddedThrow_ScalesFastContacts_NotSlowTail()
    {
        // Reproduces the telemetry-002 row 8 -> rows 97/136/154 pattern: a
        // fast throw into a clip-brush band scales the fast embedded contacts
        // and leaves the slow tail at the standard 0.45. Launch speed
        // |v| = sqrt(600^2 + 300^2 + 280^2) ~= 726.9 u/s, so the reflected
        // speed 0.45 x 726.9 = 327.1 exceeds the sqrt(96000) threshold; the
        // bounce halves it to 244.6 and every later contact is below it.
        // The start height puts the first contact in the first half of its
        // tick (q=1), which is the only half the engine scales.
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-224f);
        world.AddClipBrush(
            new Vec3(-1e5f, -1e5f, -240f),
            new Vec3(1e5f, 1e5f, -100f));

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(
                new Vec3(0f, 0f, -140f),
                new Vec3(600f, 300f, -280f),
                GrenadeType.Smoke,
                1f,
                64),
            world);

        // Tangential components carry no residual and no gravity, so the
        // velocity ratio across a bounce IS the elasticity the resolver used.
        TrajectoryPoint first = Contact(result, 1);
        TrajectoryPoint second = Contact(result, 2);
        float firstRatio = first.Velocity.X / 600f;
        float secondRatio = second.Velocity.X / first.Velocity.X;
        Assert.InRange(firstRatio, EmbeddedElasticity - 1e-4f, EmbeddedElasticity + 1e-4f);
        Assert.InRange(secondRatio, 0.45f - 1e-5f, 0.45f + 1e-5f);
    }

    [Fact]
    public void FastEmbeddedThrow_SecondHalfContactKeepsBaseElasticity()
    {
        // The palace counterexample: the 1015 sample's embedded first contact
        // is a second-half contact (q=2, fraction 0.758). The engine keeps the
        // plain elasticity there (measured 762.5 -> 344.2 u/s, ratio 0.46),
        // so the speed gate alone must not scale it. The start height here
        // puts the same fast contact in the second half of its tick.
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-224f);
        world.AddClipBrush(
            new Vec3(-1e5f, -1e5f, -240f),
            new Vec3(1e5f, 1e5f, -100f));

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(
                new Vec3(0f, 0f, -150f),
                new Vec3(600f, 300f, -280f),
                GrenadeType.Smoke,
                1f,
                64),
            world);

        TrajectoryPoint first = Contact(result, 1);
        Assert.InRange(first.Velocity.X / 600f, 0.45f - 1e-5f, 0.45f + 1e-5f);
    }

    [Fact]
    public void SlowEmbeddedThrow_NeverScales()
    {
        // Reproduces the telemetry-001 row 128 pattern: a slow throw whose
        // FIRST embedded contact is already below the threshold bounces at
        // the plain 0.45 throughout. Launch speed |v| = sqrt(40^2 + 25^2 +
        // 200^2) ~= 207 u/s, reflected 93.1 - far below sqrt(96000).
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-224f);
        world.AddClipBrush(
            new Vec3(90f, -1e5f, -240f),
            new Vec3(130f, 1e5f, -100f));

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(
                new Vec3(96f, 0f, -190f),
                new Vec3(40f, 25f, -200f),
                GrenadeType.Smoke,
                1f,
                64),
            world);

        TrajectoryPoint first = Contact(result, 1);
        TrajectoryPoint second = Contact(result, 2);
        float firstRatio = first.Velocity.X / 40f;
        float secondRatio = second.Velocity.X / first.Velocity.X;
        Assert.InRange(firstRatio, 0.45f - 1e-5f, 0.45f + 1e-5f);
        Assert.InRange(secondRatio, 0.45f - 1e-5f, 0.45f + 1e-5f);

        // The grenade must come to rest inside the brush band, i.e. it really
        // spent every embedded contact without the scale.
        Assert.InRange(result.Impact.Position.X, 90f, 130f);
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
