// <copyright file="ThrowVelocityCalculator.cs" company="CS2LineupFinder">
// Turns a player's aim into the grenade's launch velocity.
// </copyright>

using System;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Core;

/// <summary>
/// Resolves the launch velocity the way CS:GO / CS2 do.
///
/// Source, CS:GO <c>game/shared/cstrike/weapon_basecsgrenade.cpp</c>
/// (<c>CBaseCSGrenade::ThrowGrenade</c>):
/// <code>
/// float flVel = (90 - angThrow.x) * 6;
/// if (flVel > 750) flVel = 750;
/// Vector vecThrow = vForward * flVel + pPlayer->GetAbsVelocity();
/// </code>
/// Note the thrower's velocity is added unscaled there. CS2 applies a documented
/// 1.25x inheritance factor instead, which is why <see cref="PhysicsParameters.VelocityInheritance"/>
/// exists; set it to 1.0 to reproduce the CS:GO formula exactly.
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

    /// <summary>Launch speed for a discrete CS2 throw mode, u/s.</summary>
    public static float LaunchSpeed(ThrowMode mode, PhysicsParameters parameters)
        => mode switch
        {
            ThrowMode.FullThrow => parameters.FullThrowSpeed.Value,
            ThrowMode.Lob => parameters.LobThrowSpeed.Value,
            ThrowMode.Underhand => parameters.UnderhandThrowSpeed.Value,
            _ => parameters.FullThrowSpeed.Value,
        };

    /// <summary>
    /// Applies the CS:GO +10 degree up-bias to a unit aim vector. The original code
    /// remaps <c>angThrow.x</c> into <c>[-10, +10]</c> before building the basis
    /// vectors, which is a pitch offset, not a speed change.
    /// </summary>
    public static Vector3 ApplyAimBias(Vector3 forward, PhysicsParameters parameters)
    {
        float bias = parameters.AimBiasDegrees.Value;
        if (Math.Abs(bias) < 1e-4f)
        {
            return forward.Normalized();
        }

        // Rotate about the right vector (forward x up). Sign chosen so a positive
        // bias raises the throw for a level aim.
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        if (right.IsZero())
        {
            // Looking straight up or down: the cross product degenerates, so fall
            // back to the world X axis and accept the tiny in-plane error.
            right = Vector3.UnitX;
        }

        right = right.Normalized();
        return RotateAboutAxis(forward.Normalized(), right, -bias).Normalized();
    }

    /// <summary>Rodrigues rotation of <paramref name="v"/> about unit <paramref name="axis"/>.</summary>
    public static Vector3 RotateAboutAxis(Vector3 v, Vector3 axis, float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        return (v * c) + (Vector3.Cross(axis, v) * s) + (axis * (Vector3.Dot(axis, v) * (1f - c)));
    }

    /// <summary>
    /// Full launch velocity: aim direction at the mode's speed, plus an inherited
    /// fraction of the thrower's own velocity, scaled by <see cref="ThrowParams.ThrowStrength"/>.
    /// </summary>
    public static Vector3 Compute(
        Vector3 aimForward,
        Vector3 playerVelocity,
        ThrowMode mode,
        float throwStrength,
        PhysicsParameters parameters)
    {
        Vector3 forward = ApplyAimBias(aimForward, parameters);
        float speed = LaunchSpeed(mode, parameters) * Math.Clamp(throwStrength, 0f, 1f);
        float inheritance = parameters.VelocityInheritance.Value;
        return (forward * speed) + (playerVelocity * inheritance);
    }

    /// <summary>
    /// The release point: eye position pushed <see cref="PhysicsParameters.ReleaseOffset"/>
    /// units along forward. The engine hull-traces that offset so a thin wall in
    /// front cannot spawn the grenade inside geometry; see the trace in
    /// <c>ThrowGrenade</c>. The caller performs the trace and clamps the result.
    /// </summary>
    public static Vector3 ReleasePoint(Vector3 eyePosition, Vector3 aimForward, PhysicsParameters parameters)
        => eyePosition + (aimForward.Normalized() * parameters.ReleaseOffset.Value);
}