// <copyright file="BounceResolver.cs" company="CS2LineupFinder">
// Bounce, friction and drag rules, kept separate from the integration loop.
// </copyright>

using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core;

/// <summary>Outcome of resolving a surface contact.</summary>
public readonly struct BounceResult
{
    public BounceResult(Vec3 velocity, bool onGround, int bounceCount)
    {
        Velocity = velocity;
        OnGround = onGround;
        BounceCount = bounceCount;
    }

    public Vec3 Velocity { get; }

    /// <summary>True when the struck surface counts as walkable floor.</summary>
    public bool OnGround { get; }

    public int BounceCount { get; }
}

/// <summary>
/// Implements the contact rules for the projectile.
///
/// MEASURED on CS2 (telemetry-001 + telemetry-002, per-tick velocity at 64 Hz,
/// 7 world floor contacts): the engine reflects the WHOLE velocity vector about
/// the contact normal, scales it by the restitution, then adds a small fixed
/// impulse ALONG the normal:
///
/// <code>
/// v_post = mirror(v_in, n) * 0.45 + n * BounceNormalResidual
/// </code>
///
/// Evidence for mirror-and-scale: all 14 tangential components across the 7
/// world contacts satisfy post/pre = 0.4500000 to within CSV rounding (4e-7),
/// and a mirror is an isometry, so a whole-vector scale is the only law that
/// produces that without a separate tangential term.
///
/// Evidence for the additive residual: the normal component does NOT satisfy
/// post/pre = 0.45. Writing vz_post = 0.45*|vz_pre| + C, C is constant within
/// each capture (telemetry-001: -1.36094 across four contacts spanning 24..360
/// u/s; telemetry-002: +2.26406 across three contacts spanning 40..209 u/s) yet
/// differs between captures. GrenadeSimulator feeds the reflection the
/// HALF-TICK-BOUNDARY speed (gravity is consumed in two quanta of g*dt/2 per
/// tick; a first-half contact reflects the once-quantised speed, a second-half
/// contact the twice-quantised speed). Under that input convention ONE residual
/// value reproduces all 7 contacts to within CSV rounding:
///
/// <code>
/// C(first-half contact)  = 0.45*2.5 - 2.5 + 0.0140625 = -1.3609375
/// C(second-half contact) = 0.45*5.0 - 0   + 0.0140625 = +2.2640625
/// </code>
///
/// Both captures agree on the same residual, which is the cross-check that
/// separates it from the quantisation term. The residual is applied along the
/// contact normal for non-floor surfaces as an extrapolation: no wall contact
/// has been captured yet.
///
/// One contact in telemetry-002 (contact 8) returns 0.3364 on both tangential
/// components instead of 0.45. That contact begins EMBEDDED in a
/// PlayerClip+NpcClip+StaticLevel brush (engine IsAllSolid=1, Fraction=0 -
/// measured with the live tracer); the impact point itself is still the plane
/// floor under the brush, so the bounce law is unchanged and only the
/// elasticity is scaled, by the measured
/// <see cref="PhysicsParameters.ClipEmbeddedElasticityScale"/>. R4 gate plus
/// the 2026-10-23 first-half gate: the scale applies if and only if the
/// contact begins embedded, lands in the FIRST half of its tick (q=1), AND
/// the post-restitution reflected speed exceeds
/// <see cref="PhysicsParameters.EmbeddedScaleSpeedThreshold"/> - an earlier
/// "first embedded contact only" rule was falsified by telemetry-001 row 128,
/// where the FIRST embedded contact of the flight (152.6 u/s) is NOT scaled.
/// The q gate was forced by the 1015 palace replay: its embedded first
/// contact is q=2 and the engine kept 0.46 (762.5 -> 344.2 u/s), while the
/// speed-only rule scaled it and produced a 132.8 u miss; with the q gate the
/// live replay is 1.8 u. Detection is wired in GameWorldTracer, which reads
/// the engine's IsAllSolid flag; GrenadeSimulator owns the q and speed gates.
/// </summary>
public static class BounceResolver
{
    /// <summary>
    /// Reflects <paramref name="velocity"/> about <paramref name="normal"/>,
    /// scales the WHOLE resulting vector by the effective elasticity, and adds
    /// the measured normal residual. <paramref name="velocity"/> must already
    /// include the half-tick gravity quantum (see GrenadeSimulator); that input
    /// convention is what makes both observed deficit signatures fall out of the
    /// single residual below. See the class documentation for the evidence.
    /// </summary>
    public static Vec3 Resolve(
        Vec3 velocity,
        Vec3 normal,
        bool onGround,
        GrenadeType type,
        PhysicsParameters parameters,
        ref bool firstSmokeBounceDone,
        bool embeddedContact = false)
    {
        float restitution = parameters.Restitution.Value;
        if (embeddedContact)
        {
            restitution *= parameters.ClipEmbeddedElasticityScale.Value;
        }

        // Mirror about the normal (PhysicsClipVelocity), scale the WHOLE vector
        // by the effective elasticity, then add the measured residual impulse
        // along the normal.
        Vec3 reflected = (velocity - (normal * (2f * Vec3.Dot(velocity, normal)))) * restitution;
        reflected += normal * parameters.BounceNormalResidual.Value;

        if (!onGround)
        {
            return reflected;
        }

        bool smoke = type == GrenadeType.Smoke
            && parameters.SmokeExtraDampingEnabled.Value > 0.5f
            && !firstSmokeBounceDone;

        if (smoke)
        {
            firstSmokeBounceDone = true;
        }

        return reflected;
    }

