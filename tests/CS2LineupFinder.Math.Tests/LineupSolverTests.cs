using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using Xunit;

namespace CS2LineupFinder.Math.Tests;

/// <summary>
/// Shared scenario numbers. A 400 u/s release under 800 u/s^2 gravity reaches exactly 200
/// units on the level, which keeps every expected value here a small integer.
/// </summary>
internal static class Fixture
{
    internal const double Speed = 400d;
    internal const double Gravity = 800d;
    internal const double MaxLevelReach = 200d;

    internal static readonly Vec3 Eyes = new(0f, 0f, 64f);

    internal static GrenadeProfile Smoke() => new()
    {
        ItemName = "weapon_smokegrenade",
        GrenadeType = GrenadeType.Smoke,
        PrimaryThrowVelocity = (float)Speed,
        SecondaryThrowVelocity = 220f,
        BothButtonsThrowVelocity = 300f,
    };

    internal static LineupRequest Request(Vec3 center, float radius = 8f, ThrowButton button = ThrowButton.Primary) =>
        new()
        {
            MapName = "de_mirage",
            GrenadeType = GrenadeType.Smoke,
            ThrowMode = ThrowMode.Stand,
            Button = button,
            Origin = new ThrowOrigin { Feet = new Vec3(0f, 0f, 0f), Eyes = Eyes },
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = center,
                Radius = radius,
            },
            Environment = new SimulationEnvironment { Gravity = (float)Gravity },
        };
}

/// <summary>Behaviour of the two stage inverse solver against a mock world.</summary>
public sealed class LineupSolverTests
{
    private static readonly Vec3 OnEnvelope = new(200f, 0f, 64f);
    private static readonly Vec3 TwoArcsTarget = new(180f, 0f, 64f);

    [Fact]
    public void NameIdentifiesTheStrategy()
    {
        Assert.Equal("coarse-to-fine-v1", new LineupSolver().Name);
    }

    /// <summary>
    /// Acceptance case 1: on an open field the angles the solver reports must reproduce the
    /// zone centre when substituted back into the closed-form trajectory.
    /// </summary>
    [Fact]
    public async Task SolvesTheAnalyticThrowToUnderAUnit()
    {
        var mock = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: OnEnvelope.Z);
        var outcome = await new LineupSolver().SolveAsync(Fixture.Request(OnEnvelope), mock, Fixture.Smoke());

        Assert.Equal(SolverStatus.Success, outcome.Status);
        Assert.True(
            outcome.Results.Count is > 0 and <= 5,
            $"the report must carry between one and five angle sets, got {outcome.Results.Count}");
        var best = outcome.Results[0];
        Assert.True(best.TargetDistance < 1f, $"expected a clean hit, got {best.TargetDistance} units of error");

