using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// The half of the command layer that runs a search: it drives <c>css_lf_find</c>,
/// prints the ranked results in chat and asks the visual layer for the aim beam and
/// the impact marker.
/// </summary>
public sealed partial class CommandService
{
    /// <summary>Players with a search in flight, so <c>css_lf_find</c> cannot be spammed.</summary>
    private readonly HashSet<int> _running = new();

    /// <summary>Active sliced searches, advanced once per tick from the game thread.</summary>
    private readonly Dictionary<int, SearchRun> _searches = new();

    /// <summary>Guards <see cref="_running"/>.</summary>
    private readonly object _runningGate = new();

    /// <summary>
    /// Solves a line-up for the player and reports the result. The call never throws
    /// and never outlives the configured budget, because
    /// <see cref="Simulation.SimulatorBridge.SolveAsync"/> enforces it.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="world">World geometry the simulator traces against.</param>
   /// <param name="cancellationToken">Cancels the search, e.g. when the player leaves.</param>
    /// <param name="solveSecondsOverride">One-shot search length in seconds, or null for the configured default.</param>
    /// <returns>The outcome, or <see langword="null"/> when the session was incomplete.</returns>
    public async Task<SolverOutcome?> FindAsync(
        int playerSlot,
        IWorldGeometry world,
        CancellationToken cancellationToken = default,
        double? solveSecondsOverride = null)
    {
       ArgumentNullException.ThrowIfNull(world);

        // A one-shot duration typed with the command beats the configured default
        // for exactly one search, then the session returns to the config value.
        double? solveSeconds = solveSecondsOverride;
        var session0 = Sessions.GetOrCreate(playerSlot);
        if (solveSeconds is null)
        {
            solveSeconds = session0.SolveSecondsOverride;
            session0.SolveSecondsOverride = null;
        }

        // The tracer meter is shared by every search and the zone probe, so refill
        // it here: once per command, before either path issues its first sweep.
        if (world is Game.GameWorldGeometry geometry)
        {
            geometry.ResetBudget();
        }

        if (!MapEnabled)
        {
            Reply(playerSlot, Phrases.Id.MapDisabled, _currentMap());
            return null;
        }

        lock (_runningGate)
        {
            if (!_running.Add(playerSlot))
            {
                Reply(playerSlot, Phrases.Id.SearchRunning);
                return null;
            }
        }

        var registered = false;
        try
        {
            var request = BuildRequest(playerSlot);
            if (request is null)
            {
                return null;
            }

            // Regression: the per-command duration was read (and cleared) before
            // BuildRequest consumed session.SolveSecondsOverride, so the request
            // never carried it and every "find 60" searched with the default
            // candidate cap and grid. Pass the explicit value through here; the
            // candidate budget AND the grid density both scale with it, because
            // the shipped 1.0/1.5 degree grid only holds ~4k cells and would
            // exhaust long before the wall clock mattered.
            var candidateBudget = SolverBudget.CandidateBudget(solveSeconds, Config.MaxCandidates);
            var (yawStep, elevationStep) = SolverBudget.DensifiedSteps(
                solveSeconds,
                candidateBudget,
                Config.YawStepDegrees,
                Config.ElevationStepDegrees,
                Config.YawSpanDegrees,
                Config.MinElevationDegrees,
                Config.MaxElevationDegrees);
            request = request with
            {
                SolveSeconds = solveSeconds,
                MaxCandidates = candidateBudget,
                YawStepDegrees = yawStep,
                ElevationStepDegrees = elevationStep,
            };
            LastBuiltRequest = request;

            var session = Sessions.GetOrCreate(playerSlot);
            var effectiveSeconds = solveSeconds is { } secs && secs > 0d ? secs : (double?)null;
            Reply(playerSlot, Phrases.Id.Searching, CommandUsage.Token(request.GrenadeType));
            if (effectiveSeconds is { } budgetSeconds)
            {
                Reply(playerSlot, Phrases.Id.SearchBudgetNote, budgetSeconds, ProgressReportInterval);
            }

            var search = Simulator.BeginLineupSearch(request, world);
            if (search is null)
            {
                var solved = await Simulator.SolveAsync(request, world, cancellationToken).ConfigureAwait(false);
                session.LastOutcome = solved;
                session.SelectedResultIndex = 0;
                Report(playerSlot, request, solved, world as Game.GameWorldGeometry);
                if (solved.Status == SolverStatus.Success && session.SelectedSolution is { } hit)
                {
                    RecordSuggestion(playerSlot, request, hit);
                    if (Config.AutoAim)
                    {
                        AimAtSolution(playerSlot, hit);
                    }
                }

                return solved;
            }

            // Every slice, including the first, advances from OnTick: chat
            // command callbacks are not guaranteed to run on the game thread,
            // and engine traces must stay on it. The command returns null here,
           // which the caller reports as "searching".
            var run = new SearchRun(
                request,
                search,
                cancellationToken,
                    effectiveSeconds is { } explicitSeconds
                        ? TimeSpan.FromSeconds(explicitSeconds)
                        : TimeSpan.FromSeconds(Config.SolveTimeoutSeconds),
                world as Game.GameWorldGeometry);

            _searches.Add(playerSlot, run);
            registered = true;
            return null;
        }
        finally
        {
            // A sliced run keeps its slot reserved until TickSearches finishes it;
            // everything else releases the slot here.
            if (!registered)
            {
                lock (_runningGate)
                {
                    _running.Remove(playerSlot);
                }
            }
        }
    }

