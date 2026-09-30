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
/// Source, CS:GO <c>basecsgrenade_projectile.cpp :: ResolveFlyCollisionCustom</c>:
/// <code>
/// float flTotalElasticity = flGrenadeElasticity * flSurfaceElasticity;   // 0.45 for world
/// PhysicsClipVelocity(vecVelocity, trace.plane.normal, vecAbsVelocity, 2.0f);
/// vecAbsVelocity *= flTotalElasticity;
/// if (trace.plane.normal.z > 0.7f) { /* floor: continue moving along the surface */ }
/// </code>
/// So the rule is a plain mirror about the normal followed by a 0.45 scale, with
/// no surface-specific term. That matches the CS2 finding that every surface
/// returns 0.45x the incoming speed.
/// </summary>
public static class BounceResolver
{
    /// <summary>
    /// Reflects <paramref name="velocity"/> about <paramref name="normal"/>, scales
    /// by restitution, then applies tangential damping.
    ///
    /// A smoke's first ground contact takes extra tangential damping so it settles
    /// near where it lands instead of skating across the floor. There is no Valve
    /// source for this; it is flagged as Community in PHYSICS.md and calibration
    /// can switch it off via <see cref="PhysicsParameters.SmokeExtraDampingEnabled"/>.
    /// </summary>
    public static Vec3 Resolve(
        Vec3 velocity,
        Vec3 normal,
        bool onGround,
        GrenadeType type,
        PhysicsParameters parameters,
        ref bool firstSmokeBounceDone)
    {
        float restitution = parameters.Restitution.Value;
        // Mirror about the normal, then scale: v - 2 (v.n) n, times restitution.
        Vec3 reflected = (velocity - (normal * (2f * Vec3.Dot(velocity, normal)))) * restitution;

        if (!onGround)
        {
            return reflected;
        }

        // Split into normal and tangential parts; damp only the tangential one.
        float into = Vec3.Dot(reflected, normal);
        Vec3 normalPart = normal * into;
        Vec3 tangentPart = reflected - normalPart;

        float retain = parameters.GroundFriction.Value;

        bool smoke = type == GrenadeType.Smoke
            && parameters.SmokeExtraDampingEnabled.Value > 0.5f
            && !firstSmokeBounceDone;

        if (smoke)
        {
            retain *= parameters.SmokeFirstBounceDamping.Value;
            firstSmokeBounceDone = true;
        }

        return normalPart + (tangentPart * retain);
    }

    /// <summary>
    /// Per-tick rolling friction while the projectile is in contact with the floor.
    /// Valve models this as <c>SetFriction(0.7)</c> on the physics body; here it is
    /// applied once per tick to the tangential component only.
    /// </summary>
    public static Vec3 ApplyRollingFriction(Vec3 velocity, Vec3 normal, float dt, PhysicsParameters parameters)
    {
        float into = Vec3.Dot(velocity, normal);
        Vec3 normalPart = normal * into;
        Vec3 tangentPart = velocity - normalPart;

        // Exponential decay on the tangential component, rate from the friction
        // coefficient. Clamped so a zero friction parameter means "no loss".
        float rate = parameters.GroundFriction.Value * 4f;
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