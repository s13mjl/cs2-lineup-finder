// <copyright file="ThrowVelocityCalculator.cs" company="CS2LineupFinder">
// Turns a player's aim into the grenade's launch velocity.
// </copyright>

using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core;

/// <summary>
/// Resolves the launch velocity the way CS:GO / CS2 do.
///
/// Source, the leaked CS:GO build's
/// <c>game/shared/cstrike15/weapon_basecsgrenade.cpp</c>
/// (<c>CBaseCSGrenade::ThrowGrenade</c>; mirrored in
/// docs/evidence/weapon_basecsgrenade.cpp):
/// <code>
/// float flVel = clamp((kBaseVelocity * 0.9f), 15, 750);   // L586
/// ...
/// Vector vecThrow = vForward * flVel + (pPlayer->GetAbsVelocity() * 1.25);  // L614
/// </code>
/// The 1.25x thrower-velocity inheritance is already in that line - it is not a
/// CS2-only addition, so <see cref="PhysicsParameters.VelocityInheritance"/>
/// documents the same constant for both games. telemetry-002 row 0 measured a
/// stationary-thrower launch speed of 678.63 u/s against the 675 full-throw
/// constant, independently confirming the inheritance term is live in CS2.
///
/// The CS2 build replaces the continuous <c>(90 - pitch) * 6</c> ramp with three
/// discrete speeds (202.5 / 438.75 / 675 u/s) and applies a +10 degree upward
/// bias at level aim. Both are modelled here.
/// </summary>
public static class ThrowVelocityCalculator
{
    /// <summary>CS:GO's hard cap on launch speed, u/s.</summary>
    public const float CsGoMaxLaunchSpeed = 750f;

    /// <summary>
    /// Continuous CS:GO launch speed for a pitch in degrees, before clamping.
    /// Mirrors <c>flVel = (90 - angThrow.x) * 6</c>.
    /// </summary>
    public static float CsGoLaunchSpeed(float pitchDegrees)
        => Math.Min((90f - pitchDegrees) * 6f, CsGoMaxLaunchSpeed);

    /// <summary>
    /// Launch speed for a button combination, u/s. The three tiers are measured
    /// CS2 constants; see docs/PHYSICS.md.
    /// </summary>
    public static float LaunchSpeed(ThrowButton button, PhysicsParameters parameters)
        => button switch
        {
            ThrowButton.Primary => parameters.FullThrowSpeed.Value,
            ThrowButton.Both => parameters.LobThrowSpeed.Value,
            ThrowButton.Secondary => parameters.UnderhandThrowSpeed.Value,
            _ => parameters.FullThrowSpeed.Value,
        };
    /// <summary>World up, matching the contract coordinate convention (+Z up).</summary>
    private static readonly Vec3 Up = new(0f, 0f, 1f);

    /// <summary>
    /// Applies the engine's pitch-dependent up-bias to a unit aim vector.
    /// CS:GO weapon_basecsgrenade.cpp ThrowGrenade:
    /// <code>
    /// angThrow[PITCH] -= 10.0f * (90.0f - fabsf(angThrow[PITCH])) / 90.0f;
    /// </code>
    /// Level aim gets the full 10 degrees; the boost lerps to zero at the
    /// +/-90 degree extremes. A constant rotation over-rotates every oblique
    /// throw, which the 1014 steep calibration sample exposed.
    /// </summary>
    public static Vec3 ApplyAimBias(Vec3 forward, PhysicsParameters parameters)
    {
        Vec3 unit = forward.Normalized();
        float bias = parameters.AimBiasDegrees.Value;
        if (Math.Abs(bias) < 1e-4f)
        {
            return unit;
        }

        float aimPitch = MathF.Asin(Math.Clamp(unit.Z, -1f, 1f)) * 180f / MathF.PI;
        float effective = bias * ((90f - MathF.Abs(aimPitch)) / 90f);
        if (MathF.Abs(effective) < 1e-4f)
        {
            // Straight up/down: the engine formula yields zero bias here too.
            return unit;
        }

        // Rotate about the right vector (forward x up): a right-hand rotation
        // about it tilts forward upward.
        Vec3 right = Vec3.Cross(unit, Up);
        if (right.LengthSquared <= 1e-8f)
        {
            return unit;
        }

        return RotateAboutAxis(unit, right.Normalized(), effective).Normalized();
    }