    /// <summary>
    /// Wall-clock budget, in milliseconds, for one slice of a sliced search. Small
    /// enough to sit inside a tick alongside everything else the server does, long
    /// enough that a search finishes in a handful of ticks.
    /// </summary>
    private const double SliceBudgetMilliseconds = 8.0;

    /// <summary>
    /// Report search progress once per this many evaluated candidates, so a long
    /// search gives the player a heartbeat instead of silence. The first report
    /// lands at 256 evaluated candidates, i.e. after the default 64-candidate
    /// grid is already exhausted - short searches never print it.
    /// </summary>
    private const int ProgressReportInterval = 256;

    /// <summary>
    /// Landing spreads above this (units, horizontal) are reported as unstable.
    /// Recorded calibration runs show smooth trajectories spread up to a few
    /// tens of units across a nudged-aim window, while ledge branches and
    /// multi-bounce rollouts spread 120-170u; 90 sits between the populations.
    /// Advisory only: the suggestion is still shown, the player is told to
    /// verify it.
    /// </summary>
    private const float StabilityWarnUnits = 90f;

    /// <summary>
    /// Advances every active sliced search. Called from the tick handler on the
    /// game thread; finished runs report through the normal chat path and release
    /// the player's search slot.
    /// </summary>
    public void TickSearches()
    {
        if (_searches.Count == 0)
        {
            return;
        }

        List<int> finished;
        lock (_runningGate)
        {
            finished = new List<int>();
            foreach (var pair in _searches)
            {
                var evaluations = pair.Value.Search.Evaluated;
                if (evaluations / ProgressReportInterval > pair.Value.LastProgressMark)
                {
                    pair.Value.LastProgressMark = evaluations / ProgressReportInterval;
                    Reply(
                        pair.Key,
                        Phrases.Id.SearchProgress,
                        evaluations,
                        pair.Value.Request.SolveSeconds ?? (double?)null);
                }

                var outcome = pair.Value.Step(SliceBudgetMilliseconds);
                if (outcome is null)
                {
                    continue;
                }

                finished.Add(pair.Key);
                var session = Sessions.GetOrCreate(pair.Key);
                session.LastOutcome = outcome;
                session.SelectedResultIndex = 0;
                Report(pair.Key, pair.Value.Request, outcome, pair.Value.Geometry);
                if (outcome.Status == SolverStatus.Success && session.SelectedSolution is { } tickHit)
                {
                    RecordSuggestion(pair.Key, pair.Value.Request, tickHit);
                    if (Config.AutoAim)
                    {
                        AimAtSolution(pair.Key, tickHit);
                    }
                }

                if (outcome.Status == SolverStatus.Success || outcome.Status == SolverStatus.NoSolution)
                {
                    continue;
                }

                // Failure statuses are terminal here as well; nothing further to do.
            }
        }

        foreach (var slot in finished)
        {
            _searches.Remove(slot);
            lock (_runningGate)
            {
                _running.Remove(slot);
            }
        }
    }

    /// <summary>
    /// Drops a player's sliced search without waiting for the next tick, used when
    /// that player disconnects. The cancellation token would end the run on the
    /// next tick anyway; removing it here keeps the dictionaries honest immediately.
    /// </summary>
    /// <param name="playerSlot">Engine slot of the player who left.</param>
    public void DropSearch(int playerSlot)
    {
        _searches.Remove(playerSlot);
        lock (_runningGate)
        {
            _running.Remove(playerSlot);
        }
    }

    /// <summary>One player's sliced search between its first and last slice.</summary>
    private sealed class SearchRun
    {
        internal SearchRun(LineupRequest request, ILineupSearch search, CancellationToken cancellationToken, TimeSpan timeout, Game.GameWorldGeometry? geometry)
        {
            Request = request;
            Search = search;
            _cancellationToken = cancellationToken;
            _timeout = timeout;
            _geometry = geometry;
            Geometry = geometry;
            _started = Stopwatch.StartNew();
        }

