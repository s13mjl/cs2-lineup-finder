namespace CS2LineupFinder.Contracts;

/// <summary>
/// Inverse solver: given a landing area, produce candidate view angles.
/// Implemented by <c>src/CS2LineupFinder.Math</c>, consumed by the simulator and
/// available to the plugin for offline pre-computation.
/// </summary>
public interface ILineupSolver
{
    /// <summary>Human readable solver name, e.g. <c>coarse-to-fine-v1</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Produces candidate view angles worth simulating for
    /// <paramref name="request"/>.
    /// </summary>
    /// <param name="request">Request describing origin, stance and target zone.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>An ordered, possibly empty, sequence of angle candidates.</returns>
    IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
        LineupRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A candidate view angle produced by <see cref="ILineupSolver"/>.</summary>
public sealed record AngleCandidate
{
    /// <summary>View yaw in degrees.</summary>
    public required float Yaw { get; init; }

    /// <summary>View pitch in degrees.</summary>
    public required float Pitch { get; init; }

    /// <summary>Cheap heuristic score, lower is more promising.</summary>
    public float HeuristicCost { get; init; }
}
