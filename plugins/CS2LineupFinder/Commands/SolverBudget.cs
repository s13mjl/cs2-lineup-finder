using System;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// Turns a wall-clock budget into a candidate budget. The inverse solver's
/// candidate enumeration is capped by <see cref="Contracts.LineupRequest.MaxCandidates"/>,
/// so a longer search only means something if the cap grows with it; the timeout
/// alone can never use more time than the candidate list needs.
/// </summary>
public static class SolverBudget
{
    /// <summary>
    /// Candidates one second of searching is expected to evaluate on the real
    /// simulator. Measured on the live server: a ~19.5 s solve that evaluated
    /// ~4700 candidates, and a truncated 5 s solve at ~1200 candidates - both
    /// land between 210 and 240 per second. The conservative end is used so a
    /// longer budget always buys strictly more candidates, never fewer than a
    /// shorter budget would.
    /// </summary>
    public const double MeasuredCandidatesPerSecond = 210.0;

    /// <summary>
    /// Candidate budget for a search. The default configuration stays exactly at
    /// the configured <c>solver.maxCandidates</c>; an explicit per-command
    /// duration scales the budget with the measured throughput above.
    /// </summary>
    /// <param name="solveSeconds">Explicit search length in seconds, or null.</param>
    /// <param name="configuredCandidates">Value of <c>solver.maxCandidates</c>.</param>
    /// <returns>Candidate cap to put into the request.</returns>
    public static int CandidateBudget(double? solveSeconds, int configuredCandidates)
    {
        if (solveSeconds is not { } seconds || seconds <= 0d)
        {
            return configuredCandidates;
        }

        var scaled = (long)Math.Ceiling(seconds * MeasuredCandidatesPerSecond);
        return (int)Math.Clamp(Math.Max(scaled, configuredCandidates), 1, 100_000);
    }

    /// <summary>
    /// Upper bound on coarse-grid cells the inverse solver materialises without an
    /// explicit budget. With one, <see cref="CandidateBudget"/> may exceed it, so
    /// the grid ceiling must grow with the request or the extra budget would only
    /// ever feed refinement misses.
    /// </summary>
    public const int GridCeiling = 16_384;

    /// <summary>
    /// Grid steps for a search. Without an explicit duration this is the
    /// configuration verbatim. With one, the grid is scaled so the expected
    /// cell count matches the candidate budget in both directions: a short
    /// budget coarsens the grid (a fast shallow sweep instead of an unfinished
    /// deep one), a long budget densifies it (more angles, not idle wall clock).
    /// The same throughput measurement that sizes the budget sizes the grid.
    /// </summary>
    /// <param name="solveSeconds">Explicit search length in seconds, or null.</param>
    /// <param name="candidateBudget">Result of <see cref="CandidateBudget"/>.</param>
    /// <param name="configuredYawStep">Value of <c>solver.yawStepDegrees</c>.</param>
    /// <param name="configuredElevationStep">Value of <c>solver.elevationStepDegrees</c>.</param>
    /// <param name="yawSpanDegrees">Search width either side of the bearing.</param>
    /// <param name="minElevationDegrees">Lowest grid elevation.</param>
    /// <param name="maxElevationDegrees">Highest grid elevation.</param>
    /// <returns>Yaw and elevation steps to put into the request.</returns>
    public static (double YawStep, double ElevationStep) DensifiedSteps(
        double? solveSeconds,
        int candidateBudget,
        double configuredYawStep,
        double configuredElevationStep,
        double yawSpanDegrees,
        double minElevationDegrees,
        double maxElevationDegrees)
    {
        if (solveSeconds is not { } seconds || seconds <= 0d)
        {
            return (configuredYawStep, configuredElevationStep);
        }

        // Mirrors LineupSolver.BuildCandidatesForLaunch's grid arithmetic exactly,
        // so the estimate here is the count the solver will actually enumerate.
        var target = Math.Min(candidateBudget, GridCeiling);

        // Scale both axes by one factor, preserving the grid's aspect. Two passes
        // absorb the floor rounding introduced by converting a step back into a
        // cell count, which the solver's Floor-based arithmetic will re-apply.
        var scale = 1.0;
        double yaw = configuredYawStep;
        double elevation = configuredElevationStep;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            yaw = Math.Max(configuredYawStep * scale, MinimumStep);
            elevation = Math.Max(configuredElevationStep * scale, MinimumStep);
            var scaledYawSteps = (int)Math.Floor(yawSpanDegrees / yaw);
            var scaledElevationSteps = (int)Math.Floor((maxElevationDegrees - minElevationDegrees) / elevation);
            var cells = ((2 * scaledYawSteps) + 1) * (scaledElevationSteps + 1);
            var correction = Math.Sqrt(cells / (double)target);
            if (Math.Abs(correction - 1.0) < 0.02)
            {
                break;
            }

            scale *= correction;
        }

        return (yaw, elevation);
    }

    /// <summary>Lower bound on any produced grid step, matching the config clamp.</summary>
    private const double MinimumStep = 0.25;
}
