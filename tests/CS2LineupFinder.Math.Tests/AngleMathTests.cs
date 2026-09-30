using System;
using CS2LineupFinder.Contracts;
using Xunit;
using static System.Math;

namespace CS2LineupFinder.Math.Tests;

/// <summary>
/// The Source view convention is the easiest thing in this project to get subtly wrong, so
/// it is pinned down here rather than assumed: pitch rotates about +Y (negative looks up),
/// yaw rotates about +Z, and the round trip direction -> angle -> direction is stable.
/// </summary>
public sealed class AngleMathTests
{
    [Fact]
    public void NegativePitchLooksUp()
    {
        var forward = AngleMath.ForwardFromAngle(new QAngle(pitch: -45d, yaw: 0d));
        Assert.Equal(0.70710678d, forward.X, 6);
        Assert.Equal(0d, forward.Y, 6);
        Assert.Equal(0.70710678d, forward.Z, 6);
    }

    [Fact]
    public void PositivePitchLooksDown()
    {
        var forward = AngleMath.ForwardFromAngle(new QAngle(pitch: 45d, yaw: 0d));
        Assert.Equal(0.70710678d, forward.X, 6);
        Assert.Equal(-0.70710678d, forward.Z, 6);
    }

    [Fact]
    public void YawRotatesAboutTheVerticalAxis()
    {
        var forward = AngleMath.ForwardFromAngle(new QAngle(pitch: 0d, yaw: 90d));
        Assert.Equal(0d, forward.X, 6);
        Assert.Equal(1d, forward.Y, 6);

        var south = AngleMath.ForwardFromAngle(new QAngle(pitch: 0d, yaw: 180d));
        Assert.Equal(-1d, south.X, 6);
        Assert.Equal(0d, south.Y, 5);
    }

    [Fact]
    public void RollDoesNotMoveTheForwardVector()
    {
        var withRoll = AngleMath.ForwardFromAngle(new QAngle(pitch: -30d, yaw: 20d, roll: 45d));
        var withoutRoll = AngleMath.ForwardFromAngle(new QAngle(pitch: -30d, yaw: 20d));
        Assert.Equal(withoutRoll.X, withRoll.X, 5);
        Assert.Equal(withoutRoll.Y, withRoll.Y, 5);
        Assert.Equal(withoutRoll.Z, withRoll.Z, 5);
    }

    [Fact]
    public void AngleFromDirectionInvertsForwardFromAngle()
    {
        for (var pitch = -QAngle.MaxPitch; pitch <= QAngle.MaxPitch; pitch += 7d)
        {
            for (var yaw = -180d; yaw < 180d; yaw += 13d)
            {
                var original = new QAngle(pitch, yaw).Normalized();
                var recovered = AngleMath.AngleFromDirection(AngleMath.ForwardFromAngle(original)).Normalized();
                Assert.Equal(original.Pitch, recovered.Pitch, 4);
                Assert.Equal(original.Yaw, recovered.Yaw, 4);
            }
        }
    }

    [Fact]
    public void AngleToPointsFromTheThrowerAtTheTarget()
    {
        var angle = AngleMath.AngleTo(new Vec3(0f, 0f, 64f), new Vec3(100f, 100f, 64f));
        Assert.Equal(45d, angle.Yaw, 6);
        Assert.Equal(0d, angle.Pitch, 6);

        var above = AngleMath.AngleTo(new Vec3(0f, 0f, 0f), new Vec3(100f, 0f, 100f));
        Assert.Equal(0d, above.Yaw, 6);
        Assert.Equal(-45d, above.Pitch, 6);
    }

    [Fact]
    public void AngleFromDirectionOnADegenerateVectorIsZero()
    {
        var angle = AngleMath.AngleFromDirection(Vec3.Zero);
        Assert.Equal(0d, angle.Pitch);
        Assert.Equal(0d, angle.Yaw);
    }

    [Theory]
    [InlineData(-190d, 170d)]
    [InlineData(370d, 10d)]
    [InlineData(181d, -179d)]
    [InlineData(-180d, 180d)]
    [InlineData(0d, 0d)]
    public void NormalizeYawWrapsOntoTheCanonicalRange(double input, double expected)
    {
        Assert.Equal(expected, AngleMath.NormalizeYaw(input), 9);
    }

    [Fact]
    public void AngleDifferenceTakesTheShortWayRound()
    {
        Assert.Equal(10d, AngleMath.AngleDifference(355d, 5d), 9);
        Assert.Equal(-10d, AngleMath.AngleDifference(5d, 355d), 9);
    }

    [Fact]
    public void ElevationIsTheNegatedEnginePitch()
    {
        Assert.Equal(45d, AngleMath.ElevationFromPitch(-45d), 9);
        Assert.Equal(-45d, AngleMath.PitchFromElevation(45d), 9);
        var direction = AngleMath.DirectionFromElevation(0d, 45d);
        Assert.True(direction.Z > 0.7f, "a positive elevation must climb");
    }

    [Fact]
    public void ClampPitchKeepsTheBasisNonDegenerate()
    {
        Assert.Equal(QAngle.MaxPitch, AngleMath.ClampPitch(95d), 9);
        Assert.Equal(-QAngle.MaxPitch, AngleMath.ClampPitch(-120d), 9);
    }

    [Fact]
    public void TheViewBasisStaysOrthonormal()
    {
        for (var pitch = -89d; pitch <= 89d; pitch += 11d)
        {
            var angle = new QAngle(pitch, 33d);
            var forward = AngleMath.ForwardFromAngle(angle);
            var right = AngleMath.RightFromAngle(angle);
            var up = AngleMath.UpFromAngle(angle);
            Assert.Equal(1d, forward.Length, 5);
            Assert.Equal(1d, up.Length, 5);
            Assert.Equal(0d, Vec3.Dot(forward, right), 5);
            Assert.Equal(0d, Vec3.Dot(forward, up), 5);
            Assert.Equal(0d, Vec3.Dot(right, up), 5);
        }
    }

    /// <summary>
    /// contracts/Vec3 owns add, subtract, dot, cross, normalize and distance; math-core must not
    /// redeclare them. This test is the proof the solver can lean on the contract instead.
    /// </summary>
    [Fact]
    public void ContractVectorSuppliesTheOperationsTheSolverReliesOn()
    {
        var a = new Vec3(1f, 2f, 3f);
        var b = new Vec3(4f, 5f, 6f);
        Assert.Equal(new Vec3(5f, 7f, 9f), a + b);
        Assert.Equal(new Vec3(-3f, -3f, -3f), a - b);
        Assert.Equal(32f, Vec3.Dot(a, b), 5);
        Assert.Equal(new Vec3(-3f, 6f, -3f), Vec3.Cross(a, b));
        Assert.Equal(Sqrt(14d), (double)a.Length, 6);
        Assert.Equal(3f, Vec3.Distance(Vec3.Zero, new Vec3(0f, 3f, 0f)), 5);
        Assert.Equal(1f, a.Normalized().Length, 6);
        Assert.True(Vec3.Zero.Normalized().IsZero());
    }
}
