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
    /// <summary>Grenade gravity, u/s^2. Engine constant 0.4 x sv_gravity 800.</summary>
    public PhysicsParameter GrenadeGravity { get; set; } = new(
        "GrenadeGravity", 320f, 240f, 400f, SourceTier.Leaked,
        "CS:GO basecsgh.h L66 GetGrenadeGravity() = 0.4f, declared as 'Constants for all CS Grenades' and applied to every grenade projectile (sv_gravity 800 x 0.4 = 320); memorin.app CS2 measurement agrees");

    /// <summary>Quadratic air drag coefficient, 1/units. CS2 shows no measurable drag.</summary>
    public PhysicsParameter DragCoefficient { get; set; } = new(
        "DragCoefficient", 0f, 0f, 0.02f, SourceTier.Measured,
        "0 = confirmed by CalibrationRunner over [0, 0.02] against recorded landings; real drag would measurably shorten long arcs");

    /// <summary>Linear air drag, 1/s. Independent of DragCoefficient.</summary>
    public PhysicsParameter LinearDrag { get; set; } = new(
        "LinearDrag", 0f, 0f, 0.1f, SourceTier.Measured,
        "0 = confirmed by CalibrationRunner over [0, 0.1] against recorded landings");

   /// <summary>Bounce restitution. Same on every surface including players.</summary>
   public PhysicsParameter Restitution { get; set; } = new(
       "Restitution", 0.45f, 0.35f, 0.60f, SourceTier.Valve,
        "CS:GO basecsgh.h L68 GetGrenadeElasticity() = 0.45f, used unconditionally by ResolveFlyCollisionCustom (flTotalElasticity = 0.45 x surface 1.0, clamped to 0.9); CS2 telemetry 2026-10-15 recovers 0.45 from 16 tangential components across 8 world contacts, max deviation 2e-6");

    /// <summary>
    /// Additive impulse the engine applies ALONG the contact normal after the
    /// elastic reflection, in u/s. MEASURED, not fitted to landings: with the
    /// reflection input taken at the half-tick boundary that closes the half
    /// containing the contact, one single value (+0.0140625) reproduces the
    /// normal component of all 7 world floor contacts across two independent
    /// captures to within CSV rounding (max residual 6e-5 u/s). See
    /// BounceResolver for the decomposition.
    /// </summary>
    public PhysicsParameter BounceNormalResidual { get; set; } = new(
        "BounceNormalResidual", 0.0140625f, 0f, 0.05f, SourceTier.Measured,
        "telemetry-001 (4 contacts) + telemetry-002 (3 world contacts), CS2 64 Hz: vz_post = 0.45*|vz_in| + 0.0140625 with vz_in = vz_pre - q*2.5 (q = half-ticks elapsed at the contact); residuals -1.36094/+2.26406 both decompose exactly");

    /// <summary>
    /// Multiplier on <see cref="Restitution"/> for non-world surfaces.
    /// The engine computes flTotalElasticity = flGrenadeElasticity *
    /// flSurfaceElasticity, and a mirror is an isometry, so |post|/|pre| equals
    /// that product for ANY normal. MEASURED: every world contact in the
    /// 2026-10-15 CS2 telemetry returns 0.45 (16 tangential components, max
    /// deviation 2e-6). The one contact that once read as a "non-world
    /// surface" (0.3347..0.3364 at ~705 u/s) is now attributed to an embedded
    /// clip-brush start - engine IsAllSolid=1 with PlayerClip+NpcClip
    /// contents - not to a surface material; see
    /// <see cref="ClipEmbeddedElasticityScale"/>. No genuine non-world surface
    /// has been isolated, so the world scale stays 1.0.
    /// </summary>
    public PhysicsParameter SurfaceElasticityScale { get; set; } = new(
        "SurfaceElasticityScale", 1.00f, 0.20f, 1.00f, SourceTier.Measured,
        "1.0 = world surface; CS2 telemetry 2026-10-15 telemetry-002 first contact measured 0.3347/0.45 = 0.7439 for the non-world surface it struck, fitted to 0.00000 error");

    /// <summary>
    /// Multiplier on <see cref="Restitution"/> for contacts that begin embedded
    /// in solid world geometry (engine IsAllSolid=1 with clip content bits).
    /// MEASURED: telemetry-002 contact 8 (CSV row 8) is the only one of 12
    /// captured contacts whose tangential ratio is not 0.45 - both horizontal
    /// components return 0.336431361 (7-digit agreement), and the tracer shows
    /// the tick start embedded in a PlayerClip+NpcClip+StaticLevel brush
    /// (IsAllSolid=1, Fraction=0). The impact POINT itself is the plane floor
    /// beneath the brush (parabolic contact fit lands on z=-189.9668 vs face
    /// -189.9688, 0.002u), so the position stays; only the elasticity changes.
    /// 0.7476252 = 0.336431361 / 0.45. The ratio does not appear in the
    /// official surface property table (vsurf_data.txt, 164 entries checked),
    /// so it is recorded as an engine-internal embedded-contact rule.
    /// </summary>
    public PhysicsParameter ClipEmbeddedElasticityScale { get; set; } = new(
        "ClipEmbeddedElasticityScale", 0.7476252f, 0.5f, 1.0f, SourceTier.Measured,
        "telemetry-002 contact 8 embedded clip-brush bounce: tangential ratio 0.336431361 on both components (7-digit agreement), tracer IsAllSolid=1 with PlayerClip+NpcClip+StaticLevel at the tick start, ratio to base elasticity 0.336431361/0.45 = 0.7476; not in official surfaceproperties (vsurf_data.txt 164 entries)");

    /// <summary>
    /// Minimum post-restitution reflected speed (u/s) for an embedded contact
    /// to receive <see cref="ClipEmbeddedElasticityScale"/>. R4 rule plus the
    /// 2026-10-23 first-half gate: the reduced elasticity applies if and only
    /// if the contact begins embedded, lands in the FIRST half of its tick
    /// (q=1), AND the reflected speed exceeds this threshold. Evidence across
    /// all captured embedded contacts: telemetry-002 row 8 (embedded, q=1,
    /// 317.9 u/s -> scaled 0.3364314) versus telemetry-001 row 128 (embedded
    /// FIRST contact, 152.6 u/s -> engine records 0.4499998, NOT scaled) and
    /// telemetry-002 rows 97/136/154 (embedded, 101.6/46.6/21.5 u/s -> all
    /// 0.45). The q gate was forced by the 1015 palace sample: its embedded
    /// first contact is q=2 (fraction 0.758) and the engine kept 0.46
    /// (762.5 -> 344.2 u/s), while R4 alone scaled it to 0.336 and left the
    /// simulated grenade on the lower shelf, a 132.8 u live replay miss. With
    /// the q=1 gate the same live replay is 1.8 u and the eight-sample mean
    /// drops from 30.8 u to 5.5 u.
    /// The constant is the speed threshold of ResolveFlyCollisionCustom in the
    /// CS:GO leak (basecsg.cpp, sqrt(96000) ~= 309.8387); its ROLE as the gate
    /// for the embedded elasticity scale is a hypothesis that fits every
    /// measured contact and is confirmed by live replay, not by the leak.
    /// A mirror preserves speed, so the post-restitution speed is
    /// Restitution x |inputVelocity|; the leak's speed test runs on the
    /// reflected vector, not the incident one.
    /// </summary>
    public PhysicsParameter EmbeddedScaleSpeedThreshold { get; set; } = new(
        "EmbeddedScaleSpeedThreshold", 309.8387f, 100f, 500f, SourceTier.Leaked,
        "sqrt(96000) from CS:GO leaked basecsg.cpp ResolveFlyCollisionCustom speed threshold; role as embedded-elasticity gate is a hypothesis confirmed by live replay of telemetry-001/002 (R4, 2026-10-19)");


    /// <summary>
    /// Engine bounce padding (ResolveFlyCollisionCustom): a floor bounce that
    /// comes off fast AND steep was thought to lose an extra (1.5 - alongDist)
    /// factor. DISABLED: the CS2 telemetry does not support it. telemetry-002
    /// first contact satisfies both trigger conditions (post-bounce speed
    /// 344 u/s > 310, alongDist 0.7279 > 0.5), which predicts a 0.772 multiplier
    /// and a post-bounce speed of 266 u/s; the recorded speed is 235.79 u/s,
    /// i.e. a ratio of 0.3347 that the plain whole-vector law with the measured
    /// surface elasticity already reproduces exactly. Adding padding on top
    /// double-counts the loss, so the term stays off unless a capture shows a
    /// contact the plain law cannot fit.
    /// </summary>
    public PhysicsParameter BouncePaddingEnabled { get; set; } = new(
        "BouncePaddingEnabled", 0f, 0f, 1f, SourceTier.Valve,
        "off: CS2 telemetry 2026-10-15 telemetry-002 first contact triggers the leak's padding conditions yet is fitted exactly by the plain whole-vector law, so the term double-counts; kept toggleable for A/B replay");

    /// <summary>
    /// Exponential decay rate of tangential speed while in floor contact, 1/s.
    /// MEASURED ZERO: the 2026-10-15 CS2 telemetry captures (telemetry-001, 742
    /// ticks; telemetry-002, 330 ticks) hold the tangential component bit-exact
    /// through every inter-bounce flight, and every bounce scales the WHOLE
    /// velocity vector by a single factor: the 8 world contacts return 0.4500000
    /// on all 16 tangential components (max deviation 2e-6) and the one
    /// non-world contact in telemetry-002 returns 0.334678 on the whole vector.
    /// The engine therefore has no rolling/sliding friction term between
    /// bounces; rest is reached purely through restitution decay to the 20 u/s
    /// sleep threshold.
    /// </summary>
    public PhysicsParameter GroundRollFrictionRate { get; set; } = new(
        "GroundRollFrictionRate", 0.0f, 0.0f, 16f, SourceTier.Measured,
        "2026-10-15 CS2 tick telemetry: tangential speed bit-exact between bounces and bounce scales match a single restitution on the full vector, so the ground-contact decay rate is zero (no rolling/sliding friction term in the CS2 engine)");

    /// <summary>Below this speed (u/s) the projectile is declared at rest.</summary>
    public PhysicsParameter RestSpeed { get; set; } = new(
        "RestSpeed", 20f, 10f, 40f, SourceTier.Leaked,
        "CS:GO leaked basecsg.cpp L349: const float kSleepVelocity = 20.0f (the 30*30 in older comments was a transcription error)");

    /// <summary>Surface normal Z above which a contact counts as walkable floor.</summary>
    public PhysicsParameter FloorNormalThreshold { get; set; } = new(
        "FloorNormalThreshold", 0.70f, 0.50f, 0.90f, SourceTier.Valve,
        "CS:GO ResolveFlyCollisionCustom: plane.normal.z > 0.7f");

    /// <summary>Full-power launch speed for a standing player, u/s.</summary>
    public PhysicsParameter FullThrowSpeed { get; set; } = new(
        "FullThrowSpeed", 675f, 600f, 720f, SourceTier.Valve,
        "Valve CS2 weapons.vdata m_flThrowVelocity = 750.0 (weapon_smokegrenade_prefab block L4321; HE L409; flashbang L3936; all grenade prefabs are 750) x leaked wpncsg.cpp L586 flVel = clamp(kBaseVelocity * 0.9, 15, 750) => 750 * 0.9 = 675");

    /// <summary>Both-buttons (lob) launch speed, u/s.</summary>
    public PhysicsParameter LobThrowSpeed { get; set; } = new(
        "LobThrowSpeed", 438.75f, 380f, 480f, SourceTier.Leaked,
        "wpncsg.cpp L593 flVel *= Lerp(strength, 0.3, 1) with L361 flIdealThrowStrength = 0.5 => strength 0.65: 675 * 0.65 = 438.75; memorin.app measured the same");

    /// <summary>Right-click (underhand) launch speed, u/s.</summary>
    public PhysicsParameter UnderhandThrowSpeed { get; set; } = new(
        "UnderhandThrowSpeed", 202.5f, 160f, 250f, SourceTier.Leaked,
        "wpncsg.cpp L82 GRENADE_SECONDARY_DAMPENING 0.3 with L593 flVel *= Lerp(strength, 0.3, 1) at full strength => 675 * 0.3 = 202.5; memorin.app measured the same");

    /// <summary>Multiplier applied to the thrower's own velocity.</summary>
    public PhysicsParameter VelocityInheritance { get; set; } = new(
        "VelocityInheritance", 1.25f, 1.00f, 1.50f, SourceTier.Valve,
        "CS:GO leaked wpncsg.cpp L614: vecThrow = vForward * flVel + pPlayer->GetAbsVelocity() * 1.25");

    /// <summary>Upward bias added to the aim direction at level aim, degrees.</summary>
    public PhysicsParameter AimBiasDegrees { get; set; } = new(
        "AimBiasDegrees", 10f, 0f, 12f, SourceTier.Leaked,
        "CS:GO leaked weapon_basecsgrenade.cpp L580: angThrow[PITCH] -= 10.0f * (90.0f - fabsf(angThrow[PITCH])) / 90.0f 鈥?the constant is the level-aim maximum; ApplyAimBias implements the full pitch-dependent remap. The 2026-10-15 CS2 telemetry shows launch pitches of 67.7 up / 46.7 down for unknown view angles, so the CS2-specific value is judged by the recorded-landing bias sweep, not by these two points");

    /// <summary>Release offset from the eye along forward, units.</summary>
    public PhysicsParameter ReleaseOffset { get; set; } = new(
        "ReleaseOffset", 16f, 8f, 24f, SourceTier.Valve,
        "CS:GO weapon_basecsgrenade.cpp ThrowGrenade(): trace 16 units forward");

    /// <summary>Fuse time for HE / flash / smoke / decoy, seconds.</summary>
    public PhysicsParameter FuseSeconds { get; set; } = new(
        "FuseSeconds", 1.5f, 1.0f, 2.5f, SourceTier.Valve,
        "smokegrenade_projectile.cpp: SetTimer(1.5) / GRENADE_TIMER 1.5f");

    /// <summary>
    /// Extra damping applied to a smoke's first ground contact. Off by default:
    /// with a faithful no-tangent-loss bounce the extra 0.75 shortened steep
    /// throws by ~35u in replay.
    /// </summary>
    public PhysicsParameter SmokeFirstBounceDamping { get; set; } = new(
        "SmokeFirstBounceDamping", 1.00f, 0.30f, 1.00f, SourceTier.Community,
        "1.0 = off; extra settle damping only if replay proves it needed");

    /// <summary>Seconds between recorded path samples.</summary>
    public PhysicsParameter SampleInterval { get; set; } = new(
        "SampleInterval", 1f / 64f, 1f / 128f, 1f / 32f, SourceTier.Valve,
        "64 Hz competitive tickrate");

    /// <summary>
    /// Minimum sweep hop length, units. GUESSED numerical-protection budget:
    /// 2u hops at 675 u/s cap a substep at 128 raycasts/s. Numerics knob:
    /// full-range sweep moved the replay landing by at most 0.98u (measured).
    /// </summary>
    public PhysicsParameter MinStepDistance { get; set; } = new(
        "MinStepDistance", 2f, 1f, 4f, SourceTier.Measured,
        "numerics knob, not physics: full-range sweep min 1/2/4 x max 8/12/16/24 x ref 100/250/400 moved the replay landing by at most 0.98u (floor) and 0.17u (wall) per DiscretizationConvergenceTests 2026-10-14");

    /// <summary>
    /// Maximum sweep hop length, units. Performance budget: at rest the
    /// projectile cannot cross an 8u thin wall unseen at 16u hops.
    /// </summary>
    public PhysicsParameter MaxStepDistance { get; set; } = new(
        "MaxStepDistance", 16f, 8f, 24f, SourceTier.Measured,
        "numerics knob: same 36-combination sweep, max landing shift 0.98u; see MinStepDistance");

    /// <summary>
    /// Speed (u/s) at which the step size reaches its maximum.
    /// </summary>
    public PhysicsParameter StepDistanceSpeedRef { get; set; } = new(
        "StepDistanceSpeedRef", 250f, 100f, 400f, SourceTier.Measured,
        "numerics knob: same 36-combination sweep, max landing shift 0.98u; see MinStepDistance");

    /// <summary>Physics substeps per tick, keeps the integrator stable through bounces.</summary>
    public PhysicsParameter SubstepsPerTick { get; set; } = new(
        "SubstepsPerTick", 8f, 1f, 16f, SourceTier.Measured,
        "DEPRECATED, no longer read by the simulator: the 2026-10-19 telemetry forensics fixed the engine structure at two half-tick steps per tick (the bounce law consumes gravity in g*dt/2 = 2.5 u/s quanta and free flight reproduces the exact parabola), so the value is ignored; kept only so old configs load");

    /// <summary>Hard cap on simulated flight time, seconds.</summary>
    public PhysicsParameter MaxFlightSeconds { get; set; } = new(
        "MaxFlightSeconds", 12f, 9f, 20f, SourceTier.Measured,
        "numerics guard, not physics: across smoke/flash/HE/molotov over the full solver elevation envelope (-15..85 deg) the slowest flight is a 85-deg smoke at 8.04s (FlightCapIrrelevanceTests, re-measured 2026-10-18 after the whole-vector restitution law landed; the old 6.15s figure predates it), and cap 12s vs 20s reproduces every landing exactly; the 9s floor keeps the searchable range above every reachable flight time");

    /// <summary>Toggle for the smoke settle model so calibration can A/B it.</summary>
    public PhysicsParameter SmokeExtraDampingEnabled { get; set; } = new(
        "SmokeExtraDampingEnabled", 0f, 0f, 1f, SourceTier.Community,
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
        "FullThrowSpeed",
        "VelocityInheritance",
        "AimBiasDegrees",
        "GroundRollFrictionRate",
    };

    /// <summary>Every tunable, in declaration order.</summary>
    public IEnumerable<PhysicsParameter> All()
    {
        yield return GrenadeGravity;
        yield return DragCoefficient;
        yield return LinearDrag;
        yield return Restitution;
        yield return SurfaceElasticityScale;
        yield return ClipEmbeddedElasticityScale;
        yield return EmbeddedScaleSpeedThreshold;

        yield return BouncePaddingEnabled;
        yield return GroundRollFrictionRate;
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
        yield return BounceNormalResidual;
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
