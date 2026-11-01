// <copyright file="DeriveCarriedVelocityTests.cs" company="CS2LineupFinder">
// Inverting the engine's launch composition to recover the thrower velocity.
// </copyright>

using System;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

/// <summary>
/// The engine composes a real launch as biasedForward * speed + carry *
/// inheritance. Recovering the carry from the projectile's launch state is how
/// a jump-throw carry is measured without depending on when the pawn is
/// sampled: the pawn loses ~12.5 u/s of vertical speed per tick after the jump,
/// which made identical throws read tens of u/s apart.
/// </summary>
public sealed class DeriveCarriedVelocityTests
{
    [Theory]
    [InlineData(0f, 0f, ThrowButton.Primary)]      // stand throw
    [InlineData(0f, 300f, ThrowButton.Primary)]    // bound jump throw
    [InlineData(150f, 170f, ThrowButton.Primary)]  // manual jump throw with run-up
    [InlineData(0f, 250f, ThrowButton.Secondary)]  // underhand jump throw
    public void RoundTrip_RecoversTheCarriedVelocity(float horizontalCarry, float verticalCarry, ThrowButton button)
    {
        var parameters = new PhysicsParameters();
        var simulator = new GrenadeSimulator(parameters);

        float yaw = 45f * MathF.PI / 180f;
        float pitch = 20f * MathF.PI / 180f;
        var aim = new Vec3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch));

        var carry = new Vec3(horizontalCarry, 0f, verticalCarry);
        Vec3 launch = (ThrowVelocityCalculator.ApplyAimBias(aim, parameters)
                * ThrowVelocityCalculator.LaunchSpeed(button, parameters))
            + (carry * parameters.VelocityInheritance.Value);

        Vec3 derived = simulator.DeriveCarriedVelocity(launch, aim, button);

        Assert.Equal(carry.X, derived.X, 2);
        Assert.Equal(carry.Y, derived.Y, 2);
        Assert.Equal(carry.Z, derived.Z, 2);
    }
}
