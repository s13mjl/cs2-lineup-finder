// <copyright file="DetonationModelTests.cs" company="CS2LineupFinder">
// Locks the fuse/trigger rules to the leaked source behaviour so a refactor
// cannot silently change when a grenade pops.
// </copyright>

using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

public class DetonationModelTests
{
    private const float Fuse = 1.5f;

    [Fact]
    public void Smoke_BeforeFuse_NeverDetonates()
    {
        DetonationState slow = DetonationModel.Evaluate(GrenadeType.Smoke, 1.0f, 0f, true, false, Fuse);
        DetonationState fast = DetonationModel.Evaluate(GrenadeType.Smoke, 1.0f, 500f, false, false, Fuse);
        Assert.False(slow.Detonated);
        Assert.False(fast.Detonated);
    }

    [Fact]
    public void Smoke_AfterFuse_StillMoving_Waits()
    {
        // Engine parity: Think_Detonate re-checks every 0.2s while speed > 0.1.
        DetonationState moving = DetonationModel.Evaluate(GrenadeType.Smoke, 2.0f, 5f, false, false, Fuse);
        Assert.False(moving.Detonated);
    }

    [Fact]
    public void Smoke_AfterFuse_Settled_Detonates()
    {
        DetonationState byRestFlag = DetonationModel.Evaluate(GrenadeType.Smoke, 1.6f, 0f, true, false, Fuse);
        DetonationState bySpeed = DetonationModel.Evaluate(GrenadeType.Smoke, 1.6f, 0.08f, false, false, Fuse);
        Assert.True(byRestFlag.Detonated);
        Assert.Equal(DetonationReason.AtRest, byRestFlag.Reason);
        Assert.True(bySpeed.Detonated);
    }

    [Fact]
    public void He_PopsOnTimer_EvenWhileMoving()
    {
        DetonationState flying = DetonationModel.Evaluate(GrenadeType.He, Fuse, 600f, false, false, Fuse);
        Assert.True(flying.Detonated);
        Assert.Equal(DetonationReason.FuseExpired, flying.Reason);
    }

    [Fact]
    public void Molotov_IgnoresFuse_DetonatesOnArmedContact()
    {
        DetonationState beforeArm = DetonationModel.Evaluate(GrenadeType.Molotov, 0.1f, 100f, false, true, Fuse);
        DetonationState armed = DetonationModel.Evaluate(GrenadeType.Molotov, 0.3f, 100f, false, true, Fuse);
        DetonationState armedNoContact = DetonationModel.Evaluate(GrenadeType.Molotov, 3f, 100f, false, false, Fuse);
        Assert.False(beforeArm.Detonated);
        Assert.True(armed.Detonated);
        Assert.Equal(DetonationReason.Impact, armed.Reason);
        Assert.False(armedNoContact.Detonated);
    }
}
