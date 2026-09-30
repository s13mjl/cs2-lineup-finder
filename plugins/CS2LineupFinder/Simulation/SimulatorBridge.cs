using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Simulation;

/// <summary>
/// The plugin's single entry point into the solver stack. It prefers the real
/// <see cref="ITrajectorySimulator"/> published by <c>src/CS2LineupFinder.Core</c>
/// through the CSSharp plugin capability API and falls back to
/// <see cref="StubTrajectorySimulator"/> when that plugin is not installed.
/// Either way the configured wall clock budget is enforced here, so a slow or
/// stuck implementation can never hold the server.
/// </summary>
public sealed class SimulatorBridge
{
    private readonly Func<TimeSpan> _timeoutProvider;

    /// <summary>Creates a bridge.</summary>
    /// <param name="timeoutProvider">Returns the current solve budget, so a config reload takes effect immediately.</param>
    public SimulatorBridge(Func<TimeSpan> timeoutProvider)
    {
        ArgumentNullException.ThrowIfNull(timeoutProvider);
        _timeoutProvider = timeoutProvider;
    }

    /// <summary>Simulator currently in use, replaced by <see cref="UseCoreSimulator"/>.</summary>
    public ITrajectorySimulator Simulator { get; private set; } = new StubTrajectorySimulator();

    /// <summary>Solver currently in use, replaced by <see cref="UseCoreSolver"/>.</summary>
    public ILineupSolver Solver { get; private set; } = new StubLineupSolver();

    /// <summary>True while the stand-ins are in place, i.e. Core has not been loaded.</summary>
    public bool UsingStub { get; private set; } = true;

    /// <summary>Name of the solver in use, for the diagnostics line in the menu.</summary>
    public string SolverName => Solver.Name;

    /// <summary>Installs the real forward simulator from Core.</summary>
    /// <param name="simulator">Simulator published by the Core plugin.</param>
    public void UseCoreSimulator(ITrajectorySimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(simulator);
        Simulator = simulator;
        UsingStub = false;
    }

    /// <summary>Installs the real inverse solver from Math.</summary>
    /// <param name="solver">Solver published by the Math plugin.</param>
    public void UseCoreSolver(ILineupSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        Solver = solver;
        UsingStub = false;
    }

    /// <summary>Reverts to the built-in stand-ins, used when Core is unloaded.</summary>
    public void RevertToStub()
    {
        Simulator = new StubTrajectorySimulator();
        Solver = new StubLineupSolver();
        UsingStub = true;
    }

    /// <summary>
    /// Runs a line-up search under the configured timeout. The call is always made
    /// from the thread pool so a synchronous Core implementation cannot block the
    /// game thread, and the plugin's cancellation token is the one that decides
    /// between <see cref="SolverStatus.Timeout"/> and <see cref="SolverStatus.Cancelled"/>.
    /// </summary>
    /// <param name="request">Line-up to solve.</param>
    /// <param name="world">World geometry implementation to trace against.</param>
    /// <param name="cancellationToken">Cancellation from the caller, e.g. a disconnect.</param>
    /// <returns>The outcome, with <see cref="SolverStatus.Timeout"/> on budget expiry.</returns>
    public async Task<SolverOutcome> SolveAsync(
        LineupRequest request,
        IWorldGeometry world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(world);

        var budget = _timeoutProvider();
        if (budget <= TimeSpan.Zero)
        {
            budget = TimeSpan.FromSeconds(3);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        var stopwatch = Stopwatch.StartNew();
        var solver = Solver;
        var simulator = Simulator;

        try
        {
            var solveTask = Task.Run(
                () => simulator.SolveLineupAsync(request, solver, world, cts.Token),
                CancellationToken.None);

            var outcome = await solveTask.ConfigureAwait(false);
            if (outcome is null)
            {
                return SolverOutcome.Failure(SolverStatus.Failed, "Simulator returned no outcome.", stopwatch.Elapsed);
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? SolverOutcome.Failure(SolverStatus.Cancelled, "Search cancelled.", stopwatch.Elapsed)
                : SolverOutcome.Failure(
                    SolverStatus.Timeout,
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Search exceeded the {budget.TotalSeconds:0.#}s budget."),
                    stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return SolverOutcome.Failure(SolverStatus.Failed, ex.Message, stopwatch.Elapsed);
        }
    }
}