        internal LineupRequest Request { get; }

        /// <summary>World the search traces against, when it is the real traced one.</summary>
        internal Game.GameWorldGeometry? Geometry { get; }

        /// <summary>Last reported progress bucket, so each interval prints once.</summary>
        internal int LastProgressMark;

    internal ILineupSearch Search { get; }

        private readonly CancellationToken _cancellationToken;

        private readonly TimeSpan _timeout;

        private readonly Stopwatch _started;

        private readonly Game.GameWorldGeometry? _geometry;

        /// <summary>
        /// Advances the search by one slice. The search itself enforces its wall
        /// clock budget; the caller's token is checked here so a disconnect ends a
        /// run instead of feeding it more ticks.
        /// </summary>
        /// <param name="budgetMilliseconds">Wall-clock slice, in milliseconds.</param>
        /// <returns>The outcome when finished, otherwise <see langword="null"/>.</returns>
        internal SolverOutcome? Step(double budgetMilliseconds)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                return SolverOutcome.Failure(
                    SolverStatus.Cancelled,
                    "Search cancelled.",
                    TimeSpan.Zero);
            }

            var outcome = Search.Step(budgetMilliseconds);
            if (outcome is null && _geometry is { BudgetExhausted: true })
            {
                // Every sweep past the meter is a miss, so any further candidates
                // would be simulated blind; the result would look like a solution
                // while being wrong. Abort here instead.
                _geometry.LogSweeps("sliced search aborted");
                return SolverOutcome.Failure(
                    SolverStatus.Failed,
                    "sweep-budget",
                    _started.Elapsed);
            }

            if (outcome is not null)
            {
                // Log the meter so a future "no solution" report can be told apart
                // from a budget-starved search: a normal full search shows roughly
                // the same number every time, while an exhausted meter shows the cap.
                _geometry?.LogSweeps("sliced search finished");
            }

            if (outcome is null && _started.Elapsed >= _timeout)
            {
                // Hand back what the search has already banked instead of an empty
                // failure: in-zone hits stay hits, and otherwise the closest misses
                // give the player an aim point to nudge from.
                _geometry?.LogSweeps("sliced search timed out");
                SolverOutcome partial = Search.FinishNow();
                if (partial.Status == SolverStatus.Success)
                {
                    return partial with
                    {
                        Elapsed = _started.Elapsed,
                        Message = "budget-expired",
                    };
                }

                return new SolverOutcome
                {
                    Status = SolverStatus.Timeout,
                    Results = partial.Results,
                    Elapsed = _started.Elapsed,
                    Message = partial.Message,
                };
            }

