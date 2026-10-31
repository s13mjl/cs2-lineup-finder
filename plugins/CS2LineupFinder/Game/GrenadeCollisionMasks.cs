namespace CS2LineupFinder.Plugin.Game;

/// <summary>
/// The Source 2 collision mask a thrown grenade must use.
/// </summary>
/// <remarks>
/// Values were measured on the live server with <c>TraceMaskAudit</c>: on 319
/// quiet free-flight moves the default trace produced 38 false hits, all on
/// PlayerClip (0x10) or NpcClip (0x20) surfaces the engine grenade ignores,
/// while this mask produced zero. The audit constants
/// (<c>TraceMaskAudit.InteractWith</c>/<c>ClipBits</c>) mirror these.
/// </remarks>
public static class GrenadeCollisionMasks
{
    /// <summary>Solid(0x1) | WorldGeometry(0x4000) | StaticLevel(0x40000000).</summary>
    public const ulong InteractWith = 0x1UL | 0x4000UL | 0x40000000UL;

    /// <summary>PlayerClip(0x10) | NpcClip(0x20) - the layers the engine grenade ignores.</summary>
    public const ulong InteractExclude = 0x10UL | 0x20UL;

    /// <summary>Alias of <see cref="InteractExclude"/>, for content-bit tests on trace results.</summary>
    public const ulong ClipBits = InteractExclude;
}
