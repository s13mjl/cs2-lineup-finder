// <copyright file="CalibrationRunnerTests.cs" company="CS2LineupFinder">
// Verifies the calibration harness converges and stays inside its search window.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using CS2LineupFinder.Core.Calibration;
using Xunit;

namespace CS2LineupFinder.Core.Tests;

public class CalibrationRunnerTests
{
    private const string SampleJson = """
    {
      "source": "unit test fixture",
      "samples": [
        { "name": "flat-45", "origin": {"x":0,"y":0,"z":64}, "aimYaw": 0, "aimPitch": 45,
          "grenadeType": "He", "throwButton": "Primary", "throwStrength": 1.0,
          "gameTickRate": 64, "floorHeight": 0.0, "landingX": 900, "landingY": 0 },
        { "name": "flat-30", "origin": {"x":0,"y":0,"z":64}, "aimYaw": 0, "aimPitch": 30,
          "grenadeType": "He", "throwButton": "Primary", "throwStrength": 1.0,
          "gameTickRate": 64, "floorHeight": 0.0, "landingX": 1100, "landingY": 0 },
        { "name": "flat-60", "origin": {"x":0,"y":0,"z":64}, "aimYaw": 0, "aimPitch": 60,
          "grenadeType": "He", "throwButton": "Primary", "throwStrength": 1.0,
          "gameTickRate": 64, "floorHeight": 0.0, "landingX": 800, "landingY": 0 }
      ]
    }
    """;

    [Fact]
    public void Deserialize_ReadsEverySample()
    {
        CalibrationDataSet data = CalibrationRunner.Deserialize(SampleJson);
        Assert.Equal(3, data.Samples.Count);
        Assert.Equal("flat-45", data.Samples[0].Name);
        Assert.Equal(45f, data.Samples[0].AimPitch);
    }

    [Fact]
    public void Run_NeverMovesAParameterOutsideItsSearchWindow()
    {
        CalibrationDataSet data = CalibrationRunner.Deserialize(SampleJson);
        PhysicsParameters baseline = new();
        CalibrationRunner runner = new();

        CalibrationResult result = runner.Run(data, baseline);

        foreach (ParameterFit fit in result.Fits)
        {
            Assert.InRange(fit.After, fit.Min, fit.Max);
        }
    }

    [Fact]
    public void Run_DoesNotIncreaseTheError()
    {
        CalibrationDataSet data = CalibrationRunner.Deserialize(SampleJson);
        PhysicsParameters baseline = new();
        CalibrationRunner runner = new();

        CalibrationResult result = runner.Run(data, baseline);

        // Coordinate descent keeps the best value seen, so it can never do worse.
        Assert.True(result.Rmse <= result.RmseBefore + 1e-3f,
            $"RMSE went from {result.RmseBefore} to {result.Rmse}.");
    }

    [Fact]
    public void Run_ImprovesErrorWhenTheGravityIsDeliberatelyWrong()
    {
        CalibrationDataSet data = CalibrationRunner.Deserialize(SampleJson);

        // Start from a badly wrong gravity and check the fitter recovers part of
        // the loss. This proves the search is actually doing work rather than
        // reporting the defaults back unchanged.
        PhysicsParameters baseline = new();
        baseline.GrenadeGravity.Value = 600f;

        CalibrationRunner runner = new();
        CalibrationResult result = runner.Run(data, baseline);

        Assert.True(result.Rmse < result.RmseBefore,
            $"Expected improvement, got {result.RmseBefore} -> {result.Rmse}.");

        // And gravity should have moved toward the true 320.
        ParameterFit gravity = result.Fits.Single(f => f.Name == "GrenadeGravity");
        Assert.True(gravity.After < 600f, $"Gravity did not move: {gravity.After}.");
    }

    [Fact]
    public void Run_ReportsAResidualForEverySample()
    {
        CalibrationDataSet data = CalibrationRunner.Deserialize(SampleJson);
        CalibrationResult result = new CalibrationRunner().Run(data, new PhysicsParameters());

        Assert.Equal(data.Samples.Count, result.Residuals.Count);
        Assert.All(result.Residuals, r => Assert.True(r.Error >= 0f));
    }

    [Fact]
    public void ToMarkdown_EmitsTheCalibrationRecordTable()
    {
        CalibrationDataSet data = CalibrationRunner.Deserialize(SampleJson);
        CalibrationResult result = new CalibrationRunner().Run(data, new PhysicsParameters());

        string markdown = result.ToMarkdown();

        Assert.Contains("RMSE", markdown);
        Assert.Contains("|", markdown);
        Assert.Contains("GrenadeGravity", markdown);
    }

    [Fact]
    public void Run_RejectsAnEmptyDataSet()
    {
        CalibrationDataSet empty = new();
        Assert.Throws<InvalidDataException>(() => new CalibrationRunner().Run(empty, new PhysicsParameters()));
    }
}
