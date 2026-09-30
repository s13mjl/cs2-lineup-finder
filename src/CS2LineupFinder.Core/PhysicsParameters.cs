using System.Collections.Generic;

// <copyright file="PhysicsParameters.cs" company="CS2LineupFinder">
// Every tunable used by the simulator lives here so CalibrationRunner has a flat
// surface to search. Each constant carries a Source label; full citations and
// reasoning are in docs/PHYSICS.md.
// </copyright>

namespace CS2LineupFinder.Core;

/// <summary>
/// The full parameter set. Defaults are the current best estimates;
/// CalibrationRunner searches Min..Max over SearchableNames.
/// </summary>
public sealed partial class PhysicsParameters
{
    /// <summary>Grenade gravity, u/s^2. CS2 runs grenade gravity at 0.4x sv_gravity.</summary>
    public PhysicsParameter GrenadeGravity { get; set; } = new(
        "GrenadeGravity", 320f, 240f, 400f, SourceTier.Community,
        "memorin.app CS2 mechanics: 320 u/s^2 = 0.4 x sv_gravity 800");

    /// <summary>Quadratic air drag coefficient, 1/units. CS2 shows no measurable drag.</summary>
    public PhysicsParameter DragCoefficient { get; set; } = new(
        "DragCoefficient", 0f, 0f, 0.02f, SourceTier.Community,
        "memorin.app: no air drag; searched to confirm");

    /// <summary>Linear air drag, 1/s. Independent of DragCoefficient.</summary>
    public PhysicsParameter LinearDrag { get; set; } = new(
        "LinearDrag", 0f, 0f, 0.1f, SourceTier.Community,
        "no source; searched to confirm zero");

    /// <summary>Bounce restitution. Same on every surface including players.</summary>
    public PhysicsParameter Restitution { get; set; } = new(
        "Restitution", 0.45f, 0.35f, 0.60f, SourceTier.Valve,
        "CS:GO basecsgrenade_projectile ResolveFlyCollisionCustom: elasticity 0.45");

    /// <summary>Tangential speed retained per ground contact while rolling.</summary>
    public PhysicsParameter GroundFriction { get; set; } = new(
        "GroundFriction", 0.70f, 0.50f, 0.95f, SourceTier.Valve,
        "smokegrenade_projectile.cpp Create(): SetFriction(0.7)");

    /// <summary>Below this speed (u/s) the projectile is declared at rest.</summary>
    public PhysicsParameter RestSpeed { get; set; } = new(
        "RestSpeed", 20f, 10f, 40f, SourceTier.Valve,
        "CS:GO ResolveFlyCollisionCustom: flSpeedSqr < 30*30 => at rest");

    /// <summary>Surface normal Z above which a contact counts as walkable floor.</summary>
    public PhysicsParameter FloorNormalThreshold { get; set; } = new(
        "FloorNormalThreshold", 0.70f, 0.50f, 0.90f, SourceTier.Valve,
        "CS:GO ResolveFlyCollisionCustom: plane.normal.z > 0.7f");

    /// <summary>Full-power launch speed for a standing player, u/s.</summary>
    public PhysicsParameter FullThrowSpeed { get; set; } = new(
        "FullThrowSpeed", 675f, 600f, 720f, SourceTier.Community,
        "memorin.app: three tiers 202.5 / 438.75 / 675 u/s");

    /// <summary>Both-buttons (lob) launch speed, u/s.</summary>
    public PhysicsParameter LobThrowSpeed { get; set; } = new(
        "LobThrowSpeed", 438.75f, 380f, 480f, SourceTier.Community,
        "memorin.app: three tiers 202.5 / 438.75 / 675 u/s");

    /// <summary>Right-click (underhand) launch speed, u/s.</summary>
    public PhysicsParameter UnderhandThrowSpeed { get; set; } = new(
        "UnderhandThrowSpeed", 202.5f, 160f, 250f, SourceTier.Community,
        "memorin.app: three tiers 202.5 / 438.75 / 675 u/s");

    /// <summary>Multiplier applied to the thrower's own velocity.</summary>
    public PhysicsParameter VelocityInheritance { get; set; } = new(
        "VelocityInheritance", 1.25f, 1.00f, 1.50f, SourceTier.Community,
        "memorin.app: 1.25 x your own velocity is added");

    /// <summary>Upward bias added to the aim direction at level aim, degrees.</summary>
    public PhysicsParameter AimBiasDegrees { get; set; } = new(
        "AimBiasDegrees", 10f, 0f, 12f, SourceTier.Valve,
        "CS:GO weapon_basecsgrenade.cpp: angThrow.x remapped into -10..+10");

    /// <summary>Release offset from the eye along forward, units.</summary>
    public PhysicsParameter ReleaseOffset { get; set; } = new(
        "ReleaseOffset", 16f, 8f, 24f, SourceTier.Valve,
        "CS:GO weapon_basecsgrenade.cpp ThrowGrenade(): trace 16 units forward");

    /// <summary>Fuse time for HE / flash / smoke / decoy, seconds.</summary>
    public PhysicsParameter FuseSeconds { get; set; } = new(
        "FuseSeconds", 1.5f, 1.0f, 2.5f, SourceTier.Valve,
        "smokegrenade_projectile.cpp: SetTimer(1.5) / GRENADE_TIMER 1.5f");

