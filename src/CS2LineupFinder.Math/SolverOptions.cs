using System;
using System.Diagnostics;
using static System.Math;

namespace CS2LineupFinder.Math;

/// <summary>
/// Tuning knobs for <see cref="LineupSolver"/>. Every default comes from the solver brief
/// and is justified in <c>docs/SOLVER.md</c>.
/// </summary>
public sealed record SolverOptions
{
    /// <summary>Hard ceiling on forward-simulator evaluations for a single solve.</summary>
    public int MaxEvaluations { get; init; } = 2000;

    /// <summary>Wall clock ceiling; when it expires the solver returns its best candidate so far.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How far either side of the analytic bearing the coarse yaw sweep reaches, degrees.</summary>
    public double YawSpanDegrees { get; init; } = 30d;

    /// <summary>Coarse yaw sample spacing, degrees.</summary>
    public double YawStepDegrees { get; init; } = 2d;

    /// <summary>Lowest elevation the coarse sweep tries, degrees above the horizon.</summary>
    public double MinElevationDegrees { get; init; } = 5d;

    /// <summary>Highest elevation the coarse sweep tries, degrees above the horizon.</summary>
    public double MaxElevationDegrees { get; init; } = 85d;

    /// <summary>Coarse elevation sample spacing, degrees.</summary>
    public double ElevationStepDegrees { get; init; } = 3d;

    /// <summary>How many coarse candidates are handed to the refinement stage.</summary>
    public int RefineTopK { get; init; } = 5;

    /// <summary>Iterations a single Nelder-Mead descent is allowed before it is cut off.</summary>
    public int MaxRefineIterations { get; init; } = 60;

    /// <summary>The refinement simplex is accepted once its landing spread falls under this many units.</summary>
    public double ConvergenceUnits { get; init; } = 0.5d;

    /// <summary>Angular simplex size at which refinement declares convergence, degrees.</summary>
    public double ConvergenceDegrees { get; init; } = 0.05d;

    /// <summary>Upper bound on how many alternatives are reported for one zone.</summary>
    public int MaxAlternatives { get; init; } = 5;

    /// <summary>Two alternatives closer than this in yaw are treated as the same throw, degrees.</summary>
    public double AlternativeYawSeparation { get; init; } = 1.5d;

    /// <summary>Two alternatives closer than this in elevation are treated as the same throw, degrees.</summary>
    public double AlternativeElevationSeparation { get; init; } = 2d;

    /// <summary>The briefed parameter set.</summary>
    public static SolverOptions Default { get; } = new();

    /// <summary>Validates the knobs that would otherwise make the search meaningless.</summary>
    /// <param name="reason">Receives the first violation found.</param>
    /// <returns><see langword="false"/> when the options cannot be used.</returns>
    public bool TryValidate(out string reason)
    {
        if (MaxEvaluations <= 0)
        {
            reason = "MaxEvaluations must be positive.";
            return false;
        }

        if (Timeout <= TimeSpan.Zero)
        {
            reason = "Timeout must be positive.";
            return false;
        }

        if (YawStepDegrees <= 0d || ElevationStepDegrees <= 0d)
        {
            reason = "Grid steps must be positive.";
            return false;
        }

        if (YawSpanDegrees < 0d)
        {
            reason = "YawSpanDegrees must not be negative.";
            return false;
        }

        if (MinElevationDegrees < 0d
            || MaxElevationDegrees > QAngle.MaxPitch
            || MinElevationDegrees > MaxElevationDegrees)
        {
            reason = "Elevation window must sit inside 0..89 degrees.";
            return false;
        }

        if (RefineTopK <= 0 || MaxRefineIterations <= 0 || ConvergenceUnits <= 0d)
        {
            reason = "Refinement needs a positive Top-K, iteration count and convergence tolerance.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// Evaluation and wall-clock ceiling shared by every stage of one solve, so the coarse grid
/// and the refinements cannot together overrun the budget.
/// </summary>
public sealed class SearchBudget
{
    private readonly Stopwatch _stopwatch;
    private readonly TimeSpan _timeout;

    /// <summary>Opens a budget and starts the clock.</summary>
    /// <param name="maxEvaluations">Maximum number of forward-simulator evaluations.</param>
    /// <param name="timeout">Maximum wall clock the search may take.</param>
    public SearchBudget(int maxEvaluations, TimeSpan timeout)
    {
        MaxEvaluations = maxEvaluations;
        _timeout = timeout;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>Evaluations this budget allows in total.</summary>
    public int MaxEvaluations { get; }

    /// <summary>Evaluations already granted.</summary>
    public int Used { get; private set; }

    /// <summary>Evaluations still available.</summary>
    public int Remaining => Max(0, MaxEvaluations - Used);

    /// <summary>Wall clock since the budget was opened.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>True once the wall clock ceiling has been reached.</summary>
    public bool IsExpired => _stopwatch.Elapsed >= _timeout;

    /// <summary>True once either the evaluation count or the clock ceiling has been reached.</summary>
    public bool IsExhausted => Used >= MaxEvaluations || IsExpired;

    /// <summary>Asks for one evaluation slot.</summary>
    /// <returns><see langword="true"/> when the caller may run one more forward simulation.</returns>
    public bool TryConsume()
    {
        if (IsExhausted)
        {
            return false;
        }

        Used++;
        return true;
    }
}
