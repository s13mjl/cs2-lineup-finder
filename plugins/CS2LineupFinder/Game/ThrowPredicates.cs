using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// Which entities a simulated throw should collide with, decided from an entity's
/// designer name. Kept as pure string logic so the rules can be unit tested without
/// a running server.
/// </summary>
/// <remarks>
/// Thin forwarder to <see cref="TraceFilters"/>, which is where the rule itself
/// lives. It used to be a private copy, and while the simulator in Core used its own
/// entity-index test the two drifted: the tracer let a func_brush stop a sweep and
/// returned it, and the simulator then discarded it because its index was positive,
/// so grenades passed through static brush geometry. One shared rule is the only way
/// to keep those two answers identical.
/// </remarks>
public static class ThrowPredicates
{
    /// <summary>True for a player pawn, the only entity a throw starts inside of.</summary>
    /// <param name="designerName">Entity designer name, e.g. <c>player</c>.</param>
    /// <returns><see langword="true"/> when the entity is a player.</returns>
    public static bool IsPlayerEntity(string? designerName) =>
        TraceFilters.IsPlayerEntity(designerName);

    /// <summary>True for a live grenade, including the one being simulated.</summary>
    /// <param name="designerName">Entity designer name, e.g. <c>smokegrenade_projectile</c>.</param>
    /// <returns><see langword="true"/> when the entity is a grenade in flight.</returns>
    public static bool IsGrenadeEntity(string? designerName)
    {
        return TraceFilters.IsGrenadeEntity(designerName);
    }

    /// <summary>
    /// True when an entity should stop a grenade in flight. Players and other
    /// grenades are ignored because neither is solid to a thrown grenade in CS2.
    /// </summary>
    /// <param name="designerName">Entity designer name.</param>
    /// <returns><see langword="true"/> when the trace should report the hit.</returns>
    public static bool StopsProjectile(string? designerName) =>
        TraceFilters.StopsProjectile(designerName);

    /// <summary>
    /// True when an entity blocks the aim direction. Other grenades do not block a
    /// player's view, so only players are ignored here.
    /// </summary>
    /// <param name="designerName">Entity designer name.</param>
    /// <returns><see langword="true"/> when the trace should report the hit.</returns>
    public static bool StopsRay(string? designerName) => !IsPlayerEntity(designerName);
}
