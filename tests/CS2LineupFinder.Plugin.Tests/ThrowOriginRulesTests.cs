// <copyright file="ThrowOriginRulesTests.cs" company="CS2LineupFinder">
// Rules that turn the live pose into a jump/stand search origin.
// </copyright>

using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Commands;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// A jump search must predict the carry the player actually throws with. The
/// live pawn pose carries the nominal +300 jump impulse; the player's own jump
/// throws measure the real carry, and CS2's consistency window makes the last
/// throw the best predictor of the next one.
/// </summary>
public sealed class ThrowOriginRulesTests
{
    private static ThrowOrigin Live(float vertical) => new()
    {
        Feet = new Vec3(0f, 0f, 0f),
        Eyes = new Vec3(0f, 0f, 64f),
        Velocity = new Vec3(0f, 0f, vertical),
    };

    [Fact]
    public void Jump_WithMeasuredCarry_ReplacesTheNominalImpulse()
    {
        // A manual jump throw released mid-rise carries ~120 u/s, not 300.
        ThrowOrigin origin = ThrowOriginRules.ForSearch(Live(300f), Vec3.Zero, ThrowMode.Jump, 120f, 215f, 40f, 40f);

        Assert.Equal(120f, origin.Velocity.Z);
        Assert.Equal(0f, origin.Velocity.X);
        Assert.Equal(104f, origin.Eyes.Z);   // standing eye 64 + 40 release rise
        Assert.Equal(40f, origin.Feet.Z);
    }

    [Fact]
    public void Jump_WithoutMeasurement_UsesTheConfiguredCarry()
    {
        // The first find of a session must already predict with the measured
        // starting value (215 on this server), not the +300 game impulse.
        ThrowOrigin origin = ThrowOriginRules.ForSearch(Live(300f), Vec3.Zero, ThrowMode.Jump, null, 215f, null, 40f);

        Assert.Equal(215f, origin.Velocity.Z);
    }

    [Fact]
    public void Jump_KeepsTheMarkedRunUpVelocity()
    {
        var marked = new Vec3(200f, -50f, 0f);

        ThrowOrigin unmeasured = ThrowOriginRules.ForSearch(Live(300f), marked, ThrowMode.Jump, null, 215f, null, 40f);
        Assert.Equal(200f, unmeasured.Velocity.X);
        Assert.Equal(-50f, unmeasured.Velocity.Y);
        Assert.Equal(215f, unmeasured.Velocity.Z);

        ThrowOrigin measured = ThrowOriginRules.ForSearch(Live(300f), marked, ThrowMode.Jump, 90f, 215f, null, 40f);
        Assert.Equal(200f, measured.Velocity.X);
        Assert.Equal(-50f, measured.Velocity.Y);
        Assert.Equal(90f, measured.Velocity.Z);
    }

    [Fact]
    public void Stand_LeavesTheLivePoseAlone()
    {
        var marked = new Vec3(200f, 0f, 0f);

        ThrowOrigin origin = ThrowOriginRules.ForSearch(Live(0f), marked, ThrowMode.Stand, 120f, 215f, 40f, 40f);

        Assert.Equal(0f, origin.Velocity.X);
        Assert.Equal(0f, origin.Velocity.Z);
    }

    [Fact]
    public void JumpCarry_IsOnlyConfirmedWhenThePawnLeavesTheGround()
    {
        // A real jump climbs ~25 u within six ticks.
        Assert.True(ThrowOriginRules.JumpCarryConfirmed(-40f, -15f));

        // A stutter that eats the jump leaves the pawn at its spawn height;
        // that measurement (usually zero) must not reach the session.
        Assert.False(ThrowOriginRules.JumpCarryConfirmed(-40f, -40f));
        Assert.False(ThrowOriginRules.JumpCarryConfirmed(-40f, -38f));
        Assert.False(ThrowOriginRules.JumpCarryConfirmed(-40f, float.NaN));
        Assert.False(ThrowOriginRules.JumpCarryConfirmed(-40f, -80f));
    }
}
