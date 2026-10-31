// <copyright file="TimeoutFallbackTests.cs" company="CS2LineupFinder">
// Regression coverage for the wall-clock timeout path.
// </copyright>

using System;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// Regression: the timeout used to be reported as a bare failure, discarding
/// every hit and miss the sliced search had already banked, so even a
/// <c>css_lf_find 60</c> that had been narrowing in answered "timed out" with
/// no aim point. The timeout must hand the player the closest banked landing.
/// </summary>
public sealed class TimeoutFallbackTests
{
    private const string TinyTimeoutConfig = """
        [solver]
        timeoutSeconds = 0.1
        """;

    [Fact]
    public async Task Timeout_ReportsTheClosestBankedMiss_AndDrawsItsBeam()
    {
        using var harness = new Harness(TinyTimeoutConfig);
        var session = harness.PrepareSession();

        var miss = new LineupSolution
        {
            Yaw = 21.5f,
            Pitch = -8.5f,
            ReleasePosition = new Vec3(0f, 0f, 64f),
            ImpactPosition = new Vec3(240f, 12f, 0f),
            TargetDistance = 42f,
        };
        harness.Bridge.UseCoreSimulator(new NeverFinishingSimulator(miss));

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);
        Assert.Null(outcome);

        for (var tick = 0; tick < 100 && session.LastOutcome is null; tick++)
        {
            harness.Service.TickSearches();
            await Task.Delay(10);
        }

        Assert.NotNull(session.LastOutcome);
        Assert.Equal(SolverStatus.Timeout, session.LastOutcome!.Status);
        Assert.NotEmpty(session.LastOutcome.Results);
        Assert.Same(miss, session.LastOutcome.Results[0]);

        Assert.Contains(harness.Messages.Lines, line => line.Contains("timed out after", StringComparison.Ordinal));
        Assert.Contains(harness.Messages.Lines, line => line.Contains("#1", StringComparison.Ordinal));
        Assert.Contains(harness.Messages.Lines, line => line.Contains("miss 42.0u", StringComparison.Ordinal));

        // The closest miss is the aim point: its beam and impact marker are
        // what the player nudges from.
        var beam = Assert.Single(harness.Visuals.Beams);
        Assert.Equal(Harness.Slot, beam.Slot);
        var marker = Assert.Single(harness.Visuals.Markers);
        Assert.Equal(miss.ImpactPosition, marker);
    }

    [Fact]
    public async Task Timeout_WithHitsBanked_ReportsThemWithTheBudgetNote()
    {
        using var harness = new Harness(TinyTimeoutConfig);
        var session = harness.PrepareSession();

        var hit = new LineupSolution
        {
            Yaw = 21.5f,
            Pitch = -8.5f,
            ReleasePosition = new Vec3(0f, 0f, 64f),
            ImpactPosition = new Vec3(498f, 3f, 0f),
            TargetDistance = 2.2f,
        };
        harness.Bridge.UseCoreSimulator(new NeverFinishingSimulator(hit, inZone: true));

        var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);
        Assert.Null(outcome);

        for (var tick = 0; tick < 100 && session.LastOutcome is null; tick++)
        {
            harness.Service.TickSearches();
            await Task.Delay(10);
        }

        Assert.NotNull(session.LastOutcome);
        Assert.Equal(SolverStatus.Success, session.LastOutcome!.Status);
        Assert.Same(hit, session.LastOutcome.Results[0]);

        Assert.Contains(harness.Messages.Lines, line => line.Contains("line-up(s) in", StringComparison.Ordinal));
        Assert.Contains(harness.Messages.Lines, line => line.Contains("budget expired after", StringComparison.Ordinal));
        Assert.Single(harness.Visuals.Beams);
    }

    /// <summary>
    /// A simulator whose sliced search never completes on its own; the outcome
    /// it hands back from <see cref="ILineupSearch.FinishNow"/> is what the
    /// timeout path has to report.
    /// </summary>
    private sealed class NeverFinishingSimulator : ITrajectorySimulator
    {
        private readonly LineupSolution _solution;
        private readonly bool _inZone;

        internal NeverFinishingSimulator(LineupSolution solution, bool inZone = false)
        {
            _solution = solution;
            _inZone = inZone;
        }

        /// <inheritdoc />
        public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public TrajectoryResult SimulateWithDiagnostics(ThrowParams parameters, IWorldGeometry world, out SimulationDiagnostics diagnostics)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<SolverOutcome> SolveLineupAsync(
            LineupRequest request,
            ILineupSolver solver,
            IWorldGeometry world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public ILineupSearch? BeginLineupSearch(LineupRequest request, ILineupSolver solver, IWorldGeometry world)
            => new NeverFinishingSearch(_solution, _inZone);
    }

    private sealed class NeverFinishingSearch : ILineupSearch
    {
        private readonly LineupSolution _solution;
        private readonly bool _inZone;

        internal NeverFinishingSearch(LineupSolution solution, bool inZone)
        {
            _solution = solution;
            _inZone = inZone;
        }

        /// <inheritdoc />
        public int Evaluated { get; private set; }

        /// <inheritdoc />
        public SolverOutcome? Step(double budgetMilliseconds)
        {
            Evaluated++;
            return null;
        }

        /// <inheritdoc />
        public SolverOutcome FinishNow() => _inZone
            ? SolverOutcome.Success(new[] { _solution }, TimeSpan.FromSeconds(1))
            : new SolverOutcome
            {
                Status = SolverStatus.NoSolution,
                Results = new[] { _solution },
                Elapsed = TimeSpan.FromSeconds(1),
                Message = "No candidate landed inside the zone.",
            };
    }
}
