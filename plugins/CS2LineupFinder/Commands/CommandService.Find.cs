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
    /// <returns>The outcome, or <see langword="null"/> when the session was incomplete.</returns>
    public async Task<SolverOutcome?> FindAsync(
        int playerSlot,
        IWorldGeometry world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);

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

        try
        {
            var request = BuildRequest(playerSlot);
            if (request is null)
            {
                return null;
            }

            var session = Sessions.GetOrCreate(playerSlot);
            Reply(playerSlot, Phrases.Id.Searching, CommandUsage.Token(request.GrenadeType));

            var outcome = await Simulator.SolveAsync(request, world, cancellationToken).ConfigureAwait(false);

            session.LastOutcome = outcome;
            session.SelectedResultIndex = 0;
            Report(playerSlot, request, outcome);
            return outcome;
        }
        finally
        {
            lock (_runningGate)
            {
                _running.Remove(playerSlot);
            }
        }
    }

    /// <summary>Prints an outcome and draws the visuals of its best result.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="request">Request that was solved.</param>
    /// <param name="outcome">Outcome to report.</param>
    public void Report(int playerSlot, LineupRequest request, SolverOutcome outcome)
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
                }

                DrawResult(playerSlot, request, outcome.Results[0]);
                break;

            case SolverStatus.Timeout:
                Reply(playerSlot, Phrases.Id.TimedOut, Config.SolveTimeoutSeconds);
                break;

            case SolverStatus.Cancelled:
                Reply(playerSlot, Phrases.Id.SearchFailed, "cancelled", outcome.Message ?? string.Empty);
                break;

            default:
                Reply(
                    playerSlot,
                    Phrases.Id.SearchFailed,
                    outcome.Status.ToString().ToLowerInvariant(),
                    outcome.Message ?? string.Empty);
                break;
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