    /// <summary>
    /// Per-tick update for the ground-contact rolling friction. Call once per
    /// tick, NOT per substep: the rate constant is fitted against tick-resolution
    /// telemetry, so re-applying it at substep frequency would over-damp by the
    /// number of calls per tick. The measured rate is zero (the CS2 engine has no
    /// rolling/sliding term between bounces; rest is reached purely through
    /// restitution decay to the sleep threshold), so this is a no-op by default
    /// and stays only as a calibration A/B seam.
    /// </summary>
    public static Vec3 ApplyRollingFriction(Vec3 velocity, Vec3 normal, float dt, PhysicsParameters parameters)
    {
        float into = Vec3.Dot(velocity, normal);
        Vec3 normalPart = normal * into;
        Vec3 tangentPart = velocity - normalPart;

        // Exponential decay on the tangential component while the projectile is
        // in floor contact. Kept as an independent parameter so calibration can
        // attribute an error to either side of the impact model.
        float rate = parameters.GroundRollFrictionRate.Value;
        float scale = rate <= 0f ? 1f : MathF.Exp(-rate * dt);
        return normalPart + (tangentPart * scale);
    }

    /// <summary>
    /// Applies the (zero by default) air drag. Quadratic and linear terms are
    /// separate parameters so calibration can attribute an error to either.
    /// </summary>
    public static void ApplyDrag(ref Vec3 velocity, float dt, PhysicsParameters parameters)
    {
        float quadratic = parameters.DragCoefficient.Value;
        float linear = parameters.LinearDrag.Value;

        if (quadratic <= 0f && linear <= 0f)
        {
            return;
        }

        float speed = velocity.Length;
        if (speed < 1e-4f)
        {
            return;
        }

        // dv/dt = -k|v|v - c|v|v_hat  ->  magnitude decay, direction preserved.
        float magnitude = speed - ((quadratic * speed * speed) + (linear * speed)) * dt;
        if (magnitude < 0f)
        {
            magnitude = 0f;
        }

        velocity = velocity * (magnitude / speed);
    }

    /// <summary>
    /// Adaptive sweep length. Fast projectiles are swept in 2 unit hops so thin
    /// geometry is never missed; slow ones use up to 16 units to cut raycasts.
    /// Ramps linearly between the two bounds over
    /// <see cref="PhysicsParameters.StepDistanceSpeedRef"/>.
    /// </summary>
    public static float StepDistance(float speed, PhysicsParameters parameters)
    {
        float minStep = parameters.MinStepDistance.Value;
        float maxStep = parameters.MaxStepDistance.Value;
        float refSpeed = parameters.StepDistanceSpeedRef.Value;

        if (refSpeed <= 0f)
        {
            return minStep;
        }

        float t = Math.Clamp(speed / refSpeed, 0f, 1f);

        // Slow -> max step, fast -> min step.
        return maxStep + ((minStep - maxStep) * t);
    }
}
