using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// One collision sweep as the plugin needs it: where it stopped, which way the
/// surface faced and what it hit. Deliberately flat so it outlives the trace call
/// that produced it and can be handed to the solver.
/// </summary>
public readonly struct TraceSample
{
    /// <summary>Creates a sample.</summary>
    /// <param name="didHit">Whether the sweep stopped on something solid.</param>
    /// <param name="position">Impact point, or the sweep end point on a miss.</param>
    /// <param name="normal">Unit surface normal at the impact, straight up when unknown.</param>
    /// <param name="fraction">Fraction of the requested distance that was travelled, 0..1.</param>
    /// <param name="entityIndex">Engine index of the hit entity, or -1 for world geometry.</param>
    /// <param name="designerName">Designer name of the hit entity, when there is one.</param>
    public TraceSample(bool didHit, Vec3 position, Vec3 normal, float fraction, int entityIndex = -1, string? designerName = null)
    {
        DidHit = didHit;
        Position = position;
        Normal = normal;
        Fraction = fraction;
        EntityIndex = entityIndex;
        DesignerName = designerName;
    }

    /// <summary>Whether the sweep stopped on something.</summary>
    public bool DidHit { get; }

    /// <summary>Impact point, or the point the sweep would have reached on a miss.</summary>
    public Vec3 Position { get; }

    /// <summary>Unit surface normal at the impact.</summary>
    public Vec3 Normal { get; }

    /// <summary>Fraction of the requested distance that was travelled.</summary>
    public float Fraction { get; }

    /// <summary>Engine index of the hit entity, or -1.</summary>
    public int EntityIndex { get; }

    /// <summary>Designer name of the hit entity, when there is one.</summary>
    public string? DesignerName { get; }

    /// <summary>A clean miss that travelled all the way to <paramref name="end"/>.</summary>
    /// <param name="end">Where the sweep would have stopped.</param>
    /// <returns>The miss sample.</returns>
    public static TraceSample Miss(Vec3 end) => new(false, end, Vec3.UnitZ, 1f);

    /// <summary>Converts to the engine independent contract type.</summary>
    /// <returns>The same result as a <see cref="TraceHit"/>.</returns>
    public TraceHit ToTraceHit() => new(DidHit, Position, Normal, Fraction, EntityIndex, DesignerName);
}

/// <summary>
/// The one engine call the plugin cannot express portably: a collision sweep.
/// </summary>
/// <remarks>
/// The plugin targets .NET 8, and the last CounterStrikeSharp release shipping a
/// <c>lib/net8.0</c> folder is <c>1.0.368</c>, which predates the managed
/// <c>Trace</c> type. Rather than dropping map-traced line-ups or forcing every
/// server onto .NET 10, tracing sits behind this interface with two
/// implementations: a reflective one that finds the managed trace API on a modern
/// host, and one that reports misses so every command still works.
/// </remarks>
public interface ITraceBackend
{
    /// <summary>Human readable backend name, logged once at plugin load.</summary>
    string Name { get; }

    /// <summary>Whether this backend can actually trace on the running server.</summary>
    bool IsAvailable { get; }

    /// <summary>Runs one sweep.</summary>
    /// <param name="start">Sweep start in world units.</param>
    /// <param name="end">Sweep end in world units.</param>
    /// <param name="hullRadius">Sphere radius swept along the segment; 0 traces a line.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, normally the thrower.</param>
    /// <returns>The sweep result, never <see langword="null"/>.</returns>
    TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex);
}

/// <summary>
/// The fallback tracer: it reports that every sweep travelled its full distance.
/// </summary>
/// <remarks>
/// A server without the managed trace API still gets the whole plugin - menus,
/// zones, solving, saving - it just cannot trace the map. The commands that depend
/// on a trace say so in chat instead of inventing a result, and the one warning is
/// logged once so the console is not flooded.
/// </remarks>
public sealed class NullTraceBackend : ITraceBackend
{
    private readonly Abstractions.IPluginMessages? _messages;

    private int _warned;

    /// <summary>Creates the fallback tracer.</summary>
    /// <param name="messages">Console sink for the one-time warning.</param>
    public NullTraceBackend(Abstractions.IPluginMessages? messages = null) => _messages = messages;

    /// <inheritdoc />
    public string Name => "none";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex)
    {
        if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _messages?.LogWarning(
                "Map tracing is unavailable on this server, so crosshair based zones fall back to the aim distance " +
                "and thrown grenades are assumed to fly unimpeded. Install CounterStrikeSharp 1.0.369 or newer " +
                "(which requires the .NET 10 runtime) to enable traced line-ups.");
        }

        return TraceSample.Miss(end);
    }
}

/// <summary>
/// Picks the best tracer the running server can offer and reports which one it picked.
/// </summary>
public static class TraceBackendFactory
{
    private static ITraceBackend? _instance;

    /// <summary>The tracer chosen for this process, or <see langword="null"/> before <see cref="Create"/>.</summary>
    public static ITraceBackend? Current => System.Threading.Volatile.Read(ref _instance);

    /// <summary>
    /// Creates the tracer for this server. Called once per process, so a hot reload
    /// does not re-probe the API.
    /// </summary>
    /// <param name="messages">Console sink used to report the choice.</param>
    /// <returns>The tracer to use.</returns>
    public static ITraceBackend Create(Abstractions.IPluginMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var existing = System.Threading.Volatile.Read(ref _instance);
        if (existing is not null)
        {
            return existing;
        }

        ITraceBackend backend;
        if (ReflectiveTraceBackend.TryCreate(out var reflective, out var reason))
        {
            backend = reflective;
            messages.Log("CS2LineupFinder: map tracing is available through the managed Trace API.");
        }
        else
        {
            backend = new NullTraceBackend(messages);
            messages.LogWarning(
                "CS2LineupFinder: the managed Trace API is unavailable on this CounterStrikeSharp build (" + reason +
                "). Crosshair aiming and map collisions are disabled, everything else works.");
        }

        System.Threading.Volatile.Write(ref _instance, backend);
        return backend;
    }
}
