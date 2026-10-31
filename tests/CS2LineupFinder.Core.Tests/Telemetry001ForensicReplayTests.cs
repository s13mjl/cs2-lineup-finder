// <copyright file="Telemetry001ForensicReplayTests.cs" company="CS2LineupFinder">
// Tick-by-tick forensic replay of telemetry-001 against the simulator. The
// existing Telemetry001ReplayTests only scores the FINAL resting point, which
// can agree while every intermediate bounce happens on the wrong shelf; this
// harness prints both trajectories side by side so the FIRST divergent tick -
// and therefore the first wrong contact decision - is visible in the output.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public sealed class Telemetry001ForensicReplayTests
{
    private readonly ITestOutputHelper _output;

    public Telemetry001ForensicReplayTests(ITestOutputHelper output) => _output = output;

    private static readonly string TelemetryCsv =
        @"D:\desktop\0824\cs2srv\server\game\csgo\addons\counterstrikesharp\plugins\CS2LineupFinder\data\telemetry\telemetry-001.csv";

    // No infinite floors: the capture itself proves the surfaces are finite
    // pads. See BuildTelemetryShelves below for the reconstruction.

   [Fact]
   public void Telemetry001_TickByTickDivergence()
    {
        if (!File.Exists(TelemetryCsv))
        {
            _output.WriteLine("SKIP: telemetry-001.csv not present on this machine.");
            return;
        }

        List<TelemetryRow> rows = LoadRows(TelemetryCsv);
        _output.WriteLine(FormattableString.Invariant($"telemetry rows: {rows.Count}"));

        MockWorldGeometry world = new();
        BuildTelemetryShelves(world);

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(rows[0].Position, rows[0].Velocity, GrenadeType.Smoke, 1f, 64),
            world);

        _output.WriteLine(FormattableString.Invariant(
            $"sim impact ({result.Impact.Position.X:0.####},{result.Impact.Position.Y:0.####},{result.Impact.Position.Z:0.####}) bounces {result.BounceCount} flight {result.FlightTime:0.0000}s; actual end ({rows[^1].Position.X:0.####},{rows[^1].Position.Y:0.####},{rows[^1].Position.Z:0.####})"));

        int n = System.Math.Min(result.Points.Count, rows.Count);
        float maxFree = 0f;
        int firstDivergent = -1;
        for (int i = 0; i < n; i++)
        {
            TrajectoryPoint s = result.Points[i];
            TelemetryRow r = rows[i];
            float dp = (s.Position - r.Position).Length;
            float dv = (s.Velocity - r.Velocity).Length;

            // Free flight must agree to the CSV 4-decimal rounding, well under
            // 0.05u. A jump above that marks a contact decision the engine
            // made differently.
            bool diverged = dp > 0.05f || dv > 0.05f;
            if (diverged && firstDivergent < 0)
            {
                firstDivergent = i;
            }

            bool bounceEvent = i > 0 && result.Points[i].BounceCount != result.Points[i - 1].BounceCount;
            if (i < 4 || diverged || i >= n - 3 || bounceEvent)
            {
                string flag = diverged ? "  <-- DIVERGES" : string.Empty;
                _output.WriteLine(FormattableString.Invariant(
                    $"tick {i,3} t={s.Time:0.0000}  sim p({s.Position.X,10:0.####},{s.Position.Y,10:0.####},{s.Position.Z,10:0.####}) v({s.Velocity.X,9:0.####},{s.Velocity.Y,9:0.####},{s.Velocity.Z,9:0.####}) b{s.BounceCount}  |  csv p({r.Position.X,10:0.####},{r.Position.Y,10:0.####},{r.Position.Z,10:0.####}) v({r.Velocity.X,9:0.####},{r.Velocity.Y,9:0.####},{r.Velocity.Z,9:0.####})  dp={dp,8:0.####} dv={dv,8:0.####}{flag}"));
            }

            if (firstDivergent < 0 && dp > maxFree)
            {
                maxFree = dp;
            }
        }

        Assert.True(firstDivergent < 0,
            $"telemetry-001 replay diverged at tick {firstDivergent}: dp or dv exceeded 0.05u");
        float restGap = (result.Impact.Position - rows[^1].Position).Length;
        Assert.True(restGap <= 0.05f, $"telemetry-001 rest position off by {restGap:0.####}u");
        _output.WriteLine(firstDivergent < 0
            ? FormattableString.Invariant($"all {n} compared ticks agree within 0.05u; max free-flight dp {maxFree:0.0000}u")
            : FormattableString.Invariant($"first divergent tick: {firstDivergent} (t={result.Points[firstDivergent].Time:0.0000}s)"));
    }

    // Shelves reconstructed from the capture itself. Each bounce contact is
    // recovered by back-solving the pre-impact velocity to the contact-centre
    // height (surface z + 2-unit hull radius), pinning the contact XY to
    // ~0.01u. The descent PROVES the pads are finite: the ball crossed the
    // -169.97 and -165.97 contact-centre heights at (-567.5,-2230.0),
    // (-575.4,-2232.7), (-870.2,-2336.7) and (-886.4,-2342.7) WITHOUT
    // contacting, so no shelf may extend over those XY. Each box is the
    // smallest pad that covers every contact the capture establishes
    // (bounce 1 (-595.7,-2239.9) on -179.974; bounce 2 (-877.2,-2339.0) on
    // -171.975; bounces 3/4 + rest on -167.974) and excludes every crossing
    // the capture proves is a miss.
    private static void BuildTelemetryShelves(MockWorldGeometry world)
    {
        const float thick = 4096f;
        world.AddBox(new Vec3(-610f, -2252f, -179.974f - thick), new Vec3(-555f, -2228f, -179.974f));
        world.AddBox(new Vec3(-885f, -2346f, -171.975f - thick), new Vec3(-872f, -2332f, -171.975f));
        world.AddBox(new Vec3(-942f, -2364f, -167.974f - thick), new Vec3(-921f, -2352f, -167.974f));
    }

    private static List<TelemetryRow> LoadRows(string path)
    {
        var rows = new List<TelemetryRow>();
        bool header = true;
        foreach (string line in File.ReadLines(path))
        {
            if (header)
            {
                header = false;
                continue;
            }

            string[] f = line.Split(',');
            if (f.Length < 8)
            {
                continue;
            }

            rows.Add(new TelemetryRow(
                Parse(f[0]),
                new Vec3(Parse(f[1]), Parse(f[2]), Parse(f[3])),
                new Vec3(Parse(f[4]), Parse(f[5]), Parse(f[6]))));
        }

        return rows;
    }

    private static float Parse(string s) => float.Parse(s.Trim(), CultureInfo.InvariantCulture);
}