        // Substitute the reported angles into R = v^2 sin(2*theta) / g along the reported yaw.
        var elevation = AngleMath.ElevationFromPitch(best.Pitch);
        var predicted = Ballistics.PredictImpact(
            Fixture.Eyes,
            best.Yaw,
            elevation,
            Fixture.Speed,
            Fixture.Gravity,
            OnEnvelope.Z);
        Assert.True(
            Vec3.Distance(predicted, OnEnvelope) < 1f,
            $"reported angles land {Vec3.Distance(predicted, OnEnvelope):0.###} units from the zone centre");
    }

    /// <summary>
    /// Acceptance case 2: one landing spot is reachable by several distinct throws, and the
    /// report has to hand back the alternatives rather than only the first one it found.
    /// </summary>
    [Fact]
    public async Task ReportsTheLowAndHighArcAsAlternatives()
    {
        var zone = Fixture.Request(TwoArcsTarget).TargetZone;
        var mock = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: TwoArcsTarget.Z);
        var outcome = await new LineupSolver().SolveAsync(Fixture.Request(TwoArcsTarget), mock, Fixture.Smoke());

        Assert.Equal(SolverStatus.Success, outcome.Status);
        Assert.True(outcome.Results.Count >= 2, $"expected several arcs, got {outcome.Results.Count}");
        Assert.True(outcome.Results.Count <= 5, "the report is capped at five alternatives");

        var elevations = outcome.Results.Select(r => AngleMath.ElevationFromPitch(r.Pitch)).ToArray();
        Assert.True(
            elevations.Max() - elevations.Min() > 10d,
            "the alternatives should span the flat and the lofted arc, not one arc plus noise");

        Assert.All(outcome.Results, solution => Assert.True(zone.Contains(solution.ImpactPosition)));
        Assert.All(outcome.Results, solution => Assert.Equal(Fixture.Eyes, solution.ReleasePosition));

        // Ranked by error, best first.
        var distances = outcome.Results.Select(r => (double)r.TargetDistance).ToArray();
        Assert.True(distances.SequenceEqual(distances.OrderBy(d => d)), "results must be ordered by error");
    }

    /// <summary>
    /// Acceptance case 3: the mock simulator returns a stopped landing whenever the arc is
    /// below the wall, so the only throws that report success are the ones that clear it.
    /// </summary>
    [Fact]
    public async Task GoesOverAnObstacleInsteadOfReportingTheBlockedArc()
    {
        var request = Fixture.Request(TwoArcsTarget);
        var blocked = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: TwoArcsTarget.Z)
        {
            WallDistance = 90d,

            // The flat arc is only 28 units high when it passes x=90; the lofted arc is 72.
            WallTopZ = Fixture.Eyes.Z + 45d,
        };

        var blockedOutcome = await new LineupSolver().SolveAsync(request, blocked, Fixture.Smoke());
        Assert.Equal(SolverStatus.Success, blockedOutcome.Status);
        Assert.All(
            blockedOutcome.Results,
            solution => Assert.True(
                AngleMath.ElevationFromPitch(solution.Pitch) > 45d,
                $"elevation {AngleMath.ElevationFromPitch(solution.Pitch):0.#} would be stopped by the wall"));

        // Same zone on open ground prefers the cheaper flat arc, which proves the wall bit.
        var open = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: TwoArcsTarget.Z);
        var openOutcome = await new LineupSolver().SolveAsync(request, open, Fixture.Smoke());
        Assert.Equal(SolverStatus.Success, openOutcome.Status);
        Assert.True(
            AngleMath.ElevationFromPitch(openOutcome.Results[0].Pitch) < 45d,
            "without the wall the flat arc is the closest thing to the analytic seed");
    }

    /// <summary>Acceptance case 4: a zone beyond the vacuum envelope fails with a usable reason.</summary>
    [Fact]
    public async Task ReportsUnreachableZonesWithoutSpendingTheBudget()
    {
        var mock = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: 64f);
        var outcome = await new LineupSolver().SolveAsync(
            Fixture.Request(new Vec3(2000f, 0f, 64f)),
            mock,
            Fixture.Smoke());

        Assert.Equal(SolverStatus.NoSolution, outcome.Status);
        Assert.Empty(outcome.Results);
        Assert.NotNull(outcome.Message);
        Assert.Contains("maximum vacuum reach", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, mock.EvaluationCount);
    }

    /// <summary>Acceptance case 5: the briefed ceiling on forward-simulator calls is honoured.</summary>
    [Fact]
    public async Task NeverExceedsTwoThousandEvaluations()
    {
        // Every throw reports the same hopeless landing, so the search runs to the cap.
        var mock = new AlwaysBlockedEvaluator(new Vec3(500f, 0f, 64f));
        var outcome = await new LineupSolver().SolveAsync(Fixture.Request(TwoArcsTarget), mock, Fixture.Smoke());

        Assert.True(mock.EvaluationCount <= 2000, $"the solver used {mock.EvaluationCount} evaluations");
        Assert.Equal(SolverStatus.NoSolution, outcome.Status);
        Assert.Contains("closest landing", outcome.Message, StringComparison.Ordinal);
        Assert.True(mock.EvaluationCount > 800, "the coarse grid alone should have been walked");
    }

    [Fact]
    public async Task HonoursATighterEvaluationBudget()
    {
        var mock = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: TwoArcsTarget.Z);
        var solver = new LineupSolver(options: new SolverOptions { MaxEvaluations = 100 });
        var outcome = await solver.SolveAsync(Fixture.Request(TwoArcsTarget), mock, Fixture.Smoke());

        Assert.InRange(mock.EvaluationCount, 1, 100);
        Assert.Equal(SolverStatus.Success, outcome.Status);
    }

    /// <summary>Acceptance case 6: the three second ceiling returns the best angle found so far.</summary>
    [Fact]
    public async Task ReturnsTheBestSoFarWhenTheClockRunsOut()
    {
        var mock = new AlwaysBlockedEvaluator(
            new Vec3(500f, 0f, 64f),
            TimeSpan.FromMilliseconds(3));
        var solver = new LineupSolver(options: new SolverOptions { Timeout = TimeSpan.FromMilliseconds(1) });
        var outcome = await solver.SolveAsync(Fixture.Request(TwoArcsTarget), mock, Fixture.Smoke());

        Assert.Equal(SolverStatus.Timeout, outcome.Status);
        Assert.True(mock.EvaluationCount <= 3, $"the clock should have stopped the search, got {mock.EvaluationCount} probes");
        Assert.Contains("closest landing", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsRequestsThatCannotBeSolved()
    {
        var mock = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: 64f);
        var smoke = Fixture.Smoke();

        var noGravity = Fixture.Request(TwoArcsTarget) with { Environment = new SimulationEnvironment { Gravity = 0f } };
        var gravityOutcome = await new LineupSolver().SolveAsync(noGravity, mock, smoke);
        Assert.Equal(SolverStatus.InvalidRequest, gravityOutcome.Status);
        Assert.Contains("Gravity", gravityOutcome.Message, StringComparison.Ordinal);

        var noRadius = Fixture.Request(TwoArcsTarget, radius: 0f);
        var radiusOutcome = await new LineupSolver().SolveAsync(noRadius, mock, smoke);
        Assert.Equal(SolverStatus.InvalidRequest, radiusOutcome.Status);
        Assert.Contains("positive Radius", radiusOutcome.Message, StringComparison.Ordinal);

        var onTopOfTheThrower = Fixture.Request(new Vec3(0f, 0f, 64f));
        var bearingOutcome = await new LineupSolver().SolveAsync(onTopOfTheThrower, mock, smoke);
        Assert.Equal(SolverStatus.InvalidRequest, bearingOutcome.Status);

        var underhand = Fixture.Request(TwoArcsTarget, button: ThrowButton.Secondary) with
        {
            TargetZone = Fixture.Request(new Vec3(160f, 0f, 64f)).TargetZone,
        };
        var underhandMock = new VacuumThrowMock(Fixture.Eyes, 220d, Fixture.Gravity, planeZ: 160f);
        var underhandOutcome = await new LineupSolver().SolveAsync(underhand, underhandMock, smoke);
        Assert.Equal(SolverStatus.NoSolution, underhandOutcome.Status);
        Assert.Contains("button Secondary", underhandOutcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SolvesRectangularZonesToo()
    {
        var request = Fixture.Request(TwoArcsTarget) with
        {
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Rectangle,
                Center = TwoArcsTarget,
                Width = 32f,
                Height = 16f,
            },
        };

        var mock = new VacuumThrowMock(Fixture.Eyes, Fixture.Speed, Fixture.Gravity, planeZ: TwoArcsTarget.Z);
        var outcome = await new LineupSolver().SolveAsync(request, mock, Fixture.Smoke());

        Assert.Equal(SolverStatus.Success, outcome.Status);
        Assert.True(outcome.Results[0].TargetDistance < 1f);
    }

    [Fact]
    public async Task EnumeratesAnalyticCandidatesForThePhysicsLayerToSimulate()
    {
        var solver = new LineupSolver(new StubVDataProvider(Fixture.Smoke()));
        var candidates = new List<AngleCandidate>();
        await foreach (var candidate in solver.EnumerateCandidatesAsync(Fixture.Request(TwoArcsTarget)))
        {
            candidates.Add(candidate);
        }

        Assert.NotEmpty(candidates);

        // The head of the stream is the analytic seed: due north at the flat arc.
        Assert.Equal(0d, candidates[0].Yaw, 1);
        Assert.True(candidates[0].HeuristicCost == 0f, "seeds carry no heuristic penalty");
        Assert.True(candidates[0].Pitch < 0f, "a lofted seed must report a negative engine pitch");

        var gridCosts = candidates.Skip(2).Select(c => (double)c.HeuristicCost).ToArray();
        Assert.True(gridCosts.SequenceEqual(gridCosts.OrderBy(c => c)), "grid candidates must be ranked by predicted error");
    }

    [Fact]
    public async Task EnumeratingCandidatesWithoutAProfileSourceIsAConfigurationError()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (var _ in new LineupSolver().EnumerateCandidatesAsync(Fixture.Request(TwoArcsTarget)))
                {
                }
            });

        Assert.Contains("IVDataProvider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SurfacesASimulatorThatRefusesToAnswer()
    {
        var solver = new LineupSolver();
        var outcome = await solver.SolveAsync(Fixture.Request(TwoArcsTarget), new ThrowingEvaluator(), Fixture.Smoke());
        Assert.Equal(SolverStatus.Failed, outcome.Status);
        Assert.Contains("unexpected error", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RespectsTheCancellationRequestedByTheCaller()
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        var mock = new AlwaysBlockedEvaluator(
            new Vec3(500f, 0f, 64f),
            TimeSpan.FromMilliseconds(5));

        var outcome = await new LineupSolver().SolveAsync(
            Fixture.Request(TwoArcsTarget),
            mock,
            Fixture.Smoke(),
            timeout.Token);

        Assert.Equal(SolverStatus.Cancelled, outcome.Status);
    }

    private sealed class ThrowingEvaluator : IImpactEvaluator
    {
        public int EvaluationCount => 0;

        public bool TryEvaluate(double yawDegrees, double elevationDegrees, out Vec3 impact)
            => throw new InvalidOperationException("the stub simulator is not wired up");
    }
}
