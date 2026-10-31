namespace CS2LineupFinder.Contracts;

/// <summary>
/// Which entities a simulated throw should collide with, decided from an entity's
/// designer name. Kept as pure string logic so the rule can be unit tested without
/// a running server, and shared by every layer that issues a sweep.
/// </summary>
/// <remarks>
/// This lives in the contracts on purpose. The simulator in Core and the tracer
/// in the plugin both have to answer "does this hit stop a grenade?", and when the
/// two answer differently the grenade silently passes through static brush geometry:
/// the tracer lets a func_brush stop the sweep and returns it, while the simulator
/// was discarding anything whose entity index was not world BSP. Putting the rule
/// in one place removes that class of bug instead of documenting it.
/// </remarks>
public static class TraceFilters
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

    /// <summary>
    /// Dynamic non-blocking entities a grenade flies through: dropped weapons
    /// and items, ragdolls, chickens and hostages. They are deliberately not
    /// solid to a grenade, and stopping on one made a live trace stick the
    /// simulated projectile where the real throw rolled on.
    /// </summary>
    private static readonly string[] NonBlockingFragments =
    {
        "weapon_",
        "item_",
        "ragdoll",
        "chicken",
        "hostage",
    };

    /// <summary>True for a player pawn, the only entity a throw starts inside of.</summary>
    /// <param name="designerName">Entity designer name, e.g. <c>player</c>.</param>
    /// <returns><see langword="true"/> when the entity is a player.</returns>
    public static bool IsPlayerEntity(string? designerName) =>
        string.Equals(designerName, "player", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a live grenade, including the one being simulated.</summary>
    /// <param name="designerName">Entity designer name, e.g. <c>smokegrenade_projectile</c>.</param>
    /// <returns><see langword="true"/> when the entity is a grenade in flight.</returns>
    public static bool IsGrenadeEntity(string? designerName)
    {
        if (string.IsNullOrEmpty(designerName))
        {
            return false;
        }

        foreach (string fragment in GrenadeNameFragments)
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
        !IsPlayerEntity(designerName)
        && !IsGrenadeEntity(designerName)
        && !IsNonBlockingDynamic(designerName);

    /// <summary>
    /// True when an entity blocks the aim direction. Players and the dynamic
    /// items a crosshair should see through are ignored.
    /// </summary>
    /// <param name="designerName">Entity designer name.</param>
    /// <returns><see langword="true"/> when the trace should report the hit.</returns>
    public static bool StopsRay(string? designerName) =>
        !IsPlayerEntity(designerName) && !IsNonBlockingDynamic(designerName);

    /// <summary>True for dropped weapons, items, ragdolls, chickens and hostages.</summary>
    private static bool IsNonBlockingDynamic(string? designerName)
    {
        if (string.IsNullOrEmpty(designerName))
        {
            return false;
        }

        foreach (string fragment in NonBlockingFragments)
        {
            if (designerName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
