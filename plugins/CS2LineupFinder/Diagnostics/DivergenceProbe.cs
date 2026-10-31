// <copyright file="DivergenceProbe.cs" company="CS2LineupFinder">
// Interrogates the raw engine trace at the first divergent tick of a telemetry
// replay: every sweep uses coordinates measured from the recording, and every
// reported line is the engine's own raw field dump - nothing is fitted,
// assumed or invented here.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Game;

namespace CS2LineupFinder.Plugin.Diagnostics;

/// <summary>
/// Asks the engine's raw trace what exists around the first divergent tick.
/// The recording shows how the engine handled the divergent tick; sweeping the
/// recorded move directly answers which collision shape the raw trace reports
/// there, and a fine march pins where along the segment it lives.
/// </summary>
public static class DivergenceProbe
{
    private const int MarchSegments = 16;

    /// <summary>Builds the probe report for one divergent capture.</summary>
    /// <param name="csvPath">Telemetry CSV the divergence was found in.</param>
    /// <param name="firstDivergentTick">Tick index from the replay (deduped rows).</param>
    /// <param name="probe">Raw-field probe surface of the live trace backend.</param>
    public static IReadOnlyList<string> Describe(string csvPath, int firstDivergentTick, ITraceProbe probe)
    {
        var lines = new List<string>();
        if (!TryLoadRows(csvPath, out var rows) || firstDivergentTick <= 0 || firstDivergentTick >= rows.Count)
        {
            return lines;
        }

        TelemetryRow fromRow = rows[firstDivergentTick - 1];
        TelemetryRow toRow = rows[firstDivergentTick];
        Vec3 from = fromRow.Position;
        Vec3 velocity = fromRow.Velocity;
        Vec3 engineEnd = toRow.Position;
        Vec3 linearEnd = from + (velocity * (1f / 64f));

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"probe from ({from.X:0.####},{from.Y:0.####},{from.Z:0.####}) v ({velocity.X:0.####},{velocity.Y:0.####},{velocity.Z:0.####}) " +
            $"engineEnd ({engineEnd.X:0.####},{engineEnd.Y:0.####},{engineEnd.Z:0.####})"));

        // The engine's own measured move for the divergent tick, asked twice:
        // swept sphere (what the simulator uses) and raw line (what the BSP sees).
        lines.Add("A engine-move hull2: " + probe.DescribeTrace(from, engineEnd, 2f));
        lines.Add("B engine-move line:  " + probe.DescribeTrace(from, engineEnd, 0f));

        // The same start velocity continued one tick linearly - the move the
        // simulator swept when it fired a tick early. If this reports a hit the
        // engine's own sweep denies, the obstruction exists for spheres but not
        // for the engine's collision rules on that tick.
        lines.Add("C linear-move hull2: " + probe.DescribeTrace(from, linearEnd, 2f));
        lines.Add("D linear-move line:  " + probe.DescribeTrace(from, linearEnd, 0f));

        // Fine march along the recorded move: pins WHERE along the segment the
        // obstruction lives when the full-segment sweep disagrees with the
        // simulator's reported contact fraction.
        Vec3 delta = engineEnd - from;
        float length = delta.Length;
        if (length > 1e-4f)
        {
            for (int i = 0; i < MarchSegments; i++)
            {
                Vec3 segFrom = from + (delta * ((float)i / MarchSegments));
                Vec3 segTo = from + (delta * ((float)(i + 1) / MarchSegments));
                string raw = probe.DescribeTrace(segFrom, segTo, 0f);
                if (raw.Contains("DidHit=1", StringComparison.Ordinal))
                {
                    lines.Add($"E march {i:D2}/{MarchSegments:D2}: {raw}");
                }
            }
        }

        // What surface sits directly under the engine's own landing point: a
        // steep normal here is direct evidence of a ramp or ledge the flat-floor
        // model does not expect.
        lines.Add("F under-engine-end hull2: " + probe.DescribeTrace(
            engineEnd, engineEnd + new Vec3(0f, 0f, -12f), 2f));

        // Radius bisection over the recorded engine move: if the contact the
        // hull2 sweep reports disappears at smaller radii, the simulator's
        // sphere radius is the disagreement; if every radius reports the same
        // hit, the surface itself is something the engine's grenade ignores
        // (collision-mask semantics), not a shape-size artifact.
        foreach (float radius in new[] { 0.5f, 1f, 1.5f, 2f })
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"G engine-move r={radius:0.#}: ") + probe.DescribeTrace(from, engineEnd, radius));
        }

        // The same radius question for the linear move the simulator swept when
        // it fired the tick early: this is the segment whose hull2 hit produced
        // the premature bounce.
        foreach (float radius in new[] { 0.5f, 1f, 1.5f, 2f })
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"H linear-move r={radius:0.#}: ") + probe.DescribeTrace(from, linearEnd, radius));
        }

        // Masked probes - Source 2 collision filter semantics, measured from the
        // loaded API (Contents enum) and the server binary (m_nInteracts*).
        // I: the divergent engine move swept with PlayerClip+NpcClip excluded.
        // If the premature hit disappears here the simulator must apply the same
        // exclusion the grenade's own filter uses.
        const ulong interactWith = 0x1UL | 0x4000UL | 0x40000000UL; // Solid|WorldGeometry|StaticLevel
        const ulong clipBits = 0x10UL | 0x20UL; // PlayerClip|NpcClip
        lines.Add("I engine-move masked(excl clips): " + probe.DescribeTraceMasked(from, engineEnd, 2f, interactWith, clipBits));

        // K: the identical masked sweep without the exclusion - isolates the
        // exclusion as the only difference between I and A.
        lines.Add("K engine-move masked(no excl):    " + probe.DescribeTraceMasked(from, engineEnd, 2f, interactWith, 0UL));

        // The segment the simulator itself sweeps for this tick: the free-flight
        // parabola step v*dt - 0.5*g*dt^2 upward, NOT the recorded chord. If the
        // default mask hits here while the clip-excluded mask misses, the tick-8
        // contact comes from a clip surface the engine grenade ignores, and the
        // simulator's sweep must use the same exclusion.
        float dt = 1f / 64f;
        Vec3 simStart = from + new Vec3(velocity.X * dt, velocity.Y * dt, velocity.Z * dt - 160f * dt * dt);
        Vec3 simEnd = from + new Vec3(velocity.X * dt, velocity.Y * dt, velocity.Z * dt - 320f * dt * dt);
        lines.Add("L simulator-incident masked(excl clips): " + probe.DescribeTraceMasked(simStart, simEnd, 2f, interactWith, clipBits));
        lines.Add("M simulator-incident default:            " + probe.DescribeTrace(simStart, simEnd, 2f));

        // Name the entity behind the divergent contact. A pawn/bot here means
        // the recording bounced off a live player; a static/brush response keeps
        // the suspicion on collision-mask semantics instead.
        lines.Add("N engine-move hit-entity hull2: " + probe.DescribeHitEntity(from, engineEnd, 2f));
        lines.Add("O simulator-incident hit-entity hull2: " + probe.DescribeHitEntity(simStart, simEnd, 2f));

        // J: the floor under the engine rest point with the same exclusion. The
        // floor must remain solid (IsAllSolid=1) - if it does, excluding the clip
        // layers is safe for standing surfaces and I's disappearing hit is the
        // clip surface, not the floor.
        lines.Add("J under-engine-end masked(excl clips): " + probe.DescribeTraceMasked(
            engineEnd, engineEnd + new Vec3(0f, 0f, -12f), 2f, interactWith, clipBits));

        return lines;
    }

    private static bool TryLoadRows(string path, out List<TelemetryRow> rows)
    {
        rows = new List<TelemetryRow>();
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            bool header = true;
            Vec3 previous = default;
            foreach (string line in File.ReadLines(path))
            {
                if (header)
                {
                    header = false;
                    continue;
                }

                string[] fields = line.Split(',');
                if (fields.Length < 8)
                {
                    continue;
                }

                var position = new Vec3(Parse(fields[1]), Parse(fields[2]), Parse(fields[3]));
                var velocity = new Vec3(Parse(fields[4]), Parse(fields[5]), Parse(fields[6]));
                if (rows.Count > 0 && (position - previous).LengthSquared < 1e-6f)
                {
                    continue;
                }

                previous = position;
                rows.Add(new TelemetryRow(Parse(fields[0]), position, velocity));
            }

            return rows.Count >= 2;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static float Parse(string value) => float.Parse(value.Trim(), CultureInfo.InvariantCulture);
}
