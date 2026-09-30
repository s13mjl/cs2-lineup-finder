using System;

namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// Which entities a simulated throw should collide with, decided from an entity's
/// designer name. Kept as pure string logic so the rules can be unit tested without
/// a running server.
/// </summary>
/// <remarks>
/// A practice server is full of entities that must not stop a grenade: the thrower,
/// bots, other grenades in flight and dropped items. Everything else - brushes,
/// props, doors, breakables - is treated as solid, which is the conservative
/// choice: a missed bounce would make the suggested line-up wrong, while an extra
/// one only makes the solver reject a throw that would probably have worked.
/// </remarks>
public static class ThrowPredicates
{
    private static readonly string[] GrenadeNameFragments =
    {
        "grenade",
        "molotov",
        "incgrenade",
        "decoy",
        "flashbang",
        "smoke",
        "hegrenade",
    };

    /// <summary>True for a player pawn, the only entity a throw starts inside of.</summary>
    /// <param name="designerName">Entity designer name, e.g. <c>player</c>.</param>
    /// <returns><see langword="true"/> when the entity is a player.</returns>
    public static bool IsPlayerEntity(string? designerName) =>
        Matches(designerName, "player");

    /// <summary>True for a live grenade, including the one being simulated.</summary>
    /// <param name="designerName">Entity designer name, e.g. <c>smokegrenade_projectile</c>.</param>
    /// <returns><see langword="true"/> when the entity is a thrown grenade.</returns>
    public static bool IsGrenadeEntity(string? designerName)
    {
        if (string.IsNullOrEmpty(designerName))
        {
            return false;
        }

        foreach (var fragment in GrenadeNameFragments)
        {
            if (designerName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an entity should stop a grenade in flight. Players and other
    /// grenades are ignored because neither is solid to a thrown grenade in CS2.
    /// </summary>
    /// <param name="designerName">Entity designer name.</param>
    /// <returns><see langword="true"/> when the trace should report the hit.</returns>
    public static bool StopsProjectile(string? designerName) =>
        !IsPlayerEntity(designerName) && !IsGrenadeEntity(designerName);

    /// <summary>
    /// True when an entity blocks the aim direction. Other grenades do not block a
    /// player's view, so only players are ignored here.
    /// </summary>
    /// <param name="designerName">Entity designer name.</param>
    /// <returns><see langword="true"/> when the trace should report the hit.</returns>
    public static bool StopsRay(string? designerName) => !IsPlayerEntity(designerName);

    private static bool Matches(string? designerName, string expected) =>
        string.Equals(designerName, expected, StringComparison.OrdinalIgnoreCase);
}