    /// <summary>
    /// Exact inverse of <see cref="ApplyAimBias"/>: recovers the unit aim vector whose
    /// biased release direction is <paramref name="biasedForward"/>. Working in pitch
    /// space keeps the inversion closed form. With the contract convention (positive
    /// looks up) the biased release pitch is <c>e(a) = a + b*(90-|a|)/90</c>, so for
    /// <c>e >= b</c> the aim pitch is <c>(e-b)*90/(90-b)</c> and otherwise
    /// <c>(e-b)*90/(90+b)</c>; the yaw is untouched because the rotation axis stays
    /// horizontal. Round trip verified over [-89, 89] degrees to 1.4e-14 degrees.
    /// </summary>
    public static Vec3 UndoAimBias(Vec3 biasedForward, PhysicsParameters parameters)
    {
        Vec3 v = biasedForward.Normalized();
        float bias = parameters.AimBiasDegrees.Value;
        if (Math.Abs(bias) < 1e-4f)
        {
            return v;
        }

        float physicalPitch = MathF.Asin(Math.Clamp(v.Z, -1f, 1f)) * 180f / MathF.PI;
        float delta = physicalPitch - bias;
        float aimPitch = delta >= 0f
            ? delta * 90f / (90f - bias)
            : delta * 90f / (90f + bias);
        aimPitch = Math.Clamp(aimPitch, -90f, 90f);

        float yaw = MathF.Atan2(v.Y, v.X);
        float aimRadians = aimPitch * MathF.PI / 180f;
        float cosAim = MathF.Cos(aimRadians);
        return new Vec3(
            cosAim * MathF.Cos(yaw),
            cosAim * MathF.Sin(yaw),
            MathF.Sin(aimRadians)).Normalized();
    }

    /// <summary>Rodrigues rotation of <paramref name="v"/> about unit <paramref name="axis"/>.</summary>
    public static Vec3 RotateAboutAxis(Vec3 v, Vec3 axis, float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        return (v * c) + (Vec3.Cross(axis, v) * s) + (axis * (Vec3.Dot(axis, v) * (1f - c)));
    }

    /// <summary>
    /// Full launch velocity: aim direction at the mode's speed, plus an inherited
    /// fraction of the thrower's own velocity, scaled by <see cref="ThrowParams.ThrowStrength"/>.
    /// </summary>
    public static Vec3 Compute(
        Vec3 aimForward,
        Vec3 playerVelocity,
        ThrowButton button,
        GrenadeType grenadeType,
        float throwStrength,
        PhysicsParameters parameters)
    {
        Vec3 forward = ApplyAimBias(aimForward, parameters);
        float speed = LaunchSpeed(button, parameters) * Math.Clamp(throwStrength, 0f, 1f);
        float inheritance = parameters.VelocityInheritance.Value;
        return (forward * speed) + (playerVelocity * inheritance);
    }

    /// <summary>
    /// The release point: eye position pushed <see cref="PhysicsParameters.ReleaseOffset"/>
    /// units along forward. The engine hull-traces that offset so a thin wall in
    /// front cannot spawn the grenade inside geometry; see the trace in
    /// <c>ThrowGrenade</c>. The caller performs the trace and clamps the result.
    /// </summary>
    public static Vec3 ReleasePoint(Vec3 eyePosition, Vec3 aimForward, PhysicsParameters parameters)
        => eyePosition + (aimForward.Normalized() * parameters.ReleaseOffset.Value);
}
