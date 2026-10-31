// <copyright file="Telemetry002ForensicReplayTests.cs" company="CS2LineupFinder">
// Cross-capture forensic replay: telemetry-002 against the same simulator
// telemetry-001 validates. Independent launch, independent surfaces, same
// laws - this is the check that separates a fitted curve from a physical law.
//
// Scope boundary (deliberate): the capture's FIRST contact (CSV rows 14..17)
// returns 0.3364 instead of 0.45. Forensics settled its cause: the grenade
// begins the tick EMBEDDED in a PlayerClip+NpcClip+StaticLevel brush (engine
// IsAllSolid=1, Fraction=0 - probe A/K, live tracer 2026-10-19 09:48:18), and
// the impact point is the plane floor beneath the brush (parabolic contact
// fit z=-189.9668 vs face -189.9688). That contact path is now exercised
// end-to-end: ClipEmbeddedElasticityTests pins the bounce outputs to the CSV,
// and Telemetry002_FullFlightThroughEmbeddedBrush below replays the ENTIRE
// flight - launch tick through the embedded contact to rest - tick by tick,
// closing the gap the replay below deliberately still avoids. It starts from
// the first pure free-flight tick after the embedded contact and verifies
// everything the simulator owns afterwards: ~155 ticks including a
// WALL contact (CSV vy +118.3834 -> -53.2866, vx -102.4455 -> -46.1005, vz
// -93.6898 -> -44.4104 - all three fit 0.45*|v_in| + n*0.0140625 with v_in
// one full 64 Hz gravity step down and q=2 at contact fraction 0.670, which
// is the same whole-vector law the floors use, now measured on a vertical
// normal), three world floor bounces (CSV vz -209.4104 -> 96.4988,
// -93.5013 -> 44.3396, -40.6604 -> 20.5612 - same law) and the final rest at
// (-1334.5650, -2264.7250, -221.9688).
//
// The capture logs every physics state on two consecutive CSV rows (duplicate
// sampler frames); rows are deduplicated by identical adjacent position AND
// velocity. The dedup is verified rather than assumed: consecutive deduped
// states differ by exactly one 64 Hz physics step (dvz = -5 u/s in free
// flight), which the tick-for-tick match below proves - a 32 Hz aliasing
// would diverge on the first bounce input.
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

public sealed class Telemetry002ForensicReplayTests
{
    private readonly ITestOutputHelper _output;

    public Telemetry002ForensicReplayTests(ITestOutputHelper output) => _output = output;

    private static readonly string TelemetryCsv =
        @"D:\desktop\0824\cs2srv\server\game\csgo\addons\counterstrikesharp\plugins\CS2LineupFinder\data\telemetry\telemetry-002.csv";

