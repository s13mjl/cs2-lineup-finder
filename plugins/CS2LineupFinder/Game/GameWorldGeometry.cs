using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// <see cref="IWorldGeometry"/> on top of the running map, so a search can bounce a
/// grenade off real geometry instead of a mock.
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="GameWorldTracer"/>: the contract only has the
/// three sweeps the simulator needs, while the tracer also serves the crosshair
/// probe of <c>css_lf_zone</c>. Set <see cref="Config"/> before handing this to the
/// solver to have the per-search sweep budget enforced; leave it null for an
/// unmetered tracer, which is what the unit tests use.
/// </remarks>
public sealed class GameWorldGeometry : IWorldGeometry
{
    private readonly GameWorldTracer _tracer;

    /// <summary>Creates the adapter.</summary>
    /// <param name="tracer">Tracer every sweep is routed through.</param>
    public GameWorldGeometry(GameWorldTracer tracer)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        _tracer = tracer;
    }

    /// <summary>Creates a tracer bound to one engine backend.</summary>
    /// <param name="backend">Engine binding used for every sweep.</param>
    public GameWorldGeometry(ITraceBackend backend)
        : this(new GameWorldTracer(backend))
    {
    }

    /// <summary>Live configuration, when the caller wants the sweep budget enforced.</summary>
    public Func<Config.PluginConfig>? Config { get; init; }

    /// <summary>Whether the backend can trace at all, surfaced in the menu diagnostics.</summary>
    public bool TracingAvailable => _tracer.TracingAvailable;

    /// <summary>Name of the backend in use, for diagnostics.</summary>
    public string BackendName => _tracer.BackendName;

    /// <summary>Sweeps issued since the last reset, for diagnostics and tests.</summary>
    public int SweepsUsed => _tracer.SweepsUsed;

    /// <summary>Refills the sweep budget. Called once per search.</summary>
    public void ResetBudget() => _tracer.ResetBudget();

    /// <summary>
    /// Whether sweeps against the running server reach real geometry, so the menu can
    /// say so instead of leaving a player to wonder why a zone landed in the sky.
    /// </summary>
    /// <param name="world">Geometry to ask.</param>
    /// <returns><see langword="true"/> when the backend behind <paramref name="world"/> can trace.</returns>
    public static bool CanTrace(IWorldGeometry world) => world switch
    {
        GameWorldGeometry geometry => geometry.TracingAvailable,
        _ => true,
    };

    /// <inheritdoc />
    public TraceHit TraceRay(Vec3 origin, Vec3 direction, float maxDistance, int ignoreEntityIndex = -1) =>
        _tracer.TraceLine(origin, direction, maxDistance, ignoreEntityIndex);

    /// <inheritdoc />
    public TraceHit TraceSphere(Vec3 origin, Vec3 direction, float maxDistance, float radius, int ignoreEntityIndex = -1) =>
        _tracer.TraceSphere(origin, direction, maxDistance, radius, ignoreEntityIndex);

    /// <inheritdoc />
    public TraceHit ProbeGround(Vec3 position, float maxDrop) =>
        _tracer.ProbeGround(position, maxDrop);
}
