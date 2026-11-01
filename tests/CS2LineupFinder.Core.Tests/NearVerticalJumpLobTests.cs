// <copyright file="NearVerticalJumpLobTests.cs" company="CS2LineupFinder">
// Regression: the elevation window must cover CS2's near-vertical jump lobs.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using CS2LineupFinder.Math;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// A CS2 manual jump throw routinely aims near-vertical: the recorded launch
/// (-0.3, -10.5, 943.5) with a 215 u/s carry is an 88.4 degree release. With
/// the old 85 degree ceiling the solver never sampled that part of the arc, so
/// the 60 s search ended hundreds of units short of the zone no matter how the
/// carry was modelled. The window now matches the engine's 89 degree pitch
/// limit.
/// </summary>
public sealed class NearVerticalJumpLobTests
{
    [Fact]
    public void JumpLobAtEightyEightDegrees_IsReachable()
    {
        var simulator = new GrenadeSimulator(new PhysicsParameters());
        var world = new MockWorldGeometry().AddInfiniteFloor(0f);
        var solver = new LineupSolver();

        var request = new LineupRequest
        {
            MapName = "test",
            GrenadeType = GrenadeType.Smoke,
            ThrowMode = ThrowMode.Jump,
            Button = ThrowButton.Primary,
            Origin = new ThrowOrigin
            {
                Feet = new Vec3(0f, 0f, 0f),
                Eyes = new Vec3(0f, 0f, 64f),

                // The carry this server's jump throws measure (215 u/s); the
                // simulation folds it in with the engine's 1.25 inheritance.
                Velocity = new Vec3(0f, 0f, 215f),
            },
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = new Vec3(110f, 0f, 0f),
                Radius = 40f,
            },
            Environment = new SimulationEnvironment
            {
                Gravity = 800f,
                TickInterval = 1f / 64f,
                MaxSteps = 4096,
                AirDrag = 0f,
            },
            MaxCandidates = 4096,
            YawStepDegrees = 1.0,
            ElevationStepDegrees = 1.5,
            YawSpanDegrees = 30.0,
            MinElevationDegrees = -15.0,
            MaxElevationDegrees = 89.0,
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
            outcome.Results[0].Pitch > 85f,
            $"expected a near-vertical solution, got pitch {outcome.Results[0].Pitch:0.0}");
        Assert.True(
            outcome.Results[0].TargetDistance <= 40f,
            $"solution missed the 40u zone by {outcome.Results[0].TargetDistance:0.#}u");
    }
}
