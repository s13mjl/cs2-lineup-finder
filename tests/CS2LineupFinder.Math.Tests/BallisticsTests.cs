using System;
using CS2LineupFinder.Contracts;
using Xunit;

namespace CS2LineupFinder.Math.Tests;

/// <summary>
/// The vacuum formulae that seed the search. If these are wrong every coarse grid sample is
/// aimed at the wrong bearing, so they are checked against the textbook closed forms.
/// </summary>
public sealed class BallisticsTests
{
    private const double Speed = 400d;
    private const double Gravity = 800d;

    private static readonly Vec3 Origin = new(0f, 0f, 64f);

    [Fact]
    public void LevelRangePeaksAtFortyFiveDegrees()
    {
        Assert.Equal(200d, Ballistics.LevelRange(Speed, 45d, Gravity), 6);
        Assert.Equal(200d, Ballistics.MaxLevelRange(Speed, Gravity), 6);
    }

    [Fact]
    public void LevelRangeIsSymmetricAroundFortyFiveDegrees()
    {
        Assert.Equal(Ballistics.LevelRange(Speed, 30d, Gravity), Ballistics.LevelRange(Speed, 60d, Gravity), 6);
    }

    [Fact]
    public void SolveElevationsReturnsBothArcsAndPredictsTheRequestedRange()
    {
        var count = Ballistics.SolveElevations(180d, 0d, Speed, Gravity, out var low, out var high);
        Assert.Equal(2, count);
        Assert.True(low < 45d && high > 45d, "the flat arc must sit below 45 degrees and the lofted arc above it");

        // Feeding either arc back into the range formula must reproduce the horizontal distance.
        Assert.Equal(180d, Ballistics.LevelRange(Speed, low, Gravity), 4);
        Assert.Equal(180d, Ballistics.LevelRange(Speed, high, Gravity), 4);
    }

    [Fact]
    public void SolveElevationsCollapsesToASingleArcOnTheEnvelope()
    {
        var count = Ballistics.SolveElevations(200d, 0d, Speed, Gravity, out var low, out var high);
        Assert.Equal(1, count);
        Assert.Equal(45d, low, 6);
        Assert.True(double.IsNaN(high));
    }

    [Fact]
    public void SolveElevationsHasNoSolutionBeyondMaximumReach()
    {
        Assert.Equal(0, Ballistics.SolveElevations(201d, 0d, Speed, Gravity, out _, out _));
        Assert.Equal(200d, Ballistics.MaxRange(Speed, Gravity, 0d), 6);
    }

    [Fact]
    public void MaximumReachShrinksWhenTheTargetIsAboveTheThrower()
    {
        var flat = Ballistics.MaxRange(Speed, Gravity, 0d);
        var above = Ballistics.MaxRange(Speed, Gravity, 64d);
        Assert.True(above < flat, "climbing 64 units has to cost horizontal range");
        Assert.Equal(0, Ballistics.SolveElevations(flat, 64d, Speed, Gravity, out _, out _));
    }

    [Fact]
    public void PredictImpactLandsWhereTheAnalyticArcPromised()
    {
        Ballistics.SolveElevations(180d, 0d, Speed, Gravity, out var low, out _);
        var impact = Ballistics.PredictImpact(Origin, 0d, low, Speed, Gravity, Origin.Z);
        Assert.Equal(180d, (double)impact.X, 3);
        Assert.Equal(0d, (double)impact.Y, 3);
        Assert.Equal((double)Origin.Z, (double)impact.Z, 3);
    }

    [Fact]
    public void PredictImpactFollowsYaw()
    {
        Ballistics.SolveElevations(180d, 0d, Speed, Gravity, out var low, out _);
        var north = Ballistics.PredictImpact(Origin, 90d, low, Speed, Gravity, Origin.Z);
        Assert.Equal(0d, (double)north.X, 3);
        Assert.Equal(180d, (double)north.Y, 3);
    }

    [Fact]
    public void PredictImpactIsUnreachableWhenThePlaneIsAboveTheCeiling()
    {
        Assert.True(double.IsNaN(Ballistics.TimeToPlane(0d, Speed, Gravity, 500d)));
        Assert.Equal(Vec3.Zero, Ballistics.PredictImpact(Origin, 0d, 0d, Speed, Gravity, Origin.Z + 500d));
    }

    [Fact]
    public void SpeedForSelectsTheButtonColumnOfTheProfile()
    {
        var profile = Profile(400f, 220f, 300f);
        Assert.Equal(400d, Ballistics.SpeedFor(profile, ThrowButton.Primary), 3);
        Assert.Equal(220d, Ballistics.SpeedFor(profile, ThrowButton.Secondary), 3);
        Assert.Equal(300d, Ballistics.SpeedFor(profile, ThrowButton.Both), 3);
    }

    [Fact]
    public void GravityForAppliesTheProfileScale()
    {
        var environment = new SimulationEnvironment { Gravity = 800f };
        Assert.Equal(800d, Ballistics.GravityFor(environment, Profile(400f, 220f, 300f)), 3);
        Assert.Equal(320d, Ballistics.GravityFor(environment, Profile(400f, 220f, 300f, gravityScale: 0.4f)), 2);
    }

    private static GrenadeProfile Profile(float primary, float secondary, float both, float gravityScale = 1f) =>
        new()
        {
            ItemName = "weapon_smokegrenade",
            GrenadeType = GrenadeType.Smoke,
            PrimaryThrowVelocity = primary,
            SecondaryThrowVelocity = secondary,
            BothButtonsThrowVelocity = both,
            GravityScale = gravityScale,
        };
}
