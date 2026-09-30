using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Simulation;

// TODO: replace with real Core (CS2LineupFinder.Core.GrenadeSimulator).
/// <summary>
/// Throwaway forward simulator used while <c>src/CS2LineupFinder.Core</c> is still
/// on its own branch. It integrates the ballistic arc analytically, ignoring
/// bounces, and reports the first sample that falls inside the requested landing
/// zone. That is enough to prove the plugin path end to end: the command returns a
/// yaw/pitch pair within the 3 second budget, draws its beam and can be saved.
/// </summary>
public sealed class StubTrajectorySimulator : ITrajectorySimulator
{
    private const float Gravity = 800f;

    private const float MaxFlightTime = 8f;

    private const int SamplesPerSecond = 64;

    /// <inheritdoc />
    public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var speed = parameters.Velocity.Length;
        var direction = parameters.Velocity.Normalized();
        if (direction.IsZero())
        {
            direction = Vec3.UnitX;
        }

        var origin = parameters.Origin;
        var points = new System.Collections.Generic.List<TrajectoryPoint>(256);
        var start = origin;
        TrajectoryPoint? last = null;

        var stepCount = (int)(MaxFlightTime * SamplesPerSecond);
        var dt = MaxFlightTime / stepCount;
        for (var i = 0; i <= stepCount; i++)
        {
            var t = i * dt;
            var position = new Vec3(
                origin.X + (direction.X * speed * t),
                origin.Y + (direction.Y * speed * t),
                origin.Z + (direction.Z * speed * t) - (0.5f * Gravity * t * t));

            var velocity = new Vec3(
                direction.X * speed,
                direction.Y * speed,
                (direction.Z * speed) - (Gravity * t));

            var point = new TrajectoryPoint(position, velocity, t, 0);
            points.Add(point);
            last = point;

            if (world is not null)
            {
                var ground = world.ProbeGround(position, 4096f);
                if (ground.Hit && ground.Position.Z >= position.Z)
                {
                    var impact = new FinalImpact(ground.Position, ground.PlaneNormal, 0, t, true, parameters.GrenadeType);
                    return new TrajectoryResult(points, impact, parameters.ZoneTest?.Invoke(ground.Position) ?? false);
                }
            }
        }

        var final = last ?? new TrajectoryPoint(origin, parameters.Velocity, 0f, 0);
        var impactPoint = new FinalImpact(final.Position, Vec3.UnitZ, 0, final.Time, false, parameters.GrenadeType);
        return new TrajectoryResult(points, impactPoint, parameters.ZoneTest?.Invoke(final.Position) ?? false);
    }

    /// <inheritdoc />
    public TrajectoryResult SimulateWithDiagnostics(ThrowParams parameters, IWorldGeometry world, out SimulationDiagnostics diagnostics)
    {
        var result = Simulate(parameters, world);

        var apex = 0f;
        foreach (var point in result.Points)
        {
            var height = point.Position.Z - parameters.Origin.Z;
            if (height > apex)
            {
                apex = height;
            }
        }

        var horizontal = new Vec3(result.Impact.Position.X - parameters.Origin.X, result.Impact.Position.Y - parameters.Origin.Y, 0f).Length;
        diagnostics = new SimulationDiagnostics(apex, horizontal, 0f, result.Points.Count, result.Points.Count);
        return result;
    }

    /// <inheritdoc />
    public Task<SolverOutcome> SolveLineupAsync(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(solver);

        return Task.Run(async () => await SolveAsync(request, solver, world, cancellationToken).ConfigureAwait(false), cancellationToken);
    }

    private static async Task<SolverOutcome> SolveAsync(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var hits = new System.Collections.Generic.List<LineupSolution>();
        var evaluated = 0;

        try
        {
            await foreach (var candidate in solver.EnumerateCandidatesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                evaluated++;

                var solution = Evaluate(request, candidate, world);
                if (solution is not null)
                {
                    hits.Add(solution);
                }

                if (evaluated >= request.MaxCandidates)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return SolverOutcome.Failure(SolverStatus.Cancelled, "Line-up search was cancelled.", stopwatch.Elapsed);
        }

        if (hits.Count == 0)
        {
            return SolverOutcome.Failure(SolverStatus.NoSolution, "No candidate angle landed inside the zone.", stopwatch.Elapsed);
        }

        hits.Sort(static (a, b) => a.TargetDistance.CompareTo(b.TargetDistance));
        return SolverOutcome.Success(hits, stopwatch.Elapsed);
    }

    private static LineupSolution? Evaluate(LineupRequest request, AngleCandidate candidate, IWorldGeometry world)
    {
        var radians = candidate.Pitch * MathF.PI / 180f;
        var yawRadians = candidate.Yaw * MathF.PI / 180f;
        var speed = SpeedFor(request.Button);

        var direction = new Vec3(
            MathF.Cos(radians) * MathF.Cos(yawRadians),
            MathF.Cos(radians) * MathF.Sin(yawRadians),
            MathF.Sin(radians));

        var origin = request.Origin.Eyes.IsZero() ? request.Origin.Feet : request.Origin.Eyes;
        var parameters = new ThrowParams(origin, direction * speed, request.GrenadeType, 1f, 64)
        {
            Mode = request.ThrowMode,
            IgnoreEntityIndex = -1,
        };

        var local = new StubTrajectorySimulator();
        var result = local.Simulate(parameters, world);
        var impact = result.Impact.Position;
        var distance = new Vec3(impact.X - request.TargetZone.Center.X, impact.Y - request.TargetZone.Center.Y, 0f).Length;

        return new LineupSolution
        {
            Yaw = StubLineupSolver.NormalizeYaw(candidate.Yaw),
            Pitch = candidate.Pitch,
            ReleasePosition = origin,
            ImpactPosition = impact,
            TargetDistance = distance,
            Steps = result.Points.Count,
            Note = request.ThrowMode.ToString().ToLowerInvariant() + " + " + request.Button.ToString().ToLowerInvariant(),
        };
    }

    private static float SpeedFor(ThrowButton button) => button switch
    {
        ThrowButton.Secondary => 450f,
        ThrowButton.Both => 560f,
        _ => 675f,
    };
}
