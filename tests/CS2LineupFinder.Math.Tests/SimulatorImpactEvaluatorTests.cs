using System;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using Xunit;
using static System.Math;

namespace CS2LineupFinder.Math.Tests;

/// <summary>
/// The forward simulator the brief asks the solver to be tested against, without any
/// engine or collision maths: it integrates <see cref="ThrowParams.Velocity"/> in a
/// straight vacuum line until the grenade reaches a horizontal plane.
/// </summary>
internal sealed class VacuumSimulatorStub : ITrajectorySimulator
{
    private readonly double _planeZ;
    private readonly double _gravity;

    internal VacuumSimulatorStub(double planeZ, double gravity = Fixture.Gravity)
    {
        _planeZ = planeZ;
        _gravity = gravity;
    }

    /// <summary>The parameters of the most recent call, so tests can inspect the resolution.</summary>
    internal ThrowParams? LastParameters { get; private set; }

    /// <summary>The geometry handed to the most recent call.</summary>
    internal IWorldGeometry? LastWorld { get; private set; }

    internal int SimulateCount { get; private set; }

    /// <summary>Stand in for a simulator that refuses to answer.</summary>
    internal bool ReturnNothing { get; set; }

    public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world)
    {
        SimulateCount++;
        LastParameters = parameters;
        LastWorld = world;
        if (ReturnNothing)
        {
            return null!;
        }

        var deltaZ = _planeZ - parameters.Origin.Z;
        var vertical = parameters.Velocity.Z;
        var discriminant = (vertical * vertical) - (2d * _gravity * deltaZ);
        var flightTime = discriminant < 0d ? 0d : (vertical + Sqrt(discriminant)) / _gravity;

        var position = new Vec3(
            (float)(parameters.Origin.X + (parameters.Velocity.X * flightTime)),
            (float)(parameters.Origin.Y + (parameters.Velocity.Y * flightTime)),
            (float)_planeZ);

        var impact = new FinalImpact(
            position,
            Vec3.UnitZ,
            bounceCount: 0,
            time: (float)flightTime,
            detonated: false,
            detonatedType: parameters.GrenadeType);
        var inZone = parameters.ZoneTest?.Invoke(position) ?? false;
        var path = new[]
        {
            new TrajectoryPoint(parameters.Origin, parameters.Velocity, 0f, 0),
            new TrajectoryPoint(position, parameters.Velocity, (float)flightTime, 0),
        };
        return new TrajectoryResult(path, impact, inZone);
    }

    public TrajectoryResult SimulateWithDiagnostics(
        ThrowParams parameters,
        IWorldGeometry world,
        out SimulationDiagnostics diagnostics)
    {
        var result = Simulate(parameters, world);
        var dx = result.Impact.Position.X - parameters.Origin.X;
        var dy = result.Impact.Position.Y - parameters.Origin.Y;
        var horizontal = (float)Sqrt((dx * dx) + (dy * dy));
        var apex = (parameters.Velocity.Z * parameters.Velocity.Z) / (2f * (float)_gravity);
        diagnostics = new SimulationDiagnostics(
            apexHeight: apex,
            horizontalDistance: horizontal,
            pathLength: horizontal,
            stepsTaken: result.Points.Count,
            substeps: result.Points.Count);
        return result;
    }

    public Task<SolverOutcome> SolveLineupAsync(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Core owns the driver; math-core must reach the simulator through IImpactEvaluator instead.");
}

/// <summary>Open ground: nothing ever intercepts a sweep, and the floor is one flat plane.</summary>
internal sealed class FlatWorld : IWorldGeometry
{
    private readonly double _floorZ;

    internal FlatWorld(double floorZ) => _floorZ = floorZ;

    internal int SweepCount { get; private set; }

    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1)
    {
        SweepCount++;
        return Free(origin, direction, maxDistance);
    }

    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1)
    {
        SweepCount++;
        return Free(origin, direction, maxDistance);
    }

    public TraceHit ProbeGround(Vec3 position, float maxDrop) =>
        new(true, new Vec3(position.X, position.Y, (float)_floorZ), Vec3.UnitZ, 1f);

    private static TraceHit Free(Vec3 origin, Vec3 direction, float maxDistance)
    {
        var length = direction.Length;
        var travel = length > Vec3.DefaultEpsilon ? maxDistance / length : 0f;
        return new TraceHit(false, origin + (direction * travel), Vec3.UnitZ, 1f);
    }
}

