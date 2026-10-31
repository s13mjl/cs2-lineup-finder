// <copyright file="LiveMapTelemetryReplay.cs" company="CS2LineupFinder">
// Tick-by-tick replay of a recorded telemetry capture on the LIVE map geometry,
// so the first divergent tick pins down exactly where the live trace path
// disagrees with the engine physics the CSV captured.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CS2LineupFinder.Contracts;

namespace CS2LineupFinder.Plugin.Diagnostics;

/// <summary>One comparison point: simulator state against the CSV row.</summary>
public readonly record struct ReplayComparison(
    int Tick,
    Vec3 SimPosition,
    Vec3 SimVelocity,
    Vec3 CsvPosition,
    Vec3 CsvVelocity,
    int BounceCount)
{
    /// <summary>Distance between the simulated and recorded positions.</summary>
    public float PositionDelta => (SimPosition - CsvPosition).Length;

    /// <summary>Speed difference between the simulated and recorded velocity.</summary>
    public float VelocityDelta => (SimVelocity - CsvVelocity).Length;
}

/// <summary>
/// Result of replaying one telemetry CSV on the live map: every compared tick
/// plus the first divergence, so a report can quote the exact tick where the
/// live world starts disagreeing with the engine.
/// </summary>
public sealed class LiveMapReplayResult
{
    /// <summary>CSV rows that were compared (after de-duplication).</summary>
    public int ComparedTicks { get; init; }

    /// <summary>Index of the first tick exceeding the tolerance, or -1.</summary>
    public int FirstDivergentTick { get; init; } = -1;

    /// <summary>Position error at the first divergence, or 0 when clean.</summary>
    public float FirstDivergencePositionDelta { get; init; }

    /// <summary>Speed error at the first divergence, or 0 when clean.</summary>
    public float FirstDivergenceVelocityDelta { get; init; }

    /// <summary>Largest position error before the first divergence.</summary>
    public float MaxCleanPositionDelta { get; init; }

    /// <summary>Simulator rest point (final impact position).</summary>
    public Vec3 SimRestPosition { get; init; }

    /// <summary>Where the CSV says the grenade actually stopped.</summary>
    public Vec3 CsvRestPosition { get; init; }

    /// <summary>Distance between the two rest points.</summary>
    public float RestGap => (SimRestPosition - CsvRestPosition).Length;

    /// <summary>Compared states, for detailed logging.</summary>
    public IReadOnlyList<ReplayComparison> Comparisons { get; init; } = Array.Empty<ReplayComparison>();

    /// <summary>Simulator bounce count at the end of the replay.</summary>
    public int BounceCount { get; init; }

    /// <summary>Whether the simulator path hit the per-tick bounce cap.</summary>
    public bool HitBounceCap { get; init; }
}

/// <summary>
/// Runs the forensic replay against the IWorldGeometry the server actually
/// traces, using the same state mapping the offline forensic tests proved:
/// CSV row 0 is the launch state (the simulator starts exactly there), every
/// later row is a tick-boundary state (one 1/64 s parabola step each). The
/// tolerance mirrors the offline result: free flight agrees to CSV rounding
/// (0.05u), so anything larger is a real disagreement, not noise.
/// </summary>
public static class LiveMapTelemetryReplay
{
    private const float Tolerance = 0.05f;

    /// <summary>
    /// Replays csvPath against world. Returns null when the file cannot be
    /// read or has fewer than two states.
    /// </summary>
    public static LiveMapReplayResult? Run(string csvPath, GrenadeSimulatorShim simulator, IWorldGeometry world)
    {
        ArgumentNullException.ThrowIfNull(simulator);
        ArgumentNullException.ThrowIfNull(world);

        if (!TryLoadRows(csvPath, out List<TelemetryRow> rows) || rows.Count < 2)
        {
            return null;
        }

        TrajectoryResult result = simulator.Simulate(
            new ThrowParams(rows[0].Position, rows[0].Velocity, GrenadeType.Smoke, 1f, 64),
            world);

        var comparisons = new List<ReplayComparison>(rows.Count);
        int n = Math.Min(result.Points.Count, rows.Count);
        int firstDivergent = -1;
        float maxClean = 0f;
        for (int i = 0; i < n; i++)
        {
            TrajectoryPoint s = result.Points[i];
            TelemetryRow r = rows[i];
            var comparison = new ReplayComparison(
                i, s.Position, s.Velocity, r.Position, r.Velocity, s.BounceCount);
            comparisons.Add(comparison);

            if (firstDivergent < 0)
            {
                if (comparison.PositionDelta > Tolerance || comparison.VelocityDelta > Tolerance)
                {
                    firstDivergent = i;
                }
                else
                {
                    maxClean = MathF.Max(maxClean, comparison.PositionDelta);
                }
            }
        }

        return new LiveMapReplayResult
        {
            ComparedTicks = n,
            FirstDivergentTick = firstDivergent,
            FirstDivergencePositionDelta = firstDivergent >= 0 ? comparisons[firstDivergent].PositionDelta : 0f,
            FirstDivergenceVelocityDelta = firstDivergent >= 0 ? comparisons[firstDivergent].VelocityDelta : 0f,
            MaxCleanPositionDelta = maxClean,
            SimRestPosition = result.Impact.Position,
            CsvRestPosition = rows[rows.Count - 1].Position,
            Comparisons = comparisons,
            BounceCount = result.BounceCount,
            HitBounceCap = result.BounceCount >= 32,
        };
    }

