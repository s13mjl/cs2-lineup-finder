using System.Collections.Generic;
using System.Threading;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Game;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>Chat and console recorder standing in for the running server.</summary>
internal sealed class FakeMessages : IPluginMessages
{
    private readonly List<string> _lines = new();
    private readonly List<string> _warnings = new();

    /// <summary>Every chat line, in the order it was sent.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    /// <summary>Console warnings, which are what the operator would see.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
            {
                return _warnings.ToArray();
            }
        }
    }

    /// <summary>Last chat line, or an empty string when none was sent.</summary>
    public string Last => Lines.Count > 0 ? Lines[^1] : string.Empty;

    /// <inheritdoc />
    public void Reply(int playerSlot, string text)
    {
        lock (_lines)
        {
            _lines.Add(text);
        }
    }

    /// <inheritdoc />
    public void Log(string text)
    {
    }

    /// <inheritdoc />
    public void LogWarning(string text)
    {
        lock (_warnings)
        {
            _warnings.Add(text);
        }
    }
}

/// <summary>Records what the command layer asks the world to draw.</summary>
internal sealed class FakeVisuals : IPluginVisuals
{
    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>Beams requested through <see cref="DrawAimBeam"/>.</summary>
    public List<(int Slot, Vec3 Origin, Vec3 Direction, double Duration)> Beams { get; } = new();

    /// <summary>Positions of requested impact markers.</summary>
    public List<Vec3> Markers { get; } = new();

    /// <summary>Zones requested through <see cref="DrawZone"/>.</summary>
    public List<GroundZone> Zones { get; } = new();

    /// <summary>Throw origins requested through <see cref="DrawThrowOrigin"/>.</summary>
    public List<Vec3> Origins { get; } = new();

    /// <summary>Number of clear requests.</summary>
    public int Clears { get; private set; }

    /// <inheritdoc />
    public void DrawAimBeam(int playerSlot, Vec3 origin, Vec3 direction, double durationSeconds)
        => Beams.Add((playerSlot, origin, direction, durationSeconds));

    /// <inheritdoc />
    public void DrawImpactMarker(Vec3 position, double durationSeconds) => Markers.Add(position);

    /// <inheritdoc />
    public void DrawZone(GroundZone zone, double durationSeconds) => Zones.Add(zone);

    /// <inheritdoc />
    public void DrawThrowOrigin(Vec3 position, double durationSeconds) => Origins.Add(position);

    /// <inheritdoc />
    public void Clear() => Clears++;
}

/// <summary>Fixed clock so a saved <c>createdAt</c> is assertable.</summary>
internal sealed class FakeClock : IClock
{
    /// <summary>Timestamp every call returns.</summary>
    public DateTimeOffset Value { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow => Value;
}

/// <summary>Flat floor with an optional ceiling, so a thrown grenade has a bottom.</summary>
/// <remarks>
/// The engine free half of the tracer is real code under test; only the raw sweep is
/// faked. <see cref="IsAvailable"/> is what <c>GameWorldGeometry.CanTrace</c> reads,
/// so a test can switch the plugin between "traced" and "cannot trace" behaviour.
/// </remarks>
internal sealed class FakeTraceBackend : ITraceBackend
{
    /// <summary>Height of the floor plane.</summary>
    public float GroundZ { get; set; }

    /// <summary>Whether the crosshair probe can reach geometry.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Sweeps issued so far.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public string Name => "fake-flat-floor";

    /// <inheritdoc />
    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex)
    {
        Calls++;

        var delta = end - start;
        if (delta.Z >= 0f || start.Z <= GroundZ)
        {
            // Not descending, or already at / below the floor: a clean miss.
            return TraceSample.Miss(end);
        }

        var fraction = (start.Z - GroundZ) / (start.Z - end.Z);
        var position = new Vec3(
            start.X + (delta.X * fraction),
            start.Y + (delta.Y * fraction),
            GroundZ);

        return new TraceSample(true, position, Vec3.UnitZ, fraction);
    }
}

/// <summary>A solver that never yields, used to prove the solve budget is enforced.</summary>
internal sealed class BlackHoleSolver : ILineupSolver
{
    /// <inheritdoc />
    public string Name => "blackhole";

    /// <inheritdoc />
    public async IAsyncEnumerable<AngleCandidate> EnumerateCandidatesAsync(
        LineupRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A real solver that ignores cancellation would block its thread here; the
        // bridge has to notice that the budget expired regardless.
        await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
        yield break;
    }
}

/// <summary>Simulator that ignores cancellation and spins past the budget.</summary>
internal sealed class StuckSimulator : ITrajectorySimulator
{
    /// <inheritdoc />
    public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public TrajectoryResult SimulateWithDiagnostics(ThrowParams parameters, IWorldGeometry world, out SimulationDiagnostics diagnostics)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<SolverOutcome> SolveLineupAsync(
        LineupRequest request,
        ILineupSolver solver,
        IWorldGeometry world,
        CancellationToken cancellationToken = default)
            => Task.FromException<SolverOutcome>(new InvalidOperationException("solver exploded"));
}
