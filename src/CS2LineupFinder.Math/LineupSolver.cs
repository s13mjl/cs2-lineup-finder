using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using static System.Math;

namespace CS2LineupFinder.Math;

/// <summary>
/// Coarse-to-fine inverse solver: it turns a landing zone into view angles.
/// </summary>
/// <remarks>
/// <para>
/// The contract surface, <see cref="EnumerateCandidatesAsync"/>, is pure mathematics: it
/// streams angles ranked by a vacuum prediction so the physics layer can simulate them in
/// order and stop as soon as one lands.
/// <see cref="SolveAsync(LineupRequest, IImpactEvaluator, GrenadeProfile, CancellationToken)"/>
/// is the full two stage driver
/// from the solver brief - analytic seed, coarse grid, then Nelder-Mead refinement of the best
/// few against a live <see cref="IImpactEvaluator"/>.
/// </para>
/// <para>
/// The solver searches in (yaw, elevation) and only converts to engine pitch at its output
/// boundary, because <see cref="LineupSolution.Pitch"/> is negative when a throw looks up.
/// </para>
/// <para>See <c>docs/SOLVER.md</c> for why each default has the value it has.</para>
/// </remarks>
public sealed class LineupSolver : ILineupSolver
{
    private const int GridSafetyLimit = 4096;

    private readonly IVDataProvider? _vdata;
    private readonly SolverOptions _options;