    /// <summary>Builds the one-line report for one replay.</summary>
    public static string Describe(LiveMapReplayResult result, string fileName)
    {
        var b = new StringBuilder();
        b.Append("live-map replay ").Append(fileName);
        b.Append(": ").Append(result.ComparedTicks.ToString(CultureInfo.InvariantCulture)).Append(" ticks, ");
        if (result.FirstDivergentTick < 0)
        {
            b.Append("no divergence, ");
        }
        else
        {
            b.Append("first divergence at tick ").Append(result.FirstDivergentTick.ToString(CultureInfo.InvariantCulture));
            b.Append(" (dp ").Append(result.FirstDivergencePositionDelta.ToString("0.####", CultureInfo.InvariantCulture)).Append("u");
            b.Append(", dv ").Append(result.FirstDivergenceVelocityDelta.ToString("0.####", CultureInfo.InvariantCulture)).Append("u/s), ");
        }

        b.Append("max clean dp ").Append(result.MaxCleanPositionDelta.ToString("0.####", CultureInfo.InvariantCulture)).Append("u, ");
        b.Append("rest sim (").Append(result.SimRestPosition.X.ToString("0.##", CultureInfo.InvariantCulture));
        b.Append(',').Append(result.SimRestPosition.Y.ToString("0.##", CultureInfo.InvariantCulture));
        b.Append(',').Append(result.SimRestPosition.Z.ToString("0.##", CultureInfo.InvariantCulture)).Append(") ");
        b.Append("csv (").Append(result.CsvRestPosition.X.ToString("0.##", CultureInfo.InvariantCulture));
        b.Append(',').Append(result.CsvRestPosition.Y.ToString("0.##", CultureInfo.InvariantCulture));
        b.Append(',').Append(result.CsvRestPosition.Z.ToString("0.##", CultureInfo.InvariantCulture)).Append(") ");
        b.Append("gap ").Append(result.RestGap.ToString("0.####", CultureInfo.InvariantCulture)).Append("u, ");
        b.Append("bounces ").Append(result.BounceCount.ToString(CultureInfo.InvariantCulture));
        if (result.HitBounceCap)
        {
            b.Append(" [bounce cap hit - mapping invalid]");
        }

        return b.ToString();
    }

    /// <summary>
    /// Builds a detail dump for the ticks around the divergence (or the last
    /// ticks when the replay is clean), aimed at the divergence tick itself.
    /// </summary>
    public static IReadOnlyList<string> DetailLines(LiveMapReplayResult result)
    {
        var lines = new List<string>();
        if (result.Comparisons.Count == 0)
        {
            return lines;
        }

        int focus = result.FirstDivergentTick >= 0
            ? result.FirstDivergentTick
            : result.Comparisons.Count - 1;
        int from = Math.Max(0, focus - 2);
        int to = Math.Min(result.Comparisons.Count - 1, focus + 2);
        for (int i = from; i <= to; i++)
        {
            ReplayComparison c = result.Comparisons[i];
            var b = new StringBuilder();
            b.Append("tick ");
            b.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(3));
            b.Append(" sim p(");
            AppendVec(b, c.SimPosition);
            b.Append(") v(");
            AppendVec(b, c.SimVelocity);
            b.Append(") b").Append(c.BounceCount.ToString(CultureInfo.InvariantCulture));
            b.Append(" | csv p(");
            AppendVec(b, c.CsvPosition);
            b.Append(") v(");
            AppendVec(b, c.CsvVelocity);
            b.Append(")");
            b.Append(" dp=").Append(c.PositionDelta.ToString("0.####", CultureInfo.InvariantCulture));
            b.Append(" dv=").Append(c.VelocityDelta.ToString("0.####", CultureInfo.InvariantCulture));
            if (i == result.FirstDivergentTick)
            {
                b.Append("  <-- DIVERGES");
            }

            lines.Add(b.ToString());
        }

        return lines;
    }

    private static void AppendVec(StringBuilder b, Vec3 v)
    {
        b.Append(v.X.ToString("0.####", CultureInfo.InvariantCulture));
        b.Append(',');
        b.Append(v.Y.ToString("0.####", CultureInfo.InvariantCulture));
        b.Append(',');
        b.Append(v.Z.ToString("0.####", CultureInfo.InvariantCulture));
    }

    internal static bool TryLoadRows(string path, out List<TelemetryRow> rows)
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

                // The recorder can emit two samples per engine tick when a caller
                // double-fires; a repeated position is one physical state, so keep
                // the first only (matches the offline forensic tests).
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
            // A broken telemetry file must never take the command down.
            return false;
        }
    }

    private static float Parse(string value) => float.Parse(value.Trim(), CultureInfo.InvariantCulture);
}

/// <summary>One physical state read from a telemetry CSV row.</summary>
public readonly record struct TelemetryRow(float Time, Vec3 Position, Vec3 Velocity);

/// <summary>
/// Adapter so the diagnostics namespace can drive the simulator without the
/// plugin referencing Core directly (the plugin loads Core by reflection).
/// </summary>
public sealed class GrenadeSimulatorShim
{
    private readonly Func<ThrowParams, IWorldGeometry, TrajectoryResult> _simulate;

    /// <summary>Creates the shim around any ITrajectorySimulator.</summary>
    public GrenadeSimulatorShim(ITrajectorySimulator inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _simulate = inner.Simulate;
    }

    /// <summary>Runs one simulation.</summary>
    public TrajectoryResult Simulate(ThrowParams parameters, IWorldGeometry world) => _simulate(parameters, world);
}
