// <copyright file="BounceRuleAudit.cs" company="CS2LineupFinder">
// For every recorded engine bounce this audit decomposes the measured velocity
// discontinuity against the candidate bounce rules the simulator could apply,
// and probes how deeply the grenade hull sits inside world geometry at the
// contact. Every number is either read straight from the telemetry CSV or
// measured by the live engine trace - nothing is fitted or assumed here.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Game;

namespace CS2LineupFinder.Plugin.Diagnostics;

/// <summary>
/// Decides, from recorded evidence alone, which bounce rule the CS2 engine
/// applies at each contact: plain mirror x restitution, the +residual world
/// impulse, the clip-embedded elasticity scale, or combinations. The audit
/// prints the per-rule fit error next to the raw pre/post velocities and an
/// embedding-depth probe so the rule choice is a measurement, not a guess.
/// </summary>
public static class BounceRuleAudit
{
    /// <summary>Gravity velocity step per tick, measured: 320 u/s^2 / 64 Hz.</summary>
    private const float GravityStepZ = -5f;

    /// <summary>Speed error beyond which a tick is a contact discontinuity.</summary>
    private const float BounceThreshold = 2f;

    /// <summary>Measured world restitution for grenades.</summary>
    private const float Restitution = 0.45f;

    /// <summary>Measured clip-embedded elasticity scale.</summary>
    private const float EmbeddedScale = 0.7476252f;

    /// <summary>Measured world bounce residual impulse along +Z.</summary>
    private const float Residual = 0.0140625f;

    /// <summary>Runs the audit for one telemetry CSV.</summary>
    public static IReadOnlyList<string> Run(string csvPath, ITraceProbe probe)
    {
        var lines = new List<string>();
        if (probe is not ReflectiveTraceBackend)
        {
            return lines;
        }

        if (!LiveMapTelemetryReplay.TryLoadRows(csvPath, out var rows) || rows.Count < 2)
        {
            return lines;
        }

        string fileName = System.IO.Path.GetFileName(csvPath);
        for (int i = 1; i < rows.Count; i++)
        {
            Vec3 dv = rows[i].Velocity - rows[i - 1].Velocity;
            Vec3 gravityOnly = new(0f, 0f, GravityStepZ);
            if ((dv - gravityOnly).Length <= BounceThreshold)
            {
                continue;
            }

            Vec3 pre = rows[i - 1].Velocity;
            Vec3 post = rows[i].Velocity;
            Vec3 p = rows[i - 1].Position;
            Vec3 dvBounce = dv - gravityOnly;

            // Measured elasticity: gravity never touches the horizontal plane,
            // so |post_h| / |pre_h| is the engine's own tangential retention at
            // this contact - no model, no fit. 0 when the row is a rest/sleep
            // transition rather than a mirror.
            float preH = MathF.Sqrt((pre.X * pre.X) + (pre.Y * pre.Y));
            float postH = MathF.Sqrt((post.X * post.X) + (post.Y * post.Y));
            float eMeasured = preH > 5f ? postH / preH : 0f;

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"bounce audit {fileName} row {i}: pre ({pre.X:0.####},{pre.Y:0.####},{pre.Z:0.####}) " +
                $"post ({post.X:0.####},{post.Y:0.####},{post.Z:0.####}) " +
                $"dvB ({dvBounce.X:0.####},{dvBounce.Y:0.####},{dvBounce.Z:0.####}) " +
                $"eH={eMeasured:0.#######}"));

            // Zero-length probes at the exact pre/post hull centres: IsAllSolid
            // there is the start/end embedded state - the discriminator between
            // "fresh entry into a clip brush" and "already resting inside one".
            lines.Add("bounce audit start-centre: " + probe.DescribeTrace(p, p, 2f));
            lines.Add("bounce audit end-centre:   " + probe.DescribeTrace(rows[i].Position, rows[i].Position, 2f));

            // Embedding-depth probe: 1 u and 13 u below the recorded hull
            // centre. A free hull clears the floor inside this window (a frac
            // in (0,1) against StaticLevel); an embedded hull reports
            // IsAllSolid=1 Fraction=0 for the whole window.
            lines.Add("bounce audit depth -1: " + probe.DescribeTrace(
                p + new Vec3(0f, 0f, -1f), p + new Vec3(0f, 0f, -13f), 2f));

            // Candidate rules about the up normal, for both half-tick gravity
            // conventions the simulator supports. Error is the 3D distance
            // between the rule's predicted post-bounce velocity and the CSV.
            foreach (int q in new[] { 1, 2 })
            {
                Vec3 vIn = pre + new Vec3(0f, 0f, -q * 2.5f);
                Vec3 mirror = new(vIn.X, vIn.Y, -vIn.Z);
                Vec3 r45 = mirror * Restitution + new Vec3(0f, 0f, Residual);
                Vec3 r45n = mirror * Restitution;
                Vec3 r36 = mirror * (Restitution * EmbeddedScale) + new Vec3(0f, 0f, Residual);
                Vec3 r36n = mirror * (Restitution * EmbeddedScale);
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"bounce audit {fileName} row {i} q={q}: err45R={(r45 - post).Length:0.####} " +
                    $"err45={(r45n - post).Length:0.####} err36R={(r36 - post).Length:0.####} err36={(r36n - post).Length:0.####}"));
            }
        }

        return lines;
    }
}
