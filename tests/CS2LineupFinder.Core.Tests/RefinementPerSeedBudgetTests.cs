// <copyright file="RefinementPerSeedBudgetTests.cs" company="CS2LineupFinder">
// Regression coverage for the per-seed refinement evaluation budget.
// </copyright>

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// Regression: the refinement's 60-evaluation budget was one counter shared
/// across every seed. The first seed burned all 60, and the remaining seeds -
/// often the ones whose basins hold bounce line-ups - were skipped without a
/// single forward simulation. The budget must be per seed.
/// </summary>
public sealed class RefinementPerSeedBudgetTests
{
    /// <summary>
    /// Solver stub emitting candidates spread across eight bearings, so the
    /// fallback pool yields the full four spatially distinct refinement seeds.
    /// </summary>
    private sealed class EightBearingSolver : ILineupSolver
    {
        private readonly List<AngleCandidate> _candidates = new();

        public EightBearingSolver()
        {
            for (var yaw = 0f; yaw <= 105f; yaw += 15f)
            {
                _candidates.Add(new AngleCandidate { Yaw = yaw, Pitch = -10f });
            }
        }

        public string Name => "eight-bearing-stub";

        public async IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
            LineupRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            foreach (var candidate in _candidates)
            {
                yield return candidate;
            }
        }
    }

    [Fact]
    public void Refinement_EverySeedGetsItsOwnEvaluationBudget()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
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
            // Every landing hits the walkable floor at z=0, so each one misses
            // this sunken zone by ~200u in altitude and the fallback pool feeds
            // the refinement stage four distinct seeds.
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
            MaxCandidates = 64,
        };

        ILineupSearch? search = simulator.BeginLineupSearch(
            request, new EightBearingSolver(), world);
        Assert.NotNull(search);

        SolverOutcome? outcome = null;
        for (var slice = 0; slice < 2000 && outcome is null; slice++)
        {
            outcome = search!.Step(8.0);
        }

        Assert.NotNull(outcome);

        IReadOnlyList<int> probes = simulator.RefinementProbeCountsBySeed;
        Assert.True(
            probes.Count >= 4,
            "expected all four refinement seeds to score at least one vertex, " +
            $"got seeds with probe counts [{string.Join(", ", probes)}]");

        // The failing behaviour: one shared counter means once a seed hits the
        // 60-evaluation cap, every later seed records zero probes. Under the
        // per-seed budget a seed that exhausts its own 60 must be followed by a
        // later seed that still simulated.
        for (var i = 0; i < probes.Count; i++)
        {
            Assert.True(
                probes[i] > 0,
                $"seed {i} simulated nothing; probe counts [{string.Join(", ", probes)}]");
            if (probes[i] >= RefinementBudgetCap && i + 1 < probes.Count)
            {
                Assert.True(
                    probes[i + 1] > 0,
                    $"seed {i} burned its whole budget and seed {i + 1} got none; " +
                    $"probe counts [{string.Join(", ", probes)}]");
            }
        }

        // Sanity: the probe seam and the global simulation counter agree that
        // the eight coarse candidates were on top of the refinement probes.
        Assert.Equal(
            8 + probes.Sum(),
            simulator.SimulationCount);
    }

    /// <summary>Mirrors GrenadeSimulator's private Refinement.MaxEvaluations.</summary>
    private const int RefinementBudgetCap = 60;
}
