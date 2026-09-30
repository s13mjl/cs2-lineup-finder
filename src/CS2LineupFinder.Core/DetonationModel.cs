// <copyright file="DetonationModel.cs" company="CS2LineupFinder">
// Per-grenade fuse and trigger rules.
// </copyright>

using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core;

/// <summary>Why a grenade went off.</summary>
public enum DetonationReason
{
    /// <summary>Still flying or rolling.</summary>
    None = 0,

    /// <summary>Fuse expired.</summary>
    FuseExpired = 1,

    /// <summary>Molotov touched a surface after its arm time.</summary>
    Impact = 2,

    /// <summary>Speed fell below the rest threshold.</summary>
    AtRest = 3,
}

/// <summary>Decision about whether and where a grenade detonates.</summary>
public readonly struct DetonationState
{
    public DetonationState(bool detonated, DetonationReason reason, float time)
    {
        Detonated = detonated;
        Reason = reason;
        Time = time;
    }

    public bool Detonated { get; }

    public DetonationReason Reason { get; }

    /// <summary>Seconds since release.</summary>
    public float Time { get; }
}

/// <summary>
/// Fuse and trigger rules per grenade type.
///
/// Source: <c>game/server/cstrike/smokegrenade_projectile.cpp</c> (CS:GO leak):
/// <code>
/// pGrenade->SetTimer(1.5);
/// void CSmokeGrenadeProjectile::Think_Detonate() {
///     if (GetAbsVelocity().Length() > 0.1) { SetNextThink(curtime + 0.2); return; }
///     ... spawn smoke ...
/// }
/// </code>
/// So a smoke only blooms once it has essentially stopped moving, even though its
/// fuse runs concurrently. HE, flash and decoy instead explode on the timer whether
/// or not they are still moving. Molotov detonates on contact after a short arm time.
/// </summary>
public static class DetonationModel
{
    /// <summary>Smoke's rest threshold is much stricter than the generic rest speed.</summary>
    public const float SmokeRestSpeed = 0.1f;

    /// <summary>How often the engine re-checks a stopped-molotov, seconds.</summary>
    public const float ThinkInterval = 0.2f;

    /// <summary>Molotov / incendiary arm time before it will stick, seconds.</summary>
    public const float MolotovArmSeconds = 0.2f;

    /// <summary>
    /// Evaluates the trigger rules.
    /// </summary>
    /// <param name="type">Which grenade this is.</param>
    /// <param name="elapsed">Seconds since release.</param>
    /// <param name="speed">Current speed in u/s.</param>
    /// <param name="atRest">True when the movement loop says the projectile stopped.</param>
    /// <param name="justContacted">True on the substep where a surface was struck.</param>
    /// <param name="fuseSeconds">Fuse length from the parameter set.</param>
    public static DetonationState Evaluate(
        GrenadeType type,
        float elapsed,
        float speed,
        bool atRest,
        bool justContacted,
        float fuseSeconds)
    {
        // Molotov and incendiary are contact-fused with an arming delay.
        if (type == GrenadeType.Molotov)
        {
            if (justContacted && elapsed >= MolotovArmSeconds)
            {
                return new DetonationState(true, DetonationReason.Impact, elapsed);
            }

            return new DetonationState(false, DetonationReason.None, elapsed);
        }

        bool fuseDone = elapsed >= fuseSeconds;
        if (!fuseDone)
        {
            return new DetonationState(false, DetonationReason.None, elapsed);
        }

        // A smoke waits for the projectile to actually settle, which can add
        // up to a couple of ThinkInterval slop past the fuse.
        if (type == GrenadeType.Smoke)
        {
            bool settled = atRest || speed <= SmokeRestSpeed;
            return settled
                ? new DetonationState(true, DetonationReason.AtRest, elapsed)
                : new DetonationState(false, DetonationReason.None, elapsed);
        }

        // HE, flash and decoy pop on the timer regardless of motion.
        return new DetonationState(true, DetonationReason.FuseExpired, elapsed);
    }
}
