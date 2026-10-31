// <copyright file="RecordedLandingReplayTests.cs" company="CS2LineupFinder">
// Replays the landing samples recorded in-game by LandingCalibrator against the
// analytic flat-floor world, so physics parameter changes are judged against
// real game behaviour instead of only synthetic expectations.
// </copyright>

using System;
using System.Collections.Generic;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Core;
using Xunit;
using Xunit.Abstractions;

namespace CS2LineupFinder.Core.Tests;

public class RecordedLandingReplayTests
{
    private readonly ITestOutputHelper _output;

    public RecordedLandingReplayTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record Sample(
        string Name, Vec3 Origin, float EyeHeight, float Yaw, float Pitch,
        Vec3 ActualImpact, float RecordedError);

    private static readonly Sample[] Samples =
    {
        new("1009-1 palace", new Vec3(183.49892f, -1477.1948f, -175.96875f), 64f, -139.2f, 11.7f,
            new Vec3(-822.4844f, -2358.0156f, -173.96875f), 37.0f),
        new("1009-2 mid window", new Vec3(502.93915f, -109.203995f, -160.003f), 64f, -148.8f, 18.9f,
            new Vec3(-686.0562f, -884.6193f, -253.96875f), 58.1f),
        new("1009-3 ramp", new Vec3(-91.31061f, -332.0716f, -159.96875f), 64f, -166.5f, 15.1f,
            new Vec3(-1191.1335f, -630.13257f, -165.96875f), 9.7f),
        new("1013 palace shallow", new Vec3(38.9705f, -2036.9613f, -39.96875f), 64f, -161.2f, -6.9f,
            new Vec3(-1089.178f, -2421.01f, -165.96875f), 167.4f),
        new("1014 palace steep", new Vec3(90.239044f, -2006.9675f, -39.96875f), 64f, -158.8f, -12.1f,
            new Vec3(-825.45056f, -2358.9136f, -173.96875f), 97.8f),
    };

    private static TrajectoryResult Simulate(Sample s, PhysicsParameters p)
    {
        Vec3 aim = GrenadeSimulator.DirectionFromAnglesForTest(s.Yaw, s.Pitch);
        Vec3 velocity = ThrowVelocityCalculator.Compute(aim, Vec3.Zero, ThrowButton.Primary, GrenadeType.Smoke, 1f, p);
        // Production parity: the engine releases along the BIASED direction
        // (weapon_basecsgrenade.cpp remaps the pitch before pushing vecSrc out),
        // so the harness must push the eye offset along the biased vector too.
        // The unbiased variant released off line at oblique pitches and
        // contaminated every replay residual with a few units of harness error.
        Vec3 biased = ThrowVelocityCalculator.ApplyAimBias(aim, p);
        Vec3 release = s.Origin + new Vec3(0f, 0f, s.EyeHeight) + (biased * p.ReleaseOffset.Value);
        GrenadeSimulator simulator = new(p);
        // ActualImpact is the hull CENTRE the recorder read off AbsOrigin, so the
        // surface it rests on sits one hull half-height (2 units) below it.
        return simulator.Simulate(
            new ThrowParams(release, velocity, GrenadeType.Smoke, 1f, 64),
            MockWorldGeometry.Empty().AddInfiniteFloor(s.ActualImpact.Z - 2f));
    }

    /// <summary>
    /// The palace shallow throw (the exact scenario players saw as a ~160u
    /// undershoot) is dominated by the first-bounce behaviour, so the faithful
    /// engine model (no tangential loss at impact) must stay close to the
    /// recorded landing on the replay plane. This is a smoke check only: the
    /// authoritative calibration is the in-game replay over the real map
    /// geometry, not this infinite plane.
    /// </summary>
    [Fact]
    public void RecordedLandings_PalaceShallowFaithfulBounceCheck()
    {
        PhysicsParameters p = new(); // defaults are the faithful engine model
        Sample shallow = Samples[3]; // 1013 palace shallow
        TrajectoryResult r = Simulate(shallow, p);
        float e = (r.Impact.Position - shallow.ActualImpact).Length;
        Assert.True(e <= 70f,
            $"palace shallow: replay error {e:0.#}u exceeds 70u (impact {r.Impact.Position.X:0.#},{r.Impact.Position.Y:0.#}).");
    }

    /// <summary>
    /// The 2026-10-14 measured palace throw: plugin predicted
    /// (-923.2, -2359.7, -168.0), the game detonated at
    /// (-825.5, -2358.9, -174.0), a 97.8u error almost entirely along the
    /// throw direction. Replayed here on a floor at the measured detonation
    /// height so parameter changes can be judged against the real stop point.
    /// Diagnostic only: it prints the residual for the live defaults and for a
    /// spread of roll/friction settings, no assertion.
    /// </summary>
    [Fact]
    public void RecordedLandings_Palace1014Diagnostics()
    {
        Sample s = new(
            "1014 palace",
            new Vec3(90.239044f, -2006.9675f, -39.96875f),
            64f,
            -158.8f,
            -12.1f,
            new Vec3(-825.45056f, -2358.9136f, -173.96875f),
            97.8f);

        _output.WriteLine($"actual impact: {s.ActualImpact.X:0.#},{s.ActualImpact.Y:0.#},{s.ActualImpact.Z:0.#}");
        Report("baseline (live config)", s, new PhysicsParameters());

        PhysicsParameters roll45 = new();
        roll45.GroundRollFrictionRate.Value = 4.5f;
        Report("roll 4.5", s, roll45);

        PhysicsParameters roll60 = new();
        roll60.GroundRollFrictionRate.Value = 6.0f;
        Report("roll 6.0", s, roll60);

        PhysicsParameters gf85 = new();
        gf85.SurfaceElasticityScale.Value = 0.7439f;
        Report("surface 0.7439", s, gf85);
    }

