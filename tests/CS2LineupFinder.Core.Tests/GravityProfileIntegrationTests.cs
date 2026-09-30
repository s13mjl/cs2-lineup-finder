using System;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using CS2LineupFinder.Math;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// Integration regression for the grenade-profile wiring: the forward simulator
/// must hand the authoritative <see cref="GrenadeProfile"/> to the inverse solver.
/// Before the fix, <see cref="GrenadeSimulator.SolveLineupAsync"/> called the
/// profile-less enumeration, which threw for a solver without an
/// <see cref="IVDataProvider"/>, so every real solve returned "no candidates" —
/// and the reachability pre-check ran with the 800 u/s^2 player gravity instead
/// of the 320 u/s^2 grenade value, refusing every legal zone beyond ~200 units.
/// </summary>
public class GravityProfileIntegrationTests
{
    [Fact]
    public async Task SolveLineupAsync_WithRealSolver_SeedsCandidatesWithProfileGravity()
    {
        GrenadeSimulator simulator = new(new PhysicsParameters());
        LineupSolver solver = new();
        MockWorldGeometry world = new MockWorldGeometry().AddInfiniteFloor(0f);

        // Player-gravity environment, exactly what the plugin's
        // PluginConfig.BuildEnvironment produces. The profile's GravityScale (0.4)
        // has to bring the seeding gravity down to 320 u/s^2.
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
                Center = new Vec3(700f, 0f, 0f),
                Radius = 100f,
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

        SolverOutcome outcome = await simulator.SolveLineupAsync(request, solver, world);

        // The zone is comfortably inside the ~14k-unit reach of a 675 u/s throw
        // at grenade gravity, so the solver must produce candidates and land
        // at least one inside the circle.
        Assert.NotEqual(SolverStatus.InvalidRequest, outcome.Status);
        Assert.DoesNotContain("no candidates", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(outcome.Results);
    }
}
