// <copyright file="TelemetryDifferentialTests.cs" company="CS2LineupFinder">
// Runs the differential harness over every piece of recorded in-game evidence
// found on this machine. The tests never assert a magnitude: they print the
// three-way split so a human can read which input the solver derives wrongly.
// Evidence that is absent simply skips, so the suite stays green on a machine
// with no server install.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public sealed class TelemetryDifferentialTests
{
    private readonly ITestOutputHelper _output;

    public TelemetryDifferentialTests(ITestOutputHelper output) => _output = output;

    // Where the live server writes telemetry. Kept out of the repo on purpose:
    // it is a runtime artifact of the local dedicated server.
    private static readonly string[] TelemetryDirectories =
    {
        @"D:\desktop\0824\cs2srv\server\game\csgo\addons\counterstrikesharp\plugins\CS2LineupFinder\data\telemetry",
        @"D:\desktop\0824\cs2srv\server\csgo\addons\counterstrikesharp\plugins\CS2LineupFinder\data\telemetry",
    };

    private static readonly string[] LandingDirectories =
    {
        @"D:\desktop\0824\project\CS2utinity\calibration_data",
    };

    /// <summary>
    /// Replays every raw telemetry capture with the engine launch state, which is
    /// the cleanest measure of the ballistic model alone. The other two
    /// differentials are skipped here because a raw capture does not record the
    /// aim angles the solver produced, so there is nothing to re-derive.
    /// </summary>
    [Fact]
    public void RawTelemetry_BallisticDifferential()
    {
        var files = FindFiles(TelemetryDirectories, "telemetry-*.csv");
        if (files.Count == 0)
        {
            _output.WriteLine("SKIP: no telemetry captures on this machine.");
            return;
        }

        foreach (string file in files)
        {
            TelemetryCapture? capture = TelemetryCapture.Load(file);
            if (capture is null)
            {
                continue;
            }

            IReadOnlyList<DifferentialResult> results = TelemetryDifferential.Run(capture, new PhysicsParameters());
            Report(Path.GetFileNameWithoutExtension(file), capture.Launch.Position, capture.Launch.Velocity, results);
        }
    }

    /// <summary>
    /// Replays every landing sample the in-game calibration pair recorded. This is
    /// the only evidence that carries the aim angles the solver produced, so it
    /// is the only place the derived release point and derived launch velocity can
    /// be scored against a throw that actually happened.
    /// </summary>
    [Fact]
    public void LandingSamples_SolverInputDifferential()
    {
        var files = FindFiles(LandingDirectories, "landings-*.json");
        if (files.Count == 0)
        {
            _output.WriteLine("SKIP: no landing samples on this machine.");
            return;
        }

        // A landing sample carries the aim and the two landing points but not the
        // launch velocity, so the velocity is taken from a telemetry capture of
        // the same stance when one is available. Without it, only the full solver
        // path is reported and the velocity split is skipped.
        Vec3? engineVelocity = BestEngineVelocity();
        _output.WriteLine(engineVelocity is null
            ? "engine launch velocity: unavailable, reporting solver path only"
            : FormattableString.Invariant($"engine launch velocity: {engineVelocity.Value}"));

        int index = 1;

        foreach (string file in files)
        {
            foreach (LandingSample sample in LandingSample.Parse(file))
            {
                IReadOnlyList<DifferentialResult> results = engineVelocity is null
                    ? TelemetryDifferential.Run(sample, new PhysicsParameters(), Vec3.Zero).Take(1).ToList()
                    : TelemetryDifferential.Run(sample, new PhysicsParameters(), engineVelocity.Value);
                Report(Path.GetFileNameWithoutExtension(file) + " #" + index, results);
                index++;
            }
        }
    }

    /// <summary>
    /// Sweeps the three derived-input parameters that the recorded evidence has
    /// never quantified: ReleaseOffset, AimBiasDegrees and VelocityInheritance.
    /// The solver path error is printed for each combination so the values can be
    /// </summary>
    [Fact]
    public void LandingSamples_DerivedInputSweep()
    {
        var files = FindFiles(LandingDirectories, "landings-*.json");
        if (files.Count == 0)
        {
            _output.WriteLine("SKIP: no landing samples on this machine.");
            return;
        }

        List<LandingSample> all = new();
        foreach (string file in files)
        {
            all.AddRange(LandingSample.Parse(file));
        }

        if (all.Count == 0)
        {
            _output.WriteLine("SKIP: landing sample files held no samples.");
            return;
        }

        Vec3? engineVelocity = BestEngineVelocity();
        foreach (float offset in new[] { 8f, 12f, 16f, 20f, 24f })
        {
            foreach (float bias in new[] { 0f, 5f, 10f })
            {
                PhysicsParameters p = new();
                p.ReleaseOffset.Value = offset;
                p.AimBiasDegrees.Value = bias;
                DifferentialResult? worst = null;
                float worstError = 0f;
                foreach (LandingSample sample in all)
                {
                    Vec3 velocity = engineVelocity ?? ThrowVelocityCalculator.Compute(
                        GrenadeSimulator.DirectionFromAnglesForTest(sample.AimYaw, sample.AimPitch),
                        Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, p);
                    IReadOnlyList<DifferentialResult> results = TelemetryDifferential.Run(sample, p, velocity);
                    DifferentialResult solverPath = results[0];
                    if (solverPath.HorizontalError > worstError)
                    {
                        worstError = solverPath.HorizontalError;
                        worst = solverPath;
                    }
                }

                _output.WriteLine(FormattableString.Invariant(
                    $"ReleaseOffset={offset,5} AimBias={bias,4}  worst horizontal {worstError,8:0.##}u  dz {worst?.VerticalError ?? 0f,7:0.##}"));
            }
        }
    }

    private void Report(
        string label,
        Vec3 launchPosition,
        Vec3 launchVelocity,
        IReadOnlyList<DifferentialResult> results)
    {
        _output.WriteLine(FormattableString.Invariant(
            $"== {label} == launch {launchPosition} vel {launchVelocity} ({launchVelocity.Length:0.##} u/s)"));
        foreach (DifferentialResult r in results)
        {
            Line(r);
        }
    }

    private void Report(string label, IReadOnlyList<DifferentialResult> results)
    {
        _output.WriteLine($"== {label} ==");
        foreach (DifferentialResult r in results)
        {
            Line(r);
        }
    }

    private void Line(DifferentialResult r)
    {
        _output.WriteLine(FormattableString.Invariant(
            $"   {r.Label,-56} horiz {r.HorizontalError,9:0.##}u  vert {r.VerticalError,8:0.##}u  bounces {r.Bounces}  flight {r.FlightTime:0.000}s"));
    }

    /// <summary>
    /// The engine launch velocity from the longest telemetry capture, used as the
    /// reference when scoring a landing sample whose own launch velocity was not
    /// recorded. Null when no capture exists.
    /// </summary>
    private static Vec3? BestEngineVelocity()
    {
        TelemetryCapture? best = null;
        foreach (string file in FindFiles(TelemetryDirectories, "telemetry-*.csv"))
        {
            TelemetryCapture? capture = TelemetryCapture.Load(file);
            if (capture is not null && (best is null || capture.Count > best.Count))
            {
                best = capture;
            }
        }

        return best?.Launch.Velocity;
    }

    private static List<string> FindFiles(IEnumerable<string> directories, string pattern)
    {
        List<string> found = new();
        foreach (string dir in directories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            found.AddRange(Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal));
        }

        return found;
    }
}
