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
    /// <summary>Solid(0x1) | WorldGeometry(0x4000) | StaticLevel(0x40000000) | CsgoGrenadeClip(1&lt;&lt;33).</summary>
    /// <remarks>
    /// The grenade-clip layer was added from the 2026-11-01 live-map replay of
    /// telemetry-020: the mask without it missed four real bounces at the
    /// landing area, exactly the CONTENTS_GRENADECLIP term of the leaked
    /// PhysicsSolidMaskForEntity mask.
    /// </remarks>
    public const ulong InteractWith = 0x1UL | 0x4000UL | 0x40000000UL | 0x200000000UL;

    /// <summary>PlayerClip(0x10) | NpcClip(0x20) | Sky(0x8) - the layers the engine grenade ignores.</summary>
    /// <remarks>
    /// Sky was added from the same replay: the captured throw flew straight
    /// through a Sky+StaticLevel brush at ~700 u altitude while the simulator
    /// bounced off it at tick 73.
    /// </remarks>
    public const ulong InteractExclude = 0x10UL | 0x20UL | 0x8UL;

    /// <summary>PlayerClip|NpcClip, for content-bit tests on trace results.</summary>
    public const ulong ClipBits = 0x10UL | 0x20UL;
}