    /// <summary>Creates a solver.</summary>
    /// <param name="vdata">
    /// Grenade profile source used to resolve the release speed. Only
    /// <see cref="EnumerateCandidatesAsync"/> needs it;
    /// <see cref="SolveAsync(LineupRequest, IImpactEvaluator, GrenadeProfile, CancellationToken)"/> takes the
    /// profile per request and works without it.
    /// </param>
    /// <param name="options">Search tuning, or <see langword="null"/> for the briefed defaults.</param>
    public LineupSolver(IVDataProvider? vdata = null, SolverOptions? options = null)
    {
        _vdata = vdata;
        _options = options ?? SolverOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "coarse-to-fine-v1";

    /// <summary>
    /// Streams angle pairs worth simulating, cheapest first, using nothing but a vacuum
    /// prediction of where they land.
    /// </summary>
    /// <param name="request">Request describing origin, stance and target zone.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>Ranked candidates, empty when the zone is out of reach or has no profile.</returns>
    /// <exception cref="InvalidOperationException">
    /// The solver was built without an <see cref="IVDataProvider"/>, so it cannot resolve a
    /// release speed. Use the <c>(IVDataProvider, SolverOptions)</c> constructor, or call
    /// <see cref="SolveAsync(LineupRequest, IImpactEvaluator, GrenadeProfile, CancellationToken)"/>
    /// and pass the profile directly.
    /// </exception>
    public async IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
        LineupRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = BuildAnalyticCandidates(request);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield return candidate;
        }
    }

    /// <summary>
    /// Runs the two stage search: a coarse grid around the analytic bearing, then Nelder-Mead
    /// refinement of the most promising hits, every candidate judged by one forward evaluation.
    /// </summary>
    /// <param name="request">Request describing origin, stance and target zone.</param>
    /// <param name="evaluator">One call per candidate; normally wraps the forward simulator.</param>
    /// <param name="profile">Grenade profile that fixes the release speed used for seeding.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Ranked solutions, best first. Never throws for an unreachable or blocked zone.</returns>
    public Task<SolverOutcome> SolveAsync(
        LineupRequest request,
        IImpactEvaluator evaluator,
        GrenadeProfile profile,
        CancellationToken cancellationToken = default)
    {
        var budget = new SearchBudget(_options.MaxEvaluations, _options.Timeout);

        if (request is null)
        {
            return Task.FromResult(Fail(SolverStatus.InvalidRequest, "request must not be null.", budget));
        }

        if (evaluator is null)
        {
            return Task.FromResult(Fail(SolverStatus.InvalidRequest, "evaluator must not be null.", budget));
        }

        if (profile is null)
        {
            return Task.FromResult(Fail(SolverStatus.InvalidRequest, "profile must not be null.", budget));
        }

        if (!_options.TryValidate(out var reason))
        {
            return Task.FromResult(Fail(SolverStatus.InvalidRequest, reason, budget));
        }

        if (!TryResolveLaunch(request, profile, out var launch, out var problem))
        {
            return Task.FromResult(Fail(SolverStatus.InvalidRequest, problem, budget));
        }

        var maxReach = Ballistics.MaxRange(launch.Speed, launch.Gravity, launch.DeltaZ);
        if (launch.Range > maxReach)
        {
            return Task.FromResult(Fail(
                SolverStatus.NoSolution,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Zone centre is {0:0.#} units away but the maximum vacuum reach for {1} with button {2} is {3:0.#} units, so no angle can land there.",
                    launch.Range,
                    profile.ItemName,
                    request.Button,
                    maxReach),
                budget));
        }

        try
        {
            return Task.FromResult(Search(request, evaluator, launch, budget, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Fail(SolverStatus.Cancelled, "The search was cancelled.", budget));
        }
        catch (Exception exception)
        {
            // The evaluator is another layer's code; a throw there must not take the plugin down.
            return Task.FromResult(Fail(
                SolverStatus.Failed,
                "The simulator raised an unexpected error: " + exception.Message,
                budget));
        }
    }

    /// <summary>
    /// Convenience overload that wires a forward <see cref="ITrajectorySimulator"/> straight into
    /// the solver, which is the briefed <c>SolveLineup(simulator, origin, zone, ...)</c> entry point
    /// expressed with the frozen contract types.
    /// </summary>
    /// <param name="request">Request describing origin, stance and target zone.</param>
    /// <param name="simulator">Forward trajectory simulator from the physics layer.</param>
    /// <param name="world">World geometry the simulator sweeps against.</param>
    /// <param name="profile">Grenade profile that fixes the release speed.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The same outcome as the <see cref="IImpactEvaluator"/> overload.</returns>
    public Task<SolverOutcome> SolveAsync(
        LineupRequest request,
        ITrajectorySimulator simulator,
        IWorldGeometry world,
        GrenadeProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (simulator is null || world is null)
        {
            return SolveAsync(request, evaluator: null!, profile, cancellationToken);
        }

        return SolveAsync(
            request,
            new SimulatorImpactEvaluator(simulator, world, request, profile),
            profile,
            cancellationToken);
    }

    private SolverOutcome Search(
        LineupRequest request,
        IImpactEvaluator evaluator,
        in Launch launch,
        SearchBudget budget,
        CancellationToken cancellationToken)
    {
        var zone = request.TargetZone;
        var scorer = new Scorer(evaluator, budget, zone, _options);
        var pool = new List<Scorer.Hit>();
        var stageOne = 0;

        // --- Stage 1a: the analytic low and high arc straight at the zone centre ------------
        foreach (var seed in BuildSeeds(launch))
        {
            if (scorer.TryProbe(seed.Yaw, seed.Elevation, out var hit))
            {
                pool.Add(hit);
                stageOne++;
            }
        }

        // --- Stage 1b: the coarse grid around that bearing ----------------------------------
        var yawSteps = (int)Floor((_options.YawSpanDegrees / _options.YawStepDegrees) + 1e-6);
        var elevationSteps = (int)Floor(
            ((_options.MaxElevationDegrees - _options.MinElevationDegrees) / _options.ElevationStepDegrees) + 1e-6);

        for (var yi = -yawSteps; yi <= yawSteps && !budget.IsExhausted; yi++)
        {
            var yaw = launch.Bearing + (yi * _options.YawStepDegrees);
            for (var ei = 0; ei <= elevationSteps && !budget.IsExhausted; ei++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elevation = _options.MinElevationDegrees + (ei * _options.ElevationStepDegrees);
                if (scorer.TryProbe(yaw, elevation, out var hit))
                {
                    pool.Add(hit);
                    stageOne++;
                }
            }
        }

        // --- Stage 2: refine the cheapest, spatially distinct coarse hits --------------------
        var stageTwo = 0;
        var simplexIterations = 0;
        foreach (var seed in TakeDistinct(pool, _options.RefineTopK, _options.YawStepDegrees, _options.ElevationStepDegrees))
        {
            if (budget.IsExhausted)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            scorer.Best = seed;
            var outcome = NelderMead2D.Minimize(
                scorer.Objective,
                seed.Yaw,
                seed.Elevation,
                _options.YawStepDegrees * 0.5d,
                _options.ElevationStepDegrees * 0.5d,
                _options.ConvergenceUnits,
                _options.ConvergenceDegrees,
                _options.MaxRefineIterations,
                budget);

            stageTwo++;
            simplexIterations += outcome.Iterations;
            if (scorer.Best is not null)
            {
                pool.Add(scorer.Best.Value);
            }
        }

        // --- Collect: rank by error first, then drop angles that describe the same throw -----
        var hits = new List<Scorer.Hit>();
        foreach (var hit in pool)
        {
            if (double.IsPositiveInfinity(hit.Cost) || !zone.Contains(hit.Impact))
            {
                continue;
            }

            hits.Add(hit);
        }

        hits.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        var limit = Min(_options.MaxAlternatives, Max(1, request.MaxCandidates));
        var ranked = new List<LineupSolution>(limit);
        foreach (var hit in hits)
        {
            if (ranked.Count >= limit)
            {
                break;
            }

            if (IsDuplicate(ranked, hit))
            {
                continue;
            }

            ranked.Add(ToSolution(request, hit, budget, stageOne, stageTwo, simplexIterations));
        }

        if (ranked.Count > 0)
        {
            return new SolverOutcome
            {
                Status = SolverStatus.Success,
                Results = ranked,
                Elapsed = budget.Elapsed,
                Message = budget.IsExpired
                    ? "The time budget expired before the search finished; these are the best angles found."
                    : null,
            };
        }

        // Nothing landed inside. Hand back the closest attempt so the caller can say "off by N".
        var closest = TakeDistinct(pool, 1, 0d, 0d);
        var message = closest.Count == 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "No angle could be evaluated: the budget allowed {0} simulations and every probe failed.",
                budget.MaxEvaluations)
            : string.Format(
                CultureInfo.InvariantCulture,
                "Searched {0} coarse angles and refined {1}; the closest landing missed the zone centre by {2:0.0} units. Either an obstacle blocks every arc or the zone is only just in reach.",
                stageOne,
                stageTwo,
                closest[0].Cost);

        var failure = new SolverOutcome
        {
            Status = budget.IsExpired ? SolverStatus.Timeout : SolverStatus.NoSolution,
            Elapsed = budget.Elapsed,
            Message = message,
        };

        return closest.Count == 0
            ? failure
            : failure with
            {
                Results = new[] { ToSolution(request, closest[0], budget, stageOne, stageTwo, simplexIterations) },
            };
    }

    private List<AngleCandidate> BuildAnalyticCandidates(LineupRequest request)
    {
        if (request is null)
        {
            return new List<AngleCandidate>();
        }

        if (_vdata is null)
        {
            throw new InvalidOperationException(
                "EnumerateCandidatesAsync needs a release speed. Construct LineupSolver with an IVDataProvider, or call SolveAsync and pass a GrenadeProfile.");
        }

        var profile = _vdata.GetProfile(request.GrenadeType);
        if (profile is null || !_options.TryValidate(out _) || !TryResolveLaunch(request, profile, out var launch, out _))
        {
            return new List<AngleCandidate>();
        }

        var limit = Max(1, request.MaxCandidates);
        var candidates = new List<AngleCandidate>(limit);
        foreach (var seed in BuildSeeds(launch))
        {
            candidates.Add(ToCandidate(seed.Yaw, seed.Elevation, 0d));
        }

        var grid = new List<(double Yaw, double Elevation, double Cost)>();
        var yawSteps = (int)Floor((_options.YawSpanDegrees / _options.YawStepDegrees) + 1e-6);
        var elevationSteps = (int)Floor(
            ((_options.MaxElevationDegrees - _options.MinElevationDegrees) / _options.ElevationStepDegrees) + 1e-6);
        for (var yi = -yawSteps; yi <= yawSteps && grid.Count < GridSafetyLimit; yi++)
        {
            var yaw = launch.Bearing + (yi * _options.YawStepDegrees);
            for (var ei = 0; ei <= elevationSteps; ei++)
            {
                var elevation = _options.MinElevationDegrees + (ei * _options.ElevationStepDegrees);
                var predicted = Ballistics.PredictImpact(
                    request.Origin.Eyes,
                    yaw,
                    elevation,
                    launch.Speed,
                    launch.Gravity,
                    request.TargetZone.Center.Z);
                grid.Add((yaw, elevation, Vec3.Distance(predicted, request.TargetZone.Center)));
            }
        }

        grid.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        foreach (var entry in grid)
        {
            if (candidates.Count >= limit)
            {
                break;
            }

            candidates.Add(ToCandidate(entry.Yaw, entry.Elevation, entry.Cost));
        }

        return candidates;
    }

    private static List<(double Yaw, double Elevation)> BuildSeeds(in Launch launch)
    {
        var seeds = new List<(double Yaw, double Elevation)>(2);
        var count = Ballistics.SolveElevations(
            launch.Range,
            launch.DeltaZ,
            launch.Speed,
            launch.Gravity,
            out var low,
            out var high);

        if (count == 0)
        {
            return seeds;
        }

        AddSeed(seeds, launch.Bearing, low);
        if (count > 1)
        {
            AddSeed(seeds, launch.Bearing, high);
        }

        return seeds;
    }

    private static void AddSeed(List<(double Yaw, double Elevation)> seeds, double yaw, double elevation)
    {
        if (double.IsNaN(elevation) || elevation < 0d || elevation > QAngle.MaxPitch)
        {
            return;
        }

        seeds.Add((yaw, elevation));
    }

    private bool IsDuplicate(List<LineupSolution> accepted, Scorer.Hit hit)
    {
        foreach (var solution in accepted)
        {
            var yawGap = Abs(AngleMath.AngleDifference(solution.Yaw, hit.Yaw));

            // LineupSolution.Pitch is engine pitch, so -Pitch is the elevation the search used.
            var elevationGap = Abs((double)solution.Pitch + hit.Elevation);
            if (yawGap < _options.AlternativeYawSeparation && elevationGap < _options.AlternativeElevationSeparation)
            {
                return true;
            }
        }

        return false;
    }

    private static List<Scorer.Hit> TakeDistinct(
        List<Scorer.Hit> pool,
        int count,
        double yawSeparation,
        double elevationSeparation)
    {
        pool.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        var taken = new List<Scorer.Hit>(Max(0, count));
        foreach (var hit in pool)
        {
            if (taken.Count >= count)
            {
                break;
            }

            if (double.IsPositiveInfinity(hit.Cost))
            {
                continue;
            }

            var distinct = true;
            foreach (var kept in taken)
            {
                var yawGap = Abs(AngleMath.AngleDifference(kept.Yaw, hit.Yaw));
                if (yawGap < yawSeparation && Abs(kept.Elevation - hit.Elevation) < elevationSeparation)
                {
                    distinct = false;
                    break;
                }
            }

            if (distinct)
            {
                taken.Add(hit);
            }
        }

        return taken;
    }

    private LineupSolution ToSolution(
        LineupRequest request,
        Scorer.Hit hit,
        SearchBudget budget,
        int stageOne,
        int stageTwo,
        int simplexIterations) =>
        new()
        {
            Yaw = (float)Round(AngleMath.NormalizeYaw(hit.Yaw), 1),
            Pitch = (float)Round(AngleMath.PitchFromElevation(hit.Elevation), 1),
            ReleasePosition = request.Origin.Eyes,
            ImpactPosition = hit.Impact,
            TargetDistance = (float)hit.Cost,
            Steps = budget.Used,
            Note = string.Format(
                CultureInfo.InvariantCulture,
                "{0} coarse + {1} refined seeds, {2} simplex iterations, {3} evaluations, error {4:0.0}u{5}",
                stageOne,
                stageTwo,
                simplexIterations,
                budget.Used,
                hit.Cost,
                budget.IsExpired ? ", time budget expired" : string.Empty),
        };

    private static AngleCandidate ToCandidate(double yaw, double elevation, double cost) =>
        new()
        {
            Yaw = (float)Round(AngleMath.NormalizeYaw(yaw), 1),
            Pitch = (float)Round(AngleMath.PitchFromElevation(elevation), 1),
            HeuristicCost = (float)cost,
        };

    private static SolverOutcome Fail(SolverStatus status, string message, SearchBudget budget) =>
        SolverOutcome.Failure(status, message, budget.Elapsed);

    private bool TryResolveLaunch(
        LineupRequest request,
        GrenadeProfile profile,
        out Launch launch,
        out string problem)
    {
        launch = default;
        problem = string.Empty;

        var origin = request.Origin;
        if (origin is null)
        {
            problem = "request.Origin must be set.";
            return false;
        }

        var eyes = origin.Eyes;
        if (!float.IsFinite(eyes.X) || !float.IsFinite(eyes.Y) || !float.IsFinite(eyes.Z))
        {
            problem = "request.Origin.Eyes contains a non-finite component.";
            return false;
        }

        var zone = request.TargetZone;
        if (zone is null)
        {
            problem = "request.TargetZone must be set.";
            return false;
        }

        var center = zone.Center;
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
        {
            problem = "request.TargetZone.Center contains a non-finite component.";
            return false;
        }

        if (zone.Type == GroundZoneType.Circle && zone.Radius <= 0f)
        {
            problem = "A circular zone needs a positive Radius.";
            return false;
        }

        if (zone.Type == GroundZoneType.Rectangle && (zone.Width <= 0f || zone.Height <= 0f))
        {
            problem = "A rectangular zone needs a positive Width and Height.";
            return false;
        }

        var speed = Ballistics.SpeedFor(profile, request.Button);
        if (!double.IsFinite(speed) || speed <= 0d)
        {
            problem = string.Format(
                CultureInfo.InvariantCulture,
                "The profile for {0} has no usable release speed for button {1}.",
                profile.ItemName,
                request.Button);
            return false;
        }

        var gravity = Ballistics.GravityFor(request.Environment, profile);
        if (!double.IsFinite(gravity) || gravity <= 0d)
        {
            problem = "SimulationEnvironment.Gravity must be a positive finite number.";
            return false;
        }

        var dx = (double)center.X - eyes.X;
        var dy = (double)center.Y - eyes.Y;
        var horizontal = Sqrt((dx * dx) + (dy * dy));
        if (horizontal < 1e-3)
        {
            problem = "The zone centre sits on the thrower, so no bearing is defined.";
            return false;
        }

        launch = new Launch(
            AngleMath.AngleTo(eyes, center).Yaw,
            horizontal,
            (double)center.Z - eyes.Z,
            speed,
            gravity);
        return true;
    }

    /// <summary>The vacuum quantities every stage of the search agrees on.</summary>
    private readonly struct Launch
    {
        internal Launch(double bearing, double range, double deltaZ, double speed, double gravity)
        {
            Bearing = bearing;
            Range = range;
            DeltaZ = deltaZ;
            Speed = speed;
            Gravity = gravity;
        }

        internal double Bearing { get; }

        internal double Range { get; }

        internal double DeltaZ { get; }

        internal double Speed { get; }

        internal double Gravity { get; }
    }

    /// <summary>Turns a pair of angles into a cost, charging the shared budget for each probe.</summary>
    private sealed class Scorer
    {
        private readonly IImpactEvaluator _evaluator;
        private readonly SearchBudget _budget;
        private readonly GroundZone _zone;
        private readonly SolverOptions _options;

        internal Scorer(IImpactEvaluator evaluator, SearchBudget budget, GroundZone zone, SolverOptions options)
        {
            _evaluator = evaluator;
            _budget = budget;
            _zone = zone;
            _options = options;
        }

        /// <summary>Cheapest hit recorded since <see cref="Best"/> was last reset.</summary>
        internal Hit? Best { get; set; }

        internal double Objective(double yaw, double elevation)
        {
            if (!TryProbe(yaw, elevation, out var hit))
            {
                return double.PositiveInfinity;
            }

            if (Best is null || hit.Cost < Best.Value.Cost)
            {
                Best = hit;
            }

            return hit.Cost;
        }

        internal bool TryProbe(double yaw, double elevation, out Hit hit)
        {
            hit = default;

            // The simplex is unbounded, so keep the probe inside the physical elevation window.
            var clamped = Clamp(elevation, _options.MinElevationDegrees, _options.MaxElevationDegrees);
            if (!_budget.TryConsume()
                || !_evaluator.TryEvaluate(AngleMath.NormalizeYaw(yaw), clamped, out var impact)
                || !float.IsFinite(impact.X)
                || !float.IsFinite(impact.Y)
                || !float.IsFinite(impact.Z))
            {
                return false;
            }

            hit = new Hit(yaw, clamped, Vec3.Distance(impact, _zone.Center), impact);
            return true;
        }

        /// <summary>One evaluated candidate.</summary>
        internal readonly struct Hit
        {
            internal Hit(double yaw, double elevation, double cost, Vec3 impact)
            {
                Yaw = yaw;
                Elevation = elevation;
                Cost = cost;
                Impact = impact;
            }

            internal double Yaw { get; }

            internal double Elevation { get; }

            internal double Cost { get; }

            internal Vec3 Impact { get; }
        }
    }
}
