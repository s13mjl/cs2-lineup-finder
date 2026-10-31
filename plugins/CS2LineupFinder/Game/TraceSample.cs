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
    /// <param name="contentsRaw">Raw engine Contents bits at the contact, 0 when unknown.</param>
    /// <param name="isEmbedded">True when the sweep started embedded in solid world geometry.</param>
    public TraceSample(bool didHit, Vec3 position, Vec3 normal, float fraction, int entityIndex = -1, string? designerName = null, ulong contentsRaw = 0, bool isEmbedded = false)
    {
        DidHit = didHit;
        Position = position;
        Normal = normal;
        Fraction = fraction;
        EntityIndex = entityIndex;
        DesignerName = designerName;
        ContentsRaw = contentsRaw;
        IsEmbedded = isEmbedded;
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

    /// <summary>Raw engine Contents bits at the contact, 0 when the backend could not read them.</summary>
    public ulong ContentsRaw { get; }

    /// <summary>True when the sweep started embedded in solid world geometry (engine IsAllSolid=1).</summary>
    public bool IsEmbedded { get; }

    /// <summary>A clean miss that travelled all the way to <paramref name="end"/>.</summary>
    /// <param name="end">Where the sweep would have stopped.</param>
    /// <returns>The miss sample.</returns>
    public static TraceSample Miss(Vec3 end) => new(false, end, Vec3.UnitZ, 1f);

    /// <summary>Converts to the engine independent contract type.</summary>
    /// <returns>The same result as a <see cref="TraceHit"/>.</returns>
    public TraceHit ToTraceHit() => new(DidHit, Position, Normal, Fraction, EntityIndex, DesignerName, ContentsRaw, IsEmbedded);
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

    /// <summary>Whether <see cref="Trace(Vec3, Vec3, float, int, ulong, ulong)"/> can honour the mask.</summary>
    bool SupportsMaskedTrace => false;

    /// <summary>
    /// Runs one sweep with an explicit Source 2 collision filter.
    /// </summary>
    /// <remarks>
    /// The default forwards to the mask-less overload, so backends without mask
    /// support (and test fakes) keep working; callers gate on
    /// <see cref="SupportsMaskedTrace"/> and fall back otherwise.
    /// </remarks>
    /// <param name="start">Sweep start in world units.</param>
    /// <param name="end">Sweep end in world units.</param>
    /// <param name="hullRadius">Sphere radius swept along the segment; 0 traces a line.</param>
    /// <param name="ignoreEntityIndex">Entity index to skip, normally the thrower.</param>
    /// <param name="interactsWith">Content layers that count as solid.</param>
    /// <param name="interactsExclude">Content layers to remove first.</param>
    /// <returns>The sweep result, never <see langword="null"/>.</returns>
    TraceSample Trace(Vec3 start, Vec3 end, float hullRadius, int ignoreEntityIndex, ulong interactsWith, ulong interactsExclude)
        => Trace(start, end, hullRadius, ignoreEntityIndex);
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
    /// <remarks>
    /// Backends that can see the engine's raw trace fields also expose
    /// <see cref="ITraceProbe.DescribeTrace"/>, which is how the engine's meaning of
    /// those fields gets measured instead of assumed. Probe support is optional: a
    /// backend without it still traces, it just cannot answer diagnostic questions.
    /// </remarks>
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

    /// <summary>
    /// Returns the probe surface of a backend, or <see langword="null"/> when the
    /// backend cannot report raw engine trace fields.
    /// </summary>
    /// <param name="backend">Backend to inspect.</param>
    /// <returns>The probe, or <see langword="null"/>.</returns>
    public static ITraceProbe? AsProbe(ITraceBackend backend) => backend as ITraceProbe;
}

/// <summary>
/// A trace backend that can report the engine's raw trace fields verbatim.
/// </summary>
/// <remarks>
/// The engine owns the meaning of every field it returns: CounterStrikeSharp's
/// managed <c>Trace</c> type only marshals <c>CGameTrace</c> across the script
/// boundary without interpreting it. A backend that implements this interface hands
/// those raw values back so a caller can establish, by measurement, what the engine
/// fills in - which is the only honest way to decide which field the simulator
/// should read.
/// </remarks>
public interface ITraceProbe
{
    /// <summary>Runs one raw trace and reports every field the engine filled in.</summary>
    /// <param name="start">Sweep start in world units.</param>
    /// <param name="end">Sweep end in world units.</param>
    /// <param name="hullRadius">Hull half-extent, 0 traces a line.</param>
    /// <returns>One diagnostic line with every raw field.</returns>
    string DescribeTrace(Vec3 start, Vec3 end, float hullRadius);

    /// <summary>
    /// Same as <see cref="DescribeTrace"/> but with an explicit Source 2
    /// collision filter: <paramref name="interactsWith"/> selects which content
    /// layers count as solid, <paramref name="interactsExclude"/> removes layers
    /// from that selection. Values come from the measured Contents enum.
    /// </summary>
    string DescribeTraceMasked(Vec3 start, Vec3 end, float hullRadius, ulong interactsWith, ulong interactsExclude);

    /// <summary>
    /// Runs the sweep exactly as the unmodified backend does (no explicit
    /// filter object) and returns only the contact verdict.
    /// </summary>
    bool RawHit(Vec3 start, Vec3 end, float hullRadius);

    /// <summary>
    /// Runs the same masked sweep as <see cref="DescribeTraceMasked"/> but
    /// returns only the engine's contact verdict, so code can match trace
    /// contacts against recorded events without parsing formatted text.
    /// </summary>
    bool MaskedHit(Vec3 start, Vec3 end, float hullRadius, ulong interactsWith, ulong interactsExclude);

    /// <summary>
    /// Runs the unmodified sweep and names the entity the engine says it hit -
    /// the designer name and index straight from <c>TraceResult.HitEntity</c>,
    /// or a fixed marker when the contact is the world or there is no contact.
    /// </summary>
    string DescribeHitEntity(Vec3 start, Vec3 end, float hullRadius);
}
