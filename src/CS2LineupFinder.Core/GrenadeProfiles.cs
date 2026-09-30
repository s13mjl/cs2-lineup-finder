using CS2LineupFinder.Contracts;
using System.Collections.Generic;

namespace CS2LineupFinder.Core;

/// <summary>
/// Default physics profiles per grenade family. Values come from the Valve-sourced
/// parameter table in <c>docs/PHYSICS.md</c> (projectile gravity 320 u/s^2 =
/// 0.4 x sv_gravity, elasticity 0.45) and the measured throw-speed ladder
/// (675 / 438.75 / 202.5 u/s). They are a calibration starting point: every
/// entry is expected to be refined by in-game recordings via
/// <c>CalibrationRunner</c>.
/// </summary>
public static class GrenadeProfiles
{
    private static readonly IReadOnlyDictionary<GrenadeType, GrenadeProfile> Table =
        new Dictionary<GrenadeType, GrenadeProfile>
        {
            [GrenadeType.Smoke] = new GrenadeProfile
            {
                ItemName = "weapon_smokegrenade",
                GrenadeType = GrenadeType.Smoke,
                PrimaryThrowVelocity = 675f,
                SecondaryThrowVelocity = 202.5f,
                BothButtonsThrowVelocity = 438.75f,
                GravityScale = 0.4f,
                Elasticity = 0.45f,
                FuseTime = 0f,
                DetonatesOnImpact = false,
            },
            [GrenadeType.Flash] = new GrenadeProfile
            {
                ItemName = "weapon_flashbang",
                GrenadeType = GrenadeType.Flash,
                PrimaryThrowVelocity = 675f,
                SecondaryThrowVelocity = 202.5f,
                BothButtonsThrowVelocity = 438.75f,
                GravityScale = 0.4f,
                Elasticity = 0.45f,
                FuseTime = 1.6f,
                DetonatesOnImpact = false,
            },
            [GrenadeType.He] = new GrenadeProfile
            {
                ItemName = "weapon_hegrenade",
                GrenadeType = GrenadeType.He,
                PrimaryThrowVelocity = 675f,
                SecondaryThrowVelocity = 202.5f,
                BothButtonsThrowVelocity = 438.75f,
                GravityScale = 0.4f,
                Elasticity = 0.45f,
                DetonatesOnImpact = true,
            },
            [GrenadeType.Molotov] = new GrenadeProfile
            {
                ItemName = "weapon_molotov",
                GrenadeType = GrenadeType.Molotov,
                PrimaryThrowVelocity = 675f,
                SecondaryThrowVelocity = 202.5f,
                BothButtonsThrowVelocity = 438.75f,
                GravityScale = 0.4f,
                Elasticity = 0.45f,
                DetonatesOnImpact = true,
            },
        };

    /// <summary>
    /// Returns the default profile for a grenade family. Every family has an
    /// entry, so the only failure mode is an out-of-range enum value, which the
    /// dictionary lookup surfaces as a clear exception rather than a silent
    /// 800 u/s^2 player-gravity simulation.
    /// </summary>
    /// <param name="grenadeType">Grenade family to look up.</param>
    /// <returns>The default profile for the family.</returns>
    public static GrenadeProfile Get(GrenadeType grenadeType) => Table[grenadeType];
}