/// <summary>
/// Tests for <see cref="SimulatorImpactEvaluator"/>, the only place in math-core that turns a
/// (yaw, elevation) pair into a contractual throw. One probe has to mean exactly one forward call.
/// </summary>
public sealed class SimulatorImpactEvaluatorTests
{
    private static readonly Vec3 OnEnvelope = new(200f, 0f, 64f);

    [Fact]
    public void ResolvesTheAnglePairIntoAForwardThrow()
    {
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        var request = Fixture.Request(OnEnvelope);
        var profile = Fixture.Smoke();
        var evaluator = new SimulatorImpactEvaluator(stub, new FlatWorld(OnEnvelope.Z), request, profile);

        Assert.True(evaluator.TryEvaluate(yawDegrees: 30d, elevationDegrees: 20d, out _));

        var sent = stub.LastParameters!;
        Assert.Equal(Fixture.Eyes, sent.Origin);
        Assert.Equal(GrenadeType.Smoke, sent.GrenadeType);
        Assert.Equal(ThrowMode.Stand, sent.Mode);
        Assert.Same(profile, sent.Profile);
        Assert.Equal(1f, sent.ThrowStrength);
        Assert.Equal(64, sent.GameTickRate);

        var expected = AngleMath.DirectionFromElevation(30d, 20d).Normalized() * (float)Fixture.Speed;
        AssertNear(expected.X, sent.Velocity.X, "velocity X");
        AssertNear(expected.Y, sent.Velocity.Y, "velocity Y");
        AssertNear(expected.Z, sent.Velocity.Z, "velocity Z");
    }

    [Fact]
    public void CarriesTheThrowerVelocityIntoTheRelease()
    {
        var request = Fixture.Request(OnEnvelope) with
        {
            Origin = new ThrowOrigin
            {
                Feet = Vec3.Zero,
                Eyes = Fixture.Eyes,
                Velocity = new Vec3(0f, 100f, 0f),
            },
        };
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        new SimulatorImpactEvaluator(stub, new FlatWorld(OnEnvelope.Z), request, Fixture.Smoke())
            .TryEvaluate(0d, 0d, out _);

        var sent = stub.LastParameters!;
        AssertNear(400d, sent.Velocity.X, "velocity X");
        AssertNear(100d, sent.Velocity.Y, "the inherited sideways speed");
        AssertNear(0d, sent.Velocity.Z, "velocity Z");
    }

    [Theory]
    [InlineData(ThrowButton.Primary, 400d)]
    [InlineData(ThrowButton.Secondary, 220d)]
    [InlineData(ThrowButton.Both, 300d)]
    public void PicksTheReleaseSpeedFromTheProfileColumnForThatButton(ThrowButton button, double expectedSpeed)
    {
        var request = Fixture.Request(OnEnvelope) with { Button = button };
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        new SimulatorImpactEvaluator(stub, new FlatWorld(OnEnvelope.Z), request, Fixture.Smoke())
            .TryEvaluate(0d, 30d, out _);

        AssertNear(expectedSpeed, stub.LastParameters!.Velocity.Length, "release speed");
    }

    [Fact]
    public void ScalesTheReleaseByThrowStrength()
    {
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        var request = Fixture.Request(OnEnvelope);
        new SimulatorImpactEvaluator(stub, new FlatWorld(OnEnvelope.Z), request, Fixture.Smoke(), throwStrength: 0.5f)
            .TryEvaluate(0d, 0d, out _);

        var sent = stub.LastParameters!;
        AssertNear(200d, sent.Velocity.Length, "half strength is half speed");
        Assert.Equal(0.5f, sent.ThrowStrength);
    }

    [Theory]
    [InlineData(0d, 45d)]
    [InlineData(37d, 22d)]
    [InlineData(-120d, 61d)]
    public void LandsWhereTheClosedFormPredicts(double yaw, double elevation)
    {
        var request = Fixture.Request(OnEnvelope);
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        var world = new FlatWorld(OnEnvelope.Z);
        var evaluator = new SimulatorImpactEvaluator(stub, world, request, Fixture.Smoke());

        Assert.True(evaluator.TryEvaluate(yaw, elevation, out var impact));

        var predicted = Ballistics.PredictImpact(
            Fixture.Eyes,
            yaw,
            elevation,
            Fixture.Speed,
            Fixture.Gravity,
            OnEnvelope.Z);
        AssertNear(predicted.X, impact.X, "landing X");
        AssertNear(predicted.Y, impact.Y, "landing Y");
        AssertNear(OnEnvelope.Z, impact.Z, "landing Z");
    }