            return outcome;
        }
    }

    /// <summary>Prints an outcome and draws the visuals of its best result.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="request">Request that was solved.</param>
    /// <param name="outcome">Outcome to report.</param>
    /// <param name="world">World the search traced against, when known, so failure reports can include the sweep meter.</param>
    public void Report(int playerSlot, LineupRequest request, SolverOutcome outcome, Game.GameWorldGeometry? world = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome.Status)
        {
            case SolverStatus.Success:
                var limit = Math.Min(outcome.Results.Count, Config.MaxResults);
                if (limit <= 0)
                {
                    Reply(playerSlot, Phrases.Id.NoSolution, "the solver returned an empty result set");
                    return;
                }

                Reply(
                    playerSlot,
                    Phrases.Id.Found,
                    limit,
                    outcome.Elapsed.TotalSeconds,
                    Simulator.SolverName);

                for (var i = 0; i < limit; i++)
                {
                    var solution = outcome.Results[i];
                    Reply(
                        playerSlot,
                        Phrases.Id.ResultLine,
                        i + 1,
                        CommandParser.RoundAngle(solution.Yaw),
                        CommandParser.RoundPlayerPitch(solution.Pitch),
                        solution.TargetDistance);
                    WarnIfUnstable(playerSlot, i + 1, solution);
                }

                DrawResult(playerSlot, request, outcome.Results[0]);

                // A successful solve can still carry a note, e.g. the sliced
                // search was cut off by the wall clock with hits already banked.
                if (outcome.Message is not null)
                {
                    Reply(playerSlot, Phrases.Id.SearchBudgetExpired, outcome.Elapsed.TotalSeconds);
                }

                break;

            case SolverStatus.Timeout:
                Reply(playerSlot, Phrases.Id.TimedOut, outcome.Elapsed.TotalSeconds);

                // A timed-out sliced search hands back what it already banked:
                // the closest misses, best first. Listing them and drawing the
                // best is what turns "timed out" into an aim point to nudge.
                var timedOutLimit = Math.Min(outcome.Results.Count, Config.MaxResults);
                for (var i = 0; i < timedOutLimit; i++)
                {
                    var solution = outcome.Results[i];
                    Reply(
                        playerSlot,
                        Phrases.Id.ResultLine,
                        i + 1,
                        CommandParser.RoundAngle(solution.Yaw),
                        CommandParser.RoundPlayerPitch(solution.Pitch),
                        solution.TargetDistance);
                    WarnIfUnstable(playerSlot, i + 1, solution);
                }

                if (outcome.Results.Count > 0)
                {
                    DrawResult(playerSlot, request, outcome.Results[0]);
                }

                break;

            case SolverStatus.Cancelled:
                Reply(playerSlot, Phrases.Id.SearchFailed, "cancelled", outcome.Message ?? string.Empty);
                break;

            case SolverStatus.Failed when outcome.Message == "sweep-budget":
                Reply(playerSlot, Phrases.Id.SweepBudgetExhausted);
                break;

            default:
                var detail = outcome.Message ?? string.Empty;
                if (outcome.Results.Count > 0)
                {
                    var closest = outcome.Results
                        .OrderBy(static solution => solution.TargetDistance)
                        .First();
                    detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{detail} (closest {closest.TargetDistance:0.#}u");
                    if (world is not null)
                    {
                        detail += string.Create(
                            CultureInfo.InvariantCulture,
                            $", sweeps {world.SweepsUsed}");
                    }

                    detail += ')';
                }

                Reply(
                    playerSlot,
                    Phrases.Id.SearchFailed,
                    outcome.Status.ToString().ToLowerInvariant(),
                    detail);
                break;
        }
    }

    /// <summary>
    /// Warns when the evaluated neighbourhood of a suggestion lands far from the
    /// suggestion itself: a step edge or a multi-bounce rollout can move the real
    /// landing a whole platform away even though the simulated miss is tiny. The
    /// spread comes from candidates the search already simulated, so this costs
    /// no extra engine work.
    /// </summary>
    private void WarnIfUnstable(int playerSlot, int rank, LineupSolution solution)
    {
        if (solution.NeighbourSpread is { } spread && spread > StabilityWarnUnits)
        {
            Reply(playerSlot, Phrases.Id.UnstableAim, rank, spread);
        }
    }

    /// <summary>Draws the 3 second aim beam and the impact cross for one solution.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="request">Request the solution answers.</param>
    /// <param name="solution">Solution to visualize.</param>
    public void DrawResult(int playerSlot, LineupRequest request, LineupSolution solution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(solution);

        var config = Config;
        if (!Visuals.Enabled)
        {
            return;
        }

        var eye = request.Origin.Eyes.IsZero() ? solution.ReleasePosition : request.Origin.Eyes;
        var direction = CommandParser.DirectionFromAngles(solution.Yaw, solution.Pitch);
        Visuals.DrawAimBeam(playerSlot, eye, direction, config.BeamDurationSeconds);
        Visuals.DrawImpactMarker(solution.ImpactPosition, config.BeamDurationSeconds);
    }

    /// <summary>Redraws the aim beam for the player's current best result.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <returns><see langword="true"/> when a beam was drawn.</returns>
    public bool RedrawLastResult(int playerSlot)
    {
        var session = Sessions.GetOrCreate(playerSlot);
        var solution = session.SelectedSolution;
        var request = BuildRequest(playerSlot, explain: false);
        if (solution is null || request is null)
        {
            Reply(playerSlot, Phrases.Id.NeedBoth);
            return false;
        }
 
        // Whatever the player will actually throw next is now this suggestion;
        // remember it so the real landing can be paired with it. Must happen
        // before the visuals check: with visuals disabled the selection still
        // changed and the calibration pairing still has to follow it.
        RecordSuggestion(playerSlot, request, solution);
 
        if (!Visuals.Enabled)
        {
            Reply(playerSlot, Phrases.Id.VisualsDisabled);
            return false;
        }

        DrawResult(playerSlot, request, solution);
        Reply(playerSlot, Phrases.Id.BeamDrawn, Config.BeamDurationSeconds);
        return true;
    }

    /// <summary>Formats a yaw or pitch the way chat output shows it.</summary>
    /// <param name="degrees">Angle to format.</param>
    /// <returns>The formatted angle.</returns>
    public static string FormatAngle(float degrees) => CommandParser
        .RoundAngle(degrees)
        .ToString("0.0", CultureInfo.InvariantCulture);
}
