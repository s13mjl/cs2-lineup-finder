// <copyright file="MissPoolVisibilityTests.cs" company="CS2LineupFinder">
// Regression coverage for the fallback pool the refinement seeds from.
// </copyright>

using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// Regression: the old +/-64u landing-height window made EvaluateCandidate
/// return null for landings far above or below the zone plane, so those misses
/// never reached the fallback pool. A bounce line-up whose nearest coarse
/// neighbours miss the zone in altitude was reported as "No candidate landed
/// inside the zone ... closest 316u" even though the refinement - which seeds
/// from that pool - could have walked a nudge into the zone. The pool must see
/// every simulated landing.
/// </summary>
public sealed class MissPoolVisibilityTests
{
    /// <summary>Solver stub emitting exactly one deterministic candidate.</summary>
    private sealed class SingleCandidateSolver : ILineupSolver
    {
        private readonly AngleCandidate _candidate;

        public SingleCandidateSolver(float yaw, float pitch) =>
            _candidate = new AngleCandidate { Yaw = yaw, Pitch = pitch };

        public string Name => "single-candidate-stub";

        public async IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
            LineupRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            yield return _candidate;
        }
    }

    [Fact]
    public void SlicedSearch_OffAltitudeMisses_ReachTheFallbackPool()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        MockWorldGeometry world = new MockWorldGeometry().AddInfiniteFloor(0f);

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
            // The zone plane sits 200 units below the only walkable surface, so
            // every landing misses the zone by ~200u in altitude. Under the old
            // Z-window such misses were discarded and the outcome carried no
            // fallback results at all.
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

        ILineupSearch? search = simulator.BeginLineupSearch(
            request, new SingleCandidateSolver(0f, -10f), world);
        Assert.NotNull(search);

        SolverOutcome? outcome = null;
        for (var slice = 0; slice < 200 && outcome is null; slice++)
        {
            outcome = search!.Step(8.0);
        }

        Assert.NotNull(outcome);
        Assert.Equal(SolverStatus.NoSolution, outcome!.Status);
        Assert.NotEmpty(outcome.Results);
        Assert.True(
            outcome.Results[0].TargetDistance > 8f,
            $"expected an out-of-zone miss, got {outcome.Results[0].TargetDistance:0.##}u");
    }
}
