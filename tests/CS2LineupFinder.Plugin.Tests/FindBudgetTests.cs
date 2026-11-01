using System;
using CS2LineupFinder.Plugin.Commands;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// A longer <c>!lf_find &lt;seconds&gt;</c> has to reach the search: the request
/// carries the explicit duration and the candidate cap grows with it. These tests
/// pin the regression where the duration was cleared from the session before the
/// request was built, so every search ran at the configured default.
/// </summary>
public sealed class FindBudgetTests
{
    [Theory]
    [InlineData(null, 64)]
    [InlineData(0.1, 64)]
    [InlineData(3.0, 630)]
    [InlineData(60.0, 12600)]
    public void CandidateBudget_ScalesWithDuration_ButNeverShrinksBelowConfig(double? seconds, int expected)
    {
        Assert.Equal(expected, SolverBudget.CandidateBudget(seconds, 64));
    }

    [Fact]
    public void CandidateBudget_RejectsAbsurdValues()
    {
        Assert.Equal(25_200, SolverBudget.CandidateBudget(120.0, 1));
        Assert.True(SolverBudget.CandidateBudget(120.0, 4096) <= 100_000);
    }

    [Fact]
    public void CandidateBudget_ForFiveMinutes_UsesTheRaisedCeiling()
    {
        Assert.Equal(63_000, SolverBudget.CandidateBudget(300.0, 4096));
        Assert.True(SolverBudget.CandidateBudget(300.0, 4096) <= 100_000);
    }

    [Fact]
    public void DensifiedSteps_ForFiveMinutes_FitsTheRaisedGridCeiling()
    {
        int budget = SolverBudget.CandidateBudget(300.0, 4096);
        var (yaw, elevation) = SolverBudget.DensifiedSteps(300.0, budget, 1.0, 1.5, 30.0, -15.0, 89.0);

        Assert.True(yaw >= 0.25 && elevation >= 0.25, $"steps {yaw:0.###}/{elevation:0.###} below the clamp");
        var cells = ((2 * (int)Math.Floor(30.0 / yaw)) + 1)
            * ((int)Math.Floor((89.0 + 15.0) / elevation) + 1);
        Assert.True(cells <= 65_536, $"grid {cells} exceeds the raised ceiling");
    }

    [Fact]
    public void DensifiedSteps_WithoutSeconds_KeepsTheConfiguredGrid()
    {
        var (yaw, elevation) = SolverBudget.DensifiedSteps(null, 64, 1.0, 1.5, 30.0, -15.0, 85.0);
        Assert.Equal(1.0, yaw);
        Assert.Equal(1.5, elevation);
    }

    [Fact]
    public void DensifiedSteps_ForSixtySeconds_FitsTheGridIntoTheCandidateBudget()
    {
        // The deployed config: 1.0/1.5 degree steps over a 30 degree span and a
        // 100 degree elevation window - about 4k cells, which capped every long
        // search no matter how long the player asked for.
        const double yawSpan = 30.0;
        const double minElevation = -15.0;
        const double maxElevation = 85.0;

        var (yaw, elevation) = SolverBudget.DensifiedSteps(
            60.0, SolverBudget.CandidateBudget(60.0, 4096), 1.0, 1.5, yawSpan, minElevation, maxElevation);

        Assert.True(yaw < 1.0, $"yaw step {yaw:0.###} was not densified");
        Assert.True(elevation < 1.5, $"elevation step {elevation:0.###} was not densified");

        // Mirrors the solver's grid arithmetic: the densified grid must fit the
        // budget the same command derived, otherwise the wall clock still idles.
        var budget = SolverBudget.CandidateBudget(60.0, 4096);
        var yawSteps = (int)Math.Floor(yawSpan / yaw);
        var elevationSteps = (int)Math.Floor((maxElevation - minElevation) / elevation);
        var cells = ((2 * yawSteps) + 1) * (elevationSteps + 1);
        Assert.True(cells <= budget, $"densified grid holds {cells} cells, budget is {budget}");
        Assert.True(cells > 4087, $"densified grid holds {cells} cells, no denser than the default grid");
    }

    [Fact]
    public async System.Threading.Tasks.Task Find_WithExplicitSeconds_RaisesTheCandidateBudget_AndKeepsTheDuration()
    {
        using var harness = new Harness();
        harness.PrepareSession();

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World, solveSecondsOverride: 60.0);

        Assert.NotNull(outcome);
        var request = harness.Service.LastBuiltRequest;
        Assert.NotNull(request);
        Assert.Equal(60.0, request!.SolveSeconds);
        Assert.Equal(12_600, request.MaxCandidates);
    }

    [Fact]
    public async System.Threading.Tasks.Task Find_WithoutSeconds_KeepsTheConfiguredDefaults()
    {
        using var harness = new Harness();
        harness.PrepareSession();

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);

        Assert.NotNull(outcome);
        var request = harness.Service.LastBuiltRequest;
        Assert.NotNull(request);
        Assert.Null(request!.SolveSeconds);
        Assert.Equal(64, request.MaxCandidates);
    }
}
