using System;
using CS2LineupFinder.Contracts;
using static System.Math;

namespace CS2LineupFinder.Math;

/// <summary>
/// The single seam through which the inverse solver observes the world. <c>math-core</c>
/// may not reference <c>src/CS2LineupFinder.Core</c>, so the solver is handed an
/// <see cref="IImpactEvaluator"/> and a unit test is free to hand it a table lookup instead.
/// </summary>
/// <remarks>
/// The search coordinate pair is (yaw, elevation) in degrees with a positive elevation
/// pointing up; implementations convert to engine pitch at their own boundary.
/// </remarks>
public interface IImpactEvaluator
{
    /// <summary>Number of throw evaluations performed so far.</summary>
    int EvaluationCount { get; }

    /// <summary>Simulates the throw for one pair of view angles.</summary>
    /// <param name="yawDegrees">Bearing in degrees.</param>
    /// <param name="elevationDegrees">Elevation above the horizon in degrees, positive points up.</param>
    /// <param name="impact">Receives the rest or detonation position in world units.</param>
    /// <returns><see langword="false"/> when the throw could not be evaluated at all.</returns>
    bool TryEvaluate(double yawDegrees, double elevationDegrees, out Vec3 impact);
}

/// <summary>
/// Adapts the forward <see cref="ITrajectorySimulator"/> so the solver can treat a real
/// simulated throw as one evaluation. This is the only place in math-core that resolves
/// angles into <see cref="ThrowParams"/>.
/// </summary>
public sealed class SimulatorImpactEvaluator : IImpactEvaluator
{
    private readonly ITrajectorySimulator _simulator;
    private readonly IWorldGeometry _world;
    private readonly LineupRequest _request;
    private readonly GrenadeProfile _profile;
    private readonly float _throwStrength;

    /// <summary>Builds an evaluator that runs the forward simulator once per probe.</summary>
    /// <param name="simulator">Forward trajectory simulator, normally the Core implementation.</param>
    /// <param name="world">World geometry the simulator sweeps against.</param>
    /// <param name="request">Request being solved; supplies the release point and stance.</param>
    /// <param name="profile">Grenade physics profile selecting the release speed for <see cref="ThrowButton"/>.</param>
    /// <param name="throwStrength">Release strength multiplier, 0..1, where 1 is a full release.</param>
    /// <exception cref="ArgumentNullException">One of the required arguments was null.</exception>
    public SimulatorImpactEvaluator(
        ITrajectorySimulator simulator,
        IWorldGeometry world,
        LineupRequest request,
        GrenadeProfile profile,
        float throwStrength = 1f)
    {
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _throwStrength = throwStrength;
    }

    /// <inheritdoc />
    public int EvaluationCount { get; private set; }

    /// <inheritdoc />
    public bool TryEvaluate(double yawDegrees, double elevationDegrees, out Vec3 impact)
    {
        var speed = (float)(Ballistics.SpeedFor(_profile, _request.Button) * _throwStrength);
        var direction = AngleMath.DirectionFromElevation(yawDegrees, elevationDegrees);
        var velocity = (direction.Normalized() * speed) + _request.Origin.Velocity;

        var tickRate = (int)Round(1d / _request.Environment.TickInterval);
        var parameters = new ThrowParams(
            _request.Origin.Eyes,
            velocity,
            _request.GrenadeType,
            _throwStrength,
            tickRate < 1 ? 64 : tickRate)
        {
            Mode = _request.ThrowMode,
            Profile = _profile,
            ZoneTest = _request.TargetZone.Contains,
        };

        EvaluationCount++;
        var result = _simulator.Simulate(parameters, _world);
        if (result is null)
        {
            impact = Vec3.Zero;
            return false;
        }

        impact = result.Impact.Position;
        return true;
    }
}