    /// <summary>Extra damping applied to a smoke's first ground contact.</summary>
    public PhysicsParameter SmokeFirstBounceDamping { get; set; } = new(
        "SmokeFirstBounceDamping", 0.75f, 0.5f, 1.0f, SourceTier.Community,
        "no source; extra settle damping so smokes stop where they land");

    /// <summary>Seconds between recorded path samples.</summary>
    public PhysicsParameter SampleInterval { get; set; } = new(
        "SampleInterval", 1f / 64f, 1f / 128f, 1f / 32f, SourceTier.Valve,
        "64 Hz competitive tickrate");

    /// <summary>Minimum sweep length in units when the projectile is fast.</summary>
    public PhysicsParameter MinStepDistance { get; set; } = new(
        "MinStepDistance", 2f, 1f, 4f, SourceTier.Community,
        "task spec: fast 2 u/step, slow 16 u/step");

    /// <summary>Maximum sweep length in units when the projectile is slow.</summary>
    public PhysicsParameter MaxStepDistance { get; set; } = new(
        "MaxStepDistance", 16f, 8f, 24f, SourceTier.Community,
        "task spec: fast 2 u/step, slow 16 u/step");

    /// <summary>Speed (u/s) at which the step size reaches its maximum.</summary>
    public PhysicsParameter StepDistanceSpeedRef { get; set; } = new(
        "StepDistanceSpeedRef", 250f, 100f, 400f, SourceTier.Community,
        "fits the 2..16 u ramp between rest and typical flight speed");

    /// <summary>Physics substeps per tick, keeps the integrator stable through bounces.</summary>
    public PhysicsParameter SubstepsPerTick { get; set; } = new(
        "SubstepsPerTick", 4f, 1f, 8f, SourceTier.Community,
        "finer integration for bounce accuracy");

    /// <summary>Hard cap on simulated flight time, seconds.</summary>
    public PhysicsParameter MaxFlightSeconds { get; set; } = new(
        "MaxFlightSeconds", 12f, 6f, 20f, SourceTier.Community,
        "decoys live ~15 s but their trajectory stops far earlier");

    /// <summary>Toggle for the smoke settle model so calibration can A/B it.</summary>
    public PhysicsParameter SmokeExtraDampingEnabled { get; set; } = new(
        "SmokeExtraDampingEnabled", 1f, 0f, 1f, SourceTier.Community,
        "toggle so calibration can A/B the smoke settle model");
}

/// <summary>Name-based lookup, cloning and the calibration search mask.</summary>
public sealed partial class PhysicsParameters
{
    /// <summary>Names of the tunables CalibrationRunner is allowed to search.</summary>
    public static string[] SearchableNames { get; } =
    {
        "GrenadeGravity",
        "Restitution",
        "GroundFriction",
        "FullThrowSpeed",
        "VelocityInheritance",
        "AimBiasDegrees",
    };

    /// <summary>Every tunable, in declaration order.</summary>
    public IEnumerable<PhysicsParameter> All()
    {
        yield return GrenadeGravity;
        yield return DragCoefficient;
        yield return LinearDrag;
        yield return Restitution;
        yield return GroundFriction;
        yield return RestSpeed;
        yield return FloorNormalThreshold;
        yield return FullThrowSpeed;
        yield return LobThrowSpeed;
        yield return UnderhandThrowSpeed;
        yield return VelocityInheritance;
        yield return AimBiasDegrees;
        yield return ReleaseOffset;
        yield return FuseSeconds;
        yield return SmokeFirstBounceDamping;
        yield return SampleInterval;
        yield return MinStepDistance;
        yield return MaxStepDistance;
        yield return StepDistanceSpeedRef;
        yield return SubstepsPerTick;
        yield return MaxFlightSeconds;
        yield return SmokeExtraDampingEnabled;
    }

    /// <summary>Reads a tunable by name. Throws when the name is unknown.</summary>
    public PhysicsParameter this[string name]
    {
        get
        {
            foreach (PhysicsParameter p in All())
            {
                if (p.Name == name)
                {
                    return p;
                }
            }

            throw new System.ArgumentException($"Unknown physics parameter '{name}'", nameof(name));
        }
    }

    /// <summary>Reads a tunable value by name. Throws when the name is unknown.</summary>
    public float Get(string name) => this[name].Value;

    /// <summary>Assigns a tunable by name. Throws when the name is unknown.</summary>
    public void Set(string name, float value) => this[name].Value = value;

    /// <summary>
    /// True when the value sits inside the parameter's search window. CalibrationRunner
    /// uses this to reject trials that wander outside a physically plausible range.
    /// </summary>
    public bool InRange(string name, float value)
    {
        PhysicsParameter p = this[name];
        return value >= p.Min && value <= p.Max;
    }

    /// <summary>Deep copy, so a calibration trial can never mutate the baseline set.</summary>
    public PhysicsParameters Clone()
    {
        PhysicsParameters copy = new();
        foreach (PhysicsParameter p in All())
        {
            copy.Set(p.Name, p.Value);
        }

        return copy;
    }
}
