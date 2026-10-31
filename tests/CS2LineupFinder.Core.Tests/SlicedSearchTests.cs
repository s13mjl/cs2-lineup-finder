// <copyright file="SlicedSearchTests.cs" company="CS2LineupFinder">
// End-to-end coverage for the resumable search the game thread drives.
// </copyright>

using System.Collections.Generic;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using CS2LineupFinder.Math;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// The sliced search must land a throw inside a small zone that the three degree
/// coarse grid cannot hit directly, which is exactly the failure players saw as
/// "No candidate landed inside the zone after 64 simulations".
/// </summary>
public class SlicedSearchTests
{
    [Fact]
    public async Task SlicedSearch_FindsTheLineup_InASmallZone()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        LineupSolver solver = new();
        MockWorldGeometry world = new MockWorldGeometry().AddInfiniteFloor(0f);

        LineupRequest request = new()
        {
            MapName = "test",
            GrenadeType = GrenadeType.He,
            ThrowMode = ThrowMode.Stand,
            Button = ThrowButton.Primary,
            Origin = new ThrowOrigin
            {
                Feet = new Vec3(0f, 0f, 0f),
                Eyes = new Vec3(0f, 0f, 64f),
                Velocity = Vec3.Zero,
            },
            // With the faithful engine bounce (no tangential loss at impact) an
            // HE's fuse pops it mid-roll, so the detonation point is sensitive
            // to sub-unit roll decay. A larger zone keeps the test about the
            // sliced search itself, not about a boundary landing.
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = new Vec3(300f, 0f, 0f),
                Radius = 12f,
            },
            Environment = new SimulationEnvironment
            {
                Gravity = 800f,
                TickInterval = 1f / 64f,
                MaxSteps = 4096,
                AirDrag = 0f,
            },
            MaxCandidates = 64,
        };

        ILineupSearch? search = simulator.BeginLineupSearch(request, solver, world);
        Assert.NotNull(search);

        SolverOutcome? outcome = null;
        for (var slice = 0; slice < 500 && outcome is null; slice++)
        {
            outcome = search!.Step(8.0);
        }

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.Success, outcome!.Status);
        Assert.True(
            outcome.Results[0].TargetDistance <= 12f,
            $"best landing missed the 12u zone by {outcome.Results[0].TargetDistance:0.##}u");

        // The reported angles have to be replayable by the same simulator: the
        // contract pitch (positive looks up) drives the forward vector, so the
        // replayed aim must reproduce the simulated impact.
        LineupSolution best = outcome.Results[0];
        Assert.NotEqual(0f, best.Pitch);
        Vec3 forward = GrenadeSimulator.DirectionFromAnglesForTest(best.Yaw, best.Pitch);
        Assert.True(System.Math.Abs(forward.Z) > 0.5f, $"replayed aim is unexpectedly level, got {forward}");
    }

    [Fact]
    public async Task Evaluated_MatchesTheEvaluatedCandidateCount()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        LineupSolver solver = new();
        MockWorldGeometry world = new MockWorldGeometry().AddInfiniteFloor(0f);

        LineupRequest request = new()
        {
            MapName = "test",
            GrenadeType = GrenadeType.He,
            ThrowMode = ThrowMode.Stand,
            Button = ThrowButton.Primary,
            Origin = new ThrowOrigin
            {
                Feet = new Vec3(0f, 0f, 0f),
                Eyes = new Vec3(0f, 0f, 64f),
                Velocity = Vec3.Zero,
            },
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = new Vec3(300f, 0f, 0f),
                Radius = 12f,
            },
            Environment = new SimulationEnvironment
            {
                Gravity = 800f,
                TickInterval = 1f / 64f,
                MaxSteps = 4096,
                AirDrag = 0f,
            },
            MaxCandidates = 64,
        };

        ILineupSearch? search = simulator.BeginLineupSearch(request, solver, world);
        Assert.NotNull(search);

        SolverOutcome? outcome = null;
        for (var slice = 0; slice < 500 && outcome is null; slice++)
        {
            outcome = search!.Step(8.0);
        }

        Assert.NotNull(outcome);
        Assert.Equal(64, search!.Evaluated);
    }

    /// <summary>
    /// Regression: the host stops the search when its wall-clock budget expires.
    /// It used to return a bare timeout and drop everything the search had
    /// banked, so a 60 second search answered with no aim point at all. The
    /// banked outcome - here the closest misses - has to survive the stop.
    /// </summary>
    [Fact]
    public void FinishNow_MidSearch_ReturnsTheClosestBankedMiss()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        MockWorldGeometry world = new MockWorldGeometry().AddInfiniteFloor(0f);

        // One candidate whose landing sits outside the zone, and a zone that the
        // full search (refinement included) cannot reach either - the same
        // deterministic miss MissPoolVisibilityTests pins.
        var solver = new SingleCandidateSolver(0f, -10f);

        LineupRequest request = new()
        {
            MapName = "test",
            GrenadeType = GrenadeType.Smoke,
            ThrowMode = ThrowMode.Stand,
            Button = ThrowButton.Primary,
            Origin = new ThrowOrigin
            {
                Feet = new Vec3(0f, 0f, 0f),
                Eyes = new Vec3(0f, 0f, 64f),
                Velocity = Vec3.Zero,
            },
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = new Vec3(200f, 0f, -200f),
                Radius = 8f,
            },
            Environment = new SimulationEnvironment
            {
                Gravity = 800f,
                TickInterval = 1f / 64f,
                MaxSteps = 4096,
                AirDrag = 0f,
            },
            MaxCandidates = 4,
        };

        ILineupSearch? search = simulator.BeginLineupSearch(request, solver, world);
        Assert.NotNull(search);

        SolverOutcome? stepped = null;
        for (var slice = 0; slice < 20 && search!.Evaluated == 0 && stepped is null; slice++)
        {
            stepped = search.Step(8.0);
        }

        Assert.True(stepped is null, "the search finished before it could be interrupted");

        int simulationsBefore = simulator.SimulationCount;
        SolverOutcome partial = search!.FinishNow();

        Assert.Equal(SolverStatus.NoSolution, partial.Status);
        Assert.NotEmpty(partial.Results);
        Assert.True(
            partial.Results[0].TargetDistance > 8f,
            $"expected an out-of-zone miss, got {partial.Results[0].TargetDistance:0.##}u");

        // Finishing early must freeze the search: the same outcome comes back
        // and no further simulations are issued.
        SolverOutcome again = search.Step(500.0)!;
        Assert.Same(partial, again);
        Assert.Equal(simulationsBefore, simulator.SimulationCount);
    }

    /// <summary>
    /// The other half of the timeout contract: when in-zone hits are already
    /// banked, stopping early reports them as a success instead of throwing
    /// them away.
    /// </summary>
    [Fact]
    public void FinishNow_WithHitsBanked_ReturnsSuccess()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        LineupSolver solver = new();
        MockWorldGeometry world = new MockWorldGeometry().AddInfiniteFloor(0f);

        LineupRequest request = new()
        {
            MapName = "test",
            GrenadeType = GrenadeType.He,
            ThrowMode = ThrowMode.Stand,
            Button = ThrowButton.Primary,
            Origin = new ThrowOrigin
            {
                Feet = new Vec3(0f, 0f, 0f),
                Eyes = new Vec3(0f, 0f, 64f),
                Velocity = Vec3.Zero,
            },
            // A zone large enough to contain every landing: the first evaluated
            // candidate is already in-zone.
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = new Vec3(200f, 0f, 0f),
                Radius = 5000f,
            },
            Environment = new SimulationEnvironment
            {
                Gravity = 800f,
                TickInterval = 1f / 64f,
                MaxSteps = 4096,
                AirDrag = 0f,
            },
            MaxCandidates = 64,
        };

        ILineupSearch? search = simulator.BeginLineupSearch(request, solver, world);
        Assert.NotNull(search);

        SolverOutcome? stepped = null;
        for (var slice = 0; slice < 20 && search!.Evaluated == 0 && stepped is null; slice++)
        {
            stepped = search.Step(8.0);
        }

        Assert.Null(stepped);

        SolverOutcome partial = search!.FinishNow();

        Assert.Equal(SolverStatus.Success, partial.Status);
        Assert.NotEmpty(partial.Results);
        Assert.True(
            partial.Results[0].TargetDistance <= 5000f,
            $"expected an in-zone hit, got {partial.Results[0].TargetDistance:0.##}u");
    }

    /// <summary>Solver stub emitting exactly one deterministic candidate.</summary>
    private sealed class SingleCandidateSolver : ILineupSolver
    {
        private readonly AngleCandidate _candidate;

        public SingleCandidateSolver(float yaw, float pitch) =>
            _candidate = new AngleCandidate { Yaw = yaw, Pitch = pitch };

        public string Name => "single-candidate-stub";

        public async IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
            LineupRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return _candidate;
        }
    }
}
