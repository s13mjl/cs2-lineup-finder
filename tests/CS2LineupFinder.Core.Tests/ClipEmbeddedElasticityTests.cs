// <copyright file="ClipEmbeddedElasticityTests.cs" company="CS2LineupFinder">
// The embedded clip-brush contact rule, from its inputs to the flight path.
// Every expected number below is transcribed from the telemetry-002 forensic
// worksheet (CSV row 8 / contact 8), not recomputed or fitted.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

public sealed class ClipEmbeddedElasticityTests
{
    // telemetry-002 contact 8, straight from the CSV: pre-impact velocity with
    // the first-half gravity quantum already applied (vz_pre = -528.9596 - 2.5),
    // post-impact velocity, and the tangential ratio both components agree on.
    private const float InputVz = -531.4596f;
    private const float InputVx = -304.5064f;
    private const float InputVy = 351.8798f;

    [Fact]
    public void Resolve_EmbeddedContact_ScalesElasticityByMeasuredRatio()
    {
        PhysicsParameters p = new();
        bool smokeBounceDone = false;
        Vec3 velocity = BounceResolver.Resolve(
            new Vec3(InputVx, InputVy, InputVz),
            Vec3.UnitZ,
            onGround: false,
            GrenadeType.Smoke,
            p,
            ref smokeBounceDone,
            embeddedContact: true);

        // Exact transcription: 0.336431361 * |vz_in| on the normal axis, same
        // ratio on both tangential axes (CSV rows 8 vs 9, 7-digit agreement).
        Assert.Equal(178.8137f, velocity.Z, 4);
        Assert.Equal(-102.4455f, velocity.X, 4);
        Assert.Equal(118.3834f, velocity.Y, 4);
        // The embedded contact keeps the same normal residual; the CSV's
        // tick-end vz (176.3102) is the contact-frame value minus the second
        // half-tick gravity quantum, so 176.3102 + 2.5 = 178.8102 is the
        // target and 0.336431361*531.4596 + 0.0140625 = 178.8137 lands within
        // CSV rounding.
        Assert.Equal((0.336431361f * 531.4596f) + p.BounceNormalResidual.Value, velocity.Z, 3);
    }

    [Fact]
    public void Resolve_NormalContact_KeepsBaseElasticity()
    {
        PhysicsParameters p = new();
        bool smokeBounceDone = false;
        Vec3 velocity = BounceResolver.Resolve(
            new Vec3(InputVx, InputVy, InputVz),
            Vec3.UnitZ,
            onGround: false,
            GrenadeType.Smoke,
            p,
            ref smokeBounceDone,
            embeddedContact: false);

        // The 7 world contacts all return 0.45 plus the measured residual.
        // Sign convention: v_post = mirror(v_in, n) * e + n * C; with n = +Z
        // the downward input flips up on Z, while X and Y keep their input sign.
        Assert.Equal((0.45f * 531.4596f) + p.BounceNormalResidual.Value, velocity.Z, 3);
        Assert.Equal(0.45f * InputVx, velocity.X, 3);
        Assert.Equal(0.45f * InputVy, velocity.Y, 3);
    }
}
