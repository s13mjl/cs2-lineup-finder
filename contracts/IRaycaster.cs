// <copyright file="IRaycaster.cs" company="CS2LineupFinder">
// Collision abstraction. The Core simulator never talks to the game engine
// directly; it asks an IRaycaster. The plugin shell supplies a CSSharp-backed
// implementation, the unit tests supply a mock. This is what keeps the unit
// tests runnable with no map files present.
// </copyright>

namespace CS2LineupFinder.Contracts;

/// <summary>Trace mask. Mirrors the Source/Source 2 MASK_* flags we care about.</summary>
[System.Flags]
public enum TraceMask
{
    None = 0,

    /// <summary>Solid world geometry: brushes and BSP world model.</summary>
    Solid = 0x4000000,

    /// <summary>Player and prop hulls (MOVE_COLLIDE_ALL).</summary>
    Player = 0x400000,

    /// <summary>Window panes; the engine treats these specially on bounce.</summary>
    Window = 0x2000000,

    /// <summary>Water and slime volumes.</summary>
    Water = 0x1000000,

    /// <summary>Everything static. Default for the simulator.</summary>
    AllStatic = Solid | Player | Window,
}

/// <summary>Result of one sweep against the world.</summary>
public readonly struct TraceResult
{
    public TraceResult(
        bool hit,
        Vector3 startPosition,
        Vector3 endPosition,
        Vector3 planeNormal,
        float fraction,
        TraceMask contents,
        bool isWorldGeometry)
    {
        Hit = hit;
        StartPosition = startPosition;
        EndPosition = endPosition;
        PlaneNormal = planeNormal;
        Fraction = fraction;
        Contents = contents;
        IsWorldGeometry = isWorldGeometry;
    }

    /// <summary>True when the sweep hit something before reaching the requested end.</summary>
    public bool Hit { get; }

    public Vector3 StartPosition { get; }

    /// <summary>Contact point. Equal to <see cref="StartPosition"/> on a miss.</summary>
    public Vector3 EndPosition { get; }

    /// <summary>
    /// Unit surface normal at the contact. <see cref="Vector3.UnitZ"/> on a flat floor.
    /// Normalized by the implementation.
    /// </summary>
    public Vector3 PlaneNormal { get; }

    /// <summary>Fraction of the sweep travelled before the hit, 0..1. 1 on a miss.</summary>
    public float Fraction { get; }

    /// <summary>Contents of the surface that was struck.</summary>
    public TraceMask Contents { get; }

    /// <summary>
    /// True when the contact was the world BSP or a static brush, false for
    /// dynamic entities. The simulator ignores dynamic entities, see PHYSICS.md.
    /// </summary>
    public bool IsWorldGeometry { get; }

    /// <summary>Builds a clean miss result.</summary>
    public static TraceResult Miss(Vector3 start, Vector3 end) =>
        new(false, start, end, Vector3.Zero, 1f, TraceMask.None, false);
}

/// <summary>Sweep of the world against a line or a small hull.</summary>
public interface IRaycaster
{
    /// <summary>
    /// Traces a ray from <paramref name="start"/> to <paramref name="end"/>.
    /// Implementations must never throw and must return a miss, not an exception,
    /// when the sweep is degenerate (zero length).
    /// </summary>
    TraceResult TraceRay(Vector3 start, Vector3 end, TraceMask mask);

    /// <summary>
    /// Traces a swept box (the grenade's 4-unit cube hull) rather than a ray.
    /// Falls back to <see cref="TraceRay"/> where the engine has no hull equivalent.
    /// </summary>
    TraceResult TraceHull(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, TraceMask mask);
}