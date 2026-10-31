using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Calibration;
using CS2LineupFinder.Plugin;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// The launch-capture pairing rule guards the R5 data feed: a capture may
/// only pair with a sample born from the SAME suggestion, inside one long
/// smoke flight. The 4 s window and the missing identity check both let
/// silently wrong samples into the calibration file (long lobs lost their
/// measured launch; a mid-flight re-find cross-paired two grenades).
/// </summary>
public sealed class PairingRuleTests
{
    private static PendingSuggestion Pending(long number, long suggestedTickMs = 1_000) => new(
        -160f, 5f, new Vec3(0f, 0f, 0f), 64f, GrenadeType.Smoke,
        new Vec3(500f, 0f, 0f), null, suggestedTickMs, number);

    [Fact]
    public void SameSuggestion_WithinFlightWindow_IsUsable()
    {
        Assert.True(LineupFinderPlugin.IsLaunchCaptureUsable(2_000, 7, 3_000, Pending(7)));
    }

    [Fact]
    public void LongLob_JustUnderEightSeconds_IsUsable()
    {
        // telemetry-001 measured a 5.78 s flight; the window must cover it.
        Assert.True(LineupFinderPlugin.IsLaunchCaptureUsable(1_000, 7, 8_900, Pending(7)));
    }

    [Fact]
    public void BeyondFlightWindow_IsRejected()
    {
        Assert.False(LineupFinderPlugin.IsLaunchCaptureUsable(1_000, 7, 9_100, Pending(7)));
    }

    [Fact]
    public void NewerSuggestion_ReFindMidFlight_IsRejected()
    {
        // Grenade #1 still airborne, player re-runs find: capture carries the
        // OLD suggestion number, the detonation pairs against the NEW one.
        Assert.False(LineupFinderPlugin.IsLaunchCaptureUsable(2_000, 7, 3_000, Pending(8)));
    }

    [Fact]
    public void CaptureWithoutSuggestion_IsRejected()
    {
        Assert.False(LineupFinderPlugin.IsLaunchCaptureUsable(2_000, 0, 3_000, Pending(0)));
    }

    [Fact]
    public void ZeroCaptureTick_IsRejected()
    {
        Assert.False(LineupFinderPlugin.IsLaunchCaptureUsable(0, 7, 3_000, Pending(7)));
    }

    [Fact]
    public void Suggestions_GetMonotonicNumbers()
    {
        using var harness = new Harness();
        var request = new LineupRequest
        {
            MapName = harness.Map,
            GrenadeType = GrenadeType.Smoke,
            ThrowMode = ThrowMode.Stand,
            Button = ThrowButton.Primary,
            Origin = new ThrowOrigin
            {
                Feet = new Vec3(0f, 0f, 0f),
                Eyes = new Vec3(0f, 0f, 64f),
            },
            TargetZone = new GroundZone
            {
                Type = GroundZoneType.Circle,
                Center = new Vec3(500f, 0f, 0f),
                Radius = 120f,
            },
        };
        var solution = new LineupSolution
        {
            Yaw = -160f,
            Pitch = 5f,
            ReleasePosition = new Vec3(0f, 0f, 64f),
            ImpactPosition = new Vec3(500f, 0f, 0f),
            TargetDistance = 0f,
        };

        harness.Service.RecordSuggestion(Harness.Slot, request, solution);
        var first = harness.Service.MaxPendingSuggestionNumber();

        harness.Service.RecordSuggestion(Harness.Slot, request, solution);
        var second = harness.Service.MaxPendingSuggestionNumber();

        Assert.True(first > 0, "the first suggestion must receive a non-zero number");
        Assert.True(second > first, "a second find must raise the number so stale captures stop pairing");

        var taken = harness.Service.TakeSuggestionForCalibration(Harness.Slot, GrenadeType.Smoke);
        Assert.NotNull(taken);
        Assert.Equal(second, taken!.SuggestionNumber);
    }
}
