using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Config;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// Every collision sweep the plugin issues, on top of <see cref="ITraceBackend"/>.
/// </summary>
/// <remarks>
/// Two callers need the same work: <see cref="GameWorldGeometry"/>, which hands
/// sweeps to the solver, and the crosshair probe that <c>css_lf_zone</c> uses to
/// find the ground under the player's aim. Both have to skip entities a grenade
/// flies through - a practice server is full of them, and a trace stops on them
/// unless it is told not to - so the stepping logic lives here once.
///
/// The class is synchronous and thread safe: <see cref="ITraceBackend"/> is
/// responsible for reaching the game thread, and the plugin only points the solver
/// at this class while the server is running.
/// </remarks>
public sealed class GameWorldTracer
{
    /// <summary>How many non solid entities one trace may step past before giving up.</summary>
    private const int MaxStepOvers = 3;

    private readonly ITraceBackend _backend;
    private readonly Func<PluginConfig>? _config;

    private int _sweeps;

    /// <summary>Creates a tracer.</summary>
    /// <param name="backend">Engine binding used for every sweep.</param>
    /// <param name="config">Returns the live configuration, for the sweep budget; omit for no budget.</param>
    public GameWorldTracer(ITraceBackend backend, Func<PluginConfig>? config = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _config = config;
    }

    /// <summary>Whether the backend can trace at all, surfaced in the menu diagnostics.</summary>
    public bool TracingAvailable => _backend.IsAvailable;

    /// <summary>Name of the backend in use, for diagnostics.</summary>
    public string BackendName => _backend.Name;

    /// <summary>Sweeps issued since the last reset, for diagnostics and tests.</summary>
    public int SweepsUsed => _sweeps;

    /// <summary>Refills the sweep budget. Called once per search.</summary>
    public void ResetBudget() => _sweeps = 0;

    /// <summary>Runs one sweep, stepping past entities a grenade flies through.</summary>
    /// <param name="origin">Sweep start in world units.</param>
    /// <param name="direction">Direction, does not need to be normalized.</param>
    /// <param name="maxDistance">Maximum distance to sweep.</param>
    /// <param name="radius">Swept sphere radius, 0 traces a line.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, or -1.</param>
    /// <param name="keep">Predicate deciding whether a hit stops the sweep.</param>
    /// <returns>The trace result.</returns>
    public TraceHit Sweep(
        Vec3 origin,
        Vec3 direction,
        float maxDistance,
        float radius,
        int ignoreEntityIndex,
        Func<string?, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);

        var unit = direction.Normalized();
        if (unit.IsZero() || maxDistance <= 0f)
        {
            return Miss(origin, maxDistance, unit);
        }

        var budget = _config is null ? int.MaxValue : _config().MaxTracesPerSolve;
        if (++_sweeps > budget)
        {
            // Out of budget: report a miss so the integration stops here rather than
            // letting a search continue on data it cannot trust.
            return Miss(origin, maxDistance, unit);
        }

        var steppedPast = new List<int>(MaxStepOvers);
        var ignore = ignoreEntityIndex;
        var advanced = 0f;

        for (var attempt = 0; attempt <= MaxStepOvers; attempt++)
        {
            if (advanced >= maxDistance)
            {
                return Miss(origin, maxDistance, unit);
            }

            var from = origin + (unit * advanced);
            var to = origin + (unit * maxDistance);
            var sample = _backend.Trace(from, to, MathF.Max(radius, 0f), ignore);
            if (!sample.DidHit)
            {
                return Miss(origin, maxDistance, unit);
            }

            var hit = sample.ToTraceHit();
            if (keep(hit.ClassName) || hit.EntityIndex < 0 || steppedPast.Contains(hit.EntityIndex))
            {
                return hit;
            }

            // The sweep stopped on something a thrown grenade flies through, such as
            // the thrower or a bot. Resume just past it and try again.
            steppedPast.Add(hit.EntityIndex);
            ignore = hit.EntityIndex;
            advanced = Vec3.Distance(origin, hit.Position) + 1f;
        }

        return Miss(origin, maxDistance, unit);
    }

    /// <summary>Runs a line sweep, used by the crosshair probe.</summary>
    /// <param name="origin">Sweep start in world units.</param>
    /// <param name="direction">Direction, does not need to be normalized.</param>
    /// <param name="maxDistance">Maximum distance to sweep.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, or -1.</param>
    /// <returns>The trace result, filtered with <see cref="ThrowPredicates.StopsRay"/>.</returns>
    public TraceHit TraceLine(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1) =>
        Sweep(origin, direction, maxDistance, 0f, ignoreEntityIndex, ThrowPredicates.StopsRay);

    /// <summary>Runs a sphere sweep, which is what the solver's bounces need.</summary>
    /// <param name="origin">Sweep start in world units.</param>
    /// <param name="direction">Direction, does not need to be normalized.</param>
    /// <param name="maxDistance">Maximum distance to sweep.</param>
    /// <param name="radius">Swept sphere radius.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, or -1.</param>
    /// <returns>The trace result, filtered with <see cref="ThrowPredicates.StopsProjectile"/>.</returns>
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1) =>
        Sweep(origin, direction, maxDistance, MathF.Max(radius, 0f), ignoreEntityIndex, ThrowPredicates.StopsProjectile);

    /// <summary>Looks down for the ground, which is how a zone centre is produced.</summary>
    /// <param name="position">Position to probe from, typically above the ground.</param>
    /// <param name="maxDrop">How far below <paramref name="position"/> to look.</param>
    /// <returns>The trace result, filtered with <see cref="ThrowPredicates.StopsProjectile"/>.</returns>
    public TraceHit ProbeGround(Vec3 position, float maxDrop) =>
        Sweep(position, -Vec3.UnitZ, MathF.Max(maxDrop, 1f), 0f, -1, ThrowPredicates.StopsProjectile);

    /// <summary>Builds the miss that reports where a sweep would have ended.</summary>
    /// <param name="origin">Sweep start.</param>
    /// <param name="maxDistance">Requested distance.</param>
    /// <param name="unit">Unit sweep direction.</param>
    /// <returns>A miss result.</returns>
    public static TraceHit Miss(Vec3 origin, float maxDistance, Vec3 unit)
    {
        var end = unit.IsZero() ? origin : origin + (unit * maxDistance);
        return new TraceHit(false, end, Vec3.UnitZ, 1f);
    }
}