    /// <summary>
    /// Parameter sweep over the recorded landings. Purely diagnostic: the four
    /// samples mix flat ground with slopes and level drops, so no single flat
    /// floor can fit them all. Run this before and after a physics change to
    /// see which samples moved and in which direction.
    /// </summary>
    [Fact]
    public void RecordedLandings_ParameterMatrix()
    {
        foreach (float gf in new[] { 0.70f, 0.85f, 0.90f })
        foreach (bool smokeOn in new[] { true, false })
        {
            PhysicsParameters p = new();
            p.SurfaceElasticityScale.Value = gf;
            p.SmokeExtraDampingEnabled.Value = smokeOn ? 1f : 0f;
            _output.WriteLine(Row($"surf={gf:0.00} smoke={(smokeOn ? 1 : 0)}", p));
        }

        foreach (float mu in new[] { 0.05f, 0.10f, 0.15f })
        foreach (bool smokeOn in new[] { true, false })
        {
            PhysicsParameters p = new();
            p.BouncePaddingEnabled.Value = mu switch { 0.05f => 0.5f, 0.10f => 1f, _ => 0f };
            p.SmokeExtraDampingEnabled.Value = smokeOn ? 1f : 0f;
            _output.WriteLine(Row($"pad={p.BouncePaddingEnabled.Value:0.0} smoke={(smokeOn ? 1 : 0)}", p));
        }

        // GroundFriction and Coulomb BounceFrictionMu were deleted once the
        // whole-vector restitution law explained every measured contact, so the
        // sweeps that used to run over them now vary GroundRollFrictionRate,
        // the only ground-contact term that still exists.
        foreach (float roll in new[] { 2f, 4f, 8f })
        {
            PhysicsParameters p = new();
            p.GroundRollFrictionRate.Value = roll;
            _output.WriteLine(Row($"roll={roll:0.0} smoke=0", p));
        }

        PhysicsParameters rs30 = new();
        rs30.RestSpeed.Value = 30f;
        _output.WriteLine(Row("restSpeed=30", rs30));

        PhysicsParameters muRs = new();
        muRs.GroundRollFrictionRate.Value = 4f;
        muRs.RestSpeed.Value = 30f;
        _output.WriteLine(Row("roll=4 rest=30", muRs));
    }

    /// <summary>
    /// Per-sample legacy replay errors, printed for the calibration log. No
    /// assertion: 1009-2/1009-3 cross map slopes the flat floor cannot express,
    /// so a hard mean bound would assert on geometry error, not physics error.
    /// </summary>
    [Fact]
    public void RecordedLandings_LegacyDiagnostics()
    {
        PhysicsParameters p = new();
        float total = 0f;
        foreach (Sample s in Samples)
        {
            TrajectoryResult r = Simulate(s, p);
            float e = (r.Impact.Position - s.ActualImpact).Length;
            total += e;
            _output.WriteLine($"{s.Name}: replay error {e:0.#}u");
        }

        _output.WriteLine($"mean replay error: {total / Samples.Length:0.#}u");
    }

    [Fact]
    public void RecordedLandings_SteepOvershootDiagnostics()
    {
        // Both steep palace samples land ~74u short of the recorded impact on the
        // real map geometry, while every shallow throw sits within 7.2u. The
        // suspect is the bounce padding branch: a steep throw keeps a large
        // vertical component at first ground contact, which pushes the reflected
        // speed deep into the >96000 padding window and multiplies away energy.
        foreach (float pad in new[] { 1f, 0.5f, 0f })
        foreach (float roll in new[] { 0f, 2f, 4f })
        {
            PhysicsParameters p = new();
            p.BouncePaddingEnabled.Value = pad;
            p.GroundRollFrictionRate.Value = roll;
            _output.WriteLine(Row($"pad={pad:0.0} roll={roll:0.0}", p));
        }
    }

    private void Report(string label, Sample s, PhysicsParameters p)
    {
        TrajectoryResult r = Simulate(s, p);
        Vec3 d = r.Impact.Position - s.ActualImpact;
        _output.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{label}: impact ({r.Impact.Position.X:0.##},{r.Impact.Position.Y:0.##},{r.Impact.Position.Z:0.##}) " +
            $"err {d.Length:0.##}u (dx {d.X:0.##} dy {d.Y:0.##} dz {d.Z:0.##}) bounces {r.BounceCount} flight {r.FlightTime:0.000}s"));
    }

    private string Row(string label, PhysicsParameters p)
    {
        float total = 0f;
        var parts = new List<string>();
        foreach (Sample s in Samples)
        {
            TrajectoryResult r = Simulate(s, p);
            float e = (r.Impact.Position - s.ActualImpact).Length;
            total += e;
            parts.Add($"{s.Name.Split(' ')[0]}={e:0.#}");
        }

        return $"{label}: mean={total / Samples.Length:0.#}u  " + string.Join("  ", parts);
    }
}