   [Fact]
    public void Telemetry002_TickByTickDivergence()
    {
        if (!File.Exists(TelemetryCsv))
        {
            _output.WriteLine("SKIP: telemetry-002.csv not present on this machine.");
            return;
        }

        List<TelemetryRow> states = Deduplicate(LoadRows(TelemetryCsv));
        Assert.True(states.Count >= 160, $"unexpected deduped state count {states.Count}");

        // The first (non-world) contact flips vz from -528.9596 to +176.3102;
        // the replay starts on the first pure free-flight tick after it.
        int start = states.FindIndex(r => r.Velocity.Z > 50f) + 1;
        Assert.Equal(9, start);

        // Resting centre -221.9688 with the measured 2-unit hull puts the
        // floor surface at -223.9688 - the same hull evidence as telemetry-001.
        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-223.9688f);
        // Wall face back-solved from the CSV wall contact: the coast from the
        // contact lands the recorded tick-end y exactly when the centre stops
        // at y = -2218.0316, i.e. face -2216.0316 +- 0.0002 (CSV 4-decimal
        // rounding plus the fraction's own rounding). The wall's existence is
        // measured, not assumed: without it the vy flip has no physical cause.
        world.AddBox(
            new Vec3(-1e6f, -2216.0316f, -1e6f),
            new Vec3(1e6f, -2216.0316f + 4096f, 1e6f));

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(states[start].Position, states[start].Velocity, GrenadeType.Smoke, 1f, 64),
            world);

        _output.WriteLine(FormattableString.Invariant(
            $"replay states {states.Count - start}, sim points {result.Points.Count}, bounces {result.BounceCount}"));
        _output.WriteLine(FormattableString.Invariant(
            $"sim rest ({result.Impact.Position.X:0.####},{result.Impact.Position.Y:0.####},{result.Impact.Position.Z:0.####}); actual ({states[^1].Position.X:0.####},{states[^1].Position.Y:0.####},{states[^1].Position.Z:0.####})"));

        int n = System.Math.Min(result.Points.Count, states.Count - start);
        int firstDivergent = -1;
        int lastBounce = 0;
        float maxDp = 0f;
        for (int i = 0; i < n; i++)
        {
            TrajectoryPoint s = result.Points[i];
            TelemetryRow r = states[start + i];
            float dp = (s.Position - r.Position).Length;
            float dv = (s.Velocity - r.Velocity).Length;
            if (dp > maxDp)
            {
                maxDp = dp;
            }

            bool diverged = dp > 0.05f || dv > 0.05f;
            if (diverged && firstDivergent < 0)
            {
                firstDivergent = i;
            }

            if (s.BounceCount != lastBounce)
            {
                lastBounce = s.BounceCount;
                _output.WriteLine(FormattableString.Invariant(
                    $"contact {s.BounceCount} at sim tick {i}: sim v({s.Velocity.X:0.####},{s.Velocity.Y:0.####},{s.Velocity.Z:0.####}) dp={dp:0.####} dv={dv:0.####}"));
            }

            if (diverged || i >= n - 2)
            {
                _output.WriteLine(FormattableString.Invariant(
                    $"tick {i,3}  sim p({s.Position.X,10:0.####},{s.Position.Y,10:0.####},{s.Position.Z,10:0.####}) v({s.Velocity.X,9:0.####},{s.Velocity.Y,9:0.####},{s.Velocity.Z,9:0.####})  |  csv p({r.Position.X,10:0.####},{r.Position.Y,10:0.####},{r.Position.Z,10:0.####}) v({r.Velocity.X,9:0.####},{r.Velocity.Y,9:0.####},{r.Velocity.Z,9:0.####})  dp={dp,8:0.####} dv={dv,8:0.####}{(diverged ? "  <-- DIVERGES" : string.Empty)}"));
            }
        }

        Assert.True(firstDivergent < 0,
            $"telemetry-002 replay diverged at sim tick {firstDivergent}: dp or dv exceeded 0.05u");
        Assert.True(result.Points.Count >= n,
            $"simulator produced {result.Points.Count} points, replay needs {n}");
        Assert.True(result.BounceCount >= 4, $"expected 3 bounces + rest, got {result.BounceCount}");
        float restGap = (result.Impact.Position - states[^1].Position).Length;
        Assert.True(restGap <= 0.05f, $"telemetry-002 rest position off by {restGap:0.####}u");
        _output.WriteLine(FormattableString.Invariant(
            $"all {n} replayed ticks agree within 0.05u; max dp {maxDp:0.0000}u; rest gap {restGap:0.####}u"));
    }

    /// <summary>
    /// The full-flight replay the legacy test above deliberately avoided: from
    /// the launch tick THROUGH the embedded clip-brush contact (CSV contact 8)
    /// to rest. The world encodes only what the forensics measured:
    /// - the upper pad face -191.9688 (hull-centre contact -189.9688), back
    ///   solved from the tick-8 parabolic contact fit (0.002u residual);
    /// - a ~1.5u clip brush over that pad whose hull-2 overlap reproduces the
    ///   probe A signature (hull 2 embedded, hull 1.5 clear);
    /// - the lower floor -223.9688 and the wall -2216.0316 from the legacy
    ///   replay, which the tick-9-onward replay already validates.
    /// Success means the simulator crosses the embedded contact tick-for-tick
    /// and still lands on the recorded rest point - the offline proof that the
    /// ClipEmbeddedElasticityScale rule closes the 72.37u live-replay gap.
    /// </summary>
    [Fact]
    public void Telemetry002_FullFlightThroughEmbeddedBrush()
    {
        if (!File.Exists(TelemetryCsv))
        {
            _output.WriteLine("SKIP: telemetry-002.csv not present on this machine.");
            return;
        }

        List<TelemetryRow> states = Deduplicate(LoadRows(TelemetryCsv));
        Assert.True(states.Count >= 160, $"unexpected deduped state count {states.Count}");

        MockWorldGeometry world = new();
        world.AddInfiniteFloor(-223.9688f);
        world.AddBox(
            new Vec3(-1e6f, -2216.0316f, -1e6f),
            new Vec3(1e6f, -2216.0316f + 4096f, 1e6f));
        // Upper pad: the tick-8 contact plane. Kept clear of the post-bounce
        // descent (the ball crosses the pad-top height again only at
        // x ~ -1316, outside this pad).
        world.AddBox(
            new Vec3(-1290f, -2400f, -191.9688f - 4096f),
            new Vec3(-1120f, -2240f, -191.9688f));
        // The embedded brush: 1.47u thick slab on the pad. Hull 2 from the
        // tick-8 start dips into it (IsAllSolid), hull 1.5 clears it - the
        // measured probe A discrimination.
        world.AddClipBrush(
            new Vec3(-1210f, -2327f, -191.9688f),
            new Vec3(-1200f, -2317f, -190.5f));

        GrenadeSimulator simulator = new(new PhysicsParameters());
        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(states[0].Position, states[0].Velocity, GrenadeType.Smoke, 1f, 64),
            world);

        _output.WriteLine(FormattableString.Invariant(
            $"full replay: sim points {result.Points.Count}, bounces {result.BounceCount}, flight {result.FlightTime:0.0000}s"));
        _output.WriteLine(FormattableString.Invariant(
            $"sim rest ({result.Impact.Position.X:0.####},{result.Impact.Position.Y:0.####},{result.Impact.Position.Z:0.####}); actual ({states[^1].Position.X:0.####},{states[^1].Position.Y:0.####},{states[^1].Position.Z:0.####})"));

        int n = System.Math.Min(result.Points.Count, states.Count);
        int firstDivergent = -1;
        float maxDp = 0f;
        for (int i = 0; i < n; i++)
        {
            TrajectoryPoint s = result.Points[i];
            TelemetryRow r = states[i];
            float dp = (s.Position - r.Position).Length;
            float dv = (s.Velocity - r.Velocity).Length;
            if (dp > maxDp)
            {
                maxDp = dp;
            }

            if ((dp > 0.05f || dv > 0.05f) && firstDivergent < 0)
            {
                firstDivergent = i;
                _output.WriteLine(FormattableString.Invariant(
                    $"first divergent tick {i}: sim p({s.Position.X:0.####},{s.Position.Y:0.####},{s.Position.Z:0.####}) v({s.Velocity.X:0.####},{s.Velocity.Y:0.####},{s.Velocity.Z:0.####}) | csv p({r.Position.X:0.####},{r.Position.Y:0.####},{r.Position.Z:0.####}) v({r.Velocity.X:0.####},{r.Velocity.Y:0.####},{r.Velocity.Z:0.####}) dp={dp:0.####} dv={dv:0.####}"));
            }
        }

        Assert.True(firstDivergent < 0,
            $"full-flight replay diverged at tick {firstDivergent}");
        float restGap = (result.Impact.Position - states[^1].Position).Length;
        Assert.True(restGap <= 0.05f, $"full-flight rest position off by {restGap:0.####}u");
        _output.WriteLine(FormattableString.Invariant(
            $"all {n} ticks incl. embedded contact agree within 0.05u; max dp {maxDp:0.0000}u; rest gap {restGap:0.####}u"));
    }

    // The capture logs each physics state on two consecutive rows (duplicate
    // sampler frames), so identical adjacent states collapse to one.
    private static List<TelemetryRow> Deduplicate(List<TelemetryRow> rows)
    {
        var states = new List<TelemetryRow> { rows[0] };
        for (int i = 1; i < rows.Count; i++)
        {
            TelemetryRow last = states[^1];
            TelemetryRow current = rows[i];
            if ((current.Position - last.Position).LengthSquared > 1e-6f
                || (current.Velocity - last.Velocity).LengthSquared > 1e-6f)
            {
                states.Add(current);
            }
        }

        return states;
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