    [Fact]
    public void OneProbeIsExactlyOneForwardSimulation()
    {
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        var world = new FlatWorld(OnEnvelope.Z);
        var evaluator = new SimulatorImpactEvaluator(
            stub,
            world,
            Fixture.Request(OnEnvelope),
            Fixture.Smoke());

        evaluator.TryEvaluate(0d, 40d, out _);
        evaluator.TryEvaluate(1d, 41d, out _);
        evaluator.TryEvaluate(2d, 42d, out _);

        Assert.Equal(3, evaluator.EvaluationCount);
        Assert.Equal(3, stub.SimulateCount);
        Assert.Same(world, stub.LastWorld);
    }

    [Fact]
    public void HandsTheZoneTestToTheSimulator()
    {
        var zone = Fixture.Request(OnEnvelope, radius: 8f).TargetZone;
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        new SimulatorImpactEvaluator(stub, new FlatWorld(OnEnvelope.Z), Fixture.Request(OnEnvelope), Fixture.Smoke())
            .TryEvaluate(0d, 45d, out _);

        var test = stub.LastParameters!.ZoneTest!;
        Assert.NotNull(test);
        Assert.True(test(OnEnvelope));
        Assert.False(test(OnEnvelope + (Vec3.UnitX * 50f)));
        Assert.True(zone.Contains(OnEnvelope), "the fixture zone has to contain its own centre");
    }

    [Theory]
    [InlineData(1d / 64d, 64)]
    [InlineData(1d / 128d, 128)]
    [InlineData(1d / 30d, 30)]
    public void DerivesTheTickRateFromTheEnvironment(double tickInterval, int expected)
    {
        var request = Fixture.Request(OnEnvelope) with
        {
            Environment = new SimulationEnvironment { Gravity = 800f, TickInterval = (float)tickInterval },
        };
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        new SimulatorImpactEvaluator(stub, new FlatWorld(OnEnvelope.Z), request, Fixture.Smoke())
            .TryEvaluate(0d, 45d, out _);

        Assert.Equal(expected, stub.LastParameters!.GameTickRate);
    }

    [Fact]
    public void TreatsASilentSimulatorAsAnUnusableThrow()
    {
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z) { ReturnNothing = true };
        var evaluator = new SimulatorImpactEvaluator(
            stub,
            new FlatWorld(OnEnvelope.Z),
            Fixture.Request(OnEnvelope),
            Fixture.Smoke());

        Assert.False(evaluator.TryEvaluate(0d, 45d, out var impact));
        Assert.Equal(Vec3.Zero, impact);
    }

    [Fact]
    public void RejectsMissingCollaborators()
    {
        var stub = new VacuumSimulatorStub(planeZ: OnEnvelope.Z);
        var world = new FlatWorld(OnEnvelope.Z);
        var request = Fixture.Request(OnEnvelope);
        var profile = Fixture.Smoke();

        Assert.Throws<ArgumentNullException>(() => new SimulatorImpactEvaluator(null!, world, request, profile));
        Assert.Throws<ArgumentNullException>(() => new SimulatorImpactEvaluator(stub, null!, request, profile));
        Assert.Throws<ArgumentNullException>(() => new SimulatorImpactEvaluator(stub, world, null!, profile));
        Assert.Throws<ArgumentNullException>(() => new SimulatorImpactEvaluator(stub, world, request, null!));
    }

    /// <summary>
    /// The whole point of the adapter: the solver reaches the same answer through a forward
    /// simulator as it does through the analytic mock, because both are ground truth here.
    /// </summary>
    [Fact]
    public async Task SolvesARealRequestThroughTheContractSurface()
    {
        var target = new Vec3(150f, 60f, 64f);
        var stub = new VacuumSimulatorStub(planeZ: target.Z);
        var outcome = await new LineupSolver().SolveAsync(
            Fixture.Request(target),
            stub,
            new FlatWorld(target.Z),
            Fixture.Smoke());

        Assert.Equal(SolverStatus.Success, outcome.Status);
        Assert.NotEmpty(outcome.Results);
        Assert.True(outcome.Results[0].TargetDistance < 1f, outcome.Results[0].Note);
        Assert.True(stub.SimulateCount <= 2000, $"spent {stub.SimulateCount} forward calls");
        Assert.Equal(stub.SimulateCount, outcome.Results[0].Steps);
    }

    private static void AssertNear(double expected, double actual, string what)
    {
        var tolerance = Max(0.01d, Abs(expected) * 1e-4d);
        Assert.True(
            Abs(expected - actual) <= tolerance,
            $"{what}: expected {expected:0.####}, got {actual:0.####} (tolerance {tolerance:0.####})");
    }
}
