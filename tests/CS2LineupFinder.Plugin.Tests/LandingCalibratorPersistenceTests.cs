// <copyright file="LandingCalibratorPersistenceTests.cs" company="CS2LineupFinder">
// Regression: reloading a day file used to zero every Vec3 field.
// </copyright>

using System;
using System.IO;
using System.Text.Json;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Calibration;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// Vec3 is a read-only struct; the default serializer wrote it (including its
/// computed Length* properties) but could not fill it back in, so after a
/// server restart every loaded sample had zeroed vectors and the next
/// recording rewrote them as zeros. The calibrator must round-trip its day
/// files, and it now also records the launch-state replay error next to the
/// pre-throw prediction error.
/// </summary>
public sealed class LandingCalibratorPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2lf-cal-" + Guid.NewGuid().ToString("N"));

    public LandingCalibratorPersistenceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Reload_KeepsVectors_AndBothErrors()
    {
        var suggestion = new PendingSuggestion(
            Yaw: 21.5f,
            Pitch: -8.5f,
            Origin: new Vec3(10f, 20f, 30f),
            EyeHeight: 64f,
            GrenadeType: GrenadeType.Smoke,
            PredictedImpact: new Vec3(100f, 200f, 0f),
            ReleasePosition: new Vec3(11f, 21f, 95f),
            SuggestedTickMs: 123,
            SuggestionNumber: 7);

        var first = new LandingCalibrator(_root);
        LandingSample? sample = first.Record(
            1,
            "de_mirage",
            origin: new Vec3(10f, 20f, 30f),
            eyeHeight: 64f,
            suggestion,
            actualImpact: new Vec3(120f, 205f, 0f),
            releasePosition: new Vec3(11f, 21f, 95f),
            launchPosition: new Vec3(12f, 22f, 100f),
            launchVelocity: new Vec3(-300f, -50f, 500f),
            replayImpact: new Vec3(118f, 204f, 0f));

        Assert.NotNull(sample);
        Assert.Equal(20.62f, sample!.Error, 2);
        Assert.NotNull(sample.ReplayError);
        Assert.Equal(2.24f, sample.ReplayError!.Value, 2);

        // Restart: a fresh calibrator loads the day file, then a new throw
        // rewrites it. The loaded sample's vectors must survive the rewrite.
        var second = new LandingCalibrator(_root);
        Assert.Equal(1, second.Count);
        second.Record(2, "de_mirage", new Vec3(5f, 6f, 7f), 64f, suggestion, new Vec3(50f, 50f, 0f));

        string path = Path.Combine(_root, "landings-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement samples = document.RootElement.GetProperty("samples");
        Assert.Equal(2, samples.GetArrayLength());

        JsonElement reloaded = samples[0];
        Assert.Equal(10f, (float)reloaded.GetProperty("Origin").GetProperty("X").GetDouble(), 2);
        Assert.Equal(20f, (float)reloaded.GetProperty("Origin").GetProperty("Y").GetDouble(), 2);
        Assert.Equal(30f, (float)reloaded.GetProperty("Origin").GetProperty("Z").GetDouble(), 2);
        Assert.Equal(100f, (float)reloaded.GetProperty("PredictedImpact").GetProperty("X").GetDouble(), 2);
        Assert.Equal(200f, (float)reloaded.GetProperty("PredictedImpact").GetProperty("Y").GetDouble(), 2);
        Assert.Equal(120f, (float)reloaded.GetProperty("ActualImpact").GetProperty("X").GetDouble(), 2);
        Assert.Equal(118f, (float)reloaded.GetProperty("ReplayImpact").GetProperty("X").GetDouble(), 2);
        Assert.Equal(2.24, reloaded.GetProperty("ReplayError").GetDouble(), 2);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory must not fail a test run.
        }
    }
}
